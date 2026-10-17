using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Catcom.Runtime.V8;

/// <summary>
///     A decoded source map (v3): maps generated (transpiled) positions back to the original TypeScript
///     positions so error locations point at the file the user wrote. V8 does not apply source maps itself.
/// </summary>
internal sealed partial class SourceMap
{
    private const string DataUrlPrefix = "data:application/json;base64,";

    private readonly SourceMapSegment[][] _lines;

    private SourceMap(SourceMapSegment[][] lines, IReadOnlyList<string> sources)
    {
        _lines = lines;
        Sources = sources;
    }

    /// <summary>The <c>sources</c> entries of the map.</summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>The number of generated lines the map describes.</summary>
    public int LineCount => _lines.Length;

    /// <summary>Parses a source-map JSON document.</summary>
    /// <param name="json">The map JSON (<c>version</c>, <c>sources</c>, <c>mappings</c>, …).</param>
    /// <returns>The map, or <c>null</c> when the JSON is not a usable v3 map.</returns>
    public static SourceMap? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("mappings", out var mappingsElement)
                || mappingsElement.ValueKind != JsonValueKind.String)
                return null;

            var sources = new List<string>();
            if (root.TryGetProperty("sources", out var sourcesElement) && sourcesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sourcesElement.EnumerateArray())
                    sources.Add(source.GetString() ?? "");
            }

            return new SourceMap(DecodeMappings(mappingsElement.GetString() ?? ""), sources);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Extracts the inline <c>//# sourceMappingURL=data:application/json;base64,…</c> map from generated code.</summary>
    /// <param name="code">The generated JavaScript.</param>
    /// <returns>The map JSON, or <c>null</c> when the code has no inline map.</returns>
    public static string? ExtractInlineJson(string code)
    {
        var index = code.LastIndexOf("sourceMappingURL=" + DataUrlPrefix, StringComparison.Ordinal);
        if (index < 0)
            return null;

        var start = index + "sourceMappingURL=".Length + DataUrlPrefix.Length;
        var end = start;
        while (end < code.Length && !char.IsWhiteSpace(code[end]))
            end++;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(code.Substring(start, end - start)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Builds the inline source-map comment for a map JSON document.</summary>
    /// <param name="json">The map JSON.</param>
    /// <returns>The <c>//# sourceMappingURL=data:…</c> comment (no trailing newline).</returns>
    public static string ToInlineComment(string json)
        => "//# sourceMappingURL=" + DataUrlPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    /// <summary>
    ///     Decodes a <c>mappings</c> string: lines separated by <c>;</c>, segments by <c>,</c>, each segment 1, 4 or 5
    ///     Base64-VLQ fields. Source, original line/column and name indices are relative to the previous segment
    ///     (across lines); the generated column is relative to the previous segment of the same line and resets per line.
    /// </summary>
    /// <param name="mappings">The mappings string.</param>
    /// <returns>The segments of each generated line (0-based).</returns>
    /// <exception cref="FormatException">The string contains an invalid character or a malformed segment.</exception>
    public static SourceMapSegment[][] DecodeMappings(string mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        var lines = new List<SourceMapSegment[]>();
        var current = new List<SourceMapSegment>();
        var sourceIndex = 0;
        var originalLine = 0;
        var originalColumn = 0;
        var nameIndex = 0;
        var generatedColumn = 0;
        var position = 0;
        Span<int> fields = stackalloc int[5];

        while (position <= mappings.Length)
        {
            if (position == mappings.Length || mappings[position] == ';')
            {
                lines.Add(current.ToArray());
                current.Clear();
                generatedColumn = 0;
                position++;
                continue;
            }

            if (mappings[position] == ',')
            {
                position++;
                continue;
            }

            var count = 0;
            while (position < mappings.Length && mappings[position] != ',' && mappings[position] != ';')
            {
                if (count == 5)
                    throw new FormatException("source map segment has more than 5 fields");
                fields[count++] = DecodeVlq(mappings, ref position);
            }

            if (count != 1 && count != 4 && count != 5)
                throw new FormatException($"source map segment has {count} fields (expected 1, 4 or 5)");

            generatedColumn += fields[0];
            if (count == 1)
            {
                current.Add(new SourceMapSegment(generatedColumn, -1, -1, -1, -1));
                continue;
            }

            sourceIndex += fields[1];
            originalLine += fields[2];
            originalColumn += fields[3];
            var name = -1;
            if (count == 5)
            {
                nameIndex += fields[4];
                name = nameIndex;
            }

            current.Add(new SourceMapSegment(generatedColumn, sourceIndex, originalLine, originalColumn, name));
        }

        return lines.ToArray();
    }

    /// <summary>
    ///     Maps a generated position to the original position: the last segment of the line whose generated column is at
    ///     or before the column (or the first mapped segment of the line when the column precedes them all).
    /// </summary>
    /// <param name="generatedLine1">The 1-based generated line.</param>
    /// <param name="generatedColumn1">The 1-based generated column.</param>
    /// <param name="originalLine1">The 1-based original line.</param>
    /// <param name="originalColumn1">The 1-based original column.</param>
    /// <returns><c>true</c> when the line has a mapped segment.</returns>
    public bool TryMap(int generatedLine1, int generatedColumn1, out int originalLine1, out int originalColumn1)
    {
        originalLine1 = 0;
        originalColumn1 = 0;
        var lineIndex = generatedLine1 - 1;
        if (lineIndex < 0 || lineIndex >= _lines.Length)
            return false;

        var segments = _lines[lineIndex];
        var column0 = Math.Max(0, generatedColumn1 - 1);
        var chosen = -1;
        var firstMapped = -1;
        for (var i = 0; i < segments.Length; i++)
        {
            if (!segments[i].HasSource)
                continue;

            if (firstMapped < 0)
                firstMapped = i;
            if (segments[i].GeneratedColumn <= column0)
                chosen = i;
            else
                break;
        }

        if (chosen < 0)
            chosen = firstMapped;
        if (chosen < 0)
            return false;

        originalLine1 = segments[chosen].OriginalLine + 1;
        originalColumn1 = segments[chosen].OriginalColumn + 1;
        return true;
    }

    /// <summary>Maps a generated position, falling back to the closest earlier mapped line when its own line has none.</summary>
    /// <param name="generatedLine1">The 1-based generated line.</param>
    /// <param name="generatedColumn1">The 1-based generated column.</param>
    /// <param name="originalLine1">The 1-based original line.</param>
    /// <param name="originalColumn1">The 1-based original column.</param>
    /// <returns><c>true</c> when a mapping was found.</returns>
    public bool TryMapNearest(int generatedLine1, int generatedColumn1, out int originalLine1, out int originalColumn1)
    {
        if (TryMap(generatedLine1, generatedColumn1, out originalLine1, out originalColumn1))
            return true;

        for (var line = Math.Min(generatedLine1, _lines.Length) - 1; line >= 1; line--)
        {
            if (TryMap(line, int.MaxValue, out originalLine1, out originalColumn1))
                return true;
        }

        return false;
    }

    private static int DecodeVlq(string text, ref int position)
    {
        long result = 0;
        var shift = 0;
        while (true)
        {
            if (position >= text.Length)
                throw new FormatException("truncated Base64-VLQ value");

            var digit = Base64Value(text[position++]);
            result |= (long)(digit & 0x1F) << shift;
            if ((digit & 0x20) == 0)
                break;

            shift += 5;
            if (shift > 30)
                throw new FormatException("Base64-VLQ value is too large");
        }

        var magnitude = (int)(result >> 1);
        return (result & 1) == 1 ? -magnitude : magnitude;
    }

    private static int Base64Value(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => throw new FormatException($"invalid Base64-VLQ character '{c}'"),
    };

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex LineBreakPattern();

    /// <summary>Returns the 1-based line of a source text (without its line terminator), or an empty string.</summary>
    /// <param name="source">The source text.</param>
    /// <param name="line1">The 1-based line number.</param>
    /// <returns>The line text.</returns>
    public static string GetLine(string source, int line1)
    {
        if (line1 < 1)
            return "";

        var lines = LineBreakPattern().Split(source);
        return line1 <= lines.Length ? lines[line1 - 1] : "";
    }
}
