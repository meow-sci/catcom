using System.Globalization;
using Catcom.Core.Hooks;
using Catcom.Core.Snapshots;
using Catcom.Core.Values;

namespace Catcom.Ksa.Slots;

/// <summary>One event ready for an <see cref="IEventSink"/>.</summary>
/// <param name="Type">The event name.</param>
/// <param name="VesselId">The subject vessel, or null.</param>
/// <param name="Payload">The payload fields.</param>
/// <param name="Ut">Universal time, seconds.</param>
internal readonly record struct MappedEvent(
    string Type, string? VesselId, IReadOnlyDictionary<string, ScriptValue> Payload, double Ut);

/// <summary>
///     Maps the snapshot differ's <see cref="SimEvent"/>s (the gatOS vocabulary) to script events: every
///     event is published under its gatOS name with <c>{detail, …parsed fields}</c>, and where
///     <see cref="EventCatalog.DifferAliases"/> lists a dotted alias it is also published under that alias
///     with just the alias's fields (producer <c>"differ"</c>). The detail formats parsed here are the
///     ones <see cref="EventDiffer"/> writes. Pure: no game types.
/// </summary>
internal static class DifferEvents
{
    private const char Arrow = '→';

    /// <summary>Maps one differ event to the events to publish (the gatOS-named one first).</summary>
    /// <param name="e">The differ event.</param>
    /// <param name="vessel">Looks up a vessel's current snapshot (for the docking port index); may return null.</param>
    /// <returns>One event, or two when the gatOS name has a dotted alias.</returns>
    internal static List<MappedEvent> Map(SimEvent e, Func<string, VesselSnapshot?> vessel)
    {
        var result = new List<MappedEvent>(2);
        var detail = ScriptValue.From(e.Detail);

        switch (e.Type)
        {
            case "situation-change":
            case "soi-changed":
            {
                var (from, to) = ParseArrow(e.Detail);
                Add(result, e, e.Type, ("detail", detail), ("from", from), ("to", to));
                AddAlias(result, e, e.Type, ("from", from), ("to", to));
                break;
            }

            case "active-changed":
            {
                var (from, to) = ParseArrow(e.Detail);
                Add(result, e, e.Type, ("detail", detail), ("from", from), ("to", to));
                break;
            }

            case "warp-changed":
            {
                var (from, to) = ParseArrow(e.Detail);
                Add(result, e, e.Type, ("detail", detail), ("from", ToNumber(from)), ("to", ToNumber(to)));
                break;
            }

            case "engine-state":
            {
                // "engine N on|off"
                var engine = ParseIndex(e.Detail, "engine");
                var active = e.Detail.EndsWith(" on", StringComparison.Ordinal);
                Add(result, e, e.Type, ("detail", detail), ("engine", engine), ("active", ScriptValue.From(active)));
                var alias = active ? EventCatalog.DifferAliases[e.Type] : "engine.shutdown";
                Add(result, e, alias, ("engine", engine));
                break;
            }

            case "flameout":
                // "engine N"
                Add(result, e, e.Type, ("detail", detail), ("engine", ParseIndex(e.Detail, "engine")));
                break;

            case "decoupled":
                // "decoupler N"
                Add(result, e, e.Type, ("detail", detail), ("decoupler", ParseIndex(e.Detail, "decoupler")));
                break;

            case "docked":
            case "undocked":
                Add(result, e, e.Type, ("detail", detail), ("port", ScriptValue.From(PortIndex(e, vessel))));
                break;

            case "animation-complete":
            {
                // "animation N State"
                var animation = ParseIndex(e.Detail, "animation");
                Add(result, e, e.Type, ("detail", detail), ("animation", animation));
                AddAlias(result, e, e.Type, ("animation", animation));
                break;
            }

            case "battery-depleted":
            case "battery-charged":
                Add(result, e, e.Type, ("detail", detail));
                AddAlias(result, e, e.Type);
                break;

            case "vessel-appeared":
            case "vessel-removed":
                Add(result, e, e.Type, ("detail", detail), ("name", detail));
                break;

            default:
                Add(result, e, e.Type, ("detail", detail));
                break;
        }

        return result;
    }

    private static void AddAlias(List<MappedEvent> result, SimEvent e, string type,
        params (string Key, ScriptValue Value)[] fields)
    {
        if (EventCatalog.DifferAliases.TryGetValue(type, out var alias))
            Add(result, e, alias, fields);
    }

    private static void Add(List<MappedEvent> result, SimEvent e, string type,
        params (string Key, ScriptValue Value)[] fields)
    {
        var payload = new Dictionary<string, ScriptValue>(fields.Length, StringComparer.Ordinal);
        foreach (var (key, value) in fields)
            payload[key] = value;
        result.Add(new MappedEvent(type, e.VesselId, payload, e.UtSeconds));
    }

    /// <summary>Parses <c>"A→B"</c>; the differ writes <c>none</c> for an absent side, which becomes Null.</summary>
    private static (ScriptValue From, ScriptValue To) ParseArrow(string detail)
    {
        var at = detail.IndexOf(Arrow);
        return at < 0
            ? (ScriptValue.From(detail), ScriptValue.Null)
            : (Side(detail[..at]), Side(detail[(at + 1)..]));
    }

    private static ScriptValue Side(string text)
        => text == "none" ? ScriptValue.Null : ScriptValue.From(text);

    private static ScriptValue ToNumber(ScriptValue text)
        => text.Kind == ScriptValueKind.String
           && double.TryParse(text.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? ScriptValue.From(double.IsFinite(number) ? number : 0)
            : ScriptValue.From(0);

    /// <summary>Parses the integer after <paramref name="word"/> in <c>"word N …"</c>; -1 when absent.</summary>
    private static ScriptValue ParseIndex(string detail, string word)
        => ScriptValue.From(TryParseIndex(detail, word, out var index) ? index : -1);

    private static bool TryParseIndex(string detail, string word, out int index)
    {
        index = -1;
        var parts = detail.Split(' ', 3);
        return parts.Length >= 2
               && string.Equals(parts[0], word, StringComparison.Ordinal)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>
    ///     The docking port the event concerns. The differ writes <c>"port N"</c> when it has no part name
    ///     (always for <c>undocked</c>) and the docked-to part's name otherwise; for the latter the index is
    ///     recovered from the vessel's docking list, or -1 when it cannot be.
    /// </summary>
    private static int PortIndex(SimEvent e, Func<string, VesselSnapshot?> vessel)
    {
        if (TryParseIndex(e.Detail, "port", out var parsed))
            return parsed;
        if (e.VesselId is null || vessel(e.VesselId) is not { } snapshot)
            return -1;
        foreach (var port in snapshot.Docking)
            if (port.Docked && string.Equals(port.DockedToPart, e.Detail, StringComparison.Ordinal))
                return port.Index;
        return -1;
    }
}
