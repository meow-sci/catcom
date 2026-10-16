using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KsaScripting.Core.Commands;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.V8.Tests;

/// <summary>
///     Every script lives in a V8 isolate of its own (fault isolation): the soft heap limit, an interrupt and the values a
///     script owns never reach another script. The one exception is debug mode (a debug port), where all scripts share a single
///     inspectable isolate.
/// </summary>
[TestFixture]
public sealed class IsolationTests : V8TestBase
{
    private const long MiB = 1024 * 1024;

    // ------------------------------------------------------------------ heap limit

    [Test]
    public void HeapLimit_IsPerScript_AHoarderCannotBreakItsNeighbours_AndUnloadingItGivesTheMemoryBack()
    {
        Rt.Dispose();
        var runtime = NewRuntime(heapLimitBytes: 32 * MiB);
        var hoarder = LoadScript("hoarder", """
            export function hoard(): number {
              // Keeps everything reachable from a global and never stops by itself: only the heap monitor ends the call.
              const g: any = globalThis;
              g.keep = [];
              for (let i = 0; i < 200_000_000; i++) g.keep.push({ a: i, b: "payload-" + i });
              return g.keep.length;
            }
            export function held(): number { return (globalThis as any).keep.length; }
            """, runtime);
        var neighbour = LoadScript("neighbour", "export function trivial(): number { return 7; }", runtime);
        var trivial = runtime.GetExport(neighbour, "trivial");
        Assert.That(runtime.Invoke(trivial, []).AsNumber(), Is.EqualTo(7));
        var idleHeap = runtime.Stats.HeapBytes;

        var ex = Assert.Throws<ScriptRuntimeException>(() => runtime.Invoke(runtime.GetExport(hoarder, "hoard"), []))!;

        Assert.That(ex.Message, Does.StartWith("heap limit exceeded ("));
        Assert.That(ex.Message, Does.Contain("> 33554432 bytes)"));
        var hoarded = runtime.Stats.HeapBytes;
        Assert.That(hoarded - idleHeap, Is.GreaterThan(16 * MiB), "the hoarder still holds its memory in its global");

        // Without isolation the hoarder's overshoot would make every call of every script fail until it is unloaded.
        for (var i = 0; i < 25; i++)
            Assert.That(runtime.Invoke(trivial, []).AsNumber(), Is.EqualTo(7), $"call {i} of the neighbour");
        Assert.That(Host.ReportedErrors, Is.Empty);

        runtime.Unload(hoarder);

        var reclaimed = runtime.Stats.HeapBytes;
        Assert.That(reclaimed, Is.LessThan(hoarded - 16 * MiB), "unloading the hoarder disposes its isolate and so its memory");
        Assert.That(reclaimed, Is.LessThan(16 * MiB));
        Assert.That(runtime.Invoke(trivial, []).AsNumber(), Is.EqualTo(7));
        TestContext.Progress.WriteLine($"heap isolation: idle {idleHeap / 1024} KiB, hoarder at violation {hoarded / 1024} KiB, after unload {reclaimed / 1024} KiB");
    }

