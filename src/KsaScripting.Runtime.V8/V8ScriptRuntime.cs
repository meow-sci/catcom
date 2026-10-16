using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;
using KsaScripting.Runtime.V8.Interop;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace KsaScripting.Runtime.V8;

/// <summary>
///     The V8 (Microsoft ClearScript) TypeScript/JavaScript script runtime. TypeScript is transpiled in a private compiler
///     isolate (<see cref="TypeScriptCompiler"/>); every script gets its own <see cref="V8Runtime"/> (one V8 isolate) holding its
///     own <see cref="V8ScriptEngine"/> (and so its own module graph and globals). Async functions run on the game thread:
///     promise resolutions and host-wait completions are queued by a per-engine <see cref="GameThreadSynchronizationContext"/>
///     and executed by <see cref="Advance"/>.
/// </summary>
/// <remarks>
///     <para>
///         Named <c>V8ScriptRuntime</c> to avoid clashing with ClearScript's <c>V8Runtime</c>. All members run on the game thread
///         except <see cref="Interrupt"/> (any thread) and the properties that only read cached state.
///     </para>
///     <para>
///         <b>One isolate per script.</b> ClearScript applies the soft heap limit (<c>V8Runtime.MaxHeapSize</c>) to a whole
///         isolate, not to an engine, and a violation blocks every script execution in that isolate until the limit is
///         re-assigned. Sharing one isolate would therefore let a script that hoards memory fail the calls of every other script
///         (no fault isolation); with an isolate per script <c>v8_heap_limit_mb</c> really is a "per script engine" limit, an
///         unloaded script gives all its memory back, and values never cross isolates (a script function is only ever
///         reachable through a <see cref="ScriptCallable"/> owned by its script; values handed between scripts travel as
///         <see cref="ScriptValue"/>s, where a function becomes <c>null</c>).
///     </para>
///     <para>
///         <b>Debugging exception.</b> When <see cref="RuntimeOptions.DebugPort"/> is positive all scripts share ONE debuggable
///         isolate (<c>ksa-scripts</c>) so there is a single inspector target on that port: every isolate that enables remote
///         debugging starts its own inspector endpoint and would need a port of its own. In that mode the soft heap limit is
///         shared by all scripts again (a developer convenience, not the production configuration).
///     </para>
/// </remarks>
public sealed class V8ScriptRuntime : IScriptRuntime
{
    private const string TranspilerMissingReason = "TypeScript transpiler assets missing (run tools/fetch-ts.sh)";
    private const string HeapViolationText = "exceeded its memory limit";

    private readonly object _gate = new();
    private readonly object _stackGate = new();
    private readonly ScriptWorkQueue _queue = new();
    private readonly ConcurrentDictionary<long, LoadedScript> _scripts = new();
    private readonly ConcurrentDictionary<long, CallableEntry> _callables = new();
    private readonly Stack<LoadedScript> _engineStack = new();
    private readonly object _rejectionGate = new();
    private readonly HashSet<LoadedScript> _rejectionOwners = [];
    private readonly Lazy<string?> _baseAvailability = new(ComputeAvailability, LazyThreadSafetyMode.ExecutionAndPublication);

    private IHostFacade? _host;
    private RuntimeOptions? _options;
    private TypeScriptCompiler? _compiler;
    private V8Runtime? _debugRuntime;
    private bool _initialized;
    private volatile string? _initFailure;
    private volatile LoadedScript? _currentEngine;
    private long _nextModuleToken;
    private long _nextCallableToken;
    private long _callsTotal;
    private double _lastCallMs;
    private int _disposed;

    /// <inheritdoc />
    public string Id => "v8";

    /// <inheritdoc />
    public string Language => ScriptLanguages.TypeScript;

    /// <inheritdoc />
    public bool IsAvailable => UnavailableReason is null;

    /// <inheritdoc />
    public string? UnavailableReason => _initFailure ?? _baseAvailability.Value;

    /// <inheritdoc />
    public RuntimeStats Stats
    {
        get
        {
            // The sum over every live script isolate (the compiler isolate is not a script's and stays out); the shared
            // debuggable isolate is counted once.
            long heap = 0;
            if (Volatile.Read(ref _disposed) == 0)
            {
                if (_debugRuntime is { } shared)
                {
                    heap = UsedHeapBytes(shared);
                }
                else
                {
                    foreach (var script in _scripts.Values)
                    {
                        if (!script.IsDisposed)
                            heap += UsedHeapBytes(script.Runtime);
                    }
                }
            }

            var typeScript = _compiler?.Version ?? "n/a";
            return new RuntimeStats(
                heap,
                Volatile.Read(ref _lastCallMs),
                Interlocked.Read(ref _callsTotal),
                true,
                $"ClearScript {TypeScriptCompiler.ClearScriptVersion}; TypeScript {typeScript}");
        }
    }

