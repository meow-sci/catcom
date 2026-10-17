using System.Reflection;
using System.Text.RegularExpressions;
using Catcom.Core.Api;
using Catcom.Core.Commands;

namespace Catcom.Core.Tests.Api;

/// <summary>
///     Keeps the two hand-written preludes in step with <see cref="ApiSchema"/>: every member of every
///     class the preludes implement must appear in the matching file, and every action key a schema
///     member performs must appear as a string literal in both. (The plain data classes -
///     <c>EngineInfo</c>, <c>Position</c>, <c>CommandResult</c>, ... - are produced by the host, not by
///     the preludes, and are covered by <see cref="ApiSchemaTests"/> instead.)
/// </summary>
[TestFixture]
public sealed class PreludeCoverageTests
{
    private static readonly HashSet<string> ImplementedClasses =
    [
        "ksa", "Time", "VesselRef", "VesselDebug",
        "EngineRef", "RcsRef", "SolarRef", "LightRef", "DockingRef", "DecouplerRef", "AnimationRef",
        "Subscription", "Signal", "State", "Console", "Terminal",
        "BaseContext", "EventContext", "TickContext", "ServiceContext", "TerminalContext", "MainContext",
    ];

    private static string Js => File.ReadAllText(Path.Combine(RepoPaths.PreludesDirectory, "ksa.js"));

    private static string Lua => File.ReadAllText(Path.Combine(RepoPaths.PreludesDirectory, "ksa.lua"));

    private static bool ContainsIdentifier(string source, string identifier)
        => Regex.IsMatch(source, $@"(?<![A-Za-z0-9_$]){Regex.Escape(identifier)}(?![A-Za-z0-9_$])", RegexOptions.CultureInvariant);

    private static IEnumerable<(ApiClass Class, ApiMember Member)> Members(string language)
        => ApiSchema.Classes
            .Where(c => ImplementedClasses.Contains(c.Name))
            .SelectMany(c => c.Members.Select(m => (Class: c, Member: m)))
            .Where(x => x.Member.Kind != ApiKind.Event && (x.Member.Languages is null || x.Member.Languages == language));

    [Test]
    public void EveryImplementedClassIsInTheSchema()
    {
        var names = ApiSchema.Classes.Select(c => c.Name).ToHashSet();
        Assert.That(ImplementedClasses.Except(names), Is.Empty);
    }

    [Test]
    public void JavaScriptPrelude_MentionsEveryMember()
    {
        var js = Js;
        Assert.Multiple(() =>
        {
            foreach (var (cls, member) in Members("ts"))
                Assert.That(ContainsIdentifier(js, member.Name), Is.True, $"ksa.js is missing {cls.Name}.{member.Name}");
        });
    }

    [Test]
    public void LuaPrelude_MentionsEveryMember_InSnakeCase()
    {
        var lua = Lua;
        Assert.Multiple(() =>
        {
            foreach (var (cls, member) in Members("lua"))
            {
                var luaName = LuaNames.Member(member.Name);
                Assert.That(ContainsIdentifier(lua, luaName), Is.True, $"ksa.lua is missing {cls.Name}.{luaName}");
            }
        });
    }

    [Test]
    public void JavaScriptPrelude_HasTheModuleShape()
    {
        var js = Js;
        Assert.Multiple(() =>
        {
            Assert.That(js, Does.Contain("makeContext"));
            Assert.That(js, Does.Contain("globalThis.ksa"));
            Assert.That(js, Does.Contain("export { ksa };"));
            Assert.That(js, Does.Contain("export default ksa;"));
            Assert.That(Regex.IsMatch(js, @"^\s*import\s", RegexOptions.Multiline), Is.False, "the prelude must not import anything");
        });
    }

    [Test]
    public void LuaPrelude_HasTheModuleShape()
    {
        var lua = Lua;
        Assert.Multiple(() =>
        {
            Assert.That(lua, Does.Contain("make_context"));
            Assert.That(lua, Does.Contain("__exports"));
            Assert.That(lua, Does.Contain("coroutine.yield"));
            Assert.That(lua, Does.Contain("_G.ksa = ksa"));
            Assert.That(lua.TrimEnd(), Does.EndWith("return ksa"));
            var code = StripCommentsAndStrings(lua);
            foreach (var forbidden in new[] { "require", "io", "os", "debug", "ffi", "package", "loadstring", "dofile", "setfenv", "getfenv" })
                Assert.That(Regex.IsMatch(code, $@"(?<![A-Za-z0-9_.:]){forbidden}\s*[.(:]"), Is.False, $"ksa.lua must not use {forbidden}");
            Assert.That(Regex.IsMatch(code, @"\bgoto\b"), Is.False, "ksa.lua must not use goto");
            Assert.That(code, Does.Not.Contain("table.unpack"));
        });
    }

    [Test]
    public void BothPreludes_ContainEveryActionKeyAsAStringLiteral()
    {
        var keys = ApiSchema.Classes
            .SelectMany(c => c.Members)
            .Where(m => m.ActionKey is not null)
            .Select(m => m.ActionKey!)
            .Distinct()
            .ToList();
        var constants = typeof(SimActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        var js = Js;
        var lua = Lua;
        Assert.Multiple(() =>
        {
            foreach (var key in keys)
            {
                Assert.That(constants, Does.Contain(key), key);
                Assert.That(js, Does.Contain($"\"{key}\""), $"ksa.js is missing \"{key}\"");
                Assert.That(lua, Does.Contain($"\"{key}\""), $"ksa.lua is missing \"{key}\"");
            }
        });
    }

    [Test]
    public void BothPreludes_OnlyUseSchemaActionKeys()
    {
        var keys = ApiSchema.Classes.SelectMany(c => c.Members).Where(m => m.ActionKey is not null).Select(m => m.ActionKey!).ToHashSet();
        var actionLiteral = new Regex("act(?:_solver)?\\(\\s*\"([a-z_]+\\.[a-z_]+)\"", RegexOptions.CultureInvariant);
        Assert.Multiple(() =>
        {
            foreach (var (file, source) in new[] { ("ksa.js", Js), ("ksa.lua", Lua) })
                foreach (Match match in actionLiteral.Matches(source))
                    Assert.That(keys, Does.Contain(match.Groups[1].Value), $"{file} performs an action the schema does not declare");
        });
    }

    private static string StripCommentsAndStrings(string lua)
    {
        var withoutStrings = Regex.Replace(lua, @"""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*'", "\"\"");
        return Regex.Replace(withoutStrings, @"--[^\n]*", string.Empty);
    }
}
