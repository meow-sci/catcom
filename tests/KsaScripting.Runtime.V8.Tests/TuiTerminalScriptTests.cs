using System.Text;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.V8.Tests;

/// <summary>
///     A terminal script as the scheduler starts it: <c>main(ctx)</c> with a <c>"terminal"</c> context map carrying a
///     <c>termId</c>, so the real prelude builds <c>ctx.term</c> and the real <c>scripts-lib/tui.ts</c> reads from it.
/// </summary>
[TestFixture]
public sealed class TuiTerminalScriptTests : V8TestBase
{
    private const int TermId = 7;

    [SetUp]
    public void InstallLibrary() => TuiTestSupport.InstallLibrary(ScriptsDir);

    private IEnumerable<string> Logged => Host.Logs.Select(l => l.Message);

    // The map the scheduler passes to a terminal script's main: "$ctx" marker, kind, script identity, signal and termId.
    private static ScriptValue TerminalContext(ScriptSource source)
        => ContextMaps.Create("terminal", source, ("signal", ScriptValue.From(source.Name)), ("termId", ScriptValue.From((double)TermId)));

    private (ScriptSource Source, ScriptCallable Main) LoadMain(string name, string code)
    {
        var path = WriteScript(name + ".ts", code);
        var source = new ScriptSource(name, path, code, ScriptLanguages.TypeScript);
        return (source, Rt.GetExport(Rt.Load(source), "main"));
    }

    private static ScriptValue Bytes(string latin1) => ScriptValue.From((ReadOnlyMemory<byte>)Encoding.Latin1.GetBytes(latin1));

    private void CompleteWait(int index, ScriptValue result) => Host.Waits[index].Wait.Complete(result);

    [Test]
    public void ParseInput_ReadsThroughTermReadWaits_AndEndsWhenTheTerminalCloses()
    {
        var (source, main) = LoadMain("panel", """
            import { ksa } from "ksa";
            import { parseInput } from "./lib/tui.ts";
            export async function main(ctx: any) {
              for await (const ev of parseInput(ctx.term)) ksa.console.log(ev.kind + ":" + ((ev as any).name ?? (ev as any).action ?? ""));
              ksa.console.log("done");
            }
            """);

        var immediate = Rt.Invoke(main, [TerminalContext(source)]);

        Assert.That(immediate.IsNull, Is.True, "main parks in the first term.read()");
        Assert.That(Host.Waits, Has.Count.EqualTo(1));
        Assert.That(Host.Waits[0].Kind, Is.EqualTo("termRead"));
        Assert.That(Host.Waits[0].Arg["termId"].AsNumber(), Is.EqualTo((double)TermId));
        Assert.That(Logged, Is.Empty);

        CompleteWait(0, Bytes("\x1b[Aq"));
        PumpUntil(() => Host.Waits.Count >= 2 && Logged.Count() >= 2, "both keys to be logged and the next termRead to be issued");

        Assert.That(Logged, Is.EqualTo(new[] { "key:up", "key:q" }));
        Assert.That(Host.Waits[1].Kind, Is.EqualTo("termRead"));
        Assert.That(Host.Waits[1].Arg["termId"].AsNumber(), Is.EqualTo((double)TermId));

        CompleteWait(1, ScriptValue.Null);
        PumpUntil(() => Logged.Contains("done"), "the iterator to end");

        Assert.That(Logged, Is.EqualTo(new[] { "key:up", "key:q", "done" }));
        Assert.That(Host.Waits, Has.Count.EqualTo(2), "a null read ends the loop: no further termRead");
        Assert.That(Host.ReportedErrors, Is.Empty);
        Assert.That(Host.Logs.Select(l => l.Script), Is.All.EqualTo("panel"), "continuations run attributed to the owning script");
    }

