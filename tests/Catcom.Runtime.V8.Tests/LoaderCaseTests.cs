using Catcom.Core.Runtime;

namespace Catcom.Runtime.V8.Tests;

/// <summary>
///     Containment and module identity when path casing varies. macOS (APFS) and Windows fold case by default, Linux does not,
///     and a macOS volume can be formatted case-sensitive: the loader must decide by what is on disk, not by the OS. Every test
///     adapts its expectation to the file system it runs on (probed in the temp folder), so each runs meaningfully everywhere.
///     To exercise the other flavour run the suite with <c>TMPDIR</c> pointing at a volume of that kind.
/// </summary>
[TestFixture]
public sealed class LoaderCaseTests : V8TestBase
{
    private bool FoldsCase()
    {
        var probe = Path.Combine(Root, "CaseProbe.tmp");
        File.WriteAllText(probe, "");
        return File.Exists(Path.Combine(Root, "caseprobe.TMP"));
    }

    private static string JsonLiteral(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Test]
    public void ParentTrick_WithADifferentlyCasedScriptsFolder_IsInsideOnlyWhereTheFileSystemFoldsCase()
    {
        WriteScript("lib/shared.ts", "export const v = 21;");

        if (FoldsCase())
        {
            var result = Call("foldedparent", "import { v } from \"../SCRIPTS/lib/shared.ts\";\nexport function main() { return v * 2; }", "main");
            Assert.That(result.AsNumber(), Is.EqualTo(42), "../SCRIPTS is the scripts folder itself on this file system");
        }
        else
        {
            var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("sensitiveparent", "import \"../SCRIPTS/lib/shared.ts\";\nexport function main() {}"))!;
            Assert.That(ex.Message, Does.Contain("escapes").Or.Contain("cannot find module"));
        }
    }

    [Test]
    public void SiblingFolderDifferingOnlyInCase_IsOutside_WhenTheFileSystemKeepsThemApart()
    {
        // "<root>/Scripts" next to "<root>/scripts": two folders on a case-sensitive volume, ONE folder where case folds.
        var sibling = Directory.CreateDirectory(Path.Combine(Root, "Scripts")).FullName;
        File.WriteAllText(Path.Combine(sibling, "secret.ts"), "export const secret = 1;");

        if (FoldsCase())
        {
            Assert.That(Path.GetFullPath(sibling), Is.EqualTo(Path.Combine(Root, "Scripts")));
            var result = Call("foldedsibling", "import { secret } from \"../Scripts/secret.ts\";\nexport function main() { return secret; }", "main");
            Assert.That(result.AsNumber(), Is.EqualTo(1), "the same folder: its content is inside");
        }
        else
        {
            var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("sensitivesibling", "import { secret } from \"../Scripts/secret.ts\";\nexport function main() { return secret; }"))!;
            Assert.That(ex.Message, Does.Contain("escapes"), "a different folder outside the scripts folder");
        }
    }

    [Test]
    public void AbsolutePath_WithADifferentlyCasedScriptsFolder_IsInsideOnlyWhereTheFileSystemFoldsCase()
    {
        WriteScript("lib/abs.ts", "export const v = 4;");
        var miscased = Path.Combine(Root, "SCRIPTS", "lib", "abs.ts");

        if (FoldsCase())
        {
            Assert.That(Call("foldedabs", $"import {{ v }} from {JsonLiteral(miscased)};\nexport function main() {{ return v; }}", "main").AsNumber(), Is.EqualTo(4));
        }
        else
        {
            var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("sensitiveabs", $"import {JsonLiteral(miscased)};\nexport function main() {{}}"))!;
            Assert.That(ex.Message, Does.Contain("escapes").Or.Contain("cannot find module"));
        }
    }

    [Test]
    public void SiblingWithTheScriptsFolderNameAsPrefix_IsOutside_InAnyCasing()
    {
        foreach (var name in new[] { "scripts-evil", "scripts2", "SCRIPTS-evil", "Scripts_x" })
        {
            var dir = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            File.WriteAllText(Path.Combine(dir, "x.ts"), "export const secret = 1;");

            var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("prefix", $"import \"../{name}/x.ts\";\nexport function main() {{}}"), name)!;

            Assert.That(ex.Message, Does.Contain("escapes"), name);
        }
    }

    [Test]
    public void EscapesHiddenInUriEscapesAndCasedDotSegments_AreRefused()
    {
        File.WriteAllText(Path.Combine(Root, "outside.ts"), "export const secret = 1;");
        var inside = new Uri(ScriptsDir).AbsoluteUri;

        foreach (var specifier in new[]
                 {
                     inside + "/%2e%2e/outside.ts",
                     inside + "/%2E%2E/outside.ts",
                     inside + "/lib/..%2f..%2foutside.ts",
                     inside + "/..%2foutside.ts",
                     "./lib/../../outside.ts",
                     "../SCRIPTS/../outside.ts",
                 })
        {
            var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("uriescape", $"import {JsonLiteral(specifier)};\nexport function main() {{}}"), specifier)!;

            Assert.That(ex.Message, Does.Contain("escapes").Or.Contain("cannot find module"), specifier);
        }
    }

    [Test]
    public void DifferentlyCasedImportsOfOneFile_ShareOneModuleInstance_WhereTheFileSystemFoldsCase()
    {
        WriteScript("lib/counter.ts", "let n = 0;\nexport function bump(): number { return ++n; }");
        if (!FoldsCase())
            Assert.Ignore("this file system distinguishes case: the spellings are different files");

        var result = Call("sharedinstance", """
            import { bump } from "./lib/counter.ts";
            import { bump as bumpAgain } from "./LIB/Counter.TS";
            import { bump as bumpThird } from "./Lib/COUNTER";
            export function main(): string { return [bump(), bumpAgain(), bumpThird()].join(","); }
            """, "main");

        Assert.That(result.AsString(), Is.EqualTo("1,2,3"), "one module instance however the path is spelled");
    }

    [Test]
    public void ErrorsInADifferentlyCasedImport_ReportTheFileNameAsItIsOnDisk()
    {
        var real = WriteScript("lib/Failing.ts", "export function fail(): never {\n  throw new Error(\"from lib\");\n}\n");
        if (!FoldsCase())
            Assert.Ignore("this file system distinguishes case: the spelling below would not be found");

        var module = LoadScript("casederror", "import { fail } from \"./LIB/failing.TS\";\nexport function main() { fail(); }");

        var ex = Assert.Throws<ScriptRuntimeException>(() => Rt.Invoke(Rt.GetExport(module, "main"), []))!;
        Assert.That(ex.File, Is.EqualTo(real));
        Assert.That(ex.Line, Is.EqualTo(2));
    }

    [Test]
    public void SymlinkWithADifferentCaseTarget_IsStillContained()
    {
        var outsideDir = Directory.CreateDirectory(Path.Combine(Root, "OutsideCase")).FullName;
        File.WriteAllText(Path.Combine(outsideDir, "t.ts"), "export const secret = 1;");
        try
        {
            // The link text spells the target in other letters; where case folds it still points at OutsideCase.
            Directory.CreateSymbolicLink(Path.Combine(ScriptsDir, "viacase"), Path.Combine(Root, "OUTSIDECASE"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("cannot create symbolic links on this machine: " + ex.Message);
        }

        var result = Assert.Throws<ScriptLoadException>(() => LoadScript("viacasescript", "import \"./viacase/t.ts\";\nexport function main() {}"))!;

        Assert.That(result.Message, Does.Contain("escapes").Or.Contain("cannot find module"));
    }

    [Test]
    public void ALinkRepointedAfterTheFirstImport_IsCheckedAgain()
    {
        WriteScript("inside/a.ts", "export const v = 1;");
        WriteScript("inside/b.ts", "export const v = 2;");
        var outside = Directory.CreateDirectory(Path.Combine(Root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(outside, "b.ts"), "export const v = 99;");
        var link = Path.Combine(ScriptsDir, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(ScriptsDir, "inside"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore("cannot create symbolic links on this machine: " + ex.Message);
        }

        var module = LoadScript("repoint", """
            import { ksa } from "ksa";
            export async function main() {
              const a = await import("./link/a.ts");
              ksa.console.log("first " + a.v);
              await ksa.sleep(1);
              try { const b = await import("./link/b.ts"); ksa.console.log("second " + b.v); } catch { ksa.console.log("second blocked"); }
            }
            """);
        Rt.Invoke(Rt.GetExport(module, "main"), []);
        PumpUntil(() => Host.LogMessages.Count == 1, "the first import");

        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, outside);
        Host.Waits.Single().Wait.Complete(Core.Values.ScriptValue.Null);
        PumpUntil(() => Host.LogMessages.Count == 2, "the second import");

        Assert.That(Host.LogMessages, Is.EqualTo(new[] { "first 1", "second blocked" }), "the link now leads outside the scripts folder");
    }
}
