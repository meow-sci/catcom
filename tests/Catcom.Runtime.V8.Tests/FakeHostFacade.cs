using System.Collections.Concurrent;
using Catcom.Core.Commands;
using Catcom.Core.Hosting;
using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.V8.Tests;

/// <summary>One recorded <see cref="IHostFacade.Act"/> call.</summary>
public sealed record ActCall(string Action, string? VesselId, int Ordinal, double Value, double[]? Values, string? Token, string? Aux);

/// <summary>One recorded <see cref="IHostFacade.Wait"/> call with the wait that was handed to the script.</summary>
public sealed record WaitCall(string Kind, ScriptValue Arg, HostWait Wait);

/// <summary>One recorded <see cref="IHostFacade.Subscribe"/> call.</summary>
public sealed record SubscriptionCall(Guid Id, string Kind, string Key, ScriptCallable Callable, BindingOptions Options);

/// <summary>One recorded <see cref="IHostFacade.Log"/> call with the script it was attributed to.</summary>
public sealed record LogCall(int Level, string Message, string Script);

/// <summary>A recording, scriptable <see cref="IHostFacade"/> for the V8 runtime tests.</summary>
public sealed class FakeHostFacade : IHostFacade
{
    private readonly ConcurrentQueue<ActCall> _acts = new();
    private readonly ConcurrentQueue<WaitCall> _waits = new();
    private readonly ConcurrentQueue<SubscriptionCall> _subscriptions = new();
    private readonly ConcurrentQueue<LogCall> _logs = new();
    private readonly ConcurrentQueue<ScriptRuntimeException> _errors = new();
    private readonly ConcurrentQueue<string> _errorScripts = new();
    private readonly ConcurrentQueue<string> _entered = new();
    private readonly ConcurrentDictionary<string, ScriptValue> _state = new(StringComparer.Ordinal);
    private readonly List<(int TermId, byte[] Bytes)> _termWrites = [];
    private readonly Stack<string> _scriptStack = new();
    private long _nextWaitId;

    /// <summary>Decides what <see cref="Act"/> returns (default: Frame success).</summary>
    public Func<ActCall, ActResult> ActHandler { get; set; } = _ => new ActResult(CommandResult.Ok, null);

    /// <summary>Decides what <see cref="Read"/> returns (default: <c>id</c> reads the handle suffix, anything else null).</summary>
    public Func<string, string, ScriptValue> ReadHandler { get; set; } = (handle, path) =>
        path == "id" && handle.Contains(':') ? ScriptValue.From(handle[(handle.IndexOf(':') + 1)..]) : ScriptValue.Null;

    /// <summary>Decides what <see cref="Snapshot"/> returns.</summary>
    public Func<string, ScriptValue> SnapshotHandler { get; set; } = _ => ScriptValue.Null;

    /// <summary>Decides what <see cref="List"/> returns (default: an empty list).</summary>
    public Func<string, ScriptValue> ListHandler { get; set; } = _ => ScriptValue.List([]);

    /// <summary>Invoked on every log call (for tests that wait on a message).</summary>
    public Action<LogCall>? OnLog { get; set; }

    /// <summary>The recorded acts.</summary>
    public IReadOnlyList<ActCall> Acts => _acts.ToArray();

    /// <summary>The recorded waits.</summary>
    public IReadOnlyList<WaitCall> Waits => _waits.ToArray();

    /// <summary>The recorded subscriptions.</summary>
    public IReadOnlyList<SubscriptionCall> Subscriptions => _subscriptions.ToArray();

    /// <summary>The recorded log calls.</summary>
    public IReadOnlyList<LogCall> Logs => _logs.ToArray();

    /// <summary>The errors reported through <see cref="ReportError"/>.</summary>
    public IReadOnlyList<ScriptRuntimeException> ReportedErrors => _errors.ToArray();

    /// <summary>The script each reported error was attributed to (the current script when <see cref="ReportError"/> was called), parallel to <see cref="ReportedErrors"/>.</summary>
    public IReadOnlyList<string> ReportedErrorScripts => _errorScripts.ToArray();

    /// <summary>Every script name passed to <see cref="EnterScript"/>, in order.</summary>
    public IReadOnlyList<string> EnteredScripts => _entered.ToArray();

    /// <summary>The terminal writes (terminal id, bytes).</summary>
    public IReadOnlyList<(int TermId, byte[] Bytes)> TermWrites
    {
        get
        {
            lock (_termWrites)
                return _termWrites.ToArray();
        }
    }

