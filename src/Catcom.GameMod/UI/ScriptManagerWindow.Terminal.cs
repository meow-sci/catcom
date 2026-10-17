using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Catcom.Core.Catalog;
using Catcom.Core.Terminals;
using Catcom.GameMod.Terminals;

namespace Catcom.GameMod.UI;

// Terminal tab: put the script's terminal on a purrTTY 2D window or on a vehicle part, manage the surfaces, preview the output.
public sealed partial class ScriptManagerWindow
{
    // The in-world quad's default aspect (0.6 m x 0.355 m).
    private const float QuadAspect = 1.69f;
    private const int PreviewTailBytes = 8192;
    private const double PreviewRefreshSeconds = 0.3;

    // 2D window
    private int _winCols = 80;
    private int _winRows = 24;
    private readonly ImInputString _winTitle = new(64);
    private readonly ImInputString _winTheme = new(64);
    private bool _winPersist = true;

    // in-world placement
    private readonly VesselPicker _placeVessel = new("##place_vessel");
    private readonly PartPicker _placePart = new("##place_part");
    private float3 _placeOffset = new(0f, 0f, 0.05f);
    private float3 _placeRotation = new(0f, 0f, 0f);
    private float _placeWidth = 0.6f;
    private float _placeHeight = 0.355f;
    private bool _placeKeepAspect = true;
    private int _placeCols = 40;
    private int _placeRows = 12;
    private readonly ImInputString _placeTheme = new(64);
    private bool _placeUseOpacity;
    private float _placeOpacity = 0.85f;
    private bool _placePersist = true;
    private string? _placeVesselSeen;

    // preview cache
    private string _previewScript = "";
    private double _previewAt = double.NegativeInfinity;
    private string _previewText = "";

    private void DrawTerminalTab(ScriptInfo info)
    {
        TerminalPlacer placer = _host.Placer;
        bool available = placer.IsAvailable;
        ScriptTerminal? terminal = _host.Terminals.ForScript(info.Name);

        ImGuiWidgets.TextWrapped(
            "A script's terminal exists while the script runs and its header says  @ksa terminal=true  (Start it first). "
            + "Windows and part quads are purrTTY surfaces attached to that terminal; several can show it at once.");

        if (!info.HasTerminal)
        {
            ImGuiWidgets.TextColoredWrapped(
                ImGuiWidgets.WarningColor,
                "This script's header does not say  terminal=true,  so it will not get a terminal when started.");
        }

        if (!available)
        {
            ImGui.Spacing();
            ImGuiWidgets.TextColoredWrapped(ImGuiWidgets.ErrorColor, $"Terminal surfaces are unavailable: {placer.UnavailableReason ?? "the purrTTY launcher is missing"}");
            ImGuiWidgets.TextMuted("Install purrTTY (with the scripting launcher) to open terminal windows and in-world terminals.");
        }

        ImGui.Spacing();
        ImGuiWidgets.TextMuted(terminal is null
            ? "Terminal: none right now (the script is not running with a terminal)."
            : $"Terminal: {terminal.Cols} x {terminal.Rows}, {terminal.Attached} surface(s) attached"
              + (terminal.Title is { Length: > 0 } title ? $", title '{title}'" : ""));

        using (new DisabledScope(!available))
        {
            Guard("terminal window form", () => DrawWindowForm(info));
            Guard("terminal place form", () => DrawPlaceForm(info));
        }

        Guard("terminal placements", () => DrawPlacements(info));
        Guard("terminal preview", () => DrawTerminalPreview(info, terminal));
    }

    // ------------------------------------------------------------------------------ 2D window

    private void DrawWindowForm(ScriptInfo info)
    {
        ImGui.SeparatorText("Open 2D window");
        if (ImGuiWidgets.BeginFormTableFixed("##win_form", 90f))
        {
            try
            {
                ImGuiWidgets.FormRow("Columns");
                ImGui.DragInt("##win_cols", ref _winCols, 1f, 10, 400, "%d", ImGuiSliderFlags.AlwaysClamp);
                ImGuiWidgets.FormRow("Rows");
                ImGui.DragInt("##win_rows", ref _winRows, 1f, 4, 200, "%d", ImGuiSliderFlags.AlwaysClamp);
                ImGuiWidgets.FormRow("Title");
                ImGuiWidgets.InputWithHint("##win_title", info.Name, _winTitle);
                ImGuiWidgets.FormRow("Theme");
                ImGuiWidgets.InputWithHint("##win_theme", "purrTTY theme name (blank = default)", _winTheme);
                ImGuiWidgets.FormRow("Persist");
                ImGui.Checkbox("Re-open this window whenever the script starts##win_persist", ref _winPersist);
            }
            finally
            {
                ImGuiWidgets.EndFormTable();
            }
        }

        if (ImGui.Button("Open window##win_open"))
        {
            string script = info.Name;
            int cols = _winCols;
            int rows = _winRows;
            string? title = NullIfEmpty(_winTitle.ToString());
            string? theme = NullIfEmpty(_winTheme.ToString());
            bool persist = _winPersist;
            Post("open terminal window", () => _host.Placer.OpenWindow(script, cols, rows, title, theme, persist));
        }
    }

