using System.Reflection;
using System.Text.RegularExpressions;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KsaScripting.Core.Catalog;
using KsaScripting.Core.Config;
using KsaScripting.Core.Logging;

namespace KsaScripting.GameMod.UI;

// Settings tab: edits every ksa-scripting.toml key on a working copy; Save normalizes, copies and writes the file.
public sealed partial class ScriptManagerWindow
{
    /// <summary>The KSA build this mod was verified against (the KsaAnchor sites carry the same version).</summary>
    private const string VerifiedKsaVersion = "2026.10.10.5554";

    private static readonly string[] HotkeyChoices =
    [
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11",
        "Insert", "Home", "End", "PageUp", "PageDown", "Pause", "ScrollLock",
    ];

    private ScriptingConfig? _cfg;
    private ScriptingConfig? _cfgSource;
    private bool _cfgDirty;
    private string _cfgStatus = "";
    private bool _cfgStatusError;
    private readonly ImInputString _cfgEditorTheme = new(96);
    private readonly ImInputString _cfgAutostartFilter = new(64);
    private string? _modVersion;

    private void DrawSettingsTab()
    {
        EnsureConfigDraft();
        ScriptingConfig cfg = _cfg!;

        bool dirty = false;
        float height = Math.Max(80f, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 1.4f);
        bool visible = ImGui.BeginChild("##settings_scroll", new float2(0f, height), ImGuiChildFlags.None);
        try
        {
            if (visible)
            {
                ImGuiWidgets.TextColoredWrapped(
                    ImGuiWidgets.WarningColor,
                    "Budgets, runtimes and limits take effect after restarting the game. The manager hotkey, the autostart list and the authority switches are read live.");

                dirty |= DrawBudgetSettings(cfg);
                dirty |= DrawAuthoritySettings(cfg);
                dirty |= DrawAutostartSettings(cfg);
                dirty |= DrawEditorSettings(cfg);
                dirty |= DrawRuntimeSettings(cfg);
                dirty |= DrawHotkeySetting(cfg);
                DrawAbout();
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        _cfgDirty |= dirty;

        if (ImGuiWidgets.Button("Save##cfg_save", _cfgDirty, "Nothing to save."))
        {
            SaveConfigDraft();
        }

        ImGui.SameLine();
        if (ImGuiWidgets.Button("Revert##cfg_revert", _cfgDirty, "Nothing to revert."))
        {
            _cfg = null;
            EnsureConfigDraft();
            _cfgDirty = false;
            _cfgStatus = "";
        }

        ImGui.SameLine();
        if (_cfgStatus.Length > 0)
        {
            ImGuiWidgets.TextColored(_cfgStatusError ? ImGuiWidgets.ErrorColor : ImGuiWidgets.SuccessColor, _cfgStatus);
        }
        else if (_cfgDirty)
        {
            ImGuiWidgets.TextMuted("Unsaved changes.");
        }
    }

    // ------------------------------------------------------------------------------ draft

    private void EnsureConfigDraft()
    {
        if (_cfg is not null && ReferenceEquals(_cfgSource, _host.Config))
        {
            return;
        }

        _cfgSource = _host.Config;
        _cfg = CloneConfig(_host.Config);
        ImGuiWidgets.SetText(_cfgEditorTheme, _cfg.EditorTheme ?? "");
        _cfgDirty = false;
    }

    private static ScriptingConfig CloneConfig(ScriptingConfig source)
    {
        var copy = new ScriptingConfig();
        CopyConfig(source, copy);
        return copy;
    }

    private static void CopyConfig(ScriptingConfig from, ScriptingConfig to)
    {
        to.FrameBudgetMs = from.FrameBudgetMs;
        to.SyncHookBudgetMs = from.SyncHookBudgetMs;
        to.CallBudgetMs = from.CallBudgetMs;
        to.FaultLimit = from.FaultLimit;
        to.ControlAllVessels = from.ControlAllVessels;
        to.DebugNamespace = from.DebugNamespace;
        to.Autostart = new List<string>(from.Autostart ?? []);
        to.EditorEnabled = from.EditorEnabled;
        to.EditorPortMin = from.EditorPortMin;
        to.EditorPortMax = from.EditorPortMax;
        to.EditorTheme = from.EditorTheme ?? "";
        to.V8Enabled = from.V8Enabled;
        to.LuaEnabled = from.LuaEnabled;
        to.V8HeapLimitMb = from.V8HeapLimitMb;
        to.V8DebugPort = from.V8DebugPort;
        to.LuaJitEnabled = from.LuaJitEnabled;
        to.LuaMemoryLimitKb = from.LuaMemoryLimitKb;
        to.TerminalOutputBufferKib = from.TerminalOutputBufferKib;
        to.ManagerHotkey = from.ManagerHotkey;
    }

    private void SaveConfigDraft()
    {
        try
        {
            ScriptingConfig draft = _cfg!;
            CopyConfig(draft, _host.Config);
            IReadOnlyList<string> warnings = _host.Config.Normalize();
            _host.SaveConfig();

            _cfg = CloneConfig(_host.Config);
            ImGuiWidgets.SetText(_cfgEditorTheme, _cfg.EditorTheme ?? "");
            _cfgDirty = false;
            _cfgStatusError = false;
            _cfgStatus = warnings.Count == 0
                ? $"Saved to {Path.GetFileName(_host.Paths.ConfigFile)}."
                : $"Saved with {warnings.Count} correction(s): {string.Join(" ", warnings)}";
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving the config failed: {ex.Message}");
            _cfgStatusError = true;
            _cfgStatus = $"Could not save the config: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------------------ sections

    private static bool DragDouble(string label, double current, float speed, float min, float max, string format, Action<double> set)
    {
        float v = (float)current;
        if (ImGui.DragFloat(label, ref v, speed, min, max, format, ImGuiSliderFlags.AlwaysClamp))
        {
            set(Math.Round(v, 3));
            return true;
        }

        return false;
    }

    private bool DrawBudgetSettings(ScriptingConfig cfg)
    {
        bool changed = false;
        ImGui.SeparatorText("Budgets");
        if (ImGuiWidgets.BeginFormTableFixed("##cfg_budgets", 160f, 3f))
        {
            try
            {
                ImGuiWidgets.FormRow("Frame budget");
                changed |= DragDouble("##cfg_frame", cfg.FrameBudgetMs, 0.05f, 0.1f, 100f, "%.2f ms", v => cfg.FrameBudgetMs = v);
                ImGuiWidgets.FormRow("Sync hook budget");
                changed |= DragDouble("##cfg_sync", cfg.SyncHookBudgetMs, 0.1f, 0.1f, 1000f, "%.2f ms", v => cfg.SyncHookBudgetMs = v);
                ImGuiWidgets.FormRow("Call budget");
                changed |= DragDouble("##cfg_call", cfg.CallBudgetMs, 1f, 1f, 10000f, "%.1f ms", v => cfg.CallBudgetMs = v);
                ImGuiWidgets.FormRow("Fault limit");
                int faults = cfg.FaultLimit;
                if (ImGui.DragInt("##cfg_faults", ref faults, 0.2f, 1, 1000, "%d", ImGuiSliderFlags.AlwaysClamp))
                {
                    cfg.FaultLimit = faults;
                    changed = true;
                }
            }
            finally
            {
                ImGuiWidgets.EndFormTable();
            }
        }

        ImGuiWidgets.TextMuted("Frame budget: total script time per Step slot. Sync hook: watchdog for synchronous event handlers. Call: watchdog for any other call. Fault limit: consecutive faults before a script is disabled.");
        return changed;
    }

    private bool DrawAuthoritySettings(ScriptingConfig cfg)
    {
        bool changed = false;
        ImGui.SeparatorText("Authority");
        bool all = cfg.ControlAllVessels;
        if (ImGui.Checkbox("Scripts may control any vessel (off: the controlled vessel only)##cfg_all", ref all))
        {
            cfg.ControlAllVessels = all;
            changed = true;
        }

        bool debug = cfg.DebugNamespace;
        if (ImGui.Checkbox("Allow debug actions (teleport, impulse, refuel...)##cfg_debug", ref debug))
        {
            cfg.DebugNamespace = debug;
            changed = true;
        }

        return changed;
    }

    private bool DrawAutostartSettings(ScriptingConfig cfg)
    {
        bool changed = false;
        ImGui.SeparatorText("Autostart");
        ImGuiWidgets.TextMuted("Scripts started when the game loads (as if you pressed Start). A script header  @ksa autostart=true  does the same.");

        int remove = -1;
        for (int i = 0; i < cfg.Autostart.Count; i++)
        {
            using (new IdScope($"auto{i}"))
            {
                if (ImGui.SmallButton("x"))
                {
                    remove = i;
                }

                ImGui.SameLine();
                string name = cfg.Autostart[i];
                bool known = _scripts.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal));
                if (known)
                {
                    ImGui.Text(name);
                }
                else
                {
                    ImGuiWidgets.TextColored(ImGuiWidgets.WarningColor, name);
                    ImGuiWidgets.Tooltip("No script with this name exists.");
                }
            }
        }

        if (remove >= 0)
        {
            cfg.Autostart.RemoveAt(remove);
            changed = true;
        }

        if (cfg.Autostart.Count == 0)
        {
            ImGuiWidgets.TextMuted("(none)");
        }

        ImGui.SetNextItemWidth(320f);
        if (ImGuiWidgets.FilterCombo(
                "##cfg_autostart_add",
                "add a script...",
                _cfgAutostartFilter,
                () => AutostartCandidates(cfg),
                out string? picked)
            && picked is not null)
        {
            cfg.Autostart.Add(picked);
            changed = true;
        }

        return changed;
    }

    private List<(string Key, string Label)> AutostartCandidates(ScriptingConfig cfg)
    {
        var items = new List<(string Key, string Label)>();
        foreach (ScriptInfo script in _scripts)
        {
            if (!cfg.Autostart.Contains(script.Name))
            {
                items.Add((script.Name, script.Name));
            }
        }

        return items;
    }

    private bool DrawEditorSettings(ScriptingConfig cfg)
    {
        bool changed = false;
        ImGui.SeparatorText("Editor");
        bool enabled = cfg.EditorEnabled;
        if (ImGui.Checkbox("Enable the Neovim editor integration##cfg_editor", ref enabled))
        {
            cfg.EditorEnabled = enabled;
            changed = true;
        }

        if (ImGuiWidgets.BeginFormTableFixed("##cfg_editor_form", 200f, 3f))
        {
            try
            {
                ImGuiWidgets.FormRow("RPC ports (loopback)");
                int min = cfg.EditorPortMin;
                int max = cfg.EditorPortMax;
                float half = Math.Max(60f, (ImGui.GetContentRegionAvail().X - 12f) / 2f);
                ImGui.SetNextItemWidth(half);
                if (ImGui.DragInt("##cfg_port_min", ref min, 1f, 1024, 65535, "%d", ImGuiSliderFlags.AlwaysClamp))
                {
                    cfg.EditorPortMin = min;
                    changed = true;
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(half);
                if (ImGui.DragInt("##cfg_port_max", ref max, 1f, 1024, 65535, "%d", ImGuiSliderFlags.AlwaysClamp))
                {
                    cfg.EditorPortMax = max;
                    changed = true;
                }

                ImGuiWidgets.FormRow("Editor theme");
                if (ImGuiWidgets.InputWithHint("##cfg_editor_theme", "blank = default theme", _cfgEditorTheme))
                {
                    cfg.EditorTheme = _cfgEditorTheme.ToString().Trim();
                    changed = true;
                }
            }
            finally
            {
                ImGuiWidgets.EndFormTable();
            }
        }

        return changed;
    }

    private bool DrawRuntimeSettings(ScriptingConfig cfg)
    {
        bool changed = false;
        ImGui.SeparatorText("Runtimes and limits");

        bool v8 = cfg.V8Enabled;
        if (ImGui.Checkbox("TypeScript (V8) runtime enabled##cfg_v8", ref v8))
        {
            cfg.V8Enabled = v8;
            changed = true;
        }

        ImGui.SameLine();
        bool lua = cfg.LuaEnabled;
        if (ImGui.Checkbox("Lua (LuaJIT) runtime enabled##cfg_lua", ref lua))
        {
            cfg.LuaEnabled = lua;
            changed = true;
        }

        bool jit = cfg.LuaJitEnabled;
        if (ImGui.Checkbox("LuaJIT compiler on (off: interpreter only, slower but simplest to debug)##cfg_jit", ref jit))
        {
            cfg.LuaJitEnabled = jit;
            changed = true;
        }

        if (ImGuiWidgets.BeginFormTableFixed("##cfg_limits", 210f, 3f))
        {
            try
            {
                changed |= DragLimit("V8 heap limit (MiB)", "##cfg_v8heap", cfg.V8HeapLimitMb, 1f, 8, 4096, v => cfg.V8HeapLimitMb = v);
                changed |= DragLimit("V8 inspector port (0 = off)", "##cfg_v8port", cfg.V8DebugPort, 1f, 0, 65535, v => cfg.V8DebugPort = v);
                changed |= DragLimit("Lua memory limit (KiB)", "##cfg_luamem", cfg.LuaMemoryLimitKb, 256f, 1024, 4194304, v => cfg.LuaMemoryLimitKb = v);
                changed |= DragLimit("Terminal output buffer (KiB)", "##cfg_termbuf", cfg.TerminalOutputBufferKib, 4f, 4, 65536, v => cfg.TerminalOutputBufferKib = v);
            }
            finally
            {
                ImGuiWidgets.EndFormTable();
            }
        }

        ImGuiWidgets.TextMuted("The inspector port must be 0 or 1024-65535 (other values are reset to 0 on save).");
        return changed;
    }

    private static bool DragLimit(string label, string id, int current, float speed, int min, int max, Action<int> set)
    {
        ImGuiWidgets.FormRow(label);
        int value = current;
        if (ImGui.DragInt(id, ref value, speed, min, max, "%d", ImGuiSliderFlags.AlwaysClamp))
        {
            set(value);
            return true;
        }

        return false;
    }

    private bool DrawHotkeySetting(ScriptingConfig cfg)
    {
        bool changed = false;
        ImGui.SeparatorText("Manager hotkey");
        int index = Array.FindIndex(HotkeyChoices, h => string.Equals(h, cfg.ManagerHotkey, StringComparison.OrdinalIgnoreCase));
        string preview = index >= 0 ? HotkeyChoices[index] : $"{cfg.ManagerHotkey} (custom)";
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("##cfg_hotkey", preview))
        {
            try
            {
                for (int i = 0; i < HotkeyChoices.Length; i++)
                {
                    if (ImGui.Selectable($"{HotkeyChoices[i]}##hk{i}", i == index))
                    {
                        cfg.ManagerHotkey = HotkeyChoices[i];
                        changed = true;
                    }
                }
            }
            finally
            {
                ImGui.EndCombo();
            }
        }

        ImGui.SameLine();
        ImGuiWidgets.TextMuted("Toggles this window (F12 is reserved for purrTTY). Saved with the other settings.");
        return changed;
    }

    // ------------------------------------------------------------------------------ about

    private void DrawAbout()
    {
        ImGui.SeparatorText("About");
        if (ImGuiWidgets.BeginFormTableFixed("##cfg_about", 170f, 3f))
        {
            try
            {
                ImGuiWidgets.FormText("ksa-scripting", ModVersion());

                string ksa = RunningKsaVersion();
                bool match = ksa.TrimStart('v').StartsWith(VerifiedKsaVersion, StringComparison.Ordinal);
                ImGuiWidgets.FormText("Verified against KSA", VerifiedKsaVersion);
                ImGuiWidgets.FormText(
                    "Running KSA",
                    match ? $"{ksa} (matches)" : $"{ksa} - differs from the verified build; reflection-based readers and event patches may misbehave (see Runtimes)",
                    match ? ImGuiWidgets.SuccessColor : ImGuiWidgets.WarningColor);

                bool placer = _host.Placer.IsAvailable;
                ImGuiWidgets.FormText(
                    "purrTTY terminals",
                    placer ? "available (terminal windows and in-world terminals work)" : $"unavailable: {_host.Placer.UnavailableReason ?? "the purrTTY launcher is missing"}",
                    placer ? ImGuiWidgets.SuccessColor : ImGuiWidgets.WarningColor);
                bool editor = _host.Editor.IsAvailable;
                ImGuiWidgets.FormText(
                    "Editor",
                    editor ? "available" : $"unavailable: {_host.Editor.UnavailableReason ?? "unknown reason"}",
                    editor ? ImGuiWidgets.SuccessColor : ImGuiWidgets.WarningColor);
                ImGuiWidgets.FormText("Data folder", _host.Paths.DataDir);
            }
            finally
            {
                ImGuiWidgets.EndFormTable();
            }
        }
    }

    private string ModVersion()
    {
        if (_modVersion is not null)
        {
            return _modVersion;
        }

        string version = "";
        try
        {
            string modToml = Path.Combine(_host.Paths.InstallDir, "mod.toml");
            if (File.Exists(modToml))
            {
                Match m = Regex.Match(File.ReadAllText(modToml), @"^\s*version\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                if (m.Success)
                {
                    version = m.Groups[1].Value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
            // fall back to the assembly version below
        }

        if (version.Length == 0)
        {
            Assembly assembly = typeof(ScriptManagerWindow).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            version = informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString() ?? "unknown";
        }

        _modVersion = version;
        return version;
    }

    private static string RunningKsaVersion()
    {
        try
        {
            return KSA.VersionInfo.Current.VersionString;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
