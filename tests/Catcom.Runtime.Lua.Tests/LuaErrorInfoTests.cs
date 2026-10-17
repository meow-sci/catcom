namespace Catcom.Runtime.Lua.Tests;

[TestFixture]
public sealed class LuaErrorInfoTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "scripts-dir");

    [Test]
    public void Parse_FindsTheFirstScriptsLocation_AndMapsItUnderTheScriptsDirectory()
    {
        var info = LuaErrorInfo.Parse("scripts/lib/tui.lua:42: attempt to index a nil value\nstack traceback:\n\tscripts/main.lua:3: in function <scripts/main.lua:1>", Dir);

        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Value.Line, Is.EqualTo(42));
        Assert.That(info.Value.File, Is.EqualTo(Path.Combine(Dir, "lib", "tui.lua")));
        Assert.That(info.Value.ChunkName, Is.EqualTo("scripts/lib/tui.lua"));
    }

    [Test]
    public void Parse_FindsALocationInsideTheTraceback()
    {
        var info = LuaErrorInfo.Parse("error object\nstack traceback:\n\t[C]: in function 'error'\n\tscripts/ascent.lua:7: in function 'main'", Dir);

        Assert.That(info!.Value.Line, Is.EqualTo(7));
        Assert.That(info.Value.File, Is.EqualTo(Path.Combine(Dir, "ascent.lua")));
    }

    [Test]
    public void Parse_FallsBackToPlainLuaChunkNames()
    {
        var info = LuaErrorInfo.Parse("lib/x.lua:5: boom", Dir);

        Assert.That(info!.Value.Line, Is.EqualTo(5));
        Assert.That(info.Value.File, Is.EqualTo(Path.Combine(Dir, "lib", "x.lua")));
    }

    [Test]
    public void Parse_PrefersKnownChunkPaths()
    {
        var known = new Dictionary<string, string> { ["scripts/x.lua"] = "/elsewhere/x.lua" };

        var info = LuaErrorInfo.Parse("scripts/x.lua:9: boom", Dir, known);

        Assert.That(info!.Value.File, Is.EqualTo("/elsewhere/x.lua"));
    }

    [TestCase("")]
    [TestCase("attempt to call a nil value")]
    [TestCase("ksa:12: prelude error")]
    [TestCase("=ksa-runtime:3: bootstrap")]
    public void Parse_ReturnsNull_WithoutAScriptLocation(string message)
    {
        Assert.That(LuaErrorInfo.Parse(message, Dir), Is.Null);
    }

    [Test]
    public void MessageOf_StripsTheTraceback()
    {
        Assert.That(LuaErrorInfo.MessageOf("a: b\nstack traceback:\n\t[C]: x"), Is.EqualTo("a: b"));
        Assert.That(LuaErrorInfo.MessageOf("plain"), Is.EqualTo("plain"));
        Assert.That(LuaErrorInfo.MessageOf("two\nlines\nstack traceback:\n\tx"), Is.EqualTo("two\nlines"));
    }
}
