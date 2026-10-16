// Layout helpers and the filter combo are adapted from purrTTY (meow sci) purrTTY.GameMod/UI/ImGuiWidgets.cs.
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace KsaScripting.GameMod.UI;

/// <summary>
///     Reusable ImGui widgets, layout helpers and scope guards for the Script Manager, following the
///     KSA mod layout conventions (label/widget tables with <c>NoPadOuterX</c> and uniform cell padding,
///     fixed-width modals).
/// </summary>
/// <remarks>
///     Trap this class exists to hide: in Brutal.ImGui only <c>ImGui.Text</c> is unformatted. <c>TextColored</c>,
///     <c>TextDisabled</c>, <c>TextWrapped</c>, <c>LabelText</c>, <c>BulletText</c> and <c>SetTooltip</c> pass
///     their text to Dear ImGui as a printf format string, so dynamic text (script names, error messages,
///     log lines) containing <c>%</c> would be misread. Every dynamic string goes through the helpers below,
///     which use <c>ImGui.Text</c> under a pushed colour.
/// </remarks>
internal static class ImGuiWidgets
{
    /// <summary>Status text color for errors.</summary>
    public static readonly float4 ErrorColor = new(1f, 0.3f, 0.3f, 1f);

    /// <summary>Status text color for success.</summary>
    public static readonly float4 SuccessColor = new(0.4f, 1f, 0.4f, 1f);

    /// <summary>Status text color for warnings.</summary>
    public static readonly float4 WarningColor = new(1f, 0.8f, 0.2f, 1f);

    /// <summary>Dimmed text (hints, secondary values).</summary>
    public static readonly float4 MutedColor = new(0.62f, 0.62f, 0.66f, 1f);

    /// <summary>Accent for informational values.</summary>
    public static readonly float4 InfoColor = new(0.45f, 0.78f, 1f, 1f);

    // ------------------------------------------------------------------------------ hotkey guard input

    /// <summary>
    ///     True once a focused text input of the Script Manager (window, modal or dropdown) was seen this frame;
    ///     reset by the window at the start of every frame and published as <c>WantsTextInput</c> at its end.
    ///     Game-thread only.
    /// </summary>
    public static bool FrameWantsText { get; set; }

