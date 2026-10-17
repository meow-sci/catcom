namespace Catcom.Core.Logging;

/// <summary>One captured log message.</summary>
/// <param name="Level">The message severity.</param>
/// <param name="Message">The unformatted message text.</param>
public readonly record struct LogEntry(LogLevel Level, string Message);
