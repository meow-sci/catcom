using System.Text;
using Catcom.Core.Catalog;
using Catcom.Core.Hooks;
using Catcom.Core.Hosting;
using Catcom.Core.Runtime;
using Catcom.Core.Terminals;
using Catcom.Core.Tests.Support;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Catalog;

/// <summary>Whole-stack scenarios: catalog + scheduler + facade + terminals + the null runtime.</summary>
[TestFixture]
public sealed class ScriptCatalogIntegrationTests
{
    private sealed class RecordingSink : ITerminalSink
    {
        public List<string> Writes { get; } = [];

        public List<string> Terminations { get; } = [];

        public void Write(ReadOnlySpan<byte> bytes) => Writes.Add(Encoding.UTF8.GetString(bytes));

        public void Terminate(string message) => Terminations.Add(message);
    }

    [Test]
    public void ATerminalService_EchoesInputToItsSurface_UntilItIsStopped()
    {
        using var rig = new TestRig();
        rig.WriteScript("door.lua", "-- @ksa name=\"Door panel\" terminal=true\n");
        rig.Runtime.Register("door.lua", new Dictionary<string, NullFunction>
        {
            ["main"] = (host, args) =>
            {
                var termId = (int)args[0]["termId"].AsNumber();
                host.TermWrite(termId, "READY"u8);

                ScriptValue Loop()
                {
                    var wait = host.Wait("termRead", ScriptValue.From(termId));
                    return rig.Runtime.Await(wait, input =>
                    {
                        host.TermWrite(termId, input.AsBytes().Span);
                        return Loop();
                    });
                }

                return Loop();
            },
        });

        rig.Catalog.Start("door.lua");
        rig.Slot(1);
        var terminal = rig.Terminals.ForScript("door.lua")!;
        var sink = new RecordingSink();
        terminal.Attach(sink, 80, 24);
        Assert.That(sink.Writes.Single(), Is.EqualTo("\x1b[?2026hREADY\x1b[?2026l"), "a late surface sees the screen so far");
        sink.Writes.Clear();

        terminal.EnqueueInput("hi"u8.ToArray());
        rig.Slot(2);
        terminal.EnqueueInput("again"u8.ToArray());
        rig.Slot(3);

        Assert.Multiple(() =>
        {
            Assert.That(sink.Writes, Is.EqualTo(new[] { "\x1b[?2026hhi\x1b[?2026l", "\x1b[?2026hagain\x1b[?2026l" }));
            Assert.That(rig.Catalog.Get("door.lua")!.State, Is.EqualTo(ScriptState.Service));
        });

        rig.Catalog.Stop("door.lua");

        Assert.Multiple(() =>
        {
            Assert.That(sink.Terminations, Is.EqualTo(new[] { "[script exited]" }));
            Assert.That(rig.Runtime.PendingContinuations, Is.Zero);
            Assert.That(rig.Scheduler.Stats.PendingWaits, Is.Zero);
            Assert.That(rig.Catalog.Get("door.lua")!.State, Is.EqualTo(ScriptState.Idle));
        });
    }

    [Test]
    public void AnEventDrivenScript_ReactsToEventsThroughStoredBindings_AndKeepsWorkingAcrossReloads()
    {
        using var rig = new TestRig();
        rig.WriteScript("watch.lua");
        var seen = new List<string>();
        rig.Runtime.Register("watch.lua", new Dictionary<string, NullFunction>
        {
            ["on_ignite"] = (_, args) =>
            {
                seen.Add(args[0]["event"]["payload"]["engine"].AsString() + "@" + args[0]["vesselId"].AsString());
                return ScriptValue.Null;
            },
        });
        rig.Bindings.Save([new BindingRecord("watch.lua", "on_ignite", "event", "engine.ignite", false, "active", "ut", 0, true)]);
        rig.Bridge.SetActiveVessel("v1");
        rig.Catalog.Load("watch.lua");

        rig.Events.Publish(TestRig.Event("engine.ignite", "v1", 1, ("engine", ScriptValue.From("e0"))));
        rig.Events.Publish(TestRig.Event("engine.ignite", "v2", 2, ("engine", ScriptValue.From("e1"))));
        rig.Slot(3);

        rig.Catalog.Reload("watch.lua");
        rig.Events.Publish(TestRig.Event("engine.ignite", "v1", 4, ("engine", ScriptValue.From("e2"))));
        rig.Slot(5);

        Assert.That(seen, Is.EqualTo(new[] { "e0@v1", "e2@v1" }));
    }

    [Test]
    public void AScriptThatAwaitsAnEvent_ResumesWhenItArrives()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        var log = new List<string>();
        rig.Runtime.Register("a.lua", new Dictionary<string, NullFunction>
        {
            ["main"] = (host, _) =>
            {
                log.Add("start");
                var wait = host.Wait("waitEvent", TestRig.Map(("name", ScriptValue.From("dock")), ("timeoutSeconds", ScriptValue.From(60))));
                return rig.Runtime.Await(wait, ev =>
                {
                    log.Add(ev.IsNull ? "timeout" : "docked:" + ev["vesselId"].AsString());
                    return ScriptValue.Null;
                });
            },
        });
        rig.Slot(1);

        rig.Catalog.Run("a.lua");
        rig.Slot(2);
        Assert.That(log, Is.EqualTo(new[] { "start" }));

        rig.Events.Publish(TestRig.Event("dock", "v9"));
        rig.Slot(3);

        Assert.That(log, Is.EqualTo(new[] { "start", "docked:v9" }));
    }

    [Test]
    public void ASolverActionAwaitedByAScript_ResumesWithTheBridgesResult()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua");
        var outcomes = new List<string>();
        rig.Runtime.Register("a.lua", new Dictionary<string, NullFunction>
        {
            ["main"] = (host, _) =>
            {
                var result = host.Act("vessel.burn", "v1", -1, 0, [100, 1, 0, 0], null, null);
                return rig.Runtime.Await(result.Pending!, r =>
                {
                    outcomes.Add(r["outcome"].AsString());
                    return ScriptValue.Null;
                });
            },
        });
        rig.Slot(1);

        rig.Catalog.Run("a.lua");
        rig.Slot(2);
        Assert.That(outcomes, Is.Empty);

        rig.Bridge.CompleteSolver(0, Catcom.Core.Commands.CommandResult.Ok);
        rig.Slot(3);

        Assert.That(outcomes, Is.EqualTo(new[] { "Ok" }));
    }
}
