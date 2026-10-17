using Catcom.Core.Commands;
using Catcom.Core.Hooks;
using Catcom.Core.Hosting;
using Catcom.Core.Logging;
using Catcom.Core.Runtime;
using Catcom.Core.Tests.Support;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Hosting;

[TestFixture]
public sealed class HostFacadeTests
{
    // ---------------------------------------------------------------- identity

    [Test]
    public void EnterScript_NestsAndRestoresThePreviousScript()
    {
        using var rig = new TestRig();
        Assert.That(rig.Host.CurrentScript, Is.Empty);

        using (rig.Host.EnterScript("a.lua"))
        {
            Assert.That(rig.Host.CurrentScript, Is.EqualTo("a.lua"));
            using (rig.Host.EnterScript("b.lua"))
            {
                Assert.That(rig.Host.CurrentScript, Is.EqualTo("b.lua"));
            }

            Assert.That(rig.Host.CurrentScript, Is.EqualTo("a.lua"));
        }

        Assert.That(rig.Host.CurrentScript, Is.Empty);
    }

    [Test]
    public void EnterScript_DisposingTwice_DoesNotPopTheOuterScope()
    {
        using var rig = new TestRig();
        using var outer = rig.Host.EnterScript("a.lua");
        var inner = rig.Host.EnterScript("b.lua");

        inner.Dispose();
        inner.Dispose();

        Assert.That(rig.Host.CurrentScript, Is.EqualTo("a.lua"));
    }

    [Test]
    public void TheConstructor_RegistersTheFacadeWithTheSchedulerAndHooks()
    {
        using var rig = new TestRig();

        Assert.Multiple(() =>
        {
            Assert.That(rig.Scheduler.Host, Is.SameAs(rig.Host));
            Assert.That(rig.Scheduler.Terminals, Is.SameAs(rig.Terminals));
            Assert.That(rig.Hooks.ActiveVesselProvider, Is.Not.Null);
        });
    }

    [Test]
    public void TheActiveVesselProvider_ReadsTimeActiveVesselId()
    {
        using var rig = new TestRig();
        Assert.That(rig.Hooks.ActiveVesselProvider!(), Is.Null);

        rig.Bridge.SetActiveVessel("kitty-1");
        Assert.That(rig.Hooks.ActiveVesselProvider!(), Is.EqualTo("kitty-1"));

        rig.Bridge.ThrowOnRead = new InvalidOperationException("no game");
        Assert.That(rig.Hooks.ActiveVesselProvider!(), Is.Null);
    }

    // ---------------------------------------------------------------- reads

    [Test]
    public void Read_RoutesBridgeHandlesToTheBridge()
    {
        using var rig = new TestRig();
        rig.Bridge.SetRead("vessel:v1", "position.cci", ScriptValue.From(42));

        var value = rig.Host.Read("vessel:v1", "position.cci");

        Assert.Multiple(() =>
        {
            Assert.That(value.AsNumber(), Is.EqualTo(42));
            Assert.That(rig.Bridge.ReadCalls, Is.EqualTo(new[] { ("vessel:v1", "position.cci") }));
        });
    }

    [Test]
    public void Read_SignalHandles_ReportTheCancelFlag()
    {
        using var rig = new TestRig();

        Assert.That(rig.Host.Read("signal:a.lua", "cancelled").AsBool(), Is.False);
        rig.Scheduler.SetCancelled("a.lua", true);
        Assert.Multiple(() =>
        {
            Assert.That(rig.Host.Read("signal:a.lua", "cancelled").AsBool(), Is.True);
            Assert.That(rig.Host.Read("signal:b.lua", "cancelled").AsBool(), Is.False);
            Assert.That(rig.Host.Read("signal:a.lua", "other").IsNull, Is.True);
            Assert.That(rig.Bridge.ReadCalls, Is.Empty, "signal reads never reach the bridge");
        });
    }

