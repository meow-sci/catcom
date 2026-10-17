using Brutal.ImGuiApi;
using Brutal.Numerics;
using Catcom.Core.Catalog;
using Catcom.Core.Hosting;
using Catcom.Core.Logging;
using Catcom.Core.Runtime;

namespace Catcom.GameMod.UI;

/// <summary>
///     The Script Manager ImGui window (guide §7; docs/research/architecture/script-manager-ui.md).
///     Drawn every frame from <c>[StarMapAfterGui]</c>; reads host state directly and posts every
///     mutating action through <see cref="ScriptingHost.Post"/>.
/// </summary>
/// <remarks>
///     <para>
///         Layout: a toolbar, a scripts list on the left and the selected script's tabs (Overview, Bindings,
///         Terminal, Output, Code) on the right, and a collapsible bottom section with the global tabs
///         (Events, Runtimes, Settings). Dialogs (New, Add binding, confirmations, scripts folder) are modal
///         popups opened through request flags at the window's top-level ID scope (an <c>OpenPopup</c> from
///         inside a child window would hash a different ID than the <c>BeginPopupModal</c> outside it).
///     </para>
///     <para>
///         Robustness: every section runs under <see cref="Guard"/> (an exception is logged once and the rest
///         of the window keeps drawing); every <c>Begin*</c> has its <c>End*</c> in a <c>finally</c>.
///     </para>
/// </remarks>
public sealed partial class ScriptManagerWindow : IDisposable
{
    private const string WindowTitle = "Scripting###catcom-manager";
    private const double ListRefreshSeconds = 0.25;

    // The guard reads the flag from the input thread; it is only trusted while Draw keeps refreshing it, so a
    // window that stopped drawing (mod unloaded, draw hook broken) can never leave the game's hotkeys blocked.
    private const long WantsTextFreshMs = 500;

    private static volatile bool s_wantsTextInput;
    private static long s_wantsTextStamp;
    // Weak: the mod owns the window; a static strong reference would keep an unloaded host (and its assembly load context) alive.
    private static WeakReference<ScriptManagerWindow>? s_instanceRef;

    private readonly ScriptingHost _host;
    private readonly IGameBridge _bridge;
    private string? _requestDetailTab;
    private string? _requestGlobalTab;
    private bool _open;

    // scripts list
    private IReadOnlyList<ScriptInfo> _scripts = [];
    private double _scriptsAt = double.NegativeInfinity;
    private string? _selected;
    private readonly Dictionary<string, double> _hiddenUntil = new(StringComparer.Ordinal);
    private readonly ImInputString _filter = new(128);
    private bool _globalsOpen;

    // toggle hotkey
    private string _hotkeyText = "";
    private ImGuiKey _hotkey = ImGuiKey.F11;

    /// <summary>Creates the window bound to the running host.</summary>
    public ScriptManagerWindow(ScriptingHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _bridge = host.Bridge;
        s_instanceRef = new WeakReference<ScriptManagerWindow>(this);
    }

    /// <summary>The single live window (for the menu entries), or null.</summary>
    public static ScriptManagerWindow? Instance
    {
        get => s_instanceRef is { } reference && reference.TryGetTarget(out ScriptManagerWindow? window) ? window : null;
        private set => s_instanceRef = value is null ? null : new WeakReference<ScriptManagerWindow>(value);
    }

    /// <summary>
    ///     True while a text input inside the window has keyboard focus this frame (set inside
    ///     Begin/End); read by the GameSettings.OnKeyAll hotkey guard (possibly from the input thread).
    /// </summary>
    public static bool WantsTextInput
    {
        get => s_wantsTextInput
               && System.Diagnostics.Stopwatch.GetTimestamp() - Volatile.Read(ref s_wantsTextStamp)
               < System.Diagnostics.Stopwatch.Frequency * WantsTextFreshMs / 1000;
        private set
        {
            Volatile.Write(ref s_wantsTextStamp, System.Diagnostics.Stopwatch.GetTimestamp());
            s_wantsTextInput = value;
        }
    }

