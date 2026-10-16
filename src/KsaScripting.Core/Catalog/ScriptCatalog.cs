using KsaScripting.Core.Hooks;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Scheduling;
using KsaScripting.Core.Terminals;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Catalog;

/// <summary>
///     The set of scripts under the scripts folder and their lifecycle: scan, load (compile and evaluate,
///     register code-declared hooks, apply stored UI bindings), run, start/stop as services, reload,
///     unload. Names are paths relative to the scripts folder with <c>/</c> separators
///     (<c>ascent.lua</c>, <c>dir/x.ts</c>). A failed reload keeps the previous module and its hooks alive.
///     Game-thread only; no method throws: problems land in <see cref="ScriptInfo.LastError"/> and the log.
/// </summary>
public sealed class ScriptCatalog
{
    private readonly string _scriptsDir;
    private readonly IRuntimeSet _runtimes;
    private readonly HookRegistry _hooks;
    private readonly BindingStore _bindings;
    private readonly Scheduler _scheduler;
    private readonly IHostFacade _host;
    private readonly SortedDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Creates the catalog and starts listening for scripts the scheduler disables.</summary>
    /// <param name="scriptsDir">The scripts folder (need not exist yet).</param>
    /// <param name="runtimes">The available runtimes.</param>
    /// <param name="hooks">The binding registry.</param>
    /// <param name="bindings">The stored UI bindings.</param>
    /// <param name="scheduler">The scheduler (paths, faults, waits, service start).</param>
    /// <param name="host">The host facade used to attribute load-time host calls to the script.</param>
    public ScriptCatalog(
        string scriptsDir,
        IRuntimeSet runtimes,
        HookRegistry hooks,
        BindingStore bindings,
        Scheduler scheduler,
        IHostFacade host)
    {
        ArgumentException.ThrowIfNullOrEmpty(scriptsDir);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(host);

        _scriptsDir = Path.GetFullPath(scriptsDir);
        _runtimes = runtimes;
        _hooks = hooks;
        _bindings = bindings;
        _scheduler = scheduler;
        _host = host;
        scheduler.ScriptDisabled += OnScriptDisabled;
    }

    /// <summary>Raised after a script's state or metadata changed (also when a script appeared or vanished).</summary>
    public event Action<ScriptInfo>? Changed;

    /// <summary>The scripts folder (full path).</summary>
    public string ScriptsDir => _scriptsDir;

    /// <summary>
    ///     The terminal registry. When set, <see cref="Start"/> of a script whose header says
    ///     <c>terminal=true</c> creates its terminal before <c>main</c> runs, and <see cref="Stop"/> ends it
    ///     with <c>[script exited]</c>.
    /// </summary>
    public TerminalRegistry? Terminals { get; set; }

    /// <summary>The current infos, ordered by name (ordinal).</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<ScriptInfo> All() => _entries.Values.Select(ToInfo).ToArray();

    /// <summary>The info of one script, or <see langword="null"/> when unknown.</summary>
    /// <param name="name">The script name.</param>
    /// <returns>The info, or <see langword="null"/>.</returns>
    public ScriptInfo? Get(string name)
        => _entries.TryGetValue(NormalizeName(name), out var entry) ? ToInfo(entry) : null;

    /// <summary>The catalog name of a file path: relative to the scripts folder, with <c>/</c> separators.</summary>
    /// <param name="fullPath">A file path.</param>
    /// <returns>The relative name (starts with <c>..</c> when the file is outside the scripts folder).</returns>
    public string NameForPath(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        return Path.GetRelativePath(_scriptsDir, Path.GetFullPath(fullPath)).Replace(Path.DirectorySeparatorChar, '/');
    }

    // ---------------------------------------------------------------- scan

