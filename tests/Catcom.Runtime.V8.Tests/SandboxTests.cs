using Catcom.Core.Runtime;
using Catcom.Core.Values;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace Catcom.Runtime.V8.Tests;

/// <summary>What a script can and cannot reach: the host bridge, host objects and .NET exceptions stay out of script hands.</summary>
[TestFixture]
public sealed class SandboxTests : V8TestBase
{
    private static readonly string[] FacadeMembers =
    [
        "act", "consoleExec", "list", "log", "read", "snapshot", "stateGet", "stateSet", "subscribe", "termInfo", "termPolicy",
        "termSetTitle", "termWrite", "unsubscribe", "wait",
    ];

    [Test]
    public void GlobalObject_HoldsOnlyTheStandardNamesPlusKsaAndTheFrozenHostFacade()
    {
        // A bare ClearScript engine of the same flavour is the baseline: ECMAScript builtins plus ClearScript's EngineInternal.
        using var runtime = new V8Runtime();
        using var bare = runtime.CreateScriptEngine("baseline", V8ScriptEngineFlags.EnableTaskPromiseConversion | V8ScriptEngineFlags.EnableStringifyEnhancements);
        var baseline = ((string)bare.Evaluate("Object.getOwnPropertyNames(globalThis).join(',')")).Split(',').ToHashSet(StringComparer.Ordinal);

        var visible = Call("globalnames", """
            export function main(): string { return Object.getOwnPropertyNames(globalThis).join(","); }
            """, "main").AsString().Split(',').ToHashSet(StringComparer.Ordinal);

        Assert.That(visible.Except(baseline), Is.EquivalentTo(new[] { "__host", "ksa" }), "scripts see exactly two extra globals");
        Assert.That(baseline.Except(visible), Is.Empty, "nothing was removed from the standard set");
        Assert.That(visible, Does.Not.Contain("__hostNative"));
        Assert.That(visible, Does.Not.Contain("host").And.Not.Contain("xHost").And.Not.Contain("clr").And.Not.Contain("process").And.Not.Contain("require"));
    }

    [Test]
    public void NoGlobalOrFacadeMember_IsAHostObject_AndTheFacadeIsExactlyTheDocumentedSurface()
    {
        var result = Call("hostobjects", """
            export function main(): string {
              const g: any = globalThis;
              const isHost = (v: unknown) => g.EngineInternal.isHostObject(v);
              const found: string[] = [];
              const visit = (label: string, value: any, depth: number) => {
                if (value === null || (typeof value !== "object" && typeof value !== "function")) return;
                if (isHost(value)) { found.push(label); return; }
                if (depth === 0) return;
                for (const key of Reflect.ownKeys(value)) {
                  let child: unknown;
                  try { child = value[key as any]; } catch { continue; }
                  visit(label + "." + String(key), child, depth - 1);
                }
              };
              for (const name of Object.getOwnPropertyNames(g)) {
                if (name === "globalThis") continue;
                let value: unknown;
                try { value = g[name]; } catch { continue; }
                visit(name, value, 2);
              }
              const facade = Object.getOwnPropertyNames(g.__host).sort().join(",");
              const descriptor = Object.getOwnPropertyDescriptor(g, "__host")!;
              return JSON.stringify({ found, facade, frozen: Object.isFrozen(g.__host), writable: descriptor.writable, enumerable: descriptor.enumerable, native: typeof g.__hostNative, nativeDescriptor: String(Object.getOwnPropertyDescriptor(g, "__hostNative")) });
            }
            """, "main").AsString();

        var report = System.Text.Json.JsonDocument.Parse(result).RootElement;
        Assert.That(report.GetProperty("found").GetArrayLength(), Is.EqualTo(0), "host objects reachable: " + report.GetProperty("found"));
        Assert.That(report.GetProperty("facade").GetString(), Is.EqualTo(string.Join(",", FacadeMembers)));
        Assert.That(report.GetProperty("frozen").GetBoolean(), Is.True);
        Assert.That(report.GetProperty("writable").GetBoolean(), Is.False);
        Assert.That(report.GetProperty("enumerable").GetBoolean(), Is.False);
        Assert.That(report.GetProperty("native").GetString(), Is.EqualTo("undefined"));
        Assert.That(report.GetProperty("nativeDescriptor").GetString(), Is.EqualTo("undefined"));
    }

    [Test]
    public void HostExceptions_ReachScriptsAsPlainErrors_WithoutTheDotNetException()
    {
        var result = Call("hostexceptions", """
            export function main(): string {
              const h = (globalThis as any).__host;
              const out: string[] = [];
              for (const attempt of [() => h.subscribe("event", "x", 42, {}), () => h.unsubscribe("not-a-guid"), () => h.termWrite(1, 42)]) {
                try { attempt(); out.push("no error"); } catch (e: any) {
                  out.push(Reflect.ownKeys(e).map(String).sort().join("/") + "|" + typeof e.hostException + "|" + (e instanceof Error) + "|" + e.stack.includes("System.") + "|" + e.stack.includes("HostBridge"));
                }
              }
              return out.join(";");
            }
            """, "main").AsString();

        Assert.That(result, Is.EqualTo(string.Join(";", Enumerable.Repeat("message/stack|undefined|true|false|false", 3))));
    }