    [Test]
    public void HeapLimit_Violation_RearmsOnlyTheOffendingScript()
    {
        Rt.Dispose();
        var runtime = NewRuntime(heapLimitBytes: 32 * MiB);
        var hog = LoadScript("hog", """
            export function hog(): number {
              const keep: object[] = [];
              for (let i = 0; i < 200_000_000; i++) keep.push({ a: i, b: "payload-" + i });
              return keep.length;
            }
            export function small(): number { return 1; }
            """, runtime);
        var bystander = LoadScript("bystander", """
            export function hog(): number {
              const keep: object[] = [];
              for (let i = 0; i < 200_000_000; i++) keep.push({ a: i, b: "payload-" + i });
              return keep.length;
            }
            export function small(): number { return 2; }
            """, runtime);

        // Both scripts overrun their own limit in turn. Each violation is handled in its own isolate: the failed script
        // is usable again as soon as its garbage is gone (its limit was re-armed) and the other one never notices.
        var hogError = Assert.Throws<ScriptRuntimeException>(() => runtime.Invoke(runtime.GetExport(hog, "hog"), []))!;
        Assert.That(hogError.Message, Does.StartWith("heap limit exceeded ("));
        Assert.That(runtime.Invoke(runtime.GetExport(bystander, "small"), []).AsNumber(), Is.EqualTo(2));
        Assert.That(runtime.Invoke(runtime.GetExport(hog, "small"), []).AsNumber(), Is.EqualTo(1));

        var bystanderError = Assert.Throws<ScriptRuntimeException>(() => runtime.Invoke(runtime.GetExport(bystander, "hog"), []))!;
        Assert.That(bystanderError.Message, Does.StartWith("heap limit exceeded ("));
        Assert.That(runtime.Invoke(runtime.GetExport(hog, "small"), []).AsNumber(), Is.EqualTo(1));
        Assert.That(runtime.Invoke(runtime.GetExport(bystander, "small"), []).AsNumber(), Is.EqualTo(2));
    }

    [Test]
    public void HeapLimit_ExceededWhileLoading_FailsThatLoadOnly_AndReleasesTheIsolate()
    {
        Rt.Dispose();
        var runtime = NewRuntime(heapLimitBytes: 32 * MiB);
        var bystander = LoadScript("loadbystander", "export function small(): number { return 2; }", runtime);

        var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("loadhog", """
            const keep: object[] = [];
            for (let i = 0; i < 200_000_000; i++) keep.push({ a: i, b: "payload-" + i });
            export function f() {}
            """, runtime))!;

