using System.Text;
using Catcom.Core.Hooks;
using Catcom.Core.Logging;
using Catcom.Core.Values;

namespace Catcom.Core.Terminals;

/// <summary>
///     The host side of a script-owned virtual terminal: an output ring buffer (so a surface that
///     attaches later sees the current screen), the output pending for the next flush, an input
///     queue, the attached surfaces with their grids, and the events scripts subscribe to
///     (<c>term.data</c>, <c>term.resize</c>, <c>term.attach</c>, <c>term.detach</c>, <c>term.cancel</c>).
///     Game-thread only.
/// </summary>
public sealed class ScriptTerminal
{
    /// <summary>The grid reported while no surface is attached.</summary>
    public const int DefaultCols = 80, DefaultRows = 24;

    /// <summary>The bound of the queued input; beyond it the oldest bytes are dropped.</summary>
    public const int MaxQueuedInputBytes = 64 * 1024;

    private const int MaxPendingBytes = 4 * 1024 * 1024;

    private static readonly byte[] SyncBegin = "\x1b[?2026h"u8.ToArray();
    private static readonly byte[] SyncEnd = "\x1b[?2026l"u8.ToArray();

    private readonly List<SinkState> _sinks = [];
    private readonly byte[] _ring;
    private int _ringHead;
    private int _ringLength;
    private byte[] _pending = new byte[256];
    private int _pendingLength;
    private byte[] _input = new byte[256];
    private int _inputLength;
    private bool _terminated;

    /// <summary>Creates a terminal.</summary>
    /// <param name="id">The terminal id (the <c>term:&lt;id&gt;</c> handle).</param>
    /// <param name="script">The owning script's catalog name.</param>
    /// <param name="bufferKiB">The output ring buffer size in KiB.</param>
    public ScriptTerminal(int id, string script, int bufferKiB = 256)
    {
        ArgumentNullException.ThrowIfNull(script);
        Id = id;
        Script = script;
        _ring = new byte[Math.Max(1, bufferKiB) * 1024];
        Cols = DefaultCols;
        Rows = DefaultRows;
    }

    /// <summary>The terminal id.</summary>
    public int Id { get; }

    /// <summary>The owning script's catalog name.</summary>
    public string Script { get; }

    /// <summary>The columns of the smallest attached surface (<see cref="DefaultCols"/> when none).</summary>
    public int Cols { get; private set; }

    /// <summary>The rows of the smallest attached surface (<see cref="DefaultRows"/> when none).</summary>
    public int Rows { get; private set; }

    /// <summary>The number of attached surfaces.</summary>
    public int Attached => _sinks.Count;

    /// <summary>Whether the script should stop when the last surface detaches (raises <see cref="LastDetached"/>).</summary>
    public bool StopWhenDetached { get; set; }

    /// <summary>The last title set with <see cref="SetTitle"/>, or <see langword="null"/>.</summary>
    public string? Title { get; private set; }

    /// <summary>Whether input is queued for <see cref="TryReadInput"/>.</summary>
    public bool HasInput => _inputLength > 0;

    /// <summary>
    ///     Raised for <c>term.data</c>, <c>term.resize</c>, <c>term.attach</c>, <c>term.detach</c> and
    ///     <c>term.cancel</c>. The event carries <c>"$script"</c> (this terminal's script, so only that
    ///     script receives it) and <c>"termId"</c> in its payload.
    /// </summary>
    public event Action<ScriptEvent>? Event;

    /// <summary>Raised when the last surface detached and <see cref="StopWhenDetached"/> is set.</summary>
    public event Action<ScriptTerminal>? LastDetached;

    /// <summary>
    ///     A copy of the output ring buffer in logical (oldest first) order, for the UI preview. A
    ///     method rather than a span because the buffer is circular.
    /// </summary>
    /// <returns>The last <c>bufferKiB * 1024</c> bytes written, or fewer.</returns>
    public byte[] CopyRingBuffer()
    {
        var copy = new byte[_ringLength];
        CopyRingTo(copy);
        return copy;
    }