    // ------------------------------------------------------------------------------ place on part

    private void DrawPlaceForm(ScriptInfo info)
    {
        ImGui.SeparatorText("Place on part");
        _placeVessel.DefaultToActive(_bridge);
        double now = ImGui.GetTime();

        if (ImGuiWidgets.BeginFormTableFixed("##place_form", 90f))
        {
            try
            {
                ImGuiWidgets.FormRow("Vessel");
                _placeVessel.Draw(_bridge, now);
                if (_placeVesselSeen != _placeVessel.SelectedId)
                {
                    _placePart.Reset();
                    _placeVesselSeen = _placeVessel.SelectedId;
                }

                ImGuiWidgets.FormRow("Part");
                _placePart.Draw(_bridge, _placeVessel.SelectedId, now);
                if (_placePart.Error is { } partError)
                {
                    ImGuiWidgets.FormText("", partError, ImGuiWidgets.ErrorColor);
                }

                ImGuiWidgets.FormRow("Offset (m)");
                ImGui.DragFloat3("##place_offset", ref _placeOffset, 0.01f, -50f, 50f, "%.3f", ImGuiSliderFlags.AlwaysClamp);
                ImGuiWidgets.FormRow("Rotation (deg)");
                ImGui.DragFloat3("##place_rotation", ref _placeRotation, 1f, -360f, 360f, "%.1f", ImGuiSliderFlags.AlwaysClamp);

                ImGuiWidgets.FormRow("Width (m)");
                if (ImGui.DragFloat("##place_width", ref _placeWidth, 0.01f, 0.05f, 20f, "%.3f", ImGuiSliderFlags.AlwaysClamp) && _placeKeepAspect)
                {
                    _placeHeight = _placeWidth / QuadAspect;
                }

                ImGuiWidgets.FormRow("Height (m)");
                if (ImGui.DragFloat("##place_height", ref _placeHeight, 0.01f, 0.05f, 20f, "%.3f", ImGuiSliderFlags.AlwaysClamp) && _placeKeepAspect)
                {
                    _placeWidth = _placeHeight * QuadAspect;
                }

                ImGuiWidgets.FormRow("Aspect");
                if (ImGui.Checkbox($"Keep the {QuadAspect.ToString("0.00", CultureInfo.InvariantCulture)} : 1 aspect##place_aspect", ref _placeKeepAspect) && _placeKeepAspect)
                {
                    _placeHeight = _placeWidth / QuadAspect;
                }

                ImGuiWidgets.FormRow("Columns");
                ImGui.DragInt("##place_cols", ref _placeCols, 1f, 10, 200, "%d", ImGuiSliderFlags.AlwaysClamp);
                ImGuiWidgets.FormRow("Rows");
                ImGui.DragInt("##place_rows", ref _placeRows, 1f, 4, 100, "%d", ImGuiSliderFlags.AlwaysClamp);
                ImGuiWidgets.FormRow("Theme");
                ImGuiWidgets.InputWithHint("##place_theme", "purrTTY theme name (blank = default)", _placeTheme);

                ImGuiWidgets.FormRow("Background");
                ImGui.Checkbox("Override opacity##place_useopacity", ref _placeUseOpacity);
                if (_placeUseOpacity)
                {
                    ImGuiWidgets.FormRow("Opacity");
                    ImGui.DragFloat("##place_opacity", ref _placeOpacity, 0.01f, 0f, 1f, "%.2f", ImGuiSliderFlags.AlwaysClamp);
                }

                ImGuiWidgets.FormRow("Persist");
                ImGui.Checkbox("Re-create this quad whenever the script starts##place_persist", ref _placePersist);
            }
            finally
            {
                ImGuiWidgets.EndFormTable();
            }
        }

        string? vesselId = _placeVessel.SelectedId;
        uint? partId = _placePart.SelectedInstanceId;
        bool ready = vesselId is not null && partId is not null;
        if (ImGuiWidgets.Button("Place on part##place_go", ready, "Choose a vessel and a part first.") && ready)
        {
            string script = info.Name;
            string vessel = vesselId!;
            uint part = partId!.Value;
            var options = new InWorldPlacementOptions(
                Cols: _placeCols,
                Rows: _placeRows,
                OffsetX: _placeOffset.X,
                OffsetY: _placeOffset.Y,
                OffsetZ: _placeOffset.Z,
                RotationX: _placeRotation.X,
                RotationY: _placeRotation.Y,
                RotationZ: _placeRotation.Z,
                WidthMeters: _placeWidth,
                HeightMeters: _placeHeight,
                Theme: NullIfEmpty(_placeTheme.ToString()),
                BackgroundOpacity: _placeUseOpacity ? _placeOpacity : null);
            bool persist = _placePersist;
            Post("place terminal on part", () => _host.Placer.PlaceOnPart(script, vessel, part, options, persist));
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh parts##place_refresh"))
        {
            _placePart.Refresh();
        }

        ImGuiWidgets.Tooltip("Re-read the vessel's parts the next time the part list is opened.");
    }

    // ------------------------------------------------------------------------------ lists

    private void DrawPlacements(ScriptInfo info)
    {
        IReadOnlyList<TerminalPlacement> placements = _host.Placer.Placements(info.Name);
        IReadOnlyList<PlacedTerminal> live = _host.Placer.Open(info.Name);

        ImGui.SeparatorText($"Saved placements ({placements.Count})");
        if (placements.Count == 0)
        {
            ImGuiWidgets.TextMuted("None. A saved placement is re-created every time the script starts.");
        }
        else if (ImGui.BeginTable("##placements", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Borders))
        {
            try
            {
                ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 3f);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableHeadersRow();
                for (int i = 0; i < placements.Count; i++)
                {
                    TerminalPlacement p = placements[i];
                    using (new IdScope($"pl{i}"))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.Text(p.Kind);
                        ImGui.TableNextColumn();
                        ImGui.Text($"{p.Cols} x {p.Rows}");
                        ImGui.TableNextColumn();
                        string target = p.Kind == "inworld"
                            ? $"vessel {p.VehicleId ?? "?"}, part #{(p.PartInstanceId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "?")}"
                            : p.Title is { Length: > 0 } t ? $"window '{t}'" : "window";
                        if (!string.IsNullOrEmpty(p.Theme))
                        {
                            target += $", theme {p.Theme}";
                        }

                        ImGui.Text(target);
                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton("Remove"))
                        {
                            string script = info.Name;
                            int index = i;
                            Post("remove placement", () => _host.Placer.RemovePlacement(script, index));
                        }
                    }
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        ImGui.SeparatorText($"Open surfaces ({live.Count})");
        if (live.Count == 0)
        {
            ImGuiWidgets.TextMuted("None open.");
            return;
        }

        if (!ImGui.BeginTable("##surfaces", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Borders))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("Surface", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableHeadersRow();
            for (int i = 0; i < live.Count; i++)
            {
                PlacedTerminal t = live[i];
                using (new IdScope($"live{i}"))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(t.IsOpen ? t.Name : $"{t.Name} (closed)");
                    ImGui.TableNextColumn();
                    ImGui.Text(t.Kind);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{t.Cols} x {t.Rows}");
                    ImGui.TableNextColumn();
                    Guid id = t.Id;
                    if (ImGui.SmallButton("Focus"))
                    {
                        Post("focus terminal", () => _host.Placer.Focus(id));
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton("Close"))
                    {
                        Post("close terminal", () => _host.Placer.Close(id));
                    }
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    // ------------------------------------------------------------------------------ preview

    private void DrawTerminalPreview(ScriptInfo info, ScriptTerminal? terminal)
    {
        ImGui.SeparatorText("Output preview");
        if (terminal is null)
        {
            ImGuiWidgets.TextMuted("No terminal. Start the script (toolbar) to see what it prints here.");
            return;
        }

        double now = ImGui.GetTime();
        if (_previewScript != info.Name || now - _previewAt >= PreviewRefreshSeconds)
        {
            byte[] ring = terminal.CopyRingBuffer();
            int take = Math.Min(ring.Length, PreviewTailBytes);
            _previewText = AnsiText.Strip(ring.AsSpan(ring.Length - take, take));
            _previewScript = info.Name;
            _previewAt = now;
        }

        bool visible = ImGui.BeginChild("##term_preview", new float2(0f, 170f), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        try
        {
            if (visible)
            {
                if (_previewText.Length == 0)
                {
                    ImGuiWidgets.TextMuted("(nothing printed yet)");
                }
                else
                {
                    using (new MonoScope())
                    {
                        ImGui.Text(_previewText);
                    }
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        ImGuiWidgets.TextMuted("Last ~2 KiB of output with escape sequences removed; a full-screen app looks rough here - open a window for the real view.");
    }

    private static string? NullIfEmpty(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
