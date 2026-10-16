using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.V8.Tests;

[TestFixture]
public sealed class ErrorTests : V8TestBase
{
    [Test]
    public void Error_LineIsMappedToTsSource()
    {
        // Lines 1-5 are type-only (removed by transpilation) so generated and original lines differ.
        var path = WriteScript("mapped.ts", """
            interface Options { verbose: boolean }
            type Id = string;
            declare const unused: number;
            enum Mode { A, B }

            export function boom(o: Options): void {
              throw new Error("kapow " + o.verbose);
            }
            """);
        var module = Rt.Load(new ScriptSource("mapped", path, File.ReadAllText(path), ScriptLanguages.TypeScript));

        var ex = Assert.Throws<ScriptRuntimeException>(
            () => Rt.Invoke(Rt.GetExport(module, "boom"), [ScriptValue.Map(new Dictionary<string, ScriptValue> { ["verbose"] = ScriptValue.True })]))!;

        Assert.That(ex.Message, Does.Contain("kapow true"));
        Assert.That(ex.Line, Is.EqualTo(7));
        Assert.That(ex.File, Is.EqualTo(path));
        Assert.That(ex.Traceback, Does.Contain(path + ":7:"));
        Assert.That(ex.Traceback, Does.Not.Contain("[internal]"));
    }

    [Test]
    public void Error_InImportedModule_ReportsThatFile()
    {
        var libPath = WriteScript("lib/failing.ts", "export function fail(): never {\n  throw new Error(\"from lib\");\n}\n");

        var module = LoadScript("caller", """
            import { fail } from "./lib/failing.ts";
            export function main() { fail(); }
            """);

        var ex = Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(Rt.GetExport(module, "main"), []))!;

