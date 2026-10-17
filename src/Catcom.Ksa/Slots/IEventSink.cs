using Catcom.Core.Values;

namespace Catcom.Ksa.Slots;

/// <summary>
///     Where the Ksa facade publishes script events (Harmony hooks and the snapshot differ). Decouples
///     <see cref="SlotHost"/> and the event patches from the Core event bus: the adapter
///     <see cref="EventBusSink"/> wraps <c>EventBus</c>, and tests can supply a recording fake.
///     Calls happen inside Harmony patches or the Step slot and must not throw;
///     <see cref="PublishSync"/> is only called on the game thread.
/// </summary>
public interface IEventSink
{
    /// <summary>
    ///     Publishes an event from inside a game-code patch: sync-capable subscriptions run before this
    ///     call returns (under the sync budget), then the event is queued for async delivery.
    /// </summary>
    /// <param name="type">The event name, e.g. <c>engine.activate</c> (see <c>EventCatalog</c>).</param>
    /// <param name="vesselId">The subject vessel's id, or <see langword="null"/> for a global event.</param>
    /// <param name="payload">The event fields (camelCase keys, exactly those the catalog lists).</param>
    /// <param name="ut">Universal time, in seconds, when the event was raised.</param>
    void PublishSync(string type, string? vesselId, IReadOnlyDictionary<string, ScriptValue> payload, double ut);

    /// <summary>Queues an event for async delivery in the next Step slot (used for differ events).</summary>
    /// <param name="type">The event name.</param>
    /// <param name="vesselId">The subject vessel's id, or <see langword="null"/> for a global event.</param>
    /// <param name="payload">The event fields.</param>
    /// <param name="ut">Universal time, in seconds, when the event was raised.</param>
    void Publish(string type, string? vesselId, IReadOnlyDictionary<string, ScriptValue> payload, double ut);
}
