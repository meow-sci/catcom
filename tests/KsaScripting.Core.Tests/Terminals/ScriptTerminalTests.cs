using System.Text;
using KsaScripting.Core.Hooks;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Terminals;
using KsaScripting.Core.Tests.Support;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Tests.Terminals;

[TestFixture]
public sealed class ScriptTerminalTests
{
    private const string Begin = "\x1b[?2026h";
    private const string End = "\x1b[?2026l";

    private sealed class RecordingSink : ITerminalSink
    {
        public List<byte[]> Writes { get; } = [];

        public List<string> Terminations { get; } = [];

        public Exception? ThrowOnWrite { get; set; }

        public string Text => Encoding.UTF8.GetString(Writes.SelectMany(w => w).ToArray());

        public void Write(ReadOnlySpan<byte> bytes)
        {
            if (ThrowOnWrite is not null)
                throw ThrowOnWrite;
            Writes.Add(bytes.ToArray());
        }

        public void Terminate(string message) => Terminations.Add(message);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static ScriptTerminal NewTerminal(out List<ScriptEvent> events, int bufferKiB = 256, string script = "t.lua")
    {
        var terminal = new ScriptTerminal(7, script, bufferKiB);
        var list = new List<ScriptEvent>();
        terminal.Event += list.Add;
        events = list;
        return terminal;
    }

    // ---------------------------------------------------------------- ring buffer

    [Test]
    public void Write_KeepsTheLastBufferBytes()
    {
        var terminal = NewTerminal(out _, bufferKiB: 1);
        var data = Enumerable.Range(0, 3 * 1024).Select(i => (byte)(i % 251)).ToArray();

        terminal.Write(data);
        var ring = terminal.CopyRingBuffer();

        Assert.Multiple(() =>
        {
            Assert.That(ring, Has.Length.EqualTo(1024));
            Assert.That(ring, Is.EqualTo(data[^1024..]));
        });
    }

    [Test]
    public void Write_WrapsAroundCorrectly_AcrossManySmallWrites()
    {
        var terminal = NewTerminal(out _, bufferKiB: 1);
        var all = new List<byte>();
        for (var i = 0; i < 100; i++)
        {
            var chunk = Enumerable.Range(0, 37).Select(j => (byte)(i + j)).ToArray();
            all.AddRange(chunk);
            terminal.Write(chunk);
        }

        Assert.That(terminal.CopyRingBuffer(), Is.EqualTo(all.TakeLast(1024).ToArray()));
    }

    [Test]
    public void Write_BelowCapacity_KeepsEverythingInOrder()
    {
        var terminal = NewTerminal(out _, bufferKiB: 1);

        terminal.Write(Bytes("hello "));
        terminal.Write(Bytes("world"));

        Assert.That(Encoding.UTF8.GetString(terminal.CopyRingBuffer()), Is.EqualTo("hello world"));
    }

    // ---------------------------------------------------------------- flush

