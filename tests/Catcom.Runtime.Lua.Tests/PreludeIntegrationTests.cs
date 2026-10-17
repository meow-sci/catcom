using Catcom.Core.Commands;
using Catcom.Core.Hosting;
using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>
///     The real script prelude (<c>src/Catcom.Core/Api/Preludes/ksa.lua</c>, embedded in the runtime) running on the
///     real runtime against the recording host: what a script's calls turn into on <see cref="IHostFacade"/>, and what comes back.
/// </summary>
[TestFixture]
public sealed class PreludeIntegrationTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new LuaRuntimeFixture();
        // The prelude's ksa.vessel(id) answers nil unless Read("vessel:<id>", "id") is non-null.
        Host.ReadHandler = (handle, path) =>
            handle.StartsWith("vessel:", StringComparison.Ordinal) && path == "id" && handle != "vessel:ghost"
                ? ScriptValue.From(handle["vessel:".Length..])
                : ScriptValue.Null;
    }

    [TearDown]
    public void TearDown() => _fx.Dispose();

    private FakeHostFacade Host => _fx.Host;

    private LuaRuntime Rt => _fx.Runtime;

    private IEnumerable<string> Logged => Host.Logs.Select(l => l.Message);

    private static void AssertOptions(SubscriptionCall call, string kind, string key, BindingOptions options)
    {
        Assert.That((call.Kind, call.Key), Is.EqualTo((kind, key)));
        Assert.That(call.Options, Is.EqualTo(options));
    }

    [Test]
    public void On_WithSyncAndVessel_SubscribesWithBindingOptions()
    {
        _fx.Load("on-options", """
            local v = ksa.vessel("v1")
            ksa.on("stage", function() end, { sync = true, vessel = "v1" })
            ksa.on("dock", function() end, { vessel = v })
            ksa.on("impact", function() end)
            ksa.on("warp.change", function() end, { sync = false })
            return {}
            """);

        Assert.That(Host.Subscriptions, Has.Count.EqualTo(4));
        AssertOptions(Host.Subscriptions[0], "event", "stage", new BindingOptions(Sync: true, Vessel: "v1"));
        AssertOptions(Host.Subscriptions[1], "event", "dock", new BindingOptions(Vessel: "v1"));
        AssertOptions(Host.Subscriptions[2], "event", "impact", new BindingOptions());
        AssertOptions(Host.Subscriptions[3], "event", "warp.change", new BindingOptions());
        Assert.That(Host.Subscriptions.Select(s => s.Fn.RuntimeId), Is.All.EqualTo("lua"));
    }

    [Test]
    public void On_ReturnsAHandle_WhoseUnsubscribePassesTheBindingId()
    {
        var module = _fx.Load("unsub", """
            local handle = ksa.on("stage", function() end)
            return { main = function() handle.unsubscribe() return handle.id end }
            """);

        var id = Rt.Invoke(_fx.Export(module, "main"), []).AsString();

        Assert.That(Host.Unsubscribed, Is.EqualTo(new[] { Host.Subscriptions.Single().Id }));
        Assert.That(Guid.Parse(id), Is.EqualTo(Host.Subscriptions.Single().Id));
    }

    [Test]
    public void Every_MapsTheIntervalAndClock_ToCamelCasedBindingOptions()
    {
        _fx.Load("every", """
            ksa.every(2.5, function() end, { clock = "wall" })
            ksa.every("0.25", function() end)
            ksa.every("frame", function() end)
            ksa.every("step", function() end, { clock = "render" })
            return {}
            """);

        Assert.That(Host.Subscriptions, Has.Count.EqualTo(4));
        AssertOptions(Host.Subscriptions[0], "tick", "every", new BindingOptions(Clock: "wall", IntervalSeconds: 2.5));
        AssertOptions(Host.Subscriptions[1], "tick", "every", new BindingOptions(Clock: "ut", IntervalSeconds: 0.25));
        AssertOptions(Host.Subscriptions[2], "tick", "frame", new BindingOptions());
        AssertOptions(Host.Subscriptions[3], "tick", "step", new BindingOptions(Clock: "render"));
    }

    [Test]
    public void Every_RejectsAnInvalidKind_WithoutTouchingTheHost()
    {
        var result = _fx.Call("""
            return { f = function()
              local _, nonsense = pcall(ksa.every, "sometimes", function() end)
              local _, negative = pcall(ksa.every, -1, function() end)
              local _, notFunction = pcall(ksa.on, "stage", "oops")
              return { nonsense, negative, notFunction }
            end }
            """, "f").AsList();

        Assert.That(result[0].AsString(), Does.Contain("ksa.every: kind must be"));
        Assert.That(result[1].AsString(), Does.Contain("ksa.every: kind must be"));
        Assert.That(result[2].AsString(), Does.Contain("must be a function"));
        Assert.That(Host.Subscriptions, Is.Empty);
    }

    [Test]
    public void Service_And_Command_Subscribe_WithTheirKinds()
    {
        var source = _fx.Write("kinds", """
            ksa.service(function(ctx)
              ksa.console.log("service " .. ctx.kind .. " " .. tostring(ctx.signal.cancelled))
            end)
            ksa.command("greet", function(arg) ksa.console.log("hello " .. tostring(arg)) end, "says hello")
            return {}
            """);
        Rt.Load(source);

        Assert.That(Host.Subscriptions, Has.Count.EqualTo(2));
        AssertOptions(Host.Subscriptions[0], "service", string.Empty, new BindingOptions());
        AssertOptions(Host.Subscriptions[1], "command", "greet", new BindingOptions());

        // A command receives the argument text as one plain String; a service receives a context.
        Rt.Invoke(Host.Subscriptions[1].Fn, [ScriptValue.From("world")]);
        Rt.Invoke(Host.Subscriptions[0].Fn, [ContextMaps.Create("service", source, ("signal", ScriptValue.From("kinds")))]);

        Assert.That(Logged, Is.EqualTo(new[] { "hello world", "service service false" }));
    }

    [Test]
    public void SolverAction_ReturnsAPendingWait_ParksTheCoroutine_AndResumesWithTheResultMap()
    {
        var hostWait = new HostWait(900, "act");
        Host.ActHandler = act => act.Action == "vessel.attitude_mode" ? new ActResult(null, hostWait) : new ActResult(CommandResult.Ok, null);
        var module = _fx.Load("solver", """
            return { main = function()
              local v = ksa.vessel("v1")
              ksa.console.log("before")
              local r = v:set_attitude_mode("prograde")
              ksa.console.log("after ok=" .. tostring(r.ok) .. " outcome=" .. tostring(r.outcome) .. " message=" .. tostring(r.message))
            end }
            """);

        var immediate = Rt.Invoke(_fx.Export(module, "main"), []);

        Assert.That(immediate.IsNull, Is.True, "the invocation is parked on the solver wait");
        Assert.That(Host.Acts.Single(), Is.EqualTo(new ActCall("vessel.attitude_mode", "v1", -1, 0, null, "prograde", null)));
        Assert.That(Logged, Is.EqualTo(new[] { "before" }));
        Assert.That(Rt.Advance(TimeSpan.FromMilliseconds(10)), Is.False, "the wait is still open");

        hostWait.Complete(ScriptValue.Map(new Dictionary<string, ScriptValue>
        {
            ["ok"] = ScriptValue.False,
            ["outcome"] = ScriptValue.From("Rejected"),
            ["message"] = ScriptValue.From("no solver"),
        }));
        Rt.Advance(TimeSpan.FromSeconds(5));

        Assert.That(Logged, Is.EqualTo(new[] { "before", "after ok=false outcome=Rejected message=no solver" }));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void SolverActions_SendTheirArguments_AndFrameActionsReturnTheirResultImmediately()
    {
        Host.ActHandler = act => act.Action.StartsWith("vessel.attitude", StringComparison.Ordinal)
            ? new ActResult(null, new HostWait(1000 + Host.Acts.Count, "act"))
            : new ActResult(CommandResult.Invalid("bad value"), null);
        var module = _fx.Load("actions", """
            return { main = function()
              local v = ksa.vessel("v1")
              local frame = v:set_throttle(0.5)
              ksa.console.log("frame ok=" .. tostring(frame.ok) .. " outcome=" .. frame.outcome .. " message=" .. frame.message)
              v:set_attitude_target({ x = 0, y = 0.5, z = 0, w = 1 })
              v:set_attitude_frame("Cci")
            end }
            """);

        Rt.Invoke(_fx.Export(module, "main"), []);

        Assert.That(Logged, Is.EqualTo(new[] { "frame ok=false outcome=Invalid message=bad value" }));
        Assert.That(Host.Acts.Select(a => a.Action), Is.EqualTo(new[] { "vessel.throttle", "vessel.attitude_target" }), "the second solver action has not run: the first one parked the invocation");
        Assert.That(Host.Acts[0], Is.EqualTo(new ActCall("vessel.throttle", "v1", -1, 0.5, null, null, null)));
        Assert.That(Host.Acts[1].Values, Is.EqualTo(new[] { 0d, 0.5, 0d, 1d }));
        Assert.That(Host.Acts[1].VesselId, Is.EqualTo("v1"));
    }

    [Test]
    public void WaitUntil_SendsThePredicateTimeoutAndClock_AndReturnsTheHostResult()
    {
        var module = _fx.Load("wait-until", """
            return { main = function()
              local flag = false
              local a = ksa.wait_until(function() return flag end, { timeout_seconds = 5, clock = "wall" })
              ksa.console.log("a=" .. tostring(a))
              local b = ksa.wait_until(function() return true end)
              ksa.console.log("b=" .. tostring(b))
            end }
            """);

        Rt.Invoke(_fx.Export(module, "main"), []);

        var first = Host.Waits[0];
        Assert.That(first.Kind, Is.EqualTo("until"));
        Assert.That(first.Arg["timeoutSeconds"].AsNumber(), Is.EqualTo(5d), "timeout_seconds is camelCased");
        Assert.That(first.Arg["clock"].AsString(), Is.EqualTo("wall"));
        Assert.That(first.Arg["fn"].Kind, Is.EqualTo(ScriptValueKind.Function));
        Assert.That(Rt.Invoke(first.Arg["fn"].AsFunction(), []).AsBool(), Is.False, "the host can call the predicate");

        first.Wait.Complete(ScriptValue.False); // timed out
        Rt.Advance(TimeSpan.FromSeconds(5));
        var second = Host.Waits[1];
        Assert.That(second.Arg.AsMap().Keys, Is.EquivalentTo(new[] { "fn" }), "unset options are not sent");
        second.Wait.Complete(ScriptValue.True);
        Rt.Advance(TimeSpan.FromSeconds(5));

        Assert.That(Logged, Is.EqualTo(new[] { "a=false", "b=true" }));
    }

    [Test]
    public void WaitEvent_SendsNameVesselAndTimeout_AndReturnsTheEventWithSnakeCaseKeys()
    {
        var module = _fx.Load("wait-event", """
            return { main = function()
              local ev = ksa.wait_event("stage", { vessel = "v1", timeout_seconds = 3 })
              ksa.console.log(ev.type .. " " .. ev.vessel_id .. " " .. ev.payload.stage_index .. " " .. ev.ut)
              local none = ksa.wait_event("impact", { vessel = ksa.vessel("v2") })
              ksa.console.log("none=" .. tostring(none))
            end }
            """);

        Rt.Invoke(_fx.Export(module, "main"), []);

        var first = Host.Waits[0];
        Assert.That(first.Kind, Is.EqualTo("waitEvent"));
        Assert.That(first.Arg["name"].AsString(), Is.EqualTo("stage"));
        Assert.That(first.Arg["vessel"].AsString(), Is.EqualTo("v1"));
        Assert.That(first.Arg["timeoutSeconds"].AsNumber(), Is.EqualTo(3d));

        first.Wait.Complete(ContextMaps.Event("stage", "v1", ContextMaps.Map(("stageIndex", ScriptValue.From(2d)))));
        Rt.Advance(TimeSpan.FromSeconds(5));
        var second = Host.Waits[1];
        Assert.That(second.Arg["vessel"].AsString(), Is.EqualTo("v2"), "a VesselRef is sent as its id");
        Assert.That(second.Arg.AsMap().ContainsKey("timeoutSeconds"), Is.False);
        second.Wait.Complete(ScriptValue.Null); // timed out
        Rt.Advance(TimeSpan.FromSeconds(5));

        Assert.That(Logged, Is.EqualTo(new[] { "stage v1 2 1", "none=nil" }));
    }

    [Test]
    public void State_GetAndSet_ReachTheHost_WithValuesUntouched()
    {
        Host.State["seed"] = ContextMaps.Map(("someKey", ScriptValue.From("kept")), ("nested", ContextMaps.Map(("innerKey", ScriptValue.From(1d)))));

        var result = _fx.Call("""
            return { f = function()
              ksa.state.set("count", 3)
              ksa.state.set("config", { mode = "auto", camelKey = { 1, 2, 3 } })
              ksa.state.dotted = "via metatable"
              local seed = ksa.state.get("seed")
              local viaIndex = ksa.state.dotted
              ksa.state:set("colon", true)
              ksa.state.set("gone", 1)
              ksa.state.set("gone", nil)
              return { seed = seed.someKey, inner = seed.nested.innerKey, viaIndex = viaIndex, missing = ksa.state.get("nothing") }
            end }
            """, "f").AsMap();

        Assert.That(Host.State["count"].AsNumber(), Is.EqualTo(3d));
        var config = Host.State["config"].AsMap();
        Assert.That(config["mode"].AsString(), Is.EqualTo("auto"));
        Assert.That(config["camelKey"].AsList().Select(v => v.AsNumber()), Is.EqualTo(new[] { 1d, 2d, 3d }), "persisted keys are stored verbatim");
        Assert.That(Host.State["dotted"].AsString(), Is.EqualTo("via metatable"));
        Assert.That(Host.State["colon"].AsBool(), Is.True);
        Assert.That(Host.State["gone"].IsNull, Is.True, "nil clears the key");
        Assert.That(result["seed"].AsString(), Is.EqualTo("kept"), "values come back exactly as stored (no snake_case conversion)");
        Assert.That(result["inner"].AsNumber(), Is.EqualTo(1d));
        Assert.That(result["viaIndex"].AsString(), Is.EqualTo("via metatable"));
        Assert.That(result.ContainsKey("missing"), Is.False);
    }

    [Test]
    public void ConsoleExec_And_ConsoleLog_ReachTheHost()
    {
        _fx.Call("""
            return { f = function()
              ksa.console.exec("help ksa")
              ksa.console:exec("clear")
              ksa.console.log("plain", 7, { a = 1 }, true)
              ksa.console.warn("careful")
              ksa.console.error("broken")
              ksa.console.debug("trace")
              print("via print", 2)
            end }
            """, "f");

        Assert.That(Host.ConsoleCommands, Is.EqualTo(new[] { "help ksa", "clear" }));
        Assert.That(Host.Logs.Select(l => (l.Level, l.Message)), Is.EqualTo(new[]
        {
            (1, "plain 7 {a = 1} true"),
            (2, "careful"),
            (3, "broken"),
            (0, "trace"),
            (1, "via print\t2"),
        }));
    }

    [Test]
    public void ConsoleExec_FailureFromTheHost_RaisesInTheScript_WithTheHostMessage()
    {
        Host.OnConsoleExec = _ => throw new InvalidOperationException("console is down");

        var result = _fx.Call("return { f = function() local ok, err = pcall(ksa.console.exec, 'boom') return { ok, err } end }", "f").AsList();

        Assert.That(result[0].AsBool(), Is.False);
        Assert.That(result[1].AsString(), Does.EndWith("ksa_host.console_exec: console is down"));
    }

    [Test]
    public void SignalCancelled_ReadsTheSignalHandle_EachTime()
    {
        var cancelled = false;
        Host.ReadHandler = (handle, path) => handle == "signal:watcher" && path == "cancelled" ? ScriptValue.From(cancelled) : ScriptValue.Null;
        var source = _fx.Write("watcher", """
            return { main = function(ctx)
              ksa.console.log("cancelled=" .. tostring(ctx.signal.cancelled))
              ksa.console.log("again=" .. tostring(ctx.signal.cancelled))
              ctx.signal.on_cancel(function() ksa.console.log("cleanup") end)
            end }
            """);
        var module = Rt.Load(source);
        var context = ContextMaps.Create("service", source, ("signal", ScriptValue.From("watcher")));

        Rt.Invoke(_fx.Export(module, "main"), [context]);
        cancelled = true;
        Rt.Invoke(_fx.Export(module, "main"), [context]);

        Assert.That(Logged, Is.EqualTo(new[] { "cancelled=false", "again=false", "cancelled=true", "again=true" }));
        Assert.That(Host.Reads.Count(r => r == "signal:watcher|cancelled"), Is.EqualTo(4));
        Assert.That(Host.Subscriptions.Select(s => (s.Kind, s.Key)), Is.EqualTo(new[] { ("event", "signal.cancel"), ("event", "signal.cancel") }));
        Rt.Invoke(Host.Subscriptions[0].Fn, [ContextMaps.Create("event", source, ("event", ContextMaps.Event("signal.cancel", null, ContextMaps.Map())))]);
        Assert.That(Logged.Last(), Is.EqualTo("cleanup"));
    }

    [Test]
    public void FailedWait_RaisesInsideTheScript_AndIsReportedWithTheScriptFileAndLine()
    {
        var source = _fx.Write("exploder", "return { main = function()\n  ksa.console.log('before')\n  ksa.sleep(1)\n  ksa.console.log('unreachable')\nend }");
        var module = Rt.Load(source);
        Rt.Invoke(_fx.Export(module, "main"), []);

        Host.Waits[0].Wait.Fail(new InvalidOperationException("sleep exploded"));
        Rt.Advance(TimeSpan.FromSeconds(5));

        var error = Host.ReportedErrors.Single();
        Assert.That(error.Message, Does.Contain("sleep exploded"));
        Assert.That(error.File, Is.EqualTo(source.Path));
        Assert.That(error.Line, Is.EqualTo(3), "the line of the ksa.sleep call");
        Assert.That(error.Traceback, Does.Contain("stack traceback").And.Contain("exploder.lua:3"));
        Assert.That(Logged, Is.EqualTo(new[] { "before" }));
    }

    [Test]
    public void FailedWait_InAHelperFunction_IsReportedInTheScriptFile_AndTheTracebackNamesTheWaitingLine()
    {
        var source = _fx.Write("helper-exploder", "local function nap()\n  ksa.sleep(1)\nend\nreturn { main = function()\n  nap()\nend }");
        var module = Rt.Load(source);
        Rt.Invoke(_fx.Export(module, "main"), []);

        Host.Waits[0].Wait.Fail(new InvalidOperationException("sleep exploded"));
        Rt.Advance(TimeSpan.FromSeconds(5));

        var error = Host.ReportedErrors.Single();
        Assert.That(error.File, Is.EqualTo(source.Path));
        // The message position comes from the prelude's error(msg, 3) behind tail calls, so with a helper in between it names
        // the helper's caller (line 5) rather than the waiting line (2); the traceback below is exact.
        Assert.That(error.Line, Is.Not.Null.And.GreaterThan(0));
        Assert.That(error.Traceback, Does.Contain("helper-exploder.lua:2: in function 'nap'"));
    }

    [Test]
    public void VesselRef_Getters_ReadTheFacade_AndSnakeCaseTheResult()
    {
        var baseRead = Host.ReadHandler;
        Host.ReadHandler = (handle, path) => (handle, path) switch
        {
            ("vessel:v1", "battery") => ContextMaps.Map(("fraction", ScriptValue.From(0.5)), ("maxCharge", ScriptValue.From(10d))),
            ("vessel:v1", "name") => ScriptValue.From("Kitten One"),
            ("time", "ut") => ScriptValue.From(1234.5),
            _ => baseRead!(handle, path),
        };

        var result = _fx.Call("""
            return { f = function()
              local v = ksa.vessel("v1")
              return {
                name = v.name, id = v.id, tostring = tostring(v),
                charge = v.battery.max_charge, fraction = v.battery.fraction,
                ut = ksa.time.ut,
                ghost = tostring(ksa.vessel("ghost")),
                same = ksa.vessel("v1") == v,
              }
            end }
            """, "f").AsMap();

        Assert.That(result["name"].AsString(), Is.EqualTo("Kitten One"));
        Assert.That(result["id"].AsString(), Is.EqualTo("v1"));
        Assert.That(result["tostring"].AsString(), Is.EqualTo("VesselRef(v1)"));
        Assert.That(result["charge"].AsNumber(), Is.EqualTo(10d));
        Assert.That(result["fraction"].AsNumber(), Is.EqualTo(0.5));
        Assert.That(result["ut"].AsNumber(), Is.EqualTo(1234.5));
        Assert.That(result["ghost"].AsString(), Is.EqualTo("nil"), "ksa.vessel is nil unless Read(\"vessel:<id>\", \"id\") is non-null");
        Assert.That(result["same"].AsBool(), Is.True);
        Assert.That(Host.Reads, Does.Contain("vessel:ghost|id"));
    }

    [Test]
    public void TimeWarp_IsAFrameAction()
    {
        _fx.Call("return { f = function() ksa.time.set_warp(4) ksa.time:set_warp(8) end }", "f");

        Assert.That(Host.Acts, Is.EqualTo(new[]
        {
            new ActCall("debug.warp", null, -1, 4, null, null, null),
            new ActCall("debug.warp", null, -1, 8, null, null, null),
        }));
    }
}
