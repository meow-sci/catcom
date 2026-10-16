using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KsaScripting.Core.Api;
using KsaScripting.Core.Catalog;
using KsaScripting.Core.Config;
using KsaScripting.Core.Hooks;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Persistence;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Scheduling;
using KsaScripting.Core.Terminals;
using KsaScripting.GameMod.Commands;
using KsaScripting.GameMod.Editor;
using KsaScripting.GameMod.Persistence;
using KsaScripting.GameMod.Shells;
using KsaScripting.GameMod.Terminals;
using KsaScripting.Ksa;
using KsaScripting.Ksa.Bridge;
using KsaScripting.Ksa.Commands;
using KsaScripting.Ksa.Events;
using KsaScripting.Ksa.Slots;

namespace KsaScripting.GameMod;

/// <summary>Availability of one script runtime as discovered at start-up (Runtimes tab).</summary>
public sealed record RuntimeStatus(string Id, string Language, bool Available, string? Reason);

/// <summary>
///     The composition root: every long-lived object of the running mod, built by <see cref="Start"/>
///     in <c>[StarMapAllModsLoaded]</c> and torn down in reverse by <see cref="Dispose"/>. All members are
///     game-thread only. UI code reads state from here and posts every mutating action through
///     <see cref="Post"/> so it runs in the next Step slot (solver idle).
/// </summary>
public sealed partial class ScriptingHost : IDisposable
{
    private const string HashDeleted = "deleted";

