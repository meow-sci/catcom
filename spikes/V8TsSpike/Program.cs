// V8TsSpike: measure TypeScript-in-V8 transpilation via ClearScript, plus host interop basics.
using System.Diagnostics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

var tsPath = args.Length > 0 ? args[0] : "/Users/asherwin/repos/meow-sci/ksa-game-assemblies/node_modules/typescript/lib/typescript.js";
var tsSource = File.ReadAllText(tsPath);
Console.WriteLine($"typescript.js: {tsSource.Length / 1024} KiB");

var sample = """
    import { vessel } from "ksa";
    interface Reading { alt: number; vel: number }
    export async function onIgnite(ev: { engine: string; vesselId: string }): Promise<void> {
      const r: Reading = { alt: vessel.altitude, vel: vessel.speed };
      let total = 0;
      for (let i = 0; i < 10; i++) total += r.alt * i;
      console.log(`ignite ${ev.engine} on ${ev.vesselId}: ${total}`);
    }
    enum Mode { A = 1, B = 2 }
    export const mode: Mode = Mode.B;
    """;

var sw = Stopwatch.StartNew();
var swFirst = Stopwatch.StartNew(); using var engineWarm = new V8ScriptEngine(); Console.WriteLine($"first engine (platform init): {swFirst.ElapsedMilliseconds} ms"); sw.Restart();
using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion | V8ScriptEngineFlags.EnableDynamicModuleImports);
Console.WriteLine($"engine create: {sw.ElapsedMilliseconds} ms  (V8 {engine.Script.eval("typeof process === 'undefined' ? 'v8' : 'node'")})");

// 1) Cold load of typescript.js
sw.Restart();
engine.Execute(new DocumentInfo("typescript.js"), tsSource);
Console.WriteLine($"typescript.js execute (cold): {sw.ElapsedMilliseconds} ms; ts.version = {engine.Script.ts.version}");

// 2) Load with V8 code cache: compile once producing cache bytes, then compile again consuming them
sw.Restart();
var compiled = engine.Compile(new DocumentInfo("typescript.js"), tsSource, V8CacheKind.Code, out byte[] cacheBytes);
Console.WriteLine($"compile + produce code cache: {sw.ElapsedMilliseconds} ms, cache={cacheBytes.Length / 1024} KiB");
using (var engine2 = new V8ScriptEngine())
{
    sw.Restart();
    var c2 = engine2.Compile(new DocumentInfo("typescript.js"), tsSource, V8CacheKind.Code, cacheBytes, out bool accepted);
    engine2.Execute(c2);
    Console.WriteLine($"fresh engine: compile-with-cache + execute: {sw.ElapsedMilliseconds} ms (cache accepted={accepted})");
}

// 2b) sucrase (bundled IIFE) as the lightweight alternative
var sucrasePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "sucrase.bundle.js"));
if (File.Exists(sucrasePath))
{
    var sucraseSrc = File.ReadAllText(sucrasePath);
    using var eng3 = new V8ScriptEngine();
    sw.Restart();
    eng3.Execute(new DocumentInfo("sucrase.js"), sucraseSrc);
    Console.WriteLine($"sucrase bundle ({sucraseSrc.Length / 1024} KiB) execute (cold): {sw.ElapsedMilliseconds} ms");
    eng3.Execute("function sct(src){ return sucrase.transform(src, { transforms: ['typescript'], disableESTransforms: true, keepUnusedImports: true }).code; }");
    sw.Restart(); var o1 = (string)eng3.Script.sct(sample); Console.WriteLine($"sucrase first transpile: {sw.ElapsedMilliseconds} ms");
    sw.Restart(); for (int i = 0; i < 200; i++) eng3.Script.sct(sample); Console.WriteLine($"sucrase avg transpile x200: {sw.Elapsed.TotalMilliseconds / 200:F2} ms");
    Console.WriteLine("--- sucrase output head ---"); Console.WriteLine(string.Join('\n', o1.Split('\n').Take(12)));
}
// 3) transpileModule
engine.Execute("""
    function tsp(src, fileName) {
      const r = ts.transpileModule(src, {
        fileName,
        reportDiagnostics: true,
        compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022, inlineSourceMap: true, inlineSources: true }
      });
      return JSON.stringify({ out: r.outputText, diag: (r.diagnostics || []).map(d => ts.flattenDiagnosticMessageText(d.messageText, '\n')) });
    }
    """);
