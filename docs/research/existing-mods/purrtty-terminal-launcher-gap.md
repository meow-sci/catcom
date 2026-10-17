---
type: Gap Analysis
title: Programmatic terminal launch from another mod is not possible in purrTTY today
description: What terminal-creation API exists inside purrTTY, why none of it is reachable across the StarMap ALC boundary, the user-driven workarounds available now, and the minimal purrTTY extension (ITerminalLauncher in the exported contract assembly) catcom needs to open nvim in a terminal from code.
resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.Display/Ghostty/GhosttyTerminalController.cs
tags: [purrtty, gap, terminal-launcher, nvim, cross-mod]
sources:
  - id: report
    resource: /references/purrtty-internals-report-2026-10-16.md
    title: purrTTY internals research report (2026-10-16)
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:45:00Z }
status: stable
---

# What exists inside purrTTY

`purrTTY.Display.Ghostty.GhosttyTerminalController` has `OpenWindow(ProcessLaunchOptions?, string? title)`, `OpenTab(...)`, `CreateConfiguredWindow(WindowLayoutRecord, ThemeDefinition?)`, `CloseWindow`, `IsVisible`; `TerminalWindow` has `RequestFocus()`, `Close()`, `Sessions` (a `SessionManager` with `CreateSessionAsync`, `RestartSessionAsync`), and `TerminalSession.ProcessExited`. `TerminalTargetRegistry` names every live terminal.[^report]

# Why catcom cannot call it

- `purrTTY.Terminal` and `purrTTY.Display` are not in `ExportedAssemblies`, so none of those types can be imported across the ALC boundary.
- The live controller is private state (`TerminalMod._controller`; `TerminalMenus.MenuController` is `internal static`). There is no public static accessor, event bus, or main-thread dispatcher.
- Window/registry/dialog code is main-thread-only; a caller on another thread would need a queue pumped from `OnAfterUi`.
- Layouts are user-apply only by design (no auto-apply), though a layout entry may carry `ShellType.Custom` + `CustomShellPath = "nvim"` + args, or `startup_command = "nvim <file>"`.[^report]

# Workarounds available today

1. Register an `ICustomShell` and let the user pick it from the New Tab menu (good for a REPL, useless for nvim because a custom shell is not a PTY process).
2. Ship a layout file the user applies once ("Scripting editor") whose entry launches the shipped nvim with a startup command. The user clicks; the mod cannot.

# Recommended purrTTY extension (small, versioned with the contract)

> Superseded in detail by the fuller contract in the [purrTTY virtual-pty report §6](/references/purrtty-virtual-pty-report-2026-10-16.md) and [Plan 00b](../../../plans/00b-purrtty-scripting-extensions-plan.md), which add in-world placement by `Part.InstanceId`, handles with lifecycle events, and the main-thread pump.

Add to `purrTTY.CustomShellContract` (already exported) and have `TerminalMod` implement it with a main-thread queue drained at the top of `OnAfterUi`:[^report]

```csharp
public sealed class TerminalLaunchRequest
{
    public string? TerminalName { get; set; }  public string? Title { get; set; }
    public string? Program { get; set; }       public List<string> Arguments { get; set; } = new();
    public string? WorkingDirectory { get; set; }  public Dictionary<string,string> Environment { get; set; } = new();
    public string? CustomShellId { get; set; } public string? StartupCommand { get; set; }
    public bool NewWindow { get; set; } = true; public bool Focus { get; set; } = true;
}
public interface ITerminalHandle { string TerminalName { get; } Guid SessionId { get; } bool IsRunning { get; } event Action<int>? Exited; void Focus(); void Close(); void WriteInput(ReadOnlyMemory<byte> bytes); }
public interface ITerminalLauncher { Task<ITerminalHandle> LaunchAsync(TerminalLaunchRequest request); }
public static class TerminalLauncherRegistry { public static ITerminalLauncher? Current { get; set; } }
```

Implementation sketch: map the request to `ProcessLaunchOptions.CreateCustom(Program, args)` / `CreateCustomGame(id)` / `CreateDefault()` + `StartupCommand`, call `controller.OpenWindow(opts, title)` or `OpenTab`, set `IsVisible = true`, `RequestFocus()`, and wire `session.ProcessExited` to `Exited` (raised off-thread; consumer marshals). About 150 lines, no new exports. The alternative of exporting `purrTTY.Display`/`purrTTY.Terminal` exposes far more surface and still needs the main-thread marshalling.[^report]

catcom should treat the launcher as **optional**: if `TerminalLauncherRegistry.Current` is null (older purrTTY), fall back to printing the layout instructions and the file path.

[^report]: purrTTY internals research report (2026-10-16)
