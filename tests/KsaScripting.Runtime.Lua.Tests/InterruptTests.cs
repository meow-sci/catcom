using KsaScripting.Core.Runtime;

namespace KsaScripting.Runtime.Lua.Tests;

[TestFixture]
public sealed class InterruptTests
{
    private const string SpinScript = "return { spin = function() ksa_host.log(1, \"spinning\") while true do end end }";

    [Test]
    [CancelAfter(20000)]
    public void Interrupt_StopsInfiniteLoop_JitOn() => AssertInterruptStopsLoop(jitEnabled: true, SpinScript);

    [Test]
    [CancelAfter(20000)]
    public void Interrupt_StopsInfiniteLoop_JitOff() => AssertInterruptStopsLoop(jitEnabled: false, SpinScript);

    [Test]
    [CancelAfter(20000)]
    public void Interrupt_StopsHotArithmeticTrace_JitOn() => AssertInterruptStopsLoop(
        jitEnabled: true,
        "return { spin = function() ksa_host.log(1, \"spinning\") local n = 0 while true do n = n + 1 end end }");

    [Test]
    [CancelAfter(20000)]
    public void Interrupt_IsNotSwallowedByScriptPcall()
    {
        // A pcall that catches the hook's error must not stop the interrupt from unwinding.
        AssertInterruptStopsLoop(
            jitEnabled: true,
            "return { spin = function() ksa_host.log(1, \"spinning\") while true do pcall(function() while true do end end) end end }");
    }

    [Test]
    [CancelAfter(20000)]
    public void Interrupt_IsNotSwallowedByScriptXpcallOrCoroutine()
    {
        AssertInterruptStopsLoop(
            jitEnabled: false,
            """
            return { spin = function()
              ksa_host.log(1, "spinning")
              while true do
                xpcall(function() while true do end end, function(m) return m end)
                local co = coroutine.create(function() while true do end end)
                coroutine.resume(co)
              end
            end }
            """);
    }

    [Test]
    [CancelAfter(20000)]
    public void Interrupt_RequestedJustBeforeACall_StopsThatCall()
    {
        // The watchdog arms its deadline before it calls Invoke, so an expiry can land in between.
        using var fx = new LuaRuntimeFixture();
        var module = fx.Load("late", "return { spin = function() while true do end end, f = function() return 1 end }");

        fx.Runtime.Interrupt();
        var ex = Assert.Throws<ScriptRuntimeException>(() => fx.Runtime.Invoke(fx.Export(module, "spin"), []))!;

        Assert.That(ex.Message, Is.EqualTo("interrupted"));
        Assert.That(fx.Runtime.Invoke(fx.Export(module, "f"), []).AsNumber(), Is.EqualTo(1d), "the request is consumed by that call");
    }

    [Test]
    [CancelAfter(20000)]
    public void Interrupt_OfANestedCall_OnlyStopsTheInnerCall()
    {
        using var fx = new LuaRuntimeFixture();
        var module = fx.Load("nested", """
            return {
              outer = function() ksa_host.log(1, "trigger") return "outer-done" end,
              inner = function() ksa_host.log(1, "spinning") while true do end end,
            }
            """);
        var inner = fx.Export(module, "inner");
        var spinning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptRuntimeException? innerError = null;
        fx.Host.OnLog = log =>
        {
            if (log.Message == "spinning")
            {
                spinning.TrySetResult();
            }
            else if (log.Message == "trigger")
            {
                try
                {
                    fx.Runtime.Invoke(inner, []);
                }
                catch (ScriptRuntimeException ex)
                {
                    innerError = ex;
                }
            }
        };

        using var interrupter = new BackgroundInterrupter(fx.Runtime, spinning.Task);

        var result = fx.Runtime.Invoke(fx.Export(module, "outer"), []);
        interrupter.Finish();

        Assert.That(innerError?.Message, Is.EqualTo("interrupted"));
        Assert.That(result.AsString(), Is.EqualTo("outer-done"), "the outer call is not affected");
    }

    [Test]
    public void Interrupt_WithNothingRunning_DoesNotThrow()
    {
        using var fx = new LuaRuntimeFixture();

        Assert.DoesNotThrow(() => fx.Runtime.Interrupt());
    }

    [Test]
    [CancelAfter(60000)]
    public void Interrupt_Hammered_WhileTheJitCompilesAndRunsHotLoops_DoesNotCrashTheProcess()
    {
        // lua_sethook from another thread at an unbounded rate crashed LuaJIT's dispatch within one round (SIGSEGV
        // in the VM); the runtime rate-limits re-arming, so a caller that hammers Interrupt() is harmless.
        for (var round = 0; round < 40; round++)
        {
            using var fx = new LuaRuntimeFixture(jitEnabled: true);
            var module = fx.Load("hot", "return { f = function() local s = 0 for i = 1, 100000 do s = s + i end return s end }");
            var f = fx.Export(module, "f");
            var stop = 0;
            var hammer = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                    fx.Runtime.Interrupt();
            });
            hammer.Start();

            for (var i = 0; i < 20; i++)
            {
                try
                {
                    fx.Runtime.Invoke(f, []);
                }
                catch (ScriptRuntimeException ex)
                {
                    Assert.That(ex.Message, Is.EqualTo("interrupted"));
                }
            }

            Volatile.Write(ref stop, 1);
            hammer.Join();
        }
    }

    [Test]
    public void Interrupt_AfterDispose_IsNoOp()
    {
        var fx = new LuaRuntimeFixture();
        fx.Dispose();

        Assert.DoesNotThrow(() => fx.Runtime.Interrupt());
        Assert.DoesNotThrow(() => fx.Runtime.Dispose());
    }

    private static void AssertInterruptStopsLoop(bool jitEnabled, string script)
    {
        using var fx = new LuaRuntimeFixture(jitEnabled);
        Assert.That(fx.Runtime.Stats.JitEnabled, Is.EqualTo(jitEnabled));

        var spinning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Host.OnLog = log =>
        {
            if (log.Message == "spinning")
                spinning.TrySetResult();
        };

        var module = fx.Load("spin", script);
        var spin = fx.Export(module, "spin");

        // One Interrupt is enough; the interrupter only repeats to cover a lost wake-up and stops once the call returns.
        using var interrupter = new BackgroundInterrupter(fx.Runtime, spinning.Task);

        var ex = Assert.Throws<ScriptRuntimeException>(() => fx.Runtime.Invoke(spin, []))!;
        interrupter.Finish();

        Assert.That(ex.Message, Is.EqualTo("interrupted"));

        // The runtime is still usable and the interrupt does not leak into the next call.
        var ok = fx.Load("after", "return { f = function(x) return x + 1 end }");
        Assert.That(fx.Runtime.Invoke(fx.Export(ok, "f"), [KsaScripting.Core.Values.ScriptValue.From(1d)]).AsNumber(), Is.EqualTo(2d));
    }
}
