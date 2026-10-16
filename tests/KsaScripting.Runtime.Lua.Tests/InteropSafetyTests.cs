using KsaScripting.Core.Values;
using KsaScripting.Runtime.Lua.Interop;

namespace KsaScripting.Runtime.Lua.Tests;

/// <summary>
///     The "never raise through a managed frame" rule, tested from the hostile side: values whose metamethods and finalizers
///     would raise if the marshalling (or the collector the runtime drives) ever ran Lua code, and host values that would hit
///     LuaJIT's size limits (which raise). A violation would not fail an assertion, it would crash the test process.
/// </summary>
[TestFixture]
public sealed class InteropSafetyTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp() => _fx = new LuaRuntimeFixture();

    [TearDown]
    public void TearDown() => _fx.Dispose();

    private FakeHostFacade Host => _fx.Host;

    // Every metamethod the runtime could conceivably trigger counts a hit and raises.
    private const string HostileTable = """
        local hits = 0
        local function boom() hits = hits + 1 error("a metamethod ran") end
        local function hostile(t)
          return setmetatable(t, {
            __index = boom, __newindex = boom, __len = boom, __eq = boom, __call = boom, __tostring = boom,
            __concat = boom, __unm = boom, __lt = boom, __le = boom, __add = boom, __gc = boom,
          })
        end
        """;

    [Test]
    public void ConvertingHostileTables_RunsNoMetamethods_InEitherDirection()
    {
        var module = _fx.Load("hostile-meta", HostileTable + "\n" + """
            return {
              returned = function() return hostile({ 1, 2, 3, name = "x", nested = hostile({ 10, 20 }) }) end,
              sequence = function() return hostile({ "a", "b" }) end,
              toHost = function()
                local t = hostile({ 1, 2, 3, name = "x" })
                local out = {
                  state = { ksa_host.state_set("k", t) },
                  wait = { ksa_host.wait("never", t) },
                  act = { ksa_host.act("a", "v", 1, 1, t) },
                  subscribe = { ksa_host.subscribe("event", "e", function() end, hostile({ sync = true, vessel = "v1" })) },
                  badTypes = { ksa_host.read(t, t) },
                  log = { ksa_host.log(1, t) },
                }
                return { ok = out.state[1], act = out.act[1], subscribe = out.subscribe[1], badTypes = out.badTypes[1], log = out.log[1], hits = hits }
              end,
              hits = function() return hits end,
            }
            """);

        var returned = _fx.Runtime.Invoke(_fx.Export(module, "returned"), []).AsMap();
        var sequence = _fx.Runtime.Invoke(_fx.Export(module, "sequence"), []);
        var toHost = _fx.Runtime.Invoke(_fx.Export(module, "toHost"), []).AsMap();

        Assert.That(returned.Keys, Is.EquivalentTo(new[] { "1", "2", "3", "name", "nested" }));
        Assert.That(returned["nested"].AsList().Select(v => v.AsNumber()), Is.EqualTo(new[] { 10d, 20d }), "the length is read raw, no __len");
        Assert.That(sequence.Kind, Is.EqualTo(ScriptValueKind.List));
        Assert.That(toHost["ok"].AsBool(), Is.True);
        Assert.That(toHost["act"].AsBool(), Is.True, "act's vector is read raw: elements 1..3");
        Assert.That(Host.Acts.Single().Values, Is.EqualTo(new[] { 1d, 2d, 3d }));
        Assert.That(toHost["subscribe"].AsBool(), Is.True);
        Assert.That(toHost["badTypes"].AsBool(), Is.False);
        Assert.That(toHost["log"].AsBool(), Is.False);
        Assert.That(toHost["hits"].AsNumber(), Is.EqualTo(0d));
        Assert.That(Host.State["k"].AsMap().Keys, Is.EquivalentTo(new[] { "1", "2", "3", "name" }));
        Assert.That(Host.Subscriptions.Single().Options.Sync, Is.True);
        Assert.That(Host.Subscriptions.Single().Options.Vessel, Is.EqualTo("v1"));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "hits"), []).AsNumber(), Is.EqualTo(0d));
    }

    [Test]
    public void ScriptsCannotRegisterFinalizers_SoTheCollectorTheRuntimeDrivesRunsNoLua()
    {
        // Userdata (the only thing Lua 5.1 runs __gc for) cannot be created: newproxy is gone, and __gc on a table is inert.
        var module = _fx.Load("finalizers", """
            return {
              churn = function()
                for i = 1, 2000 do
                  setmetatable({ i }, { __gc = function() ksa_host.log(3, "finalizer ran") error("finalizer error") end })
                end
                return type(newproxy)
              end,
            }
            """);

        var newproxy = _fx.Runtime.Invoke(_fx.Export(module, "churn"), []);
        _fx.Runtime.Unload(module); // lua_gc(LUA_GCCOLLECT)
        _fx.Runtime.Dispose(); // lua_close runs every pending finalizer

        Assert.That(newproxy.AsString(), Is.EqualTo("nil"));
        Assert.That(Host.Logs, Is.Empty);
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void WeakTables_ConvertWithoutRaising_EvenWhileTheCollectorRuns()
    {
        var module = _fx.Load("weak", """
            return {
              keys = function()
                local weak = setmetatable({}, { __mode = "k" })
                weak[{}] = 1
                weak["s"] = 2
                weak[7] = "seven"
                return weak
              end,
              values = function()
                local weak = setmetatable({}, { __mode = "v" })
                for i = 1, 3000 do weak["f" .. i] = function() return i end end
                return weak
              end,
            }
            """);

        var keys = _fx.Runtime.Invoke(_fx.Export(module, "keys"), []).AsMap();
        var values = _fx.Runtime.Invoke(_fx.Export(module, "values"), []).AsMap();

        Assert.That(keys["s"].AsNumber(), Is.EqualTo(2d));
        Assert.That(keys["7"].AsString(), Is.EqualTo("seven"));
        Assert.That(keys, Has.Count.EqualTo(2), "a table key has no string form and is skipped");
        Assert.That(values.Count, Is.LessThanOrEqualTo(3000));
        Assert.That(values.Values.All(v => v.Kind == ScriptValueKind.Function), Is.True);
    }

    [Test]
    public void HostLists_OverTheTableLimit_BecomeNil_InsteadOfRaisingTableOverflow()
    {
        var module = _fx.Load("big", "return { kind = function(x) return type(x) end, size = function(x) return #x end }");
        var tooMany = ScriptValue.List(Enumerable.Repeat(ScriptValue.Null, LuaValues.MaxNodes + 1).ToArray());
        var justFits = ScriptValue.List(Enumerable.Repeat(ScriptValue.From(1d), 1000).ToArray());

        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "kind"), [tooMany]).AsString(), Is.EqualTo("nil"));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "size"), [justFits]).AsNumber(), Is.EqualTo(1000d));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "kind"), [justFits]).AsString(), Is.EqualTo("table"));
    }

    [Test]
    [CancelAfter(20000)]
    public void HostValues_WithSharedSubstructure_AreBoundedInTime_WhenPushed()
    {
        // 2^40 paths through shared sub-values: pushing must stop at the node budget, not hang the host.
        var value = ScriptValue.From(1d);
        for (var i = 0; i < 40; i++)
            value = ScriptValue.List([value, value]);
        var module = _fx.Load("dag", "return { kind = function(x) return type(x) end }");

        var kind = _fx.Runtime.Invoke(_fx.Export(module, "kind"), [value]);

        Assert.That(kind.AsString(), Is.EqualTo("table"));
    }

    [Test]
    public void HostValues_NestedDeeperThanTheLimit_BecomeNilAtTheLimit()
    {
        var value = ScriptValue.From(1d);
        for (var i = 0; i < 100; i++)
            value = ScriptValue.List([value]);
        var module = _fx.Load("deep", "return { depth = function(x) local d = 0 while type(x) == 'table' do d = d + 1 x = x[1] end return d end }");

        var depth = _fx.Runtime.Invoke(_fx.Export(module, "depth"), [value]).AsNumber();

        Assert.That(depth, Is.EqualTo(LuaValues.MaxDepth + 1));
    }

    [Test]
    public void NumbersAndNilArgumentsToHostFunctions_AreConvertedInPlace_WithoutDisturbingTheCaller()
    {
        var result = _fx.Call("""
            return { f = function()
              local n = 12.5
              local ok1 = ksa_host.console_exec(n)          -- a number where a string is expected is converted in the argument slot only
              local ok2 = ksa_host.state_set(7, { 1.5 })
              local ok3 = ksa_host.term_write(1, 99)
              return { n = n, type = type(n), ok1 = ok1, ok2 = ok2, ok3 = ok3 }
            end }
            """, "f").AsMap();

        Assert.That(result["n"].AsNumber(), Is.EqualTo(12.5), "the caller's variable is still a number");
        Assert.That(result["type"].AsString(), Is.EqualTo("number"));
        Assert.That(result["ok1"].AsBool(), Is.True);
        Assert.That(Host.ConsoleCommands, Is.EqualTo(new[] { "12.5" }));
        Assert.That(Host.State.Keys, Is.EqualTo(new[] { "7" }));
        Assert.That(Host.TermWrites.Single().Bytes, Is.EqualTo("99"u8.ToArray()));
    }
}
