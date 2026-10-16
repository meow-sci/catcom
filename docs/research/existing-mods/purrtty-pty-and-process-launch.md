---
type: Component Analysis
title: purrTTY PTY and process launch layer
description: How purrTTY spawns a real process inside a pseudo-terminal on Windows (ConPTY) and Linux/macOS (fork-free posix_spawn on a pty), the ProcessLaunchOptions model, default environment, resize and exit semantics, and the exact path to run an arbitrary program such as nvim as the PTY child.
resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.Terminal/Pty
tags: [purrtty, pty, conpty, posix-spawn, nvim, process-launch]
sources:
  - id: report
    resource: /references/purrtty-internals-report-2026-10-16.md
    title: purrTTY internals research report (2026-10-16)
  - id: launchopts
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.Terminal/Pty/ProcessLaunchOptions.cs
    title: ProcessLaunchOptions.cs
  - id: resolver
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.Terminal/Pty/Process/ShellCommandResolver.cs
    title: ShellCommandResolver.cs
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:45:00Z }
status: stable
---

# Launch options

Namespace `purrTTY.Core.Terminal`, assembly `purrTTY.Terminal` (**not exported** over StarMap today):[^launchopts]

```csharp
public enum ShellType { Auto, PowerShell, Wsl, PowerShellCore, Cmd, Custom, CustomGame }
public class ProcessLaunchOptions
{
    public ShellType ShellType { get; set; } = ShellType.Auto;
    public string? CustomShellPath { get; set; }      // ShellType.Custom: absolute path OR bare exe name (PATH-resolved)
    public string? CustomShellId { get; set; }        // ShellType.CustomGame: CustomShellRegistry id
    public List<string> Arguments { get; set; } = new();
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();   // merged over parent env
    public int InitialWidth { get; set; } = 80;
    public int InitialHeight { get; set; } = 24;
    public string? StartupCommand { get; set; }       // typed into the child's stdin + "\n" after start
    public static ProcessLaunchOptions CreateCustom(string shellPath, params string[] arguments);
    public static ProcessLaunchOptions CreateCustomGame(string customShellId);
    public static ProcessLaunchOptions CreateDefault();
}
```

Default environment injected by `CreateDefault()` (and `CustomShellStartOptions.CreateDefault()`): `TERM=xterm-256color`, `TERM_PROGRAM=ghostty`, `TERM_PROGRAM_VERSION=1.3.2`, `COLORTERM=truecolor`, `FORCE_COLOR=1`, `CLICOLOR_FORCE=1`. `TERM` stays `xterm-256color` deliberately so **no terminfo is ever needed**; nothing in purrTTY handles terminfo.[^report]

# Two ways to run nvim

1. **nvim as the PTY child** — `ProcessLaunchOptions.CreateCustom("nvim", file)` plus `WorkingDirectory` and env. `ShellType.Custom` with a bare name is PATH-resolved from the **game process's PATH** (`ShellCommandResolver.FindExecutableInPath` reads the parent PATH, not the override), so pass an absolute path to the shipped binary.[^resolver] When nvim exits, `TerminalSession.ProcessExited` fires with its exit code and the tab title gains `(exit N)`; the tab stays open (no auto-close, no auto-restart).
2. **Shell + `StartupCommand = "nvim '<file>'"`** — the shell survives nvim; nvim's exit is not observable from outside.

Option 1 is the right primitive for "open the editor, reload on exit"; save-while-editing detection is a separate mechanism (see [save detection and reload](/editor/save-detection-and-reload.md)).

# Platform backends

- **Windows (ConPTY):** `CreatePseudoConsole` with `dwFlags = 0`, `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`, `CreateProcessW` with `EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT`, `STARTF_USESTDHANDLES` with null handles (so a parent with redirected stdio does not leak its pipes), quoted command line per `CommandLineToArgvW` rules, exit via `Process.Exited`, dedicated blocking `ReadFile` pump with no sleeps, teardown order `ClosePseudoConsole` → drain ≤2 s → close pipes.[^report]
- **Linux/macOS (fork-free):** `posix_openpt` → `grantpt` → `unlockpt` → `ptsname_r`, `TIOCSWINSZ` on the slave fd, `posix_spawnp` with `addchdir_np`, `addopen(0, slave)`, `adddup2` for 1 and 2, attrs `POSIX_SPAWN_SETSID | SETSIGDEF | SETSIGMASK`. Forking the multi-GB game process is deliberately avoided. Resize via `ioctl(TIOCSWINSZ)` on the master; exit via a `waitpid` thread; stop via `kill(-pid, SIGHUP)` then `SIGKILL`.[^report]
- Input is queued (1 MiB bounded) to a writer thread; `Write` never blocks the game thread.

# Process manager contract

```csharp
public interface IProcessManager : IDisposable
{
    bool IsRunning { get; }  int? ProcessId { get; }  int? ExitCode { get; }
    Task StartAsync(ProcessLaunchOptions options, CancellationToken ct = default);   // throws ProcessStartException
    Task StopAsync(CancellationToken ct = default);
    void Write(ReadOnlySpan<byte> data);  void Write(string text);  void Resize(int width, int height);
    event EventHandler<DataReceivedEventArgs>? DataReceived;
    event EventHandler<ProcessExitedEventArgs>? ProcessExited;
    event EventHandler<ProcessErrorEventArgs>? ProcessError;
}
```

`ShellType.CustomGame` is routed to `CustomShellPtyBridge` wrapping an `ICustomShell` instead of a real process.[^report]

[^report]: purrTTY internals research report (2026-10-16)
[^launchopts]: ProcessLaunchOptions.cs
[^resolver]: ShellCommandResolver.cs
