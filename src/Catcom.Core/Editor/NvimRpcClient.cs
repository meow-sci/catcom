using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Catcom.Core.Editor;

/// <summary>
///     A msgpack-RPC client for a Neovim started with <c>--listen 127.0.0.1:&lt;port&gt;</c>
///     (docs/research/editor/neovim-tcp-launch.md). A background task reads and decodes frames,
///     completing pending requests by message id and raising <see cref="Notification"/> for server
///     notifications (such as the shipped config's <c>ksa_saved</c>); requests from Neovim are
///     answered with an error so it never blocks. Writes are serialised with a lock. Thread-safe.
/// </summary>
public sealed class NvimRpcClient : IDisposable
{
    /// <summary>The default time <see cref="RequestAsync"/> waits for a response.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private const int InitialBufferBytes = 16 * 1024;
    private const int MaxFrameBytes = 64 * 1024 * 1024;
    private const int SendTimeoutMs = 5000;
    private static readonly TimeSpan ConnectRetryInterval = TimeSpan.FromMilliseconds(100);

    // Lua chunks run through nvim_exec_lua; the script's varargs are the request arguments.
    private const string LuaFindBuffer = """
        local function find_buf(p)
          local win = vim.fn.has('win32') == 1
          local want = vim.fs.normalize(vim.fn.fnamemodify(p, ':p'))
          if win then want = want:lower() end
          for _, b in ipairs(vim.api.nvim_list_bufs()) do
            local name = vim.api.nvim_buf_get_name(b)
            if name ~= '' then
              name = vim.fs.normalize(name)
              if win then name = name:lower() end
              if name == want then return b end
            end
          end
          local n = vim.fn.bufnr(vim.fn.fnameescape(p))
          if n > 0 then return n end
          return nil
        end
        """;

    private const string LuaSetDiagnostic = LuaFindBuffer + """

        local path, line, col, msg = ...
        local buf = find_buf(path)
        if not buf then return false end
        local ns = vim.api.nvim_create_namespace('catcom')
        local count = vim.api.nvim_buf_line_count(buf)
        local lnum = math.min(math.max(line - 1, 0), math.max(count - 1, 0))
        local ok, lines = pcall(vim.api.nvim_buf_get_lines, buf, lnum, lnum + 1, false)
        local text = (ok and lines[1]) or ''
        local c = math.min(math.max(col - 1, 0), #text)
        vim.diagnostic.set(ns, buf, { {
          lnum = lnum, col = c, message = msg,
          severity = vim.diagnostic.severity.ERROR, source = 'catcom',
        } })
        if buf == vim.api.nvim_get_current_buf() then
          pcall(vim.api.nvim_win_set_cursor, 0, { lnum + 1, c })
        end
        return true
        """;

    private const string LuaClearDiagnostics = LuaFindBuffer + """

        local path = ...
        local buf = find_buf(path)
        if not buf then return false end
        vim.diagnostic.reset(vim.api.nvim_create_namespace('catcom'), buf)
        return true
        """;

    private const string LuaGoToLine = LuaFindBuffer + """

        local path, line = ...
        local buf = find_buf(path)
        if not buf then return false end
        if buf ~= vim.api.nvim_get_current_buf() then
          if not pcall(vim.cmd, 'buffer ' .. buf) then return false end
        end
        local count = vim.api.nvim_buf_line_count(buf)
        pcall(vim.api.nvim_win_set_cursor, 0, { math.min(math.max(line, 1), math.max(count, 1)), 0 })
        pcall(vim.cmd, 'normal! zz')
        return true
        """;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<object?>> _pending = new();
    private readonly CancellationTokenSource _readCts = new();
    private int _state; // 0 = open, 1 = closed
    private int _disconnectedRaised;
    private int _disposed;
    private int _nextId;
    private long _channelId = -1;

    private NvimRpcClient(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _tcp.SendTimeout = SendTimeoutMs;
        _stream = tcp.GetStream();
        var token = _readCts.Token;
        _ = Task.Run(() => ReadLoopAsync(token));
    }