    [Test]
    public void Read_TerminalHandles_ResolveTheInfoMap()
    {
        using var rig = new TestRig();
        var terminal = rig.Terminals.Create("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(rig.Host.Read($"term:{terminal.Id}", "cols").AsNumber(), Is.EqualTo(80));
            Assert.That(rig.Host.Read($"term:{terminal.Id}", "rows").AsNumber(), Is.EqualTo(24));
            Assert.That(rig.Host.Read($"term:{terminal.Id}", "attached").AsNumber(), Is.Zero);
            Assert.That(rig.Host.Read($"term:{terminal.Id}", "").AsMap().Keys, Is.EquivalentTo(new[] { "cols", "rows", "attached" }));
            Assert.That(rig.Host.Read($"term:{terminal.Id}", "nope").IsNull, Is.True);
            Assert.That(rig.Host.Read("term:99", "cols").IsNull, Is.True);
            Assert.That(rig.Host.Read("term:abc", "cols").IsNull, Is.True, "an unparsable id falls through to the bridge, which knows nothing");
        });
    }

    [Test]
    public void Read_ABridgeThatThrows_YieldsNull_AndLogsOnce()
    {
        using var log = new LogCapture();
        using var rig = new TestRig();
        rig.Bridge.ThrowOnRead = new InvalidOperationException("vehicle disposed");

        var first = rig.Host.Read("vessel:v1", "mass");
        var second = rig.Host.Read("vessel:v1", "mass");

        Assert.Multiple(() =>
        {
            Assert.That(first.IsNull, Is.True);
            Assert.That(second.IsNull, Is.True);
            Assert.That(log.Entries.Count(e => e.Message.Contains("vehicle disposed")), Is.EqualTo(1));
        });
    }

    [Test]
    public void Snapshot_RoutesTerminalsAndBridgeHandles()
    {
        using var rig = new TestRig();
        var terminal = rig.Terminals.Create("a.lua");
        rig.Bridge.SetSnapshot("body:b1", TestRig.Map(("mass", ScriptValue.From(5))));

        Assert.Multiple(() =>
        {
            Assert.That(rig.Host.Snapshot($"term:{terminal.Id}")["cols"].AsNumber(), Is.EqualTo(80));
            Assert.That(rig.Host.Snapshot("body:b1")["mass"].AsNumber(), Is.EqualTo(5));
            Assert.That(rig.Host.Snapshot("body:unknown").IsNull, Is.True);
            Assert.That(rig.Host.Snapshot("term:99").IsNull, Is.True);
        });
    }

    // ---------------------------------------------------------------- list

    [Test]
    public void List_VesselsAndBodies_AreStringIdLists()
    {
        using var rig = new TestRig();
        rig.Bridge.SetList("vessels", "v1", "v2");

        var vessels = rig.Host.List("vessels");

        Assert.Multiple(() =>
        {
            Assert.That(vessels.AsList().Select(v => v.AsString()), Is.EqualTo(new[] { "v1", "v2" }));
            Assert.That(rig.Host.List("bodies").AsList(), Is.Empty);
            Assert.That(rig.Host.List("something-else").AsList(), Is.Empty);
        });
    }

    [Test]
    public void List_Events_ReturnsTheCatalogMaps()
    {
        using var rig = new TestRig();

        var events = rig.Host.List("events").AsList();

        Assert.That(events, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(events[0].AsMap().Keys, Is.EquivalentTo(new[] { "name", "description", "payloadFields", "producer" }));
            Assert.That(events[0]["name"].AsString(), Is.EqualTo("engine.ignite"));
            Assert.That(events[0]["description"].AsString(), Is.EqualTo("An engine ignited."));
            Assert.That(events[0]["payloadFields"].AsList().Select(f => f.AsString()), Is.EqualTo(new[] { "engine" }));
            Assert.That(events[0]["producer"].AsString(), Is.EqualTo("differ"));
            Assert.That(events[1]["payloadFields"].AsList(), Is.Empty);
        });
    }

    [Test]
    public void List_Scripts_ReturnsTheDistinctScriptsThatHaveBindings()
    {
        using var rig = new TestRig();
        rig.Subscribe("b.lua", "event", "e", (_, _) => ScriptValue.Null);
        rig.Subscribe("a.lua", "event", "e", (_, _) => ScriptValue.Null);
        rig.Subscribe("a.lua", "tick", "frame", (_, _) => ScriptValue.Null);

        Assert.That(rig.Host.List("scripts").AsList().Select(s => s.AsString()), Is.EqualTo(new[] { "b.lua", "a.lua" }));
    }

    // ---------------------------------------------------------------- act

    [Test]
    public void Act_FramePhase_ExecutesImmediately_WithTheBuiltCommand()
    {
        using var rig = new TestRig();
        rig.Bridge.ExecuteResult = _ => CommandResult.Invalid("bad throttle");

        var result = rig.Host.Act("vessel.throttle", "v1", -1, 0.5, [1, 2, 3], "tok", "aux");

        Assert.Multiple(() =>
        {
            Assert.That(result.Pending, Is.Null);
            Assert.That(result.Immediate!.Outcome, Is.EqualTo(CommandOutcome.Invalid));
            Assert.That(rig.Bridge.Executed, Has.Count.EqualTo(1));
            var command = rig.Bridge.Executed[0];
            Assert.That(command.VesselId, Is.EqualTo("v1"));
            Assert.That(command.Action, Is.EqualTo("vessel.throttle"));
            Assert.That(command.Ordinal, Is.EqualTo(-1));
            Assert.That(command.Value, Is.EqualTo(0.5));
            Assert.That(command.Values, Is.EqualTo(new[] { 1.0, 2.0, 3.0 }));
            Assert.That(command.Token, Is.EqualTo("tok"));
            Assert.That(command.Aux, Is.EqualTo("aux"));
        });
    }

    [Test]
    public void Act_NullVesselId_BecomesTheEmptyString()
    {
        using var rig = new TestRig();

        rig.Host.Act("debug.warp", null, -1, 2, null, null, null);

        Assert.That(rig.Bridge.Executed.Single().VesselId, Is.Empty);
    }

    [Test]
    public void Act_ABridgeThatThrows_ReturnsAFaultResult()
    {
        using var rig = new TestRig();
        rig.Bridge.ThrowOnExecute = new InvalidOperationException("executor exploded");

        var result = rig.Host.Act("vessel.stage", "v1", -1, 0, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Immediate!.Outcome, Is.EqualTo(CommandOutcome.Fault));
            Assert.That(result.Immediate.Message, Does.Contain("executor exploded"));
        });
    }

    [Test]
    public void Act_SolverPhase_ReturnsAPendingWait_CompletedWhenTheBridgeDeliversTheResult()
    {
        using var rig = new TestRig();
        rig.Slot(1);
        ActResult result;
        using (rig.Host.EnterScript("a.lua"))
            result = rig.Host.Act("vessel.burn", "v1", -1, 0, [100, 1, 0, 0], null, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Immediate, Is.Null);
            Assert.That(result.Pending, Is.Not.Null);
            Assert.That(result.Pending!.Kind, Is.EqualTo("act"));
            Assert.That(result.Pending.IsCompleted, Is.False);
            Assert.That(rig.Bridge.Executed, Is.Empty);
            Assert.That(rig.Bridge.QueuedSolvers, Has.Count.EqualTo(1));
            Assert.That(rig.Bridge.QueuedSolvers[0].Command.Action, Is.EqualTo("vessel.burn"));
        });

        rig.Bridge.CompleteSolver(0, CommandResult.Denied("no"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Pending!.IsCompleted, Is.True);
            Assert.That(result.Pending.Task.Result["ok"].AsBool(), Is.False);
            Assert.That(result.Pending.Task.Result["outcome"].AsString(), Is.EqualTo("Denied"));
            Assert.That(result.Pending.Task.Result["message"].AsString(), Is.EqualTo("no"));
        });
    }

    [Test]
    public void Act_SolverPhase_AQueueThatThrows_CompletesTheWaitWithAFault()
    {
        using var rig = new TestRig();
        rig.Bridge.ThrowOnQueueSolver = new InvalidOperationException("queue closed");

        var result = rig.Host.Act("debug.refill_fuel", "v1", -1, 0, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Pending!.IsCompleted, Is.True);
            Assert.That(result.Pending.Task.Result["outcome"].AsString(), Is.EqualTo("Fault"));
            Assert.That(rig.Scheduler.Stats.PendingWaits, Is.Zero);
        });
    }

    [Test]
    public void Act_DebugActions_AreDeniedWhenTheDebugNamespaceIsOff()
    {
        using var rig = new TestRig(options: new HostFacadeOptions(DebugNamespace: false));

        var frame = rig.Host.Act("debug.warp", null, -1, 2, null, null, null);
        var solver = rig.Host.Act("debug.refill_fuel", "v1", -1, 0, null, null, null);
        var normal = rig.Host.Act("vessel.stage", "v1", -1, 0, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(frame.Immediate!.Outcome, Is.EqualTo(CommandOutcome.Denied));
            Assert.That(frame.Immediate.Message, Is.EqualTo("debug namespace disabled in catcom.toml"));
            Assert.That(solver.Immediate!.Outcome, Is.EqualTo(CommandOutcome.Denied));
            Assert.That(solver.Pending, Is.Null);
            Assert.That(normal.Immediate!.IsSuccess, Is.True);
            Assert.That(rig.Bridge.Executed.Select(c => c.Action), Is.EqualTo(new[] { "vessel.stage" }));
            Assert.That(rig.Bridge.QueuedSolvers, Is.Empty);
        });
    }

    [Test]
    public void Act_WhenControlIsRestricted_OnlyTheActiveVesselMayBeControlled()
    {
        using var rig = new TestRig(options: new HostFacadeOptions(ControlAllVessels: false));
        rig.Bridge.SetActiveVessel("v1");

        var allowed = rig.Host.Act("vessel.stage", "v1", -1, 0, null, null, null);
        var denied = rig.Host.Act("vessel.stage", "v2", -1, 0, null, null, null);
        var solverDenied = rig.Host.Act("vessel.burn", "v2", -1, 0, null, null, null);
        var focus = rig.Host.Act("camera.focus", "v2", -1, 0, null, null, null);
        var noVessel = rig.Host.Act("debug.warp", null, -1, 2, null, null, null);
        var emptyVessel = rig.Host.Act("debug.warp", "", -1, 2, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(allowed.Immediate!.IsSuccess, Is.True);
            Assert.That(denied.Immediate!.Outcome, Is.EqualTo(CommandOutcome.Denied));
            Assert.That(denied.Immediate.Message, Is.EqualTo("control is restricted to the active vessel"));
            Assert.That(solverDenied.Immediate!.Outcome, Is.EqualTo(CommandOutcome.Denied));
            Assert.That(focus.Immediate!.IsSuccess, Is.True, "camera.focus is exempt");
            Assert.That(noVessel.Immediate!.IsSuccess, Is.True);
            Assert.That(emptyVessel.Immediate!.IsSuccess, Is.True);
            Assert.That(rig.Bridge.Executed.Select(c => c.VesselId), Is.EqualTo(new[] { "v1", "v2", "", "" }));
        });
    }

    [Test]
    public void Act_WhenControlIsRestricted_AndNoVesselIsActive_DeniesNamedVessels()
    {
        using var rig = new TestRig(options: new HostFacadeOptions(ControlAllVessels: false));

        var result = rig.Host.Act("vessel.stage", "v1", -1, 0, null, null, null);

        Assert.That(result.Immediate!.Outcome, Is.EqualTo(CommandOutcome.Denied));
    }

    // ---------------------------------------------------------------- waits and subscriptions

    [Test]
    public void Wait_CreatesTheSchedulerWait_AttributedToTheCurrentScript()
    {
        using var rig = new TestRig();
        HostWait wait;
        using (rig.Host.EnterScript("a.lua"))
            wait = rig.Host.Wait("nextFrame", ScriptValue.Null);

        Assert.Multiple(() =>
        {
            Assert.That(wait.Kind, Is.EqualTo("nextFrame"));
            Assert.That(rig.Scheduler.PendingWaitIds("a.lua"), Is.EqualTo(new[] { wait.Id }));
        });
        Assert.Throws<ArgumentException>(() => rig.Host.Wait("bogus", ScriptValue.Null));
    }

    [Test]
    public void Subscribe_AttributesTheBindingToTheEnteredScript_AsCodeDeclared()
    {
        using var rig = new TestRig();
        Guid id;
        using (rig.Host.EnterScript("ascent.lua"))
            id = rig.Host.Subscribe("event", "engine.ignite", rig.Fn((_, _) => ScriptValue.Null, "on_ignite"), new BindingOptions(Sync: true, Vessel: "active"));

        var binding = rig.Hooks.All().Single();
        Assert.Multiple(() =>
        {
            Assert.That(binding.Id, Is.EqualTo(id));
            Assert.That(binding.Script, Is.EqualTo("ascent.lua"));
            Assert.That(binding.Export, Is.EqualTo("on_ignite"));
            Assert.That(binding.Kind, Is.EqualTo(BindingKind.Event));
            Assert.That(binding.Key, Is.EqualTo("engine.ignite"));
            Assert.That(binding.FromCode, Is.True);
            Assert.That(binding.Options.Sync, Is.True);
            Assert.That(binding.Options.Vessel, Is.EqualTo("active"));
        });
    }

    [Test]
    public void Subscribe_OutsideAScript_Throws()
    {
        using var rig = new TestRig();

        Assert.Throws<InvalidOperationException>(() =>
            rig.Host.Subscribe("event", "stage", rig.Fn((_, _) => ScriptValue.Null), new BindingOptions()));
    }

    [Test]
    public void Subscribe_AnUnknownKind_Throws()
    {
        using var rig = new TestRig();
        using var scope = rig.Host.EnterScript("a.lua");

        Assert.Throws<ArgumentException>(() =>
            rig.Host.Subscribe("terminal", "x", rig.Fn((_, _) => ScriptValue.Null), new BindingOptions()));
    }

    [Test]
    public void Unsubscribe_RemovesTheBinding_AndDisposesTheCallable()
    {
        using var rig = new TestRig();
        var fn = rig.Fn((_, _) => ScriptValue.Null);
        Guid id;
        using (rig.Host.EnterScript("a.lua"))
            id = rig.Host.Subscribe("tick", "frame", fn, new BindingOptions());

        rig.Host.Unsubscribe(id);

        Assert.Multiple(() =>
        {
            Assert.That(rig.Hooks.All(), Is.Empty);
            Assert.That(fn.IsDisposed, Is.True);
        });
    }

    // ---------------------------------------------------------------- log / errors / output

    [Test]
    public void Log_WritesWithTheScriptPrefix_ToTheLogAndTheOutputRing_ClampingTheLevel()
    {
        using var log = new LogCapture();
        using var rig = new TestRig();
        var announced = new List<(string Script, LogEntry Entry)>();
        rig.Host.Output += (script, entry) => announced.Add((script, entry));

        using (rig.Host.EnterScript("a.lua"))
        {
            rig.Host.Log(1, "hello");
            rig.Host.Log(99, "clamped up");
            rig.Host.Log(-5, "clamped down");
        }

        var output = rig.Host.OutputFor("a.lua");
        Assert.Multiple(() =>
        {
            Assert.That(output, Is.EqualTo(new[]
            {
                new LogEntry(LogLevel.Info, "[a.lua] hello"),
                new LogEntry(LogLevel.Error, "[a.lua] clamped up"),
                new LogEntry(LogLevel.Debug, "[a.lua] clamped down"),
            }));
            Assert.That(log.Contains(LogLevel.Info, "[a.lua] hello"), Is.True);
            Assert.That(announced.Select(a => a.Script), Is.EqualTo(new[] { "a.lua", "a.lua", "a.lua" }));
            Assert.That(rig.Host.OutputFor("b.lua"), Is.Empty);
        });
    }

    [Test]
    public void Log_OutsideAScript_GoesToTheLogOnly()
    {
        using var log = new LogCapture();
        using var rig = new TestRig();

        rig.Host.Log(2, "hi");

        Assert.Multiple(() =>
        {
            Assert.That(log.Contains(LogLevel.Warn, "[host] hi"), Is.True);
            Assert.That(rig.Host.OutputFor(""), Is.Empty);
        });
    }

    [Test]
    public void TheOutputRing_IsBounded_ClearableAndIsolatedPerScript()
    {
        using var rig = new TestRig(options: new HostFacadeOptions(OutputLinesPerScript: 3));

        using (rig.Host.EnterScript("a.lua"))
        {
            for (var i = 1; i <= 5; i++)
                rig.Host.Log(1, $"line {i}");
        }

        using (rig.Host.EnterScript("b.lua"))
            rig.Host.Log(1, "other");

        Assert.Multiple(() =>
        {
            Assert.That(rig.Host.OutputFor("a.lua").Select(e => e.Message), Is.EqualTo(new[] { "[a.lua] line 3", "[a.lua] line 4", "[a.lua] line 5" }));
            Assert.That(rig.Host.OutputFor("b.lua"), Has.Count.EqualTo(1));
        });

        rig.Host.ClearOutput("a.lua");
        Assert.Multiple(() =>
        {
            Assert.That(rig.Host.OutputFor("a.lua"), Is.Empty);
            Assert.That(rig.Host.OutputFor("b.lua"), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void ReportError_CountsAgainstTheCurrentScript_AndAppearsOnceInTheOutput()
    {
        using var log = new LogCapture();
        using var rig = new TestRig();

        using (rig.Host.EnterScript("a.lua"))
            rig.Host.ReportError(new ScriptRuntimeException("unhandled rejection: nope", "stack line 1\nstack line 2"));

        var output = rig.Host.OutputFor("a.lua");
        Assert.Multiple(() =>
        {
            Assert.That(rig.Errors, Has.Count.EqualTo(1));
            Assert.That(rig.Errors[0].Script, Is.EqualTo("a.lua"));
            Assert.That(rig.Scheduler.Stats.Faults, Is.EqualTo(1));
            Assert.That(output, Has.Count.EqualTo(1), "the scheduler callback and ReportError must not double-record");
            Assert.That(output[0].Level, Is.EqualTo(LogLevel.Error));
            Assert.That(output[0].Message, Is.EqualTo("error: unhandled rejection: nope (stack line 1)"));
            Assert.That(log.Contains(LogLevel.Error, "[a.lua] error: unhandled rejection"), Is.True);
        });
    }

    [Test]
    public void RecordError_AppendsAnErrorLine_AndIgnoresTheSameExceptionTwice()
    {
        using var rig = new TestRig();
        var ex = new InvalidOperationException("plain failure");

        rig.Host.RecordError("a.lua", ex);
        rig.Host.RecordError("a.lua", ex);

        Assert.That(rig.Host.OutputFor("a.lua").Select(e => e.Message), Is.EqualTo(new[] { "error: plain failure" }));
    }

    [Test]
    public void InvokeErrors_ReportedBytheScheduler_LandInTheOutputRing()
    {
        using var rig = new TestRig();
        rig.Subscribe("a.lua", "tick", "frame", (_, _) => throw new InvalidOperationException("tick failed"));

        rig.Slot(1);

        Assert.That(rig.Host.OutputFor("a.lua").Single().Message, Does.Contain("tick failed"));
    }

    // ---------------------------------------------------------------- console / state / terminals

    [Test]
    public void ConsoleExec_ForwardsToTheBridge_AndSwallowsFailures()
    {
        using var log = new LogCapture();
        using var rig = new TestRig();

        rig.Host.ConsoleExec("help");

        Assert.That(rig.Bridge.ConsoleCommands, Is.EqualTo(new[] { "help" }));

        var failing = new ThrowingConsoleBridge();
        using var other = new TestRig();
        var host = new HostFacade(failing, other.Scheduler, other.Hooks, other.Terminals, other.State);
        Assert.DoesNotThrow(() => host.ConsoleExec("boom"));
        Assert.That(log.Contains(LogLevel.Warn, "boom"), Is.True);
    }

    private sealed class ThrowingConsoleBridge : IGameBridge
    {
        public ScriptValue Read(string handle, string path) => ScriptValue.Null;

        public ScriptValue Snapshot(string handle) => ScriptValue.Null;

        public IReadOnlyList<string> List(string kind) => [];

        public CommandResult Execute(SimCommand command) => CommandResult.Ok;

        public void QueueSolver(SimCommand command, Action<CommandResult> onResult)
        {
        }

        public double UtSeconds => 0;

        public double SimDt => 0;

        public double Warp => 1;

        public bool Paused => false;

        public ulong FrameNumber => 0;

        public IReadOnlyList<EventDescriptor> EventCatalog => [];

        public void ConsoleExec(string command) => throw new InvalidOperationException("console unavailable");
    }

    [Test]
    public void StateGetAndSet_AreScopedToTheCurrentScript()
    {
        using var rig = new TestRig();

        using (rig.Host.EnterScript("a.lua"))
        {
            rig.Host.StateSet("count", ScriptValue.From(7));
            Assert.That(rig.Host.StateGet("count").AsNumber(), Is.EqualTo(7));
        }

        using (rig.Host.EnterScript("b.lua"))
            Assert.That(rig.Host.StateGet("count").IsNull, Is.True);

        Assert.That(rig.State.Get("a.lua", "count").AsNumber(), Is.EqualTo(7));
    }

    [Test]
    public void StateSet_OfAFunction_ThrowsToTheCaller()
    {
        using var rig = new TestRig();
        using var scope = rig.Host.EnterScript("a.lua");

        Assert.Throws<ArgumentException>(() => rig.Host.StateSet("fn", ScriptValue.Function(rig.Fn((_, _) => ScriptValue.Null))));
    }

    [Test]
    public void TermWrite_AppendsToTheTerminal_AndUnknownIdsAreIgnored()
    {
        using var log = new LogCapture();
        using var rig = new TestRig();
        var terminal = rig.Terminals.Create("a.lua");

        rig.Host.TermWrite(terminal.Id, "hi"u8);
        Assert.DoesNotThrow(() => rig.Host.TermWrite(99, "x"u8));

        Assert.Multiple(() =>
        {
            Assert.That(System.Text.Encoding.UTF8.GetString(terminal.CopyRingBuffer()), Is.EqualTo("hi"));
            Assert.That(log.Contains(LogLevel.Warn, "unknown terminal 99"), Is.True);
        });
    }

    [Test]
    public void TermInfo_ReportsTheGridAndAttachedCount_OrNullForUnknownTerminals()
    {
        using var rig = new TestRig();
        var terminal = rig.Terminals.Create("a.lua");
        terminal.Attach(new NoopSink(), 100, 40);

        var info = rig.Host.TermInfo(terminal.Id);

        Assert.Multiple(() =>
        {
            Assert.That(info["cols"].AsNumber(), Is.EqualTo(100));
            Assert.That(info["rows"].AsNumber(), Is.EqualTo(40));
            Assert.That(info["attached"].AsNumber(), Is.EqualTo(1));
            Assert.That(rig.Host.TermInfo(99).IsNull, Is.True);
        });
    }

    private sealed class NoopSink : Catcom.Core.Terminals.ITerminalSink
    {
        public void Write(ReadOnlySpan<byte> bytes)
        {
        }

        public void Terminate(string message)
        {
        }
    }

    [Test]
    public void TermSetTitle_AndTermPolicy_ReachTheTerminal()
    {
        using var rig = new TestRig();
        var terminal = rig.Terminals.Create("a.lua");

        rig.Host.TermSetTitle(terminal.Id, "Door");
        rig.Host.TermPolicy(terminal.Id, true);

        Assert.Multiple(() =>
        {
            Assert.That(terminal.Title, Is.EqualTo("Door"));
            Assert.That(terminal.StopWhenDetached, Is.True);
            Assert.DoesNotThrow(() => rig.Host.TermSetTitle(99, "x"));
            Assert.DoesNotThrow(() => rig.Host.TermPolicy(99, true));
        });
    }

    [Test]
    public void HostFacadeOptions_FromConfig_CopiesTheAuthorityFlags()
    {
        var options = HostFacadeOptions.FromConfig(new Catcom.Core.Config.ScriptingConfig { ControlAllVessels = false, DebugNamespace = false });

        Assert.Multiple(() =>
        {
            Assert.That(options.ControlAllVessels, Is.False);
            Assert.That(options.DebugNamespace, Is.False);
            Assert.That(options.OutputLinesPerScript, Is.EqualTo(500));
        });
    }
}
