using System.Text;

namespace KsaScripting.Runtime.V8;

/// <summary>
///     Turns paths into one spelling per file, from what is ON DISK rather than from what the OS usually does: every existing
///     component is replaced by the name its directory lists (so <c>/x/SCRIPTS/Lib.TS</c> and <c>/x/scripts/lib.ts</c>
///     become the same string on a case-folding file system such as macOS APFS or NTFS, and stay different on a
///     case-sensitive one), and, for <see cref="Real"/>, symbolic links are followed as well. Containment checks and module
///     identity can then use plain ordinal comparison and be right on every file system, including a case-sensitive volume on
///     macOS. Only the directories handed to <see cref="Pin"/> (the scripts folder and its ancestors, which are fixed by
///     configuration and expensive to list - think of a temp folder with thousands of entries) are remembered; everything
///     below them is looked at afresh on every call, so files and links that appear or change later are seen.
/// </summary>
internal sealed class PathCanonicalizer
{
    private const int MaxLinkDepth = 32;

    private readonly object _gate = new();
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _caseOnly = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _real = new(StringComparer.Ordinal);

    /// <summary>Declares a directory (and so all of its ancestors) stable: its resolution is computed once and remembered.</summary>
    /// <param name="directory">An absolute, normalised directory path, spelled as it will be asked for.</param>
    public void Pin(string directory)
    {
        lock (_gate)
        {
            for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
                _pinned.Add(current);
        }
    }

    /// <summary>The path with the on-disk spelling of every existing component (links are left as they are).</summary>
    /// <param name="fullPath">An absolute, normalised path.</param>
    /// <returns>The same path, spelled as the file system lists it; components that do not exist keep their spelling.</returns>
    public string CaseExact(string fullPath) => Resolve(fullPath, followLinks: false, depth: 0);

    /// <summary>The "realpath": on-disk spelling of every component with all symbolic links followed.</summary>
    /// <param name="fullPath">An absolute, normalised path.</param>
    /// <returns>The resolved path; components that do not exist keep their spelling.</returns>
    public string Real(string fullPath) => Resolve(fullPath, followLinks: true, depth: 0);

    private string Resolve(string fullPath, bool followLinks, int depth)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        var trimmed = fullPath.Length > root.Length ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : fullPath;
        var parent = Path.GetDirectoryName(trimmed);
        if (parent is null || trimmed.Length <= root.Length)
            return NormalizeRoot(trimmed);

        var parentResolved = ResolveDirectory(parent, followLinks, depth);
        var name = Path.GetFileName(trimmed);
        var current = Path.Combine(parentResolved, TrueName(parentResolved, name) ?? name);
        if (followLinks && TryGetLinkTarget(current, out var target))
            return depth >= MaxLinkDepth ? current : Resolve(target, followLinks: true, depth + 1);

        return current;
    }

    private string ResolveDirectory(string directory, bool followLinks, int depth)
    {
        var cache = followLinks ? _real : _caseOnly;
        bool pinned;
        lock (_gate)
        {
            pinned = _pinned.Contains(directory);
            if (pinned && cache.TryGetValue(directory, out var known))
                return known;
        }

        var resolved = Resolve(directory, followLinks, depth);
        if (pinned)
        {
            lock (_gate)
                cache[directory] = resolved;
        }

        return resolved;
    }

    /// <summary>The spelling the directory lists for an existing entry; <c>null</c> when the entry does not exist (or cannot be listed).</summary>
    private static string? TrueName(string directory, string name)
    {
        try
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) && !Directory.Exists(path))
                return null;

            // The file system found it, so on a case-folding volume a differently-cased listing entry IS this entry; on a
            // case-sensitive volume only the exact spelling exists and is found first.
            string? equivalent = null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var listed = Path.GetFileName(entry);
                if (string.Equals(listed, name, StringComparison.Ordinal))
                    return listed;

                if (equivalent is null && Equivalent(listed, name))
                    equivalent = listed;
            }

            return equivalent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not listable: the spelling cannot be proven, so it is kept as given (a later ordinal comparison then fails closed).
            return null;
        }
    }

    // Case-insensitive, and insensitive to Unicode normalisation (APFS stores names as given but matches NFC and NFD alike).
    private static bool Equivalent(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    private static bool TryGetLinkTarget(string path, out string target)
    {
        target = "";
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget is null)
                return false;

            var final = info.ResolveLinkTarget(returnFinalTarget: true);
            if (final is null)
                return false;

            target = Path.GetFullPath(final.FullName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable link cannot be proven safe by following it; the lexical path stays.
            return false;
        }
    }

    // A Windows drive letter is spelled in upper case by convention; the roots themselves cannot be listed.
    private static string NormalizeRoot(string path)
        => OperatingSystem.IsWindows() && path.Length >= 2 && path[1] == ':' && char.IsAsciiLetterLower(path[0])
            ? char.ToUpperInvariant(path[0]) + path[1..]
            : path;
}
