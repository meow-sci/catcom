using Catcom.Core.Api;

namespace Catcom.Core.Tests.Api;

[TestFixture]
public sealed class StubWriterTests
{
    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ksa-stubwriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private string ScriptsDir => Path.Combine(_dir, "scripts");

    [Test]
    public void WriteAll_CreatesTheFourFiles()
    {
        var written = StubWriter.WriteAll(ScriptsDir);

        var expected = new[]
        {
            Path.Combine(ScriptsDir, "stubs", "host.d.ts"),
            Path.Combine(ScriptsDir, "stubs", "ksa.lua"),
            Path.Combine(ScriptsDir, "tsconfig.json"),
            Path.Combine(ScriptsDir, ".luarc.json"),
        };
        Assert.That(written, Is.EquivalentTo(expected));
        Assert.Multiple(() =>
        {
            foreach (var path in expected)
                Assert.That(File.Exists(path), Is.True, path);
            Assert.That(File.ReadAllText(expected[0]), Is.EqualTo(StubGenerators.TypeScriptDeclarations(ApiSchema.Classes)));
            Assert.That(File.ReadAllText(expected[1]), Is.EqualTo(StubGenerators.LuaCats(ApiSchema.Classes)));
            Assert.That(File.ReadAllText(expected[2]), Is.EqualTo(StubGenerators.TsConfigJson()));
            Assert.That(File.ReadAllText(expected[3]), Is.EqualTo(StubGenerators.LuaRcJson()));
        });
    }

    [Test]
    public void WriteAll_SecondCallWritesNothing()
    {
        StubWriter.WriteAll(ScriptsDir);
        Assert.That(StubWriter.WriteAll(ScriptsDir), Is.Empty);
    }

    [Test]
    public void WriteAll_RewritesOnlyTheFilesThatChanged()
    {
        StubWriter.WriteAll(ScriptsDir);
        var tsconfig = Path.Combine(ScriptsDir, "tsconfig.json");
        File.WriteAllText(tsconfig, "{}");

        var written = StubWriter.WriteAll(ScriptsDir);

        Assert.That(written, Is.EqualTo(new[] { tsconfig }));
        Assert.That(File.ReadAllText(tsconfig), Is.EqualTo(StubGenerators.TsConfigJson()));
    }

    [Test]
    public void WriteAll_NeverThrows_WhenTheFolderCannotBeCreated()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file, not a folder");

        IReadOnlyList<string>? written = null;
        Assert.DoesNotThrow(() => written = StubWriter.WriteAll(Path.Combine(blocker, "scripts")));
        Assert.That(written, Is.Empty);
    }
}
