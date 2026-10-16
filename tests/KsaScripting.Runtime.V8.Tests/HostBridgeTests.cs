using KsaScripting.Core.Hosting;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.V8.Tests;

[TestFixture]
public sealed class HostBridgeTests : V8TestBase
{
    [Test]
    public void Context_CtxMapPassesThroughMakeContext()
    {
        var module = LoadScript("ctxscript", """
            import { ksa } from "ksa";
            export function onTick(ctx: any) {
              ksa.console.log([ctx.kind, ctx.ut, ctx.dt, ctx.script.name, ctx.ksa === ksa, ctx["$ctx"], typeof ctx.log].join("|"));
            }
            """);
        var context = ScriptValue.Map(new Dictionary<string, ScriptValue>
        {
            ["$ctx"] = ScriptValue.True,
            ["kind"] = ScriptValue.From("tick"),
            ["script"] = ScriptValue.Map(new Dictionary<string, ScriptValue>
            {
                ["name"] = ScriptValue.From("ctxscript"),
                ["path"] = ScriptValue.From("/x/ctxscript.ts"),
            }),
            ["ut"] = ScriptValue.From(100.5),
            ["dt"] = ScriptValue.From(0.25),
        });

        Rt.Invoke(Rt.GetExport(module, "onTick"), [context]);

        // The real prelude drops the "$ctx" marker and adds ksa + log; the placeholder keeps the same contract for ksa.
        Assert.That(Host.LogMessages.Single(), Does.StartWith("tick|100.5|0.25|ctxscript|true||"));
    }

    [Test]
    public void Context_PlainMapsAreNotContexts()
    {
        var module = LoadScript("plainmap", "export function f(m: any) { return Object.keys(m).join(\",\") + \"|\" + typeof m.ksa; }");
        var map = ScriptValue.Map(new Dictionary<string, ScriptValue> { ["a"] = ScriptValue.From(1d), ["b"] = ScriptValue.From(2d) });

        Assert.That(Rt.Invoke(Rt.GetExport(module, "f"), [map]).AsString(), Is.EqualTo("a,b|undefined"));
    }