sw.Restart();
var first = (string)engine.Script.tsp(sample, "hook.ts");
Console.WriteLine($"first transpile: {sw.ElapsedMilliseconds} ms");
sw.Restart();
const int N = 200;
for (int i = 0; i < N; i++) engine.Script.tsp(sample, "hook.ts");
Console.WriteLine($"avg transpile x{N}: {sw.Elapsed.TotalMilliseconds / N:F2} ms");
Console.WriteLine("--- output head ---");
Console.WriteLine(string.Join('\n', System.Text.Json.JsonDocument.Parse(first).RootElement.GetProperty("out").GetString()!.Split('\n').Take(12)));

// 4) Custom DocumentLoader: transparent .ts module imports (transpile on load)
engine.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
engine.DocumentSettings.Loader = new TsLoader(engine, Path.GetFullPath("scripts"));
Directory.CreateDirectory("scripts");
File.WriteAllText("scripts/ksa.ts", "export const vessel = { altitude: 1234.5, speed: 7.8 }; export function hello(name: string): string { return `hi ${name}`; }");
File.WriteAllText("scripts/hook.ts", sample.Replace("from \"ksa\"", "from \"./ksa.ts\""));
engine.AddHostType("Console", typeof(Console));
engine.Execute("const console = { log: (...a) => Console.WriteLine(a.join(' ')) };");
sw.Restart();
engine.Evaluate(new DocumentInfo("main.js") { Category = ModuleCategory.Standard },
    "import * as hook from './hook.ts'; import { hello } from './ksa.ts'; globalThis.__hook = hook; globalThis.__hello = hello;");
Console.WriteLine($"module graph import (2 .ts files): {sw.ElapsedMilliseconds} ms");
Console.WriteLine(engine.Script.__hello("kitten"));
// 5) async hook → Task interop
var task = (Task<object>)engine.Script.__hook.onIgnite(new { engine = "E1", vesselId = "V1" });
await task;
Console.WriteLine("async hook awaited OK; mode=" + engine.Script.__hook.mode);
// 6) memory + interrupt sanity
engine.MaxRuntimeHeapSize = (UIntPtr)(64 * 1024 * 1024);
engine.RuntimeHeapSizeSampleInterval = TimeSpan.FromMilliseconds(5);
var t = Task.Run(() => { Thread.Sleep(100); engine.Interrupt(); });
sw.Restart();
try { engine.Execute("for(;;){}"); } catch (ScriptInterruptedException) { Console.WriteLine($"infinite loop interrupted after {sw.ElapsedMilliseconds} ms"); }
Console.WriteLine($"heap: {engine.GetRuntimeHeapInfo().UsedHeapSize / 1024} KiB used");

sealed class TsLoader : DocumentLoader
{
    private readonly V8ScriptEngine _engine; private readonly string _root;
    public TsLoader(V8ScriptEngine engine, string root) { _engine = engine; _root = root; }
    public override Task<Document> LoadDocumentAsync(DocumentSettings settings, DocumentInfo? sourceInfo, string specifier, DocumentCategory category, DocumentContextCallback contextCallback)
    {
        var baseDir = sourceInfo?.Uri is { IsFile: true } u ? Path.GetDirectoryName(u.LocalPath)! : _root;
        var path = Path.GetFullPath(Path.Combine(baseDir, specifier));
        var src = File.ReadAllText(path);
        if (path.EndsWith(".ts"))
        {
            var json = (string)_engine.Script.tsp(src, Path.GetFileName(path));
            src = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("out").GetString()!;
        }
        var info = new DocumentInfo(new Uri(path)) { Category = ModuleCategory.Standard, ContextCallback = contextCallback };
        return Task.FromResult<Document>(new StringDocument(info, src));
    }
}
