using System.Globalization;
using System.Text.Json;
using KsaScripting.Core.Logging;
using Tomlyn;

namespace KsaScripting.Core.Config;

/// <summary>
///     User configuration, persisted as <c>ksa-scripting.toml</c> in the mod's data folder. Keys are
///     snake_case (<c>frame_budget_ms</c>, …). Loading never throws: a missing file is created with
///     defaults, an unreadable or malformed one is logged and replaced by in-memory defaults while the
///     file on disk is left untouched for the user to fix, and out-of-range values are clamped with a
///     logged warning (<see cref="Normalize"/>).
/// </summary>
public sealed class ScriptingConfig
{
    /// <summary>The config file name.</summary>
    public const string FileName = "ksa-scripting.toml";

    // One cached options instance: TomlSerializerOptions compiles mapping metadata on first use.
    private static readonly TomlSerializerOptions TomlOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const string FileHeader =
        """
        # ksa-scripting configuration.
        #
        # Edit and restart the game (or use the Script Manager). Out-of-range values are clamped
        # (and logged), not rejected. Delete this file to restore every default.

        """;

    private const double DefaultFrameBudgetMs = 2.0;
    private const double DefaultSyncHookBudgetMs = 5.0;
    private const double DefaultCallBudgetMs = 50.0;
    private const int DefaultEditorPortMin = 48600;
    private const int DefaultEditorPortMax = 48699;
    private const string DefaultManagerHotkey = "F11";

    /// <summary>Total script time per Step slot, in milliseconds (<c>frame_budget_ms</c>, 0.1–100).</summary>
    public double FrameBudgetMs { get; set; } = DefaultFrameBudgetMs;

    /// <summary>Watchdog budget for synchronous hooks, in milliseconds (<c>sync_hook_budget_ms</c>, 0.1–1000).</summary>
    public double SyncHookBudgetMs { get; set; } = DefaultSyncHookBudgetMs;

    /// <summary>Watchdog budget for other invocations, in milliseconds (<c>call_budget_ms</c>, 1–10000).</summary>
    public double CallBudgetMs { get; set; } = DefaultCallBudgetMs;

    /// <summary>Consecutive faults before a script is disabled (<c>fault_limit</c>, 1–1000).</summary>
    public int FaultLimit { get; set; } = 3;

    /// <summary>Authority gate: whether scripts may control any vessel (<c>control_all_vessels</c>).</summary>
    public bool ControlAllVessels { get; set; } = true;

    /// <summary>Whether <c>debug.*</c> actions are allowed (<c>debug_namespace</c>).</summary>
    public bool DebugNamespace { get; set; } = true;

    /// <summary>Script names started at load (<c>autostart</c>).</summary>
    public List<string> Autostart { get; set; } = [];

    /// <summary>Whether the Neovim editor integration is enabled (<c>editor_enabled</c>).</summary>
    public bool EditorEnabled { get; set; } = true;

    /// <summary>Lowest loopback port for editor RPC (<c>editor_port_min</c>, 1024–65535).</summary>
    public int EditorPortMin { get; set; } = DefaultEditorPortMin;

    /// <summary>Highest loopback port for editor RPC (<c>editor_port_max</c>, 1024–65535, not below the minimum).</summary>
    public int EditorPortMax { get; set; } = DefaultEditorPortMax;

    /// <summary>The purrTTY theme name for editor windows (<c>editor_theme</c>); empty for the default.</summary>
    public string EditorTheme { get; set; } = "";

    /// <summary>Whether the V8 (TypeScript) runtime is enabled (<c>v8_enabled</c>).</summary>
    public bool V8Enabled { get; set; } = true;

    /// <summary>Whether the LuaJIT runtime is enabled (<c>lua_enabled</c>).</summary>
    public bool LuaEnabled { get; set; } = true;

    /// <summary>Per script engine soft heap limit, in MiB (<c>v8_heap_limit_mb</c>, 8–4096).</summary>
    public int V8HeapLimitMb { get; set; } = 64;

    /// <summary>V8 inspector port; 0 turns it off (<c>v8_debug_port</c>, 0 or 1024–65535).</summary>
    public int V8DebugPort { get; set; }

    /// <summary>Whether the LuaJIT JIT is on; false calls <c>jit.off()</c> (<c>lua_jit_enabled</c>).</summary>
    public bool LuaJitEnabled { get; set; } = true;

    /// <summary>LuaJIT memory limit, in KiB (<c>lua_memory_limit_kb</c>, 1024–4194304).</summary>
    public int LuaMemoryLimitKb { get; set; } = 65536;

    /// <summary>Per terminal output replay buffer, in KiB (<c>terminal_output_buffer_kib</c>, 4–65536).</summary>
    public int TerminalOutputBufferKib { get; set; } = 256;

    /// <summary>The Script Manager toggle key (<c>manager_hotkey</c>); blank restores <c>F11</c>.</summary>
    public string ManagerHotkey { get; set; } = DefaultManagerHotkey;