    /// <summary>Raised on the reader thread for every server notification (method, arguments). Handlers must be quick.</summary>
    public event Action<string, IReadOnlyList<object?>>? Notification;

    /// <summary>Raised once when the connection ends (remote close, I/O error or <see cref="Dispose"/>); the exception is <c>null</c> for a clean close.</summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>This client's Neovim channel id (known after <see cref="InitializeAsync"/>), or <c>null</c>.</summary>
    public long? ChannelId
    {
        get
        {
            var id = Interlocked.Read(ref _channelId);
            return id >= 0 ? id : null;
        }
    }

    /// <summary>True until the connection is closed or the client disposed.</summary>
    public bool IsConnected => Volatile.Read(ref _state) == 0;

    /// <summary>
    ///     Connects to <paramref name="host"/>:<paramref name="port"/>, retrying every 100 ms (the freshly
    ///     spawned Neovim needs a moment to start listening) until <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="host">The host, normally <c>127.0.0.1</c>.</param>
    /// <param name="port">The TCP port.</param>
    /// <param name="timeout">How long to keep retrying.</param>
    /// <param name="ct">Cancels the attempt.</param>
    /// <returns>The connected client.</returns>
    /// <exception cref="TimeoutException">No connection could be made within <paramref name="timeout"/>.</exception>
    public static async Task<NvimRpcClient> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var clock = Stopwatch.StartNew();
        Exception? last = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = timeout - clock.Elapsed;
            var tcp = new TcpClient();
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(remaining > ConnectRetryInterval ? remaining : ConnectRetryInterval);
                await tcp.ConnectAsync(host, port, attempt.Token).ConfigureAwait(false);
                return new NvimRpcClient(tcp);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                last = new TimeoutException("the connection attempt timed out");
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                tcp.Dispose();
                last = ex;
            }
            catch
            {
                tcp.Dispose();
                throw;
            }

            remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Could not connect to Neovim at {host}:{port} within {timeout.TotalSeconds:0.##} s.", last);

            await Task.Delay(remaining < ConnectRetryInterval ? remaining : ConnectRetryInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Sends a request and awaits its response (matched by message id, so out-of-order answers are fine).
    /// </summary>
    /// <param name="method">The API function, e.g. <c>nvim_eval</c>.</param>
    /// <param name="args">The positional arguments.</param>
    /// <param name="timeout">Response deadline; <c>null</c> uses <see cref="DefaultRequestTimeout"/>, <see cref="Timeout.InfiniteTimeSpan"/> waits forever.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The decoded result.</returns>
    /// <exception cref="NvimRpcException">Neovim answered with an error.</exception>
    /// <exception cref="TimeoutException">No response arrived in time.</exception>
    /// <exception cref="IOException">The connection is closed or closed while waiting.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public async Task<object?> RequestAsync(string method, IReadOnlyList<object?> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(args);
        ThrowIfUnusable();

        var id = unchecked((uint)Interlocked.Increment(ref _nextId));
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            // The close path fails every pending request after flipping _state, so re-checking
            // here (after registering) closes the register-vs-close race.
            ThrowIfUnusable();
            SendFrame(MsgPackRpc.EncodeRequest(id, method, args));
            return await tcs.Task.WaitAsync(timeout ?? DefaultRequestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Neovim did not answer '{method}' in time.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a notification (no response).</summary>
    /// <param name="method">The event name.</param>
    /// <param name="args">The positional arguments.</param>
    /// <exception cref="IOException">The connection is closed.</exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public void Notify(string method, IReadOnlyList<object?> args)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(args);
        ThrowIfUnusable();
        SendFrame(MsgPackRpc.EncodeNotification(method, args));
    }

    /// <summary>
    ///     Registers this client with Neovim (<c>nvim_set_client_info</c>) and learns its channel id
    ///     (<c>nvim_get_api_info</c>), published as <see cref="ChannelId"/>.
    /// </summary>
    /// <param name="clientName">The client name shown by <c>:checkhealth</c> and <c>nvim_list_chans</c>.</param>
    /// <param name="ct">Cancels the handshake.</param>
    /// <returns>A task completing when the channel id is known.</returns>
    public async Task InitializeAsync(string clientName = "catcom", CancellationToken ct = default)
    {
        var version = new Dictionary<string, object?> { ["major"] = 0L, ["minor"] = 1L };
        await RequestAsync(
            "nvim_set_client_info",
            [clientName, version, "remote", new Dictionary<string, object?>(), new Dictionary<string, object?>()],
            ct: ct).ConfigureAwait(false);
        var info = await RequestAsync("nvim_get_api_info", [], ct: ct).ConfigureAwait(false);
        if (info is not IReadOnlyList<object?> { Count: > 0 } list || list[0] is not long channel)
            throw new NvimRpcException("nvim_get_api_info returned an unexpected result", info);
        Interlocked.Exchange(ref _channelId, channel);
    }

    /// <summary>
    ///     Shows <paramref name="message"/> as an error diagnostic (namespace <c>catcom</c>) on the
    ///     given 1-based line and column of the buffer open for <paramref name="path"/>, and moves the
    ///     cursor there when that buffer is current. A no-op when Neovim has no such buffer.
    /// </summary>
    /// <param name="path">The file's full path.</param>
    /// <param name="line">1-based line.</param>
    /// <param name="column">1-based column.</param>
    /// <param name="message">The diagnostic text.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task completing when Neovim has applied it.</returns>
    public Task SetDiagnosticAsync(string path, int line, int column, string message, CancellationToken ct = default)
        => RequestAsync("nvim_exec_lua", [LuaSetDiagnostic, new object?[] { path, (long)line, (long)column, message }], ct: ct);

    /// <summary>Removes the <c>catcom</c> diagnostics from the buffer open for <paramref name="path"/>.</summary>
    /// <param name="path">The file's full path.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task completing when Neovim has applied it.</returns>
    public Task ClearDiagnosticsAsync(string path, CancellationToken ct = default)
        => RequestAsync("nvim_exec_lua", [LuaClearDiagnostics, new object?[] { path }], ct: ct);

    /// <summary>Moves the cursor to a 1-based line of the buffer open for <paramref name="path"/>, switching to it if needed.</summary>
    /// <param name="path">The file's full path.</param>
    /// <param name="line">1-based line (clamped into the buffer).</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task completing when Neovim has applied it.</returns>
    public Task GoToLineAsync(string path, int line, CancellationToken ct = default)
        => RequestAsync("nvim_exec_lua", [LuaGoToLine, new object?[] { path, (long)line }], ct: ct);

    /// <summary>Echoes a message in Neovim's command line (and <c>:messages</c>), highlighted as an error when <paramref name="error"/> is set.</summary>
    /// <param name="text">The message.</param>
    /// <param name="error">Use the <c>ErrorMsg</c> highlight.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task completing when Neovim has applied it.</returns>
    public Task EchoAsync(string text, bool error = false, CancellationToken ct = default)
    {
        object?[] chunk = error ? [text, "ErrorMsg"] : [text];
        return RequestAsync("nvim_echo", [new object?[] { chunk }, true, new Dictionary<string, object?>()], ct: ct);
    }

    /// <summary>Closes the socket, fails pending requests with <see cref="ObjectDisposedException"/> and raises <see cref="Disconnected"/> (once).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Close(null, new ObjectDisposedException(nameof(NvimRpcClient)));
    }

    private static string Describe(object? value)
    {
        switch (value)
        {
            case null:
                return "nil";
            case string s:
                return s;
            case byte[] bytes:
                return $"<{bytes.Length} bytes>";
            case NvimHandle h:
                return $"handle({h.Kind}:{h.Value})";
            case IDictionary map:
            {
                var sb = new StringBuilder("{");
                var first = true;
                foreach (DictionaryEntry e in map)
                {
                    sb.Append(first ? "" : ", ").Append(Describe(e.Key)).Append(": ").Append(Describe(e.Value));
                    first = false;
                }

                return sb.Append('}').ToString();
            }

            case IEnumerable seq:
                return "[" + string.Join(", ", seq.Cast<object?>().Select(Describe)) + "]";
            default:
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }

    private static NvimRpcException ToException(object? error)
    {
        var message = error is IReadOnlyList<object?> { Count: >= 2 } list && list[1] is string text ? text : Describe(error);
        return new NvimRpcException(message, error);
    }

    private void ThrowIfUnusable()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(NvimRpcClient));
        if (!IsConnected)
            throw new IOException("The Neovim connection is closed.");
    }

    private void SendFrame(byte[] frame)
    {
        try
        {
            lock (_writeLock)
            {
                _stream.Write(frame, 0, frame.Length);
            }
        }
        catch (Exception ex)
        {
            // A failed or partial write leaves the stream unusable.
            Close(ex, new IOException("The Neovim connection was lost: " + ex.Message, ex));
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(NvimRpcClient));
            throw new IOException("The Neovim connection was lost: " + ex.Message, ex);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[InitialBufferBytes];
        var start = 0;
        var end = 0;
        try
        {
            while (true)
            {
                if (end == buffer.Length)
                {
                    if (start > 0)
                    {
                        Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                        end -= start;
                        start = 0;
                    }
                    else if (buffer.Length >= MaxFrameBytes)
                    {
                        throw new InvalidDataException("A Neovim frame exceeds the size limit.");
                    }
                    else
                    {
                        Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxFrameBytes));
                    }
                }

                var read = await _stream.ReadAsync(buffer.AsMemory(end), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    Close(null, new IOException("The Neovim connection was closed by the remote end."));
                    return;
                }

                end += read;
                var sequence = new ReadOnlySequence<byte>(buffer, start, end - start);
                while (MsgPackRpc.TryDecode(ref sequence, out var message))
                {
                    start = end - (int)sequence.Length;
                    Dispatch(message!);
                }

                if (start == end)
                    start = end = 0;
            }
        }
        catch (Exception ex)
        {
            if (!IsConnected)
                return; // closed on purpose (Dispose) or already reported

            Close(ex, new IOException("The Neovim connection was lost: " + ex.Message, ex));
        }
    }

    private void Dispatch(RpcMessage message)
    {
        switch (message)
        {
            case RpcResponse response:
                if (_pending.TryRemove(response.MsgId, out var tcs))
                {
                    if (response.Error is null)
                        tcs.TrySetResult(response.Result);
                    else
                        tcs.TrySetException(ToException(response.Error));
                }

                break;

            case RpcNotification notification:
                RaiseNotification(notification);
                break;

            case RpcRequest request:
                // Neovim may call us (rpcrequest); we expose nothing, but it must not block waiting.
                try
                {
                    SendFrame(MsgPackRpc.EncodeResponse(request.MsgId, new object?[] { 0L, "catcom does not handle requests" }, null));
                }
                catch (Exception)
                {
                    // SendFrame already closed the connection; the read loop notices.
                }

                break;
        }
    }

    private void RaiseNotification(RpcNotification notification)
    {
        var handlers = Notification;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string, IReadOnlyList<object?>>)handler)(notification.Method, notification.Args);
            }
            catch (Exception)
            {
                // A faulty subscriber must not stall or kill the reader.
            }
        }
    }

    private void Close(Exception? cause, Exception pendingFailure)
    {
        if (Interlocked.Exchange(ref _state, 1) != 0)
            return;

        try
        {
            _readCts.Cancel();
        }
        catch (Exception)
        {
            // Nothing is registered on this source that could throw.
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception)
        {
            // Already broken.
        }

        try
        {
            _tcp.Dispose();
        }
        catch (Exception)
        {
            // Already broken.
        }

        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetException(pendingFailure);
        }

        if (Interlocked.Exchange(ref _disconnectedRaised, 1) != 0)
            return;

        var handlers = Disconnected;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<Exception?>)handler)(cause);
            }
            catch (Exception)
            {
                // A faulty subscriber must not break teardown.
            }
        }
    }
}
