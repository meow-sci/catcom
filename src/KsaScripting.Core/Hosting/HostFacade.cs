using KsaScripting.Core.Commands;
using KsaScripting.Core.Hooks;
using KsaScripting.Core.Logging;
using CoreLog = KsaScripting.Core.Logging.Log;
using KsaScripting.Core.Persistence;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Scheduling;
using KsaScripting.Core.Terminals;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Hosting;

/// <summary>
///     The coarse host surface the script preludes are built on. Composes the game bridge, scheduler, hook
///     registry, terminals, script state and the log; attributes every call to the script currently running
///     (<see cref="EnterScript"/>); gates writes by the configured authority; and keeps a per-script output
///     ring for the Script Manager. Game-thread only.
/// </summary>
/// <remarks>
///     Wire the scheduler's <c>onScriptError</c> callback to <see cref="RecordError"/> so every reported
///     script error also lands in the output ring.
/// </remarks>
public sealed class HostFacade : IHostFacade
{
    private readonly IGameBridge _bridge;
    private readonly Scheduler _scheduler;
    private readonly HookRegistry _hooks;
    private readonly TerminalRegistry _terminals;
    private readonly ScriptStateStore _state;
    private readonly List<string> _scriptStack = [];
    private readonly Dictionary<string, Queue<LogEntry>> _output = new(StringComparer.Ordinal);
    private readonly object _outputGate = new();
    private Exception? _lastRecorded;

    /// <summary>Creates the facade and registers it with the scheduler (host, terminals) and the hook registry (active-vessel provider).</summary>
    /// <param name="bridge">The game bridge.</param>
    /// <param name="scheduler">The scheduler.</param>
    /// <param name="hooks">The hook registry.</param>
    /// <param name="terminals">The terminal registry.</param>
    /// <param name="state">The script state store.</param>
    /// <param name="options">Authority and output settings (defaults when omitted).</param>
    public HostFacade(
        IGameBridge bridge,
        Scheduler scheduler,
        HookRegistry hooks,
        TerminalRegistry terminals,
        ScriptStateStore state,
        HostFacadeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(terminals);
        ArgumentNullException.ThrowIfNull(state);

        _bridge = bridge;
        _scheduler = scheduler;
        _hooks = hooks;
        _terminals = terminals;
        _state = state;
        Options = options ?? new HostFacadeOptions();

        scheduler.Host = this;
        scheduler.Terminals = terminals;
        hooks.ActiveVesselProvider = ActiveVesselId;
    }

    /// <summary>The authority and output settings.</summary>
    public HostFacadeOptions Options { get; }

    /// <summary>Raised for every line appended to a script's output ring, with the script name.</summary>
    public event Action<string, LogEntry>? Output;

    /// <inheritdoc />
    public string CurrentScript => _scriptStack.Count == 0 ? "" : _scriptStack[^1];

    /// <inheritdoc />
    public IDisposable EnterScript(string script)
    {
        _scriptStack.Add(script ?? "");
        return new ScriptScope(this, _scriptStack.Count);
    }

    // ---------------------------------------------------------------- reads

    /// <inheritdoc />
    public ScriptValue Read(string handle, string path)
    {
        ArgumentNullException.ThrowIfNull(handle);
        path ??= "";

        if (handle.StartsWith("signal:", StringComparison.Ordinal))
        {
            return path == "cancelled"
                ? ScriptValue.From(_scheduler.IsCancelled(handle["signal:".Length..]))
                : ScriptValue.Null;
        }

        if (TryParseTerminal(handle, out var termId))
        {
            var info = TermInfo(termId);
            return path.Length == 0 ? info : SnapshotPath.Resolve(info, path);
        }

        try
        {
            return _bridge.Read(handle, path) ?? ScriptValue.Null;
        }
        catch (Exception ex)
        {
            CoreLog.LogOnce("read:" + HandlePrefix(handle), LogLevel.Warn, $"Reading '{handle}' '{path}' failed: {ex.Message}");
            return ScriptValue.Null;
        }
    }

    /// <inheritdoc />
    public ScriptValue Snapshot(string handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (TryParseTerminal(handle, out var termId))
            return TermInfo(termId);

        try
        {
            return _bridge.Snapshot(handle) ?? ScriptValue.Null;
        }
        catch (Exception ex)
        {
            CoreLog.LogOnce("snapshot:" + HandlePrefix(handle), LogLevel.Warn, $"Snapshot of '{handle}' failed: {ex.Message}");
            return ScriptValue.Null;
        }
    }

