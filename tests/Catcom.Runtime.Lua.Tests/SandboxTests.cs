using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

[TestFixture]
public sealed class SandboxTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp() => _fx = new LuaRuntimeFixture();

    [TearDown]
    public void TearDown() => _fx.Dispose();

    [Test]
    public void RemovedGlobals_AreNil()
    {
        var result = _fx.Call("""
            return { f = function()
              return {
                io = type(io), package = type(package), debug = type(debug), module = type(module),
                load = type(load), loadstring = type(loadstring), loadfile = type(loadfile), dofile = type(dofile),
                setfenv = type(setfenv), getfenv = type(getfenv), newproxy = type(newproxy), gcinfo = type(gcinfo),
                ffi = type(ffi), jit_off = type(jit.off), jit_on = type(jit.on), jit_util = type(jit.util),
                os_execute = type(os.execute), os_exit = type(os.exit), os_getenv = type(os.getenv),
                os_remove = type(os.remove), os_rename = type(os.rename), os_tmpname = type(os.tmpname),
                os_setlocale = type(os.setlocale), string_dump = type(string.dump), method_dump = type(("x").dump),
                require_ffi_global = type(rawget(_G, "ffi")),
              }
            end }
            """, "f");

        foreach (var (name, value) in result.AsMap())
            Assert.That(value.AsString(), Is.EqualTo("nil"), name);
        Assert.That(result.AsMap(), Has.Count.GreaterThan(20));
    }

    [Test]
    public void AllowedGlobals_ArePresent()
    {
        var result = _fx.Call("""
            return { f = function()
              return {
                assert = type(assert), error = type(error), ipairs = type(ipairs), pairs = type(pairs), next = type(next),
                pcall = type(pcall), xpcall = type(xpcall), select = type(select), tonumber = type(tonumber),
                tostring = type(tostring), type = type(type), unpack = type(unpack), rawequal = type(rawequal),
                rawget = type(rawget), rawset = type(rawset), setmetatable = type(setmetatable), getmetatable = type(getmetatable),
                math = type(math.floor), string = type(string.format), table = type(table.concat), bit = type(bit.band),
                coroutine = type(coroutine.wrap), clock = type(os.clock), date = type(os.date), time = type(os.time),
                difftime = type(os.difftime), jit_status = type(jit.status), jit_version = type(jit.version),
                count = type(collectgarbage("count")), version = _VERSION, print = type(print), require = type(require),
                self = tostring(_G == _G._G), ksa_host = type(ksa_host.log), ksa = type(ksa),
              }
            end }
            """, "f").AsMap();

        foreach (var name in new[] { "assert", "error", "ipairs", "pairs", "next", "pcall", "xpcall", "select", "tonumber", "tostring", "type", "unpack", "rawequal", "rawget", "rawset", "setmetatable", "getmetatable", "print", "require" })
            Assert.That(result[name].AsString(), Is.EqualTo("function"), name);
        foreach (var name in new[] { "math", "string", "table", "bit", "coroutine", "clock", "date", "time", "difftime", "jit_status", "ksa_host" })
            Assert.That(result[name].AsString(), Is.EqualTo("function"), name);
        Assert.That(result["jit_version"].AsString(), Is.EqualTo("string"));
        Assert.That(result["count"].AsString(), Is.EqualTo("number"));
        Assert.That(result["version"].AsString(), Is.EqualTo("Lua 5.1"));
        Assert.That(result["self"].AsString(), Is.EqualTo("true"));
        Assert.That(result["ksa"].AsString(), Is.EqualTo("table"));
    }

    [Test]
    public void StringMetatable_IsLocked()
    {
        var result = _fx.Call("""
            return { f = function()
              local ok, err = pcall(function() getmetatable("").__index = {} end)
              return { mt = tostring(getmetatable("")), ok = ok, upper = ("abc"):upper(), len = ("abc"):len() }
            end }
            """, "f").AsMap();

        Assert.That(result["mt"].AsString(), Is.EqualTo("false"));
        Assert.That(result["ok"].AsBool(), Is.False);
        Assert.That(result["upper"].AsString(), Is.EqualTo("ABC"));
        Assert.That(result["len"].AsNumber(), Is.EqualTo(3d));
    }

    [Test]
    public void LibraryTables_AreCopiedPerScript()
    {
        var a = _fx.Load("mutator", "string.format = nil math.floor = 42 table.insert = nil return { f = function() return type(string.format) end }");
        var b = _fx.Load("victim", "return { f = function() return type(string.format) .. type(math.floor) .. type(table.insert) end }");

        Assert.That(_fx.Runtime.Invoke(_fx.Export(a, "f"), []).AsString(), Is.EqualTo("nil"));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(b, "f"), []).AsString(), Is.EqualTo("functionfunctionfunction"));
    }

    [Test]
    public void GlobalsAreIsolated_BetweenScripts()
    {
        var a = _fx.Load("a", "shared = 'from-a' return { f = function() return shared end }");
        var b = _fx.Load("b", "return { f = function() return tostring(shared) end }");

        Assert.That(_fx.Runtime.Invoke(_fx.Export(a, "f"), []).AsString(), Is.EqualTo("from-a"));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(b, "f"), []).AsString(), Is.EqualTo("nil"));
    }

    [Test]
    public void KsaHost_IsPerScriptCopy()
    {
        var a = _fx.Load("tamper", "ksa_host.log = nil return {}");
        var b = _fx.Load("honest", "return { f = function() ksa_host.log(1, 'still works') end }");
        _fx.Runtime.Invoke(_fx.Export(b, "f"), []);

        Assert.That(_fx.Host.Logs.Select(l => l.Message), Does.Contain("still works"));
        Assert.That(a.Name, Is.EqualTo("tamper"));
    }

    [Test]
    public void JitProxy_IsReadOnly_AndStatusWorks()
    {
        var result = _fx.Call("""
            return { f = function()
              local ok = pcall(function() jit.status = nil end)
              local ok2 = pcall(function() jit.extra = 1 end)
              return { writable = ok or ok2, status = jit.status(), mt = tostring(getmetatable(jit)) }
            end }
            """, "f").AsMap();

        Assert.That(result["writable"].AsBool(), Is.False);
        Assert.That(result["status"].AsBool(), Is.True);
        Assert.That(result["mt"].AsString(), Is.EqualTo("false"));
    }

    [Test]
    public void StringRep_IsCapped_ButOrdinaryUseWorks()
    {
        var module = _fx.Load("rep", """
            return {
              ok = function() return { ("ab"):rep(3), string.rep("x", 0), string.rep("ab", 2, ","), #string.rep("x", 1024 * 1024), string.rep(5, 3) } end,
              huge = function() return string.rep("x", 2 ^ 40) end,
              method = function() return ("xx"):rep(2 ^ 30) end,
              sep = function() return string.rep("x", 2 ^ 20, ("y"):rep(1024 * 1024)) end,
            }
            """);

        var ok = _fx.Runtime.Invoke(_fx.Export(module, "ok"), []).AsList();
        Assert.That(ok.Select(v => v.Kind == Catcom.Core.Values.ScriptValueKind.Number ? v.AsNumber().ToString() : v.AsString()),
            Is.EqualTo(new[] { "ababab", "", "ab,ab", "1048576", "555" }));
        foreach (var name in new[] { "huge", "method", "sep" })
        {
            var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, name), []))!;
            Assert.That(ex.Message, Does.Contain("result too large"), name);
        }
    }

    [Test]
    public void CollectGarbage_OnlyCount()
    {
        var module = _fx.Load("gc", "return { count = function() return collectgarbage('count') end, collect = function() collectgarbage('collect') end, bare = function() collectgarbage() end }");

        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "count"), []).AsNumber(), Is.GreaterThan(0));
        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "collect"), []))!;
        Assert.That(ex.Message, Does.Contain("only 'count' is allowed"));
        Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "bare"), []));
    }

    [Test]
    public void Print_GoesToHostLog_TabJoined()
    {
        var module = _fx.Load("printer", "return { f = function() print('a', 1, nil, true) end }");

        _fx.Runtime.Invoke(_fx.Export(module, "f"), []);

        var log = _fx.Host.Logs.Single();
        Assert.That(log.Level, Is.EqualTo(1));
        Assert.That(log.Message, Is.EqualTo("a\t1\tnil\ttrue"));
        Assert.That(log.Script, Is.Empty, "Invoke is attributed by the scheduler, not by the runtime");
    }

    [Test]
    public void RequireFfi_Fails()
    {
        var module = _fx.Load("ffi-user", "return { f = function() return require('ffi') end }");

        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "f"), []))!;

        Assert.That(ex.Message, Does.Contain("ffi"));
    }

    [Test]
    public void BinaryChunk_IsRefused()
    {
        var source = _fx.Write("binary", "\u001bLJ\u0002\u0000garbage");

        var ex = Assert.Throws<ScriptLoadException>(() => _fx.Runtime.Load(source))!;

        Assert.That(ex.Message, Does.Contain("wrong mode"));
        Assert.That(ex.File, Is.EqualTo(source.Path));
    }

    [Test]
    public void StackOverflow_IsAScriptError_AndTheRuntimeSurvives()
    {
        var module = _fx.Load("recursive", "local function f(n) return 1 + f(n + 1) end return { boom = function() return f(1) end, ok = function() return 'fine' end }");

        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "boom"), []))!;

        Assert.That(ex.Message, Does.Contain("stack overflow"));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "ok"), []).AsString(), Is.EqualTo("fine"));
    }

    [Test]
    public void ErrorWithTableValue_IsReportedAsText()
    {
        var module = _fx.Load("error-table", "return { f = function() error({ code = 7 }) end, g = function() error() end }");

        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "f"), []))!;
        Assert.That(ex.Message, Does.StartWith("table:"));
        Assert.That(ex.Traceback, Does.Contain("stack traceback"));

        Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "g"), []));
    }

    [Test]
    public void YieldOutsideKsaWait_IsAnError()
    {
        var module = _fx.Load("yielder", "return { f = function() coroutine.yield(5) end, g = function() coroutine.yield({}) end }");

        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "f"), []))!;
        Assert.That(ex.Message, Does.Contain("coroutine.yield is only allowed for ksa waits"));
        Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "g"), []));
        Assert.That(_fx.Runtime.Advance(TimeSpan.FromMilliseconds(10)), Is.False);
    }

    [Test]
    public void ScriptCoroutines_WorkNormally()
    {
        var result = _fx.Call("""
            return { f = function()
              local co = coroutine.wrap(function(a) local b = coroutine.yield(a + 1) return b * 2 end)
              return { co(1), co(10) }
            end }
            """, "f");

        Assert.That(result.AsList().Select(v => v.AsNumber()), Is.EqualTo(new[] { 2d, 20d }));
    }
}
