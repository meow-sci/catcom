using KsaScripting.Core.Catalog;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Tests.Support;

namespace KsaScripting.Core.Tests.Catalog;

[TestFixture]
public sealed class ScriptCatalogScanTests
{
    private static void WriteTree(TestRig rig)
    {
        rig.WriteScript("ascent.lua", "-- @ksa name=\"Ascent\" autostart=true\n");
        rig.WriteScript("dir/x.ts", "// @ksa terminal=true\n");
        rig.WriteScript("dir/deep/y.lua");
        rig.WriteScript("dir/lib/a.lua");          // only the TOP-LEVEL lib/ is excluded
        rig.WriteScript("lib.lua");
        rig.WriteScript("lib/tui.ts");
        rig.WriteScript("lib/tui.lua");
        rig.WriteScript("stubs/host.d.ts");
        rig.WriteScript("stubs/ksa.lua");
        rig.WriteScript("types.d.ts");
        rig.WriteScript("dir/more.d.ts");
        rig.WriteScript("notes.txt");
        rig.WriteScript("README.md");
        rig.WriteScript(".hidden/z.lua");
        rig.WriteScript("dir/.secret.lua");
        rig.WriteScript("noext");
    }

    [Test]
    public void Scan_FindsLuaAndTypeScriptRecursively_ExcludingLibStubsDeclarationsAndHiddenFiles()
    {
        using var rig = new TestRig();
        WriteTree(rig);

        var infos = rig.Catalog.Scan();

        Assert.That(infos.Select(i => i.Name), Is.EqualTo(new[]
        {
            "ascent.lua", "dir/deep/y.lua", "dir/lib/a.lua", "dir/x.ts", "lib.lua",
        }));
    }

    [Test]
    public void Scan_ReportsLanguagePathStateAndHeaderMetadata()
    {
        using var rig = new TestRig();
        WriteTree(rig);

        var infos = rig.Catalog.Scan().ToDictionary(i => i.Name);

        Assert.Multiple(() =>
        {
            var ascent = infos["ascent.lua"];
            Assert.That(ascent.Language, Is.EqualTo(ScriptLanguages.Lua));
            Assert.That(ascent.State, Is.EqualTo(ScriptState.Idle));
            Assert.That(ascent.Path, Is.EqualTo(Path.Combine(rig.Catalog.ScriptsDir, "ascent.lua")));
            Assert.That(ascent.Title, Is.EqualTo("Ascent"));
            Assert.That(ascent.Autostart, Is.True);
            Assert.That(ascent.HasTerminal, Is.False);
            Assert.That(ascent.LastError, Is.Null);
            Assert.That(ascent.Exports, Is.Empty);

            var ts = infos["dir/x.ts"];
            Assert.That(ts.Language, Is.EqualTo(ScriptLanguages.TypeScript));
            Assert.That(ts.HasTerminal, Is.True);
            Assert.That(ts.Autostart, Is.False);
            Assert.That(ts.Title, Is.Null);
        });
    }

    [Test]
    public void Scan_RaisesChangedForEveryNewScript_AndAllMatchesTheScanResult()
    {
        using var rig = new TestRig();
        WriteTree(rig);
        var changed = new List<string>();
        rig.Catalog.Changed += info => changed.Add(info.Name);

        var infos = rig.Catalog.Scan();
        changed.Sort(StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(infos.Select(i => i.Name)));
            Assert.That(rig.Catalog.All(), Is.EqualTo(infos));
        });

        changed.Clear();
        rig.Catalog.Scan();
        Assert.That(changed, Is.Empty, "a second scan with no differences raises nothing");
    }

    [Test]
    public void Scan_KeepsTheStateOfKnownScripts_AddsNewOnes_AndDropsVanishedOnes()
    {
        using var rig = new TestRig();
        rig.WriteScript("keep.lua");
        rig.WriteScript("gone.lua");
        rig.Runtime.Register("gone.lua", new Dictionary<string, NullFunction>(), (host, runtime) =>
            host.Subscribe("event", "stage", runtime.CreateCallable((_, _) => KsaScripting.Core.Values.ScriptValue.Null, "on_stage"), new KsaScripting.Core.Hosting.BindingOptions()));
        rig.Catalog.Scan();
        rig.Catalog.Load("keep.lua");
        rig.Catalog.Load("gone.lua");
        Assert.That(rig.Hooks.ForScript("gone.lua"), Has.Count.EqualTo(1));

        File.Delete(Path.Combine(rig.ScriptsDir, "gone.lua"));
        rig.WriteScript("fresh.lua");
        var infos = rig.Catalog.Scan();

        Assert.Multiple(() =>
        {
            Assert.That(infos.Select(i => i.Name), Is.EqualTo(new[] { "fresh.lua", "keep.lua" }));
            Assert.That(rig.Catalog.Get("keep.lua")!.State, Is.EqualTo(ScriptState.Running));
            Assert.That(rig.Catalog.Get("fresh.lua")!.State, Is.EqualTo(ScriptState.Idle));
            Assert.That(rig.Catalog.Get("gone.lua"), Is.Null);
            Assert.That(rig.Hooks.ForScript("gone.lua"), Is.Empty, "the vanished script was unloaded");
            Assert.That(rig.Runtime.Loaded, Is.EqualTo(new[] { "keep.lua" }));
        });
    }

    [Test]
    public void Scan_RefreshesTheHeaderOfUnloadedScripts()
    {
        using var rig = new TestRig();
        rig.WriteScript("a.lua", "-- @ksa name=Old\n");
        rig.Catalog.Scan();

        rig.WriteScript("a.lua", "-- @ksa name=New terminal=true\n");
        var changed = new List<ScriptInfo>();
        rig.Catalog.Changed += changed.Add;
        rig.Catalog.Scan();

        Assert.Multiple(() =>
        {
            Assert.That(rig.Catalog.Get("a.lua")!.Title, Is.EqualTo("New"));
            Assert.That(rig.Catalog.Get("a.lua")!.HasTerminal, Is.True);
            Assert.That(changed, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Scan_OfAMissingFolder_IsEmpty()
    {
        using var rig = new TestRig();
        Directory.Delete(rig.ScriptsDir, recursive: true);

        Assert.That(rig.Catalog.Scan(), Is.Empty);
    }

    [Test]
    public void Get_AndNameForPath_UseForwardSlashRelativeNames()
    {
        using var rig = new TestRig();
        var path = rig.WriteScript("dir/sub/a.lua");
        rig.Catalog.Scan();

        Assert.Multiple(() =>
        {
            Assert.That(rig.Catalog.NameForPath(path), Is.EqualTo("dir/sub/a.lua"));
            Assert.That(rig.Catalog.Get("dir/sub/a.lua"), Is.Not.Null);
            Assert.That(rig.Catalog.Get("./dir/sub/a.lua"), Is.Not.Null, "a leading ./ is tolerated");
            Assert.That(rig.Catalog.Get("dir\\sub\\a.lua"), Is.Not.Null, "backslashes are normalized");
            Assert.That(rig.Catalog.Get("missing.lua"), Is.Null);
            Assert.That(rig.Catalog.NameForPath(Path.Combine(rig.ScriptsDir, "x.lua")), Is.EqualTo("x.lua"));
        });
    }
}