    /// <summary>
    ///     Call inside a Begin/End (or BeginPopup/EndPopup) block: records whether this window, or a child of
    ///     it, is focused while ImGui wants text input. Popups are separate windows, so each one reports itself.
    /// </summary>
    public static void NoteTextInput()
    {
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.GetIO().WantTextInput)
        {
            FrameWantsText = true;
        }
    }

    // ------------------------------------------------------------------------------ text

    /// <summary>Unformatted coloured text (safe for any content).</summary>
    public static void TextColored(float4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        try
        {
            ImGui.Text(text);
        }
        finally
        {
            ImGui.PopStyleColor();
        }
    }

    /// <summary>Unformatted dimmed text; long text wraps at the content edge.</summary>
    public static void TextMuted(string text) => TextColoredWrapped(MutedColor, text);

    /// <summary>Unformatted text wrapped at the window's content edge.</summary>
    public static void TextWrapped(string text)
    {
        ImGui.PushTextWrapPos(0f);
        try
        {
            ImGui.Text(text);
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>Unformatted coloured text wrapped at the window's content edge.</summary>
    public static void TextColoredWrapped(float4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.PushTextWrapPos(0f);
        try
        {
            ImGui.Text(text);
        }
        finally
        {
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }
    }

    /// <summary>
    ///     Shows <paramref name="text"/> as a tooltip while the previous item is hovered (also when it is
    ///     disabled). Unformatted, wrapped at about 40 characters.
    /// </summary>
    public static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            return;
        }

        if (!ImGui.BeginTooltip())
        {
            return;
        }

        try
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 40f);
            try
            {
                ImGui.Text(text);
            }
            finally
            {
                ImGui.PopTextWrapPos();
            }
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    /// <summary>A "(?)" marker that shows <paramref name="text"/> when hovered; draws on the same line.</summary>
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        TextMuted("(?)");
        Tooltip(text);
    }

    /// <summary>Makes an id/label safe for ImGui by defusing the <c>##</c> id separator inside user data.</summary>
    public static string Safe(string text) => text.Contains("##", StringComparison.Ordinal) ? text.Replace("##", "# #", StringComparison.Ordinal) : text;

    // ------------------------------------------------------------------------------ text input

    /// <summary>
    ///     <c>InputText</c> that refreshes <see cref="ImInputString.Length"/> every frame. BRUTAL only
    ///     updates the length on a <i>true</i> return, so without this the buffer would read empty while
    ///     the user types (and we deliberately do not pass <c>EnterReturnsTrue</c>).
    /// </summary>
    public static bool Input(string label, ImInputString buffer, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        bool changed = ImGui.InputText(label, buffer, flags);
        buffer.EvaluateLength();
        return changed;
    }

    /// <summary><see cref="Input"/> with a greyed placeholder hint.</summary>
    public static bool InputWithHint(string label, string hint, ImInputString buffer, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        bool changed = ImGui.InputTextWithHint(label, hint, buffer, flags);
        buffer.EvaluateLength();
        return changed;
    }

    /// <summary>Sets a buffer to <paramref name="text"/>, truncating to its capacity (never throws).</summary>
    public static void SetText(ImInputString buffer, string text)
    {
        int max = buffer.Capacity - 1;
        while (System.Text.Encoding.UTF8.GetByteCount(text) > max && text.Length > 0)
        {
            text = text[..^1];
        }

        buffer.SetValue(text.AsSpan());
    }

    // ------------------------------------------------------------------------------ layout

    /// <summary>
    ///     Pins a modal's WIDTH every frame (height auto-fits) so it never shrinks to
    ///     its content — the fixed-size idiom from the KSA mods. Call immediately
    ///     before <c>BeginPopupModal(..., AlwaysAutoResize)</c>.
    /// </summary>
    public static void SetNextModalWidth(float width)
        => ImGui.SetNextWindowSize(new float2(width, 0f), ImGuiCond.Always);

    /// <summary>
    ///     Begins a 2-column label/widget form table (label gets ¼ width, the widget
    ///     ¾, matching the KSA convention). Returns false if the table could not open
    ///     (then do NOT emit rows or call <see cref="EndFormTable"/>). Pair each
    ///     <see cref="FormRow"/> with exactly one full-width widget.
    /// </summary>
    public static bool BeginFormTable(string id)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new float2(6f, 6f));
        bool open = ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX);
        if (open)
        {
            ImGui.TableSetupColumn($"{id}_l", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn($"{id}_w", ImGuiTableColumnFlags.WidthStretch, 3f);
        }
        else
        {
            ImGui.PopStyleVar();
        }

        return open;
    }

    /// <summary>
    ///     Like <see cref="BeginFormTable"/> but the label column is a <b>fixed</b> width
    ///     (<paramref name="labelWidth"/>, typically from <see cref="MeasureLabelWidth"/>)
    ///     and the widget column stretches to fill the rest. Pair each <see cref="FormRow"/>
    ///     with one full-width widget, and close with <see cref="EndFormTable"/>. <paramref name="rowPadding"/> is the
    ///     vertical cell padding (use a small value for read-only value lists).
    /// </summary>
    public static bool BeginFormTableFixed(string id, float labelWidth, float rowPadding = 6f)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new float2(6f, rowPadding));
        bool open = ImGui.BeginTable(id, 2, ImGuiTableFlags.NoPadOuterX);
        if (open)
        {
            ImGui.TableSetupColumn($"{id}_l", ImGuiTableColumnFlags.WidthFixed, labelWidth);
            ImGui.TableSetupColumn($"{id}_w", ImGuiTableColumnFlags.WidthStretch, 1f);
        }
        else
        {
            ImGui.PopStyleVar();
        }

        return open;
    }

    /// <summary>Ends a <see cref="BeginFormTable"/> or <see cref="BeginFormTableFixed"/>.</summary>
    public static void EndFormTable()
    {
        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    /// <summary>
    ///     Measures the pixel width a fixed label column needs to fit the widest of
    ///     <paramref name="labels"/> in the current font, plus a small gutter.
    /// </summary>
    public static float MeasureLabelWidth(IReadOnlyList<string> labels)
    {
        float max = 0f;
        for (int i = 0; i < labels.Count; i++)
        {
            float w = ImGui.CalcTextSize(labels[i]).X;
            if (w > max)
            {
                max = w;
            }
        }

        return max + 16f;
    }

    /// <summary>
    ///     Emits a label cell (frame-aligned) and advances to the full-width widget
    ///     cell. Draw exactly one widget immediately after.
    /// </summary>
    public static void FormRow(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
    }

    /// <summary>A form row whose widget cell holds plain text (not a widget).</summary>
    public static void FormText(string label, string value, float4? color = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.TableNextColumn();
        if (color is { } c)
        {
            TextColoredWrapped(c, value);
        }
        else
        {
            TextWrapped(value);
        }
    }

    // ------------------------------------------------------------------------------ buttons

    /// <summary>
    ///     A red, clearly-destructive button (KSA "Destroy"-style). Returns true when
    ///     clicked. Pass <paramref name="size"/> to size it; omit for auto-fit.
    /// </summary>
    public static bool DestructiveButton(string label, float2? size = null)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(new float4(0.70f, 0.18f, 0.18f, 1f)));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(new float4(0.85f, 0.25f, 0.25f, 1f)));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGui.GetColorU32(new float4(0.60f, 0.12f, 0.12f, 1f)));
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(new float4(0.97f, 0.97f, 0.97f, 1f)));
        try
        {
            return ImGui.Button(label, size);
        }
        finally
        {
            ImGui.PopStyleColor(4);
        }
    }

    /// <summary>A button that is greyed out and inert while <paramref name="enabled"/> is false; optional tooltip for the disabled state.</summary>
    public static bool Button(string label, bool enabled = true, string? disabledReason = null, float2? size = null)
    {
        ImGui.BeginDisabled(!enabled);
        bool clicked;
        try
        {
            clicked = ImGui.Button(label, size);
        }
        finally
        {
            ImGui.EndDisabled();
        }

        if (!enabled && disabledReason is not null)
        {
            Tooltip(disabledReason);
        }

        return clicked && enabled;
    }

    // ------------------------------------------------------------------------------ combos

    /// <summary>
    ///     A combo box whose first row is a live text filter: type to narrow the list
    ///     by case-insensitive substring of each item's label. Returns <c>true</c> on
    ///     the frame an item is chosen (with <paramref name="selectedKey"/> set to that
    ///     item's key); otherwise <c>false</c> and <paramref name="selectedKey"/> is
    ///     null. The caller owns <paramref name="filter"/> (one persistent
    ///     <see cref="ImInputString"/> per combo). <paramref name="itemsProvider"/> is only invoked
    ///     while the dropdown is open, so building the items may be expensive.
    /// </summary>
    /// <param name="id">An ImGui id for the combo (conventionally starting with <c>##</c>).</param>
    /// <param name="preview">The collapsed-combo preview text (typically the current selection).</param>
    /// <param name="filter">Caller-owned filter buffer, persisted across frames.</param>
    /// <param name="itemsProvider">Selectable items as (stable key, display label) pairs.</param>
    /// <param name="selectedKey">The chosen item's key when this returns true; else null.</param>
    public static bool FilterCombo(
        string id,
        string preview,
        ImInputString filter,
        Func<IReadOnlyList<(string Key, string Label)>> itemsProvider,
        out string? selectedKey)
    {
        selectedKey = null;

        if (!ImGui.BeginCombo(id, Safe(preview)))
        {
            return false;
        }

        bool chose = false;
        try
        {
            NoteTextInput();

            // Focus the filter box the frame the popup opens so the user can type at once.
            if (ImGui.IsWindowAppearing())
            {
                ImGui.SetKeyboardFocusHere();
            }

            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText($"{id}_filter", filter, ImGuiInputTextFlags.None);
            filter.EvaluateLength();
            string needle = filter.ToString().Trim();

            ImGui.Separator();

            IReadOnlyList<(string Key, string Label)> items = itemsProvider();
            for (int i = 0; i < items.Count; i++)
            {
                (string key, string label) = items[i];
                if (needle.Length > 0 && label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                // Disambiguate the ImGui id with the row index so duplicate labels never collide.
                if (ImGui.Selectable($"{Safe(label)}##{id}_{i}", false))
                {
                    selectedKey = key;
                    chose = true;
                }
            }

            if (items.Count == 0)
            {
                TextMuted("(nothing to choose from)");
            }
        }
        finally
        {
            ImGui.EndCombo();
        }

        // Reset the filter once a choice is made so the next open starts from the full list.
        if (chose)
        {
            filter.Clear();
        }

        return chose;
    }

    /// <summary><see cref="FilterCombo(string, string, ImInputString, Func{IReadOnlyList{ValueTuple{string, string}}}, out string?)"/> over a ready list.</summary>
    public static bool FilterCombo(
        string id,
        string preview,
        ImInputString filter,
        IReadOnlyList<(string Key, string Label)> items,
        out string? selectedKey)
        => FilterCombo(id, preview, filter, () => items, out selectedKey);

    // ------------------------------------------------------------------------------ fonts

    /// <summary>
    ///     Pushes KSA's console font (a monospace face) when it is loaded; returns whether a font was pushed
    ///     (pass the result to <see cref="PopMono"/>).
    /// </summary>
    public static bool PushMono()
    {
        try
        {
            ImFontPtr font = KSA.FontManager.ConsoleFont;
            if (font.IsNull())
            {
                return false;
            }

            // The font's own loaded size (what the obsolete ImGuiKsaEx.PushFont shim passes): the base size is unscaled.
            ImGui.PushFont(font, font.LegacySize);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Pops the font pushed by <see cref="PushMono"/>.</summary>
    public static void PopMono(bool pushed)
    {
        if (pushed)
        {
            ImGui.PopFont();
        }
    }
}

/// <summary>Pushes an ImGui id for the scope of a <c>using</c>.</summary>
internal readonly ref struct IdScope
{
    public IdScope(string id) => ImGui.PushID(id);

    public IdScope(int id) => ImGui.PushID(id);

    public void Dispose() => ImGui.PopID();
}

/// <summary>Pushes one style colour for the scope of a <c>using</c>.</summary>
internal readonly ref struct ColorScope
{
    public ColorScope(ImGuiCol slot, float4 color) => ImGui.PushStyleColor(slot, color);

    public void Dispose() => ImGui.PopStyleColor();
}

/// <summary>Disables the widgets drawn inside a <c>using</c> when <c>disabled</c> is true.</summary>
internal readonly ref struct DisabledScope
{
    public DisabledScope(bool disabled) => ImGui.BeginDisabled(disabled);

    public void Dispose() => ImGui.EndDisabled();
}

/// <summary>Pushes the monospace console font for the scope of a <c>using</c> (no-op when it is not loaded).</summary>
internal readonly ref struct MonoScope
{
    private readonly bool _pushed;

    public MonoScope() => _pushed = ImGuiWidgets.PushMono();

    public void Dispose() => ImGuiWidgets.PopMono(_pushed);
}