    /// <summary>
    ///     Rescans the scripts folder (recursive <c>*.lua</c> and <c>*.ts</c>, excluding <c>*.d.ts</c>, the
    ///     top-level <c>lib/</c> and <c>stubs/</c> folders and hidden folders). Known scripts keep their
    ///     state, new ones are added as <see cref="ScriptState.Idle"/>, vanished ones are unloaded and dropped.
    /// </summary>
    /// <returns>The current infos, ordered by name.</returns>
    public IReadOnlyList<ScriptInfo> Scan()
    {
        try
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Directory.Exists(_scriptsDir))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var file in Directory.EnumerateFiles(_scriptsDir, "*", options))
                {
                    var name = NameForPath(file);
                    if (IsScriptName(name))
                        found[name] = file;
                }
            }

            foreach (var name in _entries.Keys.Where(n => !found.ContainsKey(n)).ToArray())
                Remove(_entries[name]);

            foreach (var (name, file) in found.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!_entries.TryGetValue(name, out var entry))
                {
                    entry = NewEntry(name, file);
                    _entries[name] = entry;
                    RaiseChanged(entry);
                }
                else if (entry.Module is null && RefreshHeader(entry))
                {
                    RaiseChanged(entry);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Scanning '{_scriptsDir}' failed: {ex.Message}");
        }

        return All();
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Reads, compiles and evaluates the script, registering its code-declared hooks and applying its stored bindings. A failure keeps the previous module alive.</summary>
    /// <param name="name">The script name.</param>
    /// <returns>The resulting info (state <see cref="ScriptState.Error"/> with <see cref="ScriptInfo.LastError"/> on failure).</returns>
    public ScriptInfo Load(string name)
    {
        var entry = ResolveEntry(name, out var transient);
        if (entry is null)
            return transient!;

        LoadCore(entry);
        return ToInfo(entry);
    }

    /// <summary>Loads the script from disk and, when it exports <c>main</c>, calls <c>main(ctx)</c> once.</summary>
    /// <param name="name">The script name.</param>
    public void Run(string name)
    {
        try
        {
            var entry = ResolveEntry(name, out _);
            if (entry is null || !LoadCore(entry))
                return;

            if (entry.Module is null || entry.Runtime is null || !entry.Exports.Contains("main"))
                return;

            var main = entry.Runtime.GetExport(entry.Module, "main");
            try
            {
                _scheduler.InvokeGuarded(entry.Name, main, [_scheduler.BuildContext("main", entry.Name)]);
            }
            finally
            {
                main.Dispose();
            }
        }
        catch (Exception ex)
        {
            Fail(name, ex);
        }
    }

    /// <summary>Loads the script and starts its <c>main(ctx)</c> as a service in the next Step slot (creating its terminal first when the header declares one).</summary>
    /// <param name="name">The script name.</param>
    public void Start(string name)
    {
        try
        {
            var entry = ResolveEntry(name, out _);
            if (entry is null || !LoadCore(entry))
                return;

            StartService(entry, createTerminal: true);
        }
        catch (Exception ex)
        {
            Fail(name, ex);
        }
    }

    /// <summary>Signals the script to cancel, delivers <c>signal.cancel</c>, unloads it (state <see cref="ScriptState.Idle"/>) and ends its terminal with <c>[script exited]</c>.</summary>
    /// <param name="name">The script name.</param>
    public void Stop(string name)
    {
        try
        {
            if (!_entries.TryGetValue(NormalizeName(name), out var entry))
                return;

            _scheduler.SetCancelled(entry.Name, true);
            _scheduler.DeliverNow(new ScriptEvent(0, "signal.cancel", null, TargetOf(entry.Name)));
            UnloadCore(entry);

            // The flag stays set until the script is loaded again, so a late reader still sees the stop.
            _scheduler.SetCancelled(entry.Name, true);
            DropTerminal(entry.Name, "[script exited]");
            RaiseChanged(entry);
        }
        catch (Exception ex)
        {
            Fail(name, ex);
        }
    }

    /// <summary>Reloads the script from disk; a running service restarts with the new version.</summary>
    /// <param name="name">The script name.</param>
    public void Reload(string name)
    {
        try
        {
            var entry = ResolveEntry(name, out _);
            if (entry is null)
                return;

            var wasService = entry.IsService;
            if (!LoadCore(entry))
                return;

            if (wasService)
                StartService(entry, createTerminal: false);
        }
        catch (Exception ex)
        {
            Fail(name, ex);
        }
    }

    /// <summary>Cancels the script's waits, removes its hooks, releases its module and returns it to <see cref="ScriptState.Idle"/>.</summary>
    /// <param name="name">The script name.</param>
    public void Unload(string name)
    {
        try
        {
            if (!_entries.TryGetValue(NormalizeName(name), out var entry))
                return;

            UnloadCore(entry);
            RaiseChanged(entry);
        }
        catch (Exception ex)
        {
            Fail(name, ex);
        }
    }

    /// <summary>
    ///     Re-applies the stored UI bindings of a loaded script (after the Script Manager saved
    ///     <c>bindings.toml</c>): the script's previous UI-created bindings are replaced; code-declared
    ///     bindings and a started service stay.
    /// </summary>
    /// <param name="name">The script name.</param>
    public void ApplyBindings(string name)
    {
        try
        {
            if (!_entries.TryGetValue(NormalizeName(name), out var entry) || entry.Module is null)
                return;

            _hooks.RemoveWhere(entry.Name, b => !b.FromCode && b.Id != entry.ServiceBindingId);
            var warning = ApplyStoredBindings(entry);
            if (warning is not null)
                entry.LastError = warning;
            else if (entry.LastError?.StartsWith(BindingWarningPrefix, StringComparison.Ordinal) == true)
                entry.LastError = null;
            RaiseChanged(entry);
        }
        catch (Exception ex)
        {
            Fail(name, ex);
        }
    }

    /// <summary>
    ///     Reacts to a script file that changed on disk (already marshalled to the game thread): a deleted
    ///     file unloads and drops the script, a new file is added as idle, a loaded script (or one that
    ///     failed) reloads, any other script only refreshes its header.
    /// </summary>
    /// <param name="path">The changed file.</param>
    public void OnFileChanged(string path)
    {
        try
        {
            var name = NameForPath(path);
            if (!IsScriptName(name))
                return;

            var fullPath = Path.GetFullPath(path);
            _entries.TryGetValue(name, out var entry);

            if (!File.Exists(fullPath))
            {
                if (entry is not null)
                    Remove(entry);
                return;
            }

            if (entry is null)
            {
                entry = NewEntry(name, fullPath);
                _entries[name] = entry;
                RaiseChanged(entry);
                return;
            }

            if (entry.Module is not null || entry.State == ScriptState.Error)
            {
                Reload(name);
                return;
            }

            RefreshHeader(entry);
            RaiseChanged(entry);
        }
        catch (Exception ex)
        {
            Log.Warn($"Handling a change of '{path}' failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- loading

    private const string BindingWarningPrefix = "binding:";

    private bool LoadCore(Entry entry)
    {
        var name = entry.Name;
        var previousState = entry.State;
        var oldBindingIds = _hooks.ForScript(name).Select(b => b.Id).ToHashSet();
        var oldWaitIds = _scheduler.PendingWaitIds(name).ToHashSet();
        entry.State = ScriptState.Loading;

        try
        {
            string code;
            try
            {
                code = File.ReadAllText(entry.FullPath);
            }
            catch (Exception ex)
            {
                return LoadFailed(entry, previousState, $"cannot read '{name}': {ex.Message}");
            }

            entry.Header = ScriptHeader.Parse(code);

            var runtime = _runtimes.ForPath(entry.FullPath);
            if (runtime is null || !runtime.IsAvailable)
            {
                var reason = runtime?.UnavailableReason ?? "no runtime registered";
                return LoadFailed(entry, previousState, $"runtime '{entry.Language}' not available: {reason}");
            }

            _scheduler.SetScriptPath(name, entry.FullPath);

            ScriptModule module;
            try
            {
                using (_host.EnterScript(name))
                {
                    module = runtime.Load(new ScriptSource(name, entry.FullPath, code, entry.Language));
                }
            }
            catch (Exception ex)
            {
                _hooks.RemoveWhere(name, b => !oldBindingIds.Contains(b.Id));
                var created = _scheduler.PendingWaitIds(name).Where(id => !oldWaitIds.Contains(id)).ToArray();
                if (created.Length > 0)
                    _scheduler.CancelWaits(name, created);

                var message = ex is ScriptLoadException load
                    ? $"{load.File}:{load.Line}:{load.Column}: {load.Message}"
                    : ex.Message;
                return LoadFailed(entry, previousState, message);
            }

            // Success: swap atomically. The old module's hooks and waits go, then the old module itself.
            _hooks.RemoveWhere(name, b => oldBindingIds.Contains(b.Id));
            if (oldWaitIds.Count > 0)
                _scheduler.CancelWaits(name, oldWaitIds);
            ReleaseModule(entry);

            entry.Module = module;
            entry.Runtime = runtime;
            entry.ServiceBindingId = null;
            entry.IsService = false;
            entry.Exports = SafeExports(runtime, module, name);

            _scheduler.ResetFaults(name);
            _scheduler.SetCancelled(name, false);

            entry.LastError = ApplyStoredBindings(entry);
            entry.State = ScriptState.Running;
            RaiseChanged(entry);
            return true;
        }
        catch (Exception ex)
        {
            return LoadFailed(entry, previousState, ex.Message);
        }
    }

    private bool LoadFailed(Entry entry, ScriptState previousState, string message)
    {
        entry.State = entry.Module is not null ? previousState : ScriptState.Error;
        if (entry.State == ScriptState.Loading)
            entry.State = ScriptState.Error;
        entry.LastError = message;
        Log.Warn($"Loading '{entry.Name}' failed: {message}");
        RaiseChanged(entry);
        return false;
    }

    private static IReadOnlyList<string> SafeExports(IScriptRuntime runtime, ScriptModule module, string name)
    {
        try
        {
            return runtime.Exports(module).ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing the exports of '{name}' failed: {ex.Message}");
            return [];
        }
    }

    private string? ApplyStoredBindings(Entry entry)
    {
        if (entry.Module is null || entry.Runtime is null)
            return null;

        var warnings = new List<string>();
        foreach (var record in _bindings.Load())
        {
            if (!string.Equals(record.Script, entry.Name, StringComparison.Ordinal) || !record.Enabled)
                continue;

            if (!BindingKinds.TryParse(record.Kind, out var kind))
            {
                warnings.Add($"{BindingWarningPrefix} unknown kind '{record.Kind}' for export '{record.Export}'");
                continue;
            }

            ScriptCallable fn;
            try
            {
                fn = entry.Runtime.GetExport(entry.Module, record.Export);
            }
            catch (KeyNotFoundException)
            {
                warnings.Add($"{BindingWarningPrefix} export '{record.Export}' not found");
                continue;
            }
            catch (Exception ex)
            {
                warnings.Add($"{BindingWarningPrefix} export '{record.Export}': {ex.Message}");
                continue;
            }

            _hooks.Add(
                entry.Name,
                kind,
                record.Key,
                fn,
                new BindingOptions(record.Sync, record.Vessel, record.Clock, record.IntervalSeconds),
                fromCode: false);
        }

        if (warnings.Count == 0)
            return null;

        var text = string.Join("; ", warnings);
        Log.Warn($"Applying the stored bindings of '{entry.Name}': {text}");
        return text;
    }

    private void StartService(Entry entry, bool createTerminal)
    {
        if (entry.Module is null || entry.Runtime is null || !entry.Exports.Contains("main"))
        {
            entry.LastError = "no main(ctx) export";
            entry.State = ScriptState.Error;
            RaiseChanged(entry);
            return;
        }

        if (createTerminal && entry.Header.Terminal)
            Terminals?.Create(entry.Name);

        _scheduler.SetCancelled(entry.Name, false);
        var main = entry.Runtime.GetExport(entry.Module, "main");
        entry.ServiceBindingId = _hooks.Add(entry.Name, BindingKind.Service, "main", main, new BindingOptions(), fromCode: false);
        entry.IsService = true;
        entry.State = ScriptState.Service;
        RaiseChanged(entry);
    }

    private void UnloadCore(Entry entry)
    {
        _scheduler.CancelWaits(entry.Name);
        _hooks.RemoveAll(entry.Name);
        ReleaseModule(entry);
        _scheduler.ForgetScript(entry.Name);

        entry.Module = null;
        entry.Runtime = null;
        entry.Exports = [];
        entry.ServiceBindingId = null;
        entry.IsService = false;
        entry.State = ScriptState.Idle;
        entry.LastError = null;
    }

    private static void ReleaseModule(Entry entry)
    {
        if (entry.Module is null || entry.Runtime is null)
            return;

        try
        {
            entry.Runtime.Unload(entry.Module);
        }
        catch (Exception ex)
        {
            Log.Warn($"Unloading the module of '{entry.Name}' failed: {ex.Message}");
        }
    }

    private void DropTerminal(string name, string message)
    {
        var terminal = Terminals?.ForScript(name);
        if (terminal is null)
            return;

        try
        {
            terminal.Terminate(message);
        }
        catch (Exception ex)
        {
            Log.Warn($"Terminating the terminal of '{name}' failed: {ex.Message}");
        }

        Terminals!.Remove(terminal.Id);
    }

    private void Remove(Entry entry)
    {
        UnloadCore(entry);
        DropTerminal(entry.Name, "[script removed]");
        _entries.Remove(entry.Name);
        RaiseChanged(entry);
    }

    // ---------------------------------------------------------------- bookkeeping

    private static IReadOnlyDictionary<string, ScriptValue> TargetOf(string script)
        => new Dictionary<string, ScriptValue>(StringComparer.Ordinal) { [ScriptEvent.ScriptKey] = ScriptValue.From(script) };

    private Entry? ResolveEntry(string name, out ScriptInfo? transient)
    {
        transient = null;
        var normalized = NormalizeName(name);
        if (_entries.TryGetValue(normalized, out var existing))
            return existing;

        string fullPath;
        try
        {
            fullPath = FullPathFor(normalized);
        }
        catch (ArgumentException ex)
        {
            transient = ErrorInfo(normalized, "", ex.Message);
            Log.Warn($"Cannot load '{name}': {ex.Message}");
            return null;
        }

        if (!IsScriptName(normalized) || !File.Exists(fullPath))
        {
            var message = $"script '{normalized}' not found";
            transient = ErrorInfo(normalized, fullPath, message);
            Log.Warn($"Cannot load '{name}': {message}");
            return null;
        }

        var entry = NewEntry(normalized, fullPath);
        _entries[normalized] = entry;
        return entry;
    }

    private static ScriptInfo ErrorInfo(string name, string path, string message)
        => new(name, path, ScriptLanguages.ForPath(name) ?? "", ScriptState.Error, message, [], false, false);

    private Entry NewEntry(string name, string fullPath)
    {
        var entry = new Entry(name, fullPath, ScriptLanguages.ForPath(name) ?? "");
        RefreshHeader(entry);
        return entry;
    }

    private static bool RefreshHeader(Entry entry)
    {
        ScriptHeader header;
        try
        {
            header = ScriptHeader.Parse(string.Join('\n', File.ReadLines(entry.FullPath).Take(40)));
        }
        catch
        {
            return false;
        }

        if (header == entry.Header)
            return false;

        entry.Header = header;
        return true;
    }

    private static string NormalizeName(string name)
    {
        var normalized = (name ?? "").Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }

    private string FullPathFor(string name)
    {
        if (name.Length == 0 || Path.IsPathRooted(name) || name.Split('/').Contains(".."))
            throw new ArgumentException($"invalid script name '{name}'");

        var full = Path.GetFullPath(Path.Combine(_scriptsDir, name.Replace('/', Path.DirectorySeparatorChar)));
        var root = _scriptsDir.EndsWith(Path.DirectorySeparatorChar) ? _scriptsDir : _scriptsDir + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new ArgumentException($"invalid script name '{name}'");
        return full;
    }

    private static bool IsScriptName(string name)
    {
        if (name.Length == 0 || name.StartsWith("..", StringComparison.Ordinal))
            return false;

        var segments = name.Split('/');
        if (segments.Any(s => s.Length == 0 || s[0] == '.'))
            return false;

        if (segments.Length > 1
            && (string.Equals(segments[0], "lib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segments[0], "stubs", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
            return false;

        return ScriptLanguages.ForPath(name) is not null;
    }

    private void OnScriptDisabled(string script, string reason)
    {
        if (!_entries.TryGetValue(script, out var entry))
            return;

        entry.State = ScriptState.Disabled;
        entry.LastError = reason;
        RaiseChanged(entry);
    }

    private void Fail(string name, Exception ex)
    {
        Log.Warn($"Script operation on '{name}' failed: {ex.Message}");
        if (_entries.TryGetValue(NormalizeName(name), out var entry))
        {
            entry.LastError = ex.Message;
            if (entry.Module is null)
                entry.State = ScriptState.Error;
            RaiseChanged(entry);
        }
    }

    private void RaiseChanged(Entry entry)
    {
        try
        {
            Changed?.Invoke(ToInfo(entry));
        }
        catch (Exception ex)
        {
            Log.LogOnce("catalog.changed", LogLevel.Warn, $"A ScriptCatalog.Changed handler failed: {ex.Message}");
        }
    }

    private static ScriptInfo ToInfo(Entry entry)
        => new(entry.Name, entry.FullPath, entry.Language, entry.State, entry.LastError, entry.Exports,
            entry.Header.Terminal, entry.Header.Autostart, entry.Header.Title);

    private sealed class Entry(string name, string fullPath, string language)
    {
        public string Name { get; } = name;

        public string FullPath { get; } = fullPath;

        public string Language { get; } = language;

        public ScriptState State { get; set; } = ScriptState.Idle;

        public string? LastError { get; set; }

        public IReadOnlyList<string> Exports { get; set; } = [];

        public ScriptHeader Header { get; set; } = new(null, false, false);

        public ScriptModule? Module { get; set; }

        public IScriptRuntime? Runtime { get; set; }

        public Guid? ServiceBindingId { get; set; }

        /// <summary>Whether the script was started as a service (survives a Disabled state so a reload restarts it).</summary>
        public bool IsService { get; set; }
    }
}
