using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Catcom.Core.Catalog;
using Catcom.Core.Hooks;
using Catcom.Core.Hosting;
using Catcom.Core.Logging;

namespace Catcom.GameMod.UI;

// Bindings tab and the "Add binding..." dialog.
public sealed partial class ScriptManagerWindow
{
    private const string AddBindingPopup = "Add binding###ksa_add_binding_dialog";
    private const double StoredBindingsRefreshSeconds = 0.5;

    private static readonly string[] KindTexts = ["event", "tick", "service", "command"];
    private static readonly string[] KindLabels =
    [
        "event - call it when a game event fires",
        "tick - call it every frame, step or interval",
        "service - start it as a service when the script loads",
        "command - call it from a console command",
    ];

    private static readonly string[] TickKeys = ["frame", "step", "every"];
    private static readonly string[] ClockTexts = ["ut", "wall", "render"];
    private static readonly string[] ClockLabels =
    [
        "ut - game time (stops while paused, scales with warp)",
        "wall - real time",
        "render - unscaled player frame time",
    ];

    private static IReadOnlyList<(string Key, string Label)>? s_eventItems;

    // stored (bindings.toml) rows, cached
    private IReadOnlyList<BindingRecord> _stored = [];
    private double _storedAt = double.NegativeInfinity;

    // Add binding dialog state
    private string _abScript = "";
    private int _abKind;
    private string? _abEvent;
    private readonly ImInputString _abEventFilter = new(64);
    private readonly ImInputString _abEventCustom = new(96);
    private int _abTickKind;
    private float _abInterval = 1f;
    private int _abClock;
    private readonly ImInputString _abCommand = new(64);
    private string? _abExport;
    private readonly ImInputString _abExportFilter = new(64);
    private bool _abSync;
    private int _abVesselMode;
    private readonly VesselPicker _abVessel = new("##ab_vessel");
    private string _abError = "";

    // ------------------------------------------------------------------------------ stored rows

    private IReadOnlyList<BindingRecord> StoredBindings()
    {
        double now = ImGui.GetTime();
        if (now - _storedAt >= StoredBindingsRefreshSeconds)
        {
            _stored = _host.Bindings.Load();
            _storedAt = now;
        }

        return _stored;
    }

    private void InvalidateStored() => _storedAt = double.NegativeInfinity;

    private static bool SameBinding(BindingRecord a, BindingRecord b)
        => a.Script == b.Script
           && a.Export == b.Export
           && a.Kind == b.Kind
           && a.Key == b.Key
           && a.Sync == b.Sync
           && a.Vessel == b.Vessel
           && a.Clock == b.Clock
           && a.IntervalSeconds.Equals(b.IntervalSeconds);

    /// <summary>Rewrites bindings.toml with <paramref name="change"/> applied to a fresh read, then posts <c>ApplyBindings</c>.</summary>
    private void EditStored(string script, Func<List<BindingRecord>, bool> change)
    {
        try
        {
            var all = new List<BindingRecord>(_host.Bindings.Load());
            if (!change(all))
            {
                return;
            }

            _host.Bindings.Save(all);
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving bindings failed: {ex.Message}");
            ShowToast($"Could not save {Path.GetFileName(_host.Bindings.Path)}: {ex.Message}", error: true);
            return;
        }

        InvalidateStored();
        Post($"apply bindings {script}", () => _host.Catalog.ApplyBindings(script));
    }

    // ------------------------------------------------------------------------------ tab

