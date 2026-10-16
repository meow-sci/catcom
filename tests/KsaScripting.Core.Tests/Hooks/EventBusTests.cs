using KsaScripting.Core.Hooks;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Tests.Support;

namespace KsaScripting.Core.Tests.Hooks;

[TestFixture]
public sealed class EventBusTests
{
    private static ScriptEvent Ev(string type, double ut = 0) => TestRig.Event(type, null, ut);

    [Test]
    public void Publish_CountsPerType_AndHandsOutAscendingSequenceNumbers()
    {
        var bus = new EventBus();
        Assert.That(bus.LastSequence, Is.Zero);

        bus.Publish(Ev("stage"));
        bus.Publish(Ev("stage"));
        bus.Publish(Ev("dock"));

        var drained = bus.DrainPending();
        Assert.Multiple(() =>
        {
            Assert.That(bus.Counters["stage"], Is.EqualTo(2));
            Assert.That(bus.Counters["dock"], Is.EqualTo(1));
            Assert.That(bus.LastSequence, Is.EqualTo(3));
            Assert.That(drained.Select(d => d.Seq), Is.EqualTo(new long[] { 1, 2, 3 }));
            Assert.That(drained.Select(d => d.Event.Type), Is.EqualTo(new[] { "stage", "stage", "dock" }));
        });
    }

