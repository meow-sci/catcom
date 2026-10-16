using KsaScripting.Core.Commands;
using KsaScripting.Core.Scheduling;
using KsaScripting.Core.Tests.Commands;

namespace KsaScripting.Core.Tests.Scheduling;

/// <summary>
///     The write pipe : enqueue on a transport thread, drain on
///     the game thread, with timeout/denied/abandon/fault semantics.
/// </summary>
[TestFixture]
public sealed class GameThreadQueueTests
{
    // Phase is derived from the action (SolverActions), so pick an action that maps to the wanted lane:
    // a frame action (engine.active) or a solver action (debug.refill_fuel).
    private static SimCommand Cmd(CommandPhase phase = CommandPhase.Frame)
        => phase == CommandPhase.Solver
            ? new SimCommand("v1", "debug.refill_fuel", SimCommand.NoOrdinal, 1)
            : new SimCommand("v1", "engine.active", 0, 1);

    [Test]
    public async Task Submit_ThenDrain_ExecutesAndReturnsResult()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var submit = queue.SubmitAsync(Cmd(), CancellationToken.None);

        var drained = queue.Drain(CommandPhase.Frame, executor, 64);
        var result = await submit;

        Assert.Multiple(() =>
        {
            Assert.That(drained, Is.EqualTo(1));
            Assert.That(executor.Count, Is.EqualTo(1));
            Assert.That(result.IsSuccess, Is.True);
        });
    }

    [Test]
    public async Task ControlDisabled_DeniesWithoutQueuing()
    {
        var queue = new GameThreadQueue(controlEnabled: false, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var result = await queue.SubmitAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Denied));
            Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task NotDrained_TimesOut()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromMilliseconds(50));
        var result = await queue.SubmitAsync(Cmd(), CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.TimedOut));
    }

    [Test]
    public async Task TimedOutCommand_IsSkippedByDrain()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromMilliseconds(50));
        var executor = new FakeCommandExecutor();
        var result = await queue.SubmitAsync(Cmd(), CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.TimedOut));

        // A late drain must not execute a command the writer already gave up on.
        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
        Assert.That(executor.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task ExecutorThrow_MapsToFault()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor { Throw = true };
        var submit = queue.SubmitAsync(Cmd(), CancellationToken.None);
        queue.Drain(CommandPhase.Frame, executor, 64);
        var result = await submit;
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Fault));
    }

    [Test]
    public async Task FramePhase_DoesNotDrainSolverCommands()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var submit = queue.SubmitAsync(Cmd(CommandPhase.Solver), CancellationToken.None);

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0), "frame drain skips solver commands");
        Assert.That(queue.Drain(CommandPhase.Solver, executor, 64), Is.EqualTo(1), "solver drain takes them");
        await submit;
    }

    [Test]
    public async Task Drain_RespectsMaxCommands()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var submits = Enumerable.Range(0, 5).Select(_ => queue.SubmitAsync(Cmd(), CancellationToken.None)).ToArray();

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 2), Is.EqualTo(2), "bounded per drain");
        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(3), "remainder next drain");
        await Task.WhenAll(submits);
    }

    // ---- batch submits (batch — same-tick atomic groups) --------------------------

    private static SimCommand[] Batch(int count)
        => [.. Enumerable.Range(0, count).Select(i => new SimCommand("v1", "engine.active", i, 1))];

    [Test]
    public async Task SubmitBatch_ExecutesWholeGroupInOneDrain_InOrder()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var submit = queue.SubmitBatchAsync(Batch(3), CancellationToken.None);

        var drained = queue.Drain(CommandPhase.Frame, executor, 64);
        var result = await submit;

        Assert.Multiple(() =>
        {
            Assert.That(drained, Is.EqualTo(3), "the whole group in ONE drain — the same-tick guarantee");
            Assert.That(executor.All.Select(c => c.Ordinal), Is.EqualTo(new[] { 0, 1, 2 }), "in order");
            Assert.That(result.IsSuccess, Is.True);
        });
    }

    [Test]
    public async Task SubmitBatch_NeverSplitsAcrossDrains()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var submit = queue.SubmitBatchAsync(Batch(3), CancellationToken.None);

        // A group executes in full once dequeued, so a tight budget overshoots rather than split.
        Assert.That(queue.Drain(CommandPhase.Frame, executor, 1), Is.EqualTo(3), "atomic beats the bound");
        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0), "nothing left behind");
        await submit;
    }

    [Test]
    public async Task SinglesAndBatch_DrainFifo_InOneDrain()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var before = queue.SubmitAsync(new SimCommand("v1", "engine.active", 100, 1), CancellationToken.None);
        var batch = queue.SubmitBatchAsync(Batch(2), CancellationToken.None);
        var after = queue.SubmitAsync(new SimCommand("v1", "engine.active", 200, 1), CancellationToken.None);

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(4));
        Assert.That(executor.All.Select(c => c.Ordinal), Is.EqualTo(new[] { 100, 0, 1, 200 }), "FIFO preserved");
        await Task.WhenAll(before, batch, after);
    }

    [Test]
    public async Task SubmitBatch_ReportsFirstFailure_AndStillExecutesTheRest()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor
        {
            OnExecute = c => c.Ordinal == 1
                ? new CommandResult(CommandOutcome.Busy, "no can do")
                : CommandResult.Ok,
        };
        var submit = queue.SubmitBatchAsync(Batch(3), CancellationToken.None);
        queue.Drain(CommandPhase.Frame, executor, 64);
        var result = await submit;

        Assert.Multiple(() =>
        {
            Assert.That(executor.Count, Is.EqualTo(3), "batch commands are independent — all execute");
            Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Busy));
            Assert.That(result.Message, Does.Contain("command 2/3"), "the failure names its line");
        });
    }

    [Test]
    public async Task SubmitBatch_MixedPhases_IsInvalid_WithoutQueuing()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var result = await queue.SubmitBatchAsync([Cmd(), Cmd(CommandPhase.Solver)], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Invalid));
            Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
            Assert.That(queue.Drain(CommandPhase.Solver, executor, 64), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task SubmitBatch_Empty_IsInvalid()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var result = await queue.SubmitBatchAsync([], CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Invalid));
    }

    [Test]
    public async Task SubmitBatch_ControlDisabled_Denies()
    {
        var queue = new GameThreadQueue(controlEnabled: false, debugEnabled: false, TimeSpan.FromSeconds(5));
        var result = await queue.SubmitBatchAsync(Batch(2), CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.Denied));
    }

    [Test]
    public async Task SubmitBatch_SolverPhase_DrainsInSolverLane()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        SimCommand[] refills =
        [
            new("v1", "debug.refill_fuel", SimCommand.NoOrdinal, 1),
            new("v2", "debug.refill_fuel", SimCommand.NoOrdinal, 1),
        ];
        var submit = queue.SubmitBatchAsync(refills, CancellationToken.None);

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0), "not in the frame lane");
        Assert.That(queue.Drain(CommandPhase.Solver, executor, 64), Is.EqualTo(2), "the solver lane takes it");
        await submit;
    }

    [Test]
    public async Task SubmitBatch_NotDrained_TimesOut_AndIsSkipped()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromMilliseconds(50));
        var executor = new FakeCommandExecutor();
        var result = await queue.SubmitBatchAsync(Batch(2), CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcome.TimedOut));

        // A late drain must not execute a batch the writer already gave up on.
        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
        Assert.That(executor.Count, Is.EqualTo(0));
    }

    // ---- Post: the fire-and-forget lane the host-side scheduler uses -------------------------

    /// <summary>Records every callback, so tests can assert the token/result pairing.</summary>
    private sealed class RecordingObserver : IPostObserver
    {
        public List<(int Token, CommandResult Result)> Results { get; } = [];

        public void OnCommandResult(int token, CommandResult result) => Results.Add((token, result));
    }

    [Test]
    public void Post_DrainsWithoutAWaiter_AndReportsToTheObserver()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor { Result = new CommandResult(CommandOutcome.Busy, "already fired") };
        var observer = new RecordingObserver();

        queue.Post(Cmd(), observer, token: 42);
        Assert.That(observer.Results, Is.Empty, "nothing happens until the game thread drains");

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(observer.Results, Has.Count.EqualTo(1));
            Assert.That(observer.Results[0].Token, Is.EqualTo(42));
            Assert.That(observer.Results[0].Result.Outcome, Is.EqualTo(CommandOutcome.Busy));
        });
    }

    [Test]
    public void Post_WithNoObserver_StillExecutes()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();

        queue.Post(Cmd());
        Assert.Multiple(() =>
        {
            Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(1));
            Assert.That(executor.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void Post_RoutesByPhase_AndMixedPostsBothLand()
    {
        // Unlike SubmitBatchAsync, Post has no same-tick contract, so each post routes itself and
        // mixing Frame and Solver across posts is free — exactly what a schedule needs.
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var observer = new RecordingObserver();

        queue.Post(Cmd(), observer, token: 1);
        queue.Post(Cmd(CommandPhase.Solver), observer, token: 2);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(1));
            Assert.That(queue.Drain(CommandPhase.Solver, executor, 64), Is.EqualTo(1));
            Assert.That(observer.Results.Select(r => r.Token), Is.EqualTo(new[] { 1, 2 }));
        });
    }

    [Test]
    public void Post_ControlDisabled_DeniesImmediatelyWithoutQueuing()
    {
        var queue = new GameThreadQueue(controlEnabled: false, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var observer = new RecordingObserver();

        queue.Post(Cmd(), observer, token: 7);
        Assert.Multiple(() =>
        {
            Assert.That(observer.Results, Has.Count.EqualTo(1), "reported, not silently swallowed");
            Assert.That(observer.Results[0].Token, Is.EqualTo(7));
            Assert.That(observer.Results[0].Result.Outcome, Is.EqualTo(CommandOutcome.Denied));
            Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Post_AndSubmit_ShareTheSameLane_InOrder()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();

        queue.Post(new SimCommand("posted", "engine.active", 0, 1));
        var submit = queue.SubmitAsync(new SimCommand("awaited", "engine.active", 0, 1), CancellationToken.None);

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(2));
        var result = await submit;
        Assert.Multiple(() =>
        {
            Assert.That(executor.All.Select(c => c.VesselId), Is.EqualTo(new[] { "posted", "awaited" }));
            Assert.That(result.IsSuccess, Is.True, "the awaiting path is unchanged by the refactor");
        });
    }

    // ---- ksa-scripting additions: PostCommand, PostWork/DrainWork ----------------------------------

    [Test]
    public void Defaults_AreThePinnedConstants()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GameThreadQueue.DefaultDrainBatch, Is.EqualTo(64));
            Assert.That(GameThreadQueue.DefaultTimeout, Is.EqualTo(TimeSpan.FromSeconds(2)));
        });
    }

    [Test]
    public void PostCommand_DeliversTheResultInlineOnDrain()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor { Result = new CommandResult(CommandOutcome.Busy, "already fired") };
        var received = new List<CommandResult>();
        var drainThread = Environment.CurrentManagedThreadId;
        var callbackThread = -1;

        queue.PostCommand(Cmd(), result =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            received.Add(result);
        });
        Assert.That(received, Is.Empty, "nothing happens until the game thread drains");

        Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(received, Has.Count.EqualTo(1));
            Assert.That(received[0].Outcome, Is.EqualTo(CommandOutcome.Busy));
            Assert.That(callbackThread, Is.EqualTo(drainThread), "invoked inline inside Drain");
        });
    }

    [Test]
    public void PostCommand_RoutesSolverCommandsToTheSolverLane()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var received = new List<CommandResult>();

        queue.PostCommand(Cmd(CommandPhase.Solver), received.Add);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
            Assert.That(received, Is.Empty);
            Assert.That(queue.Drain(CommandPhase.Solver, executor, 64), Is.EqualTo(1));
            Assert.That(received, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void PostCommand_ControlDisabled_DeniesImmediately()
    {
        var queue = new GameThreadQueue(controlEnabled: false, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var received = new List<CommandResult>();

        queue.PostCommand(Cmd(), received.Add);

        Assert.Multiple(() =>
        {
            Assert.That(received, Has.Count.EqualTo(1), "invoked before PostCommand returned");
            Assert.That(received[0].Outcome, Is.EqualTo(CommandOutcome.Denied));
            Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(0));
        });
    }

    [Test]
    public void ThrowingObserver_DoesNotStallTheDrain()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var executor = new FakeCommandExecutor();
        var received = 0;

        queue.PostCommand(Cmd(), _ => throw new InvalidOperationException("observer bug"));
        queue.PostCommand(Cmd(), _ => received++);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Drain(CommandPhase.Frame, executor, 64), Is.EqualTo(2));
            Assert.That(received, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task PostWork_FromFourThreads_RunsEachItemOnce_FifoPerProducer()
    {
        const int producers = 4;
        const int perProducer = 200;
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var ran = new List<(int Producer, int Sequence)>();
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < perProducer; i++)
            {
                var sequence = i;
                queue.PostWork(() => ran.Add((p, sequence)));
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(queue.PendingWork, Is.EqualTo(producers * perProducer));
        Assert.That(ran, Is.Empty, "work only runs when the game thread drains");

        var run = queue.DrainWork(int.MaxValue);

        Assert.Multiple(() =>
        {
            Assert.That(run, Is.EqualTo(producers * perProducer));
            Assert.That(queue.PendingWork, Is.EqualTo(0));
            Assert.That(ran, Has.Count.EqualTo(producers * perProducer));
            for (var p = 0; p < producers; p++)
            {
                var sequences = ran.Where(r => r.Producer == p).Select(r => r.Sequence).ToArray();
                Assert.That(sequences, Is.EqualTo(Enumerable.Range(0, perProducer)), $"producer {p} ran exactly once, in order");
            }
        });
    }

    [Test]
    public void DrainWork_RespectsMaxItems_AndPendingWorkTracksTheRemainder()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var ran = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            var n = i;
            queue.PostWork(() => ran.Add(n));
        }

        Assert.Multiple(() =>
        {
            Assert.That(queue.PendingWork, Is.EqualTo(10));
            Assert.That(queue.DrainWork(4), Is.EqualTo(4));
            Assert.That(queue.PendingWork, Is.EqualTo(6));
            Assert.That(ran, Is.EqualTo(new[] { 0, 1, 2, 3 }));
            Assert.That(queue.DrainWork(0), Is.EqualTo(0));
            Assert.That(queue.DrainWork(100), Is.EqualTo(6));
            Assert.That(queue.PendingWork, Is.EqualTo(0));
            Assert.That(queue.DrainWork(100), Is.EqualTo(0));
            Assert.That(ran, Has.Count.EqualTo(10));
        });
    }

    [Test]
    public void DrainWork_AThrowingItem_DoesNotStopLaterItems()
    {
        var queue = new GameThreadQueue(controlEnabled: true, debugEnabled: false, TimeSpan.FromSeconds(5));
        var ran = new List<string>();
        queue.PostWork(() => ran.Add("first"));
        queue.PostWork(() => throw new InvalidOperationException("boom"));
        queue.PostWork(() => ran.Add("third"));

        var run = queue.DrainWork(10);

        Assert.Multiple(() =>
        {
            Assert.That(run, Is.EqualTo(3), "the throwing item counts as run");
            Assert.That(ran, Is.EqualTo(new[] { "first", "third" }));
            Assert.That(queue.PendingWork, Is.EqualTo(0));
        });
    }

    [Test]
    public void PostWork_IgnoresControlEnabled()
    {
        // Work (terminal input, editor RPC) is not a game command; the authority gate does not apply.
        var queue = new GameThreadQueue(controlEnabled: false, debugEnabled: false, TimeSpan.FromSeconds(5));
        var ran = 0;
        queue.PostWork(() => ran++);
        Assert.Multiple(() =>
        {
            Assert.That(queue.DrainWork(10), Is.EqualTo(1));
            Assert.That(ran, Is.EqualTo(1));
        });
    }
}
