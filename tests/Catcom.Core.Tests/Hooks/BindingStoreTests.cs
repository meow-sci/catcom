using Catcom.Core.Hooks;
using Catcom.Core.Logging;
using Catcom.Core.Tests.Support;

namespace Catcom.Core.Tests.Hooks;

[TestFixture]
public sealed class BindingStoreTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "catcom-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "bindings.toml");

    private static readonly BindingRecord EventRecord =
        new("ascent.lua", "on_ignite", "event", "engine.activate", true, "active", "ut", 0, true);

    private static readonly BindingRecord TickRecord =
        new("dir/telemetry.ts", "sample", "tick", "every", false, null, "wall", 1.5, false);

    [Test]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var store = new BindingStore(FilePath);

        store.Save([EventRecord, TickRecord]);
        var loaded = store.Load();

        Assert.That(loaded, Is.EqualTo(new[] { EventRecord, TickRecord }));
    }

    [Test]
    public void Save_WritesArrayOfTables_WithTheDocumentedKeys_AndOmitsANullVessel()
    {
        var store = new BindingStore(FilePath);

        store.Save([EventRecord, TickRecord]);
        var text = File.ReadAllText(FilePath);

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("[[binding]]"));
            Assert.That(text, Does.Contain("script = \"ascent.lua\""));
            Assert.That(text, Does.Contain("export = \"on_ignite\""));
            Assert.That(text, Does.Contain("kind = \"event\""));
            Assert.That(text, Does.Contain("key = \"engine.activate\""));
            Assert.That(text, Does.Contain("sync = true"));
            Assert.That(text, Does.Contain("vessel = \"active\""));
            Assert.That(text, Does.Contain("clock = \"wall\""));
            Assert.That(text, Does.Contain("interval_seconds = 1.5"));
            Assert.That(text, Does.Contain("enabled = false"));
            Assert.That(System.Text.RegularExpressions.Regex.Matches(text, "vessel =").Count, Is.EqualTo(1), "only the first record has a vessel");
        });
    }

    [Test]
    public void Save_ARecordWithoutAVessel_WritesNoVesselKey()
    {
        var store = new BindingStore(FilePath);

        store.Save([TickRecord]);

        Assert.That(File.ReadAllText(FilePath), Does.Not.Contain("vessel"));
    }

    [Test]
    public void Save_AnEmptyList_ProducesAFileThatLoadsAsEmpty()
    {
        var store = new BindingStore(FilePath);
        store.Save([EventRecord]);

        store.Save([]);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(FilePath), Is.True);
            Assert.That(store.Load(), Is.Empty);
        });
    }

    [Test]
    public void Save_CreatesMissingFolders_AndLeavesNoTempFile()
    {
        var nested = Path.Combine(_dir, "a", "b", "bindings.toml");
        var store = new BindingStore(nested);

        store.Save([EventRecord]);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(nested), Is.True);
            Assert.That(File.Exists(nested + ".tmp"), Is.False);
            Assert.That(store.Path, Is.EqualTo(nested));
        });
    }

    [Test]
    public void Load_AMissingFile_IsEmpty()
        => Assert.That(new BindingStore(FilePath).Load(), Is.Empty);

    [Test]
    public void Load_AMalformedFile_WarnsReturnsEmptyAndLeavesTheFileUntouched()
    {
        using var log = new LogCapture();
        const string garbage = "[[binding]\nscript = \n???";
        File.WriteAllText(FilePath, garbage);

        var loaded = new BindingStore(FilePath).Load();

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Empty);
            Assert.That(File.ReadAllText(FilePath), Is.EqualTo(garbage));
            Assert.That(log.Contains(LogLevel.Warn, "bindings.toml"), Is.True);
        });
    }

    [Test]
    public void Load_AppliesDefaultsForMissingKeys_AndSkipsRowsWithoutScriptOrExport()
    {
        using var log = new LogCapture();
        File.WriteAllText(FilePath, """
            [[binding]]
            script = "a.lua"
            export = "f"
            key = "stage"

            [[binding]]
            export = "orphan"
            """);

        var loaded = new BindingStore(FilePath).Load();

        Assert.That(loaded, Is.EqualTo(new[] { new BindingRecord("a.lua", "f", "event", "stage", false, null, "ut", 0, true) }));
    }
}
