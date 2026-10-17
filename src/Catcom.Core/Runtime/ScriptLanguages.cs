namespace Catcom.Core.Runtime;

/// <summary>The script language names and their file extensions.</summary>
public static class ScriptLanguages
{
    /// <summary>Lua (run by the LuaJIT runtime, id <c>"lua"</c>).</summary>
    public const string Lua = "lua";

    /// <summary>TypeScript (transpiled and run by the V8 runtime, id <c>"v8"</c>).</summary>
    public const string TypeScript = "typescript";

    /// <summary>Maps a file path to its language by extension (case-insensitive).</summary>
    /// <param name="path">A file path or name.</param>
    /// <returns><see cref="Lua"/> for <c>.lua</c>, <see cref="TypeScript"/> for <c>.ts</c>, otherwise <c>null</c>.</returns>
    public static string? ForPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".lua", StringComparison.OrdinalIgnoreCase))
            return Lua;
        if (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase))
            return TypeScript;
        return null;
    }
}
