---
type: Reference
title: KSA in-game console and TerminalInterface command system
description: How KSA's console works (Program.TerminalInterface / ConsoleWindow), how a mod registers and unregisters commands without Harmony, how to execute existing commands programmatically, the boot/load autoexec lists, and the output pipeline a scripting mod can mirror into a purrTTY tab.
resource: /Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/decomp/Brutal.ImGuiApi.Abstractions/TerminalInterface.cs
tags: [ksa, console, terminal-interface, commands, logging]
sources:
  - id: report
    resource: /references/ksa-hooks-report-2026-10-16.md
    title: KSA hook-point research report (2026-10-16, build 5554)
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: stable
---

# Objects

- `Program.ConsoleWindow` (`Brutal.ImGuiApi.Abstractions.ConsoleWindow : ILoggerProvider`) is created in the `Program` constructor; `Program.TerminalInterface => ConsoleWindow.Terminal` is the public command dispatcher.[^report]
- `TerminalInterface` keeps a static command library (owner `null`, **never removable**) and per-owner dictionaries.

# Registering commands from a mod (no Harmony)

```csharp
static class ScriptCommands
{
    [TerminalAction("script", "run or reload a script", ArgParseMode.Unparsed)]
    static void Script(string args) { /* main thread */ }
}
// after the Program ctor, e.g. in [StarMapAllModsLoaded]
Program.TerminalInterface.RegisterCommand(new Action<string>(ScriptCommands.Script), ownerToken);
// on unload
Program.TerminalInterface.UnregisterObject(ownerToken);
```

Rules: the delegate's method **must** carry `[TerminalAction(name, description, ArgParseMode)]` (lambdas throw `TerminalInterfaceException`); names are lower-cased; re-registering under the same owner replaces silently; `RegisterObject(obj)` reflects all attributed methods; `UnregisterObject(owner)` is the only removal API, so never register with a null owner. Arguments are converted with `Convert.ChangeType` (primitives, `string`, `DateTime`; optional parameters allowed); `ArgParseMode.Unparsed` passes the raw remainder as one string. `TerminalActionAttribute` and `ArgParseMode` live in `Brutal.ImGui.Abstractions.dll`.[^report]

# Executing commands

`Program.TerminalInterface.Execute("control MyShip; refill MyShip")` splits on `;` outside quotes and runs each synchronously on the caller's thread; it must be the **main thread** (most KSA commands enqueue `InputEvents` or touch UI). Errors go to `TerminalInterface.OnOutput` (which KSA subscribes to and prints). Known command names include `help, clear, save <name>, load <name>, savefiles, threads, openlogs, goto, atmosphere, clouds, ocean, exhaust, exit, follow <id>, control <id>` plus the `Universe.RegisterCommands` set (`Duplicate, Destroy, Explode, Refill, Empty, WarpToNext, SetTargetCommand, …`, many deferring through `InputEvents`).[^report]

# Autoexec lists

`mod.toml` `[console] onBoot / onLoad` (Tomlet `ConsoleSettings`) and `GameSettings.Current.Console.OnBoot/OnLoad` are executed at boot (inside the `Program` ctor) and in `Universe.OnLoaded()` after a system load (not after a save load). Because StarMap mods register after KSA init, commands a StarMap mod registers are not available to `onBoot`; a scripting mod should run its own startup script list instead.[^report]

# Output pipeline

All `ConsoleWindow.Print*` overloads are thread-safe (lock-guarded double buffer) and funnel into `AddPendingMessage` → `SwapBuffers` at the top of `Draw` → a 64 KB ring buffer of `ConsoleLogEntry`. `ConsoleWindow` is also an `ILoggerProvider`, so `LogCategory` logging reaches the console; **`Console.WriteLine` goes to stdout only**. The console toggle key is `ConsoleWindowEx.ToggleKey` (`GlfwKey.Backslash`, static, settable) and the key chain is `GameSettings.OnKeyAll → Popup.OnKeyAll → ConsoleWindow.OnKey → !ConsoleWindow.IsOpen → !ImGui WantCaptureKeyboard → DispatchKeyEvent`.[^report]

purrTTY's Game Console shell already bridges this surface (synchronous `Execute` + a postfix on the three-argument `Print`); see [purrTTY game console shell](/existing-mods/purrtty-game-console-shell.md).

# Use in catcom

- Register `script run <file>`, `script reload`, `script list`, `script edit <file>` (opens nvim), `lua <expr>` / `ts <expr>` one-liners.
- Route script `print()` / `console.log` to `ConsoleWindow.Print` (thread-safe) and to the REPL shell when one is attached.
- Expose `game.console("...")` to scripts by calling `Program.TerminalInterface.Execute` on the main thread.

[^report]: KSA hook-point research report (2026-10-16, build 5554)