    [Test]
    public void ParseInput_HandlesMouseAndSplitInput_AcrossReads()
    {
        var (source, main) = LoadMain("clicker", """
            import { ksa } from "ksa";
            import { parseInput } from "./lib/tui.ts";
            export async function main(ctx: any) {
              for await (const ev of parseInput(ctx.term)) {
                if (ev.kind === "mouse") ksa.console.log(ev.button + "/" + ev.action + "@" + ev.col + "," + ev.row);
                else ksa.console.log(ev.kind + ":" + String((ev as any).name ?? (ev as any).text));
              }
            }
            """);
        Rt.Invoke(main, [TerminalContext(source)]);

        // A multi-byte character and a mouse report both split across reads: the parser keeps the partial bytes.
        CompleteWait(0, Bytes("\xc3"));
        PumpUntil(() => Host.Waits.Count >= 2, "the second termRead");
        CompleteWait(1, Bytes("\xa9\x1b[<0;12;3"));
        PumpUntil(() => Host.Waits.Count >= 3 && Logged.Count() >= 1, "the third termRead");
        CompleteWait(2, Bytes("M\x1b[<0;12;3m"));
        PumpUntil(() => Host.Waits.Count >= 4 && Logged.Count() >= 3, "the mouse events");

        Assert.That(Logged, Is.EqualTo(new[] { "key:é", "left/press@12,3", "left/release@12,3" }));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void TermWrite_ReachesTheHost_WithTheTerminalId_AsUtf8TextOrRawBytes()
    {
        var (source, main) = LoadMain("writer", """
            export function main(ctx: any) {
              ctx.term.write("hi");
              ctx.term.write(new Uint8Array([1, 2, 3]));
              ctx.term.write(new Uint8Array([0xff, 0x00, 0x1b, 0x5b, 0x32, 0x4a]));
              ctx.term.write("é─😀");
              ctx.term.clear();
            }
            """);

        var result = Rt.Invoke(main, [TerminalContext(source)]);

        Assert.That(result.IsNull, Is.True);
        Assert.That(Host.TermWrites.Select(w => w.TermId), Is.All.EqualTo(TermId));
        Assert.That(Host.TermWrites, Has.Count.EqualTo(5));
        Assert.That(Host.TermWrites[0].Bytes, Is.EqualTo("hi"u8.ToArray()));
        Assert.That(Host.TermWrites[1].Bytes, Is.EqualTo(new byte[] { 1, 2, 3 }), "a Uint8Array crosses as raw bytes");
        Assert.That(Host.TermWrites[2].Bytes, Is.EqualTo(new byte[] { 0xff, 0x00, 0x1b, (byte)'[', (byte)'2', (byte)'J' }), "bytes are not re-encoded as UTF-8");
        Assert.That(Host.TermWrites[3].Bytes, Is.EqualTo(Encoding.UTF8.GetBytes("é─😀")), "strings are UTF-8 encoded");
        Assert.That(Host.TermWrites[4].Bytes, Is.EqualTo("\x1b[2J\x1b[H"u8.ToArray()));
    }

    [Test]
    public void TheScreenHelper_WritesItsEscapeSequencesThroughTheRealTerminal()
    {
        var (source, main) = LoadMain("screen", """
            import { Screen } from "./lib/tui.ts";
            export function main(ctx: any) {
              const screen = new Screen(ctx.term);
              screen.enterApp();
              screen.frame(() => screen.text(2, 3, "OK é"));
              screen.leaveApp();
            }
            """);

        Rt.Invoke(main, [TerminalContext(source)]);

        var writes = Host.TermWrites.Select(w => Encoding.UTF8.GetString(w.Bytes)).ToArray();
        Assert.That(writes, Has.Length.EqualTo(3));
        Assert.That(writes[0], Does.StartWith("\x1b[?1049h").And.Contain("\x1b[?25l"));
        Assert.That(writes[1], Is.EqualTo("\x1b[?2026h\x1b[3;2HOK é\x1b[?2026l"));
        Assert.That(writes[2], Does.EndWith("\x1b[?1049l"));
    }

    [Test]
    public void TermOn_Resize_SubscribesToTheTargetedTermEvent_AndDeliversThePayload()
    {
        var (source, main) = LoadMain("resizer", """
            import { ksa } from "ksa";
            export function main(ctx: any) {
              ctx.term.on("resize", (payload: any) => {
                ksa.console.log("resize " + payload.cols + "x" + payload.rows);
              });
            }
            """);

        Rt.Invoke(main, [TerminalContext(source)]);

        var subscription = Host.Subscriptions.Single();
        Assert.That((subscription.Kind, subscription.Key), Is.EqualTo(("event", "term.resize")));
        Assert.That(subscription.Options, Is.EqualTo(new BindingOptions()));

        // The scheduler delivers the event as a context map; the handler receives the event payload.
        var payload = ContextMaps.Map(("cols", ScriptValue.From(100d)), ("rows", ScriptValue.From(30d)));
        var eventContext = ContextMaps.Create("event", source, ("event", ContextMaps.Event("term.resize", null, payload)));
        Rt.Invoke(subscription.Callable, [eventContext]);

        Assert.That(Logged, Is.EqualTo(new[] { "resize 100x30" }));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void TermSizeTitleAndPolicy_GoThroughTheHostWithTheTerminalId()
    {
        var (source, main) = LoadMain("termmisc", """
            import { ksa } from "ksa";
            export function main(ctx: any) {
              const size = ctx.term.size;
              ksa.console.log(size.cols + "x" + size.rows + " attached=" + ctx.term.attached);
              ctx.term.title("Panel");
              ctx.term.policy({ stopWhenDetached: true });
            }
            """);

        Rt.Invoke(main, [TerminalContext(source)]);

        Assert.That(Logged, Is.EqualTo(new[] { "80x24 attached=1" }));
        Assert.That(Host.Titles, Is.EqualTo(new[] { (TermId, "Panel") }));
        Assert.That(Host.Policies, Is.EqualTo(new[] { (TermId, true) }));
    }
}
