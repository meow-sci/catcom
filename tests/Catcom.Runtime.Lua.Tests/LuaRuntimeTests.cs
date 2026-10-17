using Catcom.Core.Hosting;
using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

[TestFixture]
public sealed class LuaRuntimeTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp() => _fx = new LuaRuntimeFixture();

    [TearDown]
    public void TearDown() => _fx.Dispose();

    private LuaRuntime Rt => _fx.Runtime;

    private FakeHostFacade Host => _fx.Host;

    [Test]
    public void Native_IsAvailable_OnThisMachine()
    {
        using var rt = new LuaRuntime();
        Assert.That(rt.IsAvailable, Is.True, rt.UnavailableReason);
        Assert.That(rt.UnavailableReason, Is.Null);
        Assert.That(rt.Id, Is.EqualTo("lua"));
        Assert.That(rt.Language, Is.EqualTo("lua"));
    }

    [Test]
    public void Load_ReturnsModule_AndExportsFromReturnedTable()
    {
        var module = _fx.Load("exports", "return { a = function() end, b = 1 }");

        Assert.That(module.RuntimeId, Is.EqualTo("lua"));
        Assert.That(module.Name, Is.EqualTo("exports"));
        Assert.That(Rt.Exports(module), Is.EqualTo(new[] { "a" }));
        Assert.That(_fx.Export(module, "a").RuntimeId, Is.EqualTo("lua"));
        Assert.Throws<KeyNotFoundException>(() => Rt.GetExport(module, "b"));
        Assert.Throws<KeyNotFoundException>(() => Rt.GetExport(module, "missing"));
    }

    [Test]
    public void Load_ExportViaKsaExport_MergesAndReturnedTableWins()
    {
        var module = _fx.Load("merge", """
            ksa.export("x", function() return "from-export" end)
            ksa.export("clash", function() return "from-export" end)
            return { clash = function() return "from-return" end, y = function() return "y" end }
            """);

        Assert.That(Rt.Exports(module), Is.EqualTo(new[] { "clash", "x", "y" }));
        Assert.That(Rt.Invoke(_fx.Export(module, "clash"), []).AsString(), Is.EqualTo("from-return"));
        Assert.That(Rt.Invoke(_fx.Export(module, "x"), []).AsString(), Is.EqualTo("from-export"));
    }

    [Test]
    public void Invoke_ReturnsValue_RoundTripsNumbersStringsTablesBools()
    {
        var module = _fx.Load("echo", "return { f = function(x) return x end }");
        var f = _fx.Export(module, "f");

        var map = ScriptValue.Map(new Dictionary<string, ScriptValue>
        {
            ["n"] = ScriptValue.From(1.5),
            ["s"] = ScriptValue.From("héllo wörld ✓"),
            ["t"] = ScriptValue.True,
            ["f"] = ScriptValue.False,
            ["list"] = ScriptValue.List([ScriptValue.From(1d), ScriptValue.From("two"), ScriptValue.True]),
            ["nested"] = ScriptValue.Map(new Dictionary<string, ScriptValue>
            {
                ["inner"] = ScriptValue.List([ScriptValue.From(10d), ScriptValue.From(20d)]),
            }),
        });

        var result = Rt.Invoke(f, [map]);

        Assert.That(result, Is.EqualTo(map));
        Assert.That(Rt.Invoke(f, [ScriptValue.Null]).IsNull, Is.True);
        Assert.That(Rt.Invoke(f, [ScriptValue.From(42d)]).AsNumber(), Is.EqualTo(42d));
        Assert.That(Rt.Invoke(f, [ScriptValue.From("x")]).AsString(), Is.EqualTo("x"));
        Assert.That(Rt.Invoke(f, [ScriptValue.True]).AsBool(), Is.True);
    }

    [Test]
    public void Load_SyntaxError_ThrowsScriptLoadExceptionWithFileAndLine()
    {
        var source = _fx.Write("broken", "local a = 1\nlocal b = 2\nlocal = = 3\nreturn {}\n");

        var ex = Assert.Throws<ScriptLoadException>(() => Rt.Load(source))!;

        Assert.That(ex.Line, Is.EqualTo(3));
        Assert.That(ex.File, Is.EqualTo(source.Path));
        Assert.That(ex.Snippet, Is.EqualTo("local = = 3"));
        Assert.That(ex.Message, Does.Contain("scripts/broken.lua:3:"));
    }

    [Test]
    public void Invoke_RuntimeError_ThrowsWithTracebackFileAndLine()
    {
        var source = _fx.Write("failing", "return {\n  boom = function() error(\"kaput\") end,\n}\n");
        var module = Rt.Load(source);

        var ex = Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(_fx.Export(module, "boom"), []))!;

        Assert.That(ex.Line, Is.EqualTo(2));
        Assert.That(ex.File, Is.EqualTo(source.Path));
        Assert.That(ex.Message, Does.Contain("kaput"));
        Assert.That(ex.Message, Does.Not.Contain("stack traceback"));
        Assert.That(ex.Traceback, Does.Contain("stack traceback"));
    }

    [Test]
    public void Prelude_VesselThrottle_ReachesFacade()
    {
        // ksa.vessel(id) returns nil unless the host knows the vessel (Read("vessel:<id>", "id")).
        Host.ReadHandler = (handle, path) => handle == "vessel:x" && path == "id" ? ScriptValue.From("x") : ScriptValue.Null;
        var module = _fx.Load("throttle", "return { main = function() ksa.vessel(\"x\"):set_throttle(0.5) end }");

        Rt.Invoke(_fx.Export(module, "main"), []);

        Assert.That(Host.Acts, Has.Count.EqualTo(1));
        var act = Host.Acts[0];
        Assert.That(act.Action, Is.EqualTo("vessel.throttle"));
        Assert.That(act.VesselId, Is.EqualTo("x"));
        Assert.That(act.Ordinal, Is.EqualTo(-1));
        Assert.That(act.Value, Is.EqualTo(0.5));
        Assert.That(act.Values, Is.Null);
        Assert.That(act.Token, Is.Null);
    }

    [Test]
    public void Async_Sleep_YieldsThenResumesInAdvance()
    {
        var module = _fx.Load("sleeper", "return { main = function() ksa.sleep(1) ksa.console.log(\"after\") end }");

        var result = Rt.Invoke(_fx.Export(module, "main"), []);

        Assert.That(result.IsNull, Is.True);
        Assert.That(Host.Waits, Has.Count.EqualTo(1));
        Assert.That(Host.Waits[0].Kind, Is.EqualTo("sleep"));
        Assert.That(Host.Waits[0].Arg["seconds"].AsNumber(), Is.EqualTo(1d));
        Assert.That(Host.Logs.Select(l => l.Message), Does.Not.Contain("after"));
        Assert.That(Rt.Advance(TimeSpan.FromMilliseconds(50)), Is.False, "nothing is complete yet");

        var enteredBefore = Host.EnteredScripts.Count;
        Host.Waits[0].Wait.Complete(ScriptValue.Null);
        var more = Rt.Advance(TimeSpan.FromMilliseconds(50));

        Assert.That(more, Is.False);
        var after = Host.Logs.Single(l => l.Message == "after");
        Assert.That(after.Level, Is.EqualTo(1));
        Assert.That(after.Script, Is.EqualTo("sleeper"), "the continuation runs under EnterScript(owner)");
        Assert.That(Host.EnteredScripts.Skip(enteredBefore), Does.Contain("sleeper"));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void Async_FailedWait_RaisesInScript_ReportedViaReportError()
    {
        var module = _fx.Load("failing-wait", "return { main = function() ksa.sleep(1) ksa.console.log(\"unreachable\") end }");
        Rt.Invoke(_fx.Export(module, "main"), []);

        Host.Waits[0].Wait.Fail(new InvalidOperationException("sleep exploded"));
        Rt.Advance(TimeSpan.FromMilliseconds(50));

        Assert.That(Host.ReportedErrors, Has.Count.EqualTo(1));
        Assert.That(Host.ReportedErrors[0].Message, Does.Contain("sleep exploded"));
        Assert.That(Host.ReportedErrors[0].Traceback, Does.Contain("stack traceback"));
        Assert.That(Host.Logs.Select(l => l.Message), Does.Not.Contain("unreachable"));
    }

    [Test]
    public void Async_CancelledWait_CanBeCaughtByTheScript()
    {
        var module = _fx.Load("cancelled-wait", """
            return { main = function()
              local ok, err = pcall(ksa.sleep, 1)
              ksa.console.log(tostring(ok) .. ":" .. tostring(err))
            end }
            """);
        Rt.Invoke(_fx.Export(module, "main"), []);

        Host.Waits[0].Wait.Cancel();
        Rt.Advance(TimeSpan.FromMilliseconds(50));

        Assert.That(Host.ReportedErrors, Is.Empty);
        Assert.That(Host.Logs.Single().Message, Does.StartWith("false:").And.EndWith("wait cancelled"));
    }

    [Test]
    public void Context_MapWithCtxFlag_IsPassedThroughMakeContext()
    {
        var module = _fx.Load("ctx", """
            return {
              hook = function(ctx)
                return { ctx.vessel_id, ctx.kind, ctx.step_index, ctx.ksa == ksa, ctx.script.name, rawget(ctx, "$ctx") == nil }
              end,
              raw = function(t) return t.vesselId end,
              second = function(a, b) return type(a.ksa) .. ":" .. type(b.ksa) .. ":" .. tostring(b.vesselId) end,
            }
            """);
        var ctx = ScriptValue.Map(new Dictionary<string, ScriptValue>
        {
            ["$ctx"] = ScriptValue.True,
            ["kind"] = ScriptValue.From("event"),
            ["script"] = ScriptValue.Map(new Dictionary<string, ScriptValue> { ["name"] = ScriptValue.From("ctx"), ["path"] = ScriptValue.From("/scripts/ctx.lua") }),
            ["vesselId"] = ScriptValue.From("v1"),
            ["stepIndex"] = ScriptValue.From(7d),
        });

        var result = Rt.Invoke(_fx.Export(module, "hook"), [ctx]).AsList();

        Assert.That(result[0].AsString(), Is.EqualTo("v1"), "camelCase keys become snake_case");
        Assert.That(result[1].AsString(), Is.EqualTo("event"));
        Assert.That(result[2].AsNumber(), Is.EqualTo(7d));
        Assert.That(result[3].AsBool(), Is.True, "ctx.ksa is the script's ksa table");
        Assert.That(result[4].AsString(), Is.EqualTo("ctx"));
        Assert.That(result[5].AsBool(), Is.True, "the $ctx marker is not copied");

        // A map without the "$ctx" flag is an ordinary argument and is not converted.
        var plain = ScriptValue.Map(new Dictionary<string, ScriptValue> { ["vesselId"] = ScriptValue.From("v2") });
        Assert.That(Rt.Invoke(_fx.Export(module, "raw"), [plain]).AsString(), Is.EqualTo("v2"));

        // Only flagged arguments are converted, wherever they are in the argument list.
        Assert.That(Rt.Invoke(_fx.Export(module, "second"), [ctx, plain]).AsString(), Is.EqualTo("table:nil:v2"));
    }

    [Test]
    public void Subscribe_FromTopLevel_PassesCallableAndOptions()
    {
        _fx.Load("subscriber", """
            local fired = 0
            ksa.on("stage", function() fired = fired + 1 ksa.console.log("stage:" .. fired) end, { sync = true })
            return {}
            """);

        Assert.That(Host.Subscriptions, Has.Count.EqualTo(1));
        var sub = Host.Subscriptions[0];
        Assert.That(sub.Kind, Is.EqualTo("event"));
        Assert.That(sub.Key, Is.EqualTo("stage"));
        Assert.That(sub.Options.Sync, Is.True);
        Assert.That(sub.Options.Vessel, Is.Null);
        Assert.That(sub.Options.Clock, Is.EqualTo("ut"));
        Assert.That(sub.Fn.RuntimeId, Is.EqualTo("lua"));

        Rt.Invoke(sub.Fn, []);
        Rt.Invoke(sub.Fn, []);

        Assert.That(Host.Logs.Select(l => l.Message), Is.EqualTo(new[] { "stage:1", "stage:2" }));
        Assert.That(Host.EnteredScripts, Does.Contain("subscriber"), "top-level code runs under EnterScript");
    }

    [Test]
    public void Callable_Dispose_ThenInvoke_Throws()
    {
        var module = _fx.Load("disposable", "return { f = function() return 1 end }");
        var f = _fx.Export(module, "f");
        Assert.That(Rt.Invoke(f, []).AsNumber(), Is.EqualTo(1d));
        Assert.That(_fx.Export(module, "f"), Is.SameAs(f), "the same instance is returned while it is not disposed");

        f.Dispose();

        Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(f, []));
        var again = _fx.Export(module, "f");
        Assert.That(again, Is.Not.SameAs(f));
        Assert.That(Rt.Invoke(again, []).AsNumber(), Is.EqualTo(1d));
    }

    [Test]
    public void Unload_ReleasesModule_AndParkedCoroutines()
    {
        var module = _fx.Load("unloadable", """
            return { main = function() ksa.sleep(1) ksa.console.log("resumed") end,
                     ping = function() return "pong" end }
            """);
        var main = _fx.Export(module, "main");
        var ping = _fx.Export(module, "ping");
        Rt.Invoke(main, []);
        Assert.That(Host.Waits, Has.Count.EqualTo(1));

        Rt.Unload(module);
        Host.Waits[0].Wait.Complete(ScriptValue.Null);
        var more = Rt.Advance(TimeSpan.FromMilliseconds(50));

        Assert.That(more, Is.False);
        Assert.That(Host.Logs.Select(l => l.Message), Does.Not.Contain("resumed"));
        Assert.That(Host.ReportedErrors, Is.Empty);
        Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(ping, []));
        Assert.That(Rt.Exports(module), Is.Empty);
        Assert.Throws<KeyNotFoundException>(() => Rt.GetExport(module, "ping"));
        Assert.DoesNotThrow(() => Rt.Unload(module), "unloading twice is a no-op");
    }

    [Test]
    public void Stats_ReportsHeapJitAndVersion()
    {
        var before = Rt.Stats;
        Assert.That(before.JitEnabled, Is.True);
        Assert.That(before.Version, Does.StartWith("LuaJIT 2.1"));
        Assert.That(before.HeapBytes, Is.GreaterThan(0));

        var module = _fx.Load("stats", "return { f = function() return 1 end }");
        Rt.Invoke(_fx.Export(module, "f"), []);
        Rt.Invoke(_fx.Export(module, "f"), []);

        var after = Rt.Stats;
        Assert.That(after.CallsTotal, Is.EqualTo(2));
        Assert.That(after.LastCallMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(after.HeapBytes, Is.GreaterThan(0));
    }

    [Test]
    public void Stats_BeforeInitialize_AreZeros()
    {
        using var rt = new LuaRuntime();
        var stats = rt.Stats;
        Assert.That(stats.HeapBytes, Is.EqualTo(0));
        Assert.That(stats.CallsTotal, Is.EqualTo(0));
        Assert.That(stats.JitEnabled, Is.False);
        Assert.That(stats.Version, Is.EqualTo(string.Empty));
    }
}
