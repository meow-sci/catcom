using static KsaScripting.Core.Api.Dsl;

namespace KsaScripting.Core.Api;

/// <summary>The context objects handed to hooks, ticks, services and terminal scripts, and the script terminal.</summary>
internal static class ContextClasses
{
    private static readonly ApiType TerminalEvent = new(
        "\"data\" | \"resize\" | \"attach\" | \"detach\" | \"cancel\"",
        "\"data\"|\"resize\"|\"attach\"|\"detach\"|\"cancel\"");

    internal static IReadOnlyList<ApiClass> Create() =>
    [
        new("Terminal",
            "A script-owned virtual terminal (`ctx.term`), rendered by purrTTY in a window or on a part. Write ANSI escape sequences, read keyboard and mouse bytes. Output is buffered and flushed once per step slot.",
            [
                Fn("write", Ty.Void, "Appends output to the terminal (a string<ts>; bytes work too</ts>).",
                    P("data", new ApiType("string | Uint8Array", "string"), "The text or bytes to write.")),
                Wait("read", new ApiType("Uint8Array", "string"),
                    "Waits for the next chunk of input (keys, mouse reports, terminal replies) and returns its bytes<ts> as a Uint8Array</ts><lua> as a string</lua>."),
                Fn("on", Ty.Ref("Subscription"),
                    "Push-style input: calls `fn` with the event payload. `resize` carries `{ cols, rows }`, `data` carries `{ data }`.",
                    P("event", TerminalEvent, "The terminal event."),
                    P("fn", Ty.Func("payload: Record<string, unknown>", "void", "payload: table"), "Called with the event payload.")),
                Prop("size", new ApiType("{ cols: number; rows: number }", "{ cols: number, rows: number }"),
                    "The current grid: the smallest attached surface. Re-read it after a `resize` event."),
                Prop("attached", Ty.Number, "The number of surfaces (windows or part quads) attached to this terminal."),
                Fn("clear", Ty.Void, "Clears the screen and homes the cursor."),
                Fn("flush", Ty.Void, "Does nothing: output is flushed automatically once per step slot. Kept for symmetry with real terminals."),
                Fn("title", Ty.Void, "Sets the terminal window title.", P("s", Ty.String, "The title.")),
                Fn("policy", Ty.Void, "Sets the terminal lifecycle policy.",
                    P("opts", new ApiType("{ stopWhenDetached: boolean }", "{ stop_when_detached: boolean }"),
                        "`stopWhenDetached` stops the script when the last surface closes (default: keep running and buffer output)."))
            ]),
        new("BaseContext", "What every hook and entry point receives as its only argument.",
        [
            Prop("ksa", new ApiType("typeof ksa", "ksa"), "The ksa module (the same object as the global)."),
            Prop("script", Ty.Ref("ScriptInfo"), "The running script."),
            Prop("log", Ty.Ref("Console"), "The console, for logging (same as `ksa.console`)."),
            Prop("kind", Ty.String, "Which kind of invocation this is: `\"event\"`, `\"tick\"`, `\"service\"`, `\"main\"` or `\"terminal\"`.")
        ]),
        new("EventContext", "The context of an event hook.",
        [
            Prop("event", Ty.Ref("EventInfo"), "The event."),
            Prop("vessel", Ty.Optional(Ty.Ref("VesselRef")), "The vessel the event concerns, or undefined (nil in Lua)."),
            Prop("sync", Ty.Boolean, "True when the hook runs inline inside the game call that raised the event (it must not wait)."),
            Prop("ut", Ty.Number, "Universal time of the event (copy of `event.ut`)."),
            Prop("type", Ty.EventName, "The event name (copy of `event.type`)."),
            Prop("vesselId", Ty.Nullable(Ty.String), "The vessel id, or null (copy of `event.vesselId`)."),
            Prop("payload", Ty.UnknownMap, "The event payload (copy of `event.payload`).")
        ], "BaseContext"),
        new("TickContext", "The context of a per-frame, per-step or interval hook.",
        [
            Prop("ut", Ty.Number, "Universal time, in seconds."),
            Prop("dt", Ty.Number, "Duration of the step (or frame), in seconds."),
            Prop("stepIndex", Ty.Number, "The simulation step counter."),
            Prop("frame", Ty.Number, "The rendered frame counter.")
        ], "BaseContext"),
        new("ServiceContext", "The context of a service or main entry point.",
        [
            Prop("signal", Ty.Ref("Signal"), "Becomes cancelled when the script is stopped or reloaded."),
            Prop("term", Ty.Optional(Ty.Ref("Terminal")), "The script's virtual terminal when it owns one; undefined (nil in Lua) otherwise. Terminal scripts use TerminalContext, where it is always present.")
        ], "BaseContext"),
        new("TerminalContext", "The context of a terminal script: a service that also owns a virtual terminal.",
        [
            Prop("term", Ty.Ref("Terminal"), "The script's virtual terminal.")
        ], "ServiceContext"),
        new("MainContext", "The context of a script's `main` entry point (the same as a service context).", [], "ServiceContext")
    ];
}