    private void DrawBindings(ScriptInfo info)
    {
        IReadOnlyList<Binding> live = _host.Hooks.ForScript(info.Name);
        var declared = new List<Binding>();
        foreach (Binding b in live)
        {
            if (b.FromCode)
            {
                declared.Add(b);
            }
        }

        var mine = new List<BindingRecord>();
        foreach (BindingRecord r in StoredBindings())
        {
            if (string.Equals(r.Script, info.Name, StringComparison.Ordinal))
            {
                mine.Add(r);
            }
        }

        if (ImGui.Button("Add binding...##bind_add"))
        {
            _abScript = info.Name;
            _requestAddBinding = true;
        }

        ImGuiWidgets.Tooltip("Attach an exported function to an event, a tick, a service or a console command. Saved in bindings.toml and applied every time the script loads.");
        ImGui.SameLine();
        ImGuiWidgets.TextMuted($"{declared.Count} declared in code, {mine.Count} from the UI");

        if (info.Exports.Count == 0)
        {
            ImGuiWidgets.TextColoredWrapped(
                ImGuiWidgets.WarningColor,
                "No exports known yet - load the script (Run, Start or Reload) so the Add binding picker can list its functions.");
        }

        if (declared.Count == 0 && mine.Count == 0)
        {
            ImGui.Spacing();
            ImGuiWidgets.TextMuted("No bindings. Declare hooks in code (ksa.on, ksa.every, ksa.service, ksa.command) or add one here.");
            return;
        }

        if (!ImGui.BeginTable("##bind_table", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Borders))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 34f);
            ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Trigger", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Calls", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Options", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 130f);
            ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableHeadersRow();

            // Code-declared rows: greyed, read-only.
            for (int i = 0; i < declared.Count; i++)
            {
                Binding b = declared[i];
                using (new IdScope($"code{i}"))
                using (new ColorScope(ImGuiCol.Text, ImGuiWidgets.MutedColor))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    bool on = b.Enabled;
                    using (new DisabledScope(true))
                    {
                        ImGui.Checkbox("##on", ref on);
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(BindingKinds.ToText(b.Kind));
                    ImGui.TableNextColumn();
                    ImGui.Text(b.Key);
                    ImGui.TableNextColumn();
                    ImGui.Text(b.Export);
                    ImGui.TableNextColumn();
                    ImGuiWidgets.TextWrapped(DescribeOptions(b.Kind, b.Key, b.Options));
                    ImGui.TableNextColumn();
                    ImGui.Text("declared in code");
                    ImGui.TableNextColumn();
                }
            }

            // UI rows: enable checkbox + remove.
            for (int i = 0; i < mine.Count; i++)
            {
                BindingRecord r = mine[i];
                using (new IdScope($"ui{i}"))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    bool on = r.Enabled;
                    if (ImGui.Checkbox("##on", ref on))
                    {
                        BindingRecord target = r;
                        bool value = on;
                        EditStored(info.Name, all =>
                        {
                            int index = all.FindIndex(x => SameBinding(x, target));
                            if (index < 0)
                            {
                                return false;
                            }

                            all[index] = all[index] with { Enabled = value };
                            return true;
                        });
                    }

                    ImGuiWidgets.Tooltip("Disabled bindings stay in bindings.toml but are not applied.");
                    ImGui.TableNextColumn();
                    ImGui.Text(r.Kind);
                    ImGui.TableNextColumn();
                    ImGui.Text(r.Key);
                    ImGui.TableNextColumn();
                    bool exportKnown = info.Exports.Contains(r.Export) || info.State is ScriptState.Idle;
                    if (exportKnown)
                    {
                        ImGui.Text(r.Export);
                    }
                    else
                    {
                        ImGuiWidgets.TextColored(ImGuiWidgets.WarningColor, r.Export);
                        ImGuiWidgets.Tooltip("The loaded script does not export this function (renamed or removed?).");
                    }

                    ImGui.TableNextColumn();
                    ImGuiWidgets.TextWrapped(DescribeOptions(r.Kind, r.Key, r.Sync, r.Vessel, r.Clock, r.IntervalSeconds));
                    ImGui.TableNextColumn();
                    DrawStoredStatus(info, r, live);
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton("Remove"))
                    {
                        BindingRecord target = r;
                        EditStored(info.Name, all => all.RemoveAll(x => SameBinding(x, target)) > 0);
                    }
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private static void DrawStoredStatus(ScriptInfo info, BindingRecord r, IReadOnlyList<Binding> live)
    {
        if (!r.Enabled)
        {
            ImGuiWidgets.TextMuted("disabled");
            return;
        }

        bool loaded = info.State is ScriptState.Running or ScriptState.Service;
        if (!loaded)
        {
            ImGuiWidgets.TextMuted("applies on load");
            return;
        }

        foreach (Binding b in live)
        {
            if (!b.FromCode && BindingKinds.ToText(b.Kind) == r.Kind && b.Key == r.Key && b.Export == r.Export)
            {
                ImGuiWidgets.TextColored(ImGuiWidgets.SuccessColor, "active");
                return;
            }
        }

        ImGuiWidgets.TextColored(ImGuiWidgets.WarningColor, "not applied");
        ImGuiWidgets.Tooltip("The script is loaded but this binding is not registered. Check the script's last error (Overview) or Reload it.");
    }

