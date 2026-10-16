using KsaScripting.Core.Runtime;

namespace KsaScripting.Runtime.V8.Tests;

[TestFixture]
public sealed class LoaderTests : V8TestBase
{
    [Test]
    public void Loader_RejectsParentEscape()
    {
        File.WriteAllText(Path.Combine(Root, "outside.ts"), "export const secret = 1;");

        var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("escape", """
            import { secret } from "../outside.ts";
            export function main() { return secret; }
            """))!;

        Assert.That(ex.Message, Does.Contain("escapes"));
        Assert.That(ex.Message, Does.Contain("../outside.ts"));
    }

    [Test]
    public void Loader_RejectsParentEscape_EvenWhenTheTargetDoesNotExist()
    {
        var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("escape2", "import \"../missing.ts\";\nexport function main() {}"))!;

        Assert.That(ex.Message, Does.Contain("escapes"));
    }

    [Test]
    public void Loader_RejectsAbsolutePathsAndFileUrisOutsideTheFolder()
    {
        var outside = Path.Combine(Root, "outside.ts");
        File.WriteAllText(outside, "export const secret = 1;");

        var absolute = Assert.Throws<ScriptLoadException>(() => LoadScript("abs", $"import {JsonLiteral(outside)};\nexport function main() {{}}"))!;
        var uri = Assert.Throws<ScriptLoadException>(() => LoadScript("uri", $"import {JsonLiteral(new Uri(outside).AbsoluteUri)};\nexport function main() {{}}"))!;

        Assert.That(absolute.Message, Does.Contain("escapes"));
        Assert.That(uri.Message, Does.Contain("escapes"));
    }

    [Test]
    public void Loader_AllowsAbsolutePathsAndFileUrisInsideTheFolder()
    {
        var inside = WriteScript("lib/inside.ts", "export const v = 3;");

        var result = Call("absinside", $$"""
            import { v } from {{JsonLiteral(inside)}};
            import { v as w } from {{JsonLiteral(new Uri(inside).AbsoluteUri)}};
            export function main() { return v + w; }
            """, "main");

        Assert.That(result.AsNumber(), Is.EqualTo(6));
    }

    [Test]
    public void Loader_RejectsSymlinkedFileAndDirectoryEscapes()
    {
        var outsideDir = Directory.CreateDirectory(Path.Combine(Root, "outsidedir")).FullName;
        File.WriteAllText(Path.Combine(outsideDir, "target.ts"), "export const secret = 1;");
        try
        {
            File.CreateSymbolicLink(Path.Combine(ScriptsDir, "filelink.ts"), Path.Combine(outsideDir, "target.ts"));
            Directory.CreateSymbolicLink(Path.Combine(ScriptsDir, "dirlink"), outsideDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("cannot create symbolic links on this machine: " + ex.Message);
        }

        var fileLink = Assert.Throws<ScriptLoadException>(() => LoadScript("viafile", "import \"./filelink.ts\";\nexport function main() {}"))!;
        var dirLink = Assert.Throws<ScriptLoadException>(() => LoadScript("viadir", "import \"./dirlink/target.ts\";\nexport function main() {}"))!;

        Assert.That(fileLink.Message, Does.Contain("escapes"));
        Assert.That(dirLink.Message, Does.Contain("escapes"));
    }

    [Test]
    public void Loader_FollowsSymlinksThatStayInsideTheFolder()
    {
        WriteScript("real/lib.ts", "export const v = 11;");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(ScriptsDir, "alias"), Path.Combine(ScriptsDir, "real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("cannot create symbolic links on this machine: " + ex.Message);
        }

        var result = Call("inside", "import { v } from \"./alias/lib.ts\";\nexport function main() { return v; }", "main");

        Assert.That(result.AsNumber(), Is.EqualTo(11));
    }

    [Test]
    public void Loader_RejectsBareSpecifier()
    {
        var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("bare", """
            import lodash from "lodash";
            export function main() { return lodash; }
            """))!;

        Assert.That(ex.Message, Does.Contain("bare module specifier 'lodash' is not supported (only \"ksa\" and relative paths)"));
    }

    [Test]
    public void Loader_RejectsUnsupportedFileTypes()
    {
        WriteScript("data.json", "{}");

        var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("json", "import \"./data.json\";\nexport function main() {}"))!;

        Assert.That(ex.Message, Does.Contain("unsupported module type '.json'"));
    }

    [Test]
    public void Loader_ReportsMissingModules()
    {
        var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("missing", "import \"./nope\";\nexport function main() {}"))!;

        Assert.That(ex.Message, Does.Contain("cannot find module './nope'"));
    }

    [Test]
    public void DynamicImport_GoesThroughTheSameLoader()
    {
        WriteScript("lib/dyn.ts", "export const v = 5;");
        File.WriteAllText(Path.Combine(Root, "outside.ts"), "export const secret = 1;");
        var module = LoadScript("dyn", """
            import { ksa } from "ksa";
            export async function main() {
              const ok = await import("./lib/dyn.ts");
              ksa.console.log("loaded " + ok.v);
              try { await import("../outside.ts"); ksa.console.log("escaped!"); }
              catch (e) { ksa.console.log("blocked"); }
            }
            """);

        Rt.Invoke(Rt.GetExport(module, "main"), []);
        PumpUntil(() => Host.LogMessages.Count >= 2, "the dynamic imports");

        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "loaded 5", "blocked" }));
    }

    private static string JsonLiteral(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
