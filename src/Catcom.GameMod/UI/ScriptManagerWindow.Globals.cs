using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Catcom.Core.Hosting;
using Catcom.Core.Hooks;
using Catcom.Core.Runtime;
using Catcom.Core.Scheduling;
using Catcom.Core.Snapshots;
using Catcom.Core.Terminals;
using Catcom.GameMod.Editor;
using Catcom.Ksa.Events;

namespace Catcom.GameMod.UI;

// The bottom section: Events | Runtimes | Settings.
public sealed partial class ScriptManagerWindow
{
    private const double CountersRefreshSeconds = 0.25;

    private readonly ImInputString _eventFilter = new(64);
    private IReadOnlyDictionary<string, long> _counters = new Dictionary<string, long>();
    private double _countersAt = double.NegativeInfinity;

    private void DrawGlobals()
    {
        if (!ImGui.BeginTabBar("##globaltabs", ImGuiTabBarFlags.None))
        {
            return;
        }

        try
        {
            DrawGlobalTab("Events", "events tab", DrawEventsTab);
            DrawGlobalTab("Runtimes", "runtimes tab", DrawRuntimesTab);
            DrawGlobalTab("Settings", "settings tab", DrawSettingsTab);
            _requestGlobalTab = null;
        }
        finally
        {
            ImGui.EndTabBar();
        }
    }

    /// <summary>Selects one of the global tabs (Events, Runtimes, Settings) on the next frame.</summary>
    internal void RequestGlobalTab(string label) => _requestGlobalTab = label;

