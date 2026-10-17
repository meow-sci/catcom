namespace Catcom.Core.Config;

/// <summary>
///     Crash-safe file writes: the content is written to a sibling temp file and renamed over the
///     destination, so an interrupted write can never leave a truncated file behind. The parent
///     directory is created when missing.
/// </summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/> atomically (UTF-8, no BOM).</summary>
    /// <param name="path">The destination file.</param>
    /// <param name="contents">The text to write.</param>
    public static void WriteAllText(string path, string contents)
        => Write(path, tmp => File.WriteAllText(tmp, contents));

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/> atomically.</summary>
    /// <param name="path">The destination file.</param>
    /// <param name="bytes">The bytes to write.</param>
    public static void WriteAllBytes(string path, byte[] bytes)
        => Write(path, tmp => File.WriteAllBytes(tmp, bytes));

    private static void Write(string path, Action<string> writeTemp)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tmp = path + ".tmp";
        try
        {
            writeTemp(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // Best effort: the original failure is the one worth reporting.
            }

            throw;
        }
    }
}
