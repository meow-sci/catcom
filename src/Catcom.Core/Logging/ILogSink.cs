namespace Catcom.Core.Logging;

/// <summary>
///     Destination for <see cref="Log"/> output. The host installs a sink that writes to stdout and the
///     in-game console; tests install a <see cref="MemoryLogSink"/>.
/// </summary>
public interface ILogSink
{
    /// <summary>
    ///     Writes one message. <paramref name="message"/> is the UNformatted text (no prefix, no level
    ///     tag); a sink that produces text output calls <see cref="Log.Format"/> to add them.
    ///     May be called from any thread. Must not throw (a throwing sink is swallowed by <see cref="Log"/>).
    /// </summary>
    /// <param name="level">The message severity.</param>
    /// <param name="message">The unformatted message text.</param>
    void Write(LogLevel level, string message);
}
