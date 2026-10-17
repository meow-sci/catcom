using Catcom.Core.Hosting;
using Catcom.Core.Runtime;
using Catcom.Core.Tests.Support;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Runtime;

[TestFixture]
public sealed class NullRuntimeTests
{
    private static ScriptSource Source(string name, string code = "-- code") => new(name, "/scripts/" + name, code, ScriptLanguages.Lua);

    [Test]
    public void Identity_DefaultsAndOverrides()
    {
        using var defaults = new NullRuntime();
        using var custom = new NullRuntime("v8", ScriptLanguages.TypeScript);

        Assert.Multiple(() =>
        {
            Assert.That(defaults.Id, Is.EqualTo("null"));
            Assert.That(defaults.Language, Is.EqualTo(ScriptLanguages.Lua));
            Assert.That(defaults.IsAvailable, Is.True);
            Assert.That(defaults.UnavailableReason, Is.Null);
            Assert.That(custom.Id, Is.EqualTo("v8"));
            Assert.That(custom.Language, Is.EqualTo(ScriptLanguages.TypeScript));
        });
    }

    [Test]
    public void MarkUnavailable_FlipsAvailabilityAndRecordsTheReason()
    {
        using var runtime = new NullRuntime();

        runtime.MarkUnavailable("native library missing");

        Assert.Multiple(() =>
        {
            Assert.That(runtime.IsAvailable, Is.False);
            Assert.That(runtime.UnavailableReason, Is.EqualTo("native library missing"));
        });
    }

    [Test]
    public void Load_OfAnUnregisteredScript_YieldsAModuleWithNoExports()
    {
        using var rig = new TestRig();

        var module = rig.Runtime.Load(Source("plain.lua"));

        Assert.Multiple(() =>
        {
            Assert.That(rig.Runtime.Exports(module), Is.Empty);
            Assert.That(rig.Runtime.Loaded, Is.EqualTo(new[] { "plain.lua" }));
            Assert.Throws<KeyNotFoundException>(() => rig.Runtime.GetExport(module, "main"));
        });
    }

    [Test]
    public void Load_RunsOnLoad_AndExposesTheRegisteredExports()
    {
        using var rig = new TestRig();
        var loads = 0;
        rig.Runtime.Register(
            "a.lua",
            new Dictionary<string, NullFunction>
            {
                ["main"] = (_, _) => ScriptValue.From("ran main"),
                ["helper"] = (_, args) => ScriptValue.From(args.Length),
            },
            (_, _) => loads++);

        var module = rig.Runtime.Load(Source("a.lua"));
        var main = rig.Runtime.GetExport(module, "main");
        var helper = rig.Runtime.GetExport(module, "helper");

        Assert.Multiple(() =>
        {
            Assert.That(loads, Is.EqualTo(1));
            Assert.That(rig.Runtime.Exports(module), Is.EquivalentTo(new[] { "main", "helper" }));
            Assert.That(main.Name, Is.EqualTo("main"));
            Assert.That(rig.Runtime.Invoke(main, []).AsString(), Is.EqualTo("ran main"));
            Assert.That(rig.Runtime.Invoke(helper, [ScriptValue.Null, ScriptValue.Null]).AsNumber(), Is.EqualTo(2));
            Assert.That(rig.Runtime.Stats.CallsTotal, Is.EqualTo(2));
        });
    }