    [Test]
    public void Subscribe_JsFunctionBecomesCallable_InvokeRunsIt()
    {
        var module = LoadScript("subscriber", """
            import { ksa } from "ksa";
            export function main() {
              const sub = ksa.on("stage", (payload: any) => { ksa.console.log("got " + payload.n); }, { vessel: "v1", sync: true });
              ksa.console.log("id=" + sub.id);
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);

        var subscription = Host.Subscriptions.Single();
        Assert.That(subscription.Kind, Is.EqualTo("event"));
        Assert.That(subscription.Key, Is.EqualTo("stage"));
        Assert.That(subscription.Options, Is.EqualTo(new BindingOptions(Sync: true, Vessel: "v1", Clock: "ut", IntervalSeconds: 0)));
        Assert.That(subscription.Callable.RuntimeId, Is.EqualTo("v8"));
        Assert.That(Host.LogMessages.Single(), Is.EqualTo("id=" + subscription.Id.ToString("D")), "subscribe returns the Guid in D format");

        var payload = ScriptValue.Map(new Dictionary<string, ScriptValue> { ["n"] = ScriptValue.From(5d) });
        var result = Rt.Invoke(subscription.Callable, [payload]);

        Assert.That(result.IsNull, Is.True);
        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "id=" + subscription.Id.ToString("D"), "got 5" }));

        subscription.Callable.Dispose();
        Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(subscription.Callable, [payload]));
    }

    [Test]
    public void Subscribe_OptionDefaults_AndTickOptions()
    {
        var module = LoadScript("ticker", """
            import { ksa } from "ksa";
            export function main() {
              ksa.on("engine.activate", () => {});
              ksa.every(2.5, () => {}, { clock: "wall" });
              ksa.every("frame", () => {});
              const sub = ksa.on("stage", () => {});
              sub.unsubscribe();
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);

        var subs = Host.Subscriptions;
        Assert.That(subs[0].Options, Is.EqualTo(new BindingOptions()));
        Assert.That((subs[1].Kind, subs[1].Key, subs[1].Options), Is.EqualTo(("tick", "every", new BindingOptions(Clock: "wall", IntervalSeconds: 2.5))));
        Assert.That((subs[2].Kind, subs[2].Key), Is.EqualTo(("tick", "frame")));
        Assert.That(Host.Unsubscribed, Is.EqualTo(new[] { subs[3].Id }));
    }

    [Test]
    public void Subscribe_NonFunctionHandler_Throws()
    {
        var ex = Assert.Throws<ScriptRuntimeException>(() => Call("badsub", """
            export function main() { (globalThis as any).__host.subscribe("event", "stage", 42, {}); }
            """, "main"))!;

        Assert.That(ex.Message, Does.Contain("must be a function"));
        Assert.That(Host.Subscriptions, Is.Empty);
    }

    [Test]
    public void Values_HostToScript_CoversEveryKind()
    {
        var callable = ScriptCallable.Create("v8", 99, "ignored", _ => { });
        Host.StateSet("k", ScriptValue.Map(new Dictionary<string, ScriptValue>
        {
            ["name"] = ScriptValue.From("kitten"),
            ["list"] = ScriptValue.List([ScriptValue.From(1d), ScriptValue.From(2d), ScriptValue.From(3d)]),
            ["nested"] = ScriptValue.Map(new Dictionary<string, ScriptValue>
            {
                ["deep"] = ScriptValue.List([ScriptValue.From(10d), ScriptValue.From(20d)]),
            }),
            ["bytes"] = ScriptValue.From(new byte[] { 1, 2, 250, 255, 0 }),
            ["handle"] = ScriptValue.Handle("vessel:abc"),
            ["fn"] = ScriptValue.Function(callable),
            ["flag"] = ScriptValue.True,
            ["nothing"] = ScriptValue.Null,
            ["text"] = ScriptValue.From("héllo \"quoted\" ☃"),
            ["big"] = ScriptValue.From(1.5e300),
        }));

        Call("fromhost", """
            import { ksa } from "ksa";
            export function main() {
              const v: any = ksa.state.get("k");
              ksa.console.log([
                v.name, v.list.length, v.nested.deep[1], v.bytes instanceof Uint8Array,
                Array.from(v.bytes as Uint8Array).join(","), v.handle, v.fn === null, v.flag, v.nothing === null,
                v.text, v.big,
              ].join("|"));
              ksa.console.log(String(ksa.state.get("unset")));
            }
            """, "main");

        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "kitten|3|20|true|1,2,250,255,0|vessel:abc|true|true|true|héllo \"quoted\" ☃|1.5e+300", "null" }));
    }

    [Test]
    public void Values_ScriptToHost_CoversEveryKind()
    {
        Call("tohost", """
            import { ksa } from "ksa";
            export function main() {
              ksa.state.set("out", {
                a: 1, f32: 1.5, text: "s", t: true, n: null, u: undefined,
                list: [true, "s", null, [1, 2]],
                deep: { bytes: new Uint8Array([9, 8]), buf: new Uint8Array([7, 6, 5]).buffer },
                fn: function named() { return 1; },
                when: new Date(0),
                typed: new Float64Array([0.5, 1.5]),
                p: Promise.resolve(1),
              });
            }
            """, "main");

        var map = Host.State["out"].AsMap();
        Assert.That(map["a"].AsNumber(), Is.EqualTo(1));
        Assert.That(map["f32"].AsNumber(), Is.EqualTo(1.5));
        Assert.That(map["text"].AsString(), Is.EqualTo("s"));
        Assert.That(map["t"].AsBool(), Is.True);
        Assert.That(map["n"].IsNull, Is.True);
        Assert.That(map["u"].IsNull, Is.True);
        Assert.That(map["list"].AsList().Select(v => v.Kind), Is.EqualTo(new[] { ScriptValueKind.Bool, ScriptValueKind.String, ScriptValueKind.Null, ScriptValueKind.List }));
        Assert.That(map["list"].AsList()[3].AsList().Select(v => v.AsNumber()), Is.EqualTo(new[] { 1d, 2d }));
        Assert.That(map["deep"]["bytes"].AsBytes().ToArray(), Is.EqualTo(new byte[] { 9, 8 }));
        Assert.That(map["deep"]["buf"].AsBytes().ToArray(), Is.EqualTo(new byte[] { 7, 6, 5 }));
        Assert.That(map["fn"].Kind, Is.EqualTo(ScriptValueKind.Function));
        Assert.That(map["fn"].AsFunction().Name, Is.EqualTo("named"));
        Assert.That(Rt.Invoke(map["fn"].AsFunction(), []).AsNumber(), Is.EqualTo(1));
        Assert.That(map["when"].AsString(), Is.EqualTo("1970-01-01T00:00:00.000Z"));
        Assert.That(map["typed"].AsList().Select(v => v.AsNumber()), Is.EqualTo(new[] { 0.5, 1.5 }));
        Assert.That(map["p"].IsNull, Is.True, "a Promise cannot cross as data");
    }

    [Test]
    public void Values_DeeplyNestedAndCyclicObjects_AreCutOff()
    {
        Call("deep", """
            import { ksa } from "ksa";
            export function main() {
              const root: any = { name: "root" };
              root.self = root;
              ksa.state.set("cyc", root);
              let deep: any = { leaf: true };
              for (let i = 0; i < 40; i++) deep = { child: deep };
              ksa.state.set("deep", deep);
            }
            """, "main");

        var cyc = Host.State["cyc"];
        Assert.That(cyc["name"].AsString(), Is.EqualTo("root"));
        var depth = 0;
        for (var node = cyc; node.Kind == ScriptValueKind.Map; node = node["self"])
            depth++;
        Assert.That(depth, Is.EqualTo(32), "recursion stops at the depth limit with Null");

        var deepDepth = 0;
        for (var node = Host.State["deep"]; node.Kind == ScriptValueKind.Map; node = node["child"])
            deepDepth++;
        Assert.That(deepDepth, Is.EqualTo(32));
    }

    [Test]
    public void Read_Snapshot_List_ReturnHostValues()
    {
        Host.ReadHandler = (handle, path) => handle == "time" && path == "ut" ? ScriptValue.From(1234.5) : ScriptValue.Null;
        Host.SnapshotHandler = handle => handle == "system"
            ? ScriptValue.Map(new Dictionary<string, ScriptValue> { ["home"] = ScriptValue.From("Kerbin") })
            : ScriptValue.Null;

        var result = Call("reads", """
            import { ksa } from "ksa";
            export function main() { return [ksa.time.ut, ksa.system.home, ksa.vessels().length].join("|"); }
            """, "main");

        Assert.That(result.AsString(), Is.EqualTo("1234.5|Kerbin|0"));
    }

    [Test]
    public void Act_NormalisesArguments()
    {
        Call("acts", """
            export function main() {
              const h = (globalThis as any).__host;
              h.act("a.one");
              h.act("a.two", undefined, undefined, undefined, undefined, undefined, undefined);
              h.act("a.three", "v1", 2, 0.25, [1, 2.5, 3], "tok", "aux");
              h.act("a.four", null, 3.0, 7, new Float64Array([4, 5]), null, null);
            }
            """, "main");

        var acts = Host.Acts;
        Assert.That(acts[0], Is.EqualTo(new ActCall("a.one", null, -1, 0, null, null, null)));
        Assert.That(acts[1], Is.EqualTo(new ActCall("a.two", null, -1, 0, null, null, null)));
        Assert.That((acts[2].Action, acts[2].VesselId, acts[2].Ordinal, acts[2].Value, acts[2].Token, acts[2].Aux), Is.EqualTo(("a.three", "v1", 2, 0.25, "tok", "aux")));
        Assert.That(acts[2].Values, Is.EqualTo(new[] { 1d, 2.5, 3d }));
        Assert.That((acts[3].Ordinal, acts[3].Value), Is.EqualTo((3, 7d)));
        Assert.That(acts[3].Values, Is.EqualTo(new[] { 4d, 5d }));
    }

    [Test]
    public void Terminal_WriteInfoTitleAndPolicy()
    {
        var result = Call("term", """
            export function main() {
              const h = (globalThis as any).__host;
              h.termWrite(3, "hié");
              h.termWrite(3, new Uint8Array([1, 2, 3]));
              h.termWrite(3, new Uint8Array([9, 8, 7]).buffer);
              h.termSetTitle(3, "Title");
              h.termPolicy(3, true);
              const info = h.termInfo(3);
              try { h.termWrite(3, 42); return "no error"; } catch (e) { return info.cols + "x" + info.rows + " attached=" + info.attached + " " + (e as Error).message; }
            }
            """, "main");

        Assert.That(Host.TermWrites.Select(w => (w.TermId, Convert.ToHexString(w.Bytes))), Is.EqualTo(new[]
        {
            (3, "6869C3A9"), (3, "010203"), (3, "090807"),
        }));
        Assert.That(Host.Titles, Is.EqualTo(new[] { (3, "Title") }));
        Assert.That(Host.Policies, Is.EqualTo(new[] { (3, true) }));
        Assert.That(result.AsString(), Does.StartWith("80x24 attached=1 "));
        Assert.That(result.AsString(), Does.Contain("termWrite: data must be a string"));
    }

    [Test]
    public void ConsoleExec_AndState_RoundTrip()
    {
        Call("misc", """
            import { ksa } from "ksa";
            export function main() {
              ksa.console.exec("warp 4");
              ksa.state.set("counter", 41);
              ksa.state.counter = (ksa.state.counter as number) + 1;
              ksa.console.log(String(ksa.state.get("counter")));
            }
            """, "main");

        Assert.That(Host.ConsoleCommands, Is.EqualTo(new[] { "warp 4" }));
        Assert.That(Host.State["counter"].AsNumber(), Is.EqualTo(42));
        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "42" }));
    }
}