    [Test]
    public void Counters_ReturnASnapshotCopy()
    {
        var bus = new EventBus();
        bus.Publish(Ev("stage"));
        var snapshot = bus.Counters;

        bus.Publish(Ev("stage"));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot["stage"], Is.EqualTo(1));
            Assert.That(bus.Counters["stage"], Is.EqualTo(2));
        });
    }

    [Test]
    public void DrainPending_RemovesEverything_ButTheSequenceKeepsGrowing()
    {
        var bus = new EventBus();
        bus.Publish(Ev("a"));

        Assert.That(bus.DrainPending(), Has.Count.EqualTo(1));
        Assert.That(bus.DrainPending(), Is.Empty);
        Assert.That(bus.PendingCount, Is.Zero);

        bus.Publish(Ev("b"));
        Assert.That(bus.DrainPending().Single().Seq, Is.EqualTo(2));
    }

    [Test]
    public void Publish_BeyondTheBound_DropsTheOldest_AndLogsOnce()
    {
        using var log = new LogCapture();
        var bus = new EventBus();

        for (var i = 1; i <= EventBus.MaxQueued + 5; i++)
            bus.Publish(Ev("e", i));

        var drained = bus.DrainPending();
        Assert.Multiple(() =>
        {
            Assert.That(drained, Has.Count.EqualTo(EventBus.MaxQueued));
            Assert.That(drained[0].Seq, Is.EqualTo(6), "the five oldest were dropped");
            Assert.That(drained[^1].Seq, Is.EqualTo(EventBus.MaxQueued + 5));
            Assert.That(bus.Counters["e"], Is.EqualTo(EventBus.MaxQueued + 5), "dropped events are still counted");
            Assert.That(log.Entries.Count(e => e.Message.Contains("queue is full")), Is.EqualTo(1));
        });
    }

    [Test]
    public void Requeue_PutsItemsBackAtTheFront_PreservingOrder()
    {
        var bus = new EventBus();
        for (var i = 1; i <= 4; i++)
            bus.Publish(Ev("e", i));

        var drained = bus.DrainPending();
        bus.Publish(Ev("e", 5));
        bus.Requeue([drained[2], drained[3]]);

        Assert.That(bus.DrainPending().Select(d => d.Seq), Is.EqualTo(new long[] { 3, 4, 5 }));
    }

    [Test]
    public void Requeue_RespectsTheBound_KeepingTheNewest()
    {
        var bus = new EventBus();
        bus.Publish(Ev("e"));
        var drained = bus.DrainPending();
        for (var i = 0; i < EventBus.MaxQueued; i++)
            bus.Publish(Ev("e"));

        bus.Requeue(drained);

        var after = bus.DrainPending();
        Assert.Multiple(() =>
        {
            Assert.That(after, Has.Count.EqualTo(EventBus.MaxQueued));
            Assert.That(after[0].Seq, Is.EqualTo(2), "the requeued (oldest) event was dropped first");
        });
    }

    [Test]
    public void Published_IsRaisedForPublishAndPublishSync_AndHandlerExceptionsAreSwallowed()
    {
        var bus = new EventBus();
        var seen = new List<string>();
        bus.Published += ev => seen.Add(ev.Type);
        bus.Published += _ => throw new InvalidOperationException("ui exploded");

        Assert.DoesNotThrow(() =>
        {
            bus.Publish(Ev("a"));
            bus.PublishSync(Ev("b"));
        });
        Assert.That(seen, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void PublishSync_RunsTheDispatcherThenQueuesAndCountsOnce()
    {
        var bus = new EventBus();
        var order = new List<string>();
        bus.SyncDispatcher = ev => order.Add("dispatch:" + bus.PendingCount);

        bus.PublishSync(Ev("e"));

        Assert.Multiple(() =>
        {
            Assert.That(order, Is.EqualTo(new[] { "dispatch:0" }), "dispatch happens before the event is queued");
            Assert.That(bus.PendingCount, Is.EqualTo(1));
            Assert.That(bus.Counters["e"], Is.EqualTo(1));
            Assert.That(bus.IsSyncDeferred(1), Is.False);
        });
    }

    [Test]
    public void PublishSync_WithoutADispatcher_JustQueues()
    {
        var bus = new EventBus();

        bus.PublishSync(Ev("e"));

        Assert.That(bus.DrainPending(), Has.Count.EqualTo(1));
    }

    [Test]
    public void PublishSync_WhileAnInvokeIsRunning_SkipsTheDispatcherAndFlagsTheEvent()
    {
        var bus = new EventBus { InvokeDepthProvider = () => 1 };
        var dispatched = 0;
        bus.SyncDispatcher = _ => dispatched++;

        bus.PublishSync(Ev("e"));

        Assert.Multiple(() =>
        {
            Assert.That(dispatched, Is.Zero);
            Assert.That(bus.IsSyncDeferred(1), Is.True);
            Assert.That(bus.PendingCount, Is.EqualTo(1));
        });

        bus.ClearSyncDeferred(1);
        Assert.That(bus.IsSyncDeferred(1), Is.False);
    }

    [Test]
    public void PublishSync_ADispatcherThatThrows_NeverEscapes_AndTheEventIsStillQueued()
    {
        using var log = new LogCapture();
        var bus = new EventBus { SyncDispatcher = _ => throw new InvalidOperationException("boom") };

        Assert.DoesNotThrow(() => bus.PublishSync(Ev("e")));

        Assert.Multiple(() =>
        {
            Assert.That(bus.PendingCount, Is.EqualTo(1));
            Assert.That(log.Contains(LogLevel.Warn, "Sync dispatch of 'e' failed"), Is.True);
        });
    }

    [Test]
    public void PublishSync_ADroppedDeferredEvent_ForgetsItsFlag()
    {
        var bus = new EventBus { InvokeDepthProvider = () => 1, SyncDispatcher = _ => { } };
        bus.PublishSync(Ev("first"));
        for (var i = 0; i < EventBus.MaxQueued; i++)
            bus.Publish(Ev("filler"));

        Assert.That(bus.IsSyncDeferred(1), Is.False, "seq 1 was dropped by the bound");
    }

    [Test]
    public void Publish_FromSeveralThreads_LosesNothing()
    {
        var bus = new EventBus();
        const int threads = 4, perThread = 500;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                bus.Publish(Ev("e"));
        });

        var drained = bus.DrainPending();
        Assert.Multiple(() =>
        {
            Assert.That(drained, Has.Count.EqualTo(threads * perThread));
            Assert.That(drained.Select(d => d.Seq).Distinct().Count(), Is.EqualTo(threads * perThread));
            Assert.That(bus.Counters["e"], Is.EqualTo(threads * perThread));
        });
    }
}
