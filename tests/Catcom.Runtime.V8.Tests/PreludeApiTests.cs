using Catcom.Core.Commands;
using Catcom.Core.Hosting;
using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.V8.Tests;

/// <summary>The real <c>ksa.js</c> prelude (from Core) over the V8 facade: what each script-facing call asks of the host.</summary>
[TestFixture]
public sealed class PreludeApiTests : V8TestBase
{
    private ScriptSource Write(string name, string code)
    {
        var path = WriteScript(name + ".ts", code);
        return new ScriptSource(name, path, code, ScriptLanguages.TypeScript);
    }

    private ScriptCallable Main(ScriptSource source, string export = "main") => Rt.GetExport(Rt.Load(source), export);

    private static ScriptValue Payload(params (string Key, ScriptValue Value)[] entries) => ContextMaps.Map(entries);

    // ---------------------------------------------------------------- ksa.on

    [Test]
    public void On_SyncAndVesselOptions_BecomeBindingOptions_AndTheHandlerGetsTheEventContext()
    {
        var source = Write("onhook", """
            import { ksa } from "ksa";
            export function main() {
              ksa.on("engine.activate", (ctx: any) => {
                ksa.console.log([ctx.kind, ctx.type, ctx.vesselId, ctx.vessel.id, ctx.sync, ctx.ut, ctx.payload.engine, ctx.event.type].join("|"));
              }, { sync: true, vessel: "v1" });
              ksa.on("stage", () => {}, { vessel: ksa.vessel("v2") });
              ksa.on("decouple", () => {});
            }
            """);
        Rt.Invoke(Main(source), []);

        var subs = Host.Subscriptions;
        Assert.That(subs, Has.Count.EqualTo(3));
        Assert.That((subs[0].Kind, subs[0].Key, subs[0].Options), Is.EqualTo(("event", "engine.activate", new BindingOptions(Sync: true, Vessel: "v1", Clock: "ut", IntervalSeconds: 0))));
        Assert.That(subs[1].Options, Is.EqualTo(new BindingOptions(Vessel: "v2")), "a VesselRef is reduced to its id");
        Assert.That(subs[2].Options, Is.EqualTo(new BindingOptions()));

        var context = ContextMaps.Create(
            "event",
            source,
            ("sync", ScriptValue.True),
            ("vesselId", ScriptValue.From("v1")),
            ("event", ContextMaps.Event("engine.activate", "v1", Payload(("engine", ScriptValue.From(2d))))));
        Rt.Invoke(subs[0].Callable, [context]);

        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "event|engine.activate|v1|v1|true|1|2|engine.activate" }));
    }

    // ---------------------------------------------------------------- ksa.every

    [Test]
    public void Every_FrameStepAndSeconds_MapToTickKeysIntervalsAndClocks()
    {
        var source = Write("ticks", """
            import { ksa } from "ksa";
            export function main() {
              ksa.every("frame", () => {});
              ksa.every("step", () => {}, { clock: "render" });
              ksa.every(2.5, () => {});
              ksa.every(10, (ctx: any) => { ksa.console.log(ctx.kind + " " + ctx.ut + " " + ctx.dt + " " + ctx.stepIndex + " " + ctx.frame); }, { clock: "wall" });
              try { ksa.every(0, () => {}); } catch (e) { ksa.console.log("rejected: " + (e as Error).message); }
            }
            """);
        Rt.Invoke(Main(source), []);

        var subs = Host.Subscriptions;
        Assert.That(subs.Select(s => (s.Kind, s.Key, s.Options)), Is.EqualTo(new[]
        {
            ("tick", "frame", new BindingOptions()),
            ("tick", "step", new BindingOptions(Clock: "render")),
            ("tick", "every", new BindingOptions(Clock: "ut", IntervalSeconds: 2.5)),
            ("tick", "every", new BindingOptions(Clock: "wall", IntervalSeconds: 10)),
        }));
        Assert.That(Host.LogMessages.Single(), Does.StartWith("rejected: ksa.every: kind must be"));

        var tick = ContextMaps.Create(
            "tick",
            source,
            ("ut", ScriptValue.From(100d)),
            ("dt", ScriptValue.From(0.5)),
            ("stepIndex", ScriptValue.From(3d)),
            ("frame", ScriptValue.From(9d)));
        Rt.Invoke(subs[3].Callable, [tick]);

        Assert.That(Host.LogMessages[1], Is.EqualTo("tick 100 0.5 3 9"));
    }

    // ---------------------------------------------------------------- ksa.service / ksa.command

    [Test]
    public void Service_RegistersAServiceBinding_AndRunsWithAServiceContext()
    {
        var source = Write("svc", """
            import { ksa } from "ksa";
            export function main() {
              ksa.service(async (ctx: any) => {
                ksa.console.log("service " + ctx.kind + " " + ctx.script.name + " cancelled=" + ctx.signal.cancelled + " term=" + typeof ctx.term);
              });
            }
            """);
        Rt.Invoke(Main(source), []);

        var subscription = Host.Subscriptions.Single();
        Assert.That((subscription.Kind, subscription.Key, subscription.Options), Is.EqualTo(("service", "", new BindingOptions())));

        var result = Rt.Invoke(subscription.Callable, [ContextMaps.Create("service", source, ("signal", ScriptValue.From("svc")))]);

        Assert.That(result.IsNull, Is.True, "an async service returns immediately");
        PumpUntil(() => Host.LogMessages.Count > 0, "the service body");
        Assert.That(Host.LogMessages.Single(), Is.EqualTo("service service svc cancelled=false term=undefined"));
    }

    [Test]
    public void Command_RegistersByName_AndTheHandlerReceivesTheArgumentText()
    {
        var source = Write("cmd", """
            import { ksa } from "ksa";
            export function main() {
              ksa.command("hello", (args: string) => { ksa.console.log("hello " + typeof args + " [" + args + "]"); }, "Says hello");
            }
            """);
        Rt.Invoke(Main(source), []);

        var subscription = Host.Subscriptions.Single();
        Assert.That((subscription.Kind, subscription.Key), Is.EqualTo(("command", "hello")));

        Rt.Invoke(subscription.Callable, [ScriptValue.From("world  and more")]);

        Assert.That(Host.LogMessages.Single(), Is.EqualTo("hello string [world  and more]"), "commands get one String argument, not a context");
    }

    // ---------------------------------------------------------------- Solver actions

    [Test]
    public void SolverAction_PromiseResolvesWithTheResultMap_AndFrameActionsReturnItDirectly()
    {
        Host.ActHandler = call => call.Action == "vessel.burn"
            ? new ActResult(null, Host.Wait("act", ScriptValue.Null))
            : new ActResult(CommandResult.Ok, null);

        var source = Write("solver", """
            import { ksa } from "ksa";
            export async function main() {
              const v = ksa.vessel("v1")!;
              const pending = v.burn(1000, { x: 1, y: 2, z: 3 });
              ksa.console.log("pending is a promise: " + (pending instanceof Promise));
              const r: any = await pending;
              ksa.console.log(`ok=${r.ok} outcome=${r.outcome} message=${r.message}`);
              const f: any = v.setThrottle(0.25);
              ksa.console.log(`frame ok=${f.ok} outcome=${f.outcome}`);
            }
            """);
        Rt.Invoke(Main(source), []);

        var burn = Host.Acts.Single();
        Assert.That((burn.Action, burn.VesselId, burn.Ordinal, burn.Value), Is.EqualTo(("vessel.burn", "v1", -1, 0d)));
        Assert.That(burn.Values, Is.EqualTo(new[] { 1000d, 1d, 2d, 3d }));
        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "pending is a promise: true" }));

        Host.Waits.Single(w => w.Kind == "act").Wait.Complete(CommandResult.Denied("not enough fuel").ToScriptValue());
        PumpUntil(() => Host.LogMessages.Count == 3, "the solver result");

        Assert.That(Host.LogMessages[1], Is.EqualTo("ok=false outcome=Denied message=not enough fuel"));
        Assert.That(Host.LogMessages[2], Is.EqualTo("frame ok=true outcome=Ok"));
        Assert.That(Host.Acts.Select(a => a.Action), Is.EqualTo(new[] { "vessel.burn", "vessel.throttle" }));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    // ---------------------------------------------------------------- ksa.until / ksa.waitEvent / the other waits

    [Test]
    public void Until_PassesThePredicateAndOptions_AndResolvesTrueOrFalse()
    {
        var source = Write("untilopts", """
            import { ksa } from "ksa";
            let ready = false;
            export async function main() {
              const first = await ksa.until(() => ready, { timeoutSeconds: 4, clock: "wall" });
              ksa.console.log("first=" + first);
              const second = await ksa.until(() => true);
              ksa.console.log("second=" + second);
            }
            export function flip() { ready = true; }
            """);
        var module = Rt.Load(source);
        Rt.Invoke(Rt.GetExport(module, "main"), []);

        var first = Host.Waits[0];
        Assert.That(first.Kind, Is.EqualTo("until"));
        Assert.That(first.Arg.AsMap().Keys, Is.EquivalentTo(new[] { "fn", "timeoutSeconds", "clock" }));
        Assert.That(first.Arg["timeoutSeconds"].AsNumber(), Is.EqualTo(4));
        Assert.That(first.Arg["clock"].AsString(), Is.EqualTo("wall"));
        var predicate = first.Arg["fn"].AsFunction();
        Assert.That(Rt.Invoke(predicate, []).AsBool(), Is.False);
        Rt.Invoke(Rt.GetExport(module, "flip"), []);
        Assert.That(Rt.Invoke(predicate, []).AsBool(), Is.True, "the predicate sees the script's live state");

        first.Wait.Complete(ScriptValue.False);
        PumpUntil(() => Host.Waits.Count == 2, "the second until");

        Assert.That(Host.Waits[1].Arg.AsMap().Keys, Is.EquivalentTo(new[] { "fn" }), "absent options are left out of the argument map");
        Host.Waits[1].Wait.Complete(ScriptValue.True);
        PumpUntil(() => Host.LogMessages.Count == 2, "both results");
        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "first=false", "second=true" }));
    }

    [Test]
    public void WaitEvent_PassesNameVesselAndTimeout_AndResolvesWithTheEventOrNull()
    {
        var source = Write("waitevent", """
            import { ksa } from "ksa";
            export async function main() {
              const ev: any = await ksa.waitEvent("engine.activate", { vessel: ksa.vessel("v1"), timeoutSeconds: 3 });
              ksa.console.log(`event ${ev.type} ${ev.vesselId} ${ev.ut} ${ev.payload.engine}`);
              const none = await ksa.waitEvent("stage");
              ksa.console.log("none=" + none);
            }
            """);
        Rt.Invoke(Main(source), []);

        var first = Host.Waits[0];
        Assert.That(first.Kind, Is.EqualTo("waitEvent"));
        Assert.That(first.Arg.AsMap().Keys, Is.EquivalentTo(new[] { "name", "vessel", "timeoutSeconds" }));
        Assert.That(first.Arg["name"].AsString(), Is.EqualTo("engine.activate"));
        Assert.That(first.Arg["vessel"].AsString(), Is.EqualTo("v1"), "a VesselRef is reduced to its id");
        Assert.That(first.Arg["timeoutSeconds"].AsNumber(), Is.EqualTo(3));

        first.Wait.Complete(ContextMaps.Event("engine.activate", "v1", Payload(("engine", ScriptValue.From(2d)))));
        PumpUntil(() => Host.Waits.Count == 2, "the second waitEvent");

        Assert.That(Host.Waits[1].Arg.AsMap().Keys, Is.EquivalentTo(new[] { "name" }));
        Host.Waits[1].Wait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.LogMessages.Count == 2, "both results");
        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "event engine.activate v1 1 2", "none=null" }));
    }

    [Test]
    public void SleepNextStepNextFrame_AskTheHostForTheirWaitKinds()
    {
        var source = Write("waits", """
            import { ksa } from "ksa";
            export async function main() {
              await ksa.sleep(2);
              await ksa.sleep(0.5, "wall");
              await ksa.nextStep();
              await ksa.nextFrame();
              ksa.console.log("all done");
            }
            """);
        Rt.Invoke(Main(source), []);

        for (var i = 1; i <= 4; i++)
        {
            PumpUntil(() => Host.Waits.Count == i, $"wait {i}");
            Host.Waits[i - 1].Wait.Complete(ScriptValue.Null);
        }

        PumpUntil(() => Host.LogMessages.Count == 1, "the end of main");
        Assert.That(Host.Waits.Select(w => w.Kind), Is.EqualTo(new[] { "sleep", "sleep", "nextStep", "nextFrame" }));
        Assert.That((Host.Waits[0].Arg["seconds"].AsNumber(), Host.Waits[0].Arg["clock"].AsString()), Is.EqualTo((2d, "ut")));
        Assert.That((Host.Waits[1].Arg["seconds"].AsNumber(), Host.Waits[1].Arg["clock"].AsString()), Is.EqualTo((0.5, "wall")));
        Assert.That(Host.Waits[2].Arg.IsNull, Is.True);
        Assert.That(Host.Waits[3].Arg.IsNull, Is.True);
    }

    // ---------------------------------------------------------------- ksa.state / ksa.console

    [Test]
    public void State_GetSetAndTheProxyForm_ReachTheHostStore()
    {
        var result = Call("state", """
            import { ksa } from "ksa";
            export function main() {
              ksa.state.set("a", 1);
              ksa.state.set("obj", { list: [1, 2, 3], name: "x" });
              ksa.state.counter = 41;
              ksa.state.counter = (ksa.state.counter as number) + 1;
              const seen = [ksa.state.get("a"), ksa.state.counter, "counter" in ksa.state, "missing" in ksa.state, ksa.state.get("missing")];
              delete ksa.state.counter;
              ksa.state.set("nothing", undefined);
              return seen.concat(["counter" in ksa.state, JSON.stringify(ksa.state.get("obj"))]).join("|");
            }
            """, "main");

        Assert.That(result.AsString(), Is.EqualTo("1|42|true|false||false|{\"list\":[1,2,3],\"name\":\"x\"}"));
        Assert.That(Host.State["a"].AsNumber(), Is.EqualTo(1));
        Assert.That(Host.State["counter"].IsNull, Is.True, "delete clears the key");
        Assert.That(Host.State["nothing"].IsNull, Is.True, "undefined is stored as null");
        Assert.That(Host.State["obj"]["list"].AsList().Select(v => v.AsNumber()), Is.EqualTo(new[] { 1d, 2d, 3d }));
    }

    [Test]
    public void ConsoleExec_ReachesTheGameConsole_AndLogLevelsMap()
    {
        Call("consoleapi", """
            import { ksa } from "ksa";
            export function main() {
              ksa.console.exec("timewarp 4");
              ksa.console.debug("d");
              ksa.console.log("l");
              ksa.console.info("i");
              ksa.console.warn("w");
              ksa.console.error("e", new Error("boom").message);
            }
            """, "main");

        Assert.That(Host.ConsoleCommands, Is.EqualTo(new[] { "timewarp 4" }));
        Assert.That(Host.Logs.Select(l => (l.Level, l.Message)), Is.EqualTo(new[] { (0, "d"), (1, "l"), (1, "i"), (2, "w"), (3, "e boom") }));
    }

    // ---------------------------------------------------------------- ctx.signal

    [Test]
    public void Signal_CancelledReadsTheHostHandle_AndOnCancelSubscribesToTheTargetedEvent()
    {
        var cancelled = false;
        var reads = new List<(string Handle, string Path)>();
        Host.ReadHandler = (handle, path) =>
        {
            reads.Add((handle, path));
            return handle == "signal:watcher" && path == "cancelled" ? ScriptValue.From(cancelled) : ScriptValue.Null;
        };
        var source = Write("watcher", """
            import { ksa } from "ksa";
            export function main(ctx: any) {
              ctx.signal.onCancel(() => { ksa.console.log("cancel handler ran"); });
            }
            export function check(ctx: any) { return ctx.signal.cancelled; }
            """);
        var module = Rt.Load(source);
        var context = ContextMaps.Create("service", source, ("signal", ScriptValue.From("watcher")));
        var check = Rt.GetExport(module, "check");

        Rt.Invoke(Rt.GetExport(module, "main"), [context]);
        var before = Rt.Invoke(check, [context]);
        cancelled = true;
        var after = Rt.Invoke(check, [context]);

        Assert.That(before.AsBool(), Is.False);
        Assert.That(after.AsBool(), Is.True);
        Assert.That(reads, Is.EqualTo(new[] { ("signal:watcher", "cancelled"), ("signal:watcher", "cancelled") }));

        var subscription = Host.Subscriptions.Single();
        Assert.That((subscription.Kind, subscription.Key, subscription.Options), Is.EqualTo(("event", "signal.cancel", new BindingOptions())));

        var eventContext = ContextMaps.Create("event", source, ("event", ContextMaps.Event("signal.cancel", null, Payload())));
        Rt.Invoke(subscription.Callable, [eventContext]);

        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "cancel handler ran" }));
    }

    // ---------------------------------------------------------------- vessels

    [Test]
    public void Vessels_AreFreshHostReads_AndAMissingVesselIsUndefined()
    {
        var reads = new List<(string Handle, string Path)>();
        Host.ReadHandler = (handle, path) =>
        {
            reads.Add((handle, path));
            return (handle, path) switch
            {
                ("time", "activeVesselId") => ScriptValue.From("v1"),
                ("vessel:v1", "id") => ScriptValue.From("v1"),
                ("vessel:v1", "throttleCmd") => ScriptValue.From(reads.Count(r => r.Path == "throttleCmd") * 0.25),
                ("vessel:v1", "name") => ScriptValue.From("Odyssey"),
                _ => ScriptValue.Null,
            };
        };
        Host.ListHandler = kind => kind == "vessels" ? ScriptValue.List([ScriptValue.From("v1"), ScriptValue.From("v2")]) : ScriptValue.List([]);

        var result = Call("vesselreads", """
            import { ksa } from "ksa";
            export function main() {
              const v = ksa.vessel("v1")!;
              const out: unknown[] = [v.id, v.name, v.throttleCmd, v.throttleCmd, ksa.vessel("ghost") === undefined, ksa.active?.id, ksa.vessels().map((x) => x.id).join(",")];
              return out.join("|");
            }
            """, "main");

        Assert.That(result.AsString(), Is.EqualTo("v1|Odyssey|0.25|0.5|true|v1|v1,v2"), "the second throttle read sees the changed host value");
        Assert.That(reads.Take(2), Is.EqualTo(new[] { ("vessel:v1", "id"), ("vessel:v1", "name") }), "ksa.vessel(id) reads \"id\" first to check existence");
        Assert.That(reads, Does.Contain(("vessel:ghost", "id")));
        Assert.That(reads.Count(r => r == ("vessel:v1", "throttleCmd")), Is.EqualTo(2), "every property access is a fresh read");
    }

    [Test]
    public void VesselSetters_UseTheSetPrefixedNames_AndTheDocumentedActionKeys()
    {
        Host.ActHandler = call => call.Action.StartsWith("vessel.attitude", StringComparison.Ordinal)
            ? new ActResult(null, Host.Wait("act", ScriptValue.Null))
            : new ActResult(CommandResult.Ok, null);

        Call("setters", """
            import { ksa } from "ksa";
            export function main() {
              const v = ksa.vessel("v1")!;
              v.setThrottle(0.4);
              v.setEngine(true);
              v.setLights(false);
              v.setRcs(true);
              v.ignite();
              v.shutdown();
              v.stage();
              v.translate(1, 0, -1);
              v.setAttitudeMode("Prograde");
              v.setAttitudeFrame("Orbit");
              v.setAttitudeTarget({ x: 0, y: 0, z: 0, w: 1 });
              v.setRcsMode("Enabled");
              ksa.time.setWarp(4);
            }
            """, "main");

        Assert.That(Host.Acts.Select(a => a.Action), Is.EqualTo(new[]
        {
            "vessel.throttle", "vessel.engine", "vessel.lights", "vessel.rcs", "vessel.ignite", "vessel.shutdown", "vessel.stage",
            "vessel.translate", "vessel.attitude_mode", "vessel.attitude_frame", "vessel.attitude_target", "vessel.rcs_mode", "debug.warp",
        }));
        Assert.That(Host.Acts[0].Value, Is.EqualTo(0.4));
        Assert.That((Host.Acts[1].Value, Host.Acts[2].Value, Host.Acts[3].Value), Is.EqualTo((1d, 0d, 1d)), "booleans cross as 1 / 0");
        Assert.That(Host.Acts[7].Values, Is.EqualTo(new[] { 1d, 0d, -1d }));
        Assert.That((Host.Acts[8].Token, Host.Acts[9].Token, Host.Acts[11].Token), Is.EqualTo(("Prograde", "Orbit", "Enabled")));
        Assert.That(Host.Acts[10].Values, Is.EqualTo(new[] { 0d, 0d, 0d, 1d }));
        Assert.That((Host.Acts[12].VesselId, Host.Acts[12].Value), Is.EqualTo(((string?)null, 4d)));
    }
}
