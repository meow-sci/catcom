using KsaScripting.Core.Config;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Persistence;

namespace KsaScripting.GameMod.Persistence;

/// <summary>
///     The per-save script state file: <c>&lt;saveDir&gt;/ksa-scripting-state.toml</c>, written after the
///     game wrote a save and read back after a save finished loading. Neither method ever throws; a
///     <see langword="null"/> directory (the game could not tell) skips the operation.
/// </summary>
internal static class StateSidecar
{
    /// <summary>The sidecar file name.</summary>
    public const string FileName = "ksa-scripting-state.toml";

    /// <summary>
    ///     Writes the state next to the save (atomic temp + rename). With nothing to store, a stale
    ///     sidecar of an earlier save in the same folder is removed instead.
    /// </summary>
    /// <param name="saveDir">The save's directory.</param>
    /// <param name="state">The state to persist.</param>
    public static void Save(string? saveDir, ScriptStateStore state)
    {
        if (string.IsNullOrEmpty(saveDir))
            return;

        try
        {
            var path = Path.Combine(saveDir, FileName);
            var toml = state.SerializeToml();
            if (toml.Length == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            AtomicFile.WriteAllText(path, toml);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write the script state sidecar in '{saveDir}': {ex.Message}");
        }
    }

    /// <summary>
    ///     Restores the state from the save's sidecar. A save without one clears the state (so one save's
    ///     values never leak into another).
    /// </summary>
    /// <param name="saveDir">The loaded save's directory.</param>
    /// <param name="state">The store to fill.</param>
    public static void Load(string? saveDir, ScriptStateStore state)
    {
        if (string.IsNullOrEmpty(saveDir))
            return;

        try
        {
            var path = Path.Combine(saveDir, FileName);
            state.LoadToml(File.Exists(path) ? File.ReadAllText(path) : "");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the script state sidecar in '{saveDir}': {ex.Message}");
        }
    }
}
