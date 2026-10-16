using System.Text;
using System.Text.RegularExpressions;

namespace KsaScripting.Runtime.V8;

/// <summary>
///     Rewrites V8 / ClearScript stack traces so frames inside transpiled <c>.ts</c> files show the original file position.
///     Understands both V8's native format (<c>at fn (path:line:col)</c>) and ClearScript's <c>ErrorDetails</c> format
///     (<c>at fn (path:line:col) -&gt; source text</c>).
/// </summary>
internal static partial class StackTraceMapper
{
    [GeneratedRegex(@"^\s*at\s+(?:(?<fn>.*?)\s+\()?(?<file>.+?):(?<line>\d+):(?<col>\d+)\)?(?:\s+->.*)?\s*$")]
    private static partial Regex FramePattern();

    /// <summary>Rewrites a stack trace.</summary>
    /// <param name="stack">The stack text (first line the error header, then frames).</param>
    /// <param name="loader">The loader that served the engine's files and holds their source maps, or <c>null</c>.</param>
    /// <returns>The mapped traceback and the top frame inside a served file.</returns>
    public static MappedStack Map(string? stack, TsDocumentLoader? loader)
    {
        if (string.IsNullOrWhiteSpace(stack))
            return new MappedStack("", null, null, null);

        var builder = new StringBuilder();
        string? topFile = null;
        int? topLine = null;
        int? topColumn = null;
        var first = true;

        foreach (var rawLine in stack.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            var match = FramePattern().Match(line);
            if (!match.Success)
            {
                // The error header ("Error: message") and any continuation lines; ClearScript's own frames are noise.
                if (line.Contains("[internal]", StringComparison.Ordinal))
                    continue;

                Append(builder, ref first, line);
                continue;
            }

            var file = match.Groups["file"].Value;
            if (file.Contains("[internal]", StringComparison.Ordinal))
                continue;

            var functionName = match.Groups["fn"].Success ? match.Groups["fn"].Value : "";
            var lineNumber = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var column = int.Parse(match.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var displayFile = file;

            if (loader is not null && loader.TryGetFile(file, out var served))
            {
                displayFile = served.FullPath;
                if (served.Map is { } map
                    && (map.TryMap(lineNumber, column, out var originalLine, out var originalColumn)
                        || map.TryMapNearest(lineNumber, column, out originalLine, out originalColumn)))
                {
                    lineNumber = originalLine;
                    column = originalColumn;
                }

                if (topFile is null)
                {
                    topFile = served.FullPath;
                    topLine = lineNumber;
                    topColumn = column;
                }
            }

            var location = $"{displayFile}:{lineNumber}:{column}";
            Append(builder, ref first, functionName.Length > 0 ? $"    at {functionName} ({location})" : $"    at {location}");
        }

        return new MappedStack(builder.ToString(), topFile, topLine, topColumn);
    }

    private static void Append(StringBuilder builder, ref bool first, string line)
    {
        if (!first)
            builder.Append('\n');
        builder.Append(line);
        first = false;
    }
}
