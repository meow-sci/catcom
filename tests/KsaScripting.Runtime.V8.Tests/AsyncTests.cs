using KsaScripting.Core.Commands;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.V8.Tests;

[TestFixture]
public sealed class AsyncTests : V8TestBase
{
    [Test]
    public void Async_Sleep_ResumesAfterWaitCompletesAndAdvance()
    {
        var module = LoadScript("sleeper", """
            import { ksa } from "ksa";
            export async function main() {
              await ksa.sleep(1);
              ksa.console.log("after");
            }
            """);

        var result = Rt.Invoke(Rt.GetExport(module, "main"), []);

        Assert.That(result.IsNull, Is.True, "an async function returns immediately");
        Assert.That(Host.Waits, Has.Count.EqualTo(1));
        Assert.That(Host.Waits[0].Kind, Is.EqualTo("sleep"));
        Assert.That(Host.Waits[0].Arg["seconds"].AsNumber(), Is.EqualTo(1));
        Assert.That(Host.Waits[0].Arg["clock"].AsString(), Is.EqualTo("ut"));
        Assert.That(Host.LogMessages, Does.Not.Contain("after"));
        Assert.That(Rt.Advance(TimeSpan.FromSeconds(1)), Is.False, "nothing is queued before the wait completes");
        Assert.That(Host.LogMessages, Does.Not.Contain("after"));

        var enteredBefore = Host.EnteredScripts.Count;
        Host.Waits[0].Wait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.LogMessages.Contains("after"), "the continuation to log");

