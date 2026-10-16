using KsaScripting.Core.Commands;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.Lua.Tests;

[TestFixture]
public sealed class HostFunctionTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp() => _fx = new LuaRuntimeFixture();

    [TearDown]
    public void TearDown() => _fx.Dispose();

    private FakeHostFacade Host => _fx.Host;

    // Runs `body` (Lua statements) and returns what it returns.
    private ScriptValue Run(string body) => _fx.Call($"return {{ f = function() {body} end }}", "f");

    [Test]
    public void EveryDocumentedHostFunction_Exists()
    {
        var names = new[]
        {
            "read", "snapshot", "list", "act", "wait", "subscribe", "unsubscribe", "log", "console_exec",
            "state_get", "state_set", "term_write", "term_info", "term_set_title", "term_policy",
        };

        var result = Run($"local out = {{}} for _, n in ipairs({{ {string.Join(", ", names.Select(n => $"'{n}'"))} }}) do out[n] = type(ksa_host[n]) end return out");

        foreach (var name in names)
            Assert.That(result.AsMap()[name].AsString(), Is.EqualTo("function"), name);
    }

    [Test]
    public void BadArguments_ReturnFalseAndMessage_NeverRaise()
    {
        var result = Run("""
            local a = { ksa_host.act(nil) }
            local b = { ksa_host.read(1, {}) }
            local c = { ksa_host.subscribe('event', 'e', 'not a function') }
            local d = { ksa_host.unsubscribe('not-a-guid') }
            local e = { ksa_host.term_policy(1, 'yes') }
            local f = { ksa_host.act('x', 'v', 'nan') }
            local g = { ksa_host.act('x', 'v', 1, 1, { 'a' }) }
            return { a, b, c, d, e, f, g }
            """).AsList();

        foreach (var entry in result)
        {
            Assert.That(entry.AsList()[0].AsBool(), Is.False);
            Assert.That(entry.AsList()[1].AsString(), Does.StartWith("ksa_host."));
        }

        Assert.That(result[0].AsList()[1].AsString(), Is.EqualTo("ksa_host.act: action must be a string"));
        Assert.That(result[2].AsList()[1].AsString(), Is.EqualTo("ksa_host.subscribe: fn must be a function"));
        Assert.That(Host.Acts, Is.Empty);
        Assert.That(Host.Subscriptions, Is.Empty);
    }

    [Test]
    public void HostExceptions_BecomeFalseAndMessage()
    {
        Host.ReadHandler = (_, _) => throw new InvalidOperationException("nope");

        var result = Run("return { ksa_host.read('time', 'ut') }").AsList();

        Assert.That(result[0].AsBool(), Is.False);
        Assert.That(result[1].AsString(), Is.EqualTo("ksa_host.read: nope"));
    }

    [Test]
    public void Act_PassesAllArguments_AndConvertsTheResult()
    {
        Host.ActHandler = _ => new ActResult(CommandResult.Invalid("bad value"), null);

        var result = Run("return { ksa_host.act('light.color', 'v1', 3, 1.5, { 0.1, 0.2, 0.3 }, 'tok', 'aux') }").AsList();

        Assert.That(result[0].AsBool(), Is.True);
        var map = result[1].AsMap();
        Assert.That(map["ok"].AsBool(), Is.False);
        Assert.That(map["outcome"].AsString(), Is.EqualTo("Invalid"));
        Assert.That(map["message"].AsString(), Is.EqualTo("bad value"));

        var act = Host.Acts.Single();
        Assert.That((act.Action, act.VesselId, act.Ordinal, act.Value, act.Token, act.Aux), Is.EqualTo(("light.color", "v1", 3, 1.5, "tok", "aux")));
        Assert.That(act.Values, Is.EqualTo(new[] { 0.1, 0.2, 0.3 }));
    }

    [Test]
    public void Act_Defaults_OrdinalMinusOne_ValueZero_NoVessel()
    {
        Run("ksa_host.act('debug.warp')");

        var act = Host.Acts.Single();
        Assert.That((act.VesselId, act.Ordinal, act.Value, act.Values, act.Token, act.Aux), Is.EqualTo(((string?)null, -1, 0d, (double[]?)null, (string?)null, (string?)null)));
    }

    [Test]
    public void SolverAct_ReturnsWaitTable_AndParksTheCoroutineUntilComplete()
    {
        HostWait? pending = null;
        Host.ReadHandler = (handle, path) => path == "id" ? ScriptValue.From("v1") : ScriptValue.Null;
        Host.ActHandler = act =>
        {
            if (act.Action != "vessel.attitude_mode")
                return new ActResult(CommandResult.Ok, null);
            pending = new HostWait(500, "act");
            return new ActResult(null, pending);
        };
        var module = _fx.Load("solver", """
            return { main = function()
              local r = ksa.vessel("v1"):set_attitude_mode("Prograde")
              ksa.console.log("ok=" .. tostring(r.ok) .. " outcome=" .. tostring(r.outcome))
            end }
            """);

        var immediate = _fx.Runtime.Invoke(_fx.Export(module, "main"), []);

        Assert.That(immediate.IsNull, Is.True);
        Assert.That(pending, Is.Not.Null);
        Assert.That(Host.Acts.Single().Token, Is.EqualTo("Prograde"));
        Assert.That(Host.Logs, Is.Empty);

        pending!.Complete(CommandResult.Ok.ToScriptValue());
        Assert.That(_fx.Runtime.Advance(TimeSpan.FromMilliseconds(20)), Is.False);

        Assert.That(Host.Logs.Single().Message, Is.EqualTo("ok=true outcome=Ok"));
    }

    [Test]
    public void Wait_PassesKindAndArgument_IncludingFunctions()
    {
        var module = _fx.Load("waiter", "return { f = function() ksa_host.wait('until', { fn = function() return 'yes' end, timeoutSeconds = 2 }) end }");

        _fx.Runtime.Invoke(_fx.Export(module, "f"), []);

        var wait = Host.Waits.Single();
        Assert.That(wait.Kind, Is.EqualTo("until"));
        Assert.That(wait.Arg["timeoutSeconds"].AsNumber(), Is.EqualTo(2d));
        var predicate = wait.Arg["fn"].AsFunction();
        Assert.That(_fx.Runtime.Invoke(predicate, []).AsString(), Is.EqualTo("yes"));
    }

    [Test]
    public void Wait_ResultsAreConvertedBack()
    {
        var module = _fx.Load("waiter2", "return { main = function() local r = ksa.next_step() ksa.console.log(tostring(r.step_index or r.stepIndex) .. ':' .. tostring(r.ut)) end }");
        _fx.Runtime.Invoke(_fx.Export(module, "main"), []);

        Host.Waits.Single().Wait.Complete(ScriptValue.Map(new Dictionary<string, ScriptValue>
        {
            ["ut"] = ScriptValue.From(12.5),
            ["stepIndex"] = ScriptValue.From(3d),
        }));
        _fx.Runtime.Advance(TimeSpan.FromMilliseconds(20));

        Assert.That(Host.Logs.Single().Message, Is.EqualTo("3:12.5"));
    }

    [Test]
    public void Subscribe_ReadsAllOptionKeys()
    {
        Run("ksa_host.subscribe('tick', 'every', function() end, { intervalSeconds = 2.5, clock = 'wall', vessel = 'v9', sync = true })");

        var sub = Host.Subscriptions.Single();
        Assert.That((sub.Kind, sub.Key), Is.EqualTo(("tick", "every")));
        Assert.That(sub.Options, Is.EqualTo(new BindingOptions(Sync: true, Vessel: "v9", Clock: "wall", IntervalSeconds: 2.5)));
    }

    [Test]
    public void Subscribe_IgnoresMetamethodsAndBadOptionTypes()
    {
        // Options are read raw: an __index metamethod on the table is never run, wrongly typed keys fall back to defaults.
        Run("""
            local opts = setmetatable({ sync = 'yes', clock = 5 }, { __index = function() error('must not run') end })
            local ok, id = ksa_host.subscribe('event', 'e', function() end, opts)
            assert(ok, id)
            """);

        Assert.That(Host.Subscriptions.Single().Options, Is.EqualTo(new BindingOptions()));
    }

    [Test]
    public void Subscribe_ReturnsGuidString_AndUnsubscribeTakesIt()
    {
        var result = Run("""
            local ok, id = ksa_host.subscribe('event', 'stage', function() end, {})
            local ok2 = ksa_host.unsubscribe(id)
            return { id, ok2 }
            """).AsList();

        var sub = Host.Subscriptions.Single();
        Assert.That(result[0].AsString(), Is.EqualTo(sub.Id.ToString("D")));
        Assert.That(result[1].AsBool(), Is.True);
        Assert.That(Host.Unsubscribed, Is.EqualTo(new[] { sub.Id }));
    }

    [Test]
    public void ReadSnapshotList_ReturnConvertedValues()
    {
        Host.ReadHandler = (handle, path) => ScriptValue.Map(new Dictionary<string, ScriptValue>
        {
            ["handle"] = ScriptValue.From(handle),
            ["path"] = ScriptValue.From(path),
            ["cci"] = ScriptValue.List([ScriptValue.From(1d), ScriptValue.From(2d)]),
        });

        var result = Run("""
            local _, r = ksa_host.read('vessel:a', 'position.cci')
            local _, s = ksa_host.snapshot('body:b')
            local _, l = ksa_host.list('vessels')
            return { r, s, l }
            """).AsList();

        Assert.That(Host.Reads, Is.EqualTo(new[] { "vessel:a|position.cci" }));
        Assert.That(result[0].AsMap()["path"].AsString(), Is.EqualTo("position.cci"));
        Assert.That(result[0].AsMap()["cci"].AsList(), Has.Count.EqualTo(2));
        Assert.That(result[1].AsMap()["handle"].AsString(), Is.EqualTo("body:b"));
        Assert.That(result[2].AsList()[0].AsString(), Is.EqualTo("vessels"));
    }

    [Test]
    public void Log_ConsoleExec_State_Term_Functions()
    {
        Run("""
            ksa_host.log(2, 'careful')
            ksa_host.console_exec('help')
            ksa_host.state_set('answer', { 1, 2, { x = true } })
            ksa_host.term_write(4, 'abc\0\255')
            ksa_host.term_set_title(4, 'title')
            ksa_host.term_policy(4, true)
            """);

        Assert.That(Host.Logs.Single(), Is.EqualTo(new LogCall(2, "careful", "")));
        Assert.That(Host.ConsoleCommands, Is.EqualTo(new[] { "help" }));
        Assert.That(Host.State["answer"].Kind, Is.EqualTo(ScriptValueKind.List));
        Assert.That(Host.TermWrites.Single().TermId, Is.EqualTo(4));
        Assert.That(Host.TermWrites.Single().Bytes, Is.EqualTo(new byte[] { 0x61, 0x62, 0x63, 0x00, 0xFF }));

        var roundTrip = Run("local _, v = ksa_host.state_get('answer') local _, info = ksa_host.term_info(4) return { v, info }").AsList();
        Assert.That(roundTrip[0].AsList(), Has.Count.EqualTo(3));
        Assert.That(roundTrip[1].AsMap()["cols"].AsNumber(), Is.EqualTo(80d));
    }

    [Test]
    public void HostCall_FromANestedInvoke_Works_AndDepthIsRestored()
    {
        // A host call (here: Log) re-enters the runtime, the way a sync event hook does.
        var module = _fx.Load("reentrant", """
            return {
              outer = function() ksa_host.log(1, 'trigger') return 'outer-done' end,
              inner = function() ksa_host.log(1, 'inner') return 'inner-done' end,
            }
            """);
        var inner = _fx.Export(module, "inner");
        string? innerResult = null;
        Host.OnLog = log =>
        {
            if (log.Message == "trigger")
                innerResult = _fx.Runtime.Invoke(inner, []).AsString();
        };

        var outer = _fx.Runtime.Invoke(_fx.Export(module, "outer"), []);

        Assert.That(outer.AsString(), Is.EqualTo("outer-done"));
        Assert.That(innerResult, Is.EqualTo("inner-done"));
        Assert.That(Host.Logs.Select(l => l.Message), Is.EqualTo(new[] { "trigger", "inner" }));
        Assert.That(_fx.Runtime.Stats.CallsTotal, Is.EqualTo(1), "only the outermost call is counted");
    }

    [Test]
    public void NestedInvoke_ThatFails_DoesNotBreakTheOuterCall()
    {
        var module = _fx.Load("reentrant-fail", """
            return {
              outer = function() ksa_host.log(1, 'trigger') return 'outer-done' end,
              bad = function() error('inner failure') end,
            }
            """);
        var bad = _fx.Export(module, "bad");
        ScriptRuntimeException? caught = null;
        Host.OnLog = log =>
        {
            if (log.Message != "trigger")
                return;
            try
            {
                _fx.Runtime.Invoke(bad, []);
            }
            catch (ScriptRuntimeException ex)
            {
                caught = ex;
            }
        };

        var outer = _fx.Runtime.Invoke(_fx.Export(module, "outer"), []);

        Assert.That(outer.AsString(), Is.EqualTo("outer-done"));
        Assert.That(caught?.Message, Does.Contain("inner failure"));
    }
}