    /// <summary>The TypeScript compiler (test aid).</summary>
    internal TypeScriptCompiler? Compiler => _compiler;

    /// <summary>Whether the transpiler fell back to sucrase (test and diagnostics aid).</summary>
    internal bool UsingSucrase => _compiler?.UsingSucrase ?? false;

    /// <summary>Whether the persisted V8 code cache was accepted when the TypeScript compiler was loaded (<c>null</c> when none).</summary>
    internal bool? CompilerCodeCacheAccepted => _compiler?.CodeCacheAccepted;

    /// <summary>The code cache file used for <c>typescript.js</c> (test and diagnostics aid).</summary>
    internal string? CompilerCodeCachePath => _compiler?.CodeCachePath;

    /// <summary>The number of loaded scripts (test aid).</summary>
    internal int LoadedScriptCount => _scripts.Count;

    /// <summary>
    ///     The distinct V8 isolates the loaded scripts live in (test aid): one per script, or a single shared one when
    ///     <see cref="RuntimeOptions.DebugPort"/> is positive. The private compiler isolate is not included.
    /// </summary>
    internal IReadOnlyList<V8Runtime> ScriptIsolates
        => _scripts.Values.Where(script => !script.IsDisposed).Select(script => script.Runtime).Distinct().ToArray();

    /// <summary>Blocks until work is queued for <see cref="Advance"/> or the timeout passes (test aid; the game never blocks).</summary>
    /// <param name="timeout">The longest time to wait.</param>
    /// <returns><c>true</c> when work is queued.</returns>
    internal bool WaitForWork(TimeSpan timeout) => _queue.WaitForWork(timeout);

    // ------------------------------------------------------------------ Initialize

