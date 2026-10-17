namespace Catcom.Core.Logging;

/// <summary>A thread-safe sink that records every message in memory; for tests and the UI.</summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];

    /// <summary>A snapshot copy of the recorded entries, oldest first.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Discards all recorded entries.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <inheritdoc />
    public void Write(LogLevel level, string message)
    {
        lock (_gate)
        {
            _entries.Add(new LogEntry(level, message));
        }
    }
}
