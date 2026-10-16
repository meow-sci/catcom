namespace KsaScripting.Runtime.Lua.Interop;

/// <summary>
///     Reads <c>require</c>d modules from the scripts directory. Plain managed code (no Lua API); it is used by
///     the private <c>internal.read_module</c> host function.
/// </summary>
internal static class ModuleReader
{
    /// <summary>Upper bound on a module's source size.</summary>
    public const long MaxModuleBytes = 8 * 1024 * 1024;

    /// <summary>
    ///     Resolves <c>&lt;scriptsDir&gt;/&lt;name with '.' replaced by '/'&gt;.lua</c> and reads it, refusing anything
    ///     that resolves outside <paramref name="scriptsDir"/>.
    /// </summary>
    /// <param name="scriptsDir">The scripts directory (module root).</param>
    /// <param name="name">The module name (already restricted by the sandbox to <c>[%w_.]+</c>).</param>
    /// <param name="source">The file contents (UTF-8 BOM stripped).</param>
    /// <param name="chunkName">The Lua chunk name, <c>@scripts/&lt;relative path&gt;</c>.</param>
    /// <param name="fullPath">The absolute path of the file that was read.</param>
    /// <param name="error">The failure message.</param>
    /// <returns><c>true</c> on success.</returns>
    public static bool TryRead(string scriptsDir, string name, out byte[] source, out string chunkName, out string fullPath, out string error)
    {
        source = [];
        chunkName = string.Empty;
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrEmpty(scriptsDir))
        {
            error = $"module '{name}' not found: no scripts directory is configured";
            return false;
        }

        if (!IsValidName(name))
        {
            error = $"module '{name}' not found: invalid module name";
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scriptsDir));
        var relative = name.Replace('.', '/') + ".lua";
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsInside(root, full))
        {
            error = $"module '{name}' resolves outside the scripts directory";
            return false;
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                error = $"module '{name}' not found (looked for scripts/{relative})";
                return false;
            }

            if (!LinksStayInside(root, relative))
            {
                error = $"module '{name}' resolves outside the scripts directory";
                return false;
            }

            if (info.Length > MaxModuleBytes)
            {
                error = $"module '{name}' is too large";
                return false;
            }

            var bytes = File.ReadAllBytes(full);
            source = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;
            chunkName = "@scripts/" + relative;
            fullPath = full;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"module '{name}' could not be read: {ex.Message}";
            return false;
        }
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0 || name.Length > 200)
            return false;
        if (name[0] == '.' || name[^1] == '.' || name.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (var c in name)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.';
            if (!ok)
                return false;
        }

        return true;
    }

    private static bool IsInside(string root, string full)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    // Walks the path below the root: a symbolic link (directory or file) must resolve to a location that is
    // still inside the root, so a link planted in the scripts directory cannot be used to read elsewhere.
    private static bool LinksStayInside(string root, string relative)
    {
        var realRoot = ResolvedRoot(root);
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is null)
                continue;

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
                return false;
            var targetPath = Path.GetFullPath(target.FullName);
            if (!IsInside(root, targetPath) && !IsInside(realRoot, targetPath))
                return false;
        }

        return true;
    }

    private static string ResolvedRoot(string root)
    {
        try
        {
            var target = new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? root : Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return root;
        }
    }
}