    private void DrawGlobalTab(string label, string key, Action body)
    {
        ImGuiTabItemFlags flags = string.Equals(_requestGlobalTab, label, StringComparison.Ordinal) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
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

    private IReadOnlyDictionary<string, long> LiveCounters()
    {
        double now = ImGui.GetTime();
        if (now - _countersAt >= CountersRefreshSeconds)
        {
            _counters = _host.Events.Counters;
            _countersAt = now;
        }

        return _counters;
    }

    // ------------------------------------------------------------------------------ events

    private void DrawEventsTab()
    {
        IReadOnlyDictionary<string, long> counters = LiveCounters();

        ImGui.SetNextItemWidth(220f);
        ImGuiWidgets.InputWithHint("##ev_filter", "filter events...", _eventFilter);
        ImGui.SameLine();
        ImGuiWidgets.TextMuted($"{EventCatalog.All.Count} events; {_host.Events.PendingCount} queued for delivery. Counts are for this session.");

        string needle = _eventFilter.ToString().Trim();
        float height = Math.Max(80f, ImGui.GetContentRegionAvail().Y);
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY
                                      | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##events", 5, flags, new float2(0f, height)))
        {
            return;
        }

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Event", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Producer", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Payload", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch, 6f);
            ImGui.TableSetupColumn("Fired", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableHeadersRow();

            foreach (EventDescriptor ev in EventCatalog.All)
            {
                if (needle.Length > 0
                    && ev.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                    && ev.Description.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                    && ev.Producer.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(ev.Name);
                ImGui.TableNextColumn();
                ImGuiWidgets.TextMuted(ev.Producer);
                ImGui.TableNextColumn();
                ImGuiWidgets.TextMuted(ev.PayloadFields.Count == 0 ? "-" : string.Join(", ", ev.PayloadFields));
                ImGui.TableNextColumn();
                ImGuiWidgets.TextWrapped(ev.Description);
                ImGui.TableNextColumn();
                long count = counters.TryGetValue(ev.Name, out long n) ? n : 0;
                if (count > 0)
                {
                    ImGuiWidgets.TextColored(ImGuiWidgets.SuccessColor, count.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    ImGuiWidgets.TextMuted("0");
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    // ------------------------------------------------------------------------------ runtimes

    private void DrawRuntimesTab()
    {
        ImGui.SeparatorText("Script runtimes");
        if (ImGui.BeginTable("##rt_table", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            try
            {
                ImGui.TableSetupColumn("Runtime", ImGuiTableColumnFlags.WidthFixed, 130f);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("Heap", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupColumn("JIT", ImGuiTableColumnFlags.WidthFixed, 40f);
                ImGui.TableSetupColumn("Calls", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Last call", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableHeadersRow();

                foreach (RuntimeStatus status in _host.RuntimeStatuses)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text($"{status.Id} ({status.Language})");
                    ImGui.TableNextColumn();
                    if (status.Available)
                    {
                        ImGuiWidgets.TextColored(ImGuiWidgets.SuccessColor, "available");
                    }
                    else
                    {
                        ImGuiWidgets.TextColoredWrapped(ImGuiWidgets.ErrorColor, $"unavailable: {status.Reason ?? "unknown reason"}");
                    }

                    IScriptRuntime? runtime = status.Available ? _host.Runtimes.ForId(status.Id) : null;
                    if (runtime is { IsAvailable: true })
                    {
                        RuntimeStats stats = runtime.Stats;
                        ImGui.TableNextColumn();
                        ImGui.Text(stats.Version);
                        ImGui.TableNextColumn();
                        ImGui.Text(FormatBytes(stats.HeapBytes));
                        ImGui.TableNextColumn();
                        ImGui.Text(stats.JitEnabled ? "on" : "off");
                        ImGui.TableNextColumn();
                        ImGui.Text(stats.CallsTotal.ToString(CultureInfo.InvariantCulture));
                        ImGui.TableNextColumn();
                        ImGui.Text($"{stats.LastCallMs:F3} ms");
                    }
                    else
                    {
                        for (int c = 0; c < 5; c++)
                        {
                            ImGui.TableNextColumn();
                            ImGuiWidgets.TextMuted("-");
                        }
                    }
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        ImGuiWidgets.TextMuted("Enable or disable a runtime, heap and memory limits: Settings tab (take effect after a restart).");

        Guard("scheduler stats", DrawSchedulerStats);
        Guard("slot health", DrawSlotHealth);
        Guard("editor sessions", DrawEditorSessions);
        Guard("accessor health", DrawAccessorHealth);
    }

    private void DrawSchedulerStats()
    {
        ScheduleStats s = _host.Scheduler.Stats;
        ImGui.SeparatorText("Scheduler");
        if (!ImGuiWidgets.BeginFormTableFixed("##sch_form", 170f, 2f))
        {
            return;
        }

        try
        {
            ImGuiWidgets.FormText("Step slots run", s.Slots.ToString(CultureInfo.InvariantCulture));
            ImGuiWidgets.FormText(
                "Slot time (last / max)",
                $"{s.LastSlotMs:F3} ms / {s.MaxSlotMs:F3} ms   (budget {_host.Config.FrameBudgetMs:0.###} ms)");
            ImGuiWidgets.FormText("Script invocations", s.Invocations.ToString(CultureInfo.InvariantCulture));
            ImGuiWidgets.FormText("Faults", s.Faults.ToString(CultureInfo.InvariantCulture), s.Faults > 0 ? ImGuiWidgets.WarningColor : null);
            ImGuiWidgets.FormText("Budget overruns", s.BudgetOverruns.ToString(CultureInfo.InvariantCulture), s.BudgetOverruns > 0 ? ImGuiWidgets.WarningColor : null);
            ImGuiWidgets.FormText("Pending waits", s.PendingWaits.ToString(CultureInfo.InvariantCulture));
            ImGuiWidgets.FormText("Pending events", s.PendingEvents.ToString(CultureInfo.InvariantCulture));
            ImGuiWidgets.FormText("Watchdog trips", s.WatchdogTrips.ToString(CultureInfo.InvariantCulture), s.WatchdogTrips > 0 ? ImGuiWidgets.WarningColor : null);
        }
        finally
        {
            ImGuiWidgets.EndFormTable();
        }
    }

    private void DrawSlotHealth()
    {
        ImGui.SeparatorText("Game slots and event patches");
        if (!ImGuiWidgets.BeginFormTableFixed("##slot_form", 170f, 2f))
        {
            return;
        }

        try
        {
            var slots = _host.Slots;
            if (slots is null)
            {
                ImGuiWidgets.FormText("Step slot", "not created (see the log)", ImGuiWidgets.ErrorColor);
            }
            else
            {
                ImGuiWidgets.FormText(
                    "Step slot",
                    slots.Faulted ? "FAULTED - scripts are no longer driven (see the log)" : "healthy",
                    slots.Faulted ? ImGuiWidgets.ErrorColor : ImGuiWidgets.SuccessColor);
                ImGuiWidgets.FormText("Steps / last step", $"{slots.Steps} / {slots.LastStepMs:F3} ms");
                ImGuiWidgets.FormText("Slot patches", $"{slots.Installed.Count} installed, {slots.Failed.Count} failed", slots.Failed.Count > 0 ? ImGuiWidgets.WarningColor : null);
                if (slots.Failed.Count > 0)
                {
                    ImGuiWidgets.FormText("Failed slot patches", string.Join(", ", slots.Failed), ImGuiWidgets.WarningColor);
                }
            }

            IReadOnlyList<string> installed = EventPatches.Installed;
            IReadOnlyList<string> failed = EventPatches.Failed;
            ImGuiWidgets.FormText("Event patches", $"{installed.Count} installed, {failed.Count} failed", failed.Count > 0 ? ImGuiWidgets.WarningColor : null);
            if (failed.Count > 0)
            {
                ImGuiWidgets.FormText("Failed event patches", string.Join(", ", failed), ImGuiWidgets.WarningColor);
            }
        }
        finally
        {
            ImGuiWidgets.EndFormTable();
        }
    }

    private void DrawEditorSessions()
    {
        IReadOnlyList<EditorSessionInfo> sessions = _host.Editor.Sessions;
        ImGui.SeparatorText($"Editor sessions ({sessions.Count})");
        if (!_host.Editor.IsAvailable)
        {
            ImGuiWidgets.TextMuted($"Editor unavailable: {_host.Editor.UnavailableReason ?? "unknown reason"}");
        }

        if (sessions.Count == 0)
        {
            ImGuiWidgets.TextMuted("None open.");
            return;
        }

        foreach (EditorSessionInfo session in sessions)
        {
            ImGui.Text($"{session.ScriptName}   port {session.Port}   {(session.Connected ? "connected" : "connecting...")}");
        }
    }

    private void DrawAccessorHealth()
    {
        IReadOnlyList<AccessorHealthSnapshot> degraded = _host.Health.Snapshot();
        ImGui.SeparatorText($"KSA accessors degraded ({degraded.Count})");
        if (degraded.Count == 0)
        {
            ImGuiWidgets.TextMuted("None - every reader and actuator is working.");
            return;
        }

        foreach (AccessorHealthSnapshot a in degraded)
        {
            ImGuiWidgets.TextColoredWrapped(ImGuiWidgets.WarningColor, $"{a.Name}: {a.Error}");
        }
    }
}
