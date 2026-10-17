using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Catcom.Core.Editor;

namespace Catcom.Core.Tests.Editor;

/// <summary>The RPC client against a scripted fake Neovim: handshake, id matching, errors, notifications, disconnects.</summary>
[TestFixture]
public sealed class NvimRpcClientTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static async Task<NvimRpcClient> ConnectAsync(FakeNvimServer server)
        => await NvimRpcClient.ConnectAsync("127.0.0.1", server.Port, Wait);

    [Test]
    public async Task Initialize_RegistersTheClientAndLearnsTheChannelId()
    {
        using var server = new FakeNvimServer(handshakeChannel: 4);
        using var client = await ConnectAsync(server);
        Assert.That(client.ChannelId, Is.Null);

        await client.InitializeAsync().WaitAsync(Wait);

        var setInfo = await server.NextRequestAsync();
        var apiInfo = await server.NextRequestAsync();
        Assert.Multiple(() =>
        {
            Assert.That(client.ChannelId, Is.EqualTo(4L));
            Assert.That(client.IsConnected, Is.True);
            Assert.That(setInfo.Method, Is.EqualTo("nvim_set_client_info"));
            Assert.That(setInfo.Args[0], Is.EqualTo("catcom"));
            var version = (Dictionary<object, object?>)setInfo.Args[1]!;
            Assert.That(version["major"], Is.EqualTo(0L));
            Assert.That(version["minor"], Is.EqualTo(1L));
            Assert.That(setInfo.Args[2], Is.EqualTo("remote"));
            Assert.That(setInfo.Args[3], Is.InstanceOf<Dictionary<object, object?>>());
            Assert.That(setInfo.Args[4], Is.InstanceOf<Dictionary<object, object?>>());
            Assert.That(apiInfo.Method, Is.EqualTo("nvim_get_api_info"));
            Assert.That(apiInfo.Args, Is.Empty);
        });
    }

    [Test]
    public async Task Initialize_UsesTheGivenClientName()
    {
        using var server = new FakeNvimServer(handshakeChannel: 9);
        using var client = await ConnectAsync(server);

        await client.InitializeAsync("custom-name").WaitAsync(Wait);

        Assert.That((await server.NextRequestAsync()).Args[0], Is.EqualTo("custom-name"));
    }

    [Test]
    public async Task Initialize_RejectsAnUnexpectedApiInfoShape()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);

        var init = client.InitializeAsync();
        await server.RespondAsync(await server.NextRequestAsync());
        await server.RespondAsync(await server.NextRequestAsync(), result: "not a list");

        Assert.That(async () => await init.WaitAsync(Wait), Throws.TypeOf<NvimRpcException>());
    }

    [Test]
    public async Task Request_MatchesResponsesByIdEvenWhenAnsweredOutOfOrder()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);

        var first = client.RequestAsync("nvim_eval", ["first"]);
        var second = client.RequestAsync("nvim_eval", ["second"]);
        var requestA = await server.NextRequestAsync();
        var requestB = await server.NextRequestAsync();

        // Answer in the reverse of the order they arrived.
        await server.RespondAsync(requestB, result: "answer to " + requestB.Args[0]);
        await server.RespondAsync(requestA, result: "answer to " + requestA.Args[0]);

        Assert.Multiple(() =>
        {
            Assert.That(requestA.MsgId, Is.Not.EqualTo(requestB.MsgId));
            Assert.That(first.WaitAsync(Wait).Result, Is.EqualTo("answer to first"));
            Assert.That(second.WaitAsync(Wait).Result, Is.EqualTo("answer to second"));
        });
    }

    [Test]
    public async Task Request_DecodesStructuredResults()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);

        var call = client.RequestAsync("nvim_get_current_buf", []);
        await server.RespondAsync(await server.NextRequestAsync(), result: new NvimHandle(0, 3));

        Assert.That(await call.WaitAsync(Wait), Is.EqualTo(new NvimHandle(0, 3)));
    }

    [Test]
    public async Task ErrorResponse_BecomesNvimRpcExceptionWithTheServerMessage()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);

        var call = client.RequestAsync("nvim_buf_get_name", [99]);
        await server.RespondAsync(await server.NextRequestAsync(), error: new object?[] { 1, "Invalid buffer id: 99" });

        var ex = Assert.ThrowsAsync<NvimRpcException>(async () => await call.WaitAsync(Wait));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Is.EqualTo("Invalid buffer id: 99"));
            Assert.That(ex.Error, Is.InstanceOf<object?[]>());
        });
    }

    [Test]
    public async Task ErrorResponse_WithANonListError_UsesItsTextForm()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);

        var plain = client.RequestAsync("a", []);
        await server.RespondAsync(await server.NextRequestAsync(), error: "plain failure");
        var code = client.RequestAsync("b", []);
        await server.RespondAsync(await server.NextRequestAsync(), error: 42);

        Assert.Multiple(() =>
        {
            Assert.That(Assert.ThrowsAsync<NvimRpcException>(async () => await plain.WaitAsync(Wait))!.Message, Is.EqualTo("plain failure"));
            Assert.That(Assert.ThrowsAsync<NvimRpcException>(async () => await code.WaitAsync(Wait))!.Message, Is.EqualTo("42"));
        });
    }

    [Test]
    public async Task Request_TimesOutWhenTheServerNeverAnswers()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);

        var call = client.RequestAsync("nvim_eval", ["1+1"], TimeSpan.FromMilliseconds(100));

        Assert.That(async () => await call.WaitAsync(Wait), Throws.TypeOf<TimeoutException>());
        Assert.That(client.IsConnected, Is.True);
    }

    [Test]
    public async Task Request_HonoursCancellation()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);
        using var cts = new CancellationTokenSource();

        var call = client.RequestAsync("nvim_eval", ["1+1"], ct: cts.Token);
        await server.NextRequestAsync();
        await cts.CancelAsync();

        Assert.That(async () => await call.WaitAsync(Wait), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task ServerNotification_ReachesTheNotificationEvent()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);
        var received = new TaskCompletionSource<(string Method, IReadOnlyList<object?> Args)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Notification += (method, args) => received.TrySetResult((method, args));

        await server.SendAsync(MsgPackRpc.EncodeNotification("ksa_saved", ["/home/me/scripts/a.ts", 3]));
        var (name, args) = await received.Task.WaitAsync(Wait);

        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo("ksa_saved"));
            Assert.That(args, Has.Count.EqualTo(2));
            Assert.That(args[0], Is.EqualTo("/home/me/scripts/a.ts"));
            Assert.That(args[1], Is.EqualTo(3L));
        });
    }

    [Test]
    public async Task ThrowingNotificationSubscriber_DoesNotStopLaterNotifications()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Notification += (_, _) => throw new InvalidOperationException("subscriber bug");
        client.Notification += (method, args) =>
        {
            if (args.Count > 0 && args[0] is string text)
                second.TrySetResult(method + ":" + text);
        };

        await server.SendAsync(MsgPackRpc.EncodeNotification("a", ["one"]));

        Assert.That(await second.Task.WaitAsync(Wait), Is.EqualTo("a:one"));
        Assert.That(client.IsConnected, Is.True);
    }

    [Test]
    public async Task ServerRequest_GetsAnErrorResponseSoNeovimNeverBlocks()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);

        await server.SendAsync(MsgPackRpc.EncodeRequest(77, "nvim_unexpected", [1, "two"]));
        var reply = await server.NextAsync();

        Assert.That(reply, Is.InstanceOf<RpcResponse>());
        var response = (RpcResponse)reply;
        Assert.Multiple(() =>
        {
            Assert.That(response.MsgId, Is.EqualTo(77u));
            Assert.That(response.Result, Is.Null);
            Assert.That(response.Error, Is.InstanceOf<object?[]>());
            Assert.That(((object?[])response.Error!)[1], Is.EqualTo("catcom does not handle requests"));
            Assert.That(client.IsConnected, Is.True);
        });
    }

    [Test]
    public async Task Notify_SendsANotificationFrame()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);

        client.Notify("nvim_command", ["echo 'hi'"]);
        var message = await server.NextAsync();

        Assert.That(message, Is.EqualTo(new RpcNotification("nvim_command", ["echo 'hi'"])).Using<RpcNotification>(
            (a, b) => a.Method == b.Method && a.Args.SequenceEqual(b.Args)));
    }

    [Test]
    public async Task ServerClosingTheSocket_RaisesDisconnectedOnce_AndFailsPendingRequests()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);
        var disconnects = 0;
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += ex =>
        {
            Interlocked.Increment(ref disconnects);
            disconnected.TrySetResult(ex);
        };

        var pending = client.RequestAsync("nvim_eval", ["1+1"]);
        await server.NextRequestAsync();
        await server.CloseClientAsync();
        var cause = await disconnected.Task.WaitAsync(Wait);

        Assert.That(async () => await pending.WaitAsync(Wait), Throws.InstanceOf<IOException>());
        client.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(cause, Is.Null, "a clean remote close is not an error");
            Assert.That(client.IsConnected, Is.False);
            Assert.That(Volatile.Read(ref disconnects), Is.EqualTo(1));
            Assert.That(() => client.Notify("x", []), Throws.Exception);
        });
        Assert.That(async () => await client.RequestAsync("nvim_eval", ["2"]), Throws.Exception);
    }

    [Test]
    public async Task Dispose_FailsPendingRequestsAndRaisesDisconnectedOnce()
    {
        using var server = new FakeNvimServer();
        var client = await ConnectAsync(server);
        var disconnects = 0;
        client.Disconnected += _ => Interlocked.Increment(ref disconnects);

        var pending = client.RequestAsync("nvim_eval", ["1+1"]);
        await server.NextRequestAsync();
        client.Dispose();
        client.Dispose();

        Assert.That(async () => await pending.WaitAsync(Wait), Throws.TypeOf<ObjectDisposedException>());
        Assert.Multiple(() =>
        {
            Assert.That(client.IsConnected, Is.False);
            Assert.That(Volatile.Read(ref disconnects), Is.EqualTo(1));
            Assert.That(() => client.Notify("x", []), Throws.TypeOf<ObjectDisposedException>());
        });
        Assert.That(async () => await client.RequestAsync("nvim_eval", ["2"]), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public async Task MalformedBytesFromTheServer_DisconnectWithTheDecodeError()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);
        var disconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += ex => disconnected.TrySetResult(ex);
        var pending = client.RequestAsync("nvim_eval", ["1+1"]);
        await server.NextRequestAsync();

        await server.SendAsync([0xc1]);
        var cause = await disconnected.Task.WaitAsync(Wait);

        Assert.That(cause, Is.TypeOf<FormatException>());
        Assert.That(async () => await pending.WaitAsync(Wait), Throws.InstanceOf<IOException>());
    }

    [Test]
    public void ConnectAsync_ToAClosedPort_TimesOutWithinTheTimeout()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var timeout = TimeSpan.FromMilliseconds(500);

        var clock = Stopwatch.StartNew();
        var ex = Assert.ThrowsAsync<TimeoutException>(async () => await NvimRpcClient.ConnectAsync("127.0.0.1", port, timeout));
        clock.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain(port.ToString()));
            Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(timeout - TimeSpan.FromMilliseconds(50)), "it kept retrying");
            Assert.That(clock.Elapsed, Is.LessThan(timeout + TimeSpan.FromSeconds(3)), "it gave up promptly");
        });
    }

    [Test]
    public async Task ConnectAsync_WithACancelledToken_Cancels()
    {
        using var server = new FakeNvimServer();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.That(async () => await NvimRpcClient.ConnectAsync("127.0.0.1", server.Port, Wait, cts.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task ConnectAsync_ToAServerThatStartsListeningWhileItRetries_Connects()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        // The first attempt may be refused; the retry loop must pick the listener up either way.
        var connecting = NvimRpcClient.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var client = await connecting.WaitAsync(Wait + Wait);
            Assert.That(client.IsConnected, Is.True);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task SetDiagnostic_SendsALuaChunkWithThePositionAndMessage()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);

        var call = client.SetDiagnosticAsync("/s/a.ts", 3, 5, "Unexpected token");
        var request = await server.NextRequestAsync();
        await server.RespondAsync(request, result: true);
        await call.WaitAsync(Wait);

        Assert.Multiple(() =>
        {
            Assert.That(request.Method, Is.EqualTo("nvim_exec_lua"));
            Assert.That((string)request.Args[0]!, Does.Contain("vim.diagnostic.set").And.Contain("'catcom'").And.Contain("nvim_win_set_cursor"));
            var args = (object?[])request.Args[1]!;
            Assert.That(args, Is.EqualTo(new object?[] { "/s/a.ts", 3L, 5L, "Unexpected token" }));
        });
    }

    [Test]
    public async Task ClearDiagnostics_GoToLine_AndEcho_SendTheExpectedRequests()
    {
        using var server = new FakeNvimServer();
        using var client = await ConnectAsync(server);

        var clear = client.ClearDiagnosticsAsync("/s/a.ts");
        var clearRequest = await server.NextRequestAsync();
        await server.RespondAsync(clearRequest, result: true);
        await clear.WaitAsync(Wait);

        var jump = client.GoToLineAsync("/s/a.ts", 12);
        var jumpRequest = await server.NextRequestAsync();
        await server.RespondAsync(jumpRequest, result: true);
        await jump.WaitAsync(Wait);

        var echoError = client.EchoAsync("it broke", error: true);
        var errorRequest = await server.NextRequestAsync();
        await server.RespondAsync(errorRequest);
        await echoError.WaitAsync(Wait);

        var echoPlain = client.EchoAsync("fine");
        var plainRequest = await server.NextRequestAsync();
        await server.RespondAsync(plainRequest);
        await echoPlain.WaitAsync(Wait);

        Assert.Multiple(() =>
        {
            Assert.That(clearRequest.Method, Is.EqualTo("nvim_exec_lua"));
            Assert.That((string)clearRequest.Args[0]!, Does.Contain("vim.diagnostic.reset"));
            Assert.That(clearRequest.Args[1], Is.EqualTo(new object?[] { "/s/a.ts" }));
            Assert.That((string)jumpRequest.Args[0]!, Does.Contain("nvim_win_set_cursor"));
            Assert.That(jumpRequest.Args[1], Is.EqualTo(new object?[] { "/s/a.ts", 12L }));

            Assert.That(errorRequest.Method, Is.EqualTo("nvim_echo"));
            var chunks = (object?[])errorRequest.Args[0]!;
            Assert.That(((object?[])chunks[0]!), Is.EqualTo(new object?[] { "it broke", "ErrorMsg" }));
            Assert.That(errorRequest.Args[1], Is.EqualTo(true));
            Assert.That(errorRequest.Args[2], Is.InstanceOf<Dictionary<object, object?>>());
            Assert.That(((object?[])((object?[])plainRequest.Args[0]!)[0]!), Is.EqualTo(new object?[] { "fine" }));
        });
    }
}
