using System.Diagnostics;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Scheduling;
using KsaScripting.Core.Tests.Support;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Tests.Scheduling;

[TestFixture]
public sealed class SchedulerFaultTests
{
    [Test]
    public void ThreeConsecutiveFaults_DisableTheScript_ExactlyOnce_AndSkipFurtherInvocations()
    {
        using var rig = new TestRig();
        var calls = 0;
        rig.Subscribe("a.lua", "tick", "frame", (_, _) => { calls++; throw new InvalidOperationException("boom"); });
        rig.Subscribe("b.lua", "tick", "frame", (_, _) => ScriptValue.Null);

        for (var i = 1; i <= 6; i++)
            rig.Slot(i);

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(3));
            Assert.That(rig.Disabled, Has.Count.EqualTo(1));
            Assert.That(rig.Disabled[0].Script, Is.EqualTo("a.lua"));
            Assert.That(rig.Disabled[0].Reason, Does.Contain("fault limit reached (3 consecutive errors)"));
            Assert.That(rig.Scheduler.IsDisabled("a.lua"), Is.True);
            Assert.That(rig.Scheduler.IsDisabled("b.lua"), Is.False);
            Assert.That(rig.Errors, Has.Count.EqualTo(3));
            Assert.That(rig.Errors[0].Error, Is.TypeOf<ScriptRuntimeException>());
            Assert.That(rig.Scheduler.Stats.Faults, Is.EqualTo(3));
        });
    }

    [Test]
    public void ASuccessfulCall_ResetsTheConsecutiveCount()
    {
        using var rig = new TestRig();
        var calls = 0;
        rig.Subscribe("a.lua", "tick", "frame", (_, _) =>
        {
            calls++;
            if (calls is 3 or 6)
                return ScriptValue.Null;
            throw new InvalidOperationException("boom");
        });

        for (var i = 1; i <= 7; i++)
            rig.Slot(i);

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(7));
            Assert.That(rig.Scheduler.IsDisabled("a.lua"), Is.False);
            Assert.That(rig.Errors, Has.Count.EqualTo(5));
        });
    }

    [Test]
    public void ResetFaults_ReEnablesTheScript_AndClearsTheCount()
    {
        using var rig = new TestRig();
        var calls = 0;
        rig.Subscribe("a.lua", "tick", "frame", (_, _) => { calls++; throw new InvalidOperationException("boom"); });
        for (var i = 1; i <= 3; i++)
            rig.Slot(i);
        Assert.That(rig.Scheduler.IsDisabled("a.lua"), Is.True);

        rig.Scheduler.ResetFaults("a.lua");
        rig.Slot(4);
        Assert.Multiple(() =>
        {
            Assert.That(rig.Scheduler.IsDisabled("a.lua"), Is.False);
            Assert.That(calls, Is.EqualTo(4));
        });

        rig.Slot(5);
        rig.Slot(6);
        Assert.Multiple(() =>
        {
            Assert.That(rig.Scheduler.IsDisabled("a.lua"), Is.True, "three fresh faults disable it again");
            Assert.That(rig.Disabled, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void AsynchronousErrors_ReportedThroughTheFacade_CountTowardTheLimit()
    {
        using var rig = new TestRig();

        using (rig.Host.EnterScript("a.lua"))
        {
            for (var i = 0; i < 3; i++)
                rig.Host.ReportError(new ScriptRuntimeException("unhandled rejection"));
        }

        Assert.Multiple(() =>
        {
            Assert.That(rig.Scheduler.IsDisabled("a.lua"), Is.True);
            Assert.That(rig.Errors, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void ErrorsWithoutAScript_AreReportedButDisableNothing()
    {
        using var rig = new TestRig();

        for (var i = 0; i < 5; i++)
            rig.Host.ReportError(new ScriptRuntimeException("orphan"));

        Assert.Multiple(() =>
        {
            Assert.That(rig.Disabled, Is.Empty);
            Assert.That(rig.Errors, Has.Count.EqualTo(5));
            Assert.That(rig.Errors[0].Script, Is.Empty);
        });
    }

    [Test]
    public void AThrowingErrorCallback_DoesNotEscape()
    {
        using var rig = new TestRig();
        using var watchdog = new Watchdog();
        var scheduler = new Scheduler(TestRig.GenerousBudgets, rig.Hooks, rig.Events, rig.Runtimes, watchdog,
            (_, _) => throw new InvalidOperationException("callback exploded"));

        Assert.DoesNotThrow(() => scheduler.ReportError("a.lua", new ScriptRuntimeException("x")));
    }

    [Test]
    public void InvokeGuarded_OfADisabledScript_ReturnsNullWithoutCalling()
    {
        using var rig = new TestRig();
        var calls = 0;
        var fn = rig.Fn((_, _) => { calls++; return ScriptValue.From(1); });
        Assert.That(rig.Scheduler.InvokeGuarded("a.lua", fn, []).AsNumber(), Is.EqualTo(1));

        for (var i = 0; i < 3; i++)
            rig.Scheduler.ReportError("a.lua", new ScriptRuntimeException("x"));

        Assert.Multiple(() =>
        {
            Assert.That(rig.Scheduler.InvokeGuarded("a.lua", fn, []).IsNull, Is.True);
            Assert.That(calls, Is.EqualTo(1));
        });
    }

    [Test]
    public void InvokeGuarded_AFunctionOfAnUnavailableRuntime_ReturnsNull()
    {
        using var rig = new TestRig();
        var fn = rig.Fn((_, _) => ScriptValue.From(1));
        rig.Runtime.MarkUnavailable("native library missing");

        Assert.That(rig.Scheduler.InvokeGuarded("a.lua", fn, []).IsNull, Is.True);
    }

    [Test]
    public void InvokeGuarded_ADisposedCallable_ReturnsNullWithoutReportingAnError()
    {
        using var rig = new TestRig();
        var fn = rig.Fn((_, _) => ScriptValue.From(1));
        fn.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(rig.Scheduler.InvokeGuarded("a.lua", fn, []).IsNull, Is.True);
            Assert.That(rig.Errors, Is.Empty);
        });
    }

    [Test]
    public void InvokeGuarded_AFunctionOfAnUnknownRuntime_ReturnsNull()
    {
        using var rig = new TestRig();
        var foreign = ScriptCallable.Create("elsewhere", 1, "f", _ => { });

        Assert.That(rig.Scheduler.InvokeGuarded("a.lua", foreign, []).IsNull, Is.True);
    }

    // ---------------------------------------------------------------- watchdog integration

    private static void SpinUntilInterrupted(NullRuntime runtime)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            runtime.CheckInterrupt();
        throw new InvalidOperationException("the watchdog never interrupted the call");
    }

    [Test]
    public void AnAsyncHookThatSpins_IsInterrupted_ReportedAndTheScriptDisabled()
    {
        var config = new ScheduleConfig(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20), 3);
        using var rig = new TestRig(config);
        rig.Subscribe("spin.lua", "tick", "frame", (_, _) => { SpinUntilInterrupted(rig.Runtime); return ScriptValue.Null; });

        rig.Slot(1);

        Assert.Multiple(() =>
        {
            Assert.That(rig.Errors, Has.Count.EqualTo(1));
            Assert.That(rig.Errors[0].Error, Is.TypeOf<ScriptRuntimeException>());
            Assert.That(rig.Errors[0].Error.Message, Is.EqualTo("interrupted"));
            Assert.That(rig.Scheduler.IsDisabled("spin.lua"), Is.True);
            Assert.That(rig.Disabled, Has.Count.EqualTo(1));
            Assert.That(rig.Disabled[0].Reason, Does.StartWith("watchdog: exceeded 20 ms"));
            Assert.That(rig.Watchdog.Tripped, Is.EqualTo(1));
            Assert.That(rig.Scheduler.Stats.WatchdogTrips, Is.EqualTo(1));
            Assert.That(rig.Scheduler.InvokeDepth, Is.Zero);
        });
    }

    [Test]
    public void ASyncHookThatSpins_UsesTheSyncBudget_AndIsDisabled()
    {
        var config = new ScheduleConfig(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(30), 3);
        using var rig = new TestRig(config);
        rig.Subscribe("spin.lua", "event", "stage", (_, _) => { SpinUntilInterrupted(rig.Runtime); return ScriptValue.Null; },
            new BindingOptions(Sync: true));

        rig.Events.PublishSync(TestRig.Event("stage"));

        Assert.Multiple(() =>
        {
            Assert.That(rig.Scheduler.IsDisabled("spin.lua"), Is.True);
            Assert.That(rig.Disabled[0].Reason, Does.StartWith("watchdog: exceeded 20 ms"));
        });
    }

    [Test]
    public void AContinuationThatSpinsInAdvance_IsInterrupted_AndItsScriptDisabled()
    {
        var config = new ScheduleConfig(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20), 3);
        using var rig = new TestRig(config);
        rig.Slot(1);
        var starter = rig.Fn((host, _) =>
            rig.Runtime.Await(host.Wait("nextFrame", ScriptValue.Null), _ =>
            {
                SpinUntilInterrupted(rig.Runtime);
                return ScriptValue.Null;
            }));
        rig.Scheduler.InvokeGuarded("spin.lua", starter, []);

        rig.Slot(2);

        Assert.Multiple(() =>
        {
            Assert.That(rig.Scheduler.IsDisabled("spin.lua"), Is.True);
            Assert.That(rig.Disabled[0].Reason, Does.StartWith("watchdog"));
            Assert.That(rig.Watchdog.Tripped, Is.EqualTo(1));
        });
    }

    [Test]
    public void AFastHook_NeverTripsTheWatchdog()
    {
        using var rig = new TestRig(new ScheduleConfig(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), 3));
        rig.Subscribe("a.lua", "tick", "frame", (_, _) => ScriptValue.Null);

        for (var i = 1; i <= 20; i++)
            rig.Slot(i);

        Assert.Multiple(() =>
        {
            Assert.That(rig.Watchdog.Tripped, Is.Zero);
            Assert.That(rig.Scheduler.IsDisabled("a.lua"), Is.False);
        });
    }
}
