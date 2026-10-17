using Catcom.Core.Hosting;
using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Runtime;

[TestFixture]
public sealed class RuntimeSetTests
{
    private sealed class FakeRuntime(string id, string language, bool available = true) : IScriptRuntime
    {
        public string Id => id;

        public string Language => language;

        public bool IsAvailable => available;

        public string? UnavailableReason => available ? null : "native library missing";

        public RuntimeStats Stats => new(0, 0, 0, false, "fake");

        public void Initialize(IHostFacade host, RuntimeOptions options) => throw new NotSupportedException();

        public ScriptModule Load(ScriptSource source) => throw new NotSupportedException();

        public IReadOnlyList<string> Exports(ScriptModule module) => throw new NotSupportedException();

        public ScriptCallable GetExport(ScriptModule module, string name) => throw new NotSupportedException();

        public ScriptValue Invoke(ScriptCallable fn, ReadOnlySpan<ScriptValue> args) => throw new NotSupportedException();

        public bool Advance(TimeSpan budget) => false;

        public void Unload(ScriptModule module)
        {
        }

        public void Interrupt()
        {
        }

        public void Dispose()
        {
        }
    }

    private static (RuntimeSet Set, FakeRuntime Lua, FakeRuntime V8) Both()
    {
        var lua = new FakeRuntime("lua", ScriptLanguages.Lua);
        var v8 = new FakeRuntime("v8", ScriptLanguages.TypeScript);
        return (new RuntimeSet([lua, v8]), lua, v8);
    }

    [Test]
    public void All_KeepsRegistrationOrder()
    {
        var (set, lua, v8) = Both();
        Assert.That(set.All, Is.EqualTo(new IScriptRuntime[] { lua, v8 }));
    }

    [Test]
    public void ForId_IsCaseInsensitive()
    {
        var (set, lua, v8) = Both();
        Assert.Multiple(() =>
        {
            Assert.That(set.ForId("lua"), Is.SameAs(lua));
            Assert.That(set.ForId("V8"), Is.SameAs(v8));
            Assert.That(set.ForId("jint"), Is.Null);
        });
    }

    [Test]
    public void ForLanguage_IsCaseInsensitive()
    {
        var (set, lua, v8) = Both();
        Assert.Multiple(() =>
        {
            Assert.That(set.ForLanguage("lua"), Is.SameAs(lua));
            Assert.That(set.ForLanguage("TypeScript"), Is.SameAs(v8));
            Assert.That(set.ForLanguage("python"), Is.Null);
        });
    }

    [TestCase("scripts/ascent.lua", "lua")]
    [TestCase("ASCENT.LUA", "lua")]
    [TestCase("a/b/c.ts", "v8")]
    [TestCase("c.TS", "v8")]
    public void ForPath_PicksTheRuntimeByExtension(string path, string expectedId)
    {
        var (set, _, _) = Both();
        Assert.That(set.ForPath(path)?.Id, Is.EqualTo(expectedId));
    }

    [TestCase("a.js")]
    [TestCase("a.tsx")]
    [TestCase("a")]
    [TestCase("")]
    [TestCase("lua")]
    public void ForPath_UnknownExtensions_AreNull(string path)
    {
        var (set, _, _) = Both();
        Assert.That(set.ForPath(path), Is.Null);
    }

    [Test]
    public void ForPath_WithoutAMatchingRuntime_IsNull()
    {
        var set = new RuntimeSet([new FakeRuntime("lua", ScriptLanguages.Lua)]);
        Assert.Multiple(() =>
        {
            Assert.That(set.ForPath("a.ts"), Is.Null);
            Assert.That(set.ForPath("a.lua"), Is.Not.Null);
        });
    }

    [Test]
    public void UnavailableRuntimes_AreStillReturned()
    {
        var down = new FakeRuntime("v8", ScriptLanguages.TypeScript, available: false);
        var set = new RuntimeSet([down]);
        Assert.Multiple(() =>
        {
            Assert.That(set.ForId("v8"), Is.SameAs(down));
            Assert.That(set.ForPath("x.ts"), Is.SameAs(down));
            Assert.That(set.ForPath("x.ts")!.IsAvailable, Is.False);
            Assert.That(down.UnavailableReason, Is.Not.Null);
        });
    }

    [Test]
    public void ScriptLanguages_ForPath()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptLanguages.ForPath("a.lua"), Is.EqualTo("lua"));
            Assert.That(ScriptLanguages.ForPath("a.LUA"), Is.EqualTo(ScriptLanguages.Lua));
            Assert.That(ScriptLanguages.ForPath("dir.d/a.ts"), Is.EqualTo("typescript"));
            Assert.That(ScriptLanguages.ForPath("a.js"), Is.Null);
            Assert.That(ScriptLanguages.ForPath("a.d.ts"), Is.EqualTo(ScriptLanguages.TypeScript));
            Assert.That(ScriptLanguages.ForPath(""), Is.Null);
        });
    }

    [Test]
    public void ScriptLoadException_KeepsItsProperties()
    {
        var inner = new FormatException("inner");
        var full = new ScriptLoadException("bad token", "a.lua", 3, 9, "local x = ", inner);
        var minimal = new ScriptLoadException("oops", "b.ts");
        Assert.Multiple(() =>
        {
            Assert.That(full.Message, Is.EqualTo("bad token"));
            Assert.That(full.File, Is.EqualTo("a.lua"));
            Assert.That(full.Line, Is.EqualTo(3));
            Assert.That(full.Column, Is.EqualTo(9));
            Assert.That(full.Snippet, Is.EqualTo("local x = "));
            Assert.That(full.InnerException, Is.SameAs(inner));
            Assert.That(minimal.File, Is.EqualTo("b.ts"));
            Assert.That(minimal.Line, Is.EqualTo(0));
            Assert.That(minimal.Column, Is.EqualTo(0));
            Assert.That(minimal.Snippet, Is.EqualTo(""));
            Assert.That(minimal.InnerException, Is.Null);
        });
    }

    [Test]
    public void ScriptRuntimeException_KeepsItsProperties()
    {
        var inner = new InvalidOperationException("inner");
        var simple = new ScriptRuntimeException("interrupted");
        var full = new ScriptRuntimeException("boom", "stack traceback:\n  a.lua:3", "a.lua", 3, inner);
        Assert.Multiple(() =>
        {
            Assert.That(simple.Message, Is.EqualTo("interrupted"));
            Assert.That(simple.Traceback, Is.EqualTo(""));
            Assert.That(simple.File, Is.Null);
            Assert.That(simple.Line, Is.Null);
            Assert.That(full.Message, Is.EqualTo("boom"));
            Assert.That(full.Traceback, Is.EqualTo("stack traceback:\n  a.lua:3"));
            Assert.That(full.File, Is.EqualTo("a.lua"));
            Assert.That(full.Line, Is.EqualTo(3));
            Assert.That(full.InnerException, Is.SameAs(inner));
        });
    }
}