    /// <summary>
    ///     Loads the config from <paramref name="path"/>, then <see cref="Normalize"/>s it. Never throws: a
    ///     missing file is created with defaults (a write failure is logged as a warning); malformed or
    ///     unreadable TOML is logged as a warning and in-memory defaults are returned, leaving the file
    ///     untouched.
    /// </summary>
    /// <param name="path">The config file path.</param>
    /// <returns>The loaded (or default) config.</returns>
    public static ScriptingConfig LoadOrCreate(string path)
    {
        ScriptingConfig config;
        try
        {
            if (!File.Exists(path))
            {
                config = new ScriptingConfig();
                try
                {
                    config.Save(path);
                    Log.Info($"Created the default config at '{path}'.");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not write the default config to '{path}': {ex.Message}");
                }
            }
            else
            {
                try
                {
                    config = FromToml(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Config '{path}' could not be read ({ex.Message}); using defaults. Fix or delete the file.");
                    return new ScriptingConfig();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Config '{path}' could not be loaded ({ex.Message}); using defaults.");
            return new ScriptingConfig();
        }

        config.Normalize();
        return config;
    }

    /// <summary>
    ///     Parses a TOML document. Missing keys keep their defaults and unknown keys are ignored. Does not
    ///     <see cref="Normalize"/>.
    /// </summary>
    /// <param name="toml">The TOML text.</param>
    /// <returns>The parsed config.</returns>
    /// <exception cref="TomlException">The text is malformed or a value has the wrong type.</exception>
    public static ScriptingConfig FromToml(string toml)
    {
        ArgumentNullException.ThrowIfNull(toml);
        return TomlSerializer.Deserialize<ScriptingConfig>(toml, TomlOptions) ?? new ScriptingConfig();
    }

    /// <summary>Renders the config as TOML: a short header comment followed by the serialized keys.</summary>
    /// <returns>The TOML text.</returns>
    public string ToToml() => FileHeader + "\n" + TomlSerializer.Serialize(this, TomlOptions);

    /// <summary>Writes the config atomically (temp file + rename) to <paramref name="path"/>.</summary>
    /// <param name="path">The config file path.</param>
    public void Save(string path) => AtomicFile.WriteAllText(path, ToToml());

    /// <summary>
    ///     Clamps out-of-range values into a usable range. Each correction is logged with
    ///     <see cref="Log.Warn"/> and returned.
    /// </summary>
    /// <returns>The warning messages, one per correction (empty when nothing changed).</returns>
    public IReadOnlyList<string> Normalize()
    {
        var warnings = new List<string>();

        FrameBudgetMs = Clamp("frame_budget_ms", FrameBudgetMs, 0.1, 100, DefaultFrameBudgetMs, warnings);
        SyncHookBudgetMs = Clamp("sync_hook_budget_ms", SyncHookBudgetMs, 0.1, 1000, DefaultSyncHookBudgetMs, warnings);
        CallBudgetMs = Clamp("call_budget_ms", CallBudgetMs, 1, 10000, DefaultCallBudgetMs, warnings);
        FaultLimit = Clamp("fault_limit", FaultLimit, 1, 1000, warnings);

        EditorPortMin = Clamp("editor_port_min", EditorPortMin, 1024, 65535, warnings);
        EditorPortMax = Clamp("editor_port_max", EditorPortMax, 1024, 65535, warnings);
        if (EditorPortMin > EditorPortMax)
        {
            warnings.Add($"editor_port_min ({EditorPortMin}) is above editor_port_max ({EditorPortMax}); "
                         + $"using {DefaultEditorPortMin}-{DefaultEditorPortMax}.");
            EditorPortMin = DefaultEditorPortMin;
            EditorPortMax = DefaultEditorPortMax;
        }

        V8HeapLimitMb = Clamp("v8_heap_limit_mb", V8HeapLimitMb, 8, 4096, warnings);
        if (V8DebugPort != 0 && (V8DebugPort < 1024 || V8DebugPort > 65535))
        {
            warnings.Add($"v8_debug_port {V8DebugPort} must be 0 (off) or in [1024, 65535]; using 0.");
            V8DebugPort = 0;
        }

        LuaMemoryLimitKb = Clamp("lua_memory_limit_kb", LuaMemoryLimitKb, 1024, 4194304, warnings);
        TerminalOutputBufferKib = Clamp("terminal_output_buffer_kib", TerminalOutputBufferKib, 4, 65536, warnings);

        if (string.IsNullOrWhiteSpace(ManagerHotkey))
        {
            warnings.Add($"manager_hotkey is blank; using {DefaultManagerHotkey}.");
            ManagerHotkey = DefaultManagerHotkey;
        }

        EditorTheme ??= "";
        NormalizeAutostart();

        foreach (var warning in warnings)
            Log.Warn(warning);
        return warnings;
    }

    private void NormalizeAutostart()
    {
        if (Autostart is null)
        {
            Autostart = [];
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<string>(Autostart.Count);
        foreach (var entry in Autostart)
        {
            var name = entry?.Trim();
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
                cleaned.Add(name);
        }

        Autostart = cleaned;
    }

    private static int Clamp(string key, int value, int min, int max, List<string> warnings)
    {
        if (value >= min && value <= max)
            return value;

        var clamped = value < min ? min : max;
        warnings.Add($"{key} {value.ToString(CultureInfo.InvariantCulture)} is out of range [{min}, {max}]; using {clamped}.");
        return clamped;
    }

    private static double Clamp(string key, double value, double min, double max, double fallback, List<string> warnings)
    {
        if (double.IsNaN(value))
        {
            warnings.Add($"{key} is not a number; using the default {Format(fallback)}.");
            return fallback;
        }

        if (value >= min && value <= max)
            return value;

        var clamped = value < min ? min : max;
        warnings.Add($"{key} {Format(value)} is out of range [{Format(min)}, {Format(max)}]; using {Format(clamped)}.");
        return clamped;
    }

    private static string Format(double value) => value.ToString("G9", CultureInfo.InvariantCulture);
}
