using KsaScripting.Core.Catalog;

namespace KsaScripting.GameMod.Commands;

/// <summary>
///     Resolves the script name a player typed (<c>ascent</c>, <c>ascent.lua</c>, <c>demo/ascent</c>,
///     <c>"Ascent guidance"</c>) to a catalog entry. Pure; the first rule that matches anything decides, and
///     matching several entries is reported as ambiguous instead of guessing.
/// </summary>
internal static class ScriptNameResolver
{
    /// <summary>Cleans a typed name: trims, drops surrounding quotes, uses <c>/</c> separators, drops a leading <c>./</c>.</summary>
    /// <param name="input">The typed text.</param>
    /// <returns>The normalized name (possibly empty).</returns>
    public static string Normalize(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
            text = text[1..^1].Trim();

        text = text.Replace('\\', '/');
        while (text.StartsWith("./", StringComparison.Ordinal))
            text = text[2..];
        return text;
    }

    /// <summary>
    ///     Finds the script. Rules, in order: exact name; name ignoring case; name without its extension; for a
    ///     bare name, the file name (with or without extension) in any folder; the header title.
    /// </summary>
    /// <param name="input">The typed name.</param>
    /// <param name="scripts">The catalog's scripts.</param>
    /// <param name="normalized">The normalized input.</param>
    /// <param name="problem">Set when several scripts match (the message lists them).</param>
    /// <returns>The single match, or <see langword="null"/> (none, or ambiguous with <paramref name="problem"/> set).</returns>
    public static ScriptInfo? Resolve(string? input, IReadOnlyList<ScriptInfo> scripts, out string normalized, out string? problem)
    {
        normalized = Normalize(input);
        problem = null;
        if (normalized.Length == 0)
            return null;

        var target = normalized;
        var bare = !target.Contains('/');
        Func<ScriptInfo, bool>[] rules =
        [
            s => string.Equals(s.Name, target, StringComparison.Ordinal),
            s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase),
            s => string.Equals(WithoutExtension(s.Name), target, StringComparison.OrdinalIgnoreCase),
            s => bare && (string.Equals(FileName(s.Name), target, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(WithoutExtension(FileName(s.Name)), target, StringComparison.OrdinalIgnoreCase)),
            s => !string.IsNullOrEmpty(s.Title) && string.Equals(s.Title, target, StringComparison.OrdinalIgnoreCase),
        ];

        foreach (var rule in rules)
        {
            var matches = scripts.Where(rule).ToArray();
            if (matches.Length == 1)
                return matches[0];

            if (matches.Length > 1)
            {
                problem = $"'{target}' is ambiguous: {string.Join(", ", matches.Select(m => m.Name))}";
                return null;
            }
        }

        return null;
    }

    private static string FileName(string name)
    {
        var slash = name.LastIndexOf('/');
        return slash < 0 ? name : name[(slash + 1)..];
    }

    private static string WithoutExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        var slash = name.LastIndexOf('/');
        return dot > slash + 1 ? name[..dot] : name;
    }
}
