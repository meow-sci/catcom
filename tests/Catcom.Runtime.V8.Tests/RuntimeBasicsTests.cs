using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.V8.Tests;

[TestFixture]
public sealed class RuntimeBasicsTests : V8TestBase
{
    [Test]
    public void Availability_IsTrueOnThisMachine()
    {
        Assert.That(Rt.IsAvailable, Is.True, Rt.UnavailableReason);
        Assert.That(Rt.UnavailableReason, Is.Null);
        Assert.That(Rt.Id, Is.EqualTo("v8"));
        Assert.That(Rt.Language, Is.EqualTo(ScriptLanguages.TypeScript));
        Assert.That(Rt.Stats.Version, Does.StartWith("ClearScript 7.5.1.1; TypeScript "));
        Assert.That(Rt.Stats.JitEnabled, Is.True);
        Assert.That(Rt.UsingSucrase, Is.False, "the full TypeScript compiler is expected when typescript.js is present");
    }

    [Test]
    public void Transpile_EnumAndInterface_Runs()
    {
        var result = Call("enumtest", """
            interface Shape { sides: number }
            enum Mode { Idle = 3, Burn = 7 }
            type Alias = Mode | "x";
            export function pick(s: Shape): number {
              const m: Mode = Mode.Burn;
              return m + s.sides;
            }
            export function main(): number { return pick({ sides: 1 }); }
            """, "main");

        Assert.That(result.Kind, Is.EqualTo(ScriptValueKind.Number));
        Assert.That(result.AsNumber(), Is.EqualTo(8));
    }

    [Test]
    public void Import_SiblingTsModule_Works()
    {
        WriteScript("lib/k.ts", """
            (globalThis as any).__loads = ((globalThis as any).__loads ?? 0) + 1;
            export const k: number = 7;
            """);
        WriteScript("lib/index.ts", "export const fromIndex = 100;");

        var result = Call("importer", """
            import { k } from "./lib/k.ts";
            import { k as k2 } from "./lib/k";
            import { fromIndex } from "./lib";
            export function main(): number { return k + k2 + fromIndex + (globalThis as any).__loads; }
            """, "main");

        // 7 + 7 + 100 + 1: both spellings resolve to ONE module instance (evaluated once).
        Assert.That(result.AsNumber(), Is.EqualTo(115));
    }

    [Test]
    public void Import_PlainJavaScriptModule_IsServedAsIs()
    {
        WriteScript("lib/plain.js", "export function twice(n) { return n * 2; }");
        var result = Call("jsimport", """
            import { twice } from "./lib/plain.js";
            export function main(): number { return twice(21); }
            """, "main");

        Assert.That(result.AsNumber(), Is.EqualTo(42));
    }

    [Test]
    public void Prelude_IsOneInstancePerEngine()
    {
        WriteScript("other.ts", """
            import { ksa } from "ksa";
            export { ksa as otherKsa };
            """);

        var result = Call("single", """
            import { ksa } from "ksa";
            import { otherKsa } from "./other.ts";
            export function main(): boolean { return ksa === otherKsa && ksa === (globalThis as any).ksa; }
            """, "main");

        Assert.That(result.AsBool(), Is.True);
    }

    [Test]
    public void Prelude_VesselThrottle_ReachesFacade()
    {
        Call("throttle", """
            import { ksa } from "ksa";
            export function main() { ksa.vessel("x").setThrottle(0.5); }
            """, "main");

        Assert.That(Host.Acts, Has.Count.EqualTo(1));
        Assert.That(Host.Acts[0], Is.EqualTo(new ActCall("vessel.throttle", "x", -1, 0.5, null, null, null)).Using<ActCall>(SameAct));
    }

    [Test]
    public void Prelude_ConsoleLogReachesHostLog_AndGlobalConsoleIsRouted()
    {
        Call("logging", """
            import { ksa } from "ksa";
            export function main() {
              ksa.console.log("one", 2, { three: 3 });
              console.warn("global console");
            }
            """, "main");

        Assert.That(Host.Logs.Select(l => (l.Level, l.Message, l.Script)), Is.EqualTo(new[]
        {
            (1, "one 2 {\"three\":3}", "logging"),
            (2, "global console", "logging"),
        }));
    }

    [Test]
    public void Exports_AreModuleFunctionsOnly()
    {
        var module = LoadScript("exports", """
            export const x = 1;
            export function a() {}
            export async function b() {}
            export const arrow = () => 1;
            export class C {}
            """);

        // Sorted ordinally; classes are functions too, constants are not.
        Assert.That(Rt.Exports(module), Is.EqualTo(new[] { "C", "a", "arrow", "b" }));
    }

