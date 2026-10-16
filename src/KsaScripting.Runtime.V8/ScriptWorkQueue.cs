using System.Collections.Concurrent;

namespace KsaScripting.Runtime.V8;

/// <summary>
///     The runtime-wide FIFO queue of <see cref="WorkItem"/>s. Anyone may enqueue from any thread; only the game thread
///     (inside <c>Advance</c>) dequeues.
/// </summary>
internal sealed class ScriptWorkQueue
{
    private readonly ConcurrentQueue<WorkItem> _items = new();
    private readonly ManualResetEventSlim _signal = new(false);
    private int _gameThreadId;

    /// <summary>The managed id of the game thread (0 until the runtime is initialised).</summary>
    public int GameThreadId
    {
        get => Volatile.Read(ref _gameThreadId);
        set => Volatile.Write(ref _gameThreadId, value);
    }

    /// <summary>Whether the calling thread is the game thread.</summary>
    public bool IsGameThread => Environment.CurrentManagedThreadId == GameThreadId;

    /// <summary>Whether no work is queued.</summary>
    public bool IsEmpty => _items.IsEmpty;

    /// <summary>Queues work. Thread-safe.</summary>
    /// <param name="item">The item.</param>
    public void Enqueue(WorkItem item)
    {
        _items.Enqueue(item);
        _signal.Set();
    }

    /// <summary>Dequeues the oldest item. Game thread only.</summary>
    /// <param name="item">The item.</param>
    /// <returns><c>false</c> when the queue is empty.</returns>
    public bool TryDequeue(out WorkItem item)
    {
        if (_items.TryDequeue(out item))
            return true;

        _signal.Reset();
        if (!_items.IsEmpty)
            _signal.Set();
        return false;
    }

    /// <summary>Blocks until work is queued or the timeout passes (a test/diagnostic aid; the game never blocks).</summary>
    /// <param name="timeout">The longest time to wait.</param>
    /// <returns><c>true</c> when work is available.</returns>
    public bool WaitForWork(TimeSpan timeout) => !_items.IsEmpty || _signal.Wait(timeout);

    /// <summary>Discards all queued work.</summary>
    public void Clear()
    {
        while (_items.TryDequeue(out _))
        {
        }

        _signal.Reset();
    }

    // Deliberately not disposable: threads may still post (a late wait completion) while the runtime shuts down, and a
    // disposed event would throw on them. The event never allocates an OS handle unless its WaitHandle is requested.
}
