using System.Net;
using System.Net.Sockets;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.V8.Tests;

[TestFixture]
public sealed class CompilerTests : V8TestBase
{
    [Test]
    public void CodeCache_IsWrittenAndReused()
    {
        var stubs = Path.Combine(Root, "own-stubs");
        Directory.CreateDirectory(stubs);

        var first = NewRuntime(stubsDir: stubs);
        var cachePath = first.CompilerCodeCachePath!;

        Assert.That(cachePath, Is.EqualTo(Path.GetFullPath(Path.Combine(stubs, "..", "cache", Path.GetFileName(cachePath)))));
        Assert.That(Path.GetFileName(cachePath), Does.Match(@"^typescript-\d+-7\.5\.1\.1\.v8cache$"));
        Assert.That(first.CompilerCodeCacheAccepted, Is.False, "no cache existed yet");
        Assert.That(File.Exists(cachePath), Is.True);
        var written = new FileInfo(cachePath).Length;
        Assert.That(written, Is.GreaterThan(100_000));

        var second = NewRuntime(stubsDir: stubs);
        Assert.That(second.CompilerCodeCacheAccepted, Is.True, "the second runtime consumed the persisted cache");
        Assert.That(new FileInfo(cachePath).Length, Is.EqualTo(written));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(cachePath)!, "*.tmp"), Is.Empty, "the atomic write leaves no temp files");

        var module = LoadScript("cached", "enum E { A = 4 }\nexport function f(): number { return E.A; }", second);
        Assert.That(second.Invoke(second.GetExport(module, "f"), []).AsNumber(), Is.EqualTo(4));
    }

    [Test]
    public void CodeCache_CorruptFile_IsReplaced()
    {
        var stubs = Path.Combine(Root, "own-stubs");
        Directory.CreateDirectory(stubs);
        var first = NewRuntime(stubsDir: stubs);
        var cachePath = first.CompilerCodeCachePath!;
        File.WriteAllBytes(cachePath, [1, 2, 3, 4, 5, 6, 7, 8]);

        var second = NewRuntime(stubsDir: stubs);
        Assert.That(second.CompilerCodeCacheAccepted, Is.False, "garbage is rejected by V8");
        Assert.That(new FileInfo(cachePath).Length, Is.GreaterThan(100_000), "and replaced by a fresh cache");

        var third = NewRuntime(stubsDir: stubs);
        Assert.That(third.CompilerCodeCacheAccepted, Is.True);
    }

    [Test]
    public void CodeCache_UnwritableLocation_IsNotFatalAndLoggedOnce()
    {
        // A FILE where the cache directory should be: creating the directory fails.
        var stubs = Path.Combine(Root, "blocked", "stubs");
        Directory.CreateDirectory(Path.GetDirectoryName(stubs)!);
        File.WriteAllText(Path.Combine(Root, "blocked", "cache"), "i am a file");
        Directory.CreateDirectory(stubs);

        var runtime = NewRuntime(stubsDir: stubs);

        Assert.That(runtime.IsAvailable, Is.True);
        Assert.That(runtime.CompilerCodeCacheAccepted, Is.False);
        Assert.That(Host.Logs.Count(l => l.Level == 2 && l.Message.Contains("code cache")), Is.EqualTo(1));
        Assert.That(Call("stillworks", "export function f(): number { return 1; }", "f").AsNumber(), Is.EqualTo(1));
    }

    [Test]
    public void Compiler_ReportsDiagnosticsWithPositions_AndCachesResults()
    {
        var compiler = Rt.CompilerForTests();
        const string good = "const a: number = 1;\nexport enum E { A }\n";

        var first = compiler.Transpile(good, "cache.ts");
        var second = compiler.Transpile(good, "cache.ts");
        var broken = compiler.Transpile("let x = ;\n", "broken.ts");

        Assert.That(second, Is.SameAs(first), "same (file, source) hits the result cache");
        Assert.That(first.Code, Does.Contain("var E;").Or.Contain("let E;").Or.Contain("E[E["));
        Assert.That(first.Diagnostics, Is.Empty);
        Assert.That(broken.Diagnostics, Has.Count.GreaterThan(0));
        Assert.That(broken.Diagnostics[0].IsError, Is.True);
        Assert.That((broken.Diagnostics[0].Line, broken.Diagnostics[0].Column), Is.EqualTo((1, 9)));
        Assert.That(broken.Diagnostics[0].Message, Does.Contain("expected").IgnoreCase);
    }

    [Test]
    public void Compiler_ResultCache_IsBounded()
    {
        var compiler = Rt.CompilerForTests();
        var first = compiler.Transpile("export const first = 0;", "first.ts");

        for (var i = 0; i < 300; i++)
            compiler.Transpile($"export const v{i} = {i};", "bound.ts");

        Assert.That(compiler.Transpile("export const first = 0;", "first.ts"), Is.Not.SameAs(first), "the cache was cleared once it exceeded 256 entries");
    }

    [Test]
    public void Sucrase_Fallback_RunsTypeScriptAndMapsLines()
    {
        var assets = Path.Combine(Root, "sucrase-only");
        Directory.CreateDirectory(assets);
        File.Copy(V8Assets.SucrasePath, Path.Combine(assets, V8Assets.SucraseFileName));

        var runtime = WithAssetsDir(assets, () => NewRuntime());
        try
        {
            Assert.That(runtime.IsAvailable, Is.True, runtime.UnavailableReason);
            Assert.That(runtime.UsingSucrase, Is.True);
            Assert.That(runtime.Stats.Version, Does.EndWith("TypeScript sucrase"));
            Assert.That(runtime.CompilerCodeCacheAccepted, Is.Null);

            var path = WriteScript("sucrased.ts", """
                interface Options { verbose: boolean }
                type Id = string;
                enum Mode { A = 2, B }
                export function ok(o: Options): number { return Mode.B + (o.verbose ? 1 : 0); }
                export function boom(): never {
                  throw new Error("sucrase boom");
                }
                """);
            var module = runtime.Load(new ScriptSource("sucrased", path, File.ReadAllText(path), ScriptLanguages.TypeScript));

            var options = ScriptValue.Map(new Dictionary<string, ScriptValue> { ["verbose"] = ScriptValue.True });
            Assert.That(runtime.Invoke(runtime.GetExport(module, "ok"), [options]).AsNumber(), Is.EqualTo(4));
            var ex = Assert.Throws<ScriptRuntimeException>(() => runtime.Invoke(runtime.GetExport(module, "boom"), []))!;
            Assert.That(ex.Message, Does.Contain("sucrase boom"));
            Assert.That(ex.File, Is.EqualTo(path));
            Assert.That(ex.Line, Is.EqualTo(6));

            var badPath = WriteScript("sucrasebad.ts", "const a = 1;\nexport const broken = ;\n");
            var load = Assert.Throws<ScriptLoadException>(() => runtime.Load(new ScriptSource("bad", badPath, File.ReadAllText(badPath), ScriptLanguages.TypeScript)))!;
            Assert.That(load.Line, Is.EqualTo(2));
            Assert.That(load.File, Is.EqualTo(badPath));
        }
        finally
        {
            runtime.Dispose();
        }
    }

    [Test]
    public void TypeScriptCompilerLoadFailure_FallsBackToSucrase()
    {
        var assets = Path.Combine(Root, "broken-ts");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, V8Assets.TypeScriptFileName), "this is not javascript (((");
        File.Copy(V8Assets.SucrasePath, Path.Combine(assets, V8Assets.SucraseFileName));

        var runtime = WithAssetsDir(assets, () => NewRuntime());

        Assert.That(runtime.IsAvailable, Is.True, runtime.UnavailableReason);
        Assert.That(runtime.UsingSucrase, Is.True);
        Assert.That(Host.Logs.Any(l => l.Level == 2 && l.Message.Contains("falling back to sucrase")), Is.True);
    }

    [Test]
    public void MissingTranspilerAssets_MakeTheRuntimeUnavailable()
    {
        var empty = Path.Combine(Root, "no-assets");
        Directory.CreateDirectory(empty);

        var runtime = WithAssetsDir(empty, () => NewRuntime());

        Assert.That(runtime.IsAvailable, Is.False);
        Assert.That(runtime.UnavailableReason, Is.EqualTo("TypeScript transpiler assets missing (run tools/fetch-ts.sh)"));
        var ex = Assert.Throws<ScriptLoadException>(() => LoadScript("unavailable", "export function f() {}", runtime))!;
        Assert.That(ex.Message, Is.EqualTo("TypeScript transpiler assets missing (run tools/fetch-ts.sh)"));
        Assert.DoesNotThrow(() => runtime.Dispose());
    }

    [Test]
    public void DebugPort_StartsTheInspector()
    {
        var port = FreeTcpPort();
        var runtime = NewRuntime(debugPort: port);
        LoadScript("debuggable", "export function f(): number { return 1; }", runtime);

        using var http = new HttpClient { Timeout = Deadline };
        var version = http.GetStringAsync($"http://127.0.0.1:{port}/json/version").GetAwaiter().GetResult();

        Assert.That(version, Does.Contain("ClearScript"));
        runtime.Dispose();
    }

    private static T WithAssetsDir<T>(string directory, Func<T> create)
    {
        var previous = Environment.GetEnvironmentVariable(V8Assets.DirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(V8Assets.DirectoryEnvironmentVariable, directory);
        try
        {
            return create();
        }
        finally
        {
            Environment.SetEnvironmentVariable(V8Assets.DirectoryEnvironmentVariable, previous);
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
