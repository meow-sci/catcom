using System.Globalization;
using Brutal.ImGuiApi;
using Catcom.Core.Logging;
using Catcom.Core.Values;
using Catcom.Core.Hosting;

namespace Catcom.GameMod.UI;

/// <summary>
///     A filterable combo over one vessel's top-level parts, labelled <c>displayName (#instanceId)</c> (part
///     display names are not unique since KSA 5402; <c>Part.InstanceId</c> is the stable address). The parts
///     come from the bridge's vessel snapshot (<c>parts</c>), read only while the dropdown is open or when
///     <see cref="Refresh"/> was requested, and cached for ten seconds.
/// </summary>
internal sealed class PartPicker
{
    private const double CacheSeconds = 10.0;

    private readonly string _id;
    private readonly ImInputString _filter = new(64);
    private IReadOnlyList<(string Key, string Label)> _items = [];
    private string? _itemsVessel;
    private double _itemsAt = double.NegativeInfinity;
    private string? _error;

    /// <summary>Creates a picker whose combo uses the ImGui id <paramref name="id"/> (start it with <c>##</c>).</summary>
    public PartPicker(string id) => _id = id;

    /// <summary>The chosen <c>Part.InstanceId</c>, or null.</summary>
    public uint? SelectedInstanceId { get; private set; }

    /// <summary>The label of the chosen part (shown while the dropdown is closed).</summary>
    public string SelectedLabel { get; private set; } = "";

    /// <summary>Forgets the selection and the cached list (the vessel changed).</summary>
    public void Reset()
    {
        SelectedInstanceId = null;
        SelectedLabel = "";
        _items = [];
        _itemsVessel = null;
        _itemsAt = double.NegativeInfinity;
        _error = null;
    }

    /// <summary>Drops the cached list so the next open re-reads the vessel.</summary>
    public void Refresh() => _itemsAt = double.NegativeInfinity;

    /// <summary>Draws the combo for <paramref name="vesselId"/>; returns true on the frame the selection changed.</summary>
    public bool Draw(IGameBridge bridge, string? vesselId, double now)
    {
        if (vesselId is null)
        {
            using (new DisabledScope(true))
            {
                if (ImGui.BeginCombo(_id, "(pick a vessel first)"))
                {
                    ImGui.EndCombo();
                }
            }

            return false;
        }

        string preview = SelectedInstanceId is null ? "(pick a part)" : SelectedLabel;
        if (ImGuiWidgets.FilterCombo(_id, preview, _filter, () => Items(bridge, vesselId, now), out string? picked)
            && picked is not null
            && uint.TryParse(picked, NumberStyles.None, CultureInfo.InvariantCulture, out uint instanceId))
        {
            SelectedInstanceId = instanceId;
            foreach ((string key, string label) in _items)
            {
                if (key == picked)
                {
                    SelectedLabel = label;
                    break;
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>The last read error, or null.</summary>
    public string? Error => _error;

    private IReadOnlyList<(string Key, string Label)> Items(IGameBridge bridge, string vesselId, double now)
    {
        if (_itemsVessel == vesselId && now - _itemsAt < CacheSeconds)
        {
            return _items;
        }

        var items = new List<(string Key, string Label)>();
        _error = null;
        try
        {
            ScriptValue snapshot = bridge.Snapshot($"vessel:{vesselId}");
            if (snapshot.Kind != ScriptValueKind.Map)
            {
                _error = "The vessel could not be read (is it still in the scene?).";
            }
            else if (snapshot.TryGet("parts", out ScriptValue parts) && parts.Kind == ScriptValueKind.List)
            {
                foreach (ScriptValue part in parts.AsList())
                {
                    ScriptValue iid = part["instanceId"];
                    if (iid.Kind != ScriptValueKind.Number)
                    {
                        continue;
                    }

                    uint instanceId = (uint)iid.AsNumber();
                    ScriptValue nameValue = part["displayName"];
                    string name = nameValue.Kind == ScriptValueKind.String ? nameValue.AsString() : "(unnamed)";
                    items.Add((instanceId.ToString(CultureInfo.InvariantCulture), $"{name} (#{instanceId})"));
                }
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Log.LogOnce("ui.part.list", LogLevel.Warn, $"Listing parts failed (logged once): {ex.Message}");
        }

        _items = items;
        _itemsVessel = vesselId;
        _itemsAt = now;
        return items;
    }
}
