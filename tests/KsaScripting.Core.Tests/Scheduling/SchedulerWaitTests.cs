using KsaScripting.Core.Commands;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Tests.Support;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Tests.Scheduling;

[TestFixture]
public sealed class SchedulerWaitTests
{
    private static ScriptValue Num(double v) => ScriptValue.From(v);

    // ---------------------------------------------------------------- sleep

    [Test]
    public void Sleep_OnTheUtClock_CompletesExactlyWhenDue()
    {
        using var rig = new TestRig();
        rig.Slot(10);

        var wait = rig.Scheduler.CreateWait("sleep", Num(5), "a.lua");

        rig.Slot(12);
        rig.Slot(14.9);
        Assert.That(wait.IsCompleted, Is.False);
        rig.Slot(15);
        Assert.Multiple(() =>
        {
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(wait.Task.Result.IsNull, Is.True);
            Assert.That(rig.Scheduler.Stats.PendingWaits, Is.Zero);
        });
    }

    [Test]
    public void Sleep_OnTheWallClock_UsesTheInjectedClock()
    {
        using var rig = new TestRig();
        rig.Wall = 100;
        rig.Slot(1);

        var wait = rig.Scheduler.CreateWait("sleep", TestRig.Map(("seconds", Num(5)), ("clock", ScriptValue.From("wall"))), "a.lua");

        rig.Wall = 104.9;
        rig.Slot(2);
        Assert.That(wait.IsCompleted, Is.False);
        rig.Wall = 105;
        rig.Slot(3);
        Assert.That(wait.IsCompleted, Is.True);
    }

    [Test]
    public void Sleep_OnTheRenderClock_AccumulatesPlayerDt()
    {
        using var rig = new TestRig();
        rig.Slot(1, playerDt: 1.0);
        rig.Slot(2, playerDt: 1.0);

        var wait = rig.Scheduler.CreateWait("sleep", TestRig.Map(("seconds", Num(3)), ("clock", ScriptValue.From("render"))), "a.lua");

        rig.Slot(3, playerDt: 2.0);
        Assert.That(wait.IsCompleted, Is.False, "4 of 5 render seconds");
        rig.Slot(4, playerDt: 1.0);
        Assert.That(wait.IsCompleted, Is.True);
    }

    [Test]
    public void Sleep_NeverCompletesInTheSlotThatCreatedIt_EvenForZeroSeconds()
    {
        using var rig = new TestRig();
        HostWait? wait = null;
        rig.Subscribe("a.lua", "tick", "frame", (host, _) =>
        {
            wait ??= host.Wait("sleep", Num(0));
            return ScriptValue.Null;
        });

        rig.Slot(1);
        Assert.That(wait!.IsCompleted, Is.False, "created mid-slot, so it waits for the next slot");
        rig.Slot(1);
        Assert.That(wait.IsCompleted, Is.True);
    }

    [Test]
    public void Sleep_CreatedBeforeTheFirstSlot_CountsFromTheFirstSlot()
    {
        using var rig = new TestRig();
        var wait = rig.Scheduler.CreateWait("sleep", Num(5), "a.lua");

        rig.Slot(1000);
        Assert.That(wait.IsCompleted, Is.False);
        rig.Slot(1004.9);
        Assert.That(wait.IsCompleted, Is.False);
        rig.Slot(1005);
        Assert.That(wait.IsCompleted, Is.True);
    }

    [TestCase("sleep", "bad")]
    [TestCase("sleep", null)]
    [TestCase("until", "bad")]
    [TestCase("waitEvent", "bad")]
    [TestCase("termRead", "bad")]
    [TestCase("noSuchKind", null)]
    public void CreateWait_RejectsUnknownKindsAndMalformedArguments(string kind, string? arg)
    {
        using var rig = new TestRig();
        var value = arg is null ? ScriptValue.Null : ScriptValue.From(arg);

        Assert.Throws<ArgumentException>(() => rig.Scheduler.CreateWait(kind, value, "a.lua"));
    }

