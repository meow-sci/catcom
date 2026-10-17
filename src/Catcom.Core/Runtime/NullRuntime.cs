using System.Diagnostics;
using Catcom.Core.Hosting;
using Catcom.Core.Logging;
using Catcom.Core.Values;

namespace Catcom.Core.Runtime;

/// <summary>
///     A script runtime whose "scripts" are C# lambdas: scripts are registered by name with a set of
///     exports and an optional top-level action, calls run the lambdas, and a lambda may suspend on a host
///     wait with <see cref="Await"/>. It exercises every host/scheduler/catalog path without a real
///     language runtime; the tests of the scheduler, the catalog and the host facade run on it, and it
///     doubles as a smoke runtime in the game. Single-threaded except <see cref="Interrupt"/>.
/// </summary>
public sealed class NullRuntime : IScriptRuntime
{
    private const string AwaitKey = "$nullAwait";

    private readonly Dictionary<string, Registration> _registered = new(StringComparer.Ordinal);
    private readonly Dictionary<long, FunctionEntry> _functions = [];
    private readonly Dictionary<long, ModuleState> _modules = [];
    private readonly Dictionary<long, AwaitRequest> _awaits = [];
    private readonly List<Continuation> _continuations = [];
    private IHostFacade? _host;
    private long _nextToken;
    private long _nextAwait;
    private long _loadingModule;
    private int _interrupted;
    private bool _disposed;
    private string? _unavailable;
    private double _lastCallMs;
    private long _calls;

    /// <summary>Creates the runtime.</summary>
    /// <param name="id">The runtime id (what <see cref="ScriptCallable.RuntimeId"/> carries).</param>
    /// <param name="language">The language it claims (<see cref="ScriptLanguages.Lua"/> or <see cref="ScriptLanguages.TypeScript"/>).</param>
    public NullRuntime(string id = "null", string language = ScriptLanguages.Lua)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(language);
        Id = id;
        Language = language;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Language { get; }

    /// <inheritdoc />
    public bool IsAvailable => _unavailable is null && !_disposed;

    /// <inheritdoc />
    public string? UnavailableReason => _unavailable;

    /// <inheritdoc />
    public RuntimeStats Stats => new(0, _lastCallMs, Interlocked.Read(ref _calls), false, "null");

    /// <summary>The number of suspended continuations (ready or still waiting).</summary>
    public int PendingContinuations => _continuations.Count;

    /// <summary>The names of the loaded modules.</summary>
    public IReadOnlyList<string> Loaded => _modules.Values.Select(m => m.Name).ToArray();

