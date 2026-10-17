using Catcom.Core.Hosting;
using Catcom.Core.Tests.Support;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Scheduling;

[TestFixture]
public sealed class SchedulerTickTests
{
    [Test]
    public void FrameTicks_FireEverySlot_AndStepTicksOnlyOnLandedSteps()
    {
        using var rig = new TestRig();
        var frames = 0;
        var steps = 0;
        rig.Subscribe("a.lua", "tick", "frame", (_, _) => { frames++; return ScriptValue.Null; });
        rig.Subscribe("a.lua", "tick", "step", (_, _) => { steps++; return ScriptValue.Null; });

        rig.Slot(1, landed: true);
        rig.Slot(1, landed: false);
        rig.Slot(1, landed: false);
        rig.Slot(1.1, landed: true);

        Assert.Multiple(() =>
        {
            Assert.That(frames, Is.EqualTo(4));
            Assert.That(steps, Is.EqualTo(2));
        });
    }

    [Test]
    public void TickContext_CarriesTheStepFields()
    {
        using var rig = new TestRig();
        rig.Scheduler.SetScriptPath("a.lua", "/s/a.lua");
        ScriptValue? ctx = null;
        rig.Subscribe("a.lua", "tick", "step", (_, args) => { ctx = args[0]; return ScriptValue.Null; });

        var step = rig.Slot(42.5, dt: 0.04);

        Assert.That(ctx, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ctx!["$ctx"].AsBool(), Is.True);
            Assert.That(ctx["kind"].AsString(), Is.EqualTo("tick"));
            Assert.That(ctx["script"]["name"].AsString(), Is.EqualTo("a.lua"));
            Assert.That(ctx["ut"].AsNumber(), Is.EqualTo(42.5));
            Assert.That(ctx["dt"].AsNumber(), Is.EqualTo(0.04));
            Assert.That(ctx["stepIndex"].AsNumber(), Is.EqualTo(step.StepIndex));
            Assert.That(ctx["frame"].AsNumber(), Is.EqualTo(step.Frame));
        });
    }

    [Test]
    public void EveryTick_OnTheUtClock_FiresAtEachInterval_WithoutBurstingAfterABigJump()
    {
        using var rig = new TestRig();
        var fired = new List<double>();
        rig.Subscribe("a.lua", "tick", "every", (_, args) => { fired.Add(args[0]["ut"].AsNumber()); return ScriptValue.Null; },
            new BindingOptions(Clock: "ut", IntervalSeconds: 1));

        foreach (var ut in new[] { 0.0, 0.5, 1.0, 1.5, 2.0, 3.0 })
            rig.Slot(ut);
        Assert.That(fired, Is.EqualTo(new[] { 1.0, 2.0, 3.0 }));

        rig.Slot(100);
        Assert.That(fired, Is.EqualTo(new[] { 1.0, 2.0, 3.0, 100.0 }), "one firing for the jump, not 97");

        rig.Slot(100.5);
        rig.Slot(101);
        Assert.That(fired, Is.EqualTo(new[] { 1.0, 2.0, 3.0, 100.0, 101.0 }));
    }

    [Test]
    public void EveryTick_OnTheWallClock_UsesTheInjectedClock()
    {
        using var rig = new TestRig();
        var fired = 0;
        rig.Subscribe("a.lua", "tick", "every", (_, _) => { fired++; return ScriptValue.Null; },
            new BindingOptions(Clock: "wall", IntervalSeconds: 2));

        rig.Wall = 10;
        rig.Slot(1);
        rig.Wall = 11.9;
        rig.Slot(2);
        Assert.That(fired, Is.Zero);
        rig.Wall = 12;
        rig.Slot(3);
        Assert.That(fired, Is.EqualTo(1));
    }

    [Test]
    public void EveryTick_OnTheRenderClock_AccumulatesPlayerDt()
    {
        using var rig = new TestRig();
        var fired = 0;
        rig.Subscribe("a.lua", "tick", "every", (_, _) => { fired++; return ScriptValue.Null; },
            new BindingOptions(Clock: "render", IntervalSeconds: 1));

        rig.Slot(1, playerDt: 0.5);   // first seen at render 0.5, due at 1.5
        rig.Slot(1, playerDt: 0.5);
        Assert.That(fired, Is.Zero);
        rig.Slot(1, playerDt: 0.5);
        Assert.That(fired, Is.EqualTo(1));
    }

    [Test]
    public void EveryTick_WithANonPositiveInterval_FiresEverySlot()
    {
        using var rig = new TestRig();
        var fired = 0;
        rig.Subscribe("a.lua", "tick", "every", (_, _) => { fired++; return ScriptValue.Null; },
            new BindingOptions(IntervalSeconds: 0));

        rig.Slot(1);
        rig.Slot(1);
        rig.Slot(1);

        Assert.That(fired, Is.EqualTo(3));
    }

    [Test]
    public void TicksOfDisabledBindings_AndDisabledScripts_DoNotRun()
    {
        using var rig = new TestRig();
        var ran = 0;
        var id = rig.Subscribe("a.lua", "tick", "frame", (_, _) => { ran++; return ScriptValue.Null; });
        rig.Slot(1);
        rig.Hooks.SetEnabled(id, false);
        rig.Slot(2);

        Assert.That(ran, Is.EqualTo(1));
    }

    [Test]
    public void ForgetScript_DropsTheEveryDueTime_SoAReloadedTickRestartsItsInterval()
    {
        using var rig = new TestRig();
        var fired = 0;
        var options = new BindingOptions(IntervalSeconds: 10);
        rig.Subscribe("a.lua", "tick", "every", (_, _) => { fired++; return ScriptValue.Null; }, options);
        rig.Slot(0);

        rig.Hooks.RemoveAll("a.lua");
        rig.Scheduler.ForgetScript("a.lua");
        rig.Subscribe("a.lua", "tick", "every", (_, _) => { fired++; return ScriptValue.Null; }, options);
        rig.Slot(5);
        rig.Slot(14);
        Assert.That(fired, Is.Zero, "first seen at 5, due at 15");
        rig.Slot(15);
        Assert.That(fired, Is.EqualTo(1));
    }
}
