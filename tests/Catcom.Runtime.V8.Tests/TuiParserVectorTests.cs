using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.V8.Tests;

/// <summary>
///     The shared terminal-input parser vectors (<c>tui-vectors.json</c>, also run against bun, a bare node V8 context and Lua)
///     driven through the real V8 runtime: the unmodified <c>scripts-lib/tui.ts</c> is transpiled and imported by the
///     loader, chunks cross as <c>Uint8Array</c>s, and the produced events are compared with the expected ones.
/// </summary>
[TestFixture]
public sealed class TuiParserVectorTests
{
    // Runs inside the script. text/bytes are hex-encoded in JavaScript (the bytes in a reply are a Uint8Array there), so
    // what crosses back is plain data; everything else (names, buttons, numbers, booleans, mods) crosses natively.
    private const string HarnessSource = """
        import { InputParser } from "./lib/tui.ts";

        const HEX = "0123456789abcdef";

        function hexBytes(bytes: ArrayLike<number>): string {
          let out = "";
          for (let i = 0; i < bytes.length; i++) out += HEX[(bytes[i] >> 4) & 15] + HEX[bytes[i] & 15];
          return out;
        }

        // UTF-8 of a UTF-16 string, written independently of the library under test; a lone surrogate becomes U+FFFD.
        function utf8Hex(s: string): string {
          const bytes: number[] = [];
          for (let i = 0; i < s.length; i++) {
            let cp = s.charCodeAt(i);
            if (cp >= 0xd800 && cp <= 0xdbff && i + 1 < s.length) {
              const next = s.charCodeAt(i + 1);
              if (next >= 0xdc00 && next <= 0xdfff) {
                cp = 0x10000 + ((cp - 0xd800) << 10) + (next - 0xdc00);
                i++;
              }
            }
            if (cp >= 0xd800 && cp <= 0xdfff) cp = 0xfffd;
            if (cp < 0x80) bytes.push(cp);
            else if (cp < 0x800) bytes.push(0xc0 | (cp >> 6), 0x80 | (cp & 63));
            else if (cp < 0x10000) bytes.push(0xe0 | (cp >> 12), 0x80 | ((cp >> 6) & 63), 0x80 | (cp & 63));
            else bytes.push(0xf0 | (cp >> 18), 0x80 | ((cp >> 12) & 63), 0x80 | ((cp >> 6) & 63), 0x80 | (cp & 63));
          }
          return hexBytes(bytes);
        }

        function describe(ev: any): Record<string, unknown> {
          const out: Record<string, unknown> = {};
          for (const key of Object.keys(ev)) {
            const value = ev[key];
            if (key === "bytes") {
              if (!(value instanceof Uint8Array)) throw new Error("reply.bytes is not a Uint8Array");
              out[key] = hexBytes(value);
            } else if (key === "text") {
              out[key] = utf8Hex(value);
            } else if (key === "mods") {
              out[key] = { ...value };
            } else {
              out[key] = value;
            }
          }
          return out;
        }

        export function feed(chunks: Uint8Array[], flushAtEnd: boolean, expectCpr: number | null): Record<string, unknown>[] {
          for (const chunk of chunks) {
            if (!(chunk instanceof Uint8Array)) throw new Error("a chunk did not arrive as a Uint8Array");
          }
          const parser = new InputParser();
          if (typeof expectCpr === "number" && expectCpr > 0) parser.expectCpr(expectCpr);
          const events: Record<string, unknown>[] = [];
          for (const chunk of chunks) {
            for (const ev of parser.feed(chunk)) events.push(describe(ev));
          }
          if (flushAtEnd) {
            for (const ev of parser.flush()) events.push(describe(ev));
          }
          return events;
        }
        """;

    private string _root = null!;
    private FakeHostFacade _host = null!;
    private V8ScriptRuntime _runtime = null!;
    private ScriptCallable _feed = null!;

    /// <summary>Every vector as its own test case, named after the vector.</summary>
    /// <returns>The test cases.</returns>
    public static IEnumerable<TestCaseData> Vectors()
    {
        foreach (var vector in TuiVector.LoadAll())
            yield return new TestCaseData(vector).SetName(vector.Name);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _root = Directory.CreateTempSubdirectory("ksa-v8-tui-").FullName;
        var scriptsDir = Path.Combine(_root, "scripts");
        Directory.CreateDirectory(scriptsDir);
        TuiTestSupport.InstallLibrary(scriptsDir);

        _host = new FakeHostFacade();
        _runtime = new V8ScriptRuntime();
        _runtime.Initialize(_host, new RuntimeOptions(scriptsDir, V8TestBase.SharedStubsFolder, 0, true, null));

        var path = Path.Combine(scriptsDir, "tui-harness.ts");
        File.WriteAllText(path, HarnessSource);
        var module = _runtime.Load(new ScriptSource("tui-harness", path, HarnessSource, ScriptLanguages.TypeScript));
        _feed = _runtime.GetExport(module, "feed");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _feed.Dispose();
        _runtime.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is not a test failure.
        }
    }

    [Test]
    public void VectorFile_IsComplete()
    {
        var vectors = TuiVector.LoadAll();

        Assert.That(vectors, Has.Count.EqualTo(380));
        Assert.That(vectors.Select(v => v.Name).Distinct().Count(), Is.EqualTo(vectors.Count), "vector names are unique");
    }

    [TestCaseSource(nameof(Vectors))]
    public void Parser_ProducesTheExpectedEvents(TuiVector vector)
    {
        var chunks = ScriptValue.List(vector.Chunks.Select(c => ScriptValue.From((ReadOnlyMemory<byte>)c)).ToArray());
        var cpr = vector.ExpectCpr is { } expected ? ScriptValue.From((double)expected) : ScriptValue.Null;

        var result = _runtime.Invoke(_feed, [chunks, ScriptValue.From(vector.FlushAtEnd), cpr]);

        var actual = result.AsList().Select(e => TuiEventForm.FromActual(e.AsMap())).ToArray();

        Assert.That(actual, Is.EqualTo(vector.Expected));
        Assert.That(_host.ReportedErrors, Is.Empty);
    }
}
