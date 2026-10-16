using KsaScripting.Core.Hooks;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Values;

namespace KsaScripting.Ksa.Slots;

/// <summary>
///     The production <see cref="IEventSink"/>: wraps the Core <see cref="EventBus"/>, building the
///     <see cref="ScriptEvent"/> record. <see cref="EventBus"/> contracts not to throw; a sink that does
///     anyway is swallowed and logged once so a bus bug cannot take down a game-code patch.
/// </summary>
/// <param name="bus">The event bus events are published to.</param>
public sealed class EventBusSink(EventBus bus) : IEventSink
{
    /// <inheritdoc />
    public void PublishSync(string type, string? vesselId, IReadOnlyDictionary<string, ScriptValue> payload, double ut)
    {
        try
        {
            bus.PublishSync(new ScriptEvent(ut, type, vesselId, payload));
        }
        catch (Exception ex)
        {
            Log.LogOnce("eventsink.sync", LogLevel.Error, $"EventBus.PublishSync('{type}') threw (logged once): {ex}");
        }
    }

    /// <inheritdoc />
    public void Publish(string type, string? vesselId, IReadOnlyDictionary<string, ScriptValue> payload, double ut)
    {
        try
        {
            bus.Publish(new ScriptEvent(ut, type, vesselId, payload));
        }
        catch (Exception ex)
        {
            Log.LogOnce("eventsink.async", LogLevel.Error, $"EventBus.Publish('{type}') threw (logged once): {ex}");
        }
    }
}
