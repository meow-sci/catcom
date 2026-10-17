using System.Text;
using Catcom.Core.Runtime;
using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

/// <summary>
///     A terminal script as the scheduler starts it: <c>main(ctx)</c> with a <c>"terminal"</c> context map carrying a
///     <c>termId</c>, so the real prelude builds <c>ctx.term</c> and the real <c>scripts-lib/tui.lua</c> reads from it.
/// </summary>
[TestFixture]
public sealed class TuiTerminalScriptTests
{
    private const int TermId = 7;

    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp()
    {
        _fx = new LuaRuntimeFixture();
        TuiTestSupport.InstallLibrary(_fx);
    }

    [TearDown]
    public void TearDown() => _fx.Dispose();

    private FakeHostFacade Host => _fx.Host;

    private LuaRuntime Rt => _fx.Runtime;

    private IEnumerable<string> Logged => Host.Logs.Select(l => l.Message);

    // The map the scheduler passes to a terminal script's main: "$ctx" marker, kind, script identity, signal and termId.
    private static ScriptValue TerminalContext(ScriptSource source)
        => ContextMaps.Create("terminal", source, ("signal", ScriptValue.From(source.Name)), ("termId", ScriptValue.From((double)TermId)));

    private (ScriptSource Source, ScriptCallable Main) LoadMain(string name, string code)
    {
        var source = _fx.Write(name, code);
        var module = Rt.Load(source);
        return (source, _fx.Export(module, "main"));
    }

    private static ScriptValue Bytes(string latin1) => ScriptValue.From(Encoding.Latin1.GetBytes(latin1));

    [Test]
    public void ParseInput_ReadsThroughTermReadWaits_AndEndsWhenTheTerminalCloses()
    {
        var (source, main) = LoadMain("panel", """
            return { main = function(ctx)
              local tui = require("lib.tui")
              for ev in tui.parse_input(ctx.term) do
                ksa.console.log(ev.kind .. ":" .. (ev.name or ev.action or ""))
              end
              ksa.console.log("done")
            end }
            """);

        var immediate = Rt.Invoke(main, [TerminalContext(source)]);

        Assert.That(immediate.IsNull, Is.True, "main parks in the first term.read()");
        Assert.That(Host.Waits, Has.Count.EqualTo(1));
        Assert.That(Host.Waits[0].Kind, Is.EqualTo("termRead"));
        Assert.That(Host.Waits[0].Arg["termId"].AsNumber(), Is.EqualTo((double)TermId));
        Assert.That(Logged, Is.Empty);

        Host.Waits[0].Wait.Complete(Bytes("\x1b[Aq"));
        Rt.Advance(TimeSpan.FromSeconds(5));

        Assert.That(Logged, Is.EqualTo(new[] { "key:up", "key:q" }));
        Assert.That(Host.Waits, Has.Count.EqualTo(2), "the queue is empty, so the iterator asks for more input");
        Assert.That(Host.Waits[1].Kind, Is.EqualTo("termRead"));
        Assert.That(Host.Waits[1].Arg["termId"].AsNumber(), Is.EqualTo((double)TermId));

        Host.Waits[1].Wait.Complete(ScriptValue.Null);
        Rt.Advance(TimeSpan.FromSeconds(5));

        Assert.That(Logged, Is.EqualTo(new[] { "key:up", "key:q", "done" }));
        Assert.That(Host.Waits, Has.Count.EqualTo(2));
        Assert.That(Host.ReportedErrors, Is.Empty);
        Assert.That(Host.Logs.Select(l => l.Script), Is.All.EqualTo("panel"));
    }

