namespace KsaScripting.Core.Logging;

/// <summary>A sink that discards everything. The default sink, so tests stay quiet.</summary>
public sealed class NullLogSink : ILogSink
{
    /// <summary>The shared instance.</summary>
    public static NullLogSink Instance { get; } = new();

    private NullLogSink()
    {
    }

    /// <inheritdoc />
    public void Write(LogLevel level, string message)
    {
    }
}
