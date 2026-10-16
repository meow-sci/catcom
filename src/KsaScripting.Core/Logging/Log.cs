using System.Collections.Concurrent;

namespace KsaScripting.Core.Logging;

/// <summary>
///     The process-wide logger. Messages go to a pluggable <see cref="ILogSink"/> (default: discard);
///     the GameMod installs a sink writing to stdout and the in-game console. Nothing here throws.
/// </summary>
public static class Log
{
    /// <summary>The text prepended to every formatted message.</summary>
    public const string Prefix = "[ksa-scripting]";

    private static readonly ConcurrentDictionary<string, byte> OnceKeys = new(StringComparer.Ordinal);
    private static ILogSink _sink = NullLogSink.Instance;
    private static int _minLevel = (int)LogLevel.Info;

    /// <summary>The current sink (default <see cref="NullLogSink.Instance"/>).</summary>
    public static ILogSink Sink => Volatile.Read(ref _sink);

    /// <summary>Messages below this level are dropped. Default <see cref="LogLevel.Info"/>.</summary>
    public static LogLevel MinLevel
    {
        get => (LogLevel)Volatile.Read(ref _minLevel);
        set => Volatile.Write(ref _minLevel, (int)value);
    }

    /// <summary>Installs <paramref name="sink"/> (thread-safe) and returns the previous sink.</summary>
    /// <param name="sink">The new sink; <c>null</c> installs <see cref="NullLogSink.Instance"/>.</param>
    /// <returns>The sink that was installed before.</returns>
    public static ILogSink SetSink(ILogSink sink)
        => Interlocked.Exchange(ref _sink, sink ?? NullLogSink.Instance);

    /// <summary>Restores the default <see cref="NullLogSink"/>.</summary>
    public static void ResetSink() => Interlocked.Exchange(ref _sink, NullLogSink.Instance);

    /// <summary>
    ///     Writes a message to the sink unless it is below <see cref="MinLevel"/>. A sink that throws is
    ///     swallowed: logging must never fault the caller (it may be on the sim path).
    /// </summary>
    /// <param name="level">The message severity.</param>
    /// <param name="message">The unformatted message text.</param>
    public static void Write(LogLevel level, string message)
    {
        if (level < MinLevel)
            return;

        try
        {
            Sink.Write(level, message);
        }
        catch
        {
            // A broken sink must not take down the caller.
        }
    }

    /// <summary>Writes a <see cref="LogLevel.Debug"/> message.</summary>
    /// <param name="message">The message text.</param>
    public static void Debug(string message) => Write(LogLevel.Debug, message);

    /// <summary>Writes a <see cref="LogLevel.Info"/> message.</summary>
    /// <param name="message">The message text.</param>
    public static void Info(string message) => Write(LogLevel.Info, message);

    /// <summary>Writes a <see cref="LogLevel.Warn"/> message.</summary>
    /// <param name="message">The message text.</param>
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    /// <summary>
    ///     Writes a <see cref="LogLevel.Error"/> message; when <paramref name="exception"/> is given its
    ///     full <c>ToString()</c> is appended after a newline.
    /// </summary>
    /// <param name="message">The message text.</param>
    /// <param name="exception">An optional exception whose details are appended.</param>
    public static void Error(string message, Exception? exception = null)
        => Write(LogLevel.Error, exception is null ? message : message + "\n" + exception);

    /// <summary>
    ///     Writes the message only the first time <paramref name="key"/> is seen (until
    ///     <see cref="ResetOnce"/>). Use for repeated faults so a broken script cannot flood the log.
    /// </summary>
    /// <param name="key">The deduplication key.</param>
    /// <param name="level">The message severity.</param>
    /// <param name="message">The unformatted message text.</param>
    /// <returns><c>true</c> if this call wrote (first time for the key); otherwise <c>false</c>.</returns>
    public static bool LogOnce(string key, LogLevel level, string message)
    {
        if (!OnceKeys.TryAdd(key, 0))
            return false;

        Write(level, message);
        return true;
    }

    /// <summary>Re-arms <see cref="LogOnce"/> for one key, or for every key when <paramref name="key"/> is <c>null</c>.</summary>
    /// <param name="key">The key to re-arm, or <c>null</c> to clear all keys.</param>
    public static void ResetOnce(string? key = null)
    {
        if (key is null)
            OnceKeys.Clear();
        else
            OnceKeys.TryRemove(key, out _);
    }

    /// <summary>Formats a message as <c>[ksa-scripting] info: &lt;message&gt;</c> (level in lowercase).</summary>
    /// <param name="level">The message severity.</param>
    /// <param name="message">The unformatted message text.</param>
    /// <returns>The prefixed, level-tagged text.</returns>
    public static string Format(LogLevel level, string message)
        => $"{Prefix} {LevelName(level)}: {message}";

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warn => "warn",
        LogLevel.Error => "error",
        _ => level.ToString().ToLowerInvariant(),
    };
}