    /// <summary>The persisted state store.</summary>
    public IReadOnlyDictionary<string, ScriptValue> State => _state;

    /// <summary>The log messages, in order.</summary>
    public IReadOnlyList<string> LogMessages => Logs.Select(l => l.Message).ToArray();

    /// <inheritdoc />
    public string CurrentScript
    {
        get
        {
            lock (_scriptStack)
                return _scriptStack.Count > 0 ? _scriptStack.Peek() : "";
        }
    }

    /// <inheritdoc />
    public ScriptValue Read(string handle, string path) => ReadHandler(handle, path);

    /// <inheritdoc />
    public ScriptValue Snapshot(string handle) => SnapshotHandler(handle);

    /// <inheritdoc />
    public ScriptValue List(string kind) => ListHandler(kind);

    /// <inheritdoc />
    public ActResult Act(string action, string? vesselId, int ordinal, double value, double[]? values, string? token, string? aux)
    {
        var call = new ActCall(action, vesselId, ordinal, value, values, token, aux);
        _acts.Enqueue(call);
        return ActHandler(call);
    }

    /// <inheritdoc />
    public HostWait Wait(string kind, ScriptValue arg)
    {
        var wait = new HostWait(Interlocked.Increment(ref _nextWaitId), kind);
        _waits.Enqueue(new WaitCall(kind, arg, wait));
        return wait;
    }

    /// <inheritdoc />
    public Guid Subscribe(string kind, string key, ScriptCallable fn, BindingOptions opts)
    {
        var id = Guid.NewGuid();
        _subscriptions.Enqueue(new SubscriptionCall(id, kind, key, fn, opts));
        return id;
    }

    /// <summary>The ids passed to <see cref="Unsubscribe"/>.</summary>
    public ConcurrentQueue<Guid> Unsubscribed { get; } = new();

    /// <inheritdoc />
    public void Unsubscribe(Guid id) => Unsubscribed.Enqueue(id);

    /// <inheritdoc />
    public void Log(int level, string message)
    {
        var call = new LogCall(level, message, CurrentScript);
        _logs.Enqueue(call);
        OnLog?.Invoke(call);
    }

    /// <summary>The console commands executed.</summary>
    public ConcurrentQueue<string> ConsoleCommands { get; } = new();

    /// <inheritdoc />
    public void ConsoleExec(string command) => ConsoleCommands.Enqueue(command);

    /// <inheritdoc />
    public ScriptValue StateGet(string key) => _state.TryGetValue(key, out var value) ? value : ScriptValue.Null;

    /// <inheritdoc />
    public void StateSet(string key, ScriptValue value) => _state[key] = value;

    /// <inheritdoc />
    public void TermWrite(int termId, ReadOnlySpan<byte> bytes)
    {
        lock (_termWrites)
            _termWrites.Add((termId, bytes.ToArray()));
    }

    /// <inheritdoc />
    public ScriptValue TermInfo(int termId) => ScriptValue.Map(new Dictionary<string, ScriptValue>
    {
        ["cols"] = ScriptValue.From(80d),
        ["rows"] = ScriptValue.From(24d),
        ["attached"] = ScriptValue.From(1d),
    });

    /// <summary>The titles set with <see cref="TermSetTitle"/>.</summary>
    public ConcurrentQueue<(int TermId, string Title)> Titles { get; } = new();

    /// <inheritdoc />
    public void TermSetTitle(int termId, string title) => Titles.Enqueue((termId, title));

    /// <summary>The policies set with <see cref="TermPolicy"/>.</summary>
    public ConcurrentQueue<(int TermId, bool StopWhenDetached)> Policies { get; } = new();

    /// <inheritdoc />
    public void TermPolicy(int termId, bool stopWhenDetached) => Policies.Enqueue((termId, stopWhenDetached));

    /// <inheritdoc />
    public IDisposable EnterScript(string script)
    {
        _entered.Enqueue(script);
        lock (_scriptStack)
            _scriptStack.Push(script);
        return new Scope(this);
    }

    /// <inheritdoc />
    public void ReportError(ScriptRuntimeException error)
    {
        _errorScripts.Enqueue(CurrentScript);
        _errors.Enqueue(error);
    }

    private sealed class Scope(FakeHostFacade owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (owner._scriptStack)
                owner._scriptStack.Pop();
        }
    }
}
