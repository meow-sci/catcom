---
type: Pattern
title: purrTTY Game Console shell and the ConsoleWindow.Print Harmony bridge
description: How purrTTY turns KSA's in-game console into a terminal shell by executing Program.TerminalInterface.Execute synchronously and capturing ConsoleWindow.Print via a Harmony postfix; a template for routing script output and game console text into a terminal.
resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.CustomShells/GameConsoleShell.cs
tags: [purrtty, game-console, harmony, console-window]
sources:
  - id: report
    resource: /references/purrtty-internals-report-2026-10-16.md
    title: purrTTY internals research report (2026-10-16)
  - id: patch
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.GameMod/Patches/ConsoleWindowPrintPatch.cs
    title: ConsoleWindowPrintPatch.cs
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:45:00Z }
status: stable
---

# Mechanism

`GameConsoleShell : BaseLineBufferedShell` requires `Program.ConsoleWindow != null && Program.TerminalInterface != null` at start, and on each line sets a static "capturing" flag, calls **`Program.TerminalInterface.Execute(commandLine)`** synchronously, then prints its prompt. Output produced during that window arrives through this Harmony postfix:[^patch]

```csharp
[HarmonyPatch(typeof(ConsoleWindow))]
[HarmonyPatch(nameof(ConsoleWindow.Print))]
[HarmonyPatch(new[] { typeof(ReadOnlySpan<char>), typeof(ImColor8), typeof(int) })]
public static class ConsoleWindowPrintPatch
{
    [HarmonyPostfix]
    public static void Postfix(ReadOnlySpan<char> text, ImColor8 color)
    {
        if (!GameConsoleShell.IsCapturing) return;
        try { GameConsoleShell.OnConsolePrint(new string(text), color); } catch (Exception) { }
    }
}
```

`ConsoleWindow.ErrorColor` / `CriticalColor` are classified as stderr. Known limits: each `Print` is one line, async output after `Execute` returns is lost, and unrelated cross-thread prints during `Execute` are misattributed.[^report]

# Relevance

- `Program.TerminalInterface.Execute(string)` is the game's own command dispatcher: a scripting API can expose `game.console("command")` by calling it on the game thread.
- The same postfix shape lets a scripting mod mirror `print()`/`console.log` output into both the KSA console and a purrTTY tab.
- Harmony patches are applied via `harmony.CreateClassProcessor(type).Patch()` with per-class try/catch so an optional patch failing does not take the mod down; unload via `UnpatchAll("<harmonyId>")`.[^report]

[^report]: purrTTY internals research report (2026-10-16)
[^patch]: ConsoleWindowPrintPatch.cs
