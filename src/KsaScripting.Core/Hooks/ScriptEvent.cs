using KsaScripting.Core.Values;

namespace KsaScripting.Core.Hooks;

/// <summary>
///     An immutable event record: what happened, when (universal time), to which vessel, with a
///     payload captured at the moment of the call. Events flow from the game (Harmony patches, the
///     snapshot differ) and from the host (terminals, <c>signal.cancel</c>) through the
///     <see cref="EventBus"/> to subscriptions and <c>waitEvent</c> waits.
/// </summary>
/// <param name="Ut">Universal time in seconds when the event was raised (0 for host events).</param>
/// <param name="Type">The event name, e.g. <c>engine.ignite</c> (see <see cref="EventCatalog"/>).</param>
/// <param name="VesselId">The vessel the event concerns, or <see langword="null"/>.</param>
/// <param name="Payload">
///     Event-specific fields. A String entry named <c>"$script"</c> targets the event at one script:
///     only that script's subscriptions and waits see it.
/// </param>
public sealed record ScriptEvent(double Ut, string Type, string? VesselId, IReadOnlyDictionary<string, ScriptValue> Payload)
{
    /// <summary>The payload key that targets an event at a single script.</summary>
    public const string ScriptKey = "$script";

    /// <summary>
    ///     The script this event is targeted at (payload entry <c>"$script"</c>), or
    ///     <see langword="null"/> when the event is broadcast.
    /// </summary>
    public string? TargetScript
        => Payload is not null && Payload.TryGetValue(ScriptKey, out var value) && value.Kind == ScriptValueKind.String
            ? value.AsString()
            : null;

    /// <summary>
    ///     The script-facing form: a Map <c>{ut, type, vesselId (or Null), payload (Map)}</c>. The
    ///     internal <c>"$script"</c> targeting entry is not exposed to scripts.
    /// </summary>
    /// <returns>The event as a <see cref="ScriptValue"/> Map.</returns>
    public ScriptValue ToScriptValue()
    {
        var payload = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
        if (Payload is not null)
        {
            foreach (var pair in Payload)
            {
                if (!string.Equals(pair.Key, ScriptKey, StringComparison.Ordinal))
                    payload[pair.Key] = pair.Value;
            }
        }

        return ScriptValue.Map(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["ut"] = ScriptValue.From(Ut),
            ["type"] = ScriptValue.From(Type),
            ["vesselId"] = VesselId is null ? ScriptValue.Null : ScriptValue.From(VesselId),
            ["payload"] = ScriptValue.Map(payload),
        });
    }
}
