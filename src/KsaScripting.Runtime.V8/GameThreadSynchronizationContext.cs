namespace KsaScripting.Runtime.V8;

/// <summary>
///     The synchronization context of one script engine. <see cref="Post"/> (from any thread) queues the work into the
///     runtime-wide <see cref="ScriptWorkQueue"/> tagged with the script, so promise resolutions and task continuations
///     always run on the game thread inside <c>Advance</c>. The runtime installs it as
///     <see cref="SynchronizationContext.Current"/> around every engine entry; ClearScript (with
///     <c>UseSynchronizationContexts</c>) captures it when it converts a Task to a Promise.
/// </summary>
internal sealed class GameThreadSynchronizationContext : SynchronizationContext
{
    private readonly ScriptWorkQueue _queue;
    private readonly Lazy<GameThreadTaskScheduler> _scheduler;
    private int _disposed;

    /// <summary>Creates the context of one script engine.</summary>
    /// <param name="scriptName">The script the context belongs to (diagnostics and host attribution).</param>
    /// <param name="queue">The runtime-wide work queue.</param>
    public GameThreadSynchronizationContext(string scriptName, ScriptWorkQueue queue)
    {
        ScriptName = scriptName;
        _queue = queue;
        _scheduler = new Lazy<GameThreadTaskScheduler>(() => new GameThreadTaskScheduler(this));
    }

    /// <summary>The script this context belongs to.</summary>
    public string ScriptName { get; }

    /// <summary>The runtime's state object for the owning engine (set once by the runtime).</summary>
    public object? Tag { get; set; }

    /// <summary>Whether the owning engine was unloaded (queued work for it is dropped).</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>A task scheduler that runs tasks as items of this context.</summary>
    public TaskScheduler Scheduler => _scheduler.Value;

    /// <summary>Marks the context unloaded; later posts are ignored and queued items are skipped.</summary>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (!IsDisposed)
            _queue.Enqueue(new WorkItem(this, d, state));
    }

    /// <inheritdoc />
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (!_queue.IsGameThread)
            throw new NotSupportedException("Send is only supported on the game thread; use Post");

        d(state);
    }

    /// <inheritdoc />
    public override SynchronizationContext CreateCopy() => this;
}
