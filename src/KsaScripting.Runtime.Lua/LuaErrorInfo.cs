using System.Text.RegularExpressions;

namespace KsaScripting.Runtime.Lua;

/// <summary>A script location extracted from a Lua error message or traceback.</summary>
/// <param name="File">The absolute file path (the scripts directory combined with the chunk-relative path).</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="ChunkName">The chunk-relative name as it appeared in the message, e.g. <c>scripts/ascent.lua</c>.</param>
internal readonly record struct LuaErrorInfo(string File, int Line, string ChunkName)
{
    // Chunk names are "@scripts/<relative path>"; Lua error messages render them as "scripts/<relative path>:<line>:".
    private static readonly Regex ScriptsChunk = new(@"scripts/([^:\r\n]+?):(\d+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Lua shortens chunk names longer than LUA_IDSIZE (60) to "..." plus their last 56 characters.
    private static readonly Regex TruncatedChunk = new(@"\.\.\.([^:\s][^:\r\n]*?\.lua):(\d+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Fallback for chunk names without the scripts/ prefix: "<path>.lua:<line>:".
    private static readonly Regex LuaFile = new(@"(?<![\w./\\-])([\w./\\-]+\.lua):(\d+):", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Finds the first <c>scripts/&lt;path&gt;:&lt;line&gt;:</c> (or, failing that, <c>&lt;path&gt;.lua:&lt;line&gt;:</c>)
    ///     location in <paramref name="message"/> and maps it to <c>Path.Combine(scriptsDir, path)</c>.
    /// </summary>
    /// <param name="message">A Lua error message or traceback.</param>
    /// <param name="scriptsDir">The scripts directory the relative path is resolved against.</param>
    /// <returns>The location, or <c>null</c> when the message contains none.</returns>
    public static LuaErrorInfo? Parse(string message, string scriptsDir) => Parse(message, scriptsDir, null);

    /// <summary>
    ///     As <see cref="Parse(string, string)"/>, preferring an exact path recorded for a loaded chunk name
    ///     (so scripts loaded from outside the scripts directory map back to their real file).
    /// </summary>
    /// <param name="message">A Lua error message or traceback.</param>
    /// <param name="scriptsDir">The scripts directory the relative path is resolved against.</param>
    /// <param name="knownChunks">Chunk name (e.g. <c>scripts/x.lua</c>) to absolute file path, or <c>null</c>.</param>
    /// <returns>The location, or <c>null</c> when the message contains none.</returns>
    public static LuaErrorInfo? Parse(string message, string scriptsDir, IReadOnlyDictionary<string, string>? knownChunks)
    {
        if (string.IsNullOrEmpty(message))
            return null;

        var m = ScriptsChunk.Match(message);
        var truncated = TruncatedChunk.Match(message);
        if (truncated.Success && (!m.Success || truncated.Index <= m.Index) && int.TryParse(truncated.Groups[2].Value, out var truncatedLine))
        {
            // "...<suffix>": recover the chunk by suffix among the chunks the runtime knows about.
            var suffix = truncated.Groups[1].Value;
            if (knownChunks is not null)
            {
                foreach (var (chunk, path) in knownChunks)
                {
                    if (chunk.EndsWith(suffix, StringComparison.Ordinal))
                        return new LuaErrorInfo(path, truncatedLine, chunk);
                }
            }

            return new LuaErrorInfo(Map(scriptsDir, suffix), truncatedLine, suffix);
        }

        if (m.Success && int.TryParse(m.Groups[2].Value, out var line))
        {
            var chunk = "scripts/" + m.Groups[1].Value;
            if (knownChunks is not null && knownChunks.TryGetValue(chunk, out var known))
                return new LuaErrorInfo(known, line, chunk);
            return new LuaErrorInfo(Map(scriptsDir, m.Groups[1].Value), line, chunk);
        }

        m = LuaFile.Match(message);
        if (m.Success && int.TryParse(m.Groups[2].Value, out line))
            return new LuaErrorInfo(Map(scriptsDir, m.Groups[1].Value), line, m.Groups[1].Value);

        return null;
    }

    /// <summary>The message part of a message-handler result (everything before <c>stack traceback:</c>).</summary>
    /// <param name="text">A message handler result: message plus traceback.</param>
    /// <returns>The message without the traceback.</returns>
    public static string MessageOf(string text)
    {
        const string marker = "\nstack traceback:";
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? text : text[..i];
    }

    private static string Map(string scriptsDir, string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        return string.IsNullOrEmpty(scriptsDir) ? normalized : Path.Combine(scriptsDir, normalized);
    }
}