    [Test]
    public void GetExport_ReturnsSameCallable_AndThrowsForUnknownNames()
    {
        var module = LoadScript("getexport", "export function a() { return 1; }\nexport const x = 1;");

        var first = Rt.GetExport(module, "a");
        Assert.That(Rt.GetExport(module, "a"), Is.SameAs(first));
        Assert.That(first.RuntimeId, Is.EqualTo("v8"));
        Assert.That(first.Name, Is.EqualTo("a"));
        Assert.Throws<KeyNotFoundException>(() => Rt.GetExport(module, "x"));
        Assert.Throws<KeyNotFoundException>(() => Rt.GetExport(module, "missing"));

        first.Dispose();
        var second = Rt.GetExport(module, "a");
        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(second.IsDisposed, Is.False);
    }

    [Test]
    public void Invoke_PassesArgumentsAndConvertsResults()
    {
        var module = LoadScript("convert", """
            export function sum(a: number, b: number): number { return a + b; }
            export function describe(v: any): any {
              return { kind: typeof v, list: [1, "two", null, true], nested: { ok: true } };
            }
            export function nothing(): void {}
            export function bytes(): Uint8Array { return new Uint8Array([1, 2, 3]); }
            """);

        Assert.That(Rt.Invoke(Rt.GetExport(module, "sum"), [ScriptValue.From(2d), ScriptValue.From(40d)]).AsNumber(), Is.EqualTo(42));
        Assert.That(Rt.Invoke(Rt.GetExport(module, "nothing"), []).IsNull, Is.True);

        var map = Rt.Invoke(Rt.GetExport(module, "describe"), [ScriptValue.From("hello")]);
        Assert.That(map["kind"].AsString(), Is.EqualTo("string"));
        Assert.That(map["list"].AsList().Select(v => v.Kind), Is.EqualTo(new[]
        {
            ScriptValueKind.Number, ScriptValueKind.String, ScriptValueKind.Null, ScriptValueKind.Bool,
        }));
        Assert.That(map["nested"]["ok"].AsBool(), Is.True);

        Assert.That(Rt.Invoke(Rt.GetExport(module, "bytes"), []).AsBytes().ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(Rt.Stats.CallsTotal, Is.EqualTo(4));
        Assert.That(Rt.Stats.LastCallMs, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Invoke_RejectsForeignAndDisposedCallables()
    {
        var module = LoadScript("validate", "export function f() { return 1; }");
        var callable = Rt.GetExport(module, "f");

        var foreign = ScriptCallable.Create("lua", 1, "x", _ => { });
        Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(foreign, []));

        var unknown = ScriptCallable.Create("v8", 987654, "x", _ => { });
        Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(unknown, []));

        callable.Dispose();
        Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(callable, []));
    }

