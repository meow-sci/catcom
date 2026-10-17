using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>
///     Host waits (ksa.sleep ...) inside coroutines the script created itself: the sandbox's
///     <c>coroutine.resume</c> / <c>coroutine.wrap</c> forward the wait to the runtime, which parks the whole invocation.
/// </summary>
[TestFixture]
public sealed class ScriptCoroutineWaitTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp() => _fx = new LuaRuntimeFixture();

    [TearDown]
    public void TearDown() => _fx.Dispose();

    private FakeHostFacade Host => _fx.Host;

    private LuaRuntime Rt => _fx.Runtime;

    private IEnumerable<string> Logged => Host.Logs.Select(l => l.Message);

    // Completes the wait the host recorded at <index> and runs one Advance (which resumes the parked invocation once).
    private void CompleteNext(ScriptValue result, int index)
    {
        Host.Waits[index].Wait.Complete(result);
        Rt.Advance(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void SleepInsideCoroutineWrap_ParksTheInvocation_AndReturnsIntoTheScript()
    {
        var module = _fx.Load("wrapped-sleep", """
            return { main = function()
              local value = coroutine.wrap(function()
                local slept = ksa.sleep(1)
                ksa.console.log("inner resumed with " .. tostring(slept))
                return 5
              end)()
              ksa.console.log("outer got " .. tostring(value))
            end }
            """);

        var immediate = Rt.Invoke(_fx.Export(module, "main"), []);

        Assert.That(immediate.IsNull, Is.True);
        Assert.That(Host.Waits, Has.Count.EqualTo(1));
        Assert.That(Host.Waits[0].Kind, Is.EqualTo("sleep"));
        Assert.That(Host.Waits[0].Arg["seconds"].AsNumber(), Is.EqualTo(1d));
        Assert.That(Logged, Is.Empty, "the invocation is parked, nothing ran past the wait");
        Assert.That(Rt.Advance(TimeSpan.FromMilliseconds(10)), Is.False, "nothing is complete yet");

        CompleteNext(ScriptValue.From(2.5), 0);

        Assert.That(Logged, Is.EqualTo(new[] { "inner resumed with 2.5", "outer got 5" }));
        Assert.That(Host.ReportedErrors, Is.Empty);
        Assert.That(Host.Logs.Select(l => l.Script), Is.All.EqualTo("wrapped-sleep"), "the continuation runs under EnterScript(owner)");
    }

    [Test]
    public void ResumeOfACreatedCoroutine_ForwardsTheWait_AndReturnsTrueAndTheResult()
    {
        var module = _fx.Load("created-sleep", """
            return { main = function()
              local co = coroutine.create(function(a, b)
                ksa.next_frame()
                return a + b
              end)
              local ok, sum = coroutine.resume(co, 2, 3)
              ksa.console.log(tostring(ok) .. ":" .. tostring(sum) .. ":" .. coroutine.status(co))
            end }
            """);

        Rt.Invoke(_fx.Export(module, "main"), []);
        Assert.That(Host.Waits.Select(w => w.Kind), Is.EqualTo(new[] { "nextFrame" }));
        Assert.That(Logged, Is.Empty);

        CompleteNext(ScriptValue.Null, 0);

        Assert.That(Logged, Is.EqualTo(new[] { "true:5:dead" }));
    }

    [Test]
    public void SeveralWaitsInOneCoroutine_ParkRepeatedly()
    {
        var module = _fx.Load("many-waits", """
            return { main = function()
              local result = coroutine.wrap(function()
                local a = ksa.sleep(1)
                local b = ksa.wait_until(function() return true end)
                local c = ksa.next_frame()
                return tostring(a) .. "/" .. tostring(b) .. "/" .. tostring(c)
              end)()
              ksa.console.log(result)
            end }
            """);

        Rt.Invoke(_fx.Export(module, "main"), []);
        Assert.That(Host.Waits.Select(w => w.Kind), Is.EqualTo(new[] { "sleep" }));

        CompleteNext(ScriptValue.From(1d), 0);
        Assert.That(Host.Waits.Select(w => w.Kind), Is.EqualTo(new[] { "sleep", "until" }));
        Assert.That(Logged, Is.Empty);

        CompleteNext(ScriptValue.True, 1);
        Assert.That(Host.Waits.Select(w => w.Kind), Is.EqualTo(new[] { "sleep", "until", "nextFrame" }));

        CompleteNext(ScriptValue.Null, 2);
        Assert.That(Logged, Is.EqualTo(new[] { "1/true/nil" }));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void GeneratorsThatYieldOrdinaryValues_WorkUnchanged()
    {
        var result = _fx.Call("""
            return { f = function()
              local seen = {}
              for n in coroutine.wrap(function() for i = 1, 3 do coroutine.yield(i * 10) end end) do
                seen[#seen + 1] = n
              end

              local co = coroutine.create(function(a) local b = coroutine.yield({ kind = "table", a }) return b end)
              local ok1, first = coroutine.resume(co, "x")
              local ok2, second = coroutine.resume(co, "y")
              local ok3, third = coroutine.resume(co)

              local multi = coroutine.wrap(function() coroutine.yield(1, 2) coroutine.yield() return "end" end)
              local a, b = multi()
              local none = select("#", multi())
              return {
                seen = seen,
                ok1 = ok1, kind = first.kind, first = first[1],
                ok2 = ok2, second = second, ok3 = ok3, third = third,
                a = a, b = b, none = none, last = multi(),
                status = coroutine.status(co),
              }
            end }
            """, "f").AsMap();

        Assert.That(result["seen"].AsList().Select(v => v.AsNumber()), Is.EqualTo(new[] { 10d, 20d, 30d }));
        Assert.That(result["ok1"].AsBool(), Is.True);
        Assert.That(result["kind"].AsString(), Is.EqualTo("table"));
        Assert.That(result["first"].AsString(), Is.EqualTo("x"));
        Assert.That(result["ok2"].AsBool(), Is.True);
        Assert.That(result["second"].AsString(), Is.EqualTo("y"));
        Assert.That(result["ok3"].AsBool(), Is.False, "resuming a dead coroutine fails");
        Assert.That(result["third"].AsString(), Is.EqualTo("cannot resume dead coroutine"));
        Assert.That((result["a"].AsNumber(), result["b"].AsNumber(), result["none"].AsNumber()), Is.EqualTo((1d, 2d, 0d)));
        Assert.That(result["last"].AsString(), Is.EqualTo("end"));
        Assert.That(result["status"].AsString(), Is.EqualTo("dead"));
        Assert.That(Host.Waits, Is.Empty);
    }

    [Test]
    public void GeneratorThatInterleavesValuesAndWaits_YieldsValuesToTheScript_AndWaitsToTheRuntime()
    {
        var module = _fx.Load("mixed-generator", """
            return { main = function()
              local gen = coroutine.wrap(function()
                coroutine.yield("first")
                local slept = ksa.sleep(2)
                coroutine.yield("second after " .. tostring(slept))
                return "third"
              end)
              ksa.console.log(gen())
              ksa.console.log(gen())
              ksa.console.log(gen())
            end }
            """);

        Rt.Invoke(_fx.Export(module, "main"), []);
        Assert.That(Logged, Is.EqualTo(new[] { "first" }));
        Assert.That(Host.Waits, Has.Count.EqualTo(1));

        CompleteNext(ScriptValue.From(7d), 0);

        Assert.That(Logged, Is.EqualTo(new[] { "first", "second after 7", "third" }));
    }

    [Test]
    public void WaitsInNestedCoroutines_ReachTheRuntime_FromBothLevels()
    {
        var module = _fx.Load("nested", """
            return { main = function()
              local outer = coroutine.wrap(function()
                ksa.console.log("outer start")
                local from_inner = coroutine.wrap(function()
                  ksa.console.log("inner start")
                  local a = ksa.sleep(1)
                  ksa.console.log("inner after sleep " .. tostring(a))
                  coroutine.yield("generated")
                  local b = ksa.next_step()
                  return "inner done " .. tostring(b)
                end)
                local g = from_inner()
                local after = ksa.next_frame()
                local done = from_inner()
                return g .. "|" .. done .. "|" .. tostring(after)
              end)
              ksa.console.log("result " .. outer())
            end }
            """);

        Rt.Invoke(_fx.Export(module, "main"), []);
        Assert.That(Host.Waits.Select(w => w.Kind), Is.EqualTo(new[] { "sleep" }));
        Assert.That(Logged, Is.EqualTo(new[] { "outer start", "inner start" }));

        CompleteNext(ScriptValue.From(3d), 0);
        Assert.That(Host.Waits.Select(w => w.Kind), Is.EqualTo(new[] { "sleep", "nextFrame" }), "after the inner generator yielded a value, the outer level waits");

        CompleteNext(ScriptValue.Null, 1);
        Assert.That(Host.Waits.Select(w => w.Kind), Is.EqualTo(new[] { "sleep", "nextFrame", "nextStep" }));

        CompleteNext(ScriptValue.From(9d), 2);
        Assert.That(Logged, Is.EqualTo(new[] { "outer start", "inner start", "inner after sleep 3", "result generated|inner done 9|nil" }));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void ThreeLevelsDeep_StillParksTheOutermostInvocation()
    {
        var module = _fx.Load("three-deep", """
            return { main = function()
              local v = coroutine.wrap(function()
                return coroutine.wrap(function()
                  return coroutine.wrap(function() return ksa.sleep(1) end)()
                end)()
              end)()
              ksa.console.log("value " .. tostring(v))
            end }
            """);

        Rt.Invoke(_fx.Export(module, "main"), []);
        Assert.That(Host.Waits, Has.Count.EqualTo(1));

        CompleteNext(ScriptValue.From(42d), 0);

        Assert.That(Logged, Is.EqualTo(new[] { "value 42" }));
    }

    [Test]
    public void FailedWait_InsideWrappedCoroutine_RaisesToTheScript_WhichCanCatchIt()
    {
        var module = _fx.Load("wrapped-failure", """
            return { main = function()
              local ok, err = pcall(coroutine.wrap(function() ksa.sleep(1) return "unreachable" end))
              ksa.console.log(tostring(ok) .. ":" .. tostring(err))
              local co = coroutine.create(function() ksa.sleep(1) end)
              local ok2, err2 = coroutine.resume(co)
              ksa.console.log(tostring(ok2) .. ":" .. tostring(err2) .. ":" .. coroutine.status(co))
            end }
            """);
        Rt.Invoke(_fx.Export(module, "main"), []);

        Host.Waits[0].Wait.Fail(new InvalidOperationException("first exploded"));
        Rt.Advance(TimeSpan.FromSeconds(5));
        Assert.That(Logged.Single(), Does.StartWith("false:").And.Contain("first exploded"));

        Host.Waits[1].Wait.Cancel();
        Rt.Advance(TimeSpan.FromSeconds(5));

        Assert.That(Logged.Last(), Does.StartWith("false:").And.Contain("wait cancelled").And.EndWith(":dead"));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void FailedWait_InsideWrappedCoroutine_IsReportedWithTheScriptLocation_WhenUncaught()
    {
        var source = _fx.Write("wrapped-uncaught", "return { main = function()\n  local f = coroutine.wrap(function() ksa.sleep(1) end)\n  f()\nend }");
        var module = Rt.Load(source);
        Rt.Invoke(_fx.Export(module, "main"), []);

        Host.Waits[0].Wait.Fail(new InvalidOperationException("late failure"));
        Rt.Advance(TimeSpan.FromSeconds(5));

        var error = Host.ReportedErrors.Single();
        Assert.That(error.Message, Does.Contain("late failure"));
        Assert.That(error.File, Is.EqualTo(source.Path));
        Assert.That(error.Line, Is.EqualTo(3), "the line that called the wrapped function");
    }

    [Test]
    public void ErrorsInWrappedCoroutines_PropagateLikeTheNativeWrap()
    {
        var result = _fx.Call("""
            return { f = function()
              local _, located = pcall(function()
                local w = coroutine.wrap(function() error("boom") end)
                w()
              end)
              local _, plain = pcall(coroutine.wrap(function() error("bare", 0) end))
              local _, object = pcall(coroutine.wrap(function() error({ code = 7 }) end))
              local dead = coroutine.wrap(function() return 1 end)
              dead()
              local _, deadMessage = pcall(dead)
              return { located = located, plain = plain, code = object.code, dead = deadMessage }
            end }
            """, "f").AsMap();

        Assert.That(result["located"].AsString(), Does.Match(@"^scripts/t[0-9a-f]+\.lua:\d+: scripts/t[0-9a-f]+\.lua:\d+: boom$"), "the caller's location is prepended once");
        Assert.That(result["plain"].AsString(), Is.EqualTo("bare"));
        Assert.That(result["code"].AsNumber(), Is.EqualTo(7d));
        Assert.That(result["dead"].AsString(), Is.EqualTo("cannot resume dead coroutine"));
    }

    [Test]
    public void AWaitThatCannotBeForwarded_IsReturnedToTheScript_AsBefore()
    {
        // A table.sort comparator is a C boundary: the wrapper cannot yield across it, so the wait table is handed back.
        var module = _fx.Load("c-boundary", """
            return { main = function()
              local kinds = {}
              table.sort({ 2, 1 }, function(a, b)
                local r = coroutine.wrap(function() ksa.sleep(1) end)()
                kinds[#kinds + 1] = type(r)
                return a < b
              end)
              ksa.console.log(table.concat(kinds, ","))
            end }
            """);

        var result = Rt.Invoke(_fx.Export(module, "main"), []);

        Assert.That(result.IsNull, Is.True);
        Assert.That(Logged.Single(), Does.StartWith("table"));
        Assert.That(Rt.Advance(TimeSpan.FromMilliseconds(10)), Is.False, "the unclaimed wait did not park anything");
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void TopLevelCodeThatWaitsInsideACoroutine_IsRefusedLikeAnyTopLevelWait()
    {
        var ex = Assert.Throws<ScriptLoadException>(() => _fx.Load("top-level-wait", "coroutine.wrap(function() ksa.sleep(1) end)()\nreturn {}"))!;

        Assert.That(ex.Message, Does.Contain("top-level code cannot wait"));
    }

    [Test]
    public void ParkedWrappedCoroutines_AreDroppedWithTheirModule()
    {
        var module = _fx.Load("doomed-wrap", "return { main = function() coroutine.wrap(function() ksa.sleep(1) ksa.console.log('never') end)() end }");
        Rt.Invoke(_fx.Export(module, "main"), []);
        Host.Waits[0].Wait.Complete(ScriptValue.Null);

        Rt.Unload(module);
        Rt.Advance(TimeSpan.FromSeconds(1));

        Assert.That(Logged, Is.Empty);
    }

    [Test]
    [CancelAfter(20000)]
    public void InterruptingASpinningCoroutine_StillUnwindsThroughTheWrappers()
    {
        var module = _fx.Load("spin-wrapped", """
            return { main = function()
              ksa.next_frame()
              ksa_host.log(1, "spinning")
              pcall(coroutine.wrap(function() while true do end end))
            end }
            """);
        Rt.Invoke(_fx.Export(module, "main"), []);
        var spinning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Host.OnLog = log =>
        {
            if (log.Message == "spinning")
                spinning.TrySetResult();
        };
        Host.Waits[0].Wait.Complete(ScriptValue.Null);

        using var interrupter = new BackgroundInterrupter(Rt, spinning.Task);
        Rt.Advance(TimeSpan.FromSeconds(30));
        interrupter.Finish();

        Assert.That(Host.ReportedErrors.Single().Message, Is.EqualTo("interrupted"));
    }
}
