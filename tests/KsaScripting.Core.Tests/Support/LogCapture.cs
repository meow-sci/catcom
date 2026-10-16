using KsaScripting.Core.Logging;

namespace KsaScripting.Core.Tests.Support;

/// <summary>Installs an in-memory log sink (all levels) for the duration of a test and restores the previous one.</summary>
internal sealed class LogCapture : IDisposable
{
    private readonly ILogSink _previous;
    private readonly LogLevel _previousLevel;

    public LogCapture()
    {
        Sink = new MemoryLogSink();
        _previousLevel = Log.MinLevel;
        _previous = Log.SetSink(Sink);
        Log.MinLevel = LogLevel.Debug;
        Log.ResetOnce();
    }

    public MemoryLogSink Sink { get; }

    public IReadOnlyList<LogEntry> Entries => Sink.Entries;

    public bool Contains(LogLevel level, string text)
        => Sink.Entries.Any(e => e.Level == level && e.Message.Contains(text, StringComparison.Ordinal));

    public void Dispose()
    {
        Log.SetSink(_previous);
        Log.MinLevel = _previousLevel;
        Log.ResetOnce();
    }
}