    [Test]
    public void Unload_ReleasesTheEngineAndItsCallables()
    {
        var module = LoadScript("unloaded", "export function f() { return 1; }");
        var callable = Rt.GetExport(module, "f");
        Assert.That(Rt.LoadedScriptCount, Is.EqualTo(1));

        Rt.Unload(module);
        Rt.Unload(module); // unknown now: no-op
        Rt.Unload(new ScriptModule("v8", "never", 424242));

        Assert.That(Rt.LoadedScriptCount, Is.EqualTo(0));
        Assert.That(callable.IsDisposed, Is.True);
        Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(callable, []));
        Assert.That(Rt.Exports(module), Is.Empty);
        Assert.Throws<KeyNotFoundException>(() => Rt.GetExport(module, "f"));
    }

    [Test]
    public void Reentrancy_NestedInvokeOfAnotherScript_Works()
    {
        var inner = LoadScript("inner", """
            import { ksa } from "ksa";
            export function hello(): number { ksa.console.log("in inner"); return 5; }
            """);
        var innerFn = Rt.GetExport(inner, "hello");

        ScriptValue nestedResult = ScriptValue.Null;
        Host.ActHandler = _ =>
        {
            nestedResult = Rt.Invoke(innerFn, []);
            return new Catcom.Core.Hosting.ActResult(Catcom.Core.Commands.CommandResult.Ok, null);
        };

        Call("outer", """
            import { ksa } from "ksa";
            export function main() { ksa.vessel("v").ignite(); ksa.console.log("outer done"); }
            """, "main");

        Assert.That(nestedResult.AsNumber(), Is.EqualTo(5));
        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "in inner", "outer done" }));
        Assert.That(Host.Logs.Select(l => l.Script), Is.EqualTo(new[] { "inner", "outer" }));
        Assert.DoesNotThrow(() => Rt.Interrupt(), "no engine is current any more");
        Assert.That(Rt.Invoke(innerFn, []).AsNumber(), Is.EqualTo(5));
    }

    [Test]
    public void Sandbox_ScriptsHaveNoAccessToTheHostRuntime()
    {
        var result = Call("sandbox", """
            export function main(): string {
              const g: any = globalThis;
              let reflection = "none";
              try { g.__host.GetType(); reflection = "reachable"; } catch (e) { reflection = "blocked"; }
              return [typeof g.host, typeof g.xHost, typeof g.process, typeof g.require, typeof g.System, typeof g.__hostNative,
                Object.isFrozen(g.__host), reflection].join(",");
            }
            """, "main");

        Assert.That(result.AsString(), Is.EqualTo("undefined,undefined,undefined,undefined,undefined,undefined,true,blocked"));
    }

    [Test]
    public void TopLevelAwaitOfHostWait_IsRejectedWithGuidance()
    {
        var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("tla", """
            import { ksa } from "ksa";
            await ksa.sleep(1);
            export function main() {}
            """))!;

        Assert.That(ex.Message, Does.Contain("top-level await of host waits is not supported; use main(ctx)"));
        Assert.That(Rt.LoadedScriptCount, Is.EqualTo(0));
    }

    [Test]
    public void TopLevelAwaitOfAnOrdinaryPromise_IsSupported()
    {
        var module = LoadScript("tlaplain", """
            const value: number = await Promise.resolve(41);
            export function main(): number { return value + 1; }
            """);

        Assert.That(Rt.Invoke(Rt.GetExport(module, "main"), []).AsNumber(), Is.EqualTo(42));
    }

    [Test]
    public void JavaScriptEntryModule_IsLoadedAsIs()
    {
        var path = WriteScript("plainentry.js", "export function main() { return typeof ksa; }");

        var module = Rt.Load(new ScriptSource("plainentry", path, File.ReadAllText(path), ScriptLanguages.TypeScript));

        Assert.That(Rt.Invoke(Rt.GetExport(module, "main"), []).AsString(), Is.EqualTo("object"), "the prelude installed globalThis.ksa");
    }

    [Test]
    public void ModuleTopLevelThrow_IsAScriptLoadExceptionWithMappedLine()
    {
        var path = WriteScript("topthrow.ts", "const n: number = 1;\n\nthrow new Error(\"top boom\");\nexport function main() {}\n");

        var ex = Assert.Throws<ScriptLoadException>(() => Rt.Load(new ScriptSource("topthrow", path, File.ReadAllText(path), ScriptLanguages.TypeScript)))!;

        Assert.That(ex.Message, Does.Contain("top boom"));
        Assert.That(ex.File, Is.EqualTo(path));
        Assert.That(ex.Line, Is.EqualTo(3));
        Assert.That(ex.Snippet, Does.Contain("throw new Error"));
    }

    [Test]
    public void EntryModuleOutsideScriptsFolder_IsServedFromMemory()
    {
        var outside = Path.Combine(Root, "elsewhere", "entry.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        // Nothing is written to disk at that path: the entry's code comes from ScriptSource.Code.
        var module = Rt.Load(new ScriptSource("entry", outside, "export function main(): number { return 9; }", ScriptLanguages.TypeScript));

        Assert.That(Rt.Invoke(Rt.GetExport(module, "main"), []).AsNumber(), Is.EqualTo(9));
    }

    [Test]
    public void LoadingTheSameNameTwice_GivesIndependentEngines()
    {
        var first = LoadScript("twice", "let n = 0; export function bump(): number { return ++n; }");
        var second = LoadScript("twice", "let n = 100; export function bump(): number { return ++n; }");

        Assert.That(Rt.Invoke(Rt.GetExport(first, "bump"), []).AsNumber(), Is.EqualTo(1));
        Assert.That(Rt.Invoke(Rt.GetExport(second, "bump"), []).AsNumber(), Is.EqualTo(101));
        Assert.That(Rt.Invoke(Rt.GetExport(first, "bump"), []).AsNumber(), Is.EqualTo(2));
    }

    private static bool SameAct(ActCall expected, ActCall actual)
        => expected.Action == actual.Action && expected.VesselId == actual.VesselId && expected.Ordinal == actual.Ordinal
            && expected.Value == actual.Value && expected.Token == actual.Token && expected.Aux == actual.Aux
            && (expected.Values is null ? actual.Values is null : actual.Values is not null && expected.Values.SequenceEqual(actual.Values));
}
