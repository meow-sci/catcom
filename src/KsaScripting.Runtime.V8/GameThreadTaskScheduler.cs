namespace KsaScripting.Runtime.V8;

/// <summary>A <see cref="TaskScheduler"/> that runs tasks as items of the engine's game-thread queue.</summary>
internal sealed class GameThreadTaskScheduler : TaskScheduler
{
    private readonly GameThreadSynchronizationContext _context;

    /// <summary>Creates a scheduler over a synchronization context.</summary>
    /// <param name="context">The context whose queue runs the tasks.</param>
    public GameThreadTaskScheduler(GameThreadSynchronizationContext context) => _context = context;

    /// <inheritdoc />
    public override int MaximumConcurrencyLevel => 1;

    /// <inheritdoc />
    protected override void QueueTask(Task task)
        => _context.Post(static state => ((TaskRunState)state!).Run(), new TaskRunState(this, task));

    /// <inheritdoc />
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    /// <inheritdoc />
    protected override IEnumerable<Task> GetScheduledTasks() => [];

    private sealed class TaskRunState(GameThreadTaskScheduler scheduler, Task task)
    {
        public void Run() => scheduler.TryExecuteTask(task);
    }
}