    /// <summary>Whether the window is shown.</summary>
    public bool IsOpen
    {
        get => _open;
        set => _open = value;
    }

    /// <summary>Shows/hides the window.</summary>
    public void Toggle() => IsOpen = !IsOpen;

    /// <summary>Detaches the window from the menu entry points and releases the hotkey guard (call on mod unload).</summary>
    public void Dispose()
    {
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }

        WantsTextInput = false;
        _open = false;
    }

    /// <summary>Per-frame draw (also polls the toggle hotkey). Never throws.</summary>
    public void Draw(double dt)
    {
        _ = dt;
        try
        {
            ImGuiWidgets.FrameWantsText = false;

            Guard("hotkey", PollHotkey);

            if (!_open)
            {
                WantsTextInput = false;
                return;
            }

            DrawWindow();
        }
        catch (Exception ex)
        {
            Fail("draw", ex);
            WantsTextInput = false;
        }
    }

    // ------------------------------------------------------------------------------ window

    private void DrawWindow()
    {
        ImGui.SetNextWindowSize(new float2(900, 560), ImGuiCond.FirstUseEver);
        bool open = _open;
        bool visible = ImGui.Begin(WindowTitle, ref open);
        try
        {
            if (visible)
            {
                DrawContents();
            }

            // Evaluated inside Begin/End: focus queries refer to the current window. Popups and combo
            // dropdowns opened from this window count as part of its popup hierarchy.
            ImGuiWidgets.NoteTextInput();
        }
        catch (Exception ex)
        {
            Fail("window", ex);
        }
        finally
        {
            ImGui.End();
        }

        _open = open;
        WantsTextInput = ImGuiWidgets.FrameWantsText && _open;
    }

    private void DrawContents()
    {
        RefreshScripts();
        if (_selected is null && _scripts.Count > 0)
        {
            _selected = _scripts[0].Name;
        }

        Guard("toolbar", DrawToolbar);
        Guard("toast", DrawToast);
        ImGui.Separator();

        float2 avail = ImGui.GetContentRegionAvail();
        float toggleRow = ImGui.GetFrameHeightWithSpacing();
        float globalsHeight = _globalsOpen ? Math.Clamp(avail.Y * 0.55f, 220f, 560f) : 0f;
        float panesHeight = Math.Max(140f, avail.Y - toggleRow - (_globalsOpen ? globalsHeight + 6f : 0f));
        float listWidth = Math.Clamp(avail.X * 0.30f, 220f, 380f);

        DrawChild("##scripts", new float2(listWidth, panesHeight), "scripts list", DrawScriptList);
        ImGui.SameLine();
        DrawChild("##detail", new float2(0f, panesHeight), "script details", DrawDetail);

        if (ImGui.SmallButton(_globalsOpen ? "Hide Events / Runtimes / Settings##globals_toggle" : "Show Events / Runtimes / Settings##globals_toggle"))
        {
            _globalsOpen = !_globalsOpen;
        }

        if (_globalsOpen)
        {
            DrawChild("##globals", new float2(0f, globalsHeight), "global tabs", DrawGlobals);
        }

        Guard("dialogs", DrawDialogs);
    }

    /// <summary>Draws a bordered child window whose body is <paramref name="body"/>; the child is always ended.</summary>
    private void DrawChild(string id, float2 size, string what, Action body)
    {
        bool visible = ImGui.BeginChild(id, size, ImGuiChildFlags.Borders);
        try
        {
            if (visible)
            {
                Guard(what, body);
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    // ------------------------------------------------------------------------------ hotkey

    private void PollHotkey()
    {
        ImGuiKey key = ResolveHotkey();
        if (key == ImGuiKey.None)
        {
            return;
        }

        // Never steal a key press that belongs to a text field (this window's, another mod's, the console).
        if (ImGui.GetIO().WantTextInput)
        {
            return;
        }

        if (ImGui.IsKeyPressed(key, false))
        {
            Toggle();
        }
    }

    private ImGuiKey ResolveHotkey()
    {
        string text = _host.Config.ManagerHotkey ?? "";
        if (!string.Equals(text, _hotkeyText, StringComparison.Ordinal))
        {
            _hotkeyText = text;
            _hotkey = ParseHotkey(text);
        }

        return _hotkey;
    }

    /// <summary>Parses a <c>manager_hotkey</c> value; blank, unknown or reserved names (F12 belongs to purrTTY) fall back to F11.</summary>
    internal static ImGuiKey ParseHotkey(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)
            && Enum.TryParse(text.Trim(), ignoreCase: true, out ImGuiKey key)
            && Enum.IsDefined(key)
            && IsAllowedHotkey(key))
        {
            return key;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            Log.LogOnce($"ui.hotkey.{text}", LogLevel.Warn, $"manager_hotkey '{text}' is not usable (unknown name or reserved); using F11.");
        }

        return ImGuiKey.F11;
    }

    private static bool IsAllowedHotkey(ImGuiKey key)
    {
        if (key is ImGuiKey.None or ImGuiKey.F12 or ImGuiKey.Escape or ImGuiKey.Enter or ImGuiKey.Space or ImGuiKey.Tab or ImGuiKey.Backspace)
        {
            return false;
        }

        string name = key.ToString();
        if (name.StartsWith("Mouse", StringComparison.Ordinal)
            || name.StartsWith("Gamepad", StringComparison.Ordinal)
            || name.StartsWith("Mod", StringComparison.Ordinal)
            || name.StartsWith("NamedKey", StringComparison.Ordinal)
            || name.StartsWith("Reserved", StringComparison.Ordinal))
        {
            return false;
        }

        return name is not ("LeftCtrl" or "LeftShift" or "LeftAlt" or "LeftSuper"
            or "RightCtrl" or "RightShift" or "RightAlt" or "RightSuper" or "Menu");
    }

    private string HotkeyLabel()
    {
        ResolveHotkey();
        return _hotkey.ToString();
    }

    // ------------------------------------------------------------------------------ toolbar

    private void DrawToolbar()
    {
        ScriptInfo? sel = SelectedInfo();
        bool has = sel is not null;
        bool editorOk = _host.Editor.IsAvailable;

        if (ImGui.Button("New...##tb_new"))
        {
            _requestNew = true;
        }

        ImGuiWidgets.Tooltip("Create a script from a template.");

        ImGui.SameLine();
        string? editHint = !has ? "Select a script first."
            : !editorOk ? _host.Editor.UnavailableReason ?? "The editor is not available."
            : "Edit the script in Neovim inside a purrTTY window.";
        if (ImGuiWidgets.Button("Edit##tb_edit", has && editorOk, editHint) && sel is not null)
        {
            OpenEditor(sel.Name, sel.Path, null);
        }

        if (has && editorOk)
        {
            ImGuiWidgets.Tooltip(editHint);
        }

        ImGui.SameLine();
        bool canRun = has && !sel!.HasTerminal;
        string runHint = !has ? "Select a script first." : "Terminal scripts need their terminal: use Start, then open a window in the Terminal tab.";
        if (ImGuiWidgets.Button("Run##tb_run", canRun, runHint) && sel is not null)
        {
            RunScript(sel.Name);
        }

        if (canRun)
        {
            ImGuiWidgets.Tooltip("Load the script and call main(ctx) once.");
        }

        ImGui.SameLine();
        bool running = sel is { State: ScriptState.Running or ScriptState.Service };
        if (ImGuiWidgets.Button((running ? "Stop" : "Start") + "##tb_startstop", has, "Select a script first.") && sel is not null)
        {
            if (running)
            {
                StopWithConfirm(sel);
            }
            else
            {
                StartScript(sel.Name);
            }
        }

        if (has)
        {
            ImGuiWidgets.Tooltip(running
                ? "Stop the script: cancel its service, remove its hooks and unload it."
                : "Load the script and start main(ctx) as a service (creates its terminal when the header says terminal=true).");
        }

        ImGui.SameLine();
        if (ImGuiWidgets.Button("Reload##tb_reload", has, "Select a script first.") && sel is not null)
        {
            ReloadScript(sel.Name);
        }

        if (has)
        {
            ImGuiWidgets.Tooltip("Recompile from disk. A failed reload keeps the previous version running.");
        }

        ImGui.SameLine();
        using (new DisabledScope(!has))
        {
            if (ImGuiWidgets.DestructiveButton("Delete##tb_delete") && sel is not null)
            {
                RequestDelete(sel);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Open folder##tb_folder"))
        {
            _requestFolder = true;
        }

        ImGuiWidgets.Tooltip("Show the scripts folder path (no OS shell is opened).");

        // Filter box, right-aligned when there is room.
        float rightEdge = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
        float filterWidth = 200f;
        float x = Math.Max(rightEdge + 12f, ImGui.GetWindowWidth() - filterWidth - 16f);
        ImGui.SameLine(x);
        ImGui.SetNextItemWidth(filterWidth);
        ImGuiWidgets.InputWithHint("##tb_filter", "filter scripts...", _filter);
    }

    // ------------------------------------------------------------------------------ scripts list

    private void RefreshScripts(bool force = false)
    {
        double now = ImGui.GetTime();
        if (!force && now - _scriptsAt < ListRefreshSeconds)
        {
            return;
        }

        IReadOnlyList<ScriptInfo> all = _host.Catalog.All();
        if (_hiddenUntil.Count > 0)
        {
            // Scripts we just deleted stay listed by the catalog until the posted Scan runs; hide them meanwhile.
            var visible = new List<ScriptInfo>(all.Count);
            foreach (ScriptInfo script in all)
            {
                if (_hiddenUntil.TryGetValue(script.Name, out double until) && now < until)
                {
                    continue;
                }

                visible.Add(script);
            }

            foreach (string name in _hiddenUntil.Where(p => now >= p.Value).Select(p => p.Key).ToArray())
            {
                _hiddenUntil.Remove(name);
            }

            all = visible;
        }

        _scripts = all;
        _scriptsAt = now;
    }

    private ScriptInfo? SelectedInfo()
    {
        if (_selected is null)
        {
            return null;
        }

        for (int i = 0; i < _scripts.Count; i++)
        {
            if (string.Equals(_scripts[i].Name, _selected, StringComparison.Ordinal))
            {
                return _scripts[i];
            }
        }

        return null;
    }

    private bool HasAttachedTerminal(string script)
        => _host.Terminals.ForScript(script) is { Attached: > 0 };

    private void DrawScriptList()
    {
        if (_scripts.Count == 0)
        {
            DrawEmptyState();
            return;
        }

        string needle = _filter.ToString().Trim();
        int shown = 0;

        if (ImGui.BeginTable("##scripttable", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoBordersInBody))
        {
            try
            {
                ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("lang", ImGuiTableColumnFlags.WidthFixed, 46f);
                ImGui.TableSetupColumn("state", ImGuiTableColumnFlags.WidthFixed, 44f);

                for (int i = 0; i < _scripts.Count; i++)
                {
                    ScriptInfo info = _scripts[i];
                    if (needle.Length > 0
                        && info.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                        && (info.Title is null || info.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        continue;
                    }

                    shown++;
                    DrawScriptRow(info, i);
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        if (shown == 0)
        {
            ImGuiWidgets.TextMuted("No script matches the filter.");
        }
    }

    private void DrawScriptRow(ScriptInfo info, int index)
    {
        bool terminalAttached = HasAttachedTerminal(info.Name);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        bool selected = string.Equals(_selected, info.Name, StringComparison.Ordinal);
        if (ImGui.Selectable($"{ImGuiWidgets.Safe(info.Name)}##row{index}", selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
        {
            _selected = info.Name;
        }

        if (ImGui.IsItemHovered())
        {
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && _host.Editor.IsAvailable)
            {
                _selected = info.Name;
                OpenEditor(info.Name, info.Path, null);
            }

            string title = info.Title is { Length: > 0 } t ? $"{t}\n" : "";
            ImGuiWidgets.Tooltip($"{title}{info.Name}\n{StateBadge.Describe(info, terminalAttached)}");
        }

        ImGui.TableNextColumn();
        ImGuiWidgets.TextMuted(info.Language == ScriptLanguages.TypeScript ? "[ts]" : "[lua]");
        ImGui.TableNextColumn();
        StateBadge.Draw(info, terminalAttached);
    }

    private void DrawEmptyState()
    {
        ImGuiWidgets.TextColored(ImGuiWidgets.InfoColor, "No scripts yet");
        ImGui.Spacing();
        ImGuiWidgets.TextWrapped("1. New...  pick a name, a language (Lua or TypeScript) and a template.");
        ImGuiWidgets.TextWrapped("2. Edit  opens the file in Neovim inside a purrTTY window (needs purrTTY).");
        ImGuiWidgets.TextWrapped("3. Run  executes main(ctx) once; Start keeps a service running.");
        ImGui.Spacing();
        ImGuiWidgets.TextMuted("Scripts folder:");
        ImGuiWidgets.TextWrapped(_host.Paths.ScriptsDir);
        ImGui.Spacing();
        ImGuiWidgets.TextMuted("API stubs for editor completion (regenerated at every start):");
        ImGuiWidgets.TextWrapped(_host.Paths.StubsDir + "  (host.d.ts, ksa.lua)");
        ImGui.Spacing();
        if (ImGui.Button("New script...##empty_new"))
        {
            _requestNew = true;
        }
    }

    // ------------------------------------------------------------------------------ detail pane

    private void DrawDetail()
    {
        ScriptInfo? info = SelectedInfo();
        if (info is null)
        {
            ImGuiWidgets.TextMuted(_scripts.Count == 0
                ? "Create a script with New... to get started."
                : "Select a script on the left.");
            return;
        }

        bool terminalAttached = HasAttachedTerminal(info.Name);
        ImGui.AlignTextToFramePadding();
        ImGui.Text(info.Title is { Length: > 0 } title ? $"{title}  ({info.Name})" : info.Name);
        ImGui.SameLine();
        StateBadge.Draw(info, terminalAttached);
        ImGuiWidgets.Tooltip(StateBadge.Describe(info, terminalAttached));

        if (!ImGui.BeginTabBar("##detailtabs", ImGuiTabBarFlags.None))
        {
            return;
        }

        try
        {
            DrawTab("Overview", "overview", () => DrawOverview(info));
            DrawTab("Bindings", "bindings", () => DrawBindings(info));
            DrawTab("Terminal", "terminal", () => DrawTerminalTab(info));
            DrawTab("Output", "output", () => DrawOutput(info));
            DrawTab("Code", "code", () => DrawCode(info));
            _requestDetailTab = null;
        }
        finally
        {
            ImGui.EndTabBar();
        }
    }

    /// <summary>Selects one of the detail tabs (Overview, Bindings, Terminal, Output, Code) on the next frame.</summary>
    internal void RequestDetailTab(string label) => _requestDetailTab = label;

    private void DrawTab(string label, string key, Action body)
    {
        ImGuiTabItemFlags flags = string.Equals(_requestDetailTab, label, StringComparison.Ordinal) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (!ImGui.BeginTabItem(label, flags))
        {
            return;
        }

        try
        {
            Guard(key, body);
        }
        finally
        {
            ImGui.EndTabItem();
        }
    }

    // ------------------------------------------------------------------------------ menu

    /// <summary>The body of the "Scripting" menu (ModMenu entry and the DrawProgramMenusHook fallback).</summary>
    public static void DrawMenuContent()
    {
        ScriptManagerWindow? window = Instance;
        if (window is null)
        {
            ImGuiWidgets.TextMuted("Scripting is not running (see the game log).");
            return;
        }

        try
        {
            window.DrawMenuCore();
        }
        catch (Exception ex)
        {
            window.Fail("menu", ex);
        }
    }

    private void DrawMenuCore()
    {
        bool open = _open;
        if (ImGui.MenuItem("Script Manager", HotkeyLabel(), ref open))
        {
            IsOpen = open;
        }

        if (ImGui.MenuItem("New script..."))
        {
            IsOpen = true;
            _requestNew = true;
        }

        if (ImGui.BeginMenu("Running"))
        {
            try
            {
                RefreshScripts();
                int services = 0;
                for (int i = 0; i < _scripts.Count; i++)
                {
                    ScriptInfo info = _scripts[i];
                    if (info.State != ScriptState.Service)
                    {
                        continue;
                    }

                    services++;
                    if (ImGui.MenuItem($"Stop {ImGuiWidgets.Safe(info.Name)}##menu_stop{i}"))
                    {
                        IsOpen = true;
                        _selected = info.Name;
                        StopWithConfirm(info);
                    }
                }

                if (services == 0)
                {
                    ImGui.MenuItem("(no services running)", default, false, false);
                }
            }
            finally
            {
                ImGui.EndMenu();
            }
        }

        if (ImGui.MenuItem("Open scripts folder"))
        {
            IsOpen = true;
            _requestFolder = true;
        }

        ImGuiWidgets.Tooltip(_host.Paths.ScriptsDir);
    }

    // ------------------------------------------------------------------------------ actions

    /// <summary>Queues <paramref name="action"/> for the next Step slot; a failure is logged, never thrown.</summary>
    private void Post(string what, Action action)
    {
        _host.Post(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Warn($"Script Manager action '{what}' failed: {ex.Message}");
            }
        });
    }

    private void RunScript(string name) => Post($"run {name}", () => _host.Catalog.Run(name));

    private void StartScript(string name) => Post($"start {name}", () => _host.Catalog.Start(name));

    private void StopScript(string name) => Post($"stop {name}", () => _host.Catalog.Stop(name));

    private void ReloadScript(string name) => Post($"reload {name}", () => _host.Catalog.Reload(name));

    private void OpenEditor(string name, string path, int? line)
        => Post($"edit {name}", () => _host.Editor.Open(name, path, line));

    private void StopWithConfirm(ScriptInfo info)
    {
        if (HasAttachedTerminal(info.Name))
        {
            string name = info.Name;
            RequestConfirm(
                "Stop script",
                $"'{name}' has a terminal with surfaces attached. Stopping it ends the script and prints [script exited] on its terminal.",
                "Stop",
                () => StopScript(name));
        }
        else
        {
            StopScript(info.Name);
        }
    }

    private void RequestDelete(ScriptInfo info)
    {
        string name = info.Name;
        string path = info.Path;
        RequestConfirm(
            "Delete script",
            $"Delete '{name}' from disk?\n{path}\n\nThis cannot be undone.",
            "Delete",
            () => DeleteScript(name, path));
    }

    private void DeleteScript(string name, string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Deleting '{path}' failed: {ex.Message}");
            ShowToast($"Could not delete '{name}': {ex.Message}", error: true);
            return;
        }

        if (string.Equals(_selected, name, StringComparison.Ordinal))
        {
            _selected = null;
        }

        _hiddenUntil[name] = ImGui.GetTime() + 3.0;
        Post($"delete {name}", () =>
        {
            ScriptInfo? info = _host.Catalog.Get(name);
            if (info is { State: not (ScriptState.Idle or ScriptState.Error) })
            {
                _host.Catalog.Stop(name);
            }

            _host.Catalog.Unload(name);
            _host.Catalog.Scan();
        });
        _scriptsAt = double.NegativeInfinity;
    }

    // ------------------------------------------------------------------------------ guard

    /// <summary>Runs one section of the window; an exception is logged once per section and the window keeps going.</summary>
    private void Guard(string section, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Fail(section, ex);
        }
    }

    private void Fail(string section, Exception ex)
        => Log.LogOnce($"ui.{section}", LogLevel.Error, $"Script Manager: the '{section}' section failed (logged once): {ex}");
}