    /// <summary>Makes the runtime report itself unavailable (the catalog then refuses to load into it).</summary>
    /// <param name="reason">Why the runtime is unavailable.</param>
    public void MarkUnavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        _unavailable = reason;
    }

    /// <summary>Registers (or replaces) the "source" of a script: its exports and its top-level code.</summary>
    /// <param name="scriptName">The catalog name the script is loaded under.</param>
    /// <param name="exports">The exported functions by name.</param>
    /// <param name="onLoad">The top-level code, run inside <see cref="Load"/> (it may call <c>host.Subscribe</c> with <see cref="CreateCallable"/>).</param>
    public void Register(string scriptName, IReadOnlyDictionary<string, NullFunction> exports, Action<IHostFacade, NullRuntime>? onLoad = null)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        ArgumentNullException.ThrowIfNull(exports);
        _registered[scriptName] = new Registration(new Dictionary<string, NullFunction>(exports, StringComparer.Ordinal), onLoad);
    }

    /// <summary>Wraps a lambda as a callable owned by this runtime (and, while a module loads, by that module).</summary>
    /// <param name="fn">The function.</param>
    /// <param name="name">A diagnostic / export name.</param>
    /// <returns>The callable; disposing it forgets the function.</returns>
    public ScriptCallable CreateCallable(NullFunction fn, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return NewCallable(fn, name, _loadingModule);
    }

    /// <summary>
    ///     Creates the marker a <see cref="NullFunction"/> returns to suspend on a host wait.
    ///     <paramref name="continuation"/> runs from <see cref="Advance"/> once the wait completed (and may
    ///     itself return another marker to chain). A failed or cancelled wait reports a script error instead.
    /// </summary>
    /// <param name="wait">The wait to suspend on.</param>
    /// <param name="continuation">Receives the wait result.</param>
    /// <returns>The marker value.</returns>
    public ScriptValue Await(HostWait wait, Func<ScriptValue, ScriptValue> continuation)
    {
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(continuation);

        var id = ++_nextAwait;
        _awaits[id] = new AwaitRequest(wait, continuation);
        return ScriptValue.Map(new Dictionary<string, ScriptValue> { [AwaitKey] = ScriptValue.From((double)id) });
    }

    /// <summary>Test lambdas call this inside loops; it throws <c>ScriptRuntimeException("interrupted")</c> once <see cref="Interrupt"/> was requested.</summary>
    /// <exception cref="ScriptRuntimeException">An interrupt is pending.</exception>
    public void CheckInterrupt()
    {
        if (Volatile.Read(ref _interrupted) != 0)
            throw new ScriptRuntimeException("interrupted");
    }

    /// <inheritdoc />
    public void Interrupt() => Volatile.Write(ref _interrupted, 1);

    /// <inheritdoc />
    public void Initialize(IHostFacade host, RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <inheritdoc />
    public ScriptModule Load(ScriptSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var host = RequireHost();

        if (source.Code.Contains("@syntax-error", StringComparison.Ordinal))
        {
            var firstLine = source.Code.Split('\n', 2)[0].TrimEnd('\r');
            throw new ScriptLoadException("syntax error", source.Path, 1, 1, firstLine);
        }

        var token = ++_nextToken;
        var module = new ModuleState(token, source.Name);
        _modules[token] = module;

        var previousLoading = _loadingModule;
        _loadingModule = token;
        try
        {
            if (_registered.TryGetValue(source.Name, out var registration))
            {
                foreach (var pair in registration.Exports)
                    module.Exports[pair.Key] = pair.Value;
                registration.OnLoad?.Invoke(host, this);
            }
        }
        catch (Exception ex)
        {
            DropModule(token);
            throw new ScriptLoadException(ex.Message, source.Path, 0, 0, "", ex);
        }
        finally
        {
            _loadingModule = previousLoading;
        }

        return new ScriptModule(Id, source.Name, token);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Exports(ScriptModule module)
        => Resolve(module).Exports.Keys.ToArray();

    /// <inheritdoc />
    public ScriptCallable GetExport(ScriptModule module, string name)
    {
        var state = Resolve(module);
        if (!state.Exports.TryGetValue(name, out var fn))
            throw new KeyNotFoundException($"module '{state.Name}' has no export '{name}'");
        return NewCallable(fn, name, state.Token);
    }

    /// <inheritdoc />
    public ScriptValue Invoke(ScriptCallable fn, ReadOnlySpan<ScriptValue> args)
    {
        ArgumentNullException.ThrowIfNull(fn);
        var host = RequireHost();

        if (!string.Equals(fn.RuntimeId, Id, StringComparison.Ordinal) || !_functions.TryGetValue(fn.Token, out var entry))
            throw new ScriptRuntimeException($"unknown function {fn}");

        Volatile.Write(ref _interrupted, 0);
        Interlocked.Increment(ref _calls);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = entry.Fn(host, args.ToArray());
            if (TryTakeAwait(result, out var request))
            {
                _continuations.Add(new Continuation(request, host.CurrentScript, entry.ModuleToken));
                return ScriptValue.Null;
            }

            return result ?? ScriptValue.Null;
        }
        catch (ScriptRuntimeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ScriptRuntimeException(ex.Message, ex.ToString(), null, null, ex);
        }
        finally
        {
            _lastCallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    /// <summary>
    ///     Runs the continuations whose wait completed, in FIFO order, each under
    ///     <see cref="IHostFacade.EnterScript"/> of the script that suspended, until none is ready or the
    ///     budget is exhausted. Errors go to <see cref="IHostFacade.ReportError"/>.
    /// </summary>
    /// <param name="budget">How long to run.</param>
    /// <returns><see langword="true"/> when completed waits still have continuations left to run (continuations still waiting are not work).</returns>
    public bool Advance(TimeSpan budget)
    {
        var host = RequireHost();
        Volatile.Write(ref _interrupted, 0);
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < budget)
        {
            var index = _continuations.FindIndex(c => c.Request.Wait.IsCompleted);
            if (index < 0)
                return false;

            var continuation = _continuations[index];
            _continuations.RemoveAt(index);
            RunContinuation(host, continuation);
        }

        return _continuations.Any(c => c.Request.Wait.IsCompleted);
    }

    /// <inheritdoc />
    public void Unload(ScriptModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        DropModule(module.Token);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _continuations.Clear();
        _awaits.Clear();
        _modules.Clear();
        _functions.Clear();
    }

    // ---------------------------------------------------------------- internals

    private void RunContinuation(IHostFacade host, Continuation continuation)
    {
        var wait = continuation.Request.Wait;
        using (host.EnterScript(continuation.Script))
        {
            if (wait.Task.IsFaulted || wait.Task.IsCanceled)
            {
                var reason = wait.Task.IsCanceled ? "cancelled" : wait.Task.Exception?.GetBaseException().Message ?? "failed";
                host.ReportError(new ScriptRuntimeException($"wait failed: {reason}"));
                return;
            }

            try
            {
                var result = continuation.Request.Continue(wait.Task.Result);
                if (TryTakeAwait(result, out var next))
                    _continuations.Add(new Continuation(next, continuation.Script, continuation.ModuleToken));
            }
            catch (ScriptRuntimeException ex)
            {
                host.ReportError(ex);
            }
            catch (Exception ex)
            {
                host.ReportError(new ScriptRuntimeException(ex.Message, ex.ToString(), null, null, ex));
            }
        }
    }

    private bool TryTakeAwait(ScriptValue? value, out AwaitRequest request)
    {
        request = null!;
        if (value is null || value.Kind != ScriptValueKind.Map || !value.TryGet(AwaitKey, out var idValue)
            || idValue.Kind != ScriptValueKind.Number)
            return false;

        return _awaits.Remove((long)idValue.AsNumber(), out request!);
    }

    private ScriptCallable NewCallable(NullFunction fn, string? name, long moduleToken)
    {
        var token = ++_nextToken;
        _functions[token] = new FunctionEntry(fn, moduleToken);
        var callable = ScriptCallable.Create(Id, token, name, c =>
        {
            _functions.Remove(c.Token);
            if (moduleToken != 0 && _modules.TryGetValue(moduleToken, out var owner))
                owner.Callables.Remove(c);
        });
        if (moduleToken != 0 && _modules.TryGetValue(moduleToken, out var module))
            module.Callables.Add(callable);
        return callable;
    }

    private void DropModule(long token)
    {
        if (!_modules.Remove(token, out var module))
            return;

        _continuations.RemoveAll(c => c.ModuleToken == token);
        foreach (var callable in module.Callables)
        {
            try
            {
                callable.Dispose();
            }
            catch (Exception ex)
            {
                Log.LogOnce("nullruntime.dispose", LogLevel.Warn, $"Disposing a callable failed: {ex.Message}");
            }
        }
    }

    private ModuleState Resolve(ScriptModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!string.Equals(module.RuntimeId, Id, StringComparison.Ordinal) || !_modules.TryGetValue(module.Token, out var state))
            throw new KeyNotFoundException($"module '{module.Name}' is not loaded");
        return state;
    }

    private IHostFacade RequireHost()
        => _host ?? throw new InvalidOperationException("the runtime is not initialized");

    private sealed record Registration(Dictionary<string, NullFunction> Exports, Action<IHostFacade, NullRuntime>? OnLoad);

    private sealed record FunctionEntry(NullFunction Fn, long ModuleToken);

    private sealed record AwaitRequest(HostWait Wait, Func<ScriptValue, ScriptValue> Continue);

    private sealed record Continuation(AwaitRequest Request, string Script, long ModuleToken);

    private sealed class ModuleState(long token, string name)
    {
        public long Token { get; } = token;

        public string Name { get; } = name;

        public Dictionary<string, NullFunction> Exports { get; } = new(StringComparer.Ordinal);

        public List<ScriptCallable> Callables { get; } = [];
    }
}