        var after = Host.Logs.Single(l => l.Message == "after");
        Assert.That(after.Script, Is.EqualTo("sleeper"), "the continuation runs under EnterScript(script)");
        Assert.That(Host.EnteredScripts.Skip(enteredBefore), Does.Contain("sleeper"));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void Async_WaitResultValueReachesScript()
    {
        var module = LoadScript("stepper", """
            import { ksa } from "ksa";
            export async function main() {
              const step = await ksa.nextStep();
              ksa.console.log(`step ${step.stepIndex} at ${step.ut}`);
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        Host.Waits.Single().Wait.Complete(ScriptValue.Map(new Dictionary<string, ScriptValue>
        {
            ["ut"] = ScriptValue.From(12.5),
            ["dt"] = ScriptValue.From(0.02),
            ["stepIndex"] = ScriptValue.From(7d),
            ["frame"] = ScriptValue.From(90d),
        }));

        PumpUntil(() => Host.LogMessages.Count > 0, "the step log");
        Assert.That(Host.LogMessages.Single(), Is.EqualTo("step 7 at 12.5"));
    }

    [Test]
    public void Async_FailedWait_RejectsAndIsReported()
    {
        var module = LoadScript("failing", """
            import { ksa } from "ksa";
            export async function main() {
              await ksa.sleep(1);
              ksa.console.log("not reached");
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        Host.Waits.Single().Wait.Fail(new InvalidOperationException("wait exploded"));

        PumpUntil(() => Host.ReportedErrors.Count > 0, "the rejection to be reported");

        Assert.That(Host.ReportedErrors, Has.Count.EqualTo(1));
        Assert.That(Host.ReportedErrors[0].Message, Does.Contain("wait exploded"));
        Assert.That(Host.LogMessages, Is.Empty);
    }

    [Test]
    public void Async_CancelledWait_RejectsCatchableInScript()
    {
        var module = LoadScript("cancelled", """
            import { ksa } from "ksa";
            export async function main() {
              try {
                await ksa.sleep(1);
              } catch (e) {
                ksa.console.log("caught: " + (e as Error).message);
              }
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        Host.Waits.Single().Wait.Cancel();

        PumpUntil(() => Host.LogMessages.Count > 0, "the catch block to log");
        Assert.That(Host.LogMessages.Single(), Does.StartWith("caught: "));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void Async_ErrorBeforeFirstAwait_IsReportedNotThrown()
    {
        var module = LoadScript("early", """
            export async function main() {
              throw new Error("early failure");
            }
            """);

        Assert.DoesNotThrow(() => Rt.Invoke(Rt.GetExport(module, "main"), []));
        PumpUntil(() => Host.ReportedErrors.Count > 0, "the early rejection to be reported");
        Assert.That(Host.ReportedErrors[0].Message, Does.Contain("early failure"));
    }

    [Test]
    public void SolverAct_ReturnsPromiseResolvedWithResultMap()
    {
        Host.ActHandler = call => call.Action == "vessel.attitude_mode"
            ? new ActResult(null, Host.Wait("act", ScriptValue.Null))
            : new ActResult(CommandResult.Ok, null);

        var module = LoadScript("solver", """
            import { ksa } from "ksa";
            export async function main() {
              const frame = ksa.vessel("x").ignite();
              ksa.console.log("frame " + JSON.stringify(frame));
              const result = await ksa.vessel("x").setAttitudeMode("Prograde");
              ksa.console.log("solver " + JSON.stringify(result));
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);

        Assert.That(Host.Acts.Select(a => (a.Action, a.Token)), Is.EqualTo(new[] { ("vessel.ignite", (string?)null), ("vessel.attitude_mode", "Prograde") }));
        Assert.That(Host.LogMessages, Is.EqualTo(new[] { """frame {"ok":true,"outcome":"Ok","message":null}""" }), "Frame actions return the result map directly");

        var pending = Host.Waits.Single(w => w.Kind == "act");
        pending.Wait.Complete(CommandResult.Denied("no fuel").ToScriptValue());
        PumpUntil(() => Host.LogMessages.Count == 2, "the solver result");

        Assert.That(Host.LogMessages[1], Is.EqualTo("""solver {"ok":false,"outcome":"Denied","message":"no fuel"}"""));
    }

    [Test]
    public void Wait_ArgumentsCrossAsMapsWithFunctions()
    {
        var module = LoadScript("until", """
            import { ksa } from "ksa";
            export async function main() {
              const ok = await ksa.until(() => true, { timeoutSeconds: 2, clock: "wall" });
              ksa.console.log("until=" + ok);
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        var wait = Host.Waits.Single();

        Assert.That(wait.Kind, Is.EqualTo("until"));
        Assert.That(wait.Arg["timeoutSeconds"].AsNumber(), Is.EqualTo(2));
        Assert.That(wait.Arg["clock"].AsString(), Is.EqualTo("wall"));
        var predicate = wait.Arg["fn"].AsFunction();
        Assert.That(Rt.Invoke(predicate, []).AsBool(), Is.True, "a JS function in a wait argument is a callable of the same runtime");

        wait.Wait.Complete(ScriptValue.True);
        PumpUntil(() => Host.LogMessages.Count > 0, "until result");
        Assert.That(Host.LogMessages.Single(), Is.EqualTo("until=true"));
    }

    [Test]
    public void Advance_DropsWorkOfUnloadedScripts()
    {
        var module = LoadScript("doomed", """
            import { ksa } from "ksa";
            export async function main() {
              await ksa.sleep(1);
              ksa.console.log("zombie");
            }
            """);
        Rt.Invoke(Rt.GetExport(module, "main"), []);
        var wait = Host.Waits.Single().Wait;

        Rt.Unload(module);
        wait.Complete(ScriptValue.Null);

        // A survivor proves the queue keeps flowing while the dead script's items are dropped.
        var other = LoadScript("survivor", """
            import { ksa } from "ksa";
            export async function main() { await ksa.sleep(1); ksa.console.log("alive"); }
            """);
        Rt.Invoke(Rt.GetExport(other, "main"), []);
        Host.Waits.Single(w => w != Host.Waits[0]).Wait.Complete(ScriptValue.Null);

        PumpUntil(() => Host.LogMessages.Contains("alive"), "the survivor");
        Assert.That(Host.LogMessages, Does.Not.Contain("zombie"));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void Reload_SameName_NewEngineKeepsRunning_OldEngineWorkIsDropped()
    {
        const string source = """
            import { ksa } from "ksa";
            export async function main() { await ksa.sleep(1); ksa.console.log("done " + Date.now() * 0); }
            """;
        var first = LoadScript("hot", source);
        Rt.Invoke(Rt.GetExport(first, "main"), []);
        var firstWait = Host.Waits.Single().Wait;

        Rt.Unload(first);
        var second = LoadScript("hot", source);
        Rt.Invoke(Rt.GetExport(second, "main"), []);
        var secondWait = Host.Waits[1].Wait;

        firstWait.Complete(ScriptValue.Null);
        secondWait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.LogMessages.Count >= 1, "the reloaded script to continue");

        // Only the live engine's continuation ran, even though both scripts share a name.
        Assert.That(Host.LogMessages, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReportedAsyncErrors_CarryTheMappedTypeScriptLocation()
    {
        var path = WriteScript("asyncloc.ts", """
            import { ksa } from "ksa";
            interface Unused { a: number }
            export async function main() {
              await ksa.sleep(1);
              throw new Error("late failure");
            }
            """);
        var module = Rt.Load(new ScriptSource("asyncloc", path, File.ReadAllText(path), ScriptLanguages.TypeScript));

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        Host.Waits.Single().Wait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.ReportedErrors.Count > 0, "the late failure");

        var error = Host.ReportedErrors.Single();
        Assert.That(error.Message, Does.Contain("late failure"));
        Assert.That(error.File, Is.EqualTo(path));
        Assert.That(error.Line, Is.EqualTo(5));
    }
}
