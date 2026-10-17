using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

[TestFixture]
public sealed class LoadTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp() => _fx = new LuaRuntimeFixture();

    [TearDown]
    public void TearDown() => _fx.Dispose();

    [Test]
    public void TopLevelWait_ThrowsScriptLoadException_AndCancelsTheWait()
    {
        var source = _fx.Write("waits-at-top", "ksa.console.log('before')\nksa.sleep(1)\nksa.console.log('after')\nreturn {}");

        var ex = Assert.Throws<ScriptLoadException>(() => _fx.Runtime.Load(source))!;

        Assert.That(ex.Message, Is.EqualTo("top-level code cannot wait; move waits into main(ctx) or a hook"));
        Assert.That(ex.File, Is.EqualTo(source.Path));
        Assert.That(ex.Line, Is.EqualTo(0));
        Assert.That(_fx.Host.Logs.Select(l => l.Message), Is.EqualTo(new[] { "before" }));
        Assert.That(_fx.Host.Waits.Single().Wait.Task.IsCanceled, Is.True);
        Assert.That(_fx.Runtime.Advance(TimeSpan.FromMilliseconds(10)), Is.False);
    }

    [Test]
    public void TopLevelRuntimeError_ThrowsWithFileAndLine()
    {
        var source = _fx.Write("explodes", "local x = nil\nx.y = 1\nreturn {}");

        var ex = Assert.Throws<ScriptLoadException>(() => _fx.Runtime.Load(source))!;

        Assert.That(ex.File, Is.EqualTo(source.Path));
        Assert.That(ex.Line, Is.EqualTo(2));
        Assert.That(ex.Snippet, Is.EqualTo("x.y = 1"));
        Assert.That(ex.Message, Does.Contain("attempt to index"));
        Assert.That(ex.InnerException, Is.InstanceOf<ScriptRuntimeException>());
    }

    [Test]
    public void TopLevelError_InARequiredModule_PointsAtThatModule()
    {
        var module = _fx.Write("lib/broken", "local a = 1\nerror('module exploded')\n");
        var source = _fx.Write("uses-broken", "require('lib.broken')\nreturn {}");

        var ex = Assert.Throws<ScriptLoadException>(() => _fx.Runtime.Load(source))!;

        Assert.That(ex.File, Is.EqualTo(module.Path));
        Assert.That(ex.Line, Is.EqualTo(2));
        Assert.That(ex.Message, Does.Contain("module exploded"));
    }

    [Test]
    public void SyntaxError_InARequiredModule_PointsAtThatModule()
    {
        var module = _fx.Write("lib/syntax", "return {\n  a = ,\n}");
        var source = _fx.Write("uses-syntax", "local m = require('lib.syntax')\nreturn {}");

        var ex = Assert.Throws<ScriptLoadException>(() => _fx.Runtime.Load(source))!;

        Assert.That(ex.File, Is.EqualTo(module.Path));
        Assert.That(ex.Line, Is.EqualTo(2));
    }

    [Test]
    public void LoadingTheSameNameTwice_CreatesIndependentModules()
    {
        const string code = "local n = 0 return { next = function() n = n + 1 return n end }";
        var first = _fx.Load("twice", code);
        var second = _fx.Load("twice", code);

        Assert.That(second.Token, Is.Not.EqualTo(first.Token));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(first, "next"), []).AsNumber(), Is.EqualTo(1d));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(first, "next"), []).AsNumber(), Is.EqualTo(2d));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(second, "next"), []).AsNumber(), Is.EqualTo(1d));

        _fx.Runtime.Unload(first);
        Assert.That(_fx.Runtime.Invoke(_fx.Export(second, "next"), []).AsNumber(), Is.EqualTo(2d), "unloading one module leaves the other alone");
    }

    [Test]
    public void FailedLoad_ReleasesItsCallables_AndLeavesTheRuntimeUsable()
    {
        var source = _fx.Write("half-loaded", "ksa.on('stage', function() end)\nerror('after subscribing')");

        Assert.Throws<ScriptLoadException>(() => _fx.Runtime.Load(source));

        var orphan = _fx.Host.Subscriptions.Single().Fn;
        Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(orphan, []));
        Assert.That(_fx.Call("return { f = function() return 1 end }", "f").AsNumber(), Is.EqualTo(1d));
    }

    [Test]
    public void ModuleReturningNonTable_HasNoExports()
    {
        var module = _fx.Load("no-exports", "local x = 5");

        Assert.That(_fx.Runtime.Exports(module), Is.Empty);
    }

    [Test]
    public void ExportsReturnedFromTheChunk_IgnoreNonFunctionsAndNonStringKeys()
    {
        var module = _fx.Load("mixed-exports", "return { fn = function() end, number = 1, [1] = function() end, nested = { f = function() end } }");

        Assert.That(_fx.Runtime.Exports(module), Is.EqualTo(new[] { "fn" }));
    }

    [Test]
    public void ChunkNames_UseScriptsPrefix_AndMapBackToTheSourcePath()
    {
        var module = _fx.Write("deep/nested/script", "return { f = function() error('here') end }");
        var loaded = _fx.Runtime.Load(module);

        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(loaded, "f"), []))!;

        Assert.That(ex.Message, Does.StartWith("scripts/deep/nested/script.lua:1:"));
        Assert.That(ex.File, Is.EqualTo(module.Path));
    }

    [Test]
    public void SourceOutsideTheScriptsDirectory_UsesItsFileNameAndKeepsItsRealPath()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"kl-{Guid.NewGuid():N}"[..13] + ".lua");
        File.WriteAllText(outside, "return { f = function() error('outside') end }");
        try
        {
            var loaded = _fx.Runtime.Load(new ScriptSource("outside", outside, File.ReadAllText(outside), ScriptLanguages.Lua));

            var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(loaded, "f"), []))!;

            Assert.That(ex.Message, Does.StartWith("scripts/" + Path.GetFileName(outside) + ":1:"));
            Assert.That(ex.File, Is.EqualTo(outside));
            Assert.That(ex.Line, Is.EqualTo(1));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Test]
    public void LongChunkNames_AreTruncatedByLua_ButStillMapBackToTheFile()
    {
        // Lua shortens chunk names over 59 characters to "..." + the last 56; the runtime recovers the file from its known chunks.
        var source = _fx.Write("a-rather-long-directory-name/another-long-directory-name/script-with-a-long-name", "return { f = function() error('deep') end }");
        var loaded = _fx.Runtime.Load(source);

        var ex = Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(_fx.Export(loaded, "f"), []))!;

        Assert.That(ex.Message, Does.StartWith("..."));
        Assert.That(ex.File, Is.EqualTo(source.Path));
        Assert.That(ex.Line, Is.EqualTo(1));
    }

    [Test]
    public void LongChunkNames_InSyntaxErrors_MapBackToTheFile()
    {
        var source = _fx.Write("a-rather-long-directory-name/another-long-directory-name/script-with-a-long-name", "\n\nlocal = =");

        var ex = Assert.Throws<ScriptLoadException>(() => _fx.Runtime.Load(source))!;

        Assert.That(ex.File, Is.EqualTo(source.Path));
        Assert.That(ex.Line, Is.EqualTo(3));
    }

    [Test]
    public void Utf8Source_WithBom_AndShebang_Loads()
    {
        var module = _fx.Load("bom", "﻿return { f = function() return 'héllo' end }");

        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "f"), []).AsString(), Is.EqualTo("héllo"));
    }

    [Test]
    public void Lifecycle_InitializeIsIdempotent_AndGuardsAgainstMisuse()
    {
        using var rt = new LuaRuntime();
        var source = new ScriptSource("early", "early.lua", "return {}", ScriptLanguages.Lua);

        Assert.Throws<InvalidOperationException>(() => rt.Load(source));
        Assert.Throws<ScriptRuntimeException>(() => rt.Invoke(ScriptCallable.Create("lua", 1, null, _ => { }), []));
        Assert.That(rt.Advance(TimeSpan.FromMilliseconds(1)), Is.False);
        Assert.DoesNotThrow(() => rt.Interrupt());
        Assert.Throws<ArgumentNullException>(() => rt.Initialize(null!, new RuntimeOptions("", "", 0, true, null)));
        Assert.Throws<ArgumentNullException>(() => rt.Initialize(new FakeHostFacade(), null!));

        var host = new FakeHostFacade();
        var options = new RuntimeOptions(_fx.ScriptsDir, "", 0, true, null);
        rt.Initialize(host, options);
        var version = rt.Stats.Version;
        rt.Initialize(new FakeHostFacade(), options with { JitEnabled = false });

        Assert.That(rt.Stats.Version, Is.EqualTo(version));
        Assert.That(rt.Stats.JitEnabled, Is.True, "the second Initialize is a no-op");

        rt.Dispose();
        rt.Dispose();
        Assert.Throws<ObjectDisposedException>(() => rt.Load(source));
        Assert.Throws<ObjectDisposedException>(() => rt.Initialize(host, options));
        Assert.That(rt.Advance(TimeSpan.FromMilliseconds(1)), Is.False);
        Assert.That(rt.Stats.HeapBytes, Is.EqualTo(0));
    }

    [Test]
    public void JitDisabled_IsReportedInStats()
    {
        using var fx = new LuaRuntimeFixture(jitEnabled: false);

        Assert.That(fx.Runtime.Stats.JitEnabled, Is.False);
        Assert.That(fx.Call("return { f = function() return jit.status() end }", "f").AsBool(), Is.False);
    }

    [Test]
    public void UnavailableRuntime_ReportsTheReason_AndRefusesWork()
    {
        using var rt = new LuaRuntime(() => "LuaJIT native library not found. Probed: nowhere");
        var source = new ScriptSource("x", "x.lua", "return {}", ScriptLanguages.Lua);

        Assert.That(rt.IsAvailable, Is.False);
        Assert.That(rt.UnavailableReason, Does.StartWith("LuaJIT native library not found"));

        var host = new FakeHostFacade();
        Assert.DoesNotThrow(() => rt.Initialize(host, new RuntimeOptions(_fx.ScriptsDir, "", 0, true, null)), "Initialize never throws for native problems");

        var load = Assert.Throws<ScriptLoadException>(() => rt.Load(source))!;
        Assert.That(load.Message, Does.StartWith("LuaJIT native library not found"));
        Assert.That(load.File, Is.EqualTo("x.lua"));
        Assert.Throws<ScriptRuntimeException>(() => rt.Invoke(ScriptCallable.Create("lua", 1, null, _ => { }), []));
        Assert.That(rt.Advance(TimeSpan.FromMilliseconds(1)), Is.False);
        Assert.That(rt.Stats, Is.EqualTo(new RuntimeStats(0, 0, 0, false, "")));
        Assert.DoesNotThrow(() => rt.Interrupt());
        Assert.DoesNotThrow(() => rt.Unload(new ScriptModule("lua", "x", 1)));
        Assert.That(rt.Exports(new ScriptModule("lua", "x", 1)), Is.Empty);
        Assert.That(host.EnteredScripts, Is.Empty);
    }
}