    [Test]
    public void Load_ASyntaxErrorMarker_ThrowsAScriptLoadException_WithTheFirstLine()
    {
        using var rig = new TestRig();

        var ex = Assert.Throws<ScriptLoadException>(() => rig.Runtime.Load(Source("bad.lua", "local x = @syntax-error\nmore")))!;

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Is.EqualTo("syntax error"));
            Assert.That(ex.File, Is.EqualTo("/scripts/bad.lua"));
            Assert.That(ex.Line, Is.EqualTo(1));
            Assert.That(ex.Column, Is.EqualTo(1));
            Assert.That(ex.Snippet, Is.EqualTo("local x = @syntax-error"));
            Assert.That(rig.Runtime.Loaded, Is.Empty);
        });
    }

    [Test]
    public void Load_AnOnLoadThatThrows_BecomesAScriptLoadException_AndLeavesNoModule()
    {
        using var rig = new TestRig();
        rig.Runtime.Register("a.lua", new Dictionary<string, NullFunction>(), (_, runtime) =>
        {
            runtime.CreateCallable((_, _) => ScriptValue.Null, "leaked");
            throw new InvalidOperationException("top-level exploded");
        });

        var ex = Assert.Throws<ScriptLoadException>(() => rig.Runtime.Load(Source("a.lua")))!;

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Is.EqualTo("top-level exploded"));
            Assert.That(rig.Runtime.Loaded, Is.Empty);
        });
    }

    [Test]
    public void Invoke_PassesScriptRuntimeExceptionsThrough_AndWrapsEverythingElse()
    {
        using var rig = new TestRig();
        var passthrough = new ScriptRuntimeException("original", "trace");
        var throwsRuntime = rig.Runtime.CreateCallable((_, _) => throw passthrough);
        var throwsOther = rig.Runtime.CreateCallable((_, _) => throw new InvalidOperationException("plain"));

        var first = Assert.Throws<ScriptRuntimeException>(() => rig.Runtime.Invoke(throwsRuntime, []))!;
        var second = Assert.Throws<ScriptRuntimeException>(() => rig.Runtime.Invoke(throwsOther, []))!;

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.SameAs(passthrough));
            Assert.That(second.Message, Is.EqualTo("plain"));
            Assert.That(second.InnerException, Is.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void Invoke_OfAnUnknownOrDisposedCallable_ThrowsAScriptRuntimeException()
    {
        using var rig = new TestRig();
        var disposed = rig.Runtime.CreateCallable((_, _) => ScriptValue.Null);
        disposed.Dispose();
        var foreign = ScriptCallable.Create("other", 1, "f", _ => { });

        Assert.Multiple(() =>
        {
            Assert.Throws<ScriptRuntimeException>(() => rig.Runtime.Invoke(disposed, []));
            Assert.Throws<ScriptRuntimeException>(() => rig.Runtime.Invoke(foreign, []));
        });
    }

    [Test]
    public void Invoke_BeforeInitialize_Throws()
    {
        using var runtime = new NullRuntime();
        var fn = runtime.CreateCallable((_, _) => ScriptValue.Null);

        Assert.Throws<InvalidOperationException>(() => runtime.Invoke(fn, []));
    }

    [Test]
    public void Initialize_IsIdempotent()
    {
        using var rig = new TestRig();

        Assert.DoesNotThrow(() => rig.Runtime.Initialize(rig.Host, new RuntimeOptions("", "", 0, false, null)));
    }

    // ---------------------------------------------------------------- interrupt

    [Test]
    public void CheckInterrupt_ThrowsOnceInterruptWasRequested_AndTheNextInvokeStartsClean()
    {
        using var rig = new TestRig();
        var fn = rig.Runtime.CreateCallable((_, _) =>
        {
            rig.Runtime.CheckInterrupt();
            return ScriptValue.From("fine");
        });

        Assert.DoesNotThrow(rig.Runtime.CheckInterrupt);
        rig.Runtime.Interrupt();
        var ex = Assert.Throws<ScriptRuntimeException>(rig.Runtime.CheckInterrupt)!;

        Assert.Multiple(() =>
        {
            Assert.That(ex.Message, Is.EqualTo("interrupted"));
            Assert.That(rig.Runtime.Invoke(fn, []).AsString(), Is.EqualTo("fine"), "the flag is cleared at Invoke entry");
        });
    }

    [Test]
    public void Interrupt_FromAnotherThread_StopsALoopingFunction()
    {
        using var rig = new TestRig();
        var started = new ManualResetEventSlim(false);
        var fn = rig.Runtime.CreateCallable((_, _) =>
        {
            started.Set();
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(10))
                rig.Runtime.CheckInterrupt();
            return ScriptValue.Null;
        });

        var interrupter = Task.Run(() =>
        {
            started.Wait(TimeSpan.FromSeconds(10));
            rig.Runtime.Interrupt();
        });

        var ex = Assert.Throws<ScriptRuntimeException>(() => rig.Runtime.Invoke(fn, []))!;
        interrupter.Wait(TimeSpan.FromSeconds(10));

        Assert.That(ex.Message, Is.EqualTo("interrupted"));
    }

    // ---------------------------------------------------------------- await

    [Test]
    public void Await_SuspendsTheFunction_AndAdvanceRunsTheContinuationOnceTheWaitCompleted()
    {
        using var rig = new TestRig();
        var results = new List<ScriptValue>();
        var wait = new HostWait(1, "custom");
        var fn = rig.Runtime.CreateCallable((_, _) => rig.Runtime.Await(wait, result =>
        {
            results.Add(result);
            return ScriptValue.Null;
        }));

        var returned = rig.Runtime.Invoke(fn, []);

        Assert.Multiple(() =>
        {
            Assert.That(returned.IsNull, Is.True, "the caller gets Null; the function is suspended");
            Assert.That(rig.Runtime.PendingContinuations, Is.EqualTo(1));
            Assert.That(rig.Runtime.Advance(TimeSpan.FromSeconds(1)), Is.False, "waiting on a pending wait is not work");
            Assert.That(results, Is.Empty);
        });

        wait.Complete(ScriptValue.From(42));
        var more = rig.Runtime.Advance(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(more, Is.False);
            Assert.That(results.Select(r => r.AsNumber()), Is.EqualTo(new double[] { 42 }));
            Assert.That(rig.Runtime.PendingContinuations, Is.Zero);
        });
    }

    [Test]
    public void Await_Chains_ContinuationsMayAwaitAgain()
    {
        using var rig = new TestRig();
        var log = new List<string>();
        var first = new HostWait(1, "a");
        var second = new HostWait(2, "b");
        var fn = rig.Runtime.CreateCallable((_, _) => rig.Runtime.Await(first, a =>
        {
            log.Add("a=" + a.AsNumber());
            return rig.Runtime.Await(second, b =>
            {
                log.Add("b=" + b.AsNumber());
                return ScriptValue.Null;
            });
        }));

        rig.Runtime.Invoke(fn, []);
        first.Complete(ScriptValue.From(1));
        rig.Runtime.Advance(TimeSpan.FromSeconds(1));
        Assert.Multiple(() =>
        {
            Assert.That(log, Is.EqualTo(new[] { "a=1" }));
            Assert.That(rig.Runtime.PendingContinuations, Is.EqualTo(1), "chained and still waiting");
        });

        second.Complete(ScriptValue.From(2));
        rig.Runtime.Advance(TimeSpan.FromSeconds(1));
        Assert.That(log, Is.EqualTo(new[] { "a=1", "b=2" }));
    }

    [Test]
    public void Await_AChainOnAnAlreadyCompletedWait_FinishesInOneAdvance()
    {
        using var rig = new TestRig();
        var log = new List<string>();
        var done = new HostWait(1, "a");
        done.Complete(ScriptValue.Null);
        var alsoDone = new HostWait(2, "b");
        alsoDone.Complete(ScriptValue.Null);
        var fn = rig.Runtime.CreateCallable((_, _) => rig.Runtime.Await(done, _ =>
        {
            log.Add("one");
            return rig.Runtime.Await(alsoDone, _ =>
            {
                log.Add("two");
                return ScriptValue.Null;
            });
        }));

        rig.Runtime.Invoke(fn, []);
        rig.Runtime.Advance(TimeSpan.FromSeconds(1));

        Assert.That(log, Is.EqualTo(new[] { "one", "two" }));
    }

    [Test]
    public void Advance_RunsReadyContinuationsInFifoOrder_UnderTheScriptThatSuspended()
    {
        using var rig = new TestRig();
        var log = new List<string>();
        var waits = new[] { new HostWait(1, "a"), new HostWait(2, "b"), new HostWait(3, "c") };
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            var fn = rig.Runtime.CreateCallable((_, _) => rig.Runtime.Await(waits[index], _ =>
            {
                log.Add($"{index}@{rig.Host.CurrentScript}");
                return ScriptValue.Null;
            }));
            rig.Scheduler.InvokeGuarded("script" + index, fn, []);
        }

        waits[2].Complete(ScriptValue.Null);
        waits[0].Complete(ScriptValue.Null);
        waits[1].Complete(ScriptValue.Null);
        rig.Runtime.Advance(TimeSpan.FromSeconds(1));

        Assert.That(log, Is.EqualTo(new[] { "0@script0", "1@script1", "2@script2" }), "suspension order, not completion order");
    }

    [Test]
    public void Advance_ReportsTrue_WhileReadyContinuationsRemainBecauseTheBudgetRanOut()
    {
        using var rig = new TestRig();
        var wait = new HostWait(1, "a");
        wait.Complete(ScriptValue.Null);
        rig.Runtime.Invoke(rig.Runtime.CreateCallable((_, _) => rig.Runtime.Await(wait, _ => ScriptValue.Null)), []);

        Assert.That(rig.Runtime.Advance(TimeSpan.Zero), Is.True);
        Assert.That(rig.Runtime.Advance(TimeSpan.FromSeconds(1)), Is.False);
    }

    [Test]
    public void AFailedOrCancelledWait_ReportsAnErrorUnderTheSuspendedScript()
    {
        using var rig = new TestRig();
        var failing = new HostWait(1, "a");
        var cancelled = new HostWait(2, "b");
        foreach (var (name, wait) in new[] { ("fails.lua", failing), ("cancelled.lua", cancelled) })
        {
            var captured = wait;
            rig.Scheduler.InvokeGuarded(name, rig.Runtime.CreateCallable((_, _) =>
                rig.Runtime.Await(captured, _ => throw new InvalidOperationException("must not run"))), []);
        }

        failing.Fail(new InvalidOperationException("act failed"));
        cancelled.Cancel();
        rig.Runtime.Advance(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(rig.Errors, Has.Count.EqualTo(2));
            Assert.That(rig.Errors[0].Script, Is.EqualTo("fails.lua"));
            Assert.That(rig.Errors[0].Error.Message, Is.EqualTo("wait failed: act failed"));
            Assert.That(rig.Errors[1].Script, Is.EqualTo("cancelled.lua"));
            Assert.That(rig.Errors[1].Error.Message, Is.EqualTo("wait failed: cancelled"));
            Assert.That(rig.Runtime.PendingContinuations, Is.Zero);
        });
    }

    [Test]
    public void AContinuationThatThrows_IsReportedThroughTheHost()
    {
        using var rig = new TestRig();
        var wait = new HostWait(1, "a");
        rig.Scheduler.InvokeGuarded("a.lua", rig.Runtime.CreateCallable((_, _) =>
            rig.Runtime.Await(wait, _ => throw new InvalidOperationException("after the await"))), []);

        wait.Complete(ScriptValue.Null);
        rig.Runtime.Advance(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(rig.Errors, Has.Count.EqualTo(1));
            Assert.That(rig.Errors[0].Script, Is.EqualTo("a.lua"));
            Assert.That(rig.Errors[0].Error.Message, Is.EqualTo("after the await"));
        });
    }

    // ---------------------------------------------------------------- unload

    [Test]
    public void Unload_DropsTheModulesContinuations_DisposesItsCallables_AndKeepsOtherModules()
    {
        using var rig = new TestRig();
        var ran = new List<string>();
        var waitA = new HostWait(1, "a");
        var waitB = new HostWait(2, "b");
        rig.Runtime.Register("a.lua", new Dictionary<string, NullFunction>
        {
            ["main"] = (_, _) => rig.Runtime.Await(waitA, _ => { ran.Add("a"); return ScriptValue.Null; }),
        });
        rig.Runtime.Register("b.lua", new Dictionary<string, NullFunction>
        {
            ["main"] = (_, _) => rig.Runtime.Await(waitB, _ => { ran.Add("b"); return ScriptValue.Null; }),
        });
        var moduleA = rig.Runtime.Load(Source("a.lua"));
        var moduleB = rig.Runtime.Load(Source("b.lua"));
        var mainA = rig.Runtime.GetExport(moduleA, "main");
        var mainB = rig.Runtime.GetExport(moduleB, "main");
        rig.Runtime.Invoke(mainA, []);
        rig.Runtime.Invoke(mainB, []);
        Assert.That(rig.Runtime.PendingContinuations, Is.EqualTo(2));

        rig.Runtime.Unload(moduleA);
        waitA.Complete(ScriptValue.Null);
        waitB.Complete(ScriptValue.Null);
        rig.Runtime.Advance(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(ran, Is.EqualTo(new[] { "b" }));
            Assert.That(mainA.IsDisposed, Is.True);
            Assert.That(mainB.IsDisposed, Is.False);
            Assert.That(rig.Runtime.Loaded, Is.EqualTo(new[] { "b.lua" }));
            Assert.Throws<KeyNotFoundException>(() => rig.Runtime.Exports(moduleA));
        });
    }

    [Test]
    public void Unload_DisposesTheCallablesCreatedByTheModulesTopLevelCode()
    {
        using var rig = new TestRig();
        ScriptCallable? created = null;
        rig.Runtime.Register("a.lua", new Dictionary<string, NullFunction>(), (_, runtime) =>
            created = runtime.CreateCallable((_, _) => ScriptValue.Null, "hook"));
        var module = rig.Runtime.Load(Source("a.lua"));
        Assert.That(created!.IsDisposed, Is.False);

        rig.Runtime.Unload(module);

        Assert.That(created.IsDisposed, Is.True);
    }

    [Test]
    public void ReloadingTheSameScript_GivesIndependentModules()
    {
        using var rig = new TestRig();
        rig.Runtime.Register("a.lua", new Dictionary<string, NullFunction> { ["main"] = (_, _) => ScriptValue.From("v1") });
        var first = rig.Runtime.Load(Source("a.lua"));
        rig.Runtime.Register("a.lua", new Dictionary<string, NullFunction> { ["main"] = (_, _) => ScriptValue.From("v2") });
        var second = rig.Runtime.Load(Source("a.lua"));

        rig.Runtime.Unload(first);

        var main = rig.Runtime.GetExport(second, "main");
        Assert.That(rig.Runtime.Invoke(main, []).AsString(), Is.EqualTo("v2"));
    }

    [Test]
    public void Dispose_MakesTheRuntimeUnavailable()
    {
        var runtime = new NullRuntime();

        runtime.Dispose();

        Assert.That(runtime.IsAvailable, Is.False);
    }
}