        Assert.That(ex.Message, Does.StartWith("heap limit exceeded ("));
        Assert.That(ex.Message, Does.Contain("> 33554432 bytes)"));
        Assert.That(runtime.LoadedScriptCount, Is.EqualTo(1));
        Assert.That(runtime.ScriptIsolates, Has.Count.EqualTo(1), "the failed load disposed its engine and its isolate");
        Assert.That(runtime.Invoke(runtime.GetExport(bystander, "small"), []).AsNumber(), Is.EqualTo(2));
        Assert.That(runtime.Stats.HeapBytes, Is.LessThan(16 * MiB), "the memory the failed script grabbed is gone");
    }

    // ------------------------------------------------------------------ many scripts

    [Test]
    public void ManyScripts_LoadedTogether_WorkIndependently_AndUnloadCleanly()
    {
        const int Count = 20;
        Rt.Dispose();
        var runtime = NewRuntime(heapLimitBytes: 64 * MiB);
        var sources = Enumerable.Range(0, Count).Select(i =>
        {
            var name = "multi" + i;
            var code = $$"""
                import { ksa } from "ksa";
                let calls = 0;
                export function bump(): number { return ++calls; }
                export function who(): string { return "{{name}}"; }
                export async function later() { await ksa.sleep(1); ksa.console.log("later {{name}}"); }
                """;
            return new ScriptSource(name, WriteScript(name + ".ts", code), code, ScriptLanguages.TypeScript);
        }).ToArray();

        var memoryBefore = Environment.WorkingSet;
        var loadMs = new List<double>();
        var modules = new List<ScriptModule>();
        long heapOfOne = 0;
        foreach (var source in sources)
        {
            var clock = Stopwatch.StartNew();
            modules.Add(runtime.Load(source));
            loadMs.Add(clock.Elapsed.TotalMilliseconds);
            if (modules.Count == 1)
                heapOfOne = runtime.Stats.HeapBytes;
        }

        var heapOfAll = runtime.Stats.HeapBytes;
        var memoryAfter = Environment.WorkingSet;
        Assert.That(runtime.LoadedScriptCount, Is.EqualTo(Count));
        Assert.That(runtime.ScriptIsolates, Has.Count.EqualTo(Count), "one isolate per script");
        Assert.That(heapOfOne, Is.GreaterThan(0));
        Assert.That(heapOfAll, Is.GreaterThan(heapOfOne * 5), "Stats.HeapBytes adds up the isolates of all scripts");

        // The state of each script is its own.
        for (var round = 1; round <= 3; round++)
        {
            for (var i = 0; i < Count; i++)
                Assert.That(runtime.Invoke(runtime.GetExport(modules[i], "bump"), []).AsNumber(), Is.EqualTo(round), sources[i].Name);
        }

        // Continuations of 20 isolates share one work queue and are each attributed to their own script.
        for (var i = 0; i < Count; i++)
            runtime.Invoke(runtime.GetExport(modules[i], "later"), []);
        Assert.That(Host.Waits, Has.Count.EqualTo(Count));
        foreach (var wait in Host.Waits.Reverse())
            wait.Wait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.Logs.Count(l => l.Message.StartsWith("later ", StringComparison.Ordinal)) == Count, "all continuations to run", runtime);
        Assert.That(Host.Logs.Where(l => l.Message.StartsWith("later ", StringComparison.Ordinal)),
            Is.All.Matches<LogCall>(l => l.Message == "later " + l.Script), "each continuation runs under its own script");
        Assert.That(Host.ReportedErrors, Is.Empty);

        var callables = modules.Select(m => runtime.GetExport(m, "who")).ToArray();
        var unloadClock = Stopwatch.StartNew();
        for (var i = 0; i < Count; i += 2)
            runtime.Unload(modules[i]);
        for (var i = 1; i < Count; i += 2)
            runtime.Unload(modules[i]);
        var unloadMs = unloadClock.Elapsed.TotalMilliseconds;

        Assert.That(runtime.LoadedScriptCount, Is.Zero);
        Assert.That(runtime.ScriptIsolates, Is.Empty);
        Assert.That(runtime.Stats.HeapBytes, Is.Zero, "no script isolate is left");
        Assert.That(callables, Is.All.Matches<ScriptCallable>(c => c.IsDisposed));

        // The runtime is as good as new.
        var again = runtime.Load(sources[0]);
        Assert.That(runtime.Invoke(runtime.GetExport(again, "bump"), []).AsNumber(), Is.EqualTo(1));

        var sorted = loadMs.Skip(1).Order().ToArray();
        TestContext.Progress.WriteLine(
            $"{Count} isolates: Load first {loadMs[0]:F1} ms, median of the rest {sorted[sorted.Length / 2]:F1} ms, max {sorted[^1]:F1} ms; "
            + $"used heap {heapOfOne / 1024} KiB for one isolate, {heapOfAll / 1024} KiB for {Count} ({heapOfAll / Count / 1024} KiB each); "
            + $"working set +{(memoryAfter - memoryBefore) / MiB} MiB ({(memoryAfter - memoryBefore) / Count / 1024} KiB each); unload {unloadMs / Count:F1} ms each");
    }

    [Test]
    public void ReloadingTheSameScriptName_GivesAFreshIsolate()
    {
        var path = WriteScript("again.ts", "let n = 0; export function bump(): number { return ++n; }");
        var code = File.ReadAllText(path);
        var first = Rt.Load(new ScriptSource("again", path, code, ScriptLanguages.TypeScript));
        Assert.That(Rt.Invoke(Rt.GetExport(first, "bump"), []).AsNumber(), Is.EqualTo(1));
        Assert.That(Rt.Invoke(Rt.GetExport(first, "bump"), []).AsNumber(), Is.EqualTo(2));

        var second = Rt.Load(new ScriptSource("again", path, code, ScriptLanguages.TypeScript));
        Assert.That(Rt.ScriptIsolates, Has.Count.EqualTo(2));
        Assert.That(Rt.Invoke(Rt.GetExport(second, "bump"), []).AsNumber(), Is.EqualTo(1), "a new load starts from scratch");

        Rt.Unload(first);
        Assert.That(Rt.ScriptIsolates, Has.Count.EqualTo(1));
        Assert.That(Rt.Invoke(Rt.GetExport(second, "bump"), []).AsNumber(), Is.EqualTo(2));
    }

    [Test]
    public void FailedLoad_ReleasesItsIsolate()
    {
        var before = Rt.Stats.HeapBytes;
        for (var i = 0; i < 5; i++)
        {
            Assert.Throws<ScriptLoadException>(() => LoadScript("thrower", "export const x = 1;\nthrow new Error(\"load failure\");"));
            Assert.Throws<ScriptLoadException>(() => LoadScript("badsyntax", "export const = 1;"));
        }

        Assert.That(Rt.LoadedScriptCount, Is.Zero);
        Assert.That(Rt.ScriptIsolates, Is.Empty, "a script that failed to load does not keep an isolate alive");
        Assert.That(Rt.Stats.HeapBytes, Is.EqualTo(before));
        Assert.That(Call("fine", "export function main(): number { return 1; }", "main").AsNumber(), Is.EqualTo(1));
    }

    // ------------------------------------------------------------------ nesting and interrupts

    [Test]
    public void Interrupt_TargetsTheEngineThatIsExecuting_WhenScriptsAreNested()
    {
        var inner = LoadScript("innerspin", """
            import { ksa } from "ksa";
            export function spin() { ksa.console.log("spinning inner"); for (;;) {} }
            export function ok(): number { return 3; }
            """);
        var outer = LoadScript("outerspin", """
            import { ksa } from "ksa";
            export function main(): number {
              ksa.vessel("v").ignite();
              ksa.console.log("spinning outer");
              for (;;) {}
            }
            export function ok(): number { return 4; }
            """);
        Assert.That(Rt.ScriptIsolates, Has.Count.EqualTo(2));

        // outer.main -> host act -> (a sync hook runs) inner.spin, which never returns by itself.
        ScriptRuntimeException? innerFailure = null;
        var innerFn = Rt.GetExport(inner, "spin");
        Host.ActHandler = _ =>
        {
            try
            {
                Rt.Invoke(innerFn, []);
            }
            catch (ScriptRuntimeException ex)
            {
                innerFailure = ex;
            }

            return new ActResult(CommandResult.Ok, null);
        };

        var innerSpinning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outerSpinning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Host.OnLog = log =>
        {
            if (log.Message == "spinning inner")
                innerSpinning.TrySetResult();
            else if (log.Message == "spinning outer")
                outerSpinning.TrySetResult();
        };

        var interrupter = Task.Run(async () =>
        {
            // The inner script is the one executing: only its isolate is interrupted, the outer script carries on ...
            await innerSpinning.Task.WaitAsync(Deadline);
            Rt.Interrupt();

            // ... and it can only get to the next log line if it was not: now it is the engine that is executing.
            await outerSpinning.Task.WaitAsync(Deadline);
            Rt.Interrupt();
        });

        ScriptRuntimeException? outerFailure;
        try
        {
            outerFailure = Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(Rt.GetExport(outer, "main"), []));
        }
        finally
        {
            // Never leave the helper waiting for log lines that will not come.
            innerSpinning.TrySetResult();
            outerSpinning.TrySetResult();
            interrupter.Wait(Deadline);
        }

        Assert.That(innerFailure?.Message, Is.EqualTo("interrupted"), "the first interrupt ended the nested call");
        Assert.That(outerFailure!.Message, Is.EqualTo("interrupted"), "the second interrupt ended the outer call");
        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "spinning inner", "spinning outer" }));
        Assert.DoesNotThrow(() => Rt.Interrupt(), "no engine is executing any more");
        Assert.That(Rt.Invoke(Rt.GetExport(inner, "ok"), []).AsNumber(), Is.EqualTo(3), "a stale interrupt does not poison the next call");
        Assert.That(Rt.Invoke(Rt.GetExport(outer, "ok"), []).AsNumber(), Is.EqualTo(4));
    }

    [Test]
    public void NestedInvoke_AcrossThreeIsolates_KeepsTheAttributionAndTheResult()
    {
        var third = LoadScript("third", """
            import { ksa } from "ksa";
            export function hello(): number { ksa.console.log("third"); return 30; }
            """);
        var thirdFn = Rt.GetExport(third, "hello");
        var second = LoadScript("second", """
            import { ksa } from "ksa";
            export function hello(): number { ksa.console.log("second before"); ksa.vessel("w").ignite(); ksa.console.log("second after"); return 20; }
            """);
        var secondFn = Rt.GetExport(second, "hello");

        var results = new List<double>();
        Host.ActHandler = call =>
        {
            results.Add(call.VesselId == "v" ? Rt.Invoke(secondFn, []).AsNumber() : Rt.Invoke(thirdFn, []).AsNumber());
            return new ActResult(CommandResult.Ok, null);
        };

        var result = Call("first", """
            import { ksa } from "ksa";
            export function main(): number { ksa.console.log("first before"); ksa.vessel("v").ignite(); ksa.console.log("first after"); return 10; }
            """, "main");

        Assert.That(result.AsNumber(), Is.EqualTo(10));
        Assert.That(results, Is.EqualTo(new[] { 30d, 20d }));
        Assert.That(Host.Logs.Select(l => l.Script + ":" + l.Message), Is.EqualTo(new[]
        {
            "first:first before", "second:second before", "third:third", "second:second after", "first:first after",
        }));
    }

    // ------------------------------------------------------------------ values never cross isolates

    [Test]
    public void FunctionValues_DeliveredToAnotherScript_BecomeNull_AndStayCallableOnlyInTheirOwner()
    {
        var producer = LoadScript("producer", """
            export function callback() { return () => 99; }
            export function bundle() { return { cb: () => 41, n: 2, list: [() => 1, 5] }; }
            """);
        var consumer = LoadScript("consumer", """
            export function take(v: any): string { return [v === null, typeof v].join(","); }
            export function takeBundle(v: any): string { return [v.cb === null, v.n, v.list[0] === null, v.list[1]].join(","); }
            """);
        Assert.That(Rt.ScriptIsolates, Has.Count.EqualTo(2));

        var function = Rt.Invoke(Rt.GetExport(producer, "callback"), []);
        var bundle = Rt.Invoke(Rt.GetExport(producer, "bundle"), []);
        Assert.That(function.Kind, Is.EqualTo(ScriptValueKind.Function));
        Assert.That(bundle["cb"].Kind, Is.EqualTo(ScriptValueKind.Function));

        // A function value from script A delivered to script B is a null in B, never an object of A's isolate.
        Assert.That(Rt.Invoke(Rt.GetExport(consumer, "take"), [function]).AsString(), Is.EqualTo("true,object"));
        Assert.That(Rt.Invoke(Rt.GetExport(consumer, "takeBundle"), [bundle]).AsString(), Is.EqualTo("true,2,true,5"));

        // The owner can still call it, and unloading the consumer leaves it alone.
        Rt.Unload(consumer);
        Assert.That(Rt.Invoke(function.AsFunction(), []).AsNumber(), Is.EqualTo(99));
        Assert.That(Rt.Invoke(bundle["cb"].AsFunction(), []).AsNumber(), Is.EqualTo(41));
    }

    [Test]
    public void AsyncWork_AndRejections_StayAttributedToTheirOwnScript()
    {
        var failing = LoadScript("failing", """
            import { ksa } from "ksa";
            export async function main() { await ksa.sleep(1); throw new Error("failing broke"); }
            """);
        var healthy = LoadScript("healthy", """
            import { ksa } from "ksa";
            export async function main() { await ksa.sleep(1); ksa.console.log("healthy finished"); }
            """);
        Rt.Invoke(Rt.GetExport(failing, "main"), []);
        Rt.Invoke(Rt.GetExport(healthy, "main"), []);
        Assert.That(Host.Waits, Has.Count.EqualTo(2));

        Host.Waits[1].Wait.Complete(ScriptValue.Null);
        Host.Waits[0].Wait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.ReportedErrors.Count > 0 && Host.LogMessages.Contains("healthy finished"), "both continuations");

        Assert.That(Host.ReportedErrors.Single().Message, Does.Contain("failing broke"));
        Assert.That(Host.ReportedErrorScripts.Single(), Is.EqualTo("failing"));
        Assert.That(Host.Logs.Single(l => l.Message == "healthy finished").Script, Is.EqualTo("healthy"));
    }

    // ------------------------------------------------------------------ stats and debugging

    [Test]
    public void Stats_HeapBytes_IsTheSumOverTheLiveScriptIsolates()
    {
        Assert.That(Rt.Stats.HeapBytes, Is.Zero, "the compiler's private isolate is not counted, and no script is loaded");

        LoadScript("sum1", "export const keep = new Array(100_000).fill(1);\nexport function f() {}");
        LoadScript("sum2", "export function f() {}");
        LoadScript("sum3", "export function f() {}");

        var isolates = Rt.ScriptIsolates;
        Assert.That(isolates, Has.Count.EqualTo(3));
        var sum = isolates.Sum(isolate => (long)isolate.GetHeapInfo().UsedHeapSize);
        Assert.That(Rt.Stats.HeapBytes, Is.EqualTo(sum).Within(2 * MiB));
        Assert.That(Rt.Stats.HeapBytes, Is.GreaterThan(isolates.Max(isolate => (long)isolate.GetHeapInfo().UsedHeapSize)), "more than any one of them");
    }

    [Test]
    public void DebugPort_KeepsOneSharedIsolateForAllScripts()
    {
        var port = FreeTcpPort();
        var runtime = NewRuntime(heapLimitBytes: 32 * MiB, debugPort: port);
        var first = LoadScript("debug1", "let n = 0; export function bump(): number { return ++n; }", runtime);
        var second = LoadScript("debug2", "let n = 100; export function bump(): number { return ++n; }", runtime);

        Assert.That(runtime.LoadedScriptCount, Is.EqualTo(2));
        Assert.That(runtime.ScriptIsolates, Has.Count.EqualTo(1), "one inspector target on the debug port, so one isolate");
        Assert.That(runtime.Invoke(runtime.GetExport(first, "bump"), []).AsNumber(), Is.EqualTo(1));
        Assert.That(runtime.Invoke(runtime.GetExport(second, "bump"), []).AsNumber(), Is.EqualTo(101), "engines are still separate");
        Assert.That(runtime.Stats.HeapBytes, Is.EqualTo((long)runtime.ScriptIsolates[0].GetHeapInfo().UsedHeapSize).Within(2 * MiB), "the shared isolate is counted once");

        using var http = new HttpClient { Timeout = Deadline };
        Assert.That(http.GetStringAsync($"http://127.0.0.1:{port}/json/version").GetAwaiter().GetResult(), Does.Contain("ClearScript"));

        // Unloading one script leaves the shared isolate (and the other script) alone.
        runtime.Unload(first);
        Assert.That(runtime.ScriptIsolates, Has.Count.EqualTo(1));
        Assert.That(runtime.Invoke(runtime.GetExport(second, "bump"), []).AsNumber(), Is.EqualTo(102));
        runtime.Unload(second);
        Assert.That(runtime.ScriptIsolates, Is.Empty);
        var third = LoadScript("debug3", "export function ok(): number { return 3; }", runtime);
        Assert.That(runtime.Invoke(runtime.GetExport(third, "ok"), []).AsNumber(), Is.EqualTo(3), "the shared isolate survives its scripts");
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
