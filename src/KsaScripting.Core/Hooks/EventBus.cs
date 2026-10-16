using KsaScripting.Core.Logging;

namespace KsaScripting.Core.Hooks;

/// <summary>
///     The pipe between event producers (Harmony patches, the snapshot differ, terminals) and the
///     scheduler. <see cref="Publish"/> queues an event for async delivery in the next Step slot;
///     <see cref="PublishSync"/> additionally lets sync subscriptions run inline first. The queue is
///     bounded and lock-protected, so publishing from a patch while a slot drains on the same thread
///     is safe (and so is a publisher on another thread).
/// </summary>
public sealed class EventBus
{
    /// <summary>The queue bound; beyond it the oldest queued event is dropped.</summary>
    public const int MaxQueued = 10_000;

    private readonly object _gate = new();
    private readonly LinkedList<(long Seq, ScriptEvent Event)> _queue = new();
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly HashSet<long> _syncDeferred = [];
    private long _sequence;

    /// <summary>
    ///     Runs the sync subscriptions of an event immediately. Set by the scheduler's constructor; the
    ///     bus calls it only when no script invocation is in progress and never lets it throw.
    /// </summary>
    public Action<ScriptEvent>? SyncDispatcher { get; set; }

    /// <summary>
    ///     Reports how many script invocations are currently on the stack (set by the scheduler).
    ///     While it is above zero a sync publish cannot run its subscribers inline.
    /// </summary>
    internal Func<int>? InvokeDepthProvider { get; set; }

    /// <summary>The sequence number of the most recently queued event (0 before the first event).</summary>
    public long LastSequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    /// <summary>The number of events waiting for async delivery.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Events published so far, per event type (a snapshot copy).</summary>
    public IReadOnlyDictionary<string, long> Counters
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, long>(_counters, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Raised for every <see cref="Publish"/> / <see cref="PublishSync"/> (UI live counters). Handler exceptions are swallowed.</summary>
    public event Action<ScriptEvent>? Published;

    /// <summary>
    ///     Called inside a Harmony patch on the game thread: counts the event, runs the sync
    ///     subscriptions inline (when <see cref="SyncDispatcher"/> is set and no script invocation is
    ///     already running), then queues the event for async delivery exactly like <see cref="Publish"/>.
    ///     When the sync subscribers cannot run inline the event is flagged so the scheduler delivers
    ///     them in the next slot with <c>ctx.sync = false</c>. Never throws.
    /// </summary>
    /// <param name="ev">The event.</param>
    public void PublishSync(ScriptEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        Count(ev);

        var deferSync = false;
        var dispatcher = SyncDispatcher;
        if (dispatcher is not null)
        {
            var depth = 0;
            try
            {
                depth = InvokeDepthProvider?.Invoke() ?? 0;
            }
            catch (Exception ex)
            {
                Log.LogOnce("eventbus.depth", LogLevel.Warn, $"Reading the script invoke depth failed: {ex.Message}");
            }

            if (depth > 0)
            {
                deferSync = true;
                Log.LogOnce("eventbus.sync-deferred", LogLevel.Debug,
                    $"A sync event ('{ev.Type}') was published while a script was running; its sync subscribers run in the next Step slot instead.");
            }
            else
            {
                try
                {
                    dispatcher(ev);
                }
                catch (Exception ex)
                {
                    Log.LogOnce("eventbus.sync-dispatch", LogLevel.Warn, $"Sync dispatch of '{ev.Type}' failed: {ex.Message}");
                }
            }
        }

        Enqueue(ev, deferSync);
        RaisePublished(ev);
    }

    /// <summary>Counts the event and queues it (FIFO) for async delivery in the next Step slot. Beyond <see cref="MaxQueued"/> the oldest event is dropped.</summary>
    /// <param name="ev">The event.</param>
    public void Publish(ScriptEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        Count(ev);
        Enqueue(ev, false);
        RaisePublished(ev);
    }

    /// <summary>Removes and returns everything queued, in order.</summary>
    /// <returns>The queued events with their sequence numbers.</returns>
    internal IReadOnlyList<(long Seq, ScriptEvent Event)> DrainPending()
    {
        lock (_gate)
        {
            var items = new (long Seq, ScriptEvent Event)[_queue.Count];
            _queue.CopyTo(items, 0);
            _queue.Clear();
            return items;
        }
    }

    /// <summary>Puts drained items back at the FRONT of the queue, preserving their order (the scheduler ran out of budget).</summary>
    /// <param name="items">The not-yet-delivered items, oldest first.</param>
    internal void Requeue(IReadOnlyList<(long Seq, ScriptEvent Event)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return;

        var dropped = 0;
        lock (_gate)
        {
            for (var i = items.Count - 1; i >= 0; i--)
                _queue.AddFirst(items[i]);

            // Events published meanwhile may push the queue past the bound: keep the newest.
            while (_queue.Count > MaxQueued)
            {
                _syncDeferred.Remove(_queue.First!.Value.Seq);
                _queue.RemoveFirst();
                dropped++;
            }
        }

        if (dropped > 0)
            LogOverflow();
    }

    /// <summary>Whether the sync subscribers of a queued event still have to be served asynchronously.</summary>
    /// <param name="seq">The event's sequence number.</param>
    /// <returns><see langword="true"/> when the event was published with sync subscribers deferred.</returns>
    internal bool IsSyncDeferred(long seq)
    {
        lock (_gate)
        {
            return _syncDeferred.Contains(seq);
        }
    }

    /// <summary>Forgets the deferred-sync flag of an event once it has been delivered.</summary>
    /// <param name="seq">The event's sequence number.</param>
    internal void ClearSyncDeferred(long seq)
    {
        lock (_gate)
        {
            _syncDeferred.Remove(seq);
        }
    }

    private void Count(ScriptEvent ev)
    {
        lock (_gate)
        {
            _counters.TryGetValue(ev.Type, out var count);
            _counters[ev.Type] = count + 1;
        }
    }

    private void Enqueue(ScriptEvent ev, bool syncDeferred)
    {
        var dropped = false;
        lock (_gate)
        {
            var seq = ++_sequence;
            _queue.AddLast((seq, ev));
            if (syncDeferred)
                _syncDeferred.Add(seq);

            if (_queue.Count > MaxQueued)
            {
                _syncDeferred.Remove(_queue.First!.Value.Seq);
                _queue.RemoveFirst();
                dropped = true;
            }
        }

        if (dropped)
            LogOverflow();
    }

    private static void LogOverflow()
        => Log.LogOnce("eventbus.overflow", LogLevel.Warn,
            $"The script event queue is full ({MaxQueued} events); dropping the oldest events. Scripts are too slow or the budget is too small.");

    private void RaisePublished(ScriptEvent ev)
    {
        try
        {
            Published?.Invoke(ev);
        }
        catch
        {
            // Observers (UI counters) must never break publishing.
        }
    }
}
