using System.Diagnostics;
using System.Text.RegularExpressions;
using Catcom.Core.Catalog;
using Catcom.Core.Commands;
using Catcom.Core.Hooks;
using Catcom.Core.Hosting;
using Catcom.Core.Persistence;
using Catcom.Core.Runtime;
using Catcom.Core.Scheduling;
using Catcom.Core.Terminals;
using Catcom.Core.Values;

namespace Catcom.Runtime.V8.Tests;

/// <summary>
///     The V8 runtime inside the real Core stack: catalog, scheduler, watchdog, hook registry and host facade over a fake game
///     bridge. The test drives <see cref="Scheduler.StepSlot"/> by hand, so there is no timer and no sleeping; asynchronous
///     continuations are awaited by stepping slots until the expected output appears (bounded by a deadline).
/// </summary>
[TestFixture]
public sealed class CoreIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private string _dir = null!;
    private V8ScriptRuntime _runtime = null!;
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
        _dir = Directory.CreateTempSubdirectory("ksa-v8-e2e-").FullName;
        _errors.Clear();
        _step = 0;
        _runtime = new V8ScriptRuntime();
        var runtimes = new RuntimeSet([_runtime]);
        var hooks = new HookRegistry();
        _events = new EventBus();
        _watchdog = new Watchdog();
        var terminals = new TerminalRegistry(256, _events);
        var config = new ScheduleConfig(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250), 3);
        _scheduler = new Scheduler(config, hooks, _events, runtimes, _watchdog, (script, error) => _errors.Add((script, error)));
        _bridge = new Bridge();
        _host = new HostFacade(_bridge, _scheduler, hooks, terminals, new ScriptStateStore());
        _runtime.Initialize(_host, new RuntimeOptions(_dir, V8TestBase.SharedStubsFolder, 0, true, null));
        _catalog = new ScriptCatalog(_dir, runtimes, hooks, new BindingStore(Path.Combine(_dir, "bindings.toml")), _scheduler, _host) { Terminals = terminals };
    }

    [TearDown]
    public void TearDown()
    {
        _watchdog.Dispose();
        _runtime.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is not a test failure.
        }
    }

    [Test]
    public void ScriptRunsEndToEnd_HooksTicksWaitsAndSolverActions()
    {
        WriteScript("ascent.ts", """
            import { ksa } from "ksa";
            let count = 0;
            ksa.on("stage", (ctx) => { count++; ksa.console.log(`stage vessel=${ctx.vesselId} n=${count}`); });
            ksa.every("frame", (ctx) => { ksa.console.log(`frame ${ctx.frame}`); });
            export async function main(ctx) {
              ksa.console.log("main kind=" + ctx.kind);
              const v = ksa.vessel("v1");
              v.setThrottle(0.8);
              await ksa.sleep(0.05);
              ksa.console.log("slept");
              const r = await v.setAttitudeMode("Prograde");
              ksa.console.log("attitude ok=" + r.ok);
              const s = await ksa.nextStep();
              ksa.console.log("next_step ut=" + s.ut);
            }
            """);
        _catalog.Scan();

        var info = _catalog.Load("ascent.ts");
        Assert.That(info.LastError, Is.Null);
        Assert.That(info.Exports, Is.EqualTo(new[] { "main" }));
        _catalog.Run("ascent.ts");

        Assert.That(Output("ascent.ts"), Is.EqualTo(new[] { "main kind=main" }), "main runs synchronously up to its first await");
        Assert.That(_bridge.Executed.Select(c => (c.Action, c.VesselId, c.Value)), Is.EqualTo(new[] { ("vessel.throttle", "v1", 0.8) }));

        // The sleep is due 0.05 s of game time after the start (ut 0): the continuation runs inside StepSlot's Advance.
        StepUntil(() => Output("ascent.ts").Contains("slept"), "the sleep to resolve");
        Assert.That(_bridge.Solvers.Single().Command.Action, Is.EqualTo("vessel.attitude_mode"));
        Assert.That(_bridge.Solvers.Single().Command.Token, Is.EqualTo("Prograde"));

        _events.Publish(new ScriptEvent(_ut, "stage", "v1", new Dictionary<string, ScriptValue>()));
        Slot();
        Assert.That(Output("ascent.ts"), Does.Contain("stage vessel=v1 n=1"), "an event hook runs inside the slot that delivers it");

        _bridge.Solvers.Single().OnResult(CommandResult.Ok);
        StepUntil(() => Output("ascent.ts").Contains("attitude ok=true"), "the solver result to reach the script");
        StepUntil(() => Output("ascent.ts").Any(l => l.StartsWith("next_step ut=", StringComparison.Ordinal)), "nextStep to resolve");

        var output = Output("ascent.ts");
        Assert.That(_errors, Is.Empty);
        Assert.That(output.Count(l => l.StartsWith("frame ", StringComparison.Ordinal)), Is.EqualTo((int)_step), "one frame tick per slot");
        Assert.That(Array.IndexOf(output, "slept"), Is.LessThan(Array.IndexOf(output, "attitude ok=true")));

        var callsBefore = _runtime.Stats.CallsTotal;
        _catalog.Reload("ascent.ts");
        _catalog.Unload("ascent.ts");
        Assert.That(_catalog.Get("ascent.ts")!.State, Is.EqualTo(ScriptState.Idle));
        Assert.That(callsBefore, Is.GreaterThanOrEqualTo(_step + 2), "the frame ticks, the stage event and main were all calls into the runtime");
        Assert.That(_runtime.LoadedScriptCount, Is.EqualTo(0), "unloading releases the engine");
    }

    [Test]
    public void ErrorsInHooksAndContinuations_AreReportedWithTheirTypeScriptLocation_AndCountTowardTheFaultLimit()
    {
        var path = WriteScript("faulty.ts", """
            import { ksa } from "ksa";
            interface Unused { a: number }
            ksa.on("sync.fail", () => {
              throw new Error("hook failed");
            });
            export async function main() {
              await ksa.sleep(0.01);
              throw new Error("continuation failed");
            }
            """);
        _catalog.Scan();
        _catalog.Load("faulty.ts");
        _catalog.Run("faulty.ts");

        _events.Publish(new ScriptEvent(0, "sync.fail", null, new Dictionary<string, ScriptValue>()));
        Slot();
        StepUntil(() => _errors.Count >= 2, "both errors to be reported");

        Assert.That(_errors.Select(e => e.Script), Is.All.EqualTo("faulty.ts"));
        var hook = (ScriptRuntimeException)_errors.Single(e => e.Error.Message.Contains("hook failed")).Error;
        var continuation = (ScriptRuntimeException)_errors.Single(e => e.Error.Message.Contains("continuation failed")).Error;
        Assert.That((hook.File, hook.Line), Is.EqualTo((path, 4)));
        Assert.That((continuation.File, continuation.Line), Is.EqualTo((path, 8)));
        Assert.That(_scheduler.IsDisabled("faulty.ts"), Is.False, "two consecutive faults are below the limit of three");
    }

    [Test]
    public void UnobservedRejection_ReachesTheSchedulersErrorCallback_AttributedToTheScript()
    {
        var path = WriteScript("loose.ts", """
            import { ksa } from "ksa";
            export function main() {
              ksa.sleep(0.01).then(() => {
                throw new Error("nobody awaits me");
              });
            }
            """);
        _catalog.Scan();
        _catalog.Run("loose.ts");
        Assert.That(_errors, Is.Empty, "main itself returned normally");

        StepUntil(() => _errors.Count > 0, "the unhandled rejection to be reported");

        var (script, error) = _errors.Single();
        Assert.That(script, Is.EqualTo("loose.ts"));
        Assert.That(error.Message, Is.EqualTo("Unhandled promise rejection: Error: nobody awaits me"));
        Assert.That(((ScriptRuntimeException)error).File, Is.EqualTo(path));
        Assert.That(((ScriptRuntimeException)error).Line, Is.EqualTo(4));
        Assert.That(_scheduler.Stats.Faults, Is.EqualTo(1));
        Assert.That(_scheduler.IsDisabled("loose.ts"), Is.False, "one fault is below the limit");
    }

    [Test]
    public void LoadFailure_IsRecordedOnTheScript_AndAFixedFileLoadsAgain()
    {
        WriteScript("broken.ts", "const fine = 1;\n\nexport const broken = ;\n");
        _catalog.Scan();

        var failed = _catalog.Load("broken.ts");

        Assert.That(failed.State, Is.EqualTo(ScriptState.Error));
        Assert.That(failed.LastError, Does.Contain("3"), "the error names line 3");

        WriteScript("broken.ts", "export function main() { }\n");
        var fixedInfo = _catalog.Load("broken.ts");

        Assert.That(fixedInfo.LastError, Is.Null);
        Assert.That(fixedInfo.Exports, Is.EqualTo(new[] { "main" }));
    }

    [Test]
    public void ServiceStartAndStop_DriveCtxSignal_AndTheTargetedCancelEvent()
    {
        WriteScript("svc.ts", """
            import { ksa } from "ksa";
            export async function main(ctx) {
              ksa.console.log("service kind=" + ctx.kind);
              ctx.signal.onCancel(() => ksa.console.log("cancel event"));
              while (!ctx.signal.cancelled) {
                await ksa.sleep(0.02);
                ksa.console.log("tick");
              }
              ksa.console.log("loop ended");
            }
            """);
        _catalog.Scan();
        _catalog.Start("svc.ts");
        Assert.That(_catalog.Get("svc.ts")!.State, Is.EqualTo(ScriptState.Service));

        StepUntil(() => Output("svc.ts").Count(l => l == "tick") >= 2, "two service ticks");
        Assert.That(Output("svc.ts").First(), Is.EqualTo("service kind=service"));

        _catalog.Stop("svc.ts");

        Assert.That(Output("svc.ts"), Does.Contain("cancel event"), "Stop delivers signal.cancel to the script's own subscription");
        Assert.That(_catalog.Get("svc.ts")!.State, Is.EqualTo(ScriptState.Idle));
        Assert.That(_runtime.LoadedScriptCount, Is.EqualTo(0));
        Slot();
        Assert.That(_errors, Is.Empty, "the cancelled sleep of the unloaded script is dropped silently");
    }

    [Test]
    [CancelAfter(30000)]
    public void RunawayHook_IsInterruptedByTheWatchdog_AndTheScriptIsDisabled()
    {
        WriteScript("spinner.ts", "import { ksa } from \"ksa\";\nksa.on('spin', () => { for (;;) {} });\nexport function noop() {}\n");
        _catalog.Scan();
        _catalog.Load("spinner.ts");
        _events.Publish(new ScriptEvent(1, "spin", null, new Dictionary<string, ScriptValue>()));

        Slot();

        Assert.That(_scheduler.IsDisabled("spinner.ts"), Is.True);
        Assert.That(_errors.Single().Error.Message, Is.EqualTo("interrupted"));
        Assert.That(_errors.Single().Script, Is.EqualTo("spinner.ts"));
        Assert.That(_watchdog.Tripped, Is.EqualTo(1));
        AssertRuntimeStillHealthy();
    }

    [Test]
    [CancelAfter(30000)]
    public void RunawayContinuation_IsInterruptedByTheWatchdog_AndTheScriptIsDisabled()
    {
        WriteScript("lateSpinner.ts", """
            import { ksa } from "ksa";
            export async function main() {
              await ksa.sleep(0.01);
              ksa.console.log("spinning");
              for (;;) {}
            }
            """);
        _catalog.Scan();
        _catalog.Run("lateSpinner.ts");

        StepUntil(() => _scheduler.IsDisabled("lateSpinner.ts"), "the watchdog to trip inside Advance");

        Assert.That(_errors.Select(e => e.Error.Message), Is.EqualTo(new[] { "interrupted" }));
        Assert.That(Output("lateSpinner.ts").First(), Is.EqualTo("spinning"));
        AssertRuntimeStillHealthy();
    }

    private void AssertRuntimeStillHealthy()
    {
        WriteScript("healthy.ts", "import { ksa } from \"ksa\";\nexport function main() { ksa.console.log('healthy'); }\n");
        _catalog.Scan();
        _catalog.Load("healthy.ts");
        _catalog.Run("healthy.ts");
        Assert.That(Output("healthy.ts"), Does.Contain("healthy"));
    }

    private string WriteScript(string relativePath, string code)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, code);
        return path;
    }

    // The host's output ring prefixes every line with "[script] ".
    private string[] Output(string script)
        => _host.OutputFor(script).Select(e => Regex.Replace(e.Message, @"^\[[^\]]*\] ", "")).ToArray();

    private double _ut;

    private void Slot()
    {
        _step++;
        _ut = (_step - 1) * 0.02;
        _scheduler.StepSlot(new StepInfo(_ut, 0.02, _step, (ulong)_step, true, 0.016));
    }

    /// <summary>Steps slots (advancing game time) until the condition holds; between slots it waits on the runtime's work signal, never a fixed delay.</summary>
    private void StepUntil(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Deadline)
                Assert.Fail($"timed out waiting for {what}");

            Slot();
            _runtime.WaitForWork(TimeSpan.FromMilliseconds(20));
        }
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