    /// <summary>Appends output to the ring buffer (which keeps the last <c>bufferKiB * 1024</c> bytes) and to the pending-flush buffer.</summary>
    /// <param name="bytes">The bytes to display.</param>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        AppendRing(bytes);
        if (_terminated)
            return;

        AppendPending(bytes);
    }

    /// <summary>
    ///     Sends the pending output to every attached surface wrapped in DEC 2026 synchronized-update
    ///     markers (one <see cref="ITerminalSink.Write"/> per sink), then clears it. With no surface the
    ///     pending output is simply discarded (the ring buffer already holds it).
    /// </summary>
    public void Flush()
    {
        if (_pendingLength == 0)
            return;

        var payload = Wrap(_pending.AsSpan(0, _pendingLength));
        _pendingLength = 0;
        if (_pending.Length > 64 * 1024)
            _pending = new byte[256];

        if (_sinks.Count == 0)
            return;

        foreach (var sink in _sinks.ToArray())
            SendToSink(sink.Sink, payload);
    }

    /// <summary>
    ///     Attaches a surface: pending output goes to the existing surfaces first, then the whole ring
    ///     buffer is replayed to the new surface (wrapped in DEC 2026), then <c>term.attach</c> and
    ///     <c>term.resize</c> (with the smallest grid) are raised.
    /// </summary>
    /// <param name="sink">The surface.</param>
    /// <param name="cols">The surface's columns.</param>
    /// <param name="rows">The surface's rows.</param>
    public void Attach(ITerminalSink sink, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_terminated)
            return;

        if (_sinks.Any(s => ReferenceEquals(s.Sink, sink)))
        {
            NotifyResize(sink, cols, rows);
            return;
        }

        Flush();
        _sinks.Add(new SinkState(sink, Math.Max(1, cols), Math.Max(1, rows)));

        if (_ringLength > 0)
        {
            var replay = new byte[_ringLength];
            CopyRingTo(replay);
            SendToSink(sink, Wrap(replay));
        }

        RecomputeGrid();
        if (_sinks.Any(s => ReferenceEquals(s.Sink, sink)))
        {
            Raise("term.attach", ("attached", ScriptValue.From(_sinks.Count)));
            Raise("term.resize", ("cols", ScriptValue.From(Cols)), ("rows", ScriptValue.From(Rows)));
        }
    }

    /// <summary>Detaches a surface (no-op when unknown), raising <c>term.detach</c>, <c>term.resize</c> (if the smallest grid changed) and <see cref="LastDetached"/>.</summary>
    /// <param name="sink">The surface.</param>
    public void Detach(ITerminalSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var index = _sinks.FindIndex(s => ReferenceEquals(s.Sink, sink));
        if (index < 0)
            return;

        _sinks.RemoveAt(index);
        var changed = RecomputeGrid();
        Raise("term.detach", ("attached", ScriptValue.From(_sinks.Count)));
        if (changed && _sinks.Count > 0)
            Raise("term.resize", ("cols", ScriptValue.From(Cols)), ("rows", ScriptValue.From(Rows)));

        if (_sinks.Count == 0 && StopWhenDetached)
            RaiseLastDetached();
    }

    /// <summary>
    ///     Queues input from a surface (bounded to <see cref="MaxQueuedInputBytes"/>, oldest dropped),
    ///     raises <c>term.data</c> and, when the chunk contains Ctrl+C (0x03), <c>term.cancel</c>.
    /// </summary>
    /// <param name="bytes">The input bytes.</param>
    public void EnqueueInput(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty || _terminated)
            return;

        var span = bytes.Span;
        if (span.Length >= MaxQueuedInputBytes)
        {
            span = span[^MaxQueuedInputBytes..];
            _inputLength = 0;
            LogInputOverflow();
        }
        else if (_inputLength + span.Length > MaxQueuedInputBytes)
        {
            var keep = MaxQueuedInputBytes - span.Length;
            Buffer.BlockCopy(_input, _inputLength - keep, _input, 0, keep);
            _inputLength = keep;
            LogInputOverflow();
        }

        if (_input.Length < _inputLength + span.Length)
            Array.Resize(ref _input, Math.Max(_input.Length * 2, _inputLength + span.Length));
        span.CopyTo(_input.AsSpan(_inputLength));
        _inputLength += span.Length;

        Raise("term.data", ("data", ScriptValue.From(bytes)));
        if (bytes.Span.Contains((byte)0x03))
            Raise("term.cancel");
    }

    /// <summary>Dequeues ALL queued input as one concatenated chunk.</summary>
    /// <param name="bytes">The input, or empty when none was queued.</param>
    /// <returns><see langword="true"/> when input was queued.</returns>
    public bool TryReadInput(out ReadOnlyMemory<byte> bytes)
    {
        if (_inputLength == 0)
        {
            bytes = ReadOnlyMemory<byte>.Empty;
            return false;
        }

        bytes = _input.AsSpan(0, _inputLength).ToArray();
        _inputLength = 0;
        return true;
    }

    /// <summary>Updates a surface's grid; <c>term.resize</c> is raised only when the smallest grid changed.</summary>
    /// <param name="sink">The reporting surface (ignored when not attached).</param>
    /// <param name="cols">Its new columns.</param>
    /// <param name="rows">Its new rows.</param>
    public void NotifyResize(ITerminalSink sink, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_terminated)
            return;

        var index = _sinks.FindIndex(s => ReferenceEquals(s.Sink, sink));
        if (index < 0)
            return;

        _sinks[index] = _sinks[index] with { Cols = Math.Max(1, cols), Rows = Math.Max(1, rows) };
        if (RecomputeGrid())
            Raise("term.resize", ("cols", ScriptValue.From(Cols)), ("rows", ScriptValue.From(Rows)));
    }

    /// <summary>Sets the window title by writing an OSC 2 sequence (control characters are stripped from the title).</summary>
    /// <param name="title">The title text.</param>
    public void SetTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var clean = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (!char.IsControl(c))
                clean.Append(c);
        }

        Title = clean.ToString();
        Write(Encoding.UTF8.GetBytes("\x1b]2;" + Title + "\x07"));
    }

    /// <summary>
    ///     Ends the terminal: flushes, writes <c>"\r\n" + message + "\r\n"</c> to every surface, calls
    ///     <see cref="ITerminalSink.Terminate"/> and detaches them all without raising events.
    /// </summary>
    /// <param name="message">The reason shown to the user, e.g. <c>[script exited]</c>.</param>
    public void Terminate(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_terminated)
            return;

        Flush();
        _terminated = true;

        var line = Encoding.UTF8.GetBytes("\r\n" + message + "\r\n");
        AppendRing(line);
        var sinks = _sinks.ToArray();
        _sinks.Clear();
        RecomputeGrid();

        foreach (var state in sinks)
        {
            SendToSink(state.Sink, line, detachOnFailure: false);
            try
            {
                state.Sink.Terminate(message);
            }
            catch (Exception ex)
            {
                Log.LogOnce($"terminal.sink.terminate:{Script}", LogLevel.Warn, $"Terminating a terminal surface of '{Script}' failed: {ex.Message}");
            }
        }
    }

    private static byte[] Wrap(ReadOnlySpan<byte> body)
    {
        var payload = new byte[SyncBegin.Length + body.Length + SyncEnd.Length];
        SyncBegin.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(SyncBegin.Length));
        SyncEnd.CopyTo(payload, SyncBegin.Length + body.Length);
        return payload;
    }

    private void SendToSink(ITerminalSink sink, byte[] payload, bool detachOnFailure = true)
    {
        try
        {
            sink.Write(payload);
        }
        catch (Exception ex)
        {
            Log.LogOnce($"terminal.sink.write:{Script}", LogLevel.Warn, $"A terminal surface of '{Script}' threw while writing ({ex.Message}); detaching it.");
            if (detachOnFailure)
                Detach(sink);
        }
    }

    private bool RecomputeGrid()
    {
        int cols, rows;
        if (_sinks.Count == 0)
        {
            cols = DefaultCols;
            rows = DefaultRows;
        }
        else
        {
            cols = _sinks.Min(s => s.Cols);
            rows = _sinks.Min(s => s.Rows);
        }

        var changed = cols != Cols || rows != Rows;
        Cols = cols;
        Rows = rows;
        return changed;
    }

    private void AppendRing(ReadOnlySpan<byte> bytes)
    {
        var capacity = _ring.Length;
        if (bytes.Length >= capacity)
        {
            bytes[^capacity..].CopyTo(_ring);
            _ringHead = 0;
            _ringLength = capacity;
            return;
        }

        var tail = (_ringHead + _ringLength) % capacity;
        var first = Math.Min(bytes.Length, capacity - tail);
        bytes[..first].CopyTo(_ring.AsSpan(tail));
        bytes[first..].CopyTo(_ring.AsSpan(0));

        var newLength = _ringLength + bytes.Length;
        if (newLength > capacity)
        {
            _ringHead = (_ringHead + (newLength - capacity)) % capacity;
            newLength = capacity;
        }

        _ringLength = newLength;
    }

    private void CopyRingTo(Span<byte> destination)
    {
        var first = Math.Min(_ringLength, _ring.Length - _ringHead);
        _ring.AsSpan(_ringHead, first).CopyTo(destination);
        _ring.AsSpan(0, _ringLength - first).CopyTo(destination[first..]);
    }

    private void AppendPending(ReadOnlySpan<byte> bytes)
    {
        if (_pendingLength + bytes.Length > MaxPendingBytes)
        {
            // A script wrote megabytes between two flushes: keep the newest output.
            Log.LogOnce($"terminal.pending:{Script}", LogLevel.Warn,
                $"Script '{Script}' wrote more than {MaxPendingBytes / (1024 * 1024)} MiB to its terminal between flushes; dropping the oldest output.");
            if (bytes.Length >= MaxPendingBytes)
            {
                _pendingLength = 0;
                bytes = bytes[^MaxPendingBytes..];
            }
            else
            {
                var keep = MaxPendingBytes - bytes.Length;
                Buffer.BlockCopy(_pending, _pendingLength - keep, _pending, 0, keep);
                _pendingLength = keep;
            }
        }

        if (_pending.Length < _pendingLength + bytes.Length)
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingLength + bytes.Length));
        bytes.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength += bytes.Length;
    }

    private void LogInputOverflow()
        => Log.LogOnce($"terminal.input:{Script}", LogLevel.Warn,
            $"Terminal input of '{Script}' is not being read ({MaxQueuedInputBytes / 1024} KiB queued); dropping the oldest input.");

    private void Raise(string type, params (string Key, ScriptValue Value)[] fields)
    {
        if (_terminated)
            return;

        var handler = Event;
        if (handler is null)
            return;

        var payload = new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            [ScriptEvent.ScriptKey] = ScriptValue.From(Script),
            ["termId"] = ScriptValue.From(Id),
        };
        foreach (var (key, value) in fields)
            payload[key] = value;

        try
        {
            handler(new ScriptEvent(0, type, null, payload));
        }
        catch (Exception ex)
        {
            Log.LogOnce($"terminal.event:{type}", LogLevel.Warn, $"A terminal event handler failed for '{type}': {ex.Message}");
        }
    }

    private void RaiseLastDetached()
    {
        try
        {
            LastDetached?.Invoke(this);
        }
        catch (Exception ex)
        {
            Log.LogOnce("terminal.lastdetached", LogLevel.Warn, $"A LastDetached handler failed: {ex.Message}");
        }
    }

    private sealed record SinkState(ITerminalSink Sink, int Cols, int Rows);
}
