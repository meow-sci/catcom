using System.Diagnostics;
using Catcom.Core.Hooks;
using Catcom.Core.Scheduling;
using Catcom.Core.Tests.Support;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Scheduling;

[TestFixture]
public sealed class SchedulerBudgetTests
{
    private static ScheduleConfig WithFrameBudget(TimeSpan budget)
        => new(budget, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 3);

    [Test]
    public void ZeroBudget_RunsNoHooks_KeepsEventsQueued_AndCountsOverruns()
    {
        using var rig = new TestRig(WithFrameBudget(TimeSpan.Zero));
        var calls = new List<string>();
        rig.Subscribe("a.lua", "event", "stage", (_, _) => { calls.Add("event"); return ScriptValue.Null; });
        rig.Subscribe("a.lua", "tick", "frame", (_, _) => { calls.Add("tick"); return ScriptValue.Null; });
        rig.Subscribe("a.lua", "service", "main", (_, _) => { calls.Add("service"); return ScriptValue.Null; });
        rig.Events.Publish(TestRig.Event("stage"));
        rig.Events.Publish(TestRig.Event("stage"));

        rig.Slot(1);
        rig.Slot(2);

        var stats = rig.Scheduler.Stats;
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.Empty);
            Assert.That(stats.PendingEvents, Is.EqualTo(2));
            Assert.That(stats.BudgetOverruns, Is.GreaterThan(0));
            Assert.That(stats.Invocations, Is.Zero);
        });
    }

    [Test]
    public void ZeroBudget_DoesNotEvaluateUntilPredicates_ButStillTimesThemOut()
    {
        using var rig = new TestRig(WithFrameBudget(TimeSpan.Zero));
        rig.Slot(10);
        var calls = 0;
        var wait = rig.Scheduler.CreateWait(
            "until",
            TestRig.Map(("fn", ScriptValue.Function(rig.Fn((_, _) => { calls++; return ScriptValue.True; }))), ("timeoutSeconds", ScriptValue.From(1))),
            "a.lua");

        rig.Slot(10.5);
        Assert.That(wait.IsCompleted, Is.False);
        rig.Slot(11);

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.Zero);
            Assert.That(wait.Task.Result.AsBool(), Is.False);
        });
    }

    [Test]
    public void AnExhaustedBudget_RequeuesTheRestOfTheEvents_AndDeliversThemInOrderNextSlot()
    {
        // The first hook burns more than the whole slot budget; the remaining events must wait for the
        // next slot, in their original order, and none may be delivered twice.
        using var rig = new TestRig(WithFrameBudget(TimeSpan.FromMilliseconds(250)));
        var delivered = new List<double>();
        var burned = false;
        rig.Subscribe("a.lua", "event", "e", (_, args) =>
        {
            delivered.Add(args[0]["event"]["ut"].AsNumber());
            if (!burned)
            {
                burned = true;
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 300)
                {
                }
            }

            return ScriptValue.Null;
        });
        for (var i = 1; i <= 4; i++)
            rig.Events.Publish(TestRig.Event("e", null, i));

        rig.Slot(1);
        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(new double[] { 1 }));
            Assert.That(rig.Scheduler.Stats.PendingEvents, Is.EqualTo(3));
            Assert.That(rig.Scheduler.Stats.BudgetOverruns, Is.EqualTo(1));
        });

        rig.Events.Publish(TestRig.Event("e", null, 5));
        rig.Slot(2);

        Assert.That(delivered, Is.EqualTo(new double[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void AnExhaustedBudget_SkipsTheRemainingTicks()
    {
        using var rig = new TestRig(WithFrameBudget(TimeSpan.FromMilliseconds(250)));
        var ticks = new List<string>();
        var burned = false;
        foreach (var name in new[] { "one", "two", "three" })
        {
            var captured = name;
            rig.Subscribe("a.lua", "tick", "frame", (_, _) =>
            {
                ticks.Add(captured);
                if (!burned)
                {
                    burned = true;
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 300)
                    {
                    }
                }

                return ScriptValue.Null;
            });
        }

        rig.Slot(1);

        Assert.Multiple(() =>
        {
            Assert.That(ticks, Has.Count.EqualTo(1), "only the first tick fit");
            Assert.That(rig.Scheduler.Stats.BudgetOverruns, Is.EqualTo(1));
        });
    }

    [Test]
    public void TickStartingIndex_RotatesEverySlot_SoNoTickStarves()
    {
        using var rig = new TestRig();
        var order = new List<string>();
        foreach (var name in new[] { "one", "two", "three" })
        {
            var captured = name;
            rig.Subscribe("a.lua", "tick", "frame", (_, _) => { order.Add(captured); return ScriptValue.Null; });
        }

        rig.Slot(1);
        rig.Slot(2);

        Assert.That(order, Is.EqualTo(new[] { "one", "two", "three", "two", "three", "one" }));
    }

    [Test]
    public void StepSlot_WithABudgetThatIsNotExhausted_DeliversEverything()
    {
        using var rig = new TestRig(ScheduleConfig.Default with { FrameBudget = TimeSpan.FromSeconds(30) });
        var count = 0;
        rig.Subscribe("a.lua", "event", "e", (_, _) => { count++; return ScriptValue.Null; });
        for (var i = 0; i < 200; i++)
            rig.Events.Publish(TestRig.Event("e"));

        rig.Slot(1);

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(200));
            Assert.That(rig.Scheduler.Stats.BudgetOverruns, Is.Zero);
        });
    }
}
