namespace Catcom.Runtime.V8;

/// <summary>One unit of work posted to the runtime's game-thread queue (a continuation, a promise resolution, …).</summary>
/// <param name="Context">The engine's synchronization context the work belongs to (dropped when it is disposed).</param>
/// <param name="Callback">The callback to run.</param>
/// <param name="State">The callback state.</param>
internal readonly record struct WorkItem(GameThreadSynchronizationContext Context, SendOrPostCallback Callback, object? State);