        Assert.That(ex.File, Is.EqualTo(libPath));
        Assert.That(ex.Line, Is.EqualTo(2));
        Assert.That(ex.Traceback, Does.Contain("caller.ts"), "the caller's frame is in the traceback");
    }

    [Test]
    public void Error_NonErrorThrownValue_StillBecomesRuntimeException()
    {
        var ex = Assert.Throws<ScriptRuntimeException>(() => Call("throwsvalue", "export function main() { throw \"just a string\"; }", "main"))!;

        Assert.That(ex.Message, Does.Contain("just a string"));
    }

    [Test]
    public void Error_InfiniteRecursion_IsARecoverableRangeError()
    {
        var module = LoadScript("recurse", """
            export function boom(n: number): number { return boom(n + 1) + 1; }
            export function ok(): number { return 3; }
            """);

        var ex = Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(Rt.GetExport(module, "boom"), [ScriptValue.From(0d)]))!;

        Assert.That(ex.Message, Does.Contain("RangeError").And.Contain("call stack"));
        Assert.That(Rt.Invoke(Rt.GetExport(module, "ok"), []).AsNumber(), Is.EqualTo(3));
    }

    [Test]
    public void Error_ThrownByHostCall_PropagatesAsScriptRuntimeException()
    {
        Host.ActHandler = _ => throw new InvalidOperationException("host refused");

        var ex = Assert.Throws<ScriptRuntimeException>(() => Call("hostfail", """
            import { ksa } from "ksa";
            export function main() { ksa.vessel("x").ignite(); }
            """, "main"))!;

        Assert.That(ex.Message, Does.Contain("host refused"));
    }

    [Test]
    public void SyntaxError_ThrowsScriptLoadExceptionWithLine()
    {
        var path = WriteScript("syntax.ts", "const fine = 1;\n\nexport const broken = ;\nexport function main() {}\n");

        var ex = Assert.Throws<ScriptLoadException>(() => Rt.Load(new ScriptSource("syntax", path, File.ReadAllText(path), ScriptLanguages.TypeScript)))!;

        Assert.That(ex.File, Is.EqualTo(path));
        Assert.That(ex.Line, Is.EqualTo(3));
        Assert.That(ex.Column, Is.GreaterThan(0));
        Assert.That(ex.Message, Is.Not.Empty);
        Assert.That(ex.Snippet, Does.Contain("export const broken = ;"));
        Assert.That(Rt.LoadedScriptCount, Is.EqualTo(0));
    }

    [Test]
    public void SyntaxError_InImportedModule_ReportsThatFile()
    {
        var bad = WriteScript("lib/bad.ts", "export const ok = 1;\nexport const = 5;\n");

        var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("importsbad", "import \"./lib/bad.ts\";\nexport function main() {}"))!;

        Assert.That(ex.File, Is.EqualTo(bad));
        Assert.That(ex.Line, Is.EqualTo(2));
    }

    [Test]
    public void Load_ForeignLanguage_IsRejected()
    {
        var ex = Assert.Throws<ScriptLoadException>(() => Rt.Load(new ScriptSource("x", Path.Combine(ScriptsDir, "x.lua"), "return 1", ScriptLanguages.Lua)))!;

        Assert.That(ex.Message, Does.Contain("lua"));
    }

    [Test]
    public void Interrupt_StopsInfiniteLoop()
    {
        var module = LoadScript("spinner", """
            import { ksa } from "ksa";
            export function spin() { ksa.console.log("spinning"); for (;;) {} }
            export function ok(): number { return 42; }
            """);
        var spinning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Host.OnLog = log =>
        {
            if (log.Message == "spinning")
                spinning.TrySetResult();
        };

        using var finished = new ManualResetEventSlim(false);
        var watchdog = Task.Run(async () =>
        {
            // Interrupt as soon as the script reports it is spinning; keep trying (bounded) in case the first
            // request lands before V8 is executing the loop.
            await spinning.Task.WaitAsync(Deadline);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!finished.Wait(25) && clock.Elapsed < Deadline)
                Rt.Interrupt();
        });

        try
        {
            var ex = Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(Rt.GetExport(module, "spin"), []))!;
            Assert.That(ex.Message, Is.EqualTo("interrupted"));
        }
        finally
        {
            finished.Set();
            watchdog.Wait(Deadline);
        }

        Assert.That(Rt.Invoke(Rt.GetExport(module, "ok"), []).AsNumber(), Is.EqualTo(42), "the runtime is usable after an interrupt");
    }

    [Test]
    public void Interrupt_DuringAdvance_IsReportedNotThrown()
    {
        var module = LoadScript("spinlater", """
            import { ksa } from "ksa";
            export async function main() {
              await ksa.sleep(1);
              ksa.console.log("spinning");
              for (;;) {}
            }
            """);
        var spinning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Host.OnLog = log =>
        {
            if (log.Message == "spinning")
                spinning.TrySetResult();
        };
        Rt.Invoke(Rt.GetExport(module, "main"), []);
        Host.Waits.Single().Wait.Complete(ScriptValue.Null);

        using var finished = new ManualResetEventSlim(false);
        var watchdog = Task.Run(async () =>
        {
            await spinning.Task.WaitAsync(Deadline);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!finished.Wait(25) && clock.Elapsed < Deadline)
                Rt.Interrupt();
        });

        try
        {
            PumpUntil(() => Host.ReportedErrors.Count > 0, "the interrupted continuation to be reported");
        }
        finally
        {
            finished.Set();
            watchdog.Wait(Deadline);
        }

        Assert.That(Host.ReportedErrors.Single().Message, Is.EqualTo("interrupted"));
    }

    [Test]
    public void Interrupt_WhenIdle_IsANoOp()
    {
        var module = LoadScript("idle", "export function ok(): number { return 1; }");

        Assert.DoesNotThrow(() => Rt.Interrupt());
        Assert.That(Rt.Invoke(Rt.GetExport(module, "ok"), []).AsNumber(), Is.EqualTo(1), "a stale Interrupt() must not poison the next call");
    }

    [Test]
    public void Interrupt_AfterDispose_IsANoOp()
    {
        var runtime = NewRuntime();
        runtime.Dispose();
        runtime.Dispose();

        Assert.DoesNotThrow(() => runtime.Interrupt());
        Assert.That(runtime.Advance(TimeSpan.FromMilliseconds(1)), Is.False);
    }

    [Test]
    public void HeapLimit_ExceededAllocationFails()
    {
        Rt.Dispose();
        var runtime = NewRuntime(heapLimitBytes: 32L * 1024 * 1024);
        var module = LoadScript("hog", """
            export function hog(): number {
              const keep: object[] = [];
              for (let i = 0; i < 200_000_000; i++) keep.push({ a: i, b: "payload-" + i });
              return keep.length;
            }
            export function small(): number { return 7; }
            """, runtime);

        var ex = Assert.Throws<ScriptRuntimeException>(() => runtime.Invoke(runtime.GetExport(module, "hog"), []))!;

        Assert.That(ex.Message, Does.StartWith("heap limit exceeded ("));
        Assert.That(ex.Message, Does.Contain("> 33554432 bytes)"));
        // The violation interrupts the runaway call but not the isolate: the limit is re-armed and later calls work.
        Assert.That(runtime.Invoke(runtime.GetExport(module, "small"), []).AsNumber(), Is.EqualTo(7));
        Assert.That(runtime.Stats.HeapBytes, Is.GreaterThan(0));
    }

    [Test]
    public void HeapLimit_ExceededInAContinuation_IsReportedAndTheRuntimeRecovers()
    {
        Rt.Dispose();
        var runtime = NewRuntime(heapLimitBytes: 32L * 1024 * 1024);
        var module = LoadScript("asynchog", """
            import { ksa } from "ksa";
            export async function main() {
              await ksa.sleep(1);
              const keep: object[] = [];
              for (let i = 0; i < 200_000_000; i++) keep.push({ a: i, b: "payload-" + i });
            }
            export function small(): number { return 9; }
            """, runtime);

        runtime.Invoke(runtime.GetExport(module, "main"), []);
        Host.Waits.Single().Wait.Complete(ScriptValue.Null);
        PumpUntil(() => Host.ReportedErrors.Count > 0, "the heap violation to be reported", runtime);

        Assert.That(Host.ReportedErrors.Single().Message, Does.StartWith("heap limit exceeded ("));
        Assert.That(runtime.Invoke(runtime.GetExport(module, "small"), []).AsNumber(), Is.EqualTo(9));
    }
}
