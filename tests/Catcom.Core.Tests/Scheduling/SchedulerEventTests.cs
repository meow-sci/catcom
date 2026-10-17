using Catcom.Core.Hooks;
using Catcom.Core.Hosting;
using Catcom.Core.Runtime;
using Catcom.Core.Tests.Support;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Scheduling;

[TestFixture]
public sealed class SchedulerEventTests
{
    private sealed record Seen(string Script, bool Sync, string Type, string? VesselId);

    private static NullFunction Recorder(List<Seen> log, string script)
        => (_, args) =>
        {
            var ctx = args[0];
            log.Add(new Seen(
                script,
                ctx["sync"].AsBool(),
                ctx["event"]["type"].AsString(),
                ctx["vesselId"].IsNull ? null : ctx["vesselId"].AsString()));
            return ScriptValue.Null;
        };

    [Test]
    public void EventContext_CarriesTheEventTheVesselAndTheSyncFlag()
    {
        using var rig = new TestRig();
        ScriptValue? ctx = null;
        rig.Scheduler.SetScriptPath("a.lua", "/s/a.lua");
        rig.Subscribe("a.lua", "event", "engine.ignite", (_, args) => { ctx = args[0]; return ScriptValue.Null; });
        rig.Events.Publish(TestRig.Event("engine.ignite", "v1", 7, ("engine", ScriptValue.From("e0"))));

        rig.Slot(8);

        Assert.That(ctx, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ctx!["$ctx"].AsBool(), Is.True);
            Assert.That(ctx["kind"].AsString(), Is.EqualTo("event"));
            Assert.That(ctx["script"]["name"].AsString(), Is.EqualTo("a.lua"));
            Assert.That(ctx["script"]["path"].AsString(), Is.EqualTo("/s/a.lua"));
            Assert.That(ctx["sync"].AsBool(), Is.False);
            Assert.That(ctx["vesselId"].AsString(), Is.EqualTo("v1"));
            Assert.That(ctx["event"]["type"].AsString(), Is.EqualTo("engine.ignite"));
            Assert.That(ctx["event"]["ut"].AsNumber(), Is.EqualTo(7));
            Assert.That(ctx["event"]["payload"]["engine"].AsString(), Is.EqualTo("e0"));
        });
    }

    [Test]
    public void PublishSync_RunsSyncSubscribersBeforeReturning_AndAsyncOnesOnlyInTheNextSlot()
    {
        using var rig = new TestRig();
        var log = new List<Seen>();
        rig.Subscribe("sync.lua", "event", "stage", Recorder(log, "sync.lua"), new BindingOptions(Sync: true));
        rig.Subscribe("async.lua", "event", "stage", Recorder(log, "async.lua"));

        rig.Events.PublishSync(TestRig.Event("stage", "v1"));

        Assert.That(log, Is.EqualTo(new[] { new Seen("sync.lua", true, "stage", "v1") }), "sync ran inline, async did not");

        rig.Slot(1);

        Assert.That(log, Is.EqualTo(new[]
        {
            new Seen("sync.lua", true, "stage", "v1"),
            new Seen("async.lua", false, "stage", "v1"),
        }), "the async queue serves only async subscribers; the sync one is not called twice");
    }

    [Test]
    public void PublishSync_WhileAScriptRuns_DefersTheSyncSubscribersToAsyncDelivery()
    {
        using var rig = new TestRig();
        var log = new List<Seen>();
        rig.Subscribe("sync.lua", "event", "stage", Recorder(log, "sync.lua"), new BindingOptions(Sync: true));
        rig.Subscribe("async.lua", "event", "stage", Recorder(log, "async.lua"));
        var depthInsideHook = -1;
        var published = false;
        rig.Subscribe("pub.lua", "tick", "frame", (_, _) =>
        {
            if (published)
                return ScriptValue.Null;

            published = true;
            depthInsideHook = rig.Scheduler.InvokeDepth;
            rig.Events.PublishSync(TestRig.Event("stage"));
            return ScriptValue.Null;
        });

        rig.Slot(1);
        Assert.Multiple(() =>
        {
            Assert.That(depthInsideHook, Is.GreaterThan(0));
            Assert.That(log, Is.Empty, "the event was published during the tick; delivery happens next slot");
        });

        rig.Slot(2);

        Assert.That(log.Select(s => (s.Script, s.Sync)), Is.EquivalentTo(new[] { ("sync.lua", false), ("async.lua", false) }),
            "the deferred sync subscriber still receives the event, asynchronously (ctx.sync = false)");

        rig.Slot(3);
        Assert.That(log, Has.Count.EqualTo(2), "and only once");
    }

    [Test]
    public void PublishSync_FromAContinuationRunningInAdvance_IsDeferredToo()
    {
        using var rig = new TestRig();
        var log = new List<Seen>();
        rig.Subscribe("sync.lua", "event", "stage", Recorder(log, "sync.lua"), new BindingOptions(Sync: true));
        rig.Slot(1);
        var starter = rig.Fn((host, _) => rig.Runtime.Await(host.Wait("nextFrame", ScriptValue.Null), _ =>
        {
            rig.Events.PublishSync(TestRig.Event("stage"));
            return ScriptValue.Null;
        }));
        rig.Scheduler.InvokeGuarded("pub.lua", starter, []);

        rig.Slot(2);
        Assert.That(log, Is.Empty, "the publisher was a script continuation; the subscriber waits for the next slot");

        rig.Slot(3);
        Assert.That(log, Is.EqualTo(new[] { new Seen("sync.lua", false, "stage", null) }));
    }

    [Test]
    public void PublishSync_ASyncSubscriberThatThrows_DoesNotEscape_AndIsFaultCounted()
    {
        using var rig = new TestRig();
        rig.Subscribe("a.lua", "event", "stage", (_, _) => throw new InvalidOperationException("boom"), new BindingOptions(Sync: true));

        Assert.DoesNotThrow(() => rig.Events.PublishSync(TestRig.Event("stage")));

        Assert.Multiple(() =>
        {
            Assert.That(rig.Errors, Has.Count.EqualTo(1));
            Assert.That(rig.Events.PendingCount, Is.EqualTo(1), "the event still goes to the async queue");
        });
    }

    [Test]
    public void PublishSync_AHookPublishingSyncFromInsideASyncHook_IsDeferred()
    {
        using var rig = new TestRig();
        var log = new List<Seen>();
        rig.Subscribe("outer.lua", "event", "outer", (_, _) =>
        {
            rig.Events.PublishSync(TestRig.Event("inner"));
            return ScriptValue.Null;
        }, new BindingOptions(Sync: true));
        rig.Subscribe("inner.lua", "event", "inner", Recorder(log, "inner.lua"), new BindingOptions(Sync: true));

        rig.Events.PublishSync(TestRig.Event("outer"));
        Assert.That(log, Is.Empty);

        rig.Slot(1);
        Assert.That(log, Is.EqualTo(new[] { new Seen("inner.lua", false, "inner", null) }));
    }

    [Test]
    public void VesselFilters_ApplyAtDeliveryTime_IncludingTheActiveVessel()
    {
        using var rig = new TestRig();
        var log = new List<Seen>();
        rig.Bridge.SetActiveVessel("v1");
        rig.Subscribe("active.lua", "event", "stage", Recorder(log, "active.lua"), new BindingOptions(Vessel: "active"));
        rig.Subscribe("v2.lua", "event", "stage", Recorder(log, "v2.lua"), new BindingOptions(Vessel: "v2"));
        rig.Subscribe("any.lua", "event", "stage", Recorder(log, "any.lua"));

        rig.Events.Publish(TestRig.Event("stage", "v1"));
        rig.Events.Publish(TestRig.Event("stage", "v2"));
        rig.Slot(1);

        Assert.That(log.Select(s => (s.Script, s.VesselId)), Is.EqualTo(new[]
        {
            ("active.lua", "v1"), ("any.lua", "v1"),
            ("v2.lua", "v2"), ("any.lua", "v2"),
        }));
    }

    [Test]
    public void TargetedEvents_ReachOnlyTheTargetScriptsSubscriptions()
    {
        using var rig = new TestRig();
        var log = new List<Seen>();
        rig.Subscribe("a.lua", "event", "term.data", Recorder(log, "a.lua"));
        rig.Subscribe("b.lua", "event", "term.data", Recorder(log, "b.lua"));

        rig.Events.Publish(TestRig.Event("term.data", null, 0, ("$script", ScriptValue.From("b.lua"))));
        rig.Events.Publish(TestRig.Event("term.data"));
        rig.Slot(1);

        Assert.That(log.Select(s => s.Script), Is.EqualTo(new[] { "b.lua", "a.lua", "b.lua" }));
    }

    [Test]
    public void ADisabledBinding_IsSkipped()
    {
        using var rig = new TestRig();
        var log = new List<Seen>();
        var id = rig.Subscribe("a.lua", "event", "stage", Recorder(log, "a.lua"));
        rig.Hooks.SetEnabled(id, false);

        rig.Events.Publish(TestRig.Event("stage"));
        rig.Slot(1);

        Assert.That(log, Is.Empty);
    }

    [Test]
    public void AHookThatUnsubscribesAnotherDuringDelivery_DoesNotInvokeTheDisposedCallable()
    {
        using var rig = new TestRig();
        var calls = new List<string>();
        Guid second = default;
        rig.Subscribe("a.lua", "event", "stage", (host, _) =>
        {
            calls.Add("first");
            host.Unsubscribe(second);
            return ScriptValue.Null;
        });
        second = rig.Subscribe("a.lua", "event", "stage", (_, _) => { calls.Add("second"); return ScriptValue.Null; });

        rig.Events.Publish(TestRig.Event("stage"));
        rig.Slot(1);

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(new[] { "first" }));
            Assert.That(rig.Errors, Is.Empty);
        });
    }

    [Test]
    public void FifoOrder_IsPreservedPerScript()
    {
        using var rig = new TestRig();
        var uts = new List<double>();
        rig.Subscribe("a.lua", "event", "e", (_, args) => { uts.Add(args[0]["event"]["ut"].AsNumber()); return ScriptValue.Null; });

        for (var i = 1; i <= 5; i++)
            rig.Events.Publish(TestRig.Event("e", null, i));
        rig.Slot(1);

        Assert.That(uts, Is.EqualTo(new double[] { 1, 2, 3, 4, 5 }));
    }

    // ---------------------------------------------------------------- DeliverNow / InvokeCommand

    [Test]
    public void DeliverNow_InvokesMatchingAsyncSubscriptionsImmediately_HonoringTargeting()
    {
        using var rig = new TestRig();
        var log = new List<Seen>();
        rig.Subscribe("a.lua", "event", "signal.cancel", Recorder(log, "a.lua"));
        rig.Subscribe("b.lua", "event", "signal.cancel", Recorder(log, "b.lua"));
        rig.Subscribe("a.lua", "event", "signal.cancel", Recorder(log, "a-sync.lua"), new BindingOptions(Sync: true));

        rig.Scheduler.DeliverNow(TestRig.Event("signal.cancel", null, 0, ("$script", ScriptValue.From("a.lua"))));

        Assert.Multiple(() =>
        {
            Assert.That(log, Is.EqualTo(new[] { new Seen("a.lua", false, "signal.cancel", null) }));
            Assert.That(rig.Events.PendingCount, Is.Zero, "DeliverNow bypasses the queue");
        });
    }

    [Test]
    public void InvokeCommand_CallsEveryMatchingBindingWithTheArgumentText()
    {
        using var rig = new TestRig();
        var received = new List<string>();
        rig.Subscribe("a.lua", "command", "greet", (_, args) => { received.Add("a:" + args[0].AsString()); return ScriptValue.Null; });
        rig.Subscribe("b.lua", "command", "Greet", (_, args) => { received.Add("b:" + args[0].AsString()); return ScriptValue.Null; });
        rig.Subscribe("c.lua", "command", "other", (_, _) => { received.Add("c"); return ScriptValue.Null; });

        var found = rig.Scheduler.InvokeCommand("greet", "hello world");
        var missing = rig.Scheduler.InvokeCommand("nope", "x");

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(missing, Is.False);
            Assert.That(received, Is.EqualTo(new[] { "a:hello world", "b:hello world" }));
        });
    }

    [Test]
    public void InvokeCommand_AFailingCommand_IsReportedNotThrown()
    {
        using var rig = new TestRig();
        rig.Subscribe("a.lua", "command", "fail", (_, _) => throw new InvalidOperationException("nope"));

        Assert.That(rig.Scheduler.InvokeCommand("fail", ""), Is.True);
        Assert.That(rig.Errors, Has.Count.EqualTo(1));
    }
}