    /// <inheritdoc />
    public ScriptValue List(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);

        try
        {
            switch (kind)
            {
                case "events":
                    return ScriptValue.List(_bridge.EventCatalog.Select(DescribeEvent).ToArray());
                case "scripts":
                    return ScriptValue.List(_hooks.All()
                        .Select(b => b.Script)
                        .Distinct(StringComparer.Ordinal)
                        .Select(s => ScriptValue.From(s))
                        .ToArray());
                default:
                    return ScriptValue.List(_bridge.List(kind).Select(s => ScriptValue.From(s)).ToArray());
            }
        }
        catch (Exception ex)
        {
            CoreLog.LogOnce("list:" + kind, LogLevel.Warn, $"Listing '{kind}' failed: {ex.Message}");
            return ScriptValue.List([]);
        }
    }

    private static ScriptValue DescribeEvent(EventDescriptor e)
        => ScriptValue.Map(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["name"] = ScriptValue.From(e.Name),
            ["description"] = ScriptValue.From(e.Description),
            ["payloadFields"] = ScriptValue.List(e.PayloadFields.Select(f => ScriptValue.From(f)).ToArray()),
            ["producer"] = ScriptValue.From(e.Producer),
        });

    // ---------------------------------------------------------------- writes

    /// <inheritdoc />
    public ActResult Act(string action, string? vesselId, int ordinal, double value, double[]? values, string? token, string? aux)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (action.StartsWith("debug.", StringComparison.Ordinal) && !Options.DebugNamespace)
            return Immediate(CommandResult.Denied("debug namespace disabled in ksa-scripting.toml"));

        if (!Options.ControlAllVessels
            && !string.IsNullOrEmpty(vesselId)
            && action != "camera.focus"
            && !string.Equals(vesselId, ActiveVesselId(), StringComparison.Ordinal))
        {
            return Immediate(CommandResult.Denied("control is restricted to the active vessel"));
        }

        var command = new SimCommand(vesselId ?? "", action, ordinal, value) { Values = values, Token = token, Aux = aux };
        if (command.Phase == CommandPhase.Frame)
        {
            try
            {
                return Immediate(_bridge.Execute(command));
            }
            catch (Exception ex)
            {
                CoreLog.LogOnce("act:" + action, LogLevel.Warn, $"Executing '{action}' failed: {ex.Message}");
                return Immediate(CommandResult.Fault(ex.Message));
            }
        }

        var wait = _scheduler.CreateWait("act", ScriptValue.Null, CurrentScript);
        try
        {
            _bridge.QueueSolver(command, result => _scheduler.CompleteWait(wait, result.ToScriptValue()));
        }
        catch (Exception ex)
        {
            CoreLog.LogOnce("act:" + action, LogLevel.Warn, $"Queueing '{action}' failed: {ex.Message}");
            _scheduler.CompleteWait(wait, CommandResult.Fault(ex.Message).ToScriptValue());
        }

        return new ActResult(null, wait);
    }

    private static ActResult Immediate(CommandResult result) => new(result, null);

    // ---------------------------------------------------------------- waits and hooks

    /// <inheritdoc />
    public HostWait Wait(string kind, ScriptValue arg) => _scheduler.CreateWait(kind, arg, CurrentScript);

    /// <inheritdoc />
    public Guid Subscribe(string kind, string key, ScriptCallable fn, BindingOptions opts)
    {
        if (!BindingKinds.TryParse(kind, out var bindingKind))
            throw new ArgumentException("unknown subscription kind", nameof(kind));

        var script = CurrentScript;
        if (script.Length == 0)
            throw new InvalidOperationException("subscribe outside a script");

        return _hooks.Add(script, bindingKind, key, fn, opts ?? new BindingOptions(), fromCode: true);
    }

    /// <inheritdoc />
    public void Unsubscribe(Guid id) => _hooks.Remove(id);

    // ---------------------------------------------------------------- misc

    /// <inheritdoc />
    public void Log(int level, string message)
    {
        var clamped = (LogLevel)Math.Clamp(level, 0, 3);
        var script = CurrentScript;
        var text = $"[{(script.Length == 0 ? "host" : script)}] {message}";

        CoreLog.Write(clamped, text);
        if (script.Length > 0)
            Append(script, new LogEntry(clamped, text));
    }

    /// <summary>
    ///     Records a script error in the script's output ring (and the log). The scheduler's
    ///     <c>onScriptError</c> callback should call this. Recording the same exception twice is a no-op.
    /// </summary>
    /// <param name="script">The script the error belongs to.</param>
    /// <param name="ex">The error.</param>
    public void RecordError(string script, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ReferenceEquals(_lastRecorded, ex))
            return;

        _lastRecorded = ex;
        var text = "error: " + ex.Message;
        if (ex is ScriptRuntimeException sre)
        {
            var firstTrace = sre.Traceback.Split('\n', 2)[0].Trim();
            if (firstTrace.Length > 0)
                text += $" ({firstTrace})";
        }

        var named = string.IsNullOrEmpty(script) ? "host" : script;
        CoreLog.Write(LogLevel.Error, $"[{named}] {text}");
        if (!string.IsNullOrEmpty(script))
            Append(script, new LogEntry(LogLevel.Error, text));
    }

    /// <inheritdoc />
    public void ReportError(ScriptRuntimeException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var script = CurrentScript;
        _scheduler.ReportError(script, error);
        RecordError(script, error);
    }

    /// <summary>The script's retained output lines (log lines and errors), oldest first.</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<LogEntry> OutputFor(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        lock (_outputGate)
        {
            return _output.TryGetValue(script, out var ring) ? ring.ToArray() : [];
        }
    }

    /// <summary>Discards the script's retained output.</summary>
    /// <param name="script">The script's catalog name.</param>
    public void ClearOutput(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        lock (_outputGate)
        {
            _output.Remove(script);
        }
    }

    /// <inheritdoc />
    public void ConsoleExec(string command)
    {
        try
        {
            _bridge.ConsoleExec(command);
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"Console command '{command}' failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public ScriptValue StateGet(string key) => _state.Get(CurrentScript, key);

    /// <inheritdoc />
    public void StateSet(string key, ScriptValue value) => _state.Set(CurrentScript, key, value);

    // ---------------------------------------------------------------- terminals

    /// <inheritdoc />
    public void TermWrite(int termId, ReadOnlySpan<byte> bytes)
    {
        var terminal = _terminals.Get(termId);
        if (terminal is null)
        {
            CoreLog.LogOnce("termwrite:" + termId, LogLevel.Warn, $"Write to unknown terminal {termId} ignored.");
            return;
        }

        terminal.Write(bytes);
    }

    /// <inheritdoc />
    public ScriptValue TermInfo(int termId)
    {
        var terminal = _terminals.Get(termId);
        if (terminal is null)
            return ScriptValue.Null;

        return ScriptValue.Map(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["cols"] = ScriptValue.From(terminal.Cols),
            ["rows"] = ScriptValue.From(terminal.Rows),
            ["attached"] = ScriptValue.From(terminal.Attached),
        });
    }

    /// <inheritdoc />
    public void TermSetTitle(int termId, string title) => _terminals.Get(termId)?.SetTitle(title);

    /// <inheritdoc />
    public void TermPolicy(int termId, bool stopWhenDetached)
    {
        var terminal = _terminals.Get(termId);
        if (terminal is not null)
            terminal.StopWhenDetached = stopWhenDetached;
    }

    // ---------------------------------------------------------------- helpers

    private string? ActiveVesselId()
    {
        try
        {
            var value = _bridge.Read("time", "activeVesselId");
            return value is { Kind: ScriptValueKind.String } ? value.AsString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseTerminal(string handle, out int termId)
    {
        termId = 0;
        return handle.StartsWith("term:", StringComparison.Ordinal)
               && int.TryParse(handle.AsSpan("term:".Length), out termId);
    }

    private static string HandlePrefix(string handle)
    {
        var colon = handle.IndexOf(':');
        return colon < 0 ? handle : handle[..colon];
    }

    private void Append(string script, LogEntry entry)
    {
        var capacity = Math.Max(1, Options.OutputLinesPerScript);
        lock (_outputGate)
        {
            if (!_output.TryGetValue(script, out var ring))
                _output[script] = ring = new Queue<LogEntry>();

            ring.Enqueue(entry);
            while (ring.Count > capacity)
                ring.Dequeue();
        }

        try
        {
            Output?.Invoke(script, entry);
        }
        catch (Exception ex)
        {
            CoreLog.LogOnce("facade.output", LogLevel.Warn, $"An Output handler failed: {ex.Message}");
        }
    }

    private void Leave(int depth)
    {
        // Disposal restores the previous script even when scopes are disposed out of order.
        if (_scriptStack.Count >= depth)
            _scriptStack.RemoveRange(depth - 1, _scriptStack.Count - depth + 1);
    }

    private sealed class ScriptScope(HostFacade owner, int depth) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Leave(depth);
        }
    }
}
