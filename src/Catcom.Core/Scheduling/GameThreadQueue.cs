using System.Collections.Concurrent;
using Catcom.Core.Commands;
using Catcom.Core.Logging;

namespace Catcom.Core.Scheduling;

/// <summary>
///     The one-way pipe between any thread and the game thread. Off-thread callers call
///     <see cref="SubmitAsync"/> (enqueue a command and await its result with a timeout) or
///     <see cref="Post"/>/<see cref="PostCommand"/> (fire and forget); the game thread calls
///     <see cref="Drain"/> once per phase per frame to execute pending commands and complete the awaiting
///     tasks, and <see cref="DrainWork"/> to run arbitrary posted work (<see cref="PostWork"/>).
///     <b>Game state is only ever touched inside the executor on the game thread</b> — this class itself is
///     game-free and lock-light (two concurrent command queues, one work queue, one TCS per awaited command).
/// </summary>
public sealed class GameThreadQueue
{
    /// <summary>The default number of commands drained per phase per frame.</summary>
    public const int DefaultDrainBatch = 64;

    /// <summary>The default time a submit waits for the game thread.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentQueue<Pending> _frame = new();
    private readonly ConcurrentQueue<Pending> _solver = new();
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly TimeSpan _timeout;
    private int _pendingWork;

    /// <summary>Creates a queue.</summary>
    /// <param name="controlEnabled"><c>control_all_vessels</c>: false denies every submit.</param>
    /// <param name="debugEnabled"><c>debug_namespace</c>: gates the <c>debug.*</c> actions.</param>
    /// <param name="timeout">How long a submit waits for the game thread; a non-positive value falls back to <see cref="DefaultTimeout"/>.</param>
    public GameThreadQueue(bool controlEnabled, bool debugEnabled, TimeSpan timeout)
    {
        ControlEnabled = controlEnabled;
        DebugEnabled = debugEnabled;
        _timeout = timeout > TimeSpan.Zero ? timeout : DefaultTimeout;
    }

    /// <summary>Master control switch; when false every submit is denied.</summary>
    public bool ControlEnabled { get; }

    /// <summary>Whether the <c>debug.*</c> cheat actions are allowed (<c>debug_namespace</c>).</summary>
    public bool DebugEnabled { get; }

    /// <summary>The number of posted work items not yet run by <see cref="DrainWork"/>.</summary>
    public int PendingWork => Volatile.Read(ref _pendingWork);

    /// <summary>
    ///     Enqueues <paramref name="command"/> for game-thread execution and awaits its result.
    ///     Returns <see cref="CommandOutcome.Denied"/> immediately when control is disabled, and
    ///     <see cref="CommandOutcome.TimedOut"/> if the game thread does not drain it in time.
    ///     Honors <paramref name="ct"/> (cancellation propagates as <see cref="OperationCanceledException"/>).
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The command's outcome.</returns>
    public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        => EnqueueAsync([command], command.Phase, ct);

    /// <summary>
    ///     The atomic batch submit: the whole group rides one <see cref="Pending"/>, so the game thread
    ///     executes it in order inside a single <see cref="Drain"/> — same tick, never split. All commands
    ///     must share one phase (the mixed case cannot mean "same tick": the two phases drain at different
    ///     points).
    /// </summary>
    /// <param name="commands">The commands, executed in order.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The first failure (annotated with which command failed), or Ok.</returns>
    public Task<CommandResult> SubmitBatchAsync(IReadOnlyList<SimCommand> commands, CancellationToken ct)
    {
        if (commands.Count == 0)
            return Task.FromResult(new CommandResult(CommandOutcome.Invalid, "empty batch"));

        var group = commands.ToArray();
        var phase = group[0].Phase;
        foreach (var command in group)
        {
            if (command.Phase != phase)
                return Task.FromResult(new CommandResult(CommandOutcome.Invalid,
                    "batch commands must all share one phase (Frame or Solver)"));
        }

        return EnqueueAsync(group, phase, ct);
    }

