using System.Diagnostics;
using Catcom.Core.Editor;

namespace Catcom.Core.Tests.Editor;

/// <summary>
///     Optional smoke test against a real <c>nvim --headless</c> found on PATH (a pass without checking
///     anything when there is none). Hard 10 s deadline; the process is always killed in <c>finally</c>.
/// </summary>
[TestFixture]
public sealed class NvimRpcClientRealNvimTests
{
    private static string? FindOnPath(string name)
    {
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return null;
    }

    [Test]
    public async Task HeadlessNvim_AnswersRequests_AcceptsDiagnostics_AndDeliversNotifications()
    {
        var nvim = FindOnPath("nvim");
        if (nvim is null)
        {
            Assert.Pass();
            return;
        }

        var root = Directory.CreateTempSubdirectory("ksa-nvim-smoke-");
        var port = LoopbackPorts.PickFree(49152, 65000);
        var start = new ProcessStartInfo(nvim)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "--headless", "--clean", "--listen", $"127.0.0.1:{port}" })
            start.ArgumentList.Add(arg);
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(root.FullName, "config");
        start.Environment["XDG_DATA_HOME"] = Path.Combine(root.FullName, "data");
        start.Environment["XDG_STATE_HOME"] = Path.Combine(root.FullName, "state");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(root.FullName, "cache");
        start.Environment["NVIM_APPNAME"] = "ksa-test-nvim";

        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            Assert.That(process.Start(), Is.True);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var client = await NvimRpcClient.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(8), deadline.Token);
            await client.InitializeAsync(ct: deadline.Token);
            var timeout = TimeSpan.FromSeconds(5);

            Assert.That(client.ChannelId, Is.GreaterThan(0));
            Assert.That(await client.RequestAsync("nvim_eval", ["1+1"], timeout, deadline.Token), Is.EqualTo(2L));
            Assert.That(await client.RequestAsync("nvim_get_current_buf", [], timeout, deadline.Token), Is.InstanceOf<NvimHandle>());

            // Errors come back as NvimRpcException.
            Assert.That(
                async () => await client.RequestAsync("nvim_command", ["this_is_not_a_command"], timeout, deadline.Token),
                Throws.TypeOf<NvimRpcException>().With.Message.Contain("E492"));

            // Notifications addressed to this channel arrive on the Notification event.
            var saved = new TaskCompletionSource<IReadOnlyList<object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Notification += (method, args) =>
            {
                if (method == "ksa_saved")
                    saved.TrySetResult(args);
            };
            await client.RequestAsync("nvim_exec_lua", ["vim.rpcnotify(..., 'ksa_saved', 'x.ts', 3)", new object?[] { client.ChannelId }], timeout, deadline.Token);
            var savedArgs = await saved.Task.WaitAsync(timeout, deadline.Token);
            Assert.That(savedArgs, Is.EqualTo(new object?[] { "x.ts", 3L }));

            // Diagnostics land in the catcom namespace of the buffer for the file, and clear again.
            var file = Path.Combine(root.FullName, "script one.ts");
            await File.WriteAllTextAsync(file, "let a = 1;\nlet b = ;\nlet c = 3;\n", deadline.Token);
            await client.RequestAsync("nvim_command", ["edit " + file.Replace(" ", "\\ ")], timeout, deadline.Token);
            await client.SetDiagnosticAsync(file, 2, 9, "Expression expected", deadline.Token);
            const string Read = "local d = vim.diagnostic.get(0); return { #d, d[1] and d[1].lnum, d[1] and d[1].col, d[1] and d[1].message, d[1] and d[1].source, vim.api.nvim_win_get_cursor(0)[1] }";
            var info = (object?[])(await client.RequestAsync("nvim_exec_lua", [Read, Array.Empty<object?>()], timeout, deadline.Token))!;
            Assert.That(info[..5], Is.EqualTo(new object?[] { 1L, 1L, 8L, "Expression expected", "catcom" }));
            Assert.That(info[5], Is.EqualTo(2L), "the cursor jumped to the error line");

            await client.ClearDiagnosticsAsync(file, deadline.Token);
            var cleared = (object?[])(await client.RequestAsync("nvim_exec_lua", [Read, Array.Empty<object?>()], timeout, deadline.Token))!;
            Assert.That(cleared[0], Is.EqualTo(0L));

            // A path with no open buffer is a quiet no-op; GoToLine and Echo work on the open one.
            await client.SetDiagnosticAsync(Path.Combine(root.FullName, "nope.ts"), 1, 1, "x", deadline.Token);
            await client.GoToLineAsync(file, 3, deadline.Token);
            Assert.That(
                ((object?[])(await client.RequestAsync("nvim_exec_lua", ["return vim.api.nvim_win_get_cursor(0)", Array.Empty<object?>()], timeout, deadline.Token))!)[0],
                Is.EqualTo(3L));
            await client.EchoAsync("hello from the test", error: true, deadline.Token);
            Assert.That(await client.RequestAsync("nvim_exec2", ["messages", new Dictionary<string, object?> { ["output"] = true }], timeout, deadline.Token), Is.InstanceOf<Dictionary<object, object?>>());

            // Quit; the process may close the socket before answering.
            try
            {
                await client.RequestAsync("nvim_command", ["qa!"], timeout, deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Expected when nvim exits before replying.
            }

            await process.WaitForExitAsync(deadline.Token);
            Assert.That(process.ExitCode, Is.EqualTo(0));
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Never started or already gone.
            }

            try
            {
                root.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