    [Test]
    public void CreateWait_RejectsAnUnknownClock()
    {
        using var rig = new TestRig();

        Assert.Throws<ArgumentException>(() =>
            rig.Scheduler.CreateWait("sleep", TestRig.Map(("seconds", Num(1)), ("clock", ScriptValue.From("lunar"))), "a.lua"));
    }

    [Test]
    public void WaitIds_AreUniqueAndIncreasing()
    {
        using var rig = new TestRig();

        var first = rig.Scheduler.CreateWait("nextFrame", ScriptValue.Null, "a.lua");
        var second = rig.Scheduler.CreateWait("nextFrame", ScriptValue.Null, "a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(second.Id, Is.GreaterThan(first.Id));
            Assert.That(first.Kind, Is.EqualTo("nextFrame"));
        });
    }

    // ---------------------------------------------------------------- nextStep / nextFrame

    [Test]
    public void NextStep_CompletesOnlyOnALandedStep_WithTheStepMap()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var wait = rig.Scheduler.CreateWait("nextStep", ScriptValue.Null, "a.lua");

        rig.Slot(1.1, landed: false);
        rig.Slot(1.2, landed: false);
        Assert.That(wait.IsCompleted, Is.False);

        var step = rig.Slot(1.3, landed: true, dt: 0.04);
        var result = wait.Task.Result;

        Assert.Multiple(() =>
        {
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(result["ut"].AsNumber(), Is.EqualTo(1.3));
            Assert.That(result["dt"].AsNumber(), Is.EqualTo(0.04));
            Assert.That(result["stepIndex"].AsNumber(), Is.EqualTo(step.StepIndex));
            Assert.That(result["frame"].AsNumber(), Is.EqualTo(step.Frame));
        });
    }

    [Test]
    public void NextFrame_CompletesInTheNextSlot_LandedOrNot()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var wait = rig.Scheduler.CreateWait("nextFrame", ScriptValue.Null, "a.lua");
        Assert.That(wait.IsCompleted, Is.False);

        rig.Slot(1, landed: false);

        Assert.Multiple(() =>
        {
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(wait.Task.Result.IsNull, Is.True);
        });
    }

    // ---------------------------------------------------------------- until

    [Test]
    public void Until_PredicateFalseFalseTrue_CompletesTrueOnTheThirdLaterSlot_AndDisposesThePredicate()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var calls = 0;
        var predicate = rig.Fn((_, _) => ScriptValue.From(++calls >= 3));

        var wait = rig.Scheduler.CreateWait("until", ScriptValue.Function(predicate), "a.lua");
        rig.Slot(2);
        rig.Slot(3);
        Assert.That(wait.IsCompleted, Is.False);
        rig.Slot(4);

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(3));
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(wait.Task.Result.AsBool(), Is.True);
            Assert.That(predicate.IsDisposed, Is.True);
        });
    }

    [Test]
    public void Until_AcceptsTheMapForm()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var wait = rig.Scheduler.CreateWait(
            "until",
            TestRig.Map(("fn", ScriptValue.Function(rig.Fn((_, _) => ScriptValue.True)))),
            "a.lua");

        rig.Slot(2);

        Assert.That(wait.Task.Result.AsBool(), Is.True);
    }

    [Test]
    public void Until_Timeout_CompletesFalse_AndDisposesThePredicate()
    {
        using var rig = new TestRig();
        rig.Slot(10);
        var predicate = rig.Fn((_, _) => ScriptValue.False);
        var wait = rig.Scheduler.CreateWait(
            "until",
            TestRig.Map(("fn", ScriptValue.Function(predicate)), ("timeoutSeconds", Num(2))),
            "a.lua");

        rig.Slot(11);
        Assert.That(wait.IsCompleted, Is.False);
        rig.Slot(12);

        Assert.Multiple(() =>
        {
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(wait.Task.Result.AsBool(), Is.False);
            Assert.That(predicate.IsDisposed, Is.True);
        });
    }

    [Test]
    public void Until_ATruePredicateAtTheTimeoutSlot_StillWins()
    {
        using var rig = new TestRig();
        rig.Slot(10);
        var wait = rig.Scheduler.CreateWait(
            "until",
            TestRig.Map(("fn", ScriptValue.Function(rig.Fn((_, _) => ScriptValue.True))), ("timeoutSeconds", Num(0))),
            "a.lua");

        rig.Slot(10.5);

        Assert.That(wait.Task.Result.AsBool(), Is.True);
    }

    [Test]
    public void Until_AThrowingPredicate_FailsTheWait_ReportsTheError_AndDisposesThePredicate()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var predicate = rig.Fn((_, _) => throw new InvalidOperationException("predicate exploded"));
        var wait = rig.Scheduler.CreateWait("until", ScriptValue.Function(predicate), "a.lua");

        rig.Slot(2);

        Assert.Multiple(() =>
        {
            Assert.That(wait.Task.IsFaulted, Is.True);
            Assert.That(wait.Task.Exception!.GetBaseException(), Is.TypeOf<ScriptRuntimeException>());
            Assert.That(rig.Errors, Has.Count.EqualTo(1));
            Assert.That(rig.Errors[0].Script, Is.EqualTo("a.lua"));
            Assert.That(rig.Errors[0].Error.Message, Does.Contain("predicate exploded"));
            Assert.That(predicate.IsDisposed, Is.True);
            Assert.That(rig.Scheduler.Stats.PendingWaits, Is.Zero);
        });
    }

    // ---------------------------------------------------------------- waitEvent

    private static ScriptValue EventWait(string name, string? vessel = null, double? timeout = null)
    {
        var items = new List<(string, ScriptValue)> { ("name", ScriptValue.From(name)) };
        if (vessel is not null)
            items.Add(("vessel", ScriptValue.From(vessel)));
        if (timeout is { } t)
            items.Add(("timeoutSeconds", Num(t)));
        return TestRig.Map(items.ToArray());
    }

    [Test]
    public void WaitEvent_MatchesNameAndVessel_IdActiveAndAny()
    {
        using var rig = new TestRig();
        rig.Bridge.SetActiveVessel("v1");
        rig.Slot(1);
        var anyVessel = rig.Scheduler.CreateWait("waitEvent", EventWait("engine.ignite"), "a.lua");
        var explicitAny = rig.Scheduler.CreateWait("waitEvent", EventWait("engine.ignite", "any"), "a.lua");
        var active = rig.Scheduler.CreateWait("waitEvent", EventWait("engine.ignite", "active"), "a.lua");
        var byId = rig.Scheduler.CreateWait("waitEvent", EventWait("engine.ignite", "v1"), "a.lua");
        var otherVessel = rig.Scheduler.CreateWait("waitEvent", EventWait("engine.ignite", "v2"), "a.lua");
        var otherEvent = rig.Scheduler.CreateWait("waitEvent", EventWait("stage"), "a.lua");

        rig.Events.Publish(TestRig.Event("engine.ignite", "v1", ut: 20, ("engine", ScriptValue.From("e0"))));
        rig.Slot(2);

        Assert.Multiple(() =>
        {
            Assert.That(anyVessel.IsCompleted, Is.True);
            Assert.That(explicitAny.IsCompleted, Is.True);
            Assert.That(active.IsCompleted, Is.True);
            Assert.That(byId.IsCompleted, Is.True);
            Assert.That(otherVessel.IsCompleted, Is.False);
            Assert.That(otherEvent.IsCompleted, Is.False);

            var result = anyVessel.Task.Result;
            Assert.That(result["type"].AsString(), Is.EqualTo("engine.ignite"));
            Assert.That(result["vesselId"].AsString(), Is.EqualTo("v1"));
            Assert.That(result["ut"].AsNumber(), Is.EqualTo(20));
            Assert.That(result["payload"]["engine"].AsString(), Is.EqualTo("e0"));
        });
    }

    [Test]
    public void WaitEvent_ActiveFilter_IgnoresEventsOfOtherVessels()
    {
        using var rig = new TestRig();
        rig.Bridge.SetActiveVessel("v1");
        rig.Slot(1);
        var active = rig.Scheduler.CreateWait("waitEvent", EventWait("stage", "active"), "a.lua");

        rig.Events.Publish(TestRig.Event("stage", "v2"));
        rig.Slot(2);
        Assert.That(active.IsCompleted, Is.False);

        rig.Events.Publish(TestRig.Event("stage", "v1"));
        rig.Slot(3);
        Assert.That(active.IsCompleted, Is.True);
    }

    [Test]
    public void WaitEvent_IgnoresEventsPublishedBeforeTheWaitWasCreated()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        rig.Events.Publish(TestRig.Event("stage", null, ut: 5));
        var wait = rig.Scheduler.CreateWait("waitEvent", EventWait("stage"), "a.lua");

        rig.Slot(2);
        Assert.That(wait.IsCompleted, Is.False, "the stage event predates the wait");

        rig.Events.Publish(TestRig.Event("stage", null, ut: 6));
        rig.Slot(3);

        Assert.That(wait.Task.Result["ut"].AsNumber(), Is.EqualTo(6));
    }

    [Test]
    public void WaitEvent_CreatedMidSlot_DoesNotSeeEventsStillQueuedInThatSlot()
    {
        using var rig = new TestRig();
        HostWait? wait = null;
        rig.Subscribe("a.lua", "event", "first", (host, _) =>
        {
            wait = host.Wait("waitEvent", EventWait("second"));
            return ScriptValue.Null;
        });
        rig.Events.Publish(TestRig.Event("first"));
        rig.Events.Publish(TestRig.Event("second"));

        rig.Slot(1);
        rig.Slot(2);

        Assert.That(wait!.IsCompleted, Is.False, "the second event was published before the wait existed");
    }

    [Test]
    public void WaitEvent_Timeout_CompletesWithNull()
    {
        using var rig = new TestRig();
        rig.Slot(10);
        var wait = rig.Scheduler.CreateWait("waitEvent", EventWait("stage", timeout: 2), "a.lua");

        rig.Slot(11);
        Assert.That(wait.IsCompleted, Is.False);
        rig.Slot(12);

        Assert.Multiple(() =>
        {
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(wait.Task.Result.IsNull, Is.True);
        });
    }

    [Test]
    public void WaitEvent_TargetedEvents_MatchOnlyTheTargetScript()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var forA = rig.Scheduler.CreateWait("waitEvent", EventWait("term.data"), "a.lua");
        var forB = rig.Scheduler.CreateWait("waitEvent", EventWait("term.data"), "b.lua");

        rig.Events.Publish(TestRig.Event("term.data", null, 0, ("$script", ScriptValue.From("b.lua")), ("termId", Num(1))));
        rig.Slot(2);

        Assert.Multiple(() =>
        {
            Assert.That(forA.IsCompleted, Is.False);
            Assert.That(forB.IsCompleted, Is.True);
            Assert.That(forB.Task.Result["payload"].AsMap().ContainsKey("$script"), Is.False);
        });
    }

    // ---------------------------------------------------------------- termRead

    [Test]
    public void TermRead_WithQueuedInput_CompletesImmediately()
    {
        using var rig = new TestRig();
        var terminal = rig.Terminals.Create("a.lua");
        terminal.EnqueueInput(new byte[] { 1, 2, 3 });

        var wait = rig.Scheduler.CreateWait("termRead", Num(terminal.Id), "a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(wait.Task.Result.AsBytes().ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(terminal.HasInput, Is.False);
            Assert.That(rig.Scheduler.Stats.PendingWaits, Is.Zero);
        });
    }

    [Test]
    public void TermRead_WithoutInput_CompletesInTheSlotAfterInputArrives()
    {
        using var rig = new TestRig();
        var terminal = rig.Terminals.Create("a.lua");
        rig.Slot(1);
        var wait = rig.Scheduler.CreateWait("termRead", TestRig.Map(("termId", Num(terminal.Id))), "a.lua");

        rig.Slot(2);
        Assert.That(wait.IsCompleted, Is.False);

        terminal.EnqueueInput(new byte[] { 0x61 });
        terminal.EnqueueInput(new byte[] { 0x62 });
        rig.Slot(3);

        Assert.That(wait.Task.Result.AsBytes().ToArray(), Is.EqualTo(new byte[] { 0x61, 0x62 }));
    }

    [Test]
    public void TermRead_OfAnUnknownTerminal_FailsTheWait()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var wait = rig.Scheduler.CreateWait("termRead", Num(99), "a.lua");

        rig.Slot(2);

        Assert.Multiple(() =>
        {
            Assert.That(wait.Task.IsFaulted, Is.True);
            Assert.That(wait.Task.Exception!.GetBaseException().Message, Is.EqualTo("terminal 99 not found"));
        });
    }

    // ---------------------------------------------------------------- act / cancel

    [Test]
    public void ActWait_IsCompletedOnlyByCompleteWait()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var wait = rig.Scheduler.CreateWait("act", ScriptValue.Null, "a.lua");

        rig.Slot(2);
        rig.Slot(3);
        Assert.That(wait.IsCompleted, Is.False);

        rig.Scheduler.CompleteWait(wait, CommandResult.Ok.ToScriptValue());
        rig.Scheduler.CompleteWait(wait, ScriptValue.Null);

        Assert.Multiple(() =>
        {
            Assert.That(wait.IsCompleted, Is.True);
            Assert.That(wait.Task.Result["ok"].AsBool(), Is.True);
            Assert.That(wait.Task.Result["outcome"].AsString(), Is.EqualTo("Ok"));
            Assert.That(rig.Scheduler.Stats.PendingWaits, Is.Zero);
        });
    }

    [Test]
    public void CancelWaits_CancelsOnlyThatScriptsWaits_AndDisposesPredicates()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var predicate = rig.Fn((_, _) => ScriptValue.False);
        var mine = rig.Scheduler.CreateWait("until", ScriptValue.Function(predicate), "a.lua");
        var alsoMine = rig.Scheduler.CreateWait("sleep", Num(100), "a.lua");
        var theirs = rig.Scheduler.CreateWait("sleep", Num(100), "b.lua");

        rig.Scheduler.CancelWaits("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(mine.Task.IsCanceled, Is.True);
            Assert.That(alsoMine.Task.IsCanceled, Is.True);
            Assert.That(theirs.IsCompleted, Is.False);
            Assert.That(predicate.IsDisposed, Is.True);
            Assert.That(rig.Scheduler.Stats.PendingWaits, Is.EqualTo(1));
        });
    }

    [Test]
    public void CancelWaits_WithIds_CancelsOnlyThoseWaits()
    {
        using var rig = new TestRig();
        var old = rig.Scheduler.CreateWait("sleep", Num(100), "a.lua");
        var oldIds = rig.Scheduler.PendingWaitIds("a.lua");
        var fresh = rig.Scheduler.CreateWait("sleep", Num(100), "a.lua");

        rig.Scheduler.CancelWaits("a.lua", oldIds);

        Assert.Multiple(() =>
        {
            Assert.That(old.Task.IsCanceled, Is.True);
            Assert.That(fresh.IsCompleted, Is.False);
            Assert.That(rig.Scheduler.PendingWaitIds("a.lua"), Is.EqualTo(new[] { fresh.Id }));
        });
    }

    [Test]
    public void AWaitCancelledBehindTheSchedulersBack_IsDroppedWithoutError()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var wait = rig.Scheduler.CreateWait("sleep", Num(100), "a.lua");
        wait.Cancel();

        rig.Slot(2);

        Assert.Multiple(() =>
        {
            Assert.That(rig.Scheduler.Stats.PendingWaits, Is.Zero);
            Assert.That(rig.Errors, Is.Empty);
        });
    }

    [Test]
    public void WaitsOfADisabledScript_StayPending()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        var wait = rig.Scheduler.CreateWait("nextFrame", ScriptValue.Null, "a.lua");
        rig.Subscribe("a.lua", "tick", "frame", (_, _) => throw new InvalidOperationException("boom"));
        rig.Slot(2);
        rig.Slot(3);
        rig.Slot(4);
        Assert.That(rig.Scheduler.IsDisabled("a.lua"), Is.True);

        var late = rig.Scheduler.CreateWait("nextFrame", ScriptValue.Null, "a.lua");
        rig.Slot(5);

        Assert.That(late.IsCompleted, Is.False);
        Assert.That(wait.IsCompleted, Is.True, "this one resolved in slot 2, before the script was disabled");
    }
}