    /// <summary>
    ///     <b>Fire-and-forget submit</b>: enqueues <paramref name="command"/> for the game thread without
    ///     creating a waiter, and hands the outcome to <paramref name="observer"/> when it drains.
    /// </summary>
    /// <remarks>
    ///     Each post routes itself by <see cref="SimCommand.Phase"/>, so <b>phase mixing across posts
    ///     is free</b> — unlike <see cref="SubmitBatchAsync"/>, which rejects it because "same tick"
    ///     is meaningless across phases. <paramref name="observer"/> is invoked inline on the game
    ///     thread inside <see cref="Drain"/>.
    /// </remarks>
    /// <param name="command">The command to enqueue.</param>
    /// <param name="observer">Optional outcome sink; null discards the result.</param>
    /// <param name="token">Correlation token echoed back to <paramref name="observer"/>.</param>
    public void Post(SimCommand command, IPostObserver? observer = null, int token = 0)
    {
        if (!ControlEnabled)
        {
            // Report rather than silently swallow: a poster whose commands are all denied must be
            // able to say so, exactly as an awaiting submit would have.
            observer?.OnCommandResult(token, new CommandResult(CommandOutcome.Denied,
                "control is disabled in catcom.toml"));
            return;
        }

        var pending = new Pending([command], observer, token);
        (command.Phase == CommandPhase.Solver ? _solver : _frame).Enqueue(pending);
    }

    /// <summary>
    ///     Fire-and-forget submit with a callback: <paramref name="onResult"/> is invoked inline on the game
    ///     thread inside <see cref="Drain"/> (so it must be trivial and must not throw). When control is
    ///     disabled the callback is invoked immediately, on the calling thread, with
    ///     <see cref="CommandOutcome.Denied"/>.
    /// </summary>
    /// <param name="command">The command to enqueue.</param>
    /// <param name="onResult">Receives the outcome.</param>
    public void PostCommand(SimCommand command, Action<CommandResult> onResult)
    {
        ArgumentNullException.ThrowIfNull(onResult);
        Post(command, new CallbackObserver(onResult));
    }

    /// <summary>
    ///     Queues arbitrary work for the game thread, from any thread (terminal input, nvim RPC, the file
    ///     watcher, PTY exit callbacks). It runs, in FIFO order, inside <see cref="DrainWork"/>.
    /// </summary>
    /// <param name="work">The work to run on the game thread.</param>
    public void PostWork(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Interlocked.Increment(ref _pendingWork);
        _work.Enqueue(work);
    }

    /// <summary>
    ///     Game-thread only: runs up to <paramref name="maxItems"/> posted work items, first in first out.
    ///     A work item that throws is logged once per exception type and does not stop later items.
    /// </summary>
    /// <param name="maxItems">The most items to run in this call.</param>
    /// <returns>The number of items run (including ones that threw).</returns>
    public int DrainWork(int maxItems)
    {
        var run = 0;
        while (run < maxItems && _work.TryDequeue(out var work))
        {
            Interlocked.Decrement(ref _pendingWork);
            run++;
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.LogOnce("gtq.work." + ex.GetType().Name, LogLevel.Warn, "posted work threw: " + ex.Message);
            }
        }