    /// <inheritdoc />
    public void Initialize(IHostFacade host, RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_gate)
        {
            if (_initialized)
                return;

            _initialized = true;
            _host = host;
            _options = options;
            _queue.GameThreadId = Environment.CurrentManagedThreadId;

            if (_baseAvailability.Value is not null)
                return;

            try
            {
                var stubsDir = string.IsNullOrWhiteSpace(options.StubsDir) ? options.ScriptsDir : options.StubsDir;
                _compiler = TypeScriptCompiler.Create(Path.GetFullPath(stubsDir), host.Log);
                if (options.DebugPort is > 0)
                    _debugRuntime = CreateIsolate("ksa-scripts", options, debugging: true);
            }
            catch (Exception ex)
            {
                _compiler?.Dispose();
                _compiler = null;
                _debugRuntime?.Dispose();
                _debugRuntime = null;
                _initFailure = ex is InvalidOperationException { Message: var message } && message.StartsWith("TypeScript transpiler assets missing", StringComparison.Ordinal)
                    ? TranspilerMissingReason
                    : $"V8 runtime failed to initialize: {ex.GetBaseException().Message}";
            }
        }
    }

    /// <summary>
    ///     Creates one script isolate: the memory constraints, the optional remote inspector (debug mode only) and the soft
    ///     heap limit.
    /// </summary>
    /// <param name="name">The isolate's name (<c>ksa-script:&lt;script&gt;</c>, or <c>ksa-scripts</c> for the shared debuggable one).</param>
    /// <param name="options">The runtime options.</param>
    /// <param name="debugging">Whether this is the shared debuggable isolate that serves the inspector on <see cref="RuntimeOptions.DebugPort"/>.</param>
    /// <returns>The isolate; the caller owns it.</returns>
    private static V8Runtime CreateIsolate(string name, RuntimeOptions options, bool debugging)
    {
        var constraints = new V8RuntimeConstraints
        {
            MaxOldSpaceSize = 256,
            MaxNewSpaceSize = 16,
            MaxArrayBufferAllocation = 64UL * 1024 * 1024,
        };

        var flags = V8RuntimeFlags.EnableDynamicModuleImports;
        V8Runtime runtime;
        if (debugging && options.DebugPort is > 0 and var port)
        {
            flags |= V8RuntimeFlags.EnableDebugging | V8RuntimeFlags.EnableRemoteDebugging;
            runtime = new V8Runtime(name, constraints, flags, port);
        }
        else
        {
            runtime = new V8Runtime(name, constraints, flags);
        }

        try
        {
            if (options.HeapLimitBytes > 0)
            {
                // ClearScript applies this soft limit per V8Runtime (isolate), which is why every script owns one: the limit is
                // per script engine. Interrupt policy (the default) rather than Exception: a violation terminates execution,
                // which a script cannot catch. With the Exception policy a script that wraps its allocation loop in try/catch
                // keeps allocating until V8 hits its hard heap limit and aborts the whole game process (verified).
                runtime.MaxHeapSize = (UIntPtr)(ulong)options.HeapLimitBytes;
                runtime.HeapSizeSampleInterval = TimeSpan.FromMilliseconds(50);
                runtime.HeapSizeViolationPolicy = V8RuntimeViolationPolicy.Interrupt;
            }
        }
        catch
        {
            runtime.Dispose();
            throw;
        }

        return runtime;
    }

    private static long UsedHeapBytes(V8Runtime runtime)
    {
        try
        {
            return (long)runtime.GetHeapInfo().UsedHeapSize;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return 0;
        }
    }

    private static string? ComputeAvailability()
    {
        if (!V8Availability.TryEnsure(out var reason))
            return reason;

        return V8Assets.HasTranspiler ? null : TranspilerMissingReason;
    }

    // ------------------------------------------------------------------ Load

    /// <inheritdoc />
    public ScriptModule Load(ScriptSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        if (UnavailableReason is { } reason)
            throw new ScriptLoadException(reason, source.Path);
        if (_host is null || _options is null || _compiler is null)
            throw new ScriptLoadException("the V8 runtime has not been initialized", source.Path);
        if (!string.Equals(source.Language, ScriptLanguages.TypeScript, StringComparison.OrdinalIgnoreCase))
            throw new ScriptLoadException($"the V8 runtime cannot load '{source.Language}' scripts", source.Path);

        var host = _host;
        using var hostScope = host.EnterScript(source.Name);

        var fullPath = Path.GetFullPath(source.Path);
        var extension = Path.GetExtension(fullPath);
        var isTypeScript = !extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase);

        // Transpile the entry up front so syntax errors surface directly with file, line and column.
        if (isTypeScript)
            _compiler.TranspileOrThrow(source.Code, fullPath);

        var token = Interlocked.Increment(ref _nextModuleToken);
        var context = new GameThreadSynchronizationContext(source.Name, _queue);
        var loader = new TsDocumentLoader(Path.GetFullPath(_options.ScriptsDir), _compiler, PreludeSource.Text);
        loader.AddOverride(fullPath, source.Code, isTypeScript);

        var (runtime, ownsRuntime, engine) = CreateIsolateAndEngine(source);
        var script = new LoadedScript(token, source.Name, fullPath, runtime, ownsRuntime, engine, context, loader);

        using (Enter(script))
        {
            try
            {
                engine.PromiseRejectionCallback = (kind, promise, reason) => OnPromiseRejection(script, kind, promise, reason);
                EvaluateEntry(script, source, fullPath);
            }
            catch (Exception ex)
            {
                // Describe the failure first: it reads the script's error object and its isolate's heap, which the disposal
                // below (engine, then the isolate the script owns) takes away.
                var failure = ToLoadException(ex, script, source, fullPath);
                DisposeScript(script);
                throw failure;
            }
        }

        _scripts[token] = script;
        ScheduleRejectionReports();
        return new ScriptModule(Id, source.Name, token);
    }

    /// <summary>
    ///     Gives a loading script its isolate and engine: a new isolate of its own, or (debug mode) the shared debuggable one.
    ///     Nothing leaks when creation fails: a half-built isolate is disposed.
    /// </summary>
    private (V8Runtime Runtime, bool OwnsRuntime, V8ScriptEngine Engine) CreateIsolateAndEngine(ScriptSource source)
    {
        var options = _options!;
        var debugging = _debugRuntime is not null;
        var runtime = _debugRuntime;
        var ownsRuntime = false;
        try
        {
            if (runtime is null)
            {
                runtime = CreateIsolate("ksa-script:" + source.Name, options, debugging: false);
                ownsRuntime = true;
            }

            var flags = V8ScriptEngineFlags.EnableTaskPromiseConversion
                | V8ScriptEngineFlags.UseSynchronizationContexts
                | V8ScriptEngineFlags.EnableDynamicModuleImports
                | V8ScriptEngineFlags.EnableStringifyEnhancements
                | V8ScriptEngineFlags.HideHostExceptions;
            if (debugging)
                flags |= V8ScriptEngineFlags.EnableDebugging | V8ScriptEngineFlags.EnableRemoteDebugging;

            return (runtime, ownsRuntime, runtime.CreateScriptEngine("script:" + source.Name, flags));
        }
        catch (Exception ex)
        {
            if (ownsRuntime)
                TryDispose(runtime);

            throw new ScriptLoadException($"the V8 isolate for the script could not be created: {ex.GetBaseException().Message}", source.Path, inner: ex);
        }
    }

    private void EvaluateEntry(LoadedScript script, ScriptSource source, string fullPath)
    {
        var engine = script.Engine;
        var loader = script.Loader;

        // AccessFlags.None: ClearScript's own file/web loading stays off. The custom loader is still consulted for every
        // import (verified) and itself enforces containment inside the scripts folder.
        engine.DocumentSettings = new DocumentSettings { AccessFlags = DocumentAccessFlags.None, Loader = loader };
        // The prelude is registered as a system document, which ClearScript serves before any loader: ONE module instance
        // per engine however many modules import "ksa".
        engine.DocumentSettings.AddSystemDocument(TsDocumentLoader.PreludeSpecifier, ModuleCategory.Standard, PreludeSource.Text);

        var values = JsValues.Create(engine, (fn, name) => RegisterCallable(script, fn, name));
        script.Values = values;
        values.InstallRejectionSink(reason => OnInvokedRejection(script, reason));
        var bridge = new HostBridge(
            _host!,
            values,
            script.Context.Scheduler,
            (fn, name) => RegisterCallable(script, fn, name),
            error => ReportAsyncFailure(script, error));
        // The native bridge is wrapped by a frozen JS facade that becomes the script-visible __host. Its promise-returning
        // methods build their Promises in JavaScript; the raw bridge is removed from the global object again.
        engine.AddHostObject("__hostNative", HostItemFlags.DirectAccess, bridge);
        values.InstallHost();

        try
        {
            engine.Execute(new DocumentInfo("ksa-init.mjs") { Category = ModuleCategory.Standard }, "import 'ksa';");
            values.InstallConsole();
        }
        catch (Exception ex) when (ex is IScriptEngineException)
        {
            throw new ScriptLoadException($"the ksa prelude failed to load: {ex.Message}", source.Path, inner: ex);
        }

        loader.LastError = null;
        var entrySpecifier = JsonSerializer.Serialize(new Uri(fullPath).AbsoluteUri);
        object? evaluation;
        // A module with top-level await is evaluated as a promise, which ClearScript hands back as a Task. With the engine's
        // synchronization context current, that Task would be completed through the game-thread queue - i.e. only on a later
        // Advance - so a top-level await that fails would look "still pending" here and its error would be lost. Without a
        // context the Task is completed inline: a failed evaluation is a faulted Task by the time Evaluate returns, and what
        // is still pending is waiting for a host wait (which is rejected below).
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            evaluation = engine.Evaluate(
                new DocumentInfo("ksa-entry.mjs") { Category = ModuleCategory.Standard },
                $"import * as m from {entrySpecifier}; globalThis.__ksaModule = m;");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        if (evaluation is Task { IsFaulted: true } faulted)
            throw faulted.Exception!.GetBaseException();

        var moduleNamespace = engine.Evaluate("globalThis.__ksaModule") as ScriptObject;
        if (moduleNamespace is null)
        {
            throw new ScriptLoadException(
                "top-level await of host waits is not supported; use main(ctx)",
                source.Path);
        }

        engine.Execute("delete globalThis.__ksaModule;");
        script.Namespace = moduleNamespace;
        script.ExportNames = values.ExportedFunctionNames(moduleNamespace);
    }

    private ScriptLoadException ToLoadException(Exception exception, LoadedScript script, ScriptSource source, string fullPath)
    {
        var ex = Unwrap(exception);
        if (ex is ScriptLoadException alreadyLoadException)
            return alreadyLoadException;

        // ClearScript reduces an exception thrown by the document loader to a script error carrying only its text; the
        // loader remembered the original so the file, line and column survive.
        if (script.Loader.LastError is { } loaderError && ex.Message.Contains(loaderError.Message, StringComparison.Ordinal))
        {
            return loaderError is ScriptLoadException loadError
                ? loadError
                : new ScriptLoadException(loaderError.Message, fullPath, inner: loaderError);
        }

        if (ex is ScriptInterruptedException)
            return new ScriptLoadException("interrupted", fullPath, inner: ex);

        if (ex is ScriptEngineException engineException)
        {
            var mapped = MapStack(engineException, script);
            var file = mapped.File ?? fullPath;
            var line = mapped.Line ?? 0;
            var snippet = line > 0 && script.Loader.TryGetFile(file, out var served) ? SourceMap.GetLine(served.Source, line) : "";
            return new ScriptLoadException(
                IsHeapViolation(engineException) ? HeapMessage(script) : engineException.Message,
                file,
                line,
                mapped.Column ?? 0,
                snippet,
                engineException);
        }

        return new ScriptLoadException(ex.Message, fullPath, inner: ex);
    }

    // ------------------------------------------------------------------ Exports

    /// <inheritdoc />
    public IReadOnlyList<string> Exports(ScriptModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return FindScript(module) is { } script ? script.ExportNames : [];
    }

    /// <inheritdoc />
    public ScriptCallable GetExport(ScriptModule module, string name)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(name);

        var script = FindScript(module)
            ?? throw new KeyNotFoundException($"module '{module.Name}' is not loaded");

        lock (script.ExportCallables)
        {
            if (script.ExportCallables.TryGetValue(name, out var existing) && !existing.IsDisposed)
                return existing;

            if (!script.ExportNames.Contains(name, StringComparer.Ordinal) || script.Namespace is null
                || script.Namespace.GetProperty(name) is not ScriptObject { } function
                || function is not IJavaScriptObject { Kind: JavaScriptObjectKind.Function })
            {
                throw new KeyNotFoundException($"module '{module.Name}' has no exported function '{name}'");
            }

            var callable = RegisterCallable(script, function, name);
            script.ExportCallables[name] = callable;
            return callable;
        }
    }

    private ScriptCallable RegisterCallable(LoadedScript script, ScriptObject function, string? name)
    {
        var token = Interlocked.Increment(ref _nextCallableToken);
        var callable = ScriptCallable.Create(Id, token, name, ReleaseCallable);
        script.Callables[token] = callable;
        _callables[token] = new CallableEntry(script, function);
        return callable;
    }

    private void ReleaseCallable(ScriptCallable callable)
    {
        if (!_callables.TryRemove(callable.Token, out var entry))
            return;

        entry.Owner.Callables.TryRemove(callable.Token, out _);
        try
        {
            entry.Function.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The engine is already gone; the function died with it.
        }
    }

    // ------------------------------------------------------------------ Invoke

    /// <inheritdoc />
    public ScriptValue Invoke(ScriptCallable fn, ReadOnlySpan<ScriptValue> args)
    {
        ArgumentNullException.ThrowIfNull(fn);
        if (Volatile.Read(ref _disposed) != 0)
            throw new ScriptRuntimeException("the V8 runtime was disposed");
        if (!string.Equals(fn.RuntimeId, Id, StringComparison.Ordinal))
            throw new ScriptRuntimeException($"the callable belongs to runtime '{fn.RuntimeId}', not '{Id}'");
        if (fn.IsDisposed || !_callables.TryGetValue(fn.Token, out var entry) || entry.Owner.IsDisposed)
            throw new ScriptRuntimeException($"unknown or disposed callable {fn}");

        var script = entry.Owner;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // The scheduler already attributes the call to the script; doing it here too keeps host calls attributed
            // correctly for nested invocations (a sync hook running while another script's host call is in progress).
            using var hostScope = _host!.EnterScript(script.Name);
            using (Enter(script, out var reentrant))
            {
                if (!reentrant)
                    script.Engine.CancelInterrupt();

                var jsArgs = new object?[args.Length];
                for (var i = 0; i < jsArgs.Length; i++)
                    jsArgs[i] = ToJsArgument(script, args[i]);

                // A function that returns a Promise (an async function) comes back as undefined: its settlement is observed
                // in JavaScript and a rejection reaches ReportError later (see ScheduleRejectionReports).
                var result = script.Values!.Invoke(entry.Function, jsArgs);
                return script.Values.ToScriptValue(result);
            }
        }
        catch (Exception ex)
        {
            throw MapError(ex, script);
        }
        finally
        {
            Volatile.Write(ref _lastCallMs, stopwatch.Elapsed.TotalMilliseconds);
            Interlocked.Increment(ref _callsTotal);
            ScheduleRejectionReports();
        }
    }

    private static object? ToJsArgument(LoadedScript script, ScriptValue value)
    {
        var values = script.Values!;
        var js = values.ToJs(value);
        // The scheduler passes context maps ({"$ctx": true, ...}); the prelude turns them into ctx objects.
        if (value.Kind == ScriptValueKind.Map && value.TryGet("$ctx", out var marker)
            && marker.Kind == ScriptValueKind.Bool && marker.AsBool())
            return values.MakeContext(js);

        return js;
    }

    private void ReportAsyncFailure(LoadedScript script, Exception exception)
    {
        if (_host is not { } host || script.IsDisposed || Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            using var scope = host.EnterScript(script.Name);
            host.ReportError(MapError(exception, script));
        }
        catch (Exception ex)
        {
            host.Log(3, $"v8: failed to report an error of script '{script.Name}': {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ Advance

    /// <inheritdoc />
    public bool Advance(TimeSpan budget)
    {
        if (Volatile.Read(ref _disposed) != 0 || _host is null)
            return false;

        var stopwatch = Stopwatch.StartNew();
        while (_queue.TryDequeue(out var item))
        {
            RunItem(item);
            if (stopwatch.Elapsed >= budget)
                break;
        }

        return !_queue.IsEmpty;
    }

    private void RunItem(WorkItem item)
    {
        if (item.Context.IsDisposed || item.Context.Tag is not LoadedScript { IsDisposed: false } script)
            return;

        var host = _host!;
        try
        {
            using var hostScope = host.EnterScript(script.Name);
            using (Enter(script, out var reentrant))
            {
                if (!reentrant)
                    script.Engine.CancelInterrupt();

                item.Callback(item.State);
            }
        }
        catch (Exception ex)
        {
            ReportAsyncFailure(script, ex);
        }
        finally
        {
            ScheduleRejectionReports();
        }
    }

    // ------------------------------------------------------------------ promise rejections

    /// <summary>
    ///     V8's promise-rejection notification (the game thread, inside script execution): remember a rejection that has no
    ///     handler yet, forget it when a handler is attached later. Nothing is reported from here - the host must never be
    ///     entered (and an engine never unloaded) in the middle of a script call.
    /// </summary>
    private void OnPromiseRejection(LoadedScript script, V8PromiseRejectionEventKind kind, ScriptObject promise, object? reason)
    {
        var keep = false;
        try
        {
            if (script.IsDisposed)
                return;

            switch (kind)
            {
                case V8PromiseRejectionEventKind.RejectedWithoutHandler:
                    script.Rejections.Add(new PendingRejection(promise, reason, FromInvoke: false));
                    MarkRejectionOwner(script);
                    keep = true;
                    break;
                case V8PromiseRejectionEventKind.HandlerAddedAfterRejection:
                    script.Rejections.Handled(promise);
                    break;
            }
        }
        catch (Exception ex)
        {
            // An exception here would unwind into V8; losing one notification is the lesser evil.
            _host?.Log(1, $"v8: promise rejection tracking failed for script '{script.Name}': {ex.Message}");
        }
        finally
        {
            if (!keep)
            {
                PendingRejection.DisposeQuietly(promise);
                PendingRejection.DisposeQuietly(reason);
            }
        }
    }

    /// <summary>The Promise an invoked function returned was rejected (reported by the JS helper that observes it).</summary>
    private void OnInvokedRejection(LoadedScript script, object? reason)
    {
        if (script.IsDisposed)
        {
            PendingRejection.DisposeQuietly(reason);
            return;
        }

        script.Rejections.Add(new PendingRejection(null, reason, FromInvoke: true));
        MarkRejectionOwner(script);
    }

    private void MarkRejectionOwner(LoadedScript script)
    {
        lock (_rejectionGate)
            _rejectionOwners.Add(script);
    }

    /// <summary>
    ///     Called when an engine entry ends. If it was the outermost one every microtask has run, so the rejections still
    ///     without a handler are final: they are queued as work items and reported by <see cref="Advance"/>, like any other
    ///     asynchronous script error (attributed to the script, outside the call that caused them).
    /// </summary>
    private void ScheduleRejectionReports()
    {
        lock (_stackGate)
        {
            if (_engineStack.Count > 0)
                return;
        }

        LoadedScript[] owners;
        lock (_rejectionGate)
        {
            if (_rejectionOwners.Count == 0)
                return;

            owners = [.. _rejectionOwners];
            _rejectionOwners.Clear();
        }

        foreach (var owner in owners)
        {
            var batch = owner.Rejections.Take();
            if (batch.Length == 0)
                continue;

            if (owner.IsDisposed)
            {
                foreach (var rejection in batch)
                    rejection.Release();
                continue;
            }

            owner.Context.Post(static state => ((RejectionReport)state!).Run(), new RejectionReport(this, owner, batch));
        }
    }

    /// <summary>The state of a queued report: runs <see cref="ReportRejections"/> on the game thread.</summary>
    private sealed record RejectionReport(V8ScriptRuntime Runtime, LoadedScript Script, PendingRejection[] Batch)
    {
        public void Run() => Runtime.ReportRejections(Script, Batch);
    }

    private void ReportRejections(LoadedScript script, PendingRejection[] batch)
    {
        foreach (var rejection in batch)
        {
            try
            {
                if (script.IsDisposed || script.Values is not { } values)
                    continue;

                var (text, stack) = values.Describe(rejection.Reason);
                var mapped = StackTraceMapper.Map(stack, script.Loader);
                var message = rejection.FromInvoke ? text : "Unhandled promise rejection: " + text;
                ReportAsyncFailure(script, new ScriptRuntimeException(message, mapped.Traceback, mapped.File, mapped.Line));
            }
            finally
            {
                rejection.Release();
            }
        }
    }

    // ------------------------------------------------------------------ Unload / Interrupt / Dispose

    /// <inheritdoc />
    public void Unload(ScriptModule module)
    {
        if (module is null || !string.Equals(module.RuntimeId, Id, StringComparison.Ordinal))
            return;

        if (_scripts.TryRemove(module.Token, out var script))
            DisposeScript(script);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Interrupts the engine that is executing right now (the top of the engine stack), whichever isolate it lives in. When
    ///     a script called into another script (a nested <see cref="Invoke"/>) that is the inner one: its call ends with
    ///     "interrupted", which the outer script sees as a failed host call; the outer script's own isolate is untouched
    ///     (the watchdog arms nested calls separately and interrupts the innermost expired deadline).
    /// </remarks>
    public void Interrupt()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var script = _currentEngine;
        if (script is null)
            return;

        try
        {
            script.Engine.Interrupt();
        }
        catch (ObjectDisposedException)
        {
            // Unloaded between the check and the call: nothing left to interrupt.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var script in _scripts.Values.ToArray())
            DisposeScript(script);

        _scripts.Clear();
        _callables.Clear();
        _queue.Clear();
        _compiler?.Dispose();
        _debugRuntime?.Dispose();
    }

    private void DisposeScript(LoadedScript script)
    {
        if (!script.MarkDisposed())
            return;

        script.Context.Dispose();
        script.Rejections.Clear();
        lock (_rejectionGate)
            _rejectionOwners.Remove(script);
        foreach (var callable in script.Callables.Values.ToArray())
        {
            try
            {
                callable.Dispose();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // Already released with the engine.
            }
        }

        script.Callables.Clear();
        script.ExportCallables.Clear();
        TryDispose(script.Namespace);
        TryDispose(script.Values);
        TryDispose(script.Engine);
        // The engine goes first, then the isolate it lived in (a debug-mode script only borrows the shared one).
        if (script.OwnsRuntime)
            TryDispose(script.Runtime);
        _scripts.TryRemove(script.Token, out _);
    }

    private static void TryDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Engine teardown already released it.
        }
    }

    // ------------------------------------------------------------------ engine entry

    private EngineEntry Enter(LoadedScript script) => Enter(script, out _);

    /// <summary>Makes <paramref name="script"/> the current engine.</summary>
    /// <param name="script">The script being entered.</param>
    /// <param name="reentrant">
    ///     Whether an engine of the SAME isolate is already running further up the stack (a nested call inside a shared
    ///     isolate): only then is a pending interrupt still meant for that outer call and must be kept. A script in an isolate of
    ///     its own starts clean even when nested - a stale request that missed its target must not poison the call.
    /// </param>
    private EngineEntry Enter(LoadedScript script, out bool reentrant)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(script.Context);
        lock (_stackGate)
        {
            reentrant = false;
            foreach (var active in _engineStack)
            {
                if (ReferenceEquals(active.Runtime, script.Runtime))
                {
                    reentrant = true;
                    break;
                }
            }

            _engineStack.Push(script);
            _currentEngine = script;
        }

        return new EngineEntry(this, previous);
    }

    private void Leave(SynchronizationContext? previous)
    {
        lock (_stackGate)
        {
            _engineStack.Pop();
            _currentEngine = _engineStack.Count > 0 ? _engineStack.Peek() : null;
        }

        SynchronizationContext.SetSynchronizationContext(previous);
    }

    /// <summary>Restores the previous engine and synchronization context when an engine entry ends.</summary>
    private readonly struct EngineEntry(V8ScriptRuntime runtime, SynchronizationContext? previous) : IDisposable
    {
        public void Dispose() => runtime.Leave(previous);
    }

    // ------------------------------------------------------------------ errors

    /// <summary>Converts any exception raised by (or while running) script code into the single exception type runtimes expose.</summary>
    /// <param name="exception">The exception.</param>
    /// <param name="script">The script it was raised for, used to map stack frames back to TypeScript.</param>
    /// <returns>The runtime exception.</returns>
    internal ScriptRuntimeException MapError(Exception exception, LoadedScript? script)
    {
        var ex = Unwrap(exception);
        switch (ex)
        {
            case ScriptRuntimeException runtimeException:
                return runtimeException;
            case ScriptInterruptedException:
                return new ScriptRuntimeException("interrupted", "", null, null, ex);
            case ScriptEngineException engineException when IsHeapViolation(engineException):
                return new ScriptRuntimeException(HeapMessage(script), "", null, null, engineException);
            case ScriptEngineException engineException:
            {
                var mapped = MapStack(engineException, script);
                return new ScriptRuntimeException(engineException.Message, mapped.Traceback, mapped.File, mapped.Line, engineException);
            }

            case ObjectDisposedException:
                return new ScriptRuntimeException("the script was unloaded", "", null, null, ex);
            case OperationCanceledException:
                return new ScriptRuntimeException("cancelled", "", null, null, ex);
            default:
                return new ScriptRuntimeException(ex.Message, ex.StackTrace ?? "", null, null, ex);
        }
    }

    private static MappedStack MapStack(ScriptEngineException exception, LoadedScript? script)
    {
        var loader = script?.Loader;

        // For a rejected promise ClearScript's ErrorDetails shows its internal rethrow site; the JS error's own stack
        // (including async frames) is the useful one. Fall back to ErrorDetails when it has no frame inside a served file.
        var jsStack = TryGetJsStack(exception);
        if (jsStack is not null)
        {
            var fromStack = StackTraceMapper.Map(jsStack, loader);
            if (fromStack.File is not null)
                return fromStack;
        }

        var fromDetails = StackTraceMapper.Map(exception.ErrorDetails, loader);
        return fromDetails.File is not null || jsStack is null ? fromDetails : StackTraceMapper.Map(jsStack, loader);
    }

    private static string? TryGetJsStack(ScriptEngineException exception)
    {
        try
        {
            return exception.ScriptExceptionAsObject is ScriptObject error && error.GetProperty("stack") is string { Length: > 0 } stack
                ? stack
                : null;
        }
        catch (Exception ex) when (ex is ScriptEngineException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsHeapViolation(ScriptEngineException exception)
        => exception.Message.Contains(HeapViolationText, StringComparison.Ordinal);

    /// <summary>Builds the heap-violation message and re-arms the limit of the isolate that violated it (only that one).</summary>
    /// <param name="script">The script whose isolate hit its limit (<c>null</c> falls back to the shared debuggable isolate, if any).</param>
    private string HeapMessage(LoadedScript? script)
    {
        long limit = _options?.HeapLimitBytes ?? 0;
        long used = limit;
        var runtime = script?.Runtime ?? _debugRuntime;
        if (runtime is not null)
        {
            try
            {
                used = (long)runtime.GetHeapInfo().UsedHeapSize;
                // The violation blocks further script execution in that isolate until the limit is assigned again.
                if (limit > 0)
                    runtime.MaxHeapSize = (UIntPtr)(ulong)limit;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // Runtime gone: report what we know.
            }
        }

        // The monitor fired because usage crossed the limit; a GC that ran since may already have lowered it.
        return $"heap limit exceeded ({Math.Max(used, limit + 1)} > {limit} bytes)";
    }

    private static Exception Unwrap(Exception exception)
    {
        while (true)
        {
            switch (exception)
            {
                case AggregateException { InnerExceptions.Count: 1 } aggregate:
                    exception = aggregate.InnerExceptions[0];
                    continue;
                case TargetInvocationException { InnerException: { } inner }:
                    exception = inner;
                    continue;
            }

            // A script error that wraps a host exception thrown by a nested call (e.g. a nested Invoke that was interrupted).
            for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is ScriptInterruptedException or ScriptRuntimeException)
                    return inner;
            }

            return exception;
        }
    }

    // ------------------------------------------------------------------ helpers

    private LoadedScript? FindScript(ScriptModule module)
        => string.Equals(module.RuntimeId, Id, StringComparison.Ordinal) && _scripts.TryGetValue(module.Token, out var script) ? script : null;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