    [Test]
    public void ParseInput_HandlesMouseAndSplitInput_AcrossReads()
    {
        var (source, main) = LoadMain("clicker", """
            return { main = function(ctx)
              local tui = require("lib.tui")
              for ev in tui.parse_input(ctx.term) do
                if ev.kind == "mouse" then
                  ksa.console.log(ev.button .. "/" .. ev.action .. "@" .. ev.col .. "," .. ev.row)
                else
                  ksa.console.log(ev.kind .. ":" .. tostring(ev.name or ev.text))
                end
              end
            end }
            """);
        Rt.Invoke(main, [TerminalContext(source)]);

        // A multi-byte character and a mouse report both split across reads: the parser keeps the partial bytes.
        Host.Waits[0].Wait.Complete(Bytes("\xc3"));
        Rt.Advance(TimeSpan.FromSeconds(5));
        Host.Waits[1].Wait.Complete(Bytes("\xa9\x1b[<0;12;3"));
        Rt.Advance(TimeSpan.FromSeconds(5));
        Host.Waits[2].Wait.Complete(Bytes("M\x1b[<0;12;3m"));
        Rt.Advance(TimeSpan.FromSeconds(5));

        Assert.That(Logged, Is.EqualTo(new[] { "key:é", "left/press@12,3", "left/release@12,3" }));
        Assert.That(Host.Waits, Has.Count.EqualTo(4));
    }

    [Test]
    public void TermWrite_ReachesTheHost_WithTheTerminalId_AndRawBytes()
    {
        var (source, main) = LoadMain("writer", """
            return { main = function(ctx)
              ctx.term.write("hi")
              ctx.term.write("\255\0\27[2J")
              ctx.term.clear()
            end }
            """);

        var result = Rt.Invoke(main, [TerminalContext(source)]);

        Assert.That(result.IsNull, Is.True);
        Assert.That(Host.TermWrites.Select(w => w.TermId), Is.All.EqualTo(TermId));
        Assert.That(Host.TermWrites[0].Bytes, Is.EqualTo("hi"u8.ToArray()));
        Assert.That(Host.TermWrites[1].Bytes, Is.EqualTo(new byte[] { 0xff, 0x00, 0x1b, (byte)'[', (byte)'2', (byte)'J' }), "bytes are not re-encoded as UTF-8");
        Assert.That(Host.TermWrites[2].Bytes, Is.EqualTo("\x1b[2J\x1b[H"u8.ToArray()));
    }

    [Test]
    public void TheScreenHelper_WritesItsEscapeSequencesThroughTheRealTerminal()
    {
        var (source, main) = LoadMain("screen", """
            return { main = function(ctx)
              local tui = require("lib.tui")
              local screen = tui.Screen.new(ctx.term)
              screen:enter_app()
              screen:frame(function() screen:text(2, 3, "OK") end)
              screen:leave_app()
            end }
            """);

        Rt.Invoke(main, [TerminalContext(source)]);

        var writes = Host.TermWrites.Select(w => Encoding.UTF8.GetString(w.Bytes)).ToArray();
        Assert.That(writes, Has.Length.EqualTo(3));
        Assert.That(writes[0], Does.StartWith("\x1b[?1049h").And.Contain("\x1b[?25l"));
        Assert.That(writes[1], Is.EqualTo("\x1b[?2026h\x1b[3;2HOK\x1b[?2026l"));
        Assert.That(writes[2], Does.EndWith("\x1b[?1049l"));
    }

    [Test]
    public void TermOn_Resize_SubscribesToTheTargetedTermEvent_AndDeliversThePayload()
    {
        var (source, main) = LoadMain("resizer", """
            return { main = function(ctx)
              ctx.term.on("resize", function(payload)
                ksa.console.log("resize " .. payload.cols .. "x" .. payload.rows)
              end)
            end }
            """);

        Rt.Invoke(main, [TerminalContext(source)]);

        var subscription = Host.Subscriptions.Single();
        Assert.That((subscription.Kind, subscription.Key), Is.EqualTo(("event", "term.resize")));
        Assert.That(subscription.Options, Is.EqualTo(new Catcom.Core.Hosting.BindingOptions()));

        // The scheduler delivers the event as a context map; the handler receives the event payload (snake_case keys).
        var payload = ContextMaps.Map(("cols", ScriptValue.From(100d)), ("rows", ScriptValue.From(30d)));
        var eventContext = ContextMaps.Create("event", source, ("event", ContextMaps.Event("term.resize", null, payload)));
        Rt.Invoke(subscription.Fn, [eventContext]);

        Assert.That(Logged, Is.EqualTo(new[] { "resize 100x30" }));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }
}
