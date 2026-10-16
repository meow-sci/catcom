// JintTsSpike: can the pure-C# Jint engine host typescript.js for transparent transpilation, and how fast?
using System.Diagnostics;
using Jint;
using Jint.Native;

var tsPath = args.Length > 0 ? args[0] : "/Users/asherwin/repos/meow-sci/ksa-game-assemblies/node_modules/typescript/lib/typescript.js";
var tsSource = File.ReadAllText(tsPath);
var sample = """
    interface Reading { alt: number; vel: number }
    export async function onIgnite(ev: { engine: string; vesselId: string }): Promise<void> {
      const r: Reading = { alt: 1, vel: 2 };
      let total = 0;
      for (let i = 0; i < 10; i++) total += r.alt * i;
      console.log(`ignite ${ev.engine} on ${ev.vesselId}: ${total}`);
    }
    enum Mode { A = 1, B = 2 }
    export const mode: Mode = Mode.B;
    """;
var sw = Stopwatch.StartNew();
var engine = new Engine(o => o.LimitRecursion(2048));
Console.WriteLine($"engine create: {sw.ElapsedMilliseconds} ms");
sw.Restart();
engine.Execute(tsSource);
Console.WriteLine($"typescript.js execute (cold): {sw.ElapsedMilliseconds} ms; ts.version = {engine.Evaluate("ts.version")}");
engine.Execute("""
    function tsp(src) {
      return ts.transpileModule(src, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
    }
    """);
sw.Restart();
var first = engine.Invoke("tsp", sample).AsString();
Console.WriteLine($"first transpile: {sw.ElapsedMilliseconds} ms");
const int N = 50;
sw.Restart();
for (int i = 0; i < N; i++) engine.Invoke("tsp", sample);
Console.WriteLine($"avg transpile x{N}: {sw.Elapsed.TotalMilliseconds / N:F2} ms");
Console.WriteLine(string.Join('\n', first.Split('\n').Take(6)));
// plain JS hot loop for engine speed comparison
sw.Restart();
var r = engine.Evaluate("let s=0; for(let i=0;i<5_000_000;i++){ s+= i*2 % 7 } s");
Console.WriteLine($"5M-iteration loop: {sw.ElapsedMilliseconds} ms (result {r})");

var sucrasePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "sucrase.bundle.js"));
if (File.Exists(sucrasePath))
{
    var e2 = new Engine(o => o.LimitRecursion(2048));
    sw.Restart(); e2.Execute(File.ReadAllText(sucrasePath)); Console.WriteLine($"sucrase bundle execute (cold): {sw.ElapsedMilliseconds} ms");
    e2.Execute("function sct(src){ return sucrase.transform(src, { transforms: ['typescript'], disableESTransforms: true }).code; }");
    sw.Restart(); e2.Invoke("sct", sample); Console.WriteLine($"sucrase first transpile: {sw.ElapsedMilliseconds} ms");
    sw.Restart(); for (int i = 0; i < 50; i++) e2.Invoke("sct", sample); Console.WriteLine($"sucrase avg transpile x50: {sw.Elapsed.TotalMilliseconds / 50:F2} ms");
}
