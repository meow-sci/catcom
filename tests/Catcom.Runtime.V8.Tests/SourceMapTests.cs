using Catcom.Core.Runtime;

namespace Catcom.Runtime.V8.Tests;

[TestFixture]
public sealed class SourceMapTests : V8TestBase
{
    [Test]
    public void SourceMap_VlqDecoder_HandWrittenMappings()
    {
        // line 1: [0,0,0,0] [+4,0,0,+4] [+1,0,0,+1]   line 2: [0,0,+1,-1]
        var lines = SourceMap.DecodeMappings("AAAA,IAAI,CAAC;AACD");

        Assert.That(lines, Has.Length.EqualTo(2));
        Assert.That(lines[0], Is.EqualTo(new[]
        {
            new SourceMapSegment(0, 0, 0, 0, -1),
            new SourceMapSegment(4, 0, 0, 4, -1),
            new SourceMapSegment(5, 0, 0, 5, -1),
        }));
        Assert.That(lines[1], Is.EqualTo(new[] { new SourceMapSegment(0, 0, 1, 4, -1) }));
    }

    [Test]
    public void SourceMap_VlqDecoder_MultiDigitNegativeAndNameFields()
    {
        // "gB" = 16 (two digits), "D" = -1, "E" = +2; the 5th field is the name index and is relative too.
        var lines = SourceMap.DecodeMappings("gBAEE,CAADC,CACAD");

        Assert.That(lines[0], Is.EqualTo(new[]
        {
            new SourceMapSegment(16, 0, 2, 2, -1),
            new SourceMapSegment(17, 0, 2, 1, 1),
            new SourceMapSegment(18, 0, 3, 1, 0),
        }));
    }

    [Test]
    public void SourceMap_VlqDecoder_OneFieldSegmentsAndEmptyLines()
    {
        var lines = SourceMap.DecodeMappings("A;;AAAA");

        Assert.That(lines, Has.Length.EqualTo(3));
        Assert.That(lines[0], Is.EqualTo(new[] { new SourceMapSegment(0, -1, -1, -1, -1) }));
        Assert.That(lines[1], Is.Empty);
        Assert.That(lines[2], Is.EqualTo(new[] { new SourceMapSegment(0, 0, 0, 0, -1) }));
    }

    [Test]
    public void SourceMap_VlqDecoder_RejectsInvalidInput()
    {
        Assert.Throws<FormatException>(() => SourceMap.DecodeMappings("AA?A"));
        Assert.Throws<FormatException>(() => SourceMap.DecodeMappings("AAA")); // 3 fields
        Assert.Throws<FormatException>(() => SourceMap.DecodeMappings("g")); // truncated continuation
    }

    [Test]
    public void SourceMap_TryMap_PicksLastSegmentAtOrBeforeColumn()
    {
        var map = SourceMap.TryParse("""{"version":3,"sources":["a.ts"],"names":[],"mappings":"AAAA,IAAI,CAAC;AACD"}""")!;

        Assert.That(map.Sources, Is.EqualTo(new[] { "a.ts" }));
        Assert.That(map.TryMap(1, 1, out var line, out var column), Is.True);
        Assert.That((line, column), Is.EqualTo((1, 1)));
        Assert.That(map.TryMap(1, 5, out line, out column), Is.True, "generated col 4 (0-based) is the second segment");
        Assert.That((line, column), Is.EqualTo((1, 5)));
        Assert.That(map.TryMap(1, 100, out line, out column), Is.True, "after the last segment: the last segment");
        Assert.That((line, column), Is.EqualTo((1, 6)));
        Assert.That(map.TryMap(2, 1, out line, out column), Is.True);
        Assert.That((line, column), Is.EqualTo((2, 5)));
        Assert.That(map.TryMap(3, 1, out _, out _), Is.False);
        Assert.That(map.TryMap(0, 1, out _, out _), Is.False);
    }

    [Test]
    public void SourceMap_TryMap_ColumnBeforeFirstSegmentUsesFirstSegment()
    {
        // The only segment on the line starts at generated column 8.
        var map = SourceMap.TryParse("""{"version":3,"sources":["a.ts"],"names":[],"mappings":"QAAA"}""")!;

        Assert.That(map.TryMap(1, 1, out var line, out var column), Is.True);
        Assert.That((line, column), Is.EqualTo((1, 1)));
    }

    [Test]
    public void SourceMap_TryParse_RejectsGarbage()
    {
        Assert.That(SourceMap.TryParse(null), Is.Null);
        Assert.That(SourceMap.TryParse("not json"), Is.Null);
        Assert.That(SourceMap.TryParse("""{"version":3}"""), Is.Null);
        Assert.That(SourceMap.TryParse("""{"mappings":"A?"}"""), Is.Null);
    }

    [Test]
    public void SourceMap_DecodesMapProducedByTypeScript()
    {
        const string source = """
            interface Foo {
              x: number;
            }
            export function f(): number {
              const y: number = 1;
              return y + 1;
            }
            """;
        var result = Rt.CompilerForTests().Transpile(source, "map.ts");
        Assert.That(result.Diagnostics, Is.Empty);
        Assert.That(result.SourceMapJson, Is.Not.Null);

        var map = SourceMap.TryParse(result.SourceMapJson)!;
        var generated = result.Code.Split('\n');
        var returnLine = Array.FindIndex(generated, l => l.Contains("return y + 1")) + 1;
        var constLine = Array.FindIndex(generated, l => l.Contains("const y = 1")) + 1;
        var functionLine = Array.FindIndex(generated, l => l.Contains("export function f")) + 1;

        Assert.That(returnLine, Is.GreaterThan(0));
        Assert.That(map.TryMap(functionLine, 1, out var line, out _), Is.True);
        Assert.That(line, Is.EqualTo(4));
        Assert.That(map.TryMap(constLine, generated[constLine - 1].IndexOf("const", StringComparison.Ordinal) + 1, out line, out var column), Is.True);
        Assert.That((line, column), Is.EqualTo((5, 3)));
        Assert.That(map.TryMap(returnLine, generated[returnLine - 1].IndexOf("return", StringComparison.Ordinal) + 1, out line, out column), Is.True);
        Assert.That((line, column), Is.EqualTo((6, 3)));
    }

    [Test]
    public void SourceMap_ExtractInlineJson_RoundTrips()
    {
        const string json = """{"version":3,"sources":["a.ts"],"mappings":"AAAA"}""";
        var code = "let a = 1;\n" + SourceMap.ToInlineComment(json) + "\n";

        Assert.That(SourceMap.ExtractInlineJson(code), Is.EqualTo(json));
        Assert.That(SourceMap.ExtractInlineJson("let a = 1;"), Is.Null);
    }
}
