using Catcom.Core.Logging;

namespace Catcom.Ksa.Events;

/// <summary>
///     Plain .NET hooks raised by the save-game patches so the GameMod can persist and restore the script
///     state sidecar next to the game's own save files. They are separate from the <c>save.write</c> /
///     <c>save.load</c> script events: those inform scripts, these let the host do its own I/O. All
///     handlers run on the game thread inside a Harmony patch; a handler that throws is logged and
///     skipped.
/// </summary>
public static class KsaSaveHooks
{
    /// <summary>
    ///     Raised after a save was written successfully (<c>UncompressedSave.Write()</c> returned true),
    ///     after the <c>save.write</c> script event was published. The argument is the save's directory.
    /// </summary>
    public static event Action<string>? SaveWritten;

    /// <summary>
    ///     Raised after a save finished loading (<c>Program.OnGameLoaded()</c>), before the
    ///     <c>save.load</c> script event is published so restored script state is visible to handlers. The
    ///     argument is the loaded save's directory, or <see langword="null"/> when it could not be
    ///     determined.
    /// </summary>
    public static event Action<string?>? GameLoaded;

    internal static void RaiseSaveWritten(string directory)
    {
        var handlers = SaveWritten;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)handler)(directory);
            }
            catch (Exception ex)
            {
                Log.LogOnce("savehook.written", LogLevel.Error, $"a SaveWritten handler threw (logged once): {ex}");
            }
        }
    }

    internal static void RaiseGameLoaded(string? directory)
    {
        var handlers = GameLoaded;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string?>)handler)(directory);
            }
            catch (Exception ex)
            {
                Log.LogOnce("savehook.loaded", LogLevel.Error, $"a GameLoaded handler threw (logged once): {ex}");
            }
        }
    }
}
