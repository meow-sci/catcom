using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using KsaScripting.Core.Editor;

namespace KsaScripting.Core.Tests.Editor;

/// <summary>
///     A scripted stand-in for Neovim's RPC endpoint: a loopback <see cref="TcpListener"/> on an
///     OS-assigned port that accepts one client, decodes its frames with <see cref="MsgPackRpc"/> and
///     queues them for the test to read; the test answers by sending frames itself (so it controls
///     order and timing). Every wait is bounded.
/// </summary>
internal sealed class FakeNvimServer : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly TaskCompletionSource<TcpClient> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<RpcMessage> _received = Channel.CreateUnbounded<RpcMessage>();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeLock = new();
    private readonly long? _handshakeChannel;

    /// <summary>Starts listening. With <paramref name="handshakeChannel"/> the server answers the client's
    /// <c>nvim_set_client_info</c> / <c>nvim_get_api_info</c> handshake itself, reporting that channel id.</summary>
    public FakeNvimServer(long? handshakeChannel = null)
    {
        _handshakeChannel = handshakeChannel;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(RunAsync);
    }

    public int Port { get; }

    /// <summary>Waits for the next frame the client sent.</summary>
    public async Task<RpcMessage> NextAsync()
        => await _received.Reader.ReadAsync(_cts.Token).AsTask().WaitAsync(Wait).ConfigureAwait(false);

    public async Task<RpcRequest> NextRequestAsync()
    {
        var message = await NextAsync().ConfigureAwait(false);
        Assert.That(message, Is.InstanceOf<RpcRequest>());
        return (RpcRequest)message;
    }

    /// <summary>Sends raw bytes to the client once it has connected.</summary>
    public async Task SendAsync(byte[] frame)
    {
        var client = await _accepted.Task.WaitAsync(Wait).ConfigureAwait(false);
        Write(client.GetStream(), frame);
    }

    public Task RespondAsync(RpcRequest request, object? result = null, object? error = null)
        => SendAsync(MsgPackRpc.EncodeResponse(request.MsgId, error, result));

    /// <summary>Closes the client's connection from the server side.</summary>
    public async Task CloseClientAsync()
    {
        var client = await _accepted.Task.WaitAsync(Wait).ConfigureAwait(false);
        client.Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_accepted.Task.IsCompletedSuccessfully)
            _accepted.Task.Result.Dispose();
        _cts.Dispose();
    }

    private void Write(NetworkStream stream, byte[] frame)
    {
        lock (_writeLock)
        {
            stream.Write(frame, 0, frame.Length);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            _accepted.TrySetResult(client);
            var chunk = new byte[4096];
            var pending = Array.Empty<byte>();
            while (true)
            {
                var read = await stream.ReadAsync(chunk, _cts.Token).ConfigureAwait(false);
                if (read == 0)
                    break;

                pending = [.. pending, .. chunk.AsSpan(0, read)];
                var sequence = new ReadOnlySequence<byte>(pending);
                while (MsgPackRpc.TryDecode(ref sequence, out var message))
                {
                    Handshake(stream, message!);
                    _received.Writer.TryWrite(message!);
                }

                pending = sequence.ToArray();
            }
        }
        catch (Exception ex)
        {
            _accepted.TrySetException(ex);
        }
        finally
        {
            _received.Writer.TryComplete();
        }
    }

    private void Handshake(NetworkStream stream, RpcMessage message)
    {
        if (_handshakeChannel is not { } channel || message is not RpcRequest request)
            return;

        switch (request.Method)
        {
            case "nvim_set_client_info":
                Write(stream, MsgPackRpc.EncodeResponse(request.MsgId, null, null));
                break;
            case "nvim_get_api_info":
                Write(stream, MsgPackRpc.EncodeResponse(request.MsgId, null, new object?[] { channel, new Dictionary<string, object?> { ["version"] = new Dictionary<string, object?> { ["major"] = 0L } } }));
                break;
        }
    }
}
