using Catcom.Core.Catalog;
using Catcom.Core.Commands;
using Catcom.Core.Hooks;
using Catcom.Core.Hosting;
using Catcom.Core.Persistence;
using Catcom.Core.Runtime;
using Catcom.Core.Scheduling;
using Catcom.Core.Terminals;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>The Lua runtime inside the real Core stack: catalog, scheduler, watchdog, hook registry and host facade.</summary>
[TestFixture]
public sealed class CoreIntegrationTests
{
    private string _dir = null!;
    private LuaRuntime _runtime = null!;
    private Watchdog _watchdog = null!;
    private Bridge _bridge = null!;
    private EventBus _events = null!;
    private Scheduler _scheduler = null!;
    private HostFacade _host = null!;
    private ScriptCatalog _catalog = null!;
    private readonly List<(string Script, Exception Error)> _errors = [];
    private long _step;

    [SetUp]
    public void SetUp()
    {
        _dir = Directory.CreateTempSubdirectory("ksa-lua-e2e-").FullName;
        _errors.Clear();
        _step = 0;
        _runtime = new LuaRuntime();
        var runtimes = new RuntimeSet([_runtime]);
        var hooks = new HookRegistry();
        _events = new EventBus();
        _watchdog = new Watchdog();
        var terminals = new TerminalRegistry(256, _events);
        var config = new ScheduleConfig(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), 3);
        _scheduler = new Scheduler(config, hooks, _events, runtimes, _watchdog, (script, error) => _errors.Add((script, error)));
        _bridge = new Bridge();
        _host = new HostFacade(_bridge, _scheduler, hooks, terminals, new ScriptStateStore());
        _runtime.Initialize(_host, new RuntimeOptions(_dir, Path.Combine(_dir, "stubs"), 0, true, null));
        _catalog = new ScriptCatalog(_dir, runtimes, hooks, new BindingStore(Path.Combine(_dir, "bindings.toml")), _scheduler, _host) { Terminals = terminals };
    }

    [TearDown]
    public void TearDown()
    {
        _watchdog.Dispose();
        _runtime.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Test]
    public void ScriptRunsEndToEnd_HooksTicksWaitsAndSolverActions()
    {
        File.WriteAllText(Path.Combine(_dir, "ascent.lua"), """
            local count = 0
            ksa.on("stage", function(ctx) count = count + 1 ksa.console.log("stage vessel=" .. tostring(ctx.vessel_id) .. " n=" .. count) end)
            ksa.every("frame", function(ctx) ksa.console.log("frame " .. tostring(ctx.frame)) end)
            return {
              main = function(ctx)
                ksa.console.log("main kind=" .. ctx.kind)
                local v = ksa.vessel("v1")
                v:set_throttle(0.8)
                ksa.sleep(0.05)
                ksa.console.log("slept")
                local r = v:set_attitude_mode("Prograde")
                ksa.console.log("attitude ok=" .. tostring(r.ok))
                local s = ksa.next_step()
                ksa.console.log("next_step ut=" .. tostring(s.ut))
              end,
            }
            """);
        _catalog.Scan();

        var info = _catalog.Load("ascent.lua");
        Assert.That(info.LastError, Is.Null);
        Assert.That(info.Exports, Is.EqualTo(new[] { "main" }));
        _catalog.Run("ascent.lua");

        for (var i = 0; i < 8; i++)
        {
            Slot(i * 0.02);
            if (i == 2)
                _events.Publish(new ScriptEvent(i * 0.02, "stage", "v1", new Dictionary<string, ScriptValue>()));
            if (i == 3)
                _bridge.Solvers.Single().OnResult(CommandResult.Ok);
        }

        var output = Output("ascent.lua");
        Assert.That(_errors, Is.Empty);
        Assert.That(_bridge.Executed.Select(c => (c.Action, c.VesselId, c.Value)), Is.EqualTo(new[] { ("vessel.throttle", "v1", 0.8) }));
        Assert.That(_bridge.Solvers.Single().Command.Action, Is.EqualTo("vessel.attitude_mode"));
        Assert.That(output, Does.Contain("main kind=main"));
        Assert.That(output, Does.Contain("stage vessel=v1 n=1"));
        Assert.That(output, Does.Contain("slept"));
        Assert.That(output, Does.Contain("attitude ok=true"));
        Assert.That(output.Any(l => l.StartsWith("next_step ut=", StringComparison.Ordinal)), Is.True);
        Assert.That(output.Count(l => l.StartsWith("frame ", StringComparison.Ordinal)), Is.EqualTo(8));

        _catalog.Reload("ascent.lua");
        _catalog.Unload("ascent.lua");
        Assert.That(_runtime.Stats.CallsTotal, Is.EqualTo(10), "main + 8 frame ticks + 1 stage event");
    }

    [Test]
    [CancelAfter(20000)]
    public void RunawayHook_IsInterruptedByTheWatchdog_AndTheScriptIsDisabled()
    {
        File.WriteAllText(Path.Combine(_dir, "spinner.lua"), "ksa.on('spin', function() while true do end end)\nreturn {}");
        _catalog.Scan();
        _catalog.Load("spinner.lua");
        _events.Publish(new ScriptEvent(1, "spin", null, new Dictionary<string, ScriptValue>()));

        Slot(1);

        Assert.That(_scheduler.IsDisabled("spinner.lua"), Is.True);
        Assert.That(_errors.Single().Error.Message, Is.EqualTo("interrupted"));
        // The runtime is still healthy afterwards.
        File.WriteAllText(Path.Combine(_dir, "healthy.lua"), "return { main = function() ksa.console.log('healthy') end }");
        _catalog.Scan();
        _catalog.Load("healthy.lua");
        _catalog.Run("healthy.lua");
        Assert.That(Output("healthy.lua"), Does.Contain("healthy"));
    }

    // The host's output ring prefixes every line with "[script] ".
    private string[] Output(string script)
        => _host.OutputFor(script).Select(e => System.Text.RegularExpressions.Regex.Replace(e.Message, @"^\[[^\]]*\] ", "")).ToArray();

    private void Slot(double ut)
    {
        _step++;
        _scheduler.StepSlot(new StepInfo(ut, 0.02, _step, (ulong)_step, true, 0.016));
    }

    private sealed class Bridge : IGameBridge
    {
        public List<SimCommand> Executed { get; } = [];

        public List<(SimCommand Command, Action<CommandResult> OnResult)> Solvers { get; } = [];

        public double UtSeconds => 0;

        public double SimDt => 0.02;

        public double Warp => 1;

        public bool Paused => false;

        public ulong FrameNumber => 0;

        public IReadOnlyList<EventDescriptor> EventCatalog => [];

        public ScriptValue Read(string handle, string path)
            => handle.StartsWith("vessel:", StringComparison.Ordinal) && path == "id" ? ScriptValue.From(handle["vessel:".Length..]) : ScriptValue.Null;

        public ScriptValue Snapshot(string handle) => ScriptValue.Null;

        public IReadOnlyList<string> List(string kind) => [];

        public CommandResult Execute(SimCommand command)
        {
            Executed.Add(command);
            return CommandResult.Ok;
        }

        public void QueueSolver(SimCommand command, Action<CommandResult> onResult) => Solvers.Add((command, onResult));

        public void ConsoleExec(string command)
        {
        }
    }
}