        return run;
    }

    private async Task<CommandResult> EnqueueAsync(SimCommand[] group, CommandPhase phase, CancellationToken ct)
    {
        if (!ControlEnabled)
            return new CommandResult(CommandOutcome.Denied, "control is disabled in catcom.toml");

        var pending = new Pending(group);
        (phase == CommandPhase.Solver ? _solver : _frame).Enqueue(pending);

        try
        {
            return await pending.Completion!.Task.WaitAsync(_timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Abandon it so the game thread skips executing a command the caller already gave up on.
            pending.Abandoned = true;
            return new CommandResult(CommandOutcome.TimedOut,
                "the game thread did not execute the command in time (paused or loading?)");
        }
        // OperationCanceledException (the caller cancelled) propagates to the caller.
    }

    /// <summary>
    ///     Game-thread only: executes up to <paramref name="maxCommands"/> pending commands of the
    ///     given <paramref name="phase"/> through <paramref name="executor"/>, completing each
    ///     awaiting submit. Abandoned (timed-out) commands are dropped without executing. A batch
    ///     group always executes in full once dequeued — same-tick atomicity is its whole point —
    ///     so the bound can overshoot by the size of the final group. Returns the number executed.
    /// </summary>
    /// <param name="phase">Which phase's queue to drain.</param>
    /// <param name="executor">Executes each command; a leaked exception becomes <see cref="CommandOutcome.Fault"/>.</param>
    /// <param name="maxCommands">The soft bound on commands executed (see <see cref="DefaultDrainBatch"/>).</param>
    /// <returns>The number of commands executed.</returns>
    public int Drain(CommandPhase phase, ICommandExecutor executor, int maxCommands)
    {
        var queue = phase == CommandPhase.Solver ? _solver : _frame;
        var executed = 0;
        while (executed < maxCommands && queue.TryDequeue(out var pending))
        {
            if (pending.Abandoned)
                continue;

            var commands = pending.Commands;
            CommandResult? failure = null;
            var result = CommandResult.Ok;
            for (var i = 0; i < commands.Length; i++)
            {
                try
                {
                    result = executor.Execute(commands[i]);
                }
                catch (Exception ex)
                {
                    // The executor contracts not to throw; treat a leak as an integration fault.
                    Log.Debug($"command executor threw for '{commands[i].Action}': {ex.Message}");
                    result = new CommandResult(CommandOutcome.Fault, ex.Message);
                }

                executed++;
                // Batch commands are independent: a failure does not stop the rest, and the
                // submitter gets the FIRST failure, annotated with which command it was.
                if (!result.IsSuccess && failure is null)
                    failure = commands.Length == 1
                        ? result
                        : new CommandResult(result.Outcome,
                            $"command {i + 1}/{commands.Length} ({commands[i].Action}): {result.Message ?? "no detail"}");
            }

            var final = failure ?? (commands.Length == 1 ? result : CommandResult.Ok);
            pending.Completion?.TrySetResult(final);
            // Fire-and-forget posts have no waiter; their observer is called INLINE here, on the
            // game thread, so it must stay trivial (the IPostObserver contract).
            if (pending.Observer is { } observer)
            {
                try
                {
                    observer.OnCommandResult(pending.Token, final);
                }
                catch (Exception ex)
                {
                    // The observer contracts not to throw; one that does must not stall the drain.
                    Log.LogOnce("gtq.observer." + ex.GetType().Name, LogLevel.Warn, "command observer threw: " + ex.Message);
                }
            }
        }

        return executed;
    }

    private sealed class CallbackObserver(Action<CommandResult> onResult) : IPostObserver
    {
        public void OnCommandResult(int token, CommandResult result) => onResult(result);
    }

    private sealed class Pending
    {
        /// <summary>The awaiting path (<see cref="SubmitAsync"/>/<see cref="SubmitBatchAsync"/>).</summary>
        internal Pending(SimCommand[] commands)
        {
            Commands = commands;
            // RunContinuationsAsynchronously: the awaiting caller must never resume inline on the game
            // thread inside Drain — nothing may block or run foreign code on the game thread.
            Completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>The fire-and-forget path (<see cref="Post"/>): no TCS is created at all.</summary>
        internal Pending(SimCommand[] commands, IPostObserver? observer, int token)
        {
            Commands = commands;
            Observer = observer;
            Token = token;
        }

        internal TaskCompletionSource<CommandResult>? Completion { get; }

        internal IPostObserver? Observer { get; }

        internal int Token { get; }

        internal SimCommand[] Commands { get; }

        internal volatile bool Abandoned;
    }
}