    // "<file>:<line>:<col>: <message>" — the catalog's LastError after a failed load (lazy file part: the
    // path may contain a drive colon).
    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+):(?<col>\d+): (?<message>.*)$", RegexOptions.Singleline)]
    private static partial Regex LoadErrorPattern();

    private readonly Dictionary<string, string> _fileHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptState> _previousStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _placedTerminals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shownLoadErrors = new(StringComparer.Ordinal);

    private Action<string>? _saveWritten;
    private Action<string?>? _gameLoaded;
    private Action<ScriptInfo>? _catalogChanged;
    private Action<ScriptTerminal>? _lastDetached;
    private Action<ScriptEvent>? _eventPublished;
    private bool _commandsRegistered;
    private bool _disposed;

    private ScriptingHost()
    {
    }

    /// <summary>The running host, or null before start / after unload.</summary>
    public static ScriptingHost? Current { get; private set; }

    /// <summary>File-system layout.</summary>
    public ModPaths Paths { get; private set; } = null!;

    /// <summary>The loaded <c>ksa-scripting.toml</c> (edited by the Settings tab).</summary>
    public ScriptingConfig Config { get; private set; } = null!;

    /// <summary>The off-thread → game-thread queue (drained at the start of every Step slot).</summary>
    public GameThreadQueue Queue { get; private set; } = null!;

    /// <summary>The registered runtimes (available or not).</summary>
    public RuntimeSet Runtimes { get; private set; } = null!;

    /// <summary>Per-runtime availability as discovered at start-up.</summary>
    public IReadOnlyList<RuntimeStatus> RuntimeStatuses { get; private set; } = [];

    /// <summary>Hook registry (code-declared and UI bindings).</summary>
    public HookRegistry Hooks { get; private set; } = null!;

    /// <summary>Event bus fed by the Harmony patches, the differ and terminals.</summary>
    public EventBus Events { get; private set; } = null!;

    /// <summary>Per-call watchdog.</summary>
    public Watchdog Watchdog { get; private set; } = null!;

    /// <summary>The Step-slot scheduler.</summary>
    public Scheduler Scheduler { get; private set; } = null!;

    /// <summary>Script virtual terminals.</summary>
    public TerminalRegistry Terminals { get; private set; } = null!;

    /// <summary>Per-script persisted state (save sidecar).</summary>
    public ScriptStateStore State { get; private set; } = null!;

    /// <summary><c>bindings.toml</c>.</summary>
    public BindingStore Bindings { get; private set; } = null!;

    /// <summary>KSA accessor health latches.</summary>
    public KsaHealth Health { get; private set; } = null!;

    /// <summary>The gatOS-vocabulary action executor.</summary>
    public ActionExecutor Executor { get; private set; } = null!;

    /// <summary>The game bridge the facade reads/writes through.</summary>
    public KsaBridge Bridge { get; private set; } = null!;

    /// <summary>The host facade bound by every runtime.</summary>
    public HostFacade Facade { get; private set; } = null!;

    /// <summary>The script catalog (scan/load/run/start/stop).</summary>
    public ScriptCatalog Catalog { get; private set; } = null!;

    /// <summary>The Step/Solver slot patches.</summary>
    public SlotHost Slots { get; private set; } = null!;

    /// <summary>purrTTY surface placement for script terminals (terminals.toml).</summary>
    public TerminalPlacer Placer { get; private set; } = null!;

    /// <summary>The shipped-Neovim editor launcher.</summary>
    public EditorLauncher Editor { get; private set; } = null!;

    /// <summary>The scripts-folder file watcher, or null when it could not be created (hot reload then relies on the editor and the UI).</summary>
    public ScriptWatcher? Watcher { get; private set; }

    /// <summary>
    ///     Builds and starts everything (guide §7 order). Never throws for a partially failing
    ///     subsystem (it logs and degrades); throws only when the core cannot be composed at all, after
    ///     tearing down whatever was already built.
    /// </summary>
    /// <param name="paths">The mod's folders.</param>
    /// <returns>The running host.</returns>
    public static ScriptingHost Start(ModPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var host = new ScriptingHost();
        try
        {
            host.Initialize(paths);
        }
        catch (Exception startFailure)
        {
            // Report while the log sink is still installed: Dispose resets it.
            Log.Error("ksa-scripting could not be started; tearing down the parts that were built.", startFailure);
            try
            {
                host.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Cleaning up after a failed start also failed: {ex.Message}");
            }

            throw;
        }

        return host;
    }

    /// <summary>Queues <paramref name="work"/> for the next Step slot (game thread, solver idle).</summary>
    /// <param name="work">The work to run.</param>
    public void Post(Action work) => Queue.PostWork(work);

    /// <summary>Saves <see cref="Config"/> to <see cref="ModPaths.ConfigFile"/> (atomic).</summary>
    public void SaveConfig() => Config.Save(Paths.ConfigFile);

    /// <summary>
    ///     Game thread: handles a changed script file from the watcher or an nvim save — skips it when
    ///     the content hash equals the last one processed for that path, else forwards to
    ///     <see cref="ScriptCatalog.OnFileChanged"/>.
    /// </summary>
    /// <param name="fullPath">The changed file.</param>
    public void OnScriptFileChanged(string fullPath)
    {
        try
        {
            if (_disposed || string.IsNullOrEmpty(fullPath))
                return;

            var path = Path.GetFullPath(fullPath);
            string? hash;
            try
            {
                hash = HashOf(path);
            }
            catch (Exception ex)
            {
                // Locked by the editor mid-save: forward without remembering a hash, the next event retries.
                Log.LogOnce("host.hash:" + path, LogLevel.Debug, $"Could not hash '{path}': {ex.Message}");
                hash = null;
            }

            if (hash is not null)
            {
                if (_fileHashes.TryGetValue(path, out var last) && string.Equals(last, hash, StringComparison.Ordinal))
                    return;

                _fileHashes[path] = hash;
            }

            Catalog.OnFileChanged(path);
        }
        catch (Exception ex)
        {
            Log.LogOnce("host.filechanged", LogLevel.Warn, $"Handling a changed script file failed: {ex.Message}");
        }
    }

    /// <summary>Reverse teardown (guide §7 <c>[StarMapUnload]</c>). Safe on a partially started host and idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        Safe("stop the file watcher", () => Watcher?.Dispose());

        // Quiet the catalog subscribers first: the teardown below changes every script's state.
        Safe("unsubscribe the catalog", () =>
        {
            if (_catalogChanged is not null && Catalog is not null)
                Catalog.Changed -= _catalogChanged;
            _catalogChanged = null;
        });

        Safe("stop the scripts", StopAllScripts);
        Safe("close the editor", () => Editor?.Dispose());
        Safe("close the terminals", () => Placer?.Dispose());

        Safe("unsubscribe the terminal events", () =>
        {
            if (_lastDetached is not null && Terminals is not null)
                Terminals.LastDetached -= _lastDetached;
            _lastDetached = null;
            if (_eventPublished is not null && Events is not null)
                Events.Published -= _eventPublished;
            _eventPublished = null;
        });

        if (_commandsRegistered)
            Safe("unregister the console command", ScriptCommands.Unregister);

        Safe("remove the event patches", EventPatches.Uninstall);
        Safe("remove the slot patches", () => Slots?.Uninstall());
        Safe("tear down the actuators", ActionExecutor.Teardown);

        Safe("unsubscribe the save hooks", () =>
        {
            if (_saveWritten is not null)
                KsaSaveHooks.SaveWritten -= _saveWritten;
            if (_gameLoaded is not null)
                KsaSaveHooks.GameLoaded -= _gameLoaded;
            _saveWritten = null;
            _gameLoaded = null;
        });

        Safe("dispose the runtimes", () =>
        {
            if (Runtimes is null)
                return;

            foreach (var runtime in Runtimes.All)
                Safe($"dispose the '{runtime.Id}' runtime", runtime.Dispose);
        });

        Safe("stop the watchdog", () => Watchdog?.Dispose());

        Log.Info("ksa-scripting stopped.");
        Log.ResetSink();
        if (ReferenceEquals(Current, this))
            Current = null;
    }

    // ---------------------------------------------------------------- composition

    private void Initialize(ModPaths paths)
    {
        // a. folders, config, log sink (sink first so everything below can report)
        Paths = paths;
        Log.SetSink(new ModLogSink());

        foreach (var failure in paths.EnsureDataDirs())
            Log.Warn($"Could not create a data folder: {failure}");
        Config = ScriptingConfig.LoadOrCreate(paths.ConfigFile);

        // b. shipped helper libraries and editor stubs
        Safe("copy the shipped script libraries", () => CopyShippedLibraries(paths));
        Safe("write the editor stubs", () => StubWriter.WriteAll(paths.ScriptsDir));

        // c. the off-thread → game-thread queue
        Queue = new GameThreadQueue(controlEnabled: true, debugEnabled: Config.DebugNamespace, GameThreadQueue.DefaultTimeout);

        // d. runtimes (by reflection; a missing one degrades its language)
        var (runtimes, loaderStatuses) = RuntimeLoader.Load(Config, paths);
        Runtimes = new RuntimeSet(runtimes);

        // e. hooks, events, watchdog, scheduler
        Hooks = new HookRegistry();
        Events = new EventBus();
        Watchdog = new Watchdog();
        HostFacade? facade = null;
        Scheduler = new Scheduler(
            ScheduleConfig.FromConfig(Config),
            Hooks,
            Events,
            Runtimes,
            Watchdog,
            (script, ex) =>
            {
                if (facade is not null)
                    facade.RecordError(script, ex); // logs and fills the script's output ring
                else
                    Log.Warn($"[{(script.Length == 0 ? "host" : script)}] error: {ex.Message}");
            });

        // f. terminals, state, bindings
        Terminals = new TerminalRegistry(Config.TerminalOutputBufferKib, Events);
        State = new ScriptStateStore();
        Bindings = new BindingStore(paths.BindingsFile);

        // g. the game side
        Health = new KsaHealth();
        Executor = new ActionExecutor(Health, Config.ControlAllVessels, Config.DebugNamespace);
        var cache = new VesselCache();
        Bridge = new KsaBridge(cache, Executor, Queue);

        // h. the facade every runtime binds
        Facade = facade = new HostFacade(Bridge, Scheduler, Hooks, Terminals, State, HostFacadeOptions.FromConfig(Config));

        // i. bind the runtimes to the facade
        RuntimeStatuses = InitializeRuntimes(loaderStatuses);

        // j. the Step / Solver slots and the Harmony event patches
        Slots = new SlotHost(new SchedulerStepSink(Scheduler), new EventBusSink(Events), Queue, Executor, cache, Bridge);
        Safe("install the slot patches", Slots.Install);
        Safe("install the event patches", () => EventPatches.Install(new EventBusSink(Events)));

        // k. the per-save state sidecar
        _saveWritten = dir => StateSidecar.Save(dir, State);
        _gameLoaded = dir => StateSidecar.Load(dir, State);
        KsaSaveHooks.SaveWritten += _saveWritten;
        KsaSaveHooks.GameLoaded += _gameLoaded;

        // l. console command and purrTTY shells
        Safe("register the console command", () =>
        {
            ScriptCommands.Register(this);
            _commandsRegistered = true;
        });
        Safe("register the purrTTY shells", () => ShellRegistrar.Register(this));

        // m. terminal surfaces
        Placer = new TerminalPlacer(paths, Config, Terminals, Queue)
        {
            VesselIds = () => Bridge.List("vessels"),
        };

        // n. the editor
        Editor = new EditorLauncher(paths, Config, Queue, path => Queue.PostWork(() => OnScriptFileChanged(path)));

        // o. the catalog and what reacts to its changes
        Catalog = new ScriptCatalog(paths.ScriptsDir, Runtimes, Hooks, Bindings, Scheduler, Facade) { Terminals = Terminals };
        _catalogChanged = OnCatalogChanged;
        Catalog.Changed += _catalogChanged;

        _lastDetached = OnLastDetached;
        Terminals.LastDetached += _lastDetached;

        // Vessels or a save appearing may make an in-world placement possible.
        _eventPublished = OnEventPublished;
        Events.Published += _eventPublished;

        // p. hot reload from the scripts folder
        try
        {
            Watcher = new ScriptWatcher(paths.ScriptsDir, path => Queue.PostWork(() => OnScriptFileChanged(path)));
        }
        catch (Exception ex)
        {
            Watcher = null;
            Log.Warn($"The scripts folder cannot be watched ({ex.Message}); edits are picked up through the editor and the Script Manager only.");
        }

        // q. scan and autostart on the first Step slot
        Queue.PostWork(RunAutostart);

        // r. running
        Current = this;
        var available = RuntimeStatuses.Where(s => s.Available).Select(s => s.Id).ToArray();
        Log.Info($"ksa-scripting started: {available.Length} runtimes available ({string.Join(", ", available)}), scripts dir {paths.ScriptsDir}");
    }

    private IReadOnlyList<RuntimeStatus> InitializeRuntimes(List<RuntimeStatus> loaderStatuses)
    {
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var runtime in Runtimes.All)
        {
            try
            {
                runtime.Initialize(Facade, OptionsFor(runtime));
            }
            catch (Exception ex)
            {
                failures[runtime.Id] = $"runtime failed to initialize: {ex.Message}";
                Log.Warn($"Runtime '{runtime.Id}' failed to initialize: {ex}");
                Safe($"dispose the failed '{runtime.Id}' runtime", runtime.Dispose);
            }
        }

        var final = new List<RuntimeStatus>(loaderStatuses.Count);
        foreach (var status in loaderStatuses)
        {
            var runtime = Runtimes.ForId(status.Id);
            if (runtime is null)
            {
                final.Add(status);
                continue;
            }

            if (failures.TryGetValue(runtime.Id, out var failure))
            {
                final.Add(new RuntimeStatus(status.Id, status.Language, false, failure));
                continue;
            }

            try
            {
                var available = runtime.IsAvailable;
                var reason = available ? null : runtime.UnavailableReason ?? status.Reason ?? "runtime not available";
                if (!available && status.Available)
                    Log.Warn($"Runtime '{runtime.Id}' is not available: {reason}");
                final.Add(new RuntimeStatus(status.Id, status.Language, available, reason));
            }
            catch (Exception ex)
            {
                final.Add(new RuntimeStatus(status.Id, status.Language, false, "availability probe failed: " + ex.Message));
            }
        }

        return final;
    }

    private RuntimeOptions OptionsFor(IScriptRuntime runtime) => runtime.Id switch
    {
        "lua" => new RuntimeOptions(Paths.ScriptsDir, Paths.StubsDir, Config.LuaMemoryLimitKb * 1024L, Config.LuaJitEnabled, null),
        "v8" => new RuntimeOptions(
            Paths.ScriptsDir,
            Paths.StubsDir,
            Config.V8HeapLimitMb * 1024L * 1024L,
            true,
            Config.V8DebugPort > 0 ? Config.V8DebugPort : null),
        _ => new RuntimeOptions(Paths.ScriptsDir, Paths.StubsDir, 64L * 1024 * 1024, true, null),
    };

    // ---------------------------------------------------------------- catalog reactions (game thread)

    private void OnCatalogChanged(ScriptInfo info)
    {
        try
        {
            var name = info.Name;
            _previousStates.TryGetValue(name, out var previous);
            _previousStates[name] = info.State;

            UpdateTerminalSurfaces(info, previous);
            UpdateEditorDiagnostics(info);
        }
        catch (Exception ex)
        {
            Log.LogOnce("host.catalogchanged", LogLevel.Warn, $"Reacting to a script change failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Re-creates a script's persisted terminal surfaces when it becomes a service and closes them when it
    ///     stops being one. A service reload passes through <c>Running</c> (the terminal and its surfaces live on),
    ///     so that is not a stop; a restart that made a NEW terminal (stop + start) re-creates the surfaces.
    /// </summary>
    private void UpdateTerminalSurfaces(ScriptInfo info, ScriptState previous)
    {
        var name = info.Name;
        if (info.State == ScriptState.Service)
        {
            var terminalId = Terminals.ForScript(name)?.Id ?? 0;
            if (_placedTerminals.TryGetValue(name, out var placed))
            {
                if (placed == terminalId)
                    return;

                if (placed == 0)
                {
                    // The terminal was created after the service started (a surface opened for a script
                    // without terminal=true): the same service, nothing to re-create.
                    _placedTerminals[name] = terminalId;
                    return;
                }

                Placer.OnScriptStopped(name); // a different terminal: the service was stopped and started again
            }

            _placedTerminals[name] = terminalId;
            Placer.OnScriptStarted(name);
            return;
        }

        if (previous == ScriptState.Service && info.State == ScriptState.Running)
            return; // a reload of a service in progress

        if (_placedTerminals.Remove(name))
            Placer.OnScriptStopped(name);
    }

    /// <summary>Shows a failed load as an editor diagnostic, clears it once the script loads again.</summary>
    private void UpdateEditorDiagnostics(ScriptInfo info)
    {
        var name = info.Name;
        var match = info.LastError is null ? null : LoadErrorPattern().Match(info.LastError);
        if (match is { Success: true })
        {
            if (_shownLoadErrors.TryGetValue(name, out var shown) && string.Equals(shown, info.LastError, StringComparison.Ordinal))
                return;

            _shownLoadErrors[name] = info.LastError!;
            Editor.ShowLoadError(
                info.Path,
                int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture),
                match.Groups["message"].Value);
            return;
        }

        if (_shownLoadErrors.Remove(name))
            Editor.ClearDiagnostics(info.Path);
    }

    private void OnLastDetached(ScriptTerminal terminal)
    {
        var script = terminal.Script;
        Queue.PostWork(() =>
        {
            try
            {
                // The script asked to stop when its last surface closes (ctx.term.policy).
                var current = Terminals.ForScript(script);
                if (current is null || current.Attached > 0 || !current.StopWhenDetached)
                    return;

                Log.Info($"Stopping '{script}': its last terminal surface closed.");
                Catalog.Stop(script);
            }
            catch (Exception ex)
            {
                Log.LogOnce("host.lastdetached", LogLevel.Warn, $"Stopping '{script}' after its terminal closed failed: {ex.Message}");
            }
        });
    }

    private void OnEventPublished(ScriptEvent ev)
    {
        if (ev.Type is "vessel.spawn" or "vessel-appeared" or "save.load" or "system.load" or "control.change" or "active-changed")
            Placer.RequestReconcile();
    }

    private void RunAutostart()
    {
        try
        {
            if (_disposed)
                return;

            Catalog.Scan();
            var all = Catalog.All();
            var names = new List<string>();

            foreach (var configured in Config.Autostart)
            {
                var match = ScriptNameResolver.Resolve(configured, all, out _, out var problem);
                if (problem is not null)
                {
                    Log.Warn($"autostart: {problem}");
                    continue;
                }

                if (match is null)
                {
                    Log.Warn($"autostart: unknown script '{configured}' (no such file in {Paths.ScriptsDir})");
                    continue;
                }

                if (!names.Contains(match.Name, StringComparer.Ordinal))
                    names.Add(match.Name);
            }

            foreach (var info in all)
            {
                if (info.Autostart && !names.Contains(info.Name, StringComparer.Ordinal))
                    names.Add(info.Name);
            }

            foreach (var name in names)
                Catalog.Start(name);

            if (names.Count > 0)
                Log.Info($"Autostarted {names.Count} script(s): {string.Join(", ", names)}");
        }
        catch (Exception ex)
        {
            Log.LogOnce("host.autostart", LogLevel.Warn, $"Autostart failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- teardown helpers

    private void StopAllScripts()
    {
        if (Catalog is null)
            return;

        foreach (var info in Catalog.All())
        {
            if (info.State == ScriptState.Service)
                Catalog.Stop(info.Name);
        }

        foreach (var info in Catalog.All())
        {
            if (info.State != ScriptState.Idle)
                Catalog.Unload(info.Name);
        }
    }

    // ---------------------------------------------------------------- files

    private static void CopyShippedLibraries(ModPaths paths)
    {
        var source = paths.ShippedScriptsLibDir;
        var target = paths.ScriptsLibDir;
        if (!Directory.Exists(source))
        {
            Log.Info($"No shipped script libraries at '{source}'; '{target}' is left as it is.");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            try
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination) && SameContent(file, destination))
                    continue;

                File.Copy(file, destination, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not copy the shipped library '{file}': {ex.Message}");
            }
        }
    }

    private static bool SameContent(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        return infoA.Length == infoB.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    private static string HashOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return HashDeleted;
        }
    }

    private static void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not {what}: {ex.Message}");
        }
    }
}