    // ------------------------------------------------------------------------------ add dialog

    private static IReadOnlyList<(string Key, string Label)> EventItems()
    {
        if (s_eventItems is not null)
        {
            return s_eventItems;
        }

        var items = new List<(string Key, string Label)>(EventCatalog.All.Count);
        foreach (EventDescriptor ev in EventCatalog.All)
        {
            items.Add((ev.Name, $"{ev.Name}  -  {Shorten(ev.Description, 70)}"));
        }

        s_eventItems = items;
        return items;
    }

    private static string Shorten(string text, int max)
    {
        int cut = text.IndexOfAny(['.', ';']);
        if (cut > 0)
        {
            text = text[..cut];
        }

        return text.Length <= max ? text : text[..(max - 1)] + "~";
    }

    private void PrepareAddBinding()
    {
        _abKind = 0;
        _abEvent = null;
        _abEventFilter.Clear();
        _abEventCustom.Clear();
        _abTickKind = 0;
        _abInterval = 1f;
        _abClock = 0;
        _abCommand.Clear();
        _abExport = null;
        _abExportFilter.Clear();
        _abSync = false;
        _abVesselMode = 0;
        _abVessel.Select(null);
        _abError = "";

        // Convenience: pre-select the conventional export names a template creates.
        ScriptInfo? info = FindScript(_abScript);
        if (info is not null)
        {
            foreach (string preferred in new[] { "onEvent", "on_event" })
            {
                if (info.Exports.Contains(preferred))
                {
                    _abExport = preferred;
                    break;
                }
            }

            if (_abExport is null && info.Exports.Count == 1)
            {
                _abExport = info.Exports[0];
            }
        }
    }

    private ScriptInfo? FindScript(string name)
    {
        for (int i = 0; i < _scripts.Count; i++)
        {
            if (string.Equals(_scripts[i].Name, name, StringComparison.Ordinal))
            {
                return _scripts[i];
            }
        }

        return null;
    }

