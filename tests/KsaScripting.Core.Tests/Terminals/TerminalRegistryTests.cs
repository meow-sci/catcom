using KsaScripting.Core.Hooks;
using KsaScripting.Core.Terminals;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Tests.Terminals;

[TestFixture]
public sealed class TerminalRegistryTests
{
    private sealed class NullSink : ITerminalSink
    {
        public List<string> Texts { get; } = [];

        public void Write(ReadOnlySpan<byte> bytes) => Texts.Add(System.Text.Encoding.UTF8.GetString(bytes));

        public void Terminate(string message)
        {
        }
    }

    [Test]
    public void Create_AssignsIdsFromOne_AndReturnsTheExistingTerminalOfAScript()
    {
        var registry = new TerminalRegistry();

        var a = registry.Create("a.lua");
        var b = registry.Create("b.lua");
        var again = registry.Create("a.lua");

        Assert.Multiple(() =>
        {
            Assert.That(a.Id, Is.EqualTo(1));
            Assert.That(b.Id, Is.EqualTo(2));
            Assert.That(again, Is.SameAs(a));
            Assert.That(registry.All(), Is.EqualTo(new[] { a, b }));
            Assert.That(registry.Get(2), Is.SameAs(b));
            Assert.That(registry.Get(3), Is.Null);
            Assert.That(registry.ForScript("b.lua"), Is.SameAs(b));
            Assert.That(registry.ForScript("c.lua"), Is.Null);
        });
    }

    [Test]
    public void Remove_ForgetsTheTerminal_AndStopsForwardingItsEvents()
    {
        var bus = new EventBus();
        var registry = new TerminalRegistry(events: bus);
        var terminal = registry.Create("a.lua");

        registry.Remove(terminal.Id);
        terminal.EnqueueInput(new byte[] { 1 });

        Assert.Multiple(() =>
        {
            Assert.That(registry.Get(terminal.Id), Is.Null);
            Assert.That(registry.ForScript("a.lua"), Is.Null);
            Assert.That(bus.PendingCount, Is.Zero);
        });

        registry.Remove(terminal.Id);
        Assert.That(registry.Create("a.lua").Id, Is.EqualTo(2), "ids are not reused");
    }

    [Test]
    public void TerminalEvents_AreForwardedToTheEventBus_TargetedAtTheScript()
    {
        var bus = new EventBus();
        var registry = new TerminalRegistry(events: bus);
        var terminal = registry.Create("a.lua");

        terminal.Attach(new NullSink(), 80, 24);
        terminal.EnqueueInput(new byte[] { 0x03 });

        var drained = bus.DrainPending();
        Assert.Multiple(() =>
        {
            Assert.That(drained.Select(d => d.Event.Type), Is.EqualTo(new[] { "term.attach", "term.resize", "term.data", "term.cancel" }));
            Assert.That(drained.All(d => d.Event.TargetScript == "a.lua"), Is.True);
            Assert.That(drained.All(d => d.Event.Payload["termId"].AsNumber() == terminal.Id), Is.True);
        });
    }

    [Test]
    public void LastDetached_IsForwardedFromEveryTerminal()
    {
        var registry = new TerminalRegistry();
        var raised = new List<string>();
        registry.LastDetached += t => raised.Add(t.Script);
        var a = registry.Create("a.lua");
        var b = registry.Create("b.lua");
        a.StopWhenDetached = true;
        var sink = new NullSink();
        a.Attach(sink, 80, 24);
        b.Attach(new NullSink(), 80, 24);

        a.Detach(sink);

        Assert.That(raised, Is.EqualTo(new[] { "a.lua" }));
    }

    [Test]
    public void FlushAll_FlushesEveryTerminal()
    {
        var registry = new TerminalRegistry();
        var a = registry.Create("a.lua");
        var b = registry.Create("b.lua");
        var sinkA = new NullSink();
        var sinkB = new NullSink();
        a.Attach(sinkA, 80, 24);
        b.Attach(sinkB, 80, 24);
        a.Write("A"u8);
        b.Write("B"u8);

        registry.FlushAll();

        Assert.Multiple(() =>
        {
            Assert.That(sinkA.Texts, Is.EqualTo(new[] { "\x1b[?2026hA\x1b[?2026l" }));
            Assert.That(sinkB.Texts, Is.EqualTo(new[] { "\x1b[?2026hB\x1b[?2026l" }));
        });
    }

    [Test]
    public void TheBufferSize_ComesFromTheRegistry()
    {
        var registry = new TerminalRegistry(bufferKiB: 2);
        var terminal = registry.Create("a.lua");

        terminal.Write(new byte[5000]);

        Assert.That(terminal.CopyRingBuffer(), Has.Length.EqualTo(2048));
    }
}
