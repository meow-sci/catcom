using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.V8.Tests;

/// <summary>
///     Promise rejections nobody handles are reported through <c>IHostFacade.ReportError</c>, attributed to the owning script,
///     exactly once and never for rejections that do get a handler (including those of invoked async functions, which are
///     reported by the call that started them). Top-level await failures surface as load errors.
/// </summary>
[TestFixture]
public sealed class RejectionTests : V8TestBase
{
    private ScriptSource Write(string name, string code)
    {
        var path = WriteScript(name + ".ts", code);
        return new ScriptSource(name, path, code, ScriptLanguages.TypeScript);
    }

    private int _barriers;

    /// <summary>
    ///     Proves everything queued so far has been processed, without sleeping: a sentinel script awaits one more host wait,
    ///     and the queue is FIFO, so once the sentinel's continuation has run any duplicate report queued earlier would already
    ///     have been delivered.
    /// </summary>
    private void Barrier()
    {
        var name = "barrier" + ++_barriers;
        var module = LoadScript(name, "import { ksa } from \"ksa\";\nexport async function main() { await ksa.sleep(0); ksa.console.log(\"" + name + " reached\"); }");
        Rt.Invoke(Rt.GetExport(module, "main"), []);
        Host.Waits[^1].Wait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.LogMessages.Contains(name + " reached"), "the barrier script to continue");
    }

    [Test]
    public void UnobservedRejection_OfADerivedPromise_IsReported_WithTheScriptAndItsTypeScriptLocation()
    {
        var source = Write("unobserved", """
            import { ksa } from "ksa";
            interface Unused { a: number }
            export function main() {
              ksa.sleep(1).then(() => {
                throw new Error("x");
              });
            }
            """);
        var module = Rt.Load(source);

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        var wait = Host.Waits.Single().Wait;
        Barrier();
        Assert.That(Host.ReportedErrors, Is.Empty, "nothing is rejected yet");

        wait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.ReportedErrors.Count > 0, "the unhandled rejection to be reported");
        Barrier();

        var error = Host.ReportedErrors.Single();
        Assert.That(error.Message, Is.EqualTo("Unhandled promise rejection: Error: x"));
        Assert.That(Host.ReportedErrorScripts.Single(), Is.EqualTo("unobserved"), "attributed to the owning script");
        Assert.That(error.File, Is.EqualTo(source.Path));
        Assert.That(error.Line, Is.EqualTo(5));
        Assert.That(error.Traceback, Does.Contain(source.Path + ":5:"));
        Assert.That(error.Traceback, Does.Not.Contain("[internal]"));
    }

    [Test]
    public void FireAndForgetAsyncFunction_ThatRejectsLater_IsReportedOnce()
    {
        var module = LoadScript("forgotten", """
            import { ksa } from "ksa";
            async function work() {
              await ksa.sleep(1);
              throw new Error("background failure");
            }
            export function main() {
              work();
              return "returned";
            }
            """);

        var result = Rt.Invoke(Rt.GetExport(module, "main"), []);
        Host.Waits.Single().Wait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.ReportedErrors.Count > 0, "the rejection to be reported");
        Barrier();

        Assert.That(result.AsString(), Is.EqualTo("returned"));
        Assert.That(Host.ReportedErrors.Single().Message, Is.EqualTo("Unhandled promise rejection: Error: background failure"));
    }

    [Test]
    public void IgnoredWaitThatFails_IsAnUnhandledRejection()
    {
        var module = LoadScript("ignoredwait", """
            import { ksa } from "ksa";
            export function main() { ksa.sleep(1); }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        Host.Waits.Single().Wait.Fail(new InvalidOperationException("wait exploded"));
        PumpUntil(() => Host.ReportedErrors.Count > 0, "the rejection to be reported");

        Assert.That(Host.ReportedErrors.Single().Message, Does.StartWith("Unhandled promise rejection:").And.Contain("wait exploded"));
    }

    [Test]
    public void SynchronousUnhandledRejection_InAHook_IsReportedByAdvance_NotThrownByInvoke()
    {
        var module = LoadScript("hook", """
            export function onTick() {
              Promise.reject(new Error("in hook"));
            }
            """);

        Assert.DoesNotThrow(() => Rt.Invoke(Rt.GetExport(module, "onTick"), []));
        Assert.That(Host.ReportedErrors, Is.Empty, "reporting happens in Advance, outside the call");
        PumpUntil(() => Host.ReportedErrors.Count > 0, "the rejection to be reported");

        Assert.That(Host.ReportedErrors.Single().Message, Is.EqualTo("Unhandled promise rejection: Error: in hook"));
        Assert.That(Host.ReportedErrorScripts.Single(), Is.EqualTo("hook"));
    }

    [Test]
    public void RejectionsThatGetAHandler_AreNotReported()
    {
        var module = LoadScript("handled", """
            import { ksa } from "ksa";
            export async function main() {
              Promise.reject(new Error("caught directly")).catch(() => {});
              const late = Promise.reject(new Error("handler attached in a microtask"));
              Promise.resolve().then(() => late.catch(() => {}));
              try { await Promise.reject(new Error("awaited")); } catch (e) { ksa.console.log("caught awaited"); }
              const waited = ksa.sleep(1);
              waited.then(undefined, () => ksa.console.log("wait failure handled"));
              await Promise.allSettled([Promise.reject(new Error("settled"))]);
              await Promise.any([Promise.reject(new Error("one")), Promise.resolve(1)]);
              ksa.console.log("main done");
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        Host.Waits.Single().Wait.Fail(new InvalidOperationException("boom"));
        PumpUntil(() => Host.LogMessages.Contains("wait failure handled") && Host.LogMessages.Contains("main done"), "main to finish");
        Barrier();

        Assert.That(Host.ReportedErrors, Is.Empty);
        Assert.That(Host.LogMessages.Where(m => !m.StartsWith("barrier", StringComparison.Ordinal)), Is.EquivalentTo(new[] { "caught awaited", "wait failure handled", "main done" }));
    }

    [Test]
    public void InvokedAsyncFunction_ThatRejects_IsReportedExactlyOnce_AsAnInvokeFailure()
    {
        var module = LoadScript("asyncexport", """
            import { ksa } from "ksa";
            export async function early() { throw new Error("before any await"); }
            export async function late() { await ksa.sleep(1); throw new Error("after an await"); }
            export const thenable = () => ({ then(_resolve: unknown, reject: (e: unknown) => void) { reject(new Error("from a thenable")); } });
            """);

        Rt.Invoke(Rt.GetExport(module, "early"), []);
        Rt.Invoke(Rt.GetExport(module, "late"), []);
        Rt.Invoke(Rt.GetExport(module, "thenable"), []);
        Host.Waits.Single().Wait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.ReportedErrors.Count >= 3, "all three failures");
        Barrier();

        Assert.That(Host.ReportedErrors.Select(e => e.Message), Is.EquivalentTo(new[]
        {
            "Error: before any await",
            "Error: after an await",
            "Error: from a thenable",
        }), "no \"Unhandled promise rejection\" prefix and no duplicates: the invoke observed them");
        Assert.That(Host.ReportedErrorScripts, Is.All.EqualTo("asyncexport"));
    }

    [Test]
    public void NonErrorReasons_AreDescribed()
    {
        var module = LoadScript("reasons", """
            export function main() {
              Promise.reject("plain text");
              Promise.reject({ code: 7 });
              Promise.reject(42);
              Promise.reject(undefined);
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        PumpUntil(() => Host.ReportedErrors.Count >= 4, "all four rejections");

        Assert.That(Host.ReportedErrors.Select(e => e.Message), Is.EqualTo(new[]
        {
            "Unhandled promise rejection: plain text",
            "Unhandled promise rejection: {\"code\":7}",
            "Unhandled promise rejection: 42",
            "Unhandled promise rejection: undefined",
        }));
    }

    [Test]
    public void FailingDynamicImport_WithoutAHandler_IsReported()
    {
        File.WriteAllText(Path.Combine(Root, "outside.ts"), "export const secret = 1;");
        var module = LoadScript("dynfail", "export function main() { import(\"../outside.ts\"); import(\"./missing.ts\"); }");

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        PumpUntil(() => Host.ReportedErrors.Count >= 2, "both import failures");

        Assert.That(Host.ReportedErrors.Select(e => e.Message), Has.Some.Contain("escapes the scripts folder"));
        Assert.That(Host.ReportedErrors.Select(e => e.Message), Has.Some.Contain("cannot find module './missing.ts'"));
        Assert.That(Host.ReportedErrors.Select(e => e.Message), Has.All.StartWith("Unhandled promise rejection: "));
    }

    [Test]
    public void RejectionAtModuleTopLevel_IsReportedAfterLoad()
    {
        var path = WriteScript("toplevel.ts", "const n: number = 1;\nPromise.reject(new Error(\"load-time\"));\nexport function main() {}\n");

        var module = Rt.Load(new ScriptSource("toplevel", path, File.ReadAllText(path), ScriptLanguages.TypeScript));
        Assert.That(Rt.Exports(module), Is.EqualTo(new[] { "main" }), "the load itself succeeded");
        PumpUntil(() => Host.ReportedErrors.Count > 0, "the load-time rejection to be reported");

        var error = Host.ReportedErrors.Single();
        Assert.That(error.Message, Is.EqualTo("Unhandled promise rejection: Error: load-time"));
        Assert.That((error.File, error.Line), Is.EqualTo((path, 2)));
        Assert.That(Host.ReportedErrorScripts.Single(), Is.EqualTo("toplevel"));
    }

    [Test]
    public void RejectionsOfAnUnloadedScript_AreDropped_AndOtherScriptsStillReport()
    {
        var doomed = LoadScript("doomed", "export function main() { Promise.reject(new Error(\"never reported\")); }");
        var other = LoadScript("other", "export function main() { Promise.reject(new Error(\"reported\")); }");
        Rt.Invoke(Rt.GetExport(doomed, "main"), []);
        Rt.Invoke(Rt.GetExport(other, "main"), []);

        Rt.Unload(doomed);
        PumpUntil(() => Host.ReportedErrors.Count > 0, "the surviving script's rejection");
        Barrier();

        Assert.That(Host.ReportedErrors.Single().Message, Does.Contain("reported"));
        Assert.That(Host.ReportedErrorScripts.Single(), Is.EqualTo("other"));
    }

    [Test]
    public void ScriptsReportToTheirOwnName_WhenInvokedFromEachOther()
    {
        var inner = LoadScript("inner", "export function boom() { Promise.reject(new Error(\"inner rejection\")); }");
        var innerBoom = Rt.GetExport(inner, "boom");
        Host.ActHandler = _ =>
        {
            Rt.Invoke(innerBoom, []);
            return new Core.Hosting.ActResult(Core.Commands.CommandResult.Ok, null);
        };
        var outer = LoadScript("outer", """
            import { ksa } from "ksa";
            export function main() {
              ksa.vessel("x").ignite();
              Promise.reject(new Error("outer rejection"));
            }
            """);

        Rt.Invoke(Rt.GetExport(outer, "main"), []);
        PumpUntil(() => Host.ReportedErrors.Count >= 2, "both rejections");

        var byScript = Host.ReportedErrors.Zip(Host.ReportedErrorScripts, (e, s) => (s, e.Message)).ToDictionary(p => p.s, p => p.Message);
        Assert.That(byScript["inner"], Does.Contain("inner rejection"));
        Assert.That(byScript["outer"], Does.Contain("outer rejection"));
    }

    // ---------------------------------------------------------------- top-level await

    [Test]
    public void TopLevelAwait_ThatRejects_FailsTheLoadWithTheRealError()
    {
        var path = WriteScript("tlareject.ts", "const n: number = 1;\nawait Promise.resolve();\nthrow new Error(\"after the await\");\nexport function main() {}\n");

        var ex = Assert.Throws<ScriptLoadException>(() => Rt.Load(new ScriptSource("tlareject", path, File.ReadAllText(path), ScriptLanguages.TypeScript)))!;

        Assert.That(ex.Message, Is.EqualTo("Error: after the await"));
        Assert.That((ex.File, ex.Line), Is.EqualTo((path, 3)));
        Assert.That(Rt.LoadedScriptCount, Is.EqualTo(0));
        Barrier();
        Assert.That(Host.ReportedErrors, Is.Empty, "a failed load reports nothing else");
    }

    [Test]
    public void TopLevelAwait_OfARejectedPromise_AndOfAFailingDynamicImport_FailTheLoadWithTheRealError()
    {
        var rejected = Assert.Throws<ScriptLoadException>(() => LoadScript("tlarejected", "await Promise.reject(new Error(\"rejected promise\"));\nexport function main() {}"))!;
        var missing = Assert.Throws<ScriptLoadException>(() => LoadScript("tlamissing", "await import(\"./nope.ts\");\nexport function main() {}"))!;

        Assert.That(rejected.Message, Is.EqualTo("Error: rejected promise"));
        Assert.That(missing.Message, Does.Contain("cannot find module './nope.ts'"));
    }

    [Test]
    public void TopLevelAwait_OfADynamicImport_Works()
    {
        WriteScript("lib/dyn.ts", "export const v = 5;");

        var module = LoadScript("tladyn", "const m = await import(\"./lib/dyn.ts\");\nexport function main(): number { return m.v; }");

        Assert.That(Rt.Invoke(Rt.GetExport(module, "main"), []).AsNumber(), Is.EqualTo(5));
    }

    [Test]
    public void TopLevelAwait_OfAHostWait_IsRefused_AndALaterFailureOfThatWaitReportsNothing()
    {
        var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("tlawait", """
            import { ksa } from "ksa";
            await ksa.sleep(1);
            throw new Error("not reached");
            export function main() {}
            """))!;
        Assert.That(ex.Message, Does.Contain("top-level await of host waits is not supported; use main(ctx)"));

        Host.Waits.Single().Wait.Fail(new InvalidOperationException("late failure of the orphaned wait"));
        Barrier();

        Assert.That(Host.ReportedErrors, Is.Empty, "the script is gone: nothing to attribute the failure to");
    }
}
