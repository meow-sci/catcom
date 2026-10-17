namespace Catcom.Core.Logging;

/// <summary>Severity of a log message. Ordered: a higher value is more severe.</summary>
public enum LogLevel
{
    /// <summary>Verbose diagnostics, normally filtered out.</summary>
    Debug = 0,

    /// <summary>Routine informational messages.</summary>
    Info = 1,

    /// <summary>Something unexpected that the mod recovered from.</summary>
    Warn = 2,

    /// <summary>A failure that lost work or disabled a feature.</summary>
    Error = 3,
}
