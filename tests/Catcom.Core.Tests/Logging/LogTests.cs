using Catcom.Core.Logging;

namespace Catcom.Core.Tests.Logging;

[TestFixture]
[NonParallelizable]
public sealed class LogTests
{
    private ILogSink _previousSink = null!;
    private LogLevel _previousMinLevel;
    private MemoryLogSink _sink = null!;

    [SetUp]
    public void SetUp()
    {
        _previousMinLevel = Log.MinLevel;
        _sink = new MemoryLogSink();
        _previousSink = Log.SetSink(_sink);
        Log.MinLevel = LogLevel.Debug;
        Log.ResetOnce();
    }

    [TearDown]
    public void TearDown()
    {
        Log.SetSink(_previousSink);
        Log.MinLevel = _previousMinLevel;
        Log.ResetOnce();
    }

    private sealed class ThrowingSink : ILogSink
    {
        public int Calls { get; private set; }

        public void Write(LogLevel level, string message)
        {
            Calls++;
            throw new InvalidOperationException("sink exploded");
        }
    }

    [TestCase(LogLevel.Debug, "[catcom] debug: hi")]
    [TestCase(LogLevel.Info, "[catcom] info: hi")]
    [TestCase(LogLevel.Warn, "[catcom] warn: hi")]
    [TestCase(LogLevel.Error, "[catcom] error: hi")]
    public void Format_PrefixesAndLowercasesTheLevel(LogLevel level, string expected)
        => Assert.That(Log.Format(level, "hi"), Is.EqualTo(expected));

    [Test]
    public void Prefix_IsPinned()
        => Assert.That(Log.Prefix, Is.EqualTo("[catcom]"));

    [Test]
    public void Helpers_WriteUnformattedMessagesAtTheirLevel()
    {
        Log.Debug("d");
        Log.Info("i");
        Log.Warn("w");
        Log.Error("e");

        Assert.That(_sink.Entries, Is.EqualTo(new[]
        {
            new LogEntry(LogLevel.Debug, "d"),
            new LogEntry(LogLevel.Info, "i"),
            new LogEntry(LogLevel.Warn, "w"),
            new LogEntry(LogLevel.Error, "e"),
        }));
    }

    [Test]
    public void Error_AppendsTheException()
    {
        Log.Error("failed", new InvalidOperationException("because"));

        var entry = _sink.Entries.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Level, Is.EqualTo(LogLevel.Error));
            Assert.That(entry.Message, Does.StartWith("failed\n"));
            Assert.That(entry.Message, Does.Contain("InvalidOperationException"));
            Assert.That(entry.Message, Does.Contain("because"));
        });
    }

    [Test]
    public void MinLevel_FiltersLowerLevels()
    {
        Log.MinLevel = LogLevel.Warn;
        Log.Debug("d");
        Log.Info("i");
        Log.Warn("w");
        Log.Error("e");

        Assert.That(_sink.Entries.Select(e => e.Message), Is.EqualTo(new[] { "w", "e" }));
    }

    [Test]
    public void DefaultMinLevel_IsInfo()
    {
        // Verified against a fresh process default: the static is restored in TearDown, so read the
        // value that was in effect before SetUp changed it.
        Assert.That(_previousMinLevel, Is.EqualTo(LogLevel.Info));
    }

    [Test]
    public void LogOnce_WritesOncePerKey_UntilReset()
    {
        var first = Log.LogOnce("k", LogLevel.Warn, "one");
        var second = Log.LogOnce("k", LogLevel.Warn, "two");
        var other = Log.LogOnce("k2", LogLevel.Warn, "three");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
            Assert.That(other, Is.True);
            Assert.That(_sink.Entries.Select(e => e.Message), Is.EqualTo(new[] { "one", "three" }));
        });

        Log.ResetOnce("k");
        Assert.That(Log.LogOnce("k", LogLevel.Warn, "again"), Is.True, "ResetOnce(key) re-arms that key");
        Assert.That(Log.LogOnce("k2", LogLevel.Warn, "no"), Is.False, "other keys stay armed");

        Log.ResetOnce();
        Assert.That(Log.LogOnce("k2", LogLevel.Warn, "after reset"), Is.True, "ResetOnce() re-arms every key");
    }

    [Test]
    public void LogOnce_BelowMinLevel_StillConsumesTheKey()
    {
        Log.MinLevel = LogLevel.Error;
        Assert.Multiple(() =>
        {
            Assert.That(Log.LogOnce("quiet", LogLevel.Debug, "hidden"), Is.True);
            Assert.That(_sink.Entries, Is.Empty);
        });
    }

    [Test]
    public void ThrowingSink_IsSwallowed()
    {
        var thrower = new ThrowingSink();
        Log.SetSink(thrower);

        Assert.Multiple(() =>
        {
            Assert.That(() => Log.Info("x"), Throws.Nothing);
            Assert.That(() => Log.Error("x", new Exception("y")), Throws.Nothing);
            Assert.That(() => Log.LogOnce("boom", LogLevel.Warn, "x"), Throws.Nothing);
            Assert.That(thrower.Calls, Is.EqualTo(3));
        });
    }

    [Test]
    public void SetSink_ReturnsThePreviousSink()
    {
        var next = new MemoryLogSink();
        var previous = Log.SetSink(next);

        Assert.Multiple(() =>
        {
            Assert.That(previous, Is.SameAs(_sink));
            Assert.That(Log.Sink, Is.SameAs(next));
        });

        Log.Info("routed");
        Assert.Multiple(() =>
        {
            Assert.That(next.Entries, Has.Count.EqualTo(1));
            Assert.That(_sink.Entries, Is.Empty);
        });
    }

    [Test]
    public void ResetSink_RestoresTheNullSink()
    {
        Log.ResetSink();
        Assert.That(Log.Sink, Is.SameAs(NullLogSink.Instance));
        Assert.That(() => Log.Info("dropped"), Throws.Nothing);
    }

    [Test]
    public void SetSink_Null_FallsBackToTheNullSink()
    {
        Log.SetSink(null!);
        Assert.That(Log.Sink, Is.SameAs(NullLogSink.Instance));
    }

    [Test]
    public void MemoryLogSink_EntriesIsASnapshot_AndClearWorks()
    {
        _sink.Write(LogLevel.Info, "a");
        var snapshot = _sink.Entries;
        _sink.Write(LogLevel.Info, "b");

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Has.Count.EqualTo(1));
            Assert.That(_sink.Entries, Has.Count.EqualTo(2));
        });

        _sink.Clear();
        Assert.That(_sink.Entries, Is.Empty);
    }

    [Test]
    public async Task MemoryLogSink_IsThreadSafe()
    {
        var tasks = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
                Log.Info($"{t}:{i}");
        })).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(_sink.Entries, Has.Count.EqualTo(1000));
    }
}
