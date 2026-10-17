using Brutal.ImGuiApi;
using Catcom.Core.Logging;
using Catcom.Core.Values;
using Catcom.Core.Hosting;

namespace Catcom.GameMod.UI;

/// <summary>
///     A filterable vessel combo (label <c>name [id]</c>, the controlled vessel marked). The list is read from
///     the game bridge only while the dropdown is open and is cached for a second; names are cached for a few
///     seconds, so an idle picker costs one read of the selected vessel's name.
/// </summary>
internal sealed class VesselPicker
{
    private const double LabelSeconds = 3.0;
    private const double ListSeconds = 1.0;

    private readonly string _id;
    private readonly ImInputString _filter = new(64);
    private readonly Dictionary<string, (string Label, double At)> _labels = new(StringComparer.Ordinal);
    private IReadOnlyList<(string Key, string Label)> _items = [];
    private double _itemsAt = double.NegativeInfinity;

    /// <summary>Creates a picker whose combo uses the ImGui id <paramref name="id"/> (start it with <c>##</c>).</summary>
    public VesselPicker(string id) => _id = id;

    /// <summary>The chosen vessel id, or null.</summary>
    public string? SelectedId { get; private set; }

    /// <summary>Chooses a vessel programmatically.</summary>
    public void Select(string? id) => SelectedId = id;

    /// <summary>The id of the vessel the player controls, or null when none (never throws).</summary>
    public static string? ActiveId(IGameBridge bridge)
    {
        try
        {
            ScriptValue v = bridge.Read("time", "activeVesselId");
            return v.Kind == ScriptValueKind.String && v.AsString().Length > 0 ? v.AsString() : null;
        }
        catch (Exception ex)
        {
            Log.LogOnce("ui.vessel.active", LogLevel.Warn, $"Reading the active vessel failed (logged once): {ex.Message}");
            return null;
        }
    }

    /// <summary>Selects the controlled vessel when nothing is selected yet.</summary>
    public void DefaultToActive(IGameBridge bridge)
    {
        if (SelectedId is null)
        {
            SelectedId = ActiveId(bridge);
        }
    }

    /// <summary>Draws the combo; returns true on the frame the selection changed.</summary>
    public bool Draw(IGameBridge bridge, double now)
    {
        string preview = SelectedId is null ? "(pick a vessel)" : Label(bridge, SelectedId, now);
        if (ImGuiWidgets.FilterCombo(_id, preview, _filter, () => Items(bridge, now), out string? picked) && picked is not null)
        {
            SelectedId = picked;
            return true;
        }

        return false;
    }

    private IReadOnlyList<(string Key, string Label)> Items(IGameBridge bridge, double now)
    {
        if (now - _itemsAt < ListSeconds)
        {
            return _items;
        }

        var items = new List<(string Key, string Label)>();
        try
        {
            string? active = ActiveId(bridge);
            foreach (string id in bridge.List("vessels"))
            {
                string label = Label(bridge, id, now);
                items.Add((id, id == active ? label + "  (controlled)" : label));
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("ui.vessel.list", LogLevel.Warn, $"Listing vessels failed (logged once): {ex.Message}");
        }

        _items = items;
        _itemsAt = now;
        return items;
    }

    private string Label(IGameBridge bridge, string id, double now)
    {
        if (_labels.TryGetValue(id, out var cached) && now - cached.At < LabelSeconds)
        {
            return cached.Label;
        }

        string label = id;
        try
        {
            ScriptValue name = bridge.Read($"vessel:{id}", "name");
            if (name.Kind == ScriptValueKind.String && name.AsString().Length > 0 && name.AsString() != id)
            {
                label = $"{name.AsString()}  [{id}]";
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("ui.vessel.name", LogLevel.Warn, $"Reading a vessel name failed (logged once): {ex.Message}");
        }

        _labels[id] = (label, now);
        return label;
    }
}
