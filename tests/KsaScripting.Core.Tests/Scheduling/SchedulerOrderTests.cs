using KsaScripting.Core.Hooks;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Terminals;
using KsaScripting.Core.Tests.Support;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Tests.Scheduling;

[TestFixture]
public sealed class SchedulerOrderTests
{
    private sealed class RecordingSink(List<string> log) : ITerminalSink
    {
        public void Write(ReadOnlySpan<byte> bytes) => log.Add("flush");

        public void Terminate(string message) => log.Add("terminate");
    }

    [Test]
    public void StepSlot_RunsWaitsEventsTicksServicesAdvanceAndFlush_InThatOrder()
    {
        using var rig = new TestRig();
        var log = new List<string>();
        const string script = "ascent.lua";

        // The terminal is flushed last; a hook writes into it so there is something to flush.
        var terminal = rig.Terminals.Create(script);
        terminal.Attach(new RecordingSink(log), 80, 24);
        log.Clear();

        rig.Subscribe(script, "event", "stage", (host, _) =>
        {
            log.Add("event");
            host.TermWrite(terminal.Id, "hello"u8);
            return ScriptValue.Null;
        });
        rig.Subscribe(script, "tick", "frame", (_, _) => { log.Add("tick:frame"); return ScriptValue.Null; });
        rig.Subscribe(script, "tick", "step", (_, _) => { log.Add("tick:step"); return ScriptValue.Null; });
        rig.Subscribe(script, "service", "main", (_, args) =>
        {
            log.Add("service:" + args[0]["kind"].AsString());
            return ScriptValue.Null;
        });

        // A wait whose continuation runs in Advance, and an `until` predicate that resolves in the waits step.
        var starter = rig.Fn((host, _) =>
        {
            var until = host.Wait("until", ScriptValue.Function(rig.Fn((_, _) => { log.Add("until"); return ScriptValue.True; })));
            return rig.Runtime.Await(until, _ => { log.Add("continuation"); return ScriptValue.Null; });
        });
        rig.Scheduler.InvokeGuarded(script, starter, []);
        rig.Events.Publish(TestRig.Event("stage"));

        rig.Slot(10);

        Assert.That(log, Is.EqualTo(new[] { "until", "event", "tick:frame", "tick:step", "service:terminal", "continuation", "flush" }));
    }

    [Test]
    public void StepSlot_NeverThrows_EvenWhenEveryStageHasNothingToDo()
    {
        using var rig = new TestRig();

        Assert.DoesNotThrow(() =>
        {
            for (var i = 0; i < 3; i++)
                rig.Slot(i);
        });
        Assert.That(rig.Scheduler.Stats.Slots, Is.EqualTo(3));
    }

    [Test]
    public void Stats_CountSlotsInvocationsAndPendingWork()
    {
        using var rig = new TestRig();
        rig.Subscribe("a.lua", "tick", "frame", (_, _) => ScriptValue.Null);
        rig.Scheduler.CreateWait("act", ScriptValue.Null, "a.lua");
        rig.Events.Publish(TestRig.Event("nobody-listens"));

        var before = rig.Scheduler.Stats;
        rig.Slot(1);
        rig.Slot(2);
        var after = rig.Scheduler.Stats;

        Assert.Multiple(() =>
        {
            Assert.That(before.PendingEvents, Is.EqualTo(1));
            Assert.That(after.Slots, Is.EqualTo(2));
            Assert.That(after.Invocations, Is.EqualTo(2));
            Assert.That(after.PendingWaits, Is.EqualTo(1), "the act wait is only completed by CompleteWait");
            Assert.That(after.PendingEvents, Is.Zero);
            Assert.That(after.LastSlotMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(after.MaxSlotMs, Is.GreaterThanOrEqualTo(after.LastSlotMs));
            Assert.That(after.Faults, Is.Zero);
        });
    }

    [Test]
    public void Services_StartOncePerBinding_AndAReloadedBindingStartsAgain()
    {
        using var rig = new TestRig();
        var starts = 0;
        var id = rig.Subscribe("a.lua", "service", "main", (_, _) => { starts++; return ScriptValue.Null; });

        rig.Slot(1);
        rig.Slot(2);
        Assert.That(starts, Is.EqualTo(1));

        rig.Hooks.Remove(id);
        rig.Subscribe("a.lua", "service", "main", (_, _) => { starts++; return ScriptValue.Null; });
        rig.Slot(3);
        Assert.That(starts, Is.EqualTo(2));
    }

    [Test]
    public void Services_ReceiveAServiceContext_WithSignalAndNoTerminalId()
    {
        using var rig = new TestRig();
        ScriptValue? ctx = null;
        rig.Scheduler.SetScriptPath("a.lua", "/scripts/a.lua");
        rig.Subscribe("a.lua", "service", "main", (_, args) => { ctx = args[0]; return ScriptValue.Null; });

        rig.Slot(1);

        Assert.That(ctx, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(ctx!["$ctx"].AsBool(), Is.True);
            Assert.That(ctx["kind"].AsString(), Is.EqualTo("service"));
            Assert.That(ctx["script"]["name"].AsString(), Is.EqualTo("a.lua"));
            Assert.That(ctx["script"]["path"].AsString(), Is.EqualTo("/scripts/a.lua"));
            Assert.That(ctx["signal"].AsString(), Is.EqualTo("a.lua"));
            Assert.That(ctx.AsMap().ContainsKey("termId"), Is.False);
        });
    }

    [Test]
    public void CurrentScript_IsTheInvokedScriptDuringEveryHook_AndEmptyAfter()
    {
        using var rig = new TestRig();
        var seen = new List<string>();
        rig.Subscribe("a.lua", "tick", "frame", (host, _) => { seen.Add(host.CurrentScript); return ScriptValue.Null; });
        rig.Subscribe("b.lua", "tick", "frame", (host, _) => { seen.Add(host.CurrentScript); return ScriptValue.Null; });

        rig.Slot(1);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.EquivalentTo(new[] { "a.lua", "b.lua" }));
            Assert.That(rig.Host.CurrentScript, Is.Empty);
        });
    }

    [Test]
    public void BuildContext_AddsSignalAndTermIdOnlyForServiceMainAndTerminalKinds()
    {
        using var rig = new TestRig();
        var terminal = rig.Terminals.Create("a.lua");

        var tick = rig.Scheduler.BuildContext("tick", "a.lua");
        var main = rig.Scheduler.BuildContext("main", "a.lua", new Dictionary<string, ScriptValue> { ["extra"] = ScriptValue.From(1) });
        var other = rig.Scheduler.BuildContext("main", "b.lua");

        Assert.Multiple(() =>
        {
            Assert.That(tick.AsMap().ContainsKey("signal"), Is.False);
            Assert.That(tick.AsMap().ContainsKey("termId"), Is.False);
            Assert.That(main["signal"].AsString(), Is.EqualTo("a.lua"));
            Assert.That(main["termId"].AsNumber(), Is.EqualTo(terminal.Id));
            Assert.That(main["extra"].AsNumber(), Is.EqualTo(1));
            Assert.That(other.AsMap().ContainsKey("termId"), Is.False);
        });
    }
}
