using System.Text;

namespace KsaScripting.Core.Catalog;

/// <summary>
///     The metadata a script declares in its leading comment lines:
///     <c>-- @ksa name="Ascent guidance" autostart=true terminal=yes</c> (Lua) or the same after
///     <c>//</c> (TypeScript). Both prefixes are accepted for either language.
/// </summary>
/// <param name="Title">The display title (<c>name=</c>), or <see langword="null"/>.</param>
/// <param name="Autostart">Whether the script starts with the game (<c>autostart=</c>).</param>
/// <param name="Terminal">Whether the script owns a terminal (<c>terminal=</c>).</param>
public sealed record ScriptHeader(string? Title, bool Autostart, bool Terminal)
{
    private const int MaxHeaderLines = 30;

    /// <summary>
    ///     Reads the header of a script. Only the leading comment lines are inspected (scanning stops at
    ///     the first line that is neither a comment nor blank, after at most 30 lines); several
    ///     <c>@ksa</c> lines merge; unknown keys and invalid values are ignored.
    /// </summary>
    /// <param name="code">The script source.</param>
    /// <returns>The parsed header (all defaults when none is present).</returns>
    public static ScriptHeader Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        string? title = null;
        var autostart = false;
        var terminal = false;

        var lineNumber = 0;
        foreach (var rawLine in code.Split('\n'))
        {
            if (++lineNumber > MaxHeaderLines)
                break;

            var line = rawLine.Trim().TrimStart('﻿');
            if (line.Length == 0)
                continue;
            if (lineNumber == 1 && line.StartsWith("#!", StringComparison.Ordinal))
                continue;
            if (!TryStripCommentPrefix(line, out var body))
                break;

            body = body.TrimStart();
            if (!body.StartsWith("@ksa", StringComparison.Ordinal)
                || (body.Length > 4 && !char.IsWhiteSpace(body[4])))
                continue;

            foreach (var (key, value) in ParsePairs(body[4..]))
            {
                switch (key.ToLowerInvariant())
                {
                    case "name":
                        title = value.Length == 0 ? null : value;
                        break;
                    case "autostart":
                        if (TryParseBool(value, out var a))
                            autostart = a;
                        break;
                    case "terminal":
                        if (TryParseBool(value, out var t))
                            terminal = t;
                        break;
                }
            }
        }

        return new ScriptHeader(title, autostart, terminal);
    }

    private static bool TryStripCommentPrefix(string line, out string body)
    {
        foreach (var prefix in (ReadOnlySpan<string>)["--", "//", "/*", "*/", "*"])
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                body = line[prefix.Length..];
                return true;
            }
        }

        body = "";
        return false;
    }

    private static IEnumerable<(string Key, string Value)> ParsePairs(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length)
                yield break;

            var keyStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '=')
                i++;
            var key = text[keyStart..i];

            if (i >= text.Length || text[i] != '=')
                continue;
            i++;

            var value = new StringBuilder();
            if (i < text.Length && text[i] == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
                        i++;
                    value.Append(text[i]);
                    i++;
                }

                i++;
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    value.Append(text[i]);
                    i++;
                }
            }

            if (key.Length > 0)
                yield return (key, value.ToString());
        }
    }

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes":
                value = true;
                return true;
            case "false" or "0" or "no":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }
}
