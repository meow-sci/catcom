using KsaScripting.Core.Runtime;

namespace KsaScripting.Runtime.Lua.Tests;

[TestFixture]
public sealed class RequireTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp() => _fx = new LuaRuntimeFixture();

    [TearDown]
    public void TearDown() => _fx.Dispose();

    [Test]
    public void Require_LoadsModulesFromTheScriptsDirectory_AndCaches()
    {
        _fx.Write("lib/util", "loads = (loads or 0) + 1 local M = {} function M.double(x) return x * 2 end function M.loads() return loads end return M");
        var module = _fx.Load("main", """
            local a = require("lib.util")
            local b = require("lib.util")
            return { f = function(x) return { a.double(x), a == b, a.loads(), require("ksa") == ksa } end }
            """);

        var result = _fx.Runtime.Invoke(_fx.Export(module, "f"), [KsaScripting.Core.Values.ScriptValue.From(21d)]).AsList();

        Assert.That(result[0].AsNumber(), Is.EqualTo(42d));
        Assert.That(result[1].AsBool(), Is.True);
        Assert.That(result[2].AsNumber(), Is.EqualTo(1d), "the module body ran once");
        Assert.That(result[3].AsBool(), Is.True, "require('ksa') is the environment's ksa table");
    }

    [Test]
    public void RequiredModules_RunInTheScriptsSandbox()
    {
        _fx.Write("lib/probe", "return { io = type(io), has_ksa = type(ksa), package = type(package) }");
        var result = _fx.Call("""
            local p = require("lib.probe")
            return { f = function() return { p.io, p.has_ksa, p.package } end }
            """, "f").AsList();

        Assert.That(result.Select(v => v.AsString()), Is.EqualTo(new[] { "nil", "table", "nil" }));
    }

    [Test]
    public void ModulesAreNotSharedBetweenScripts()
    {
        _fx.Write("lib/counter", "local n = 0 return function() n = n + 1 return n end");
        var a = _fx.Load("a", "local c = require('lib.counter') return { f = function() return c() end }");
        var b = _fx.Load("b", "local c = require('lib.counter') return { f = function() return c() end }");

        Assert.That(_fx.Runtime.Invoke(_fx.Export(a, "f"), []).AsNumber(), Is.EqualTo(1d));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(a, "f"), []).AsNumber(), Is.EqualTo(2d));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(b, "f"), []).AsNumber(), Is.EqualTo(1d));
    }

    [TestCase("../outside")]
    [TestCase("a/b")]
    [TestCase("a..b")]
    [TestCase(".hidden")]
    [TestCase("trailing.")]
    [TestCase("")]
    [TestCase("with space")]
    [TestCase("nul\\0byte")]
    public void InvalidModuleNames_AreRefused(string name)
    {
        var module = _fx.Load("bad-name", "return { f = function(n) return require(n) end }");

        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "f"), [KsaScripting.Core.Values.ScriptValue.From(name)]))!;

        Assert.That(ex.Message, Does.Contain("invalid module name").Or.Contain("not found"));
    }

    [Test]
    public void MissingModule_IsAnError()
    {
        var module = _fx.Load("missing", "return { f = function() return require('nope') end }");

        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "f"), []))!;

        Assert.That(ex.Message, Does.Contain("module 'nope' not found"));
    }

    [Test]
    public void RequireWithNonStringName_IsAnError()
    {
        var module = _fx.Load("non-string", "return { f = function() return require({}) end }");

        Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "f"), []));
    }

    [Test]
    public void CircularRequire_IsDetected()
    {
        _fx.Write("lib/a", "return require('lib.b')");
        _fx.Write("lib/b", "return require('lib.a')");
        var module = _fx.Load("cycle", "return { f = function() return require('lib.a') end }");

        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "f"), []))!;

        Assert.That(ex.Message, Does.Contain("loop"));
    }

    [Test]
    public void BinaryModules_AreRefused()
    {
        _fx.Write("lib/binary", "\u001bLJ\u0002\u0000garbage");
        var module = _fx.Load("loads-binary", "return { f = function() return require('lib.binary') end }");

        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "f"), []))!;

        Assert.That(ex.Message, Does.Contain("wrong mode"));
    }

    [Test]
    public void SymlinkEscapingTheScriptsDirectory_IsRefused()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("creating symbolic links needs elevated rights on Windows");

        var outside = Path.Combine(Path.GetTempPath(), $"ksa-lua-secret-{Guid.NewGuid():N}.lua");
        File.WriteAllText(outside, "return 'pwned'");
        var directoryTarget = Directory.CreateTempSubdirectory("ksa-lua-secret-dir-");
        File.WriteAllText(Path.Combine(directoryTarget.FullName, "inner.lua"), "return 'pwned-dir'");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_fx.ScriptsDir, "linked.lua"), outside);
            Directory.CreateSymbolicLink(Path.Combine(_fx.ScriptsDir, "linkeddir"), directoryTarget.FullName);
            var module = _fx.Load("escape", "return { file = function() return require('linked') end, dir = function() return require('linkeddir.inner') end }");

            var file = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "file"), []))!;
            var dir = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(module, "dir"), []))!;

            Assert.That(file.Message, Does.Contain("outside the scripts directory"));
            Assert.That(dir.Message, Does.Contain("outside the scripts directory"));
        }
        finally
        {
            File.Delete(outside);
            directoryTarget.Delete(recursive: true);
        }
    }

    [Test]
    public void ModuleReader_RefusesTraversalAndMissingRoot()
    {
        Assert.That(Interop.ModuleReader.TryRead(_fx.ScriptsDir, "x", out _, out _, out _, out var error), Is.False);
        Assert.That(error, Does.Contain("not found"));
        Assert.That(Interop.ModuleReader.TryRead(_fx.ScriptsDir, "..", out _, out _, out _, out error), Is.False);
        Assert.That(Interop.ModuleReader.TryRead(string.Empty, "x", out _, out _, out _, out error), Is.False);

        var written = _fx.Write("present", "return 1");
        Assert.That(Interop.ModuleReader.TryRead(_fx.ScriptsDir, "present", out var source, out var chunk, out var fullPath, out _), Is.True);
        Assert.That(chunk, Is.EqualTo("@scripts/present.lua"));
        Assert.That(fullPath, Is.EqualTo(written.Path));
        Assert.That(System.Text.Encoding.UTF8.GetString(source), Is.EqualTo("return 1"));
    }
}