    private void DrawAddBindingDialog()
    {
        ImGuiWidgets.SetNextModalWidth(620f);
        bool open = true;
        if (!ImGui.BeginPopupModal(AddBindingPopup, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        try
        {
            ImGuiWidgets.NoteTextInput();
            ScriptInfo? info = FindScript(_abScript);
            ImGui.AlignTextToFramePadding();
            ImGuiWidgets.TextMuted("Script:");
            ImGui.SameLine();
            ImGui.Text(_abScript);

            if (info is null)
            {
                ImGuiWidgets.TextColored(ImGuiWidgets.ErrorColor, "The script no longer exists.");
                if (ImGui.Button("Close##ab_close_missing"))
                {
                    ImGui.CloseCurrentPopup();
                }

                return;
            }

            if (ImGuiWidgets.BeginFormTableFixed("##ab_form", 100f))
            {
                try
                {
                    DrawAddBindingKind();
                    DrawAddBindingExport(info);
                    DrawAddBindingOptions();
                }
                finally
                {
                    ImGuiWidgets.EndFormTable();
                }
            }

            if (_abKind == 0)
            {
                DrawEventInfoPanel();
            }

            if (_abError.Length > 0)
            {
                ImGuiWidgets.TextColoredWrapped(ImGuiWidgets.ErrorColor, _abError);
            }

            ImGui.Separator();
            if (ImGui.Button("Save##ab_save", new float2(140f, 0f)))
            {
                if (TrySaveNewBinding(info))
                {
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##ab_cancel", new float2(120f, 0f)))
            {
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void DrawAddBindingKind()
    {
        ImGuiWidgets.FormRow("Kind");
        if (ImGui.BeginCombo("##ab_kind", KindLabels[_abKind]))
        {
            try
            {
                for (int i = 0; i < KindLabels.Length; i++)
                {
                    if (ImGui.Selectable($"{KindLabels[i]}##ab_kind{i}", i == _abKind))
                    {
                        _abKind = i;
                        _abError = "";
                    }
                }
            }
            finally
            {
                ImGui.EndCombo();
            }
        }

        switch (_abKind)
        {
            case 0:
                ImGuiWidgets.FormRow("Event");
                if (ImGuiWidgets.FilterCombo("##ab_event", _abEvent ?? "(choose an event)", _abEventFilter, EventItems, out string? picked) && picked is not null)
                {
                    _abEvent = picked;
                    _abEventCustom.Clear();
                    _abError = "";
                }

                ImGuiWidgets.FormRow("or custom");
                ImGuiWidgets.InputWithHint("##ab_event_custom", "a custom or targeted event name", _abEventCustom);
                break;

            case 1:
                ImGuiWidgets.FormRow("Tick");
                ImGui.RadioButton("frame##ab_tick0", ref _abTickKind, 0);
                ImGui.SameLine();
                ImGui.RadioButton("step##ab_tick1", ref _abTickKind, 1);
                ImGui.SameLine();
                ImGui.RadioButton("every N seconds##ab_tick2", ref _abTickKind, 2);
                if (_abTickKind == 2)
                {
                    ImGuiWidgets.FormRow("Interval");
                    ImGui.DragFloat("##ab_interval", ref _abInterval, 0.05f, 0.01f, 3600f, "%.2f s", ImGuiSliderFlags.AlwaysClamp);
                    ImGuiWidgets.FormRow("Clock");
                    if (ImGui.BeginCombo("##ab_clock", ClockLabels[_abClock]))
                    {
                        try
                        {
                            for (int i = 0; i < ClockLabels.Length; i++)
                            {
                                if (ImGui.Selectable($"{ClockLabels[i]}##ab_clock{i}", i == _abClock))
                                {
                                    _abClock = i;
                                }
                            }
                        }
                        finally
                        {
                            ImGui.EndCombo();
                        }
                    }
                }

                break;

            case 2:
                ImGuiWidgets.FormRow("Service");
                ImGuiWidgets.TextMuted("The function runs as a service whenever the script loads.");
                break;

            default:
                ImGuiWidgets.FormRow("Command");
                ImGuiWidgets.InputWithHint("##ab_command", "console command name (no spaces)", _abCommand);
                break;
        }
    }

    private void DrawAddBindingExport(ScriptInfo info)
    {
        ImGuiWidgets.FormRow("Function");
        IReadOnlyList<string> exports = info.Exports;
        if (ImGuiWidgets.FilterCombo(
                "##ab_export",
                _abExport ?? "(choose an exported function)",
                _abExportFilter,
                () => ExportItems(exports),
                out string? picked)
            && picked is not null)
        {
            _abExport = picked;
            _abError = "";
        }

        if (exports.Count == 0)
        {
            ImGuiWidgets.FormText("", "Load the script to discover exports (Run, Start or Reload in the toolbar).", ImGuiWidgets.WarningColor);
        }
    }

    private static List<(string Key, string Label)> ExportItems(IReadOnlyList<string> exports)
    {
        var items = new List<(string Key, string Label)>(exports.Count);
        foreach (string export in exports)
        {
            items.Add((export, export));
        }

        return items;
    }

    private void DrawAddBindingOptions()
    {
        if (_abKind != 0)
        {
            return;
        }

        ImGuiWidgets.FormRow("Options");
        ImGui.Checkbox("Run synchronously##ab_sync", ref _abSync);
        ImGuiWidgets.Tooltip(
            $"A sync handler runs inline inside the game call that raised the event, under a tight budget ({_host.Config.SyncHookBudgetMs:0.##} ms; 5 ms by default) and stops if it exceeds it. "
            + "It must not wait (no sleep / await) and it delays the game while it runs. Leave it off unless you must react before the game continues.");

        ImGuiWidgets.FormRow("Vessel");
        ImGui.RadioButton("any##ab_v0", ref _abVesselMode, 0);
        ImGui.SameLine();
        ImGui.RadioButton("controlled##ab_v1", ref _abVesselMode, 1);
        ImGui.SameLine();
        ImGui.RadioButton("pick a vessel##ab_v2", ref _abVesselMode, 2);
        if (_abVesselMode == 2)
        {
            ImGuiWidgets.FormRow("");
            _abVessel.DefaultToActive(_bridge);
            _abVessel.Draw(_bridge, ImGui.GetTime());
        }
    }

    private void DrawEventInfoPanel()
    {
        string name = _abEventCustom.ToString().Trim();
        if (name.Length == 0)
        {
            name = _abEvent ?? "";
        }

        if (name.Length == 0)
        {
            return;
        }

        ImGui.Spacing();
        EventDescriptor? ev = EventCatalog.Find(name);
        bool visible = ImGui.BeginChild("##ab_event_info", new float2(0f, 0f), ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY);
        try
        {
            if (!visible)
            {
                return;
            }

            ImGuiWidgets.TextColored(ImGuiWidgets.InfoColor, name);
            if (ev is null)
            {
                ImGuiWidgets.TextMuted("Not in the event catalog: it will only fire if something publishes an event with this exact name.");
                return;
            }

            ImGuiWidgets.TextWrapped(ev.Description);
            string fields = ev.PayloadFields.Count == 0 ? "(none)" : string.Join(", ", ev.PayloadFields);
            ImGuiWidgets.TextMuted($"payload: {fields}    producer: {ev.Producer}");
            long count = _host.Events.Counters.TryGetValue(ev.Name, out long n) ? n : 0;
            ImGuiWidgets.TextMuted($"fired {count} time(s) this session");
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private bool TrySaveNewBinding(ScriptInfo info)
    {
        _abError = "";
        string export = _abExport ?? "";
        if (export.Length == 0)
        {
            _abError = "Choose the exported function to call.";
            return false;
        }

        string kind = KindTexts[Math.Clamp(_abKind, 0, KindTexts.Length - 1)];
        string key;
        string? vessel = null;
        bool sync = false;
        string clock = "ut";
        double interval = 0;

        switch (_abKind)
        {
            case 0:
                string custom = _abEventCustom.ToString().Trim();
                key = custom.Length > 0 ? custom : _abEvent ?? "";
                if (key.Length == 0)
                {
                    _abError = "Choose an event (or type a custom event name).";
                    return false;
                }

                sync = _abSync;
                if (_abVesselMode == 1)
                {
                    vessel = "active";
                }
                else if (_abVesselMode == 2)
                {
                    vessel = _abVessel.SelectedId;
                    if (vessel is null)
                    {
                        _abError = "Pick a vessel for the vessel filter (or choose any / controlled).";
                        return false;
                    }
                }

                break;

            case 1:
                key = TickKeys[Math.Clamp(_abTickKind, 0, TickKeys.Length - 1)];
                if (_abTickKind == 2)
                {
                    interval = Math.Max(0.01, Math.Round(_abInterval, 3));
                    clock = ClockTexts[Math.Clamp(_abClock, 0, ClockTexts.Length - 1)];
                }

                break;

            case 2:
                key = export;
                break;

            default:
                key = _abCommand.ToString().Trim();
                if (key.Length == 0 || key.Any(char.IsWhiteSpace))
                {
                    _abError = "Enter a console command name without spaces.";
                    return false;
                }

                break;
        }

        var record = new BindingRecord(info.Name, export, kind, key, sync, vessel, clock, interval, Enabled: true);
        bool duplicate = false;
        foreach (BindingRecord existing in _host.Bindings.Load())
        {
            if (existing.Script == record.Script && existing.Export == record.Export && existing.Kind == record.Kind && existing.Key == record.Key)
            {
                duplicate = true;
                break;
            }
        }

        if (duplicate)
        {
            _abError = "The same binding already exists for this script.";
            return false;
        }

        EditStored(info.Name, all =>
        {
            all.Add(record);
            return true;
        });
        ShowToast(
            $"Binding added: {record.Kind} {record.Key} -> {record.Export}"
            + (info.State is ScriptState.Running or ScriptState.Service ? "." : ". It is applied when the script loads."),
            error: false);
        return true;
    }
}