    [Test]
    public void Tampering_WithTheFacadeAndGlobals_NeitherReachesTheNativeBridgeNorChangesThePrelude()
    {
        var module = LoadScript("tamper", """
            import { ksa } from "ksa";
            export async function main(): Promise<void> {
              const g: any = globalThis;
              const out: string[] = [];
              const facade = g.__host;
              try { facade.read = () => "evil"; } catch { /* frozen: assignment throws in strict code */ }
              out.push("read still " + (facade.read("time", "ut") === null ? "host" : "evil"));
              try { Object.defineProperty(g, "__hostNative", { value: facade, configurable: true }); } catch { }
              delete g.__hostNative;
              delete g.__host;
              out.push("deleted=" + (typeof g.__host === "undefined"));
              g.__host = { read: () => "evil", log: () => { throw new Error("evil log"); } };
              const again = await import("ksa");
              out.push("same prelude=" + (again.ksa === ksa && again.default === ksa) + " keys=" + Object.keys(again).sort().join("/"));
              for (const specifier of ["ksa?x=1", "KSA", "./ksa", "ksa/", "ksa.js", "file:///ksa", "ksa-init.mjs", "ksa-entry.mjs", "[internal] ksa-runtime-helper.js"]) {
                try { const other: any = await import(specifier); out.push(specifier + " loaded " + (other.ksa === ksa)); } catch { out.push(specifier + " blocked"); }
              }
              ksa.console.log(out.join("|"));
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        PumpUntil(() => Host.LogMessages.Count > 0 || Host.ReportedErrors.Count > 0, "main to finish");

        Assert.That(Host.ReportedErrors, Is.Empty);
        Assert.That(Host.LogMessages.Single().Split('|'), Is.EqualTo(new[]
        {
            "read still host",
            "deleted=true",
            "same prelude=true keys=default/ksa",
            "ksa?x=1 blocked", "KSA blocked", "./ksa blocked", "ksa/ blocked", "ksa.js blocked", "file:///ksa blocked",
            "ksa-init.mjs blocked", "ksa-entry.mjs blocked", "[internal] ksa-runtime-helper.js blocked",
        }), "the log call went through the real (captured) host even though the global was replaced");
    }

    [Test]
    public void TwoScripts_DoNotShareGlobalsOrPreludeState()
    {
        var first = LoadScript("isolation-a", """
            import { ksa } from "ksa";
            (globalThis as any).secret = "A";
            ksa.state.set("k", "from A");
            export function peek(): string { return String((globalThis as any).secret); }
            """);
        var second = LoadScript("isolation-b", """
            export function peek(): string { return typeof (globalThis as any).secret + "/" + String((globalThis as any).ksa === undefined); }
            """);

        Assert.That(Rt.Invoke(Rt.GetExport(first, "peek"), []).AsString(), Is.EqualTo("A"));
        Assert.That(Rt.Invoke(Rt.GetExport(second, "peek"), []).AsString(), Is.EqualTo("undefined/false"), "B has its own global object and its own prelude instance");
    }

    [Test]
    public void StackCallSites_DoNotExposeFunctionsOrThis_OfTheRuntimeHelpers()
    {
        var result = Call("callsites", """
            export function main(): string {
              const g: any = globalThis;
              const seen: string[] = [];
              const previous = Error.prepareStackTrace;
              (Error as any).prepareStackTrace = (_error: unknown, sites: any[]) => sites;
              let sites: any[];
              try { sites = new Error("probe").stack as unknown as any[]; } finally { (Error as any).prepareStackTrace = previous; }
              for (const site of sites) seen.push(typeof site.getFunction() + "/" + typeof site.getThis() + "/" + String(site.getFileName()).includes("ksa-runtime-helper"));
              return seen.join(",");
            }
            """, "main").AsString();

        var frames = result.Split(',');
        Assert.That(frames.Length, Is.GreaterThanOrEqualTo(2), "main is called through the runtime's invoke helper, so both appear");
        Assert.That(frames, Is.All.Matches<string>(f => f.StartsWith("undefined/undefined/", StringComparison.Ordinal)),
            "strict-mode frames never hand out their function or receiver");
    }

    [Test]
    public void ArrayBufferAllocation_BeyondTheIsolateLimit_Fails_AndTheScriptStaysUsable()
    {
        var module = LoadScript("bigbuffer", """
            export function grab(): string {
              try { return "allocated " + new ArrayBuffer(512 * 1024 * 1024).byteLength; } catch (e: any) { return "refused " + e.name; }
            }
            export function ok(): number { return 5; }
            """);

        Assert.That(Rt.Invoke(Rt.GetExport(module, "grab"), []).AsString(), Does.StartWith("refused "));
        Assert.That(Rt.Invoke(Rt.GetExport(module, "ok"), []).AsNumber(), Is.EqualTo(5));
    }

    [Test]
    public void AtomicsWait_CannotFreezeTheGameThread_TheInterruptEndsIt()
    {
        var module = LoadScript("blocker", """
            import { ksa } from "ksa";
            export function block() {
              ksa.console.log("blocking");
              Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0);
            }
            export function ok(): number { return 1; }
            """);
        var blocking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Host.OnLog = log =>
        {
            if (log.Message == "blocking")
                blocking.TrySetResult();
        };

        using var finished = new ManualResetEventSlim(false);
        var interrupter = Task.Run(async () =>
        {
            await blocking.Task.WaitAsync(Deadline);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!finished.Wait(25) && clock.Elapsed < Deadline)
                Rt.Interrupt();
        });

        try
        {
            var ex = Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(Rt.GetExport(module, "block"), []))!;
            Assert.That(ex.Message, Is.EqualTo("interrupted"));
        }
        finally
        {
            finished.Set();
            interrupter.Wait(Deadline);
        }

        Assert.That(Rt.Invoke(Rt.GetExport(module, "ok"), []).AsNumber(), Is.EqualTo(1));
    }
}
