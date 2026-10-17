using Catcom.Core.Commands;
using Catcom.Core.Hosting;
using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>A recording <see cref="IHostFacade"/> for runtime tests (single-threaded: used from the test thread only).</summary>
public sealed class FakeHostFacade : IHostFacade
{
    private readonly Stack<string> _scripts = new();
    private long _waitId;

    /// <summary>Every <c>Act</c> call.</summary>
    public List<ActCall> Acts { get; } = [];

    /// <summary>Every <c>Wait</c> call.</summary>
    public List<WaitCall> Waits { get; } = [];

    /// <summary>Every <c>Subscribe</c> call.</summary>
    public List<SubscriptionCall> Subscriptions { get; } = [];

    /// <summary>Every id passed to <c>Unsubscribe</c>.</summary>
    public List<Guid> Unsubscribed { get; } = [];

    /// <summary>Every <c>Log</c> call.</summary>
    public List<LogCall> Logs { get; } = [];

    /// <summary>Every error passed to <c>ReportError</c>.</summary>
    public List<ScriptRuntimeException> ReportedErrors { get; } = [];

    /// <summary>Every script name passed to <c>EnterScript</c>, in order.</summary>
    public List<string> EnteredScripts { get; } = [];

    /// <summary>Every <c>ConsoleExec</c> command.</summary>
    public List<string> ConsoleCommands { get; } = [];

    /// <summary>Every <c>Read</c> call as <c>handle|path</c>.</summary>
    public List<string> Reads { get; } = [];

    /// <summary>Every <c>TermWrite</c> call (terminal id, bytes).</summary>
    public List<(int TermId, byte[] Bytes)> TermWrites { get; } = [];

    /// <summary>The persisted state written through <c>StateSet</c>.</summary>
    public Dictionary<string, ScriptValue> State { get; } = [];

    /// <summary>Overrides the result of <c>Act</c>. The default is an immediate successful result.</summary>
    public Func<ActCall, ActResult>? ActHandler { get; set; }

    /// <summary>Overrides the result of <c>Read</c> (handle, path).</summary>
    public Func<string, string, ScriptValue>? ReadHandler { get; set; }

    /// <summary>Overrides the result of <c>List</c>.</summary>
    public Func<string, ScriptValue>? ListHandler { get; set; }

    /// <summary>Called for every <c>Log</c> (used by tests to signal other threads).</summary>
    public Action<LogCall>? OnLog { get; set; }

    /// <summary>Called for every <c>Act</c>.</summary>
    public Action<ActCall>? OnAct { get; set; }

    /// <summary>Called for every <c>ConsoleExec</c> (may throw to simulate a failing console).</summary>
    public Action<string>? OnConsoleExec { get; set; }

    /// <inheritdoc />
    public string CurrentScript => _scripts.Count == 0 ? string.Empty : _scripts.Peek();

    /// <inheritdoc />
    public ScriptValue Read(string handle, string path)
    {
        Reads.Add($"{handle}|{path}");
        return ReadHandler?.Invoke(handle, path) ?? ScriptValue.Null;
    }

    /// <inheritdoc />
    public ScriptValue Snapshot(string handle)
        => ScriptValue.Map(new Dictionary<string, ScriptValue> { ["handle"] = ScriptValue.From(handle) });

    /// <inheritdoc />
    public ScriptValue List(string kind)
        => ListHandler?.Invoke(kind) ?? ScriptValue.List([ScriptValue.From(kind)]);

    /// <inheritdoc />
    public ActResult Act(string action, string? vesselId, int ordinal, double value, double[]? values, string? token, string? aux)
    {
        var call = new ActCall(action, vesselId, ordinal, value, values, token, aux);
        Acts.Add(call);
        OnAct?.Invoke(call);
        return ActHandler?.Invoke(call) ?? new ActResult(CommandResult.Ok, null);
    }

    /// <inheritdoc />
    public HostWait Wait(string kind, ScriptValue arg)
    {
        var wait = new HostWait(++_waitId, kind);
        Waits.Add(new WaitCall(kind, arg, wait));
        return wait;
    }

    /// <inheritdoc />
    public Guid Subscribe(string kind, string key, ScriptCallable fn, BindingOptions opts)
    {
        var id = Guid.NewGuid();
        Subscriptions.Add(new SubscriptionCall(id, kind, key, fn, opts));
        return id;
    }

    /// <inheritdoc />
    public void Unsubscribe(Guid id) => Unsubscribed.Add(id);

    /// <inheritdoc />
    public void Log(int level, string message)
    {
        var call = new LogCall(level, message, CurrentScript);
        Logs.Add(call);
        OnLog?.Invoke(call);
    }

    /// <inheritdoc />
    public void ConsoleExec(string command)
    {
        ConsoleCommands.Add(command);
        OnConsoleExec?.Invoke(command);
    }

    /// <inheritdoc />
    public ScriptValue StateGet(string key) => State.TryGetValue(key, out var value) ? value : ScriptValue.Null;

    /// <inheritdoc />
    public void StateSet(string key, ScriptValue value) => State[key] = value;

    /// <inheritdoc />
    public void TermWrite(int termId, ReadOnlySpan<byte> bytes) => TermWrites.Add((termId, bytes.ToArray()));

    /// <inheritdoc />
    public ScriptValue TermInfo(int termId)
        => ScriptValue.Map(new Dictionary<string, ScriptValue>
        {
            ["cols"] = ScriptValue.From(80d),
            ["rows"] = ScriptValue.From(24d),
            ["attached"] = ScriptValue.From(1d),
        });

    /// <inheritdoc />
    public void TermSetTitle(int termId, string title)
    {
    }

    /// <inheritdoc />
    public void TermPolicy(int termId, bool stopWhenDetached)
    {
    }

    /// <inheritdoc />
    public IDisposable EnterScript(string script)
    {
        EnteredScripts.Add(script);
        _scripts.Push(script);
        return new Scope(this);
    }

    /// <inheritdoc />
    public void ReportError(ScriptRuntimeException error) => ReportedErrors.Add(error);

    private sealed class Scope(FakeHostFacade owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner._scripts.Pop();
        }
    }
}