    [Test]
    public void Flush_WrapsPendingOutputInDec2026_OneWritePerSink()
    {
        var terminal = NewTerminal(out _);
        var one = new RecordingSink();
        var two = new RecordingSink();
        terminal.Attach(one, 80, 24);
        terminal.Attach(two, 80, 24);
        one.Writes.Clear();
        two.Writes.Clear();

        terminal.Write(Bytes("abc"));
        terminal.Write(Bytes("def"));
        Assert.That(one.Writes, Is.Empty, "nothing is sent before the flush");
        terminal.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(one.Writes, Has.Count.EqualTo(1));
            Assert.That(two.Writes, Has.Count.EqualTo(1));
            Assert.That(one.Text, Is.EqualTo(Begin + "abcdef" + End));
            Assert.That(two.Text, Is.EqualTo(Begin + "abcdef" + End));
        });
    }

    [Test]
    public void Flush_WithNothingPending_WritesNothing()
    {
        var terminal = NewTerminal(out _);
        var sink = new RecordingSink();
        terminal.Attach(sink, 80, 24);
        sink.Writes.Clear();

        terminal.Flush();

        Assert.That(sink.Writes, Is.Empty);
    }

    [Test]
    public void Flush_WithoutSinks_DiscardsThePending_SoAnAttachDoesNotReceiveItTwice()
    {
        var terminal = NewTerminal(out _);
        terminal.Write(Bytes("screen"));
        terminal.Flush();

        var sink = new RecordingSink();
        terminal.Attach(sink, 80, 24);
        terminal.Flush();

        Assert.That(sink.Text, Is.EqualTo(Begin + "screen" + End), "only the ring-buffer replay");
    }

    // ---------------------------------------------------------------- attach

    [Test]
    public void Attach_ReplaysTheRingBufferToTheNewSinkOnly_WrappedInDec2026()
    {
        var terminal = NewTerminal(out _);
        var first = new RecordingSink();
        terminal.Attach(first, 80, 24);
        terminal.Write(Bytes("before"));
        terminal.Flush();
        first.Writes.Clear();

        var second = new RecordingSink();
        terminal.Attach(second, 100, 30);

        Assert.Multiple(() =>
        {
            Assert.That(second.Writes, Has.Count.EqualTo(1), "one Write for the whole replay");
            Assert.That(second.Text, Is.EqualTo(Begin + "before" + End));
            Assert.That(first.Writes, Is.Empty, "the existing sink gets no replay");
        });
    }

    [Test]
    public void Attach_WithAnEmptyRingBuffer_ReplaysNothing()
    {
        var terminal = NewTerminal(out _);
        var sink = new RecordingSink();

        terminal.Attach(sink, 80, 24);

        Assert.That(sink.Writes, Is.Empty);
    }

    [Test]
    public void Attach_FlushesPendingToTheExistingSinksFirst_SoNoBytesAreDuplicated()
    {
        var terminal = NewTerminal(out _);
        var old = new RecordingSink();
        terminal.Attach(old, 80, 24);
        old.Writes.Clear();

        terminal.Write(Bytes("pending"));
        var fresh = new RecordingSink();
        terminal.Attach(fresh, 80, 24);
        terminal.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(old.Text, Is.EqualTo(Begin + "pending" + End), "delivered once, by the attach-time flush");
            Assert.That(fresh.Text, Is.EqualTo(Begin + "pending" + End), "delivered once, by the replay");
        });
    }

    [Test]
    public void Attach_RaisesAttachThenResize_WithTheSmallestGrid()
    {
        var terminal = NewTerminal(out var events);

        terminal.Attach(new RecordingSink(), 100, 40);

        Assert.That(events.Select(e => e.Type), Is.EqualTo(new[] { "term.attach", "term.resize" }));
        Assert.Multiple(() =>
        {
            Assert.That(events[0].Payload["attached"].AsNumber(), Is.EqualTo(1));
            Assert.That(events[1].Payload["cols"].AsNumber(), Is.EqualTo(100));
            Assert.That(events[1].Payload["rows"].AsNumber(), Is.EqualTo(40));
            Assert.That(terminal.Attached, Is.EqualTo(1));
        });
    }

    [Test]
    public void Attach_TheSameSinkTwice_IsNotDuplicated()
    {
        var terminal = NewTerminal(out _);
        var sink = new RecordingSink();

        terminal.Attach(sink, 80, 24);
        terminal.Attach(sink, 90, 30);

        Assert.Multiple(() =>
        {
            Assert.That(terminal.Attached, Is.EqualTo(1));
            Assert.That(terminal.Cols, Is.EqualTo(90));
        });
    }

    // ---------------------------------------------------------------- grid

    [Test]
    public void TheGrid_IsTheSmallestColsAndRowsAcrossTheAttachedSinks()
    {
        var terminal = NewTerminal(out var events);
        var wide = new RecordingSink();
        var tall = new RecordingSink();

        Assert.Multiple(() =>
        {
            Assert.That(terminal.Cols, Is.EqualTo(ScriptTerminal.DefaultCols));
            Assert.That(terminal.Rows, Is.EqualTo(ScriptTerminal.DefaultRows));
        });

        terminal.Attach(wide, 120, 30);
        terminal.Attach(tall, 90, 50);
        Assert.Multiple(() =>
        {
            Assert.That(terminal.Cols, Is.EqualTo(90));
            Assert.That(terminal.Rows, Is.EqualTo(30));
        });

        events.Clear();
        terminal.NotifyResize(wide, 100, 30);
        Assert.That(events, Is.Empty, "the smallest grid did not change");

        terminal.NotifyResize(wide, 60, 20);
        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Type, Is.EqualTo("term.resize"));
            Assert.That(events[0].Payload["cols"].AsNumber(), Is.EqualTo(60));
            Assert.That(events[0].Payload["rows"].AsNumber(), Is.EqualTo(20));
            Assert.That(terminal.Cols, Is.EqualTo(60));
            Assert.That(terminal.Rows, Is.EqualTo(20));
        });
    }

    [Test]
    public void NotifyResize_OfAnUnknownSink_IsIgnored()
    {
        var terminal = NewTerminal(out var events);

        terminal.NotifyResize(new RecordingSink(), 10, 10);

        Assert.Multiple(() =>
        {
            Assert.That(events, Is.Empty);
            Assert.That(terminal.Cols, Is.EqualTo(ScriptTerminal.DefaultCols));
        });
    }

    // ---------------------------------------------------------------- detach

    [Test]
    public void Detach_RaisesDetach_AndResizeWhenTheSmallestGridGrows()
    {
        var terminal = NewTerminal(out var events);
        var small = new RecordingSink();
        var big = new RecordingSink();
        terminal.Attach(small, 50, 20);
        terminal.Attach(big, 100, 40);
        events.Clear();

        terminal.Detach(small);

        Assert.Multiple(() =>
        {
            Assert.That(events.Select(e => e.Type), Is.EqualTo(new[] { "term.detach", "term.resize" }));
            Assert.That(events[0].Payload["attached"].AsNumber(), Is.EqualTo(1));
            Assert.That(events[1].Payload["cols"].AsNumber(), Is.EqualTo(100));
            Assert.That(terminal.Attached, Is.EqualTo(1));
        });
    }

    [Test]
    public void Detach_OfTheLastSink_RaisesNoResize_AndTheGridReturnsToTheDefault()
    {
        var terminal = NewTerminal(out var events);
        var sink = new RecordingSink();
        terminal.Attach(sink, 100, 40);
        events.Clear();

        terminal.Detach(sink);

        Assert.Multiple(() =>
        {
            Assert.That(events.Select(e => e.Type), Is.EqualTo(new[] { "term.detach" }));
            Assert.That(terminal.Cols, Is.EqualTo(ScriptTerminal.DefaultCols));
            Assert.That(terminal.Rows, Is.EqualTo(ScriptTerminal.DefaultRows));
        });
    }

    [Test]
    public void Detach_OfAnUnknownSink_DoesNothing()
    {
        var terminal = NewTerminal(out var events);

        terminal.Detach(new RecordingSink());

        Assert.That(events, Is.Empty);
    }

    [Test]
    public void Detach_OfTheLastSink_RaisesLastDetached_OnlyWhenStopWhenDetachedIsSet()
    {
        var terminal = NewTerminal(out _);
        var raised = new List<ScriptTerminal>();
        terminal.LastDetached += raised.Add;
        var one = new RecordingSink();
        var two = new RecordingSink();

        terminal.Attach(one, 80, 24);
        terminal.Detach(one);
        Assert.That(raised, Is.Empty, "the default policy keeps running");

        terminal.StopWhenDetached = true;
        terminal.Attach(one, 80, 24);
        terminal.Attach(two, 80, 24);
        terminal.Detach(one);
        Assert.That(raised, Is.Empty, "a sink is still attached");
        terminal.Detach(two);

        Assert.That(raised, Is.EqualTo(new[] { terminal }));
    }

    [Test]
    public void ASinkThatThrowsOnWrite_IsCaught_LoggedAndDetached()
    {
        using var log = new LogCapture();
        var terminal = NewTerminal(out var events);
        var bad = new RecordingSink { ThrowOnWrite = new InvalidOperationException("surface gone") };
        var good = new RecordingSink();
        terminal.Attach(bad, 80, 24);
        terminal.Attach(good, 80, 24);
        good.Writes.Clear();
        events.Clear();

        terminal.Write(Bytes("x"));
        Assert.DoesNotThrow(() => terminal.Flush());

        Assert.Multiple(() =>
        {
            Assert.That(terminal.Attached, Is.EqualTo(1));
            Assert.That(good.Text, Is.EqualTo(Begin + "x" + End));
            Assert.That(events.Select(e => e.Type), Does.Contain("term.detach"));
            Assert.That(log.Contains(LogLevel.Warn, "surface gone"), Is.True);
        });
    }

    // ---------------------------------------------------------------- input

    [Test]
    public void EnqueueInput_RaisesTermData_AndTryReadInputConcatenatesTheChunks()
    {
        var terminal = NewTerminal(out var events);

        terminal.EnqueueInput(Bytes("ab"));
        terminal.EnqueueInput(Bytes("cd"));

        Assert.Multiple(() =>
        {
            Assert.That(terminal.HasInput, Is.True);
            Assert.That(events.Select(e => e.Type), Is.EqualTo(new[] { "term.data", "term.data" }));
            Assert.That(Encoding.UTF8.GetString(events[0].Payload["data"].AsBytes().Span), Is.EqualTo("ab"));
        });

        Assert.That(terminal.TryReadInput(out var all), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(Encoding.UTF8.GetString(all.Span), Is.EqualTo("abcd"));
            Assert.That(terminal.HasInput, Is.False);
            Assert.That(terminal.TryReadInput(out var none), Is.False);
            Assert.That(none.IsEmpty, Is.True);
        });
    }

    [Test]
    public void EnqueueInput_WithCtrlC_AlsoRaisesTermCancel()
    {
        var terminal = NewTerminal(out var events);

        terminal.EnqueueInput(new byte[] { (byte)'a', 0x03 });
        terminal.EnqueueInput(Bytes("plain"));

        Assert.That(events.Select(e => e.Type), Is.EqualTo(new[] { "term.data", "term.cancel", "term.data" }));
    }

    [Test]
    public void EnqueueInput_IsBounded_DroppingTheOldestBytes()
    {
        using var log = new LogCapture();
        var terminal = NewTerminal(out _);
        var chunk = new byte[40 * 1024];

        Array.Fill(chunk, (byte)'a');
        terminal.EnqueueInput(chunk);
        Array.Fill(chunk, (byte)'b');
        terminal.EnqueueInput(chunk);
        terminal.TryReadInput(out var read);

        Assert.Multiple(() =>
        {
            Assert.That(read.Length, Is.EqualTo(ScriptTerminal.MaxQueuedInputBytes));
            Assert.That(read.Span[^1], Is.EqualTo((byte)'b'));
            Assert.That(read.Span[0], Is.EqualTo((byte)'a'), "24 KiB of the old chunk survive");
            Assert.That(read.Span[24 * 1024 - 1], Is.EqualTo((byte)'a'));
            Assert.That(read.Span[24 * 1024], Is.EqualTo((byte)'b'));
            Assert.That(log.Contains(LogLevel.Warn, "not being read"), Is.True);
        });
    }

    [Test]
    public void EnqueueInput_AChunkLargerThanTheBound_KeepsOnlyItsTail()
    {
        var terminal = NewTerminal(out _);
        var huge = Enumerable.Range(0, 100 * 1024).Select(i => (byte)(i % 200)).ToArray();

        terminal.EnqueueInput(huge);
        terminal.TryReadInput(out var read);

        Assert.That(read.ToArray(), Is.EqualTo(huge[^ScriptTerminal.MaxQueuedInputBytes..]));
    }

    // ---------------------------------------------------------------- title / policy / terminate

    [Test]
    public void SetTitle_EmitsOsc2_WithControlCharactersStripped()
    {
        var terminal = NewTerminal(out _);
        var sink = new RecordingSink();
        terminal.Attach(sink, 80, 24);
        sink.Writes.Clear();

        terminal.SetTitle("Door\x1b panel\r\n\x07!");
        terminal.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(terminal.Title, Is.EqualTo("Door panel!"));
            Assert.That(sink.Text, Is.EqualTo(Begin + "\x1b]2;Door panel!\x07" + End));
        });
    }

    [Test]
    public void Terminate_FlushesThenWritesTheMessage_CallsTerminate_AndDetachesEveryoneSilently()
    {
        var terminal = NewTerminal(out var events);
        var one = new RecordingSink();
        var two = new RecordingSink();
        terminal.Attach(one, 80, 24);
        terminal.Attach(two, 80, 24);
        one.Writes.Clear();
        two.Writes.Clear();
        events.Clear();

        terminal.Write(Bytes("last words"));
        terminal.Terminate("[script exited]");

        Assert.Multiple(() =>
        {
            Assert.That(one.Text, Is.EqualTo(Begin + "last words" + End + "\r\n[script exited]\r\n"));
            Assert.That(two.Text, Is.EqualTo(one.Text));
            Assert.That(one.Terminations, Is.EqualTo(new[] { "[script exited]" }));
            Assert.That(two.Terminations, Is.EqualTo(new[] { "[script exited]" }));
            Assert.That(terminal.Attached, Is.Zero);
            Assert.That(events, Is.Empty, "no events after terminate");
        });

        terminal.EnqueueInput(Bytes("late"));
        terminal.Write(Bytes("late output"));
        terminal.Flush();
        terminal.Terminate("again");
        Assert.Multiple(() =>
        {
            Assert.That(events, Is.Empty);
            Assert.That(one.Terminations, Has.Count.EqualTo(1));
        });
    }

    // ---------------------------------------------------------------- events

    [Test]
    public void Events_CarryTheTargetScriptAndTheTerminalId()
    {
        var terminal = NewTerminal(out var events, script: "door.ts");

        terminal.Attach(new RecordingSink(), 80, 24);
        terminal.EnqueueInput(new byte[] { 0x03 });

        Assert.That(events, Is.Not.Empty);
        foreach (var ev in events)
        {
            Assert.Multiple(() =>
            {
                Assert.That(ev.TargetScript, Is.EqualTo("door.ts"));
                Assert.That(ev.Payload["termId"].AsNumber(), Is.EqualTo(7));
                Assert.That(ev.Ut, Is.Zero);
                Assert.That(ev.VesselId, Is.Null);
            });
        }
    }

    [Test]
    public void AThrowingEventHandler_DoesNotBreakTheTerminal()
    {
        var terminal = NewTerminal(out _);
        terminal.Event += _ => throw new InvalidOperationException("handler exploded");

        Assert.DoesNotThrow(() => terminal.Attach(new RecordingSink(), 80, 24));
        Assert.DoesNotThrow(() => terminal.EnqueueInput(new byte[] { 0x03 }));
    }
}
