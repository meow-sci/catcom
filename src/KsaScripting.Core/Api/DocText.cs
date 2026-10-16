using System.Text;
using System.Text.RegularExpressions;

namespace KsaScripting.Core.Api;

/// <summary>Shared text helpers of the stub emitters.</summary>
internal static class DocText
{
    private static readonly Regex TsOnly = new("<ts>(.*?)</ts>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex LuaOnly = new("<lua>(.*?)</lua>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex CodeSpan = new("`([^`]+)`", RegexOptions.CultureInvariant);
    private static readonly Regex CamelToken = new(@"(?<![A-Za-z0-9_])[a-z][a-z0-9]*(?:[A-Z][a-z0-9]*)+(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
    private static readonly Regex PayloadToken = new(@"payload\.([a-z][A-Za-z0-9]*)", RegexOptions.CultureInvariant);
    private static readonly Regex VesselIdWord = new(@"\bvesselId\b", RegexOptions.CultureInvariant);
    private static readonly Regex NilInLua = new(@"\s*\(nil in Lua\)", RegexOptions.CultureInvariant);
    private static readonly Regex NullWord = new(@"\b(?:null|undefined)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@" {2,}", RegexOptions.CultureInvariant);

    /// <summary>
    ///     Adapts schema documentation (written once, mostly in TypeScript terms) for TypeScript: keeps
    ///     <c>&lt;ts&gt;</c> sections, drops <c>&lt;lua&gt;</c> sections.
    /// </summary>
    internal static string ForTs(string doc)
        => Tidy(LuaOnly.Replace(TsOnly.Replace(doc, "$1"), string.Empty));

    /// <summary>
    ///     Adapts schema documentation for Lua: keeps <c>&lt;lua&gt;</c> sections, drops <c>&lt;ts&gt;</c> sections,
    ///     writes <c>nil</c> for <c>null</c>/<c>undefined</c>, and snake_cases identifiers in code spans and
    ///     <c>payload.xxx</c> references.
    /// </summary>
    internal static string ForLua(string doc)
    {
        var text = TsOnly.Replace(LuaOnly.Replace(doc, "$1"), string.Empty);
        text = NilInLua.Replace(text, string.Empty);
        text = NullWord.Replace(text, "nil");
        text = CodeSpan.Replace(text, m => "`" + CamelToken.Replace(m.Groups[1].Value, t => LuaNames.Member(t.Value)) + "`");
        text = PayloadToken.Replace(text, m => "payload." + ApiNames.ToSnake(m.Groups[1].Value));
        text = VesselIdWord.Replace(text, "vessel_id");
        return Tidy(text);
    }

    private static string Tidy(string text)
        => Spaces.Replace(text, " ").Trim();

    /// <summary>Splits documentation into trimmed lines, dropping trailing blank lines.</summary>
    internal static IReadOnlyList<string> Lines(string doc)
    {
        var lines = doc.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>Word-wraps documentation to at most <paramref name="width"/> characters per line (existing line breaks are kept).</summary>
    internal static IReadOnlyList<string> Wrap(string doc, int width)
    {
        var wrapped = new List<string>();
        foreach (var paragraph in Lines(doc))
        {
            var current = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.Length > 0 && current.Length + 1 + word.Length > width)
                {
                    wrapped.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                    current.Append(' ');
                current.Append(word);
            }

            wrapped.Add(current.ToString());
        }

        return wrapped;
    }

    /// <summary>The "Action: `key` (Phase)" documentation line of a member with an action key, or <c>null</c>.</summary>
    internal static string? ActionLine(ApiMember member)
        => member.ActionKey is null ? null : $"Action: `{member.ActionKey}` ({member.Phase})";

    /// <summary>Whether <paramref name="member"/> exists in the language selected by <paramref name="language"/> (<c>"ts"</c> or <c>"lua"</c>).</summary>
    internal static bool InLanguage(ApiMember member, string language)
        => member.Languages is null || string.Equals(member.Languages, language, StringComparison.Ordinal);

    /// <summary>Appends <paramref name="text"/> and a <c>"\n"</c>.</summary>
    internal static void Line(StringBuilder builder, string text) => builder.Append(text).Append('\n');
}
