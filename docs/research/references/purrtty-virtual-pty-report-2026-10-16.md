---
type: Research Report
title: purrTTY virtual-pty and in-world terminal research report (2026-10-16)
description: Verbatim research report on using purrTTY custom shells as a mock pty for in-process scripts, in-world terminal creation and input, 2D window facts, the proposed cross-mod launcher contract, ImGui helpers, and fonts/canvas resolution; the primary source behind the terminal-script concepts. Claims inside are tagged verified / test-pinned / inference by the author.
tags: [reference, report, raw, purrtty]
generated: { by: claude-fable-5.1/ksa-scripting-research-subagent, at: 2026-10-17T00:00:00Z }
status: stable
---

> Mirrored from the session scratchpad into the bundle so the cited source outlives the session.

# purrTTY as a virtual PTY for an in-process script (research, 2026-10-16)

Scope: everything a sibling mod (catcom) needs to let a Lua/TS script *own* a purrTTY terminal
the way a TUI program owns a pty — emitting escape codes, consuming keyboard/mouse bytes — and have
purrTTY render it in a 2D ImGui window or on an in-world quad. All paths are relative to
`/Users/asherwin/repos/meow-sci/purrtty` unless prefixed. This deliberately does **not** repeat
`catcom/docs/research/references/purrtty-internals-report-2026-10-16.md` (contract ABI, registry,
ALC/mod.toml, native loading, PTY backends, VT feature matrix); it goes deeper on the questions below and
flags where the earlier report or purrTTY's own docs are stale.

Tags: **verified** = read in source; **test-pinned** = asserted by a purrTTY unit test; **inference** =
derived, not executed.

---

## 1. Branch state

```
$ git -C purrtty status --short            → (empty: clean tree)
$ git branch --show-current                → feature/scripting
$ git merge-base main feature/scripting    → 790a82b4cd3e5a31886adfdb19843bda72d0419f
$ git log --oneline -1 main                → 790a82b upgrade ksa notes
$ git log --oneline main..feature/scripting→ (empty)
$ git diff main...feature/scripting --stat → (empty)
```

`feature/scripting` **is identical to `main`** (same commit `790a82b`). There is no scripting-related work on
it: `grep -ril 'catcom|TerminalLauncher|ITerminalHandle|ScriptManager'` over `*.cs/*.md/*.toml`
(excluding `vendor/`) returns nothing. Recent history on the branch is KSA-version upgrades
(`2026.9.22.5482`), packaging, and kitty-graphics native work. Everything in §6 is greenfield. (verified)

---

## 2. `CustomShellPtyBridge` — the mock-pty data path

File: `purrTTY.Terminal/Pty/CustomShellPtyBridge.cs` (380 lines), `public class CustomShellPtyBridge : IProcessManager`.
Constructed only by `purrTTY.Terminal/Sessions/TerminalSessionFactory.cs:60-87` when
`launchOptions.ShellType == ShellType.CustomGame` (`CustomShellRegistry.Instance.CreateShell(launchOptions.CustomShellId)`).
Tests: `purrTTY.Terminal.Tests/CustomShellPtyBridgeTests.cs`.

Key members, verbatim:

```csharp
public CustomShellPtyBridge(ICustomShell customShell)   // subscribes OutputReceived + Terminated in the ctor
public bool IsRunning { get; }                            // _isRunning && !_isDisposed
public int? ProcessId { get; }                            // Environment.ProcessId placeholder while running
public int? ExitCode { get; }                             // from a per-run TaskCompletionSource<int>
public event EventHandler<DataReceivedEventArgs>? DataReceived;
public event EventHandler<ProcessExitedEventArgs>? ProcessExited;
public event EventHandler<ProcessErrorEventArgs>? ProcessError;
public async Task StartAsync(ProcessLaunchOptions options, CancellationToken cancellationToken = default)
public async Task StopAsync(CancellationToken cancellationToken = default)
public void Write(ReadOnlySpan<byte> data)
public void Write(string text)
public void Resize(int width, int height)
public void Dispose()
```

`StartAsync` (lines 89-182): builds
`new CustomShellStartOptions { InitialWidth, InitialHeight, WorkingDirectory, EnvironmentVariables = new(options.EnvironmentVariables) }`
(`Configuration` dictionary is **never** populated; `CustomShellId` is not passed to the shell), awaits
`_customShell.StartAsync`, then — only if the shell did not already raise `Terminated` synchronously —
sets `_isRunning = true` and calls `_customShell.SendInitialOutput()` (exceptions → `ProcessError`, not fatal).
Any exception becomes `ProcessStartException($"Failed to start custom shell '{shellId}': ...")`.

### 2(a) Are input bytes raw or line-buffered?

**Raw, never line-buffered, one `WriteInputAsync` call per `Write`.** (verified)

```csharp
// CustomShellPtyBridge.cs:211-250
public void Write(ReadOnlySpan<byte> data)
{
    ThrowIfDisposed();
    lock (_stateLock) { if (!_isRunning) throw new InvalidOperationException("Custom shell is not running"); }
    try
    {
        Task writeTask = _customShell.WriteInputAsync(data.ToArray(), CancellationToken.None);
        if (!writeTask.IsCompletedSuccessfully)
        {
            writeTask.ContinueWith(task => { ... ProcessError?.Invoke(...) }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
    catch (Exception ex) { throw new ProcessWriteException(...); }
}
```

There is no `PtyInputQueue` on this path (that queue exists only in `ProcessManager`/`UnixProcessManager`).
Chunk granularity is whatever the frontend sends per `TerminalSession.SendInput(ReadOnlySpan<byte>)`:

| Source | Bytes per `WriteInputAsync` call | Where |
|---|---|---|
| Named key / Ctrl-Alt chord | one complete encoded sequence (e.g. `\x1b[A`, `\x12`) | `purrTTY.Display/Ghostty/TerminalInputEncoder.cs:195-212` `EncodeAndSend` → `Send` |
| Plain typed text | **one call per Unicode scalar** (1–4 UTF-8 bytes); a surrogate pair is joined into one call | `TerminalInputEncoder.cs:161-192` loops `io.InputQueueCharacters` and calls `Send` per char |
| Mouse report | one complete SGR sequence | `TerminalWindow.Input.cs:271-287` / `InWorldTerminalRenderer.cs:587-604` `EncodeMouseAndSend` |
| Paste (Ctrl+Shift+V / context menu) | the whole clipboard, bracketed if 2004 is on | `TerminalInputEncoder.cs:229-239`, `TerminalWindow.Input.cs:316-332` |
| `StartupCommand` | the command + `"\n"` in one call | `purrTTY.Terminal/Sessions/TerminalSession.cs:105-111` |
| Engine reply (DA/DSR/kitty) | one call per `BuildFrame` holding **all** replies accumulated that tick | `TerminalSession.cs:204-210` |

Consequence for a script: write the input parser as an incremental state machine over a byte stream. An
escape sequence is never split across calls today (each is produced whole), but paste/startup-command
chunks contain many keys in one call and typed text arrives one codepoint per call. Do not assume
`1 call == 1 key`. `ReadOnlyMemory<byte>` is a fresh `byte[]` per call (`data.ToArray()`), so the script may
retain it.

`Write` throws `InvalidOperationException` once `_isRunning` is false (after `Terminated`); but
`TerminalSession.SendInput` guards with `ProcessManager.IsRunning` first (`TerminalSession.cs:167-173`), so
post-exit keystrokes are **silently dropped**, not errors.

### 2(b) Are the VT engine's query replies forwarded to the shell?

**Yes — every engine reply is written back through `WriteInputAsync`, exactly as a real pty child would read it.** (verified, test-pinned)

```csharp
// TerminalSession.cs:41-44 (ctor wiring)
ProcessManager.DataReceived += OnProcessDataReceived;
Surface.PtyReply += OnSurfacePtyReply;
// TerminalSession.cs:203-210
private void OnSurfacePtyReply(byte[] bytes) { if (ProcessManager.IsRunning) ProcessManager.Write(bytes); }
```

`GhosttyTerminalSurface` (`purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs`) collects replies in
`_replies` (`opts.OnWritePty = span => _replies.AddRange(span);`, line 283) and flushes them **once per
`BuildFrame`** on the tick thread (lines 824-829). What the script will see in its input stream:

| Script emits | Script receives (bytes) | Status |
|---|---|---|
| `\x1b[c` (DA1) | `\x1b[?62;22;52c` | test-pinned (`GhosttyKittyGraphicsTests.cs:166`) |
| `\x1b[>c` (DA2) | `\x1b[>1;10;0c` | verified (`DeviceAttributesReply` lines 180-186: `ConformanceLevel=62, Features=[22,52], DeviceType=1, FirmwareVersion=10`) |
| `\x1b[=c` (DA3) | engine reply with `UnitId=0` | verified |
| `\x1b[6n` (CPR) / `\x1b[5n` (DSR) | `\x1b[<row>;<col>R` / `\x1b[0n` | test exists: `PtyReply_EmittedForDeviceStatusReport` (`GhosttyTerminalSurfaceTests.cs:744`) |
| `\x1b[?u` (kitty keyboard query) | `\x1b[?<flags>u` from the engine | inference from engine behaviour (encoder honours `\x1b[>1u`, test-pinned at line 112-131) |
| kitty graphics `\x1b_Ga=q,...\x1b\\` | `\x1b_G...;OK\x1b\\` (engine) | verified by the comment block at lines 166-179 |
| `\x1b[>0q` (XTVERSION) / ENQ | **nothing** — `OnXtversion`/enquiry callbacks are not set in the ctor (lines 276-287 set only `OnWritePty`, `OnBell`, `OnTitleChanged`, `OnDeviceAttributes`) | verified |
| `\x1b[?1004h` focus events | **never sent** (`Focus` module pruned, `vendor/Ghostty.Vt/README.md:70-71`) | verified |
| OSC 52 read (`\x1b]52;c;?\x07`) | not answered (set works, read does not) | per prior report |

Latency: a reply is delivered on the **next tick** after the query was consumed by `VTWrite`: ≤1 frame
while visible, ≤250 ms for a hidden 2D window (§2h), every frame for in-world. Replies can interleave
with keystrokes in the same `WriteInputAsync` stream but never inside one call's bytes.
Recommendation: a script should not query (it knows it is on purrTTY); if a library does, the input parser
must accept CSI with the `?`/`>`/`=` private markers and final `c`/`R`/`n`/`u`, and APC `\x1b_...\x1b\\`.

### 2(c) When `NotifyTerminalResize(width, height)` is called

Only from `CustomShellPtyBridge.Resize` (lines 267-287), which throws `InvalidOperationException` when not
running and otherwise swallows shell exceptions into `ProcessError`. `Resize` is called **synchronously on
the tick thread** when the frontend's derived grid differs from the surface:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs:424-429   (2D)
if (cols != session.Surface.Cols || rows != session.Surface.Rows)
{
    session.Surface.Resize(cols, rows, (int)_cellWidth, (int)_cellHeight);
    session.UpdateTerminalDimensions(cols, rows);
    session.ProcessManager.Resize(cols, rows);
    Sessions.UpdateLastKnownTerminalDimensions(cols, rows);
}
// purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs:159-167   (in-world, identical block inside TickAndCheckDirty)
```

Initial size is **not** delivered via `NotifyTerminalResize`; it arrives in
`CustomShellStartOptions.InitialWidth/InitialHeight`, which `SessionManager.CreateSessionAsync` stamps from
its `_lastCols/_lastRows` (`purrTTY.Terminal/Sessions/SessionManager.cs:22-23, 143-152`; defaults **80×25**,
overwriting whatever the launch options said). The real grid is then imposed on the first rendered frame:

- 2D: `cols = (int)(avail.X / _cellWidth)`, `rows = (int)(avail.Y / _cellHeight)` from the window's pixel
  size (`TerminalWindow.cs:412-413`) — the user can drag-resize at any time → repeated `NotifyTerminalResize`.
- In-world: texture extent = `clamp(ceil(Cols*cellW), 256, 4096) × clamp(ceil(Rows*cellH), 256, 4096)`
  (`InWorldTerminalInstance.cs:100-102`), then `cols = (int)(pixelWidth/_cellWidth)` — so the shell may
  receive **more** columns/rows than `record.Cols/Rows` when the 256-px floor or the ceil rounding bites
  (e.g. 8 cols × 19.2 px → 256 px → 13 cols). Resizes later only via `TrySetGridSize` (deferred 3 frames, §4e).

So a script shell must: treat `InitialWidth/Height` as provisional, expect one resize within a frame or two
of `StartAsync`, and re-layout on every `NotifyTerminalResize` (called on the main thread while the engine
is being driven — keep it cheap; a resize before `StartAsync` cannot happen through the bridge, but gatOS
tolerates it anyway).

### 2(d) `SendInitialOutput` semantics

Called exactly once per successful `StartAsync`, **after** `_isRunning = true` and only when the shell is
still alive (`startedAlive = !_exitCodeSource.Task.IsCompleted`, lines 130-165). Runs on the **pool thread**
that executes `CreateSessionAsync` (`GhosttyTerminalController.StartSession` → `Task.Run`, line 219;
`InWorldTerminalRenderer.StartDefaultSession` → `Task.Run`, line 311). `DataReceived` is already wired
(the `TerminalSession` ctor subscribed before `InitializeAsync`), so bytes emitted here reach the surface.
Order of first bytes the terminal sees: anything queued in `OnStartingAsync` → `SendInitialOutput` output →
`StartupCommand` (written by `TerminalSession.InitializeAsync` after `StartAsync` returns, so it arrives
through `WriteInputAsync` after the banner). A faulting `SendInitialOutput` is reported via `ProcessError`
and the shell stays running. Test-pinned: `StartAsync_TriggersShellInitialOutput`,
`TerminatedDuringStart_LeavesBridgeStopped`.

### 2(e) What happens on `Terminated`

```csharp
// CustomShellPtyBridge.cs:313-334
private void OnCustomShellTerminated(object? sender, ShellTerminatedEventArgs e)
{
    lock (_stateLock) { _isRunning = false; exitSource = _exitCodeSource; }
    exitSource.TrySetResult(e.ExitCode);
    ProcessExited?.Invoke(this, new ProcessExitedEventArgs(e.ExitCode, _processId ?? 0));
}
```

→ `TerminalSession.OnProcessExited` → `Settings.UpdateProcessState(...)` + `TerminalSession.ProcessExited`
event (`SessionProcessExitedEventArgs(ExitCode, ProcessId)`). Raised on **the shell's thread** (whatever
thread called `RaiseTerminated`).

UI consequences (verified):
- 2D window, **≥2 tabs**: the tab label becomes `"{title} (exit {code})"` (`TerminalWindow.cs:672-687`).
- 2D window, **single session**: the tab bar is not drawn at all (`RenderTabBar` returns when
  `sessions.Count <= 1`, line 616) and the title strip prints only `Title` — **there is no visible exit
  indication**. The last frame stays on screen; typing is dropped silently (§2a).
- In-world: nothing changes; the quad keeps sampling the last texture; clicks still focus it; input dropped.
- No auto-close, no auto-restart, no "press any key" overlay. The only restart path is
  `SessionManager.RestartSessionAsync` (`SessionManager.cs:294-337`), which calls `ProcessManager.StopAsync`
  then `StartAsync` on the *same* bridge/shell instance (the bridge's per-run `TaskCompletionSource` exists
  for this). No UI calls it.
- Reattach: impossible. Closing the tab/window/quad disposes the session → `ProcessManager.Dispose()` on a
  pool thread → `CustomShellPtyBridge.Dispose()` → `_customShell.Dispose()`. **`ICustomShell.StopAsync` is
  never called on close** — `grep StopAsync` in `Sessions/` hits only `RestartSessionAsync` (line 319);
  `TerminalSession.Dispose` (lines 228-268) calls `ProcessManager.Dispose()` only. For a
  `BaseChannelOutputShell`, `Dispose` cancels the pump and clears `IsRunning` but **does not run
  `OnStoppingAsync`** (`BaseChannelOutputShell.cs:204-232`). A script shell must therefore do its teardown
  (stop the interpreter, free the screen model) in `Dispose`, not only in `OnStoppingAsync`.

A shell that wants the exit to be *visible* should itself print a final line (e.g. `"[script exited]"`)
before raising `Terminated`.

### 2(f) `RequestCancellation` (Ctrl+C) for a raw-mode shell

**purrTTY never calls `ICustomShell.RequestCancellation()`.** `grep -rn RequestCancellation` over
`purrTTY.Terminal`, `purrTTY.Display`, `purrTTY.GameMod`, `purrTTY.CustomShells`, `purrTTY.CustomShellContract`
finds only the interface member, the `BaseCustomShell` no-op, the `GameConsoleShell` override and a doc
comment in `BaseLineBufferedShell.cs:596-600`. There is no SIGINT analogue. Ctrl+C reaches the shell **as a
byte**: `0x03` under the legacy encoder (`TerminalInputEncoder` encodes `Ctrl+C` via the engine; the only
reserved chords are Ctrl+Shift+C/V, lines 96-106), or `\x1b[99;5u` once the script has enabled the kitty
protocol with `\x1b[>1u` (pattern test-pinned for Ctrl+R → `\x1b[114;5u`, `GhosttyTerminalSurfaceTests.cs:112-131`).
`BaseLineBufferedShell` handles `0x03` itself (`protected const byte CtrlC = 0x03;` line 62 → `HandleCancelLine`
line 259-260). A raw-mode script shell must interpret `0x03` itself (and should probably treat it as
"interrupt the running script", not "exit").

### 2(g) Threading of each callback

| `ICustomShell` member | Thread it is invoked on | Caller |
|---|---|---|
| ctor + probe `Dispose` at `RegisterShell` | the registering mod's thread (`[StarMapAllModsLoaded]`) | `CustomShellRegistry.RegisterShell` |
| factory (`CreateShell`) | pool thread inside `CreateSessionAsync` | `TerminalSessionFactory.CreateProcessManager` |
| `StartAsync`, `SendInitialOutput` | pool thread (`Task.Run`) | `GhosttyTerminalController.StartSession:219`, `InWorldTerminalRenderer.StartDefaultSession:311` |
| `WriteInputAsync` (keys, mouse, paste) | **main/tick thread, synchronously inside ImGui rendering** | `TerminalWindow.Render` → `HandleInput`; `InWorldTerminalManager.OnAfterGui:288-309` |
| `WriteInputAsync` (engine replies) | main/tick thread, inside `BuildFrame` | `TerminalSession.OnSurfacePtyReply` |
| `WriteInputAsync` (`StartupCommand`) | pool thread, right after `StartAsync` | `TerminalSession.InitializeAsync:105-111` |
| `NotifyTerminalResize` | main/tick thread | `TerminalWindow.cs:427`, `InWorldTerminalRenderer.cs:165` |
| `StopAsync` | pool thread — **only** via `RestartSessionAsync` | `SessionManager.cs:319` |
| `Dispose` | pool thread (`Task.Run` in `TerminalSession.Dispose`) | `TerminalSession.cs:257-267` |
| `OutputReceived` consumer (`Surface.Write`) | whatever thread the shell raises on; thread-safe | `TerminalSession.OnProcessDataReceived` |
| `Terminated` consumer | whatever thread the shell raises on | bridge → session |

Because `WriteInputAsync` runs on the game thread, a script runtime that executes handlers synchronously in
`WriteInputAsync` is running Lua/JS **on the game's main thread during `OnAfterGui`**: it may touch game
state without marshalling (what `GameConsoleShell` relies on) but must never block. If the script runtime
lives on its own thread, `WriteInputAsync` should enqueue and return `Task.CompletedTask` (the bridge
comment at lines 225-230: fully-async shells "get no cross-call ordering guarantee here" — so enqueue
synchronously, process asynchronously).

### 2(h) Buffering / backpressure on the output path

- `BaseChannelOutputShell` (`purrTTY.CustomShellContract/Base/BaseChannelOutputShell.cs`): **unbounded**
  `Channel<(byte[] Data, ShellOutputType Type)>` (`SingleReader = true, SingleWriter = false`, lines 56-60),
  one `Task.Run` pump. `QueueOutput` never blocks and never fails (returns void; `TryWrite` on an unbounded
  channel always succeeds). No bytes/sec limit anywhere on the shell side.
- Surface inbox (`GhosttyTerminalSurface.cs:100-160, 354-423`): `internal const int MaxInboxBytes = 24 * 1024 * 1024;`
  (**24 MiB** — `docs/gotchas.md` §18 still says 8 MiB; the code is authoritative),
  `internal const int MaxBytesPerTick = 8 * 1024 * 1024;` drained per `BuildFrame`,
  `WriteBackpressureBudget = TimeSpan.FromMilliseconds(500)`. A writer that would overflow and is **not** the
  tick thread `Monitor.Wait`s up to 500 ms for a drain, then drops the chunk and injects a CAN+ST heal
  (`_inboxDropCount`, shown as `drop:N` in the perf HUD, logged once per episode). The
  `BaseChannelOutputShell` pump thread is such a writer, so a script flooding output will block its *pump*
  (not the script) for ≤500 ms per full inbox; the channel keeps absorbing meanwhile (unbounded → memory).
- Drain cadence: visible 2D window — every frame, all tabs (`TerminalWindow.cs:496-508`); hidden 2D window —
  `GhosttyTerminalController.Update` drains every 0.25 s (`_hiddenDrainTimer >= 0.25f`, lines 359-378);
  in-world — `TickAndCheckDirty` runs **every game frame** regardless of visibility/anchoring
  (`InWorldTerminalInstance.Frame:144-166`), and the off-screen pass is re-submitted only when the frame
  generation changed, a kitty image arrived, focus changed, or 1 s elapsed (`InWorldTerminalRenderer.cs:180-185`).
- Practical budget: a 40×12 panel repainting fully is ~2–5 KB per frame; even at 60 Hz that is <300 KB/s,
  three orders of magnitude under the cap. The 1 Hz floor repaint in-world is free for the script (it is a GPU
  re-render of the same frame, not a VT re-feed).

### 2(i) Is `BaseChannelOutputShell` suitable for raw-mode TUI output?

Yes, with two caveats. Ordering: FIFO through a single-reader channel → one `OutputReceived` per
`QueueOutput` → appended to the inbox in order; the bridge adds no reordering. Flush latency: a pool-thread
hop (~µs) plus the next `BuildFrame` (≤1 frame visible, ≤250 ms hidden 2D, every frame in-world); there is
no coalescing timer. Because the inbox is consumed whole per tick, a script that writes a full frame as
several `QueueOutput` calls in quick succession will normally see them land in the same tick; to make that
deterministic wrap each repaint in DEC 2026 (`\x1b[?2026h` … `\x1b[?2026l`): the surface withholds frames
while the mode is set, bounded by `SyncOutputTimeout = 1 s` (`GhosttyTerminalSurface.cs:843-877`).

Caveat 1 — **output with no subscriber is lost, not buffered**: `RaiseOutputReceived` is
`OutputReceived?.Invoke(...)` (`BaseCustomShell.cs:46-49`). Before `StartAsync`, `_outputChannel` is null so
`QueueOutput` is a silent no-op (`_outputChannel?.Writer.TryWrite`, line 188). After the bridge is disposed
it has unsubscribed (lines 364-365). In practice the bridge subscribes in its ctor, before `StartAsync`, so
everything from `OnStartingAsync` onward is delivered; but a script runtime that outlives its terminal (e.g.
one script instance shared across terminals) must keep its own screen model and repaint on (re)attach.

Caveat 2 — `Dispose` does not run `OnStoppingAsync` (§2e).

---

## 3. Terminal modes a script will set, and the exact input bytes it receives

All mode handling is the libghostty-vt engine plus the frontends; **custom shells and real pty sessions
share 100 % of this code** — `TerminalSession` is the same class, `ITerminalSurface` the same
`GhosttyTerminalSurface`, and the frontends consult only the surface (`IsMouseTrackingEnabled`,
`IsBracketedPasteEnabled`, `EncodeKey`, `EncodeMouse`, `EncodePaste`). Nothing checks `ShellType`.

| Script writes | Honoured? | Evidence |
|---|---|---|
| `\x1b[?1049h` alt screen | yes (engine); `TerminalMode.AltScreen = 1049` | `vendor/Ghostty.Vt/src/Enums/TerminalMode.cs` |
| `\x1b[?1000h` / `?1002h` / `?1003h` + `\x1b[?1006h` | yes — `IsMouseTrackingEnabled => _terminal.MouseTracking` gates the app-mouse path; the engine's encoder is mode-aware (1000 drops motion, 1002 reports drag only, 1003 reports hover) | `GhosttyTerminalSurface.cs:343, 667-707`; tests `EncodeMouse_*` lines 640-717 |
| `\x1b[?2004h` bracketed paste | yes — `EncodePaste` wraps in `\x1b[200~ … \x1b[201~` | test-pinned lines 727-742 |
| `\x1b[>1u` kitty keyboard (push flags) | yes — `VtKeyEncoder.ConfigureFromTerminal` reads the terminal's flags each key; `UnshiftedCodepoint` is set so chords encode | test-pinned lines 112-131 |
| `\x1b[?25l` hide cursor (DECTCEM) | yes — `frame.Cursor.Visible = _renderState.CursorVisible && inViewport` (line 1082) and `FrameGridRenderer` draws the cursor only `if (frame.Cursor.Visible && cursorOn …)` (line 415) | verified |
| `\x1b[?2026h` sync output | yes, 1 s safety timeout | `GhosttyTerminalSurface.cs:843-877` |
| `\x1b[?1004h` focus events | mode is set in the engine but **no `CSI I`/`CSI O` is ever sent** | README "Pruned surface" |
| `\x1b[?1005h` (UTF-8) / `?1015h` (urxvt) mouse | engine supports; not enumerated in `TerminalMode` (only matters for `ModeGet`) — use 1006 | inference |

Where the frontend code lives: 2D — `purrTTY.Display/Ghostty/TerminalWindow.Input.cs` (`HandleMouse` line 46:
`if (session.Surface.IsMouseTrackingEnabled && !io.KeyShift) HandleAppMouse(...)`), keyboard via the shared
`purrTTY.Display/Ghostty/TerminalInputEncoder.cs:87-193` `ProcessKeyboard(TerminalSession session, ImGuiIOPtr io, Func<bool>? suppress = null, Action? onInputSent = null)`.
In-world — the same `TerminalInputEncoder.ProcessKeyboard(session, io)` (`InWorldTerminalManager.cs:291`) and
`InWorldTerminalRenderer.ProcessMouse(float2? hitUv, ImGuiIOPtr io)` (lines 478-577).

### Exact encodings the script receives (legacy/xterm mode, no kitty flags)

Mouse (requires the script to enable a tracking mode; otherwise **no mouse bytes at all** — clicks only
select text / focus the quad). With `?1006h` (SGR) the engine emits `\x1b[<Cb;Cx;Cy M` on press and
`\x1b[<Cb;Cx;Cy m` on release, 1-based cells:

| Event | Bytes | Status |
|---|---|---|
| Left press at col 1,row 1 | `\x1b[<0;1;1M` | test-pinned (`GhosttyTerminalSurfaceTests.cs:649-650`) |
| Middle press | `\x1b[<1;1;1M` | test-pinned |
| Right press | `\x1b[<2;1;1M` | test-pinned |
| Left release | `\x1b[<0;1;1m` | test-pinned (lowercase `m`) |
| Left drag motion (1002/1003) | `\x1b[<32;4;4M` (button + 32) | test-pinned (line 687) |
| Hover motion, no button (1003 only) | `\x1b[<35;x;yM` | inference (xterm standard; 1002 emits nothing — test-pinned line 700) |
| Wheel up / down | `\x1b[<64;x;yM` / `\x1b[<65;x;yM`, **one report per notch** (`notches = clamp(round(abs(io.MouseWheel)),1,10)`) | verified frontends (`TerminalWindow.Input.cs:217-227`, `InWorldTerminalRenderer.cs:567-576`) + xterm standard for 64/65 |
| Modifiers | +4 Shift, +8 Alt, +16 Ctrl added to `Cb` (engine, xterm standard). **Shift-click never reaches the app in 2D** (Shift bypasses tracking for selection, `TerminalWindow.Input.cs:48-56`); in-world has no Shift bypass | verified / inference |
| Without `?1006h` | legacy X10 `\x1b[M<b+32><x+32><y+32>` with the 223-column limit — always enable 1006 | inference |

Motion is reported only when the pointer crosses into a different cell (`cellChanged`), and the position
passed to the engine is the **cell centre in integer cell metrics**, so the reported cell always equals the
glyph under the cursor (`TerminalWindow.Input.cs:144-162`, `InWorldTerminalRenderer.cs:507-522`).
Supported buttons: Left/Middle/Right + wheel (`MouseButton.{Left,Middle,Right,ScrollUp,ScrollDown}`,
`purrTTY.Terminal/Input/TerminalInput.cs:75-83`); no X1/X2. Press is hover-gated; release is sent only if
the matching press was forwarded.

Keyboard (engine `VtKeyEncoder`, configured from the terminal's modes each key):

| Key | Bytes (normal) | Bytes with `\x1b[?1h` (DECCKM) | Notes |
|---|---|---|---|
| Enter / KeypadEnter | `\r` | — | test-pinned |
| Tab | `\t` | — | test-pinned |
| Backspace | `\x7f` | — | ghostty default (DEL) — inference |
| Escape | `\x1b` alone, immediately | — | no ambiguity timer (prior report) |
| Arrows Up/Down/Right/Left | `\x1b[A` `\x1b[B` `\x1b[C` `\x1b[D` | `\x1bOA` … `\x1bOD` | Up test-pinned |
| Home / End | `\x1b[H` / `\x1b[F` | `\x1bOH` / `\x1bOF` | xterm standard |
| Insert / Delete / PgUp / PgDn | `\x1b[2~` `\x1b[3~` `\x1b[5~` `\x1b[6~` | — | xterm standard |
| F1–F4 | `\x1bOP` `\x1bOQ` `\x1bOR` `\x1bOS` | — | xterm standard |
| F5–F12 | `\x1b[15~` `\x1b[17~` `\x1b[18~` `\x1b[19~` `\x1b[20~` `\x1b[21~` `\x1b[23~` `\x1b[24~` | — | **F13+ not mapped**; the purrTTY toggle hotkey (default F12) is consumed before encoding unless an in-world terminal is focused (`TerminalMod.OnAfterUi:90`) |
| Shift/Ctrl/Alt + named key | xterm `CSI 1;<m> X` / `CSI n;<m> ~` with m = 1 + Shift(1)+Alt(2)+Ctrl(4) | — | engine (xterm "modifyOtherKeys=0" semantics) |
| Ctrl+letter | control byte (Ctrl+R → `0x12` test-pinned, Ctrl+C → `0x03`) | — | |
| Alt+letter | `\x1b` + letter | — | |
| Ctrl+Space / Ctrl+[ / Ctrl+\ / Ctrl+] | `0x00` / `0x1b` / `0x1c` / `0x1d` | — | only these punctuation chords are mapped (`TerminalInputEncoder.cs:66-72`) |
| Ctrl/Alt + digit | engine encoding (e.g. Alt+1 → `\x1b1`) | — | |
| Plain text | UTF-8 of the typed codepoint, one call per codepoint; control chars <32 and 127 dropped | — | `TerminalInputEncoder.cs:161-192` |
| Ctrl+Shift+C / Ctrl+Shift+V | **never delivered** (copy / paste) | — | |
| Super/Cmd | not read (`ReadModifiers` → Shift/Ctrl/Alt only) | — | |

Key release/repeat events are not sent (only `KeyAction.Press` is ever constructed), so even with kitty
flag 2 the script will not get release events.

### Kitty keyboard differences (`\x1b[>1u` … `\x1b[<u`)

With flag 1 (disambiguate) the encoder switches to `CSI <unicode-codepoint>;<mods>u` for everything that
is not plain text: Ctrl+R → `\x1b[114;5u` (test-pinned; mods = 1 + Ctrl 4), Ctrl+C → `\x1b[99;5u`,
Escape → `\x1b[27u`, Enter → `\x1b[13u`, Tab → `\x1b[9u`, Backspace → `\x1b[127u`, Alt+x → `\x1b[120;3u`;
arrows/F-keys keep their legacy forms unless flag 8 is also pushed. Plain text still arrives as UTF-8.
`UnshiftedCodepoint` is populated for letters, digits, space and `; ' , ` . / - = [ ] \` only
(`GhosttyTerminalSurface.cs:648-665`) — other punctuation chords still encode to nothing under kitty.
For a script that wants simple, unambiguous parsing of Esc vs Alt-chords, pushing `\x1b[>1u` on start and
popping `\x1b[<u` on exit is worthwhile; otherwise stay legacy.

---

## 4. In-world terminals (`purrTTY.GameMod/InWorld/*`)

### 4(a) The record

`purrTTY.GameMod/InWorld/Settings/InWorldTerminalRecord.cs` — `public sealed class InWorldTerminalRecord`
(session-only, never persisted by itself; layouts persist a mapping of it, §4f). Fields verbatim:

```csharp
public const string ModePart = "part";  public const string ModeBillboard = "billboard";
public string Name { get; set; } = "In-World";          // unique key in TerminalTargetRegistry
public int Cols { get; set; } = 100;  public int Rows { get; set; } = 30;   // fixed grid → texture extent
public ProcessLaunchOptions? Launch { get; set; }        // null = configured default shell
public string? ThemeName { get; set; }                   // null = global selected theme
public string Mode { get; set; } = ModePart;
// part mode
public string TargetVehicleId { get; set; } = "";        // "" = Program.ControlledVehicle
public string TargetPartId { get; set; } = "";           // "" = vehicle's first top-level part
public string TargetSubPartId { get; set; } = "";
public float PartOffsetX, PartOffsetY; public float PartOffsetZ = 2f;        // metres, part-local frame
public float PartRotationX, PartRotationY, PartRotationZ;                    // degrees
public float PartWidthMeters = 2f, PartHeightMeters = 2f;
// billboard mode
public float BillboardDistance = 5f; public float BillboardOffsetX, BillboardOffsetY;
public float BillboardWidthMeters = 3f, BillboardHeightMeters = 2f;
public float BillboardRotationX, BillboardRotationY, BillboardRotationZ;
public bool BillboardAlwaysOnTop = true;  public bool BillboardClickToFocus;  // false = focus-from-menu only
public bool IsBillboard => string.Equals(Mode, ModeBillboard, StringComparison.OrdinalIgnoreCase);
public InWorldTerminalRecord Clone();
```

**Opacity is not on the record** — it lives in the theme (`TerminalWindowSettings.BackgroundOpacity /
ForegroundOpacity / CellBackgroundOpacity`, defaults 1f) and can be changed live via
`InWorldTerminalInstance.SetOpacities(float background, float foreground, float cellBackground)`
(session-only, `InWorldTerminalInstance.cs:207-208`). The theme name selects colours + font family/size.

**Anchor identity.** `TargetVehicleId` compares against `Vehicle.Id` (`KSA/Astronomical.cs:102`
`public virtual string Id { get; protected set; }`). `TargetPartId` compares against **`Part.Id`**
(`KSA/Part.cs:702` `public string Id { get; init; }`), which is `ResolveRuntimeId(inName, Template)` =
the instance name if the craft file gave one, else **the template id** (`Part.cs:1485-1494`) — so parts
sharing a template with no instance name collide and purrTTY picks the first match (`ResolveTopLevelPart`,
`InWorldQuad.cs:416-436`). `Part.InstanceId` (`public uint InstanceId;` line 574, from
`Universe.GetNextRunningId()`) is **not** used by purrTTY and is session-only anyway. Once resolved, a
specifically-targeted `Part` object is followed by reference across decouple/dock
(`InWorldQuad.TryComputePartModel:287-335`, `VehicleLookup.FindContaining`). If a scripting API wants to
address a part by `InstanceId`, purrTTY would need a new field (trivial: resolve `vehicle.Parts.Parts`
where `p.InstanceId == x`, then set `_anchoredPart` directly).

### 4(b) Programmatic create/destroy/configure

`purrTTY.GameMod/InWorld/InWorldTerminalManager.cs` — `public sealed class InWorldTerminalManager : IDisposable`:

```csharp
public static bool Active;                                   // read by the render postfix
public static InWorldTerminalManager? Instance { get; private set; }
public static bool IsInputFocused => Instance?._focused != null;
public IReadOnlyList<InWorldTerminalInstance> Instances => _instances;
public ThemeConfiguration? Config { get; }  public ThemeCatalog? Catalog { get; }
public void Initialize(ThemeConfiguration config, ThemeCatalog catalog);   // OnFullyLoaded; no GPU work
public void OpenManager();
public InWorldTerminalInstance? Create(InWorldTerminalRecord record);      // MAIN THREAD, inside an ImGui frame
public void Remove(InWorldTerminalInstance instance);                     // main thread; GPU free deferred 2 frames
public void Focus(InWorldTerminalInstance? instance);                     // main thread
public InWorldTerminalInstance? Recreate(InWorldTerminalInstance old, InWorldTerminalRecord newRecord);
public void RecordDrawAll(CommandBuffer commandBuffer);                   // render postfix
public void OnAfterGui(double dt);                                        // per frame, main thread
public void Dispose();
```

`Create` **must** run on the main thread *with an ImGui frame active* (it measures the font cell with
`ImGui.CalcTextSize` via `InWorldTerminalRenderer.MeasureCell`, allocates Vulkan resources, and registers in
the main-thread-only `TerminalTargetRegistry`). It assigns `record.Name = TerminalTargetRegistry.SuggestUniqueName(...)`,
so the caller's name may be suffixed. Returns null on failure (logged). `LayoutManager.Apply` is the
existing example of programmatic creation (`purrTTY.GameMod/Layouts/LayoutManager.cs:57-137`), pumped from
`TerminalMod.OnAfterUi` for exactly this reason (gotcha 29).

`purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs` — `public sealed class InWorldTerminalInstance : INamedTerminal, IDisposable`:

```csharp
public InWorldTerminalRecord Record { get; }
public InWorldTerminalRenderer Content { get; }          // .ActiveSession → TerminalSession
public bool IsBillboard { get; }  public bool IsFailed { get; }
public string Name { get; }  public TerminalKind Kind => TerminalKind.InWorld;
public bool HasFocus { get; internal set; }
public void Frame(double dt);  public void RecordDraw(CommandBuffer commandBuffer);
public bool TryRaycast(Ray ray, out double t, out float2 uv);
public void ApplyTheme(ThemeDefinition theme);
public float BackgroundOpacity { get; }  public float ForegroundOpacity { get; }  public float CellBackgroundOpacity { get; }
public void SetOpacities(float background, float foreground, float cellBackground);
public bool TrySetGridSize(int cols, int rows);          // clamps 8..400 × 4..200; live, deferred 3 frames
public bool TryRename(string newName);
public void UnregisterNow();  public void Dispose();  public void Dispose(bool freeGpu);
```

Placement fields are read **live every frame** from `Record` (`InWorldQuad` reads `_settings` in
`TryComputePartModel`/`TryComputeBillboardModel`), so a scripting API can mutate `Record.PartOffsetX` etc.
on the main thread and see the quad move immediately; changing the anchor ids is also live (the cached
`_anchoredPart` is invalidated when any target id changes, `InWorldQuad.cs:291-299`). Changing font/theme
is live via `ApplyTheme`; changing the *shell* requires `Recreate` (restarts the session).

### 4(c) Can an in-world terminal run a registered custom shell?

**Yes, today, with zero changes.** `InWorldTerminalRecord.Launch` is a `ProcessLaunchOptions`; set it to
`ProcessLaunchOptions.CreateCustomGame("<id>")`. Path: `InWorldTerminalInstance` ctor →
`new InWorldTerminalRenderer(config, catalog, record.Launch, record.ThemeName)` (line 124) →
`StartDefaultSession` → `_sessions.CreateSessionAsync(null, _launch)` (line 315) → `TerminalSessionFactory`
→ `CustomShellPtyBridge`. The manager UI already offers every registered shell:
`InWorldManagerUI.cs:866-873` enumerates `CustomShellRegistry.Instance.GetAvailableShells()` and adds
`(metadata.Name, ProcessLaunchOptions.CreateCustomGame(id))`; `CreateFromDraft` (lines 560-600) also allows
a `StartupCommand` (sent to the shell's `WriteInputAsync` with `\n`). Layouts persist it as
`[terminal.shell] shell_type = "CustomGame"`, `custom_shell_id = "<id>"` (`LayoutTomlFormat.cs:113-124`,
`ShellSpec.ToLaunchOptions`).

### 4(d) Mouse and keyboard on an in-world quad

Focus acquisition (`InWorldTerminalManager.TickPicker`, lines 345-378, every frame on the main thread):
on `ImGui.IsMouseClicked(Left)`, `Ray inputRay = Cursor.GetEgoRay(Program.MainViewport)` is tested against
every pickable instance with `instance.TryRaycast(inputRay, out double t, out _)` (Möller–Trumbore on the two
triangles of the unit quad transformed by the ego-space model, `InWorldQuad.cs:181-233`); the nearest hit
becomes `_focused`. A click that hits nothing **and** is not over an ImGui widget (`!io.WantCaptureMouse`)
clears focus. Pickable = part mode always, billboard only when `BillboardClickToFocus`. Escape is deliberately
not a release key. `Focus(instance)` focuses from code/menu. If any 2D terminal window is focused this
frame, in-world focus is forcibly cleared (line 277-280) — the two are mutually exclusive.

Keyboard: while `_focused != null && !io.WantTextInput`, `TerminalInputEncoder.ProcessKeyboard(session, io)`
runs against the focused instance's session (line 288-292) — identical encoding to 2D (§3), no hotkey
suppression and no blink reset. Game keys are suppressed meanwhile by the Harmony patches in
`purrTTY.GameMod/Patcher.cs`: `Patch01.TerminalOwnsKeyboard => GhosttyTerminalController.IsAnyTerminalActive || InWorldTerminalManager.IsInputFocused`
gates `KSA.Program.OnKey` (press swallowed; release forwarded only for keys the game saw pressed),
`Patch03_HotkeyGuard` prefixes `GameSettings.OnKeyAll` and returns `__result = true` whenever
`ImGui.GetIO().WantTextInput || InWorldTerminalManager.IsInputFocused` (console window exempt), and
`Patch04_MouseBindingGate` applies the same rule to mouse-bound game actions. The purrTTY toggle hotkey is
skipped while an in-world terminal is focused (`TerminalMod.cs:90`), so F12 reaches the script there.

Mouse to cells: each frame, for the focused pickable instance,
`focused.TryRaycast(Cursor.GetEgoRay(Program.MainViewport), out _, out float2 uv)` yields a texture UV
(u 0→1 left→right, v 0→1 top→bottom, `InWorldQuad.cs:217-232`); `InWorldTerminalRenderer.ProcessMouse(hitUv, io)`
maps `px = uv.X * _gridPixelWidth`, `col = clamp((int)(px / _cellWidth), 0, cols-1)` (same for rows) and
feeds the engine the cell centre in integer metrics. Buttons: Left/Middle/Right press+release, drag motion
on cell change, wheel one report per notch. **Only when the shell enabled mouse tracking**
(`if (session == null || !session.Surface.IsMouseTrackingEnabled) return;` line 487) — otherwise a click
on the quad only focuses it. There is no Shift-to-select bypass in-world (no text selection on quads at all)
and no right-click context menu. Mouse events are *not* gated by a hover test beyond the raycast: while
focused, a release anywhere is forwarded if the press was forwarded (`_appMousePressSent`).

For a door-button panel: the script enables `\x1b[?1000h\x1b[?1006h` (press/release only; no motion
traffic), draws buttons with box-drawing/blocks, and parses `\x1b[<0;x;yM` / `…m` to hit-test its own
button rects. First click on the quad only focuses it (the click that focuses is consumed by `TickPicker`
in the same frame *before* `ProcessMouse` runs, so it is also forwarded as a press on that frame — a
script should therefore act on **release**, or ignore a press whose release never arrives).

### 4(e) Rendering

- Texture: `width = clamp(ceil(Cols * cellW), 256, 4096)`, `height = clamp(ceil(Rows * cellH), 256, 4096)`
  where `(cellW, cellH) = InWorldTerminalRenderer.MeasureCell(config, catalog, themeName)` =
  `ImGui.CalcTextSize("M")` with the theme's font family at the theme's `FontSize` (default **32 px**,
  `InWorldTerminalRenderer.BuildSettings:394` `config.FontSize ?? 32f`, range 4–72). Format
  `VkFormat.R8G8B8A8UNorm` (not SRGB — the quad shader applies `gammaToLinear`), own `OffscreenRenderTarget`,
  own secondary ImGui context sharing the main font atlas, own `PerFrameRenderer` with 2 frames in flight and
  **one extra graphics-queue submit per re-rendered frame** (gotcha 27). Sampler: `SamplerLinearClamped`, no
  mipmaps — a 2 m-wide quad viewed from 20 m will shimmer; prefer fewer, larger cells.
- Quad placement (part mode, `InWorldQuad.ComputePartTransform:337-364`): a unit quad (corners ±0.5)
  scaled by `(PartWidthMeters, PartHeightMeters, 1)`, rotated by `PartRotationX/Y/Z` (degrees, X·Y·Z order),
  translated by `(PartOffsetX, PartOffsetY, PartOffsetZ)` **in the part's local frame** (metres; the part's
  own scale is excluded: `partRotMat * partTransMat` is built from `part.Asmb2Ego(vehicle.Asmb2Ego)` and
  `part.PositionEgo(in vehMat)`), then into ego space. The quad's +Z faces the viewer when `PartOffsetZ > 0`
  and rotation is zero (default `PartOffsetZ = 2f`, i.e. 2 m "in front of" the part origin along the part's
  local Z). Depth-tested and depth-written (occludes / is occluded; `PipelineDepthTest`). Billboard mode:
  view-space, `(BillboardOffsetX, BillboardOffsetY, -BillboardDistance)` after the user rotation; `NoDepthTest`
  when `BillboardAlwaysOnTop`.
- **Flat quad only.** There is no mesh-conforming/decal mode and no attachment to a named mesh socket; the
  anchor is a Part/SubPart pose plus a manual offset/rotation. (`SharedQuadResource` holds one unit-quad
  VB/IB shared by all instances.)
- Multiple quads per vehicle: **yes** — N instances each with their own record/anchor; the manager just
  iterates `_instances`. gotcha 27 asks the UI to bound N "sensibly" but there is no hard cap in code.
- Draw injection: `purrTTY.GameMod/InWorld/Patches/RenderTranslucencyPassPatch.cs` postfixes
  `SuperMeshRenderSystem.RenderTranslucencyPass(CommandBuffer, bool useCustomRenderPass, IViewport viewport)`,
  reopens a dynamic-rendering scope over the viewport's offscreen colour+depth and calls
  `InWorldTerminalManager.Instance?.RecordDrawAll(commandBuffer)` → one `DrawIndexed(6,1,0,0,0)` per quad.
- Per-frame CPU cost per instance (main thread, `InWorldTerminalInstance.Frame`): `TickAndCheckDirty`
  always (inbox drain + `BuildFrame` — ~0.1–0.5 ms for a small quiet grid, dirty-tracked), and the full
  ImGui build + render-pass record + queue submit only when dirty (`frame.Generation` changed, new kitty
  image, focus change, or ≥1 s since last render). A static panel costs ~1 render/s; an animating script
  costs one off-screen pass per changed frame. The 2D hidden-drain (4 Hz) does **not** apply — in-world
  surfaces are ticked every game frame whether or not the quad is on screen or anchored.
- Teardown: `Remove` unregisters immediately, defers the GPU free `TeardownDelayFrames = 2` frames, then
  `Device.WaitIdle()` + dispose (gotcha 25). `TrySetGridSize` uses the same window (+1) before rebuilding
  the target in place; the shell session survives and gets a `NotifyTerminalResize`.

### 4(f) Persistence

In-world terminals are **session-only** (`InWorldTerminalRecord` doc + manager doc). The only persistence
is a user-saved layout: `LayoutManager.CaptureCurrentAs(name, description)` writes every live instance
(`FromInWorld(instance.Record)`) into `<Documents>/My Games/Kitten Space Agency/.purrTTY/layouts/<name>.toml`
as `[[terminal]] kind = "inworld"` with keys `cols, rows, mode, vehicle_id, part_id, part_name, sub_part_id,
offset_x/y/z, rotation_x/y/z, width_meters, height_meters, billboard_*` and a `[terminal.shell]` table
(`shell_type, custom_shell_path, custom_shell_id, arguments, working_directory, startup_command`)
(`purrTTY.Display/Layouts/LayoutTomlFormat.cs:77-124, 148-170`). Apply is user-initiated only (gotcha 29);
`LayoutManager.Apply(string layoutName)` → `_inWorld.Create(ToInWorldRecord(entry, name))`, skipping
name collisions. Opacity overrides set via `SetOpacities` are not captured (only the theme name is).

---

## 5. 2D windows

`purrTTY.Display/Ghostty/TerminalWindow.cs` (`public sealed partial class TerminalWindow : IDisposable, INamedTerminal`)
and `GhosttyTerminalController.cs`.

- **Fixed cols×rows: not possible.** `TrySetGridSize(int cols, int rows) => false` (line 225); the grid is
  always `(int)(avail / cell)` from the window's pixel size. The nearest equivalent: compute the pixel size
  from cols/rows and the font cell (`cols * cellW + chrome`, `rows * cellH + menubar`) and call
  `RequestPlacement`; the window still has a resize grip (always, even with chrome hidden — the grip is
  merely transparent, lines 322-336) so the user can change it. There is no `NoResize` flag path; adding one
  would be a one-line change in `Render` (`flags |= ImGuiWindowFlags.NoResize`).
- **No tab bar:** automatic — `RenderTabBar` draws nothing while `Sessions.Sessions.Count <= 1` (line 616).
  The menu-bar title strip (`RenderTitleStrip`, `ImGui.TextDisabled($" {Title}")` + an `x` close item) is
  always laid out; with `ThemeConfiguration.HideUiWhenNotHovered` it is drawn transparent when not hovered.
- **Title:** `Title => Sessions.ActiveSession?.Title ?? "purrTTY"`. The session title is the `sessionTitle`
  argument to `OpenWindow/OpenTab` (default `"Terminal N"`) and is overwritten whenever the shell emits
  OSC 0/2 (`Surface.TitleChanged → session.Title = title`, `TerminalSession.cs:212`). `Name` (the registry
  key, shown in the theme picker) is separate: `desiredName` ctor arg, else `"Terminal"`, `"Terminal 2"`…;
  `TryRename` is main-thread. A script can set its own title with `\x1b]2;Door Panel\x07`.
- **Position/size:** `public void RequestPlacement(float2 position, float2 size)` (line 164) — applied on the
  next `Render`, clamped to the work area (`ClampToWorkArea`), main-thread only. `CreateConfiguredWindow(WindowLayoutRecord spec, ThemeDefinition? theme)`
  takes `Position`/`Size`/`Name`/`ThemeName`/`Launch` in one call (lines 132-157) — this is the right
  factory for a launcher. `LastKnownPosition/LastKnownSize/HasObservedGeometry` read back.
- **Lock / click-through:** `TerminalWindowSettings.LockMode` (`TerminalWindowSettings.cs:43`); while
  `LockMode && !HasFocus && !_wantFocus && !IsContextMenuOpen` the window gets `ImGuiWindowFlags.NoMouseInputs`
  (clicks reach the game) and a separate hot-zone window (`HotZoneEnabled/Placement/Width/Height/Color/Opacity/HoverOpacity`)
  is the only refocus affordance (`TerminalWindow.HotZone.cs`). Also `BorderOnFocus/BorderOnHover/BorderOpacity`.
  All of these are per-window `Settings` (mutable at runtime) and also carried by a user theme
  (`ThemeDefinition.LockMode` etc. applied by `ApplyThemeOverrides`).
- **Shell exit:** see §2e — with one session the user sees nothing change; the window stays open until the
  `x` is clicked (`Close()` → `IsOpen = false` → controller prunes next `Render`; when the last window closes
  the controller hides itself, `GhosttyTerminalController.cs:415-427`). `NotifySessionStartFailed(message)`
  shows a red "Failed to start shell session: …" only for *start* failures.
- Visibility: `GhosttyTerminalController.IsVisible` toggles all windows; `OpenWindow/OpenTab` do **not**
  set it — `TerminalMenus.LaunchAsWindow` sets `c.IsVisible = true` afterwards (`TerminalMenus.cs:71-78`).
  `IsVisible = true` with no open windows opens a default-shell window (line 334-336) — a launcher must open
  its window *before* flipping visibility, or it will spawn an unwanted default shell.

---

## 6. Cross-mod launcher extension — proposed design (purrTTY side, `feature/scripting`)

Constraints restated: `purrTTY.Terminal`/`purrTTY.Display`/`purrTTY.GameMod` are not exported (and must not
be — they drag `Ghostty.Vt`, KSA types and ImGui into the consumer); the ABI must live in
`purrTTY.CustomShellContract` (exported, imported by gatOS already); every real operation is main-thread-only
and most need an active ImGui frame. The §4.4 sketch in the earlier report covered (i)/(v) for a 2D window;
this extends it to in-world, handles, and lifecycle events, and fixes the threading precisely.

### 6.1 Contract additions (`purrTTY.CustomShellContract`, namespace `purrTTY.Core.Terminal`, new files)

```csharp
// TerminalLaunch.cs
public enum TerminalPlacementKind { Window, Tab, InWorldPart, InWorldBillboard }

public sealed class TerminalProgramSpec            // what runs inside
{
    public string? CustomShellId { get; set; }      // ICustomShell id  (v: null → real process)
    public string? Program { get; set; }            // ShellType.Custom path/bare name; null+null → configured default shell
    public List<string> Arguments { get; set; } = new();
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new();
    public string? StartupCommand { get; set; }
    public Dictionary<string, object> Configuration { get; set; } = new();   // → CustomShellStartOptions.Configuration (today unused)
}

public sealed class TerminalWindowOptions
{
    public string? Name { get; set; }               // registry name; null = auto
    public string? Title { get; set; }              // session title
    public string? ThemeName { get; set; }
    public float? PosX, PosY, Width, Height;        // px; null = cascade/default
    public int? Cols, Rows;                         // converted to px from the theme cell size; null = default
    public bool? LockMode, HideChrome, NoResize;    // NoResize = new ImGui flag (1 line in TerminalWindow.Render)
    public bool Focus { get; set; } = true;
    public bool ShowTerminal { get; set; } = true;  // controller.IsVisible = true after opening
}

public sealed class TerminalInWorldOptions
{
    public string? Name { get; set; }  public string? ThemeName { get; set; }
    public int Cols { get; set; } = 40;  public int Rows { get; set; } = 12;
    public string VehicleId { get; set; } = "";     // "" = controlled vehicle
    public string PartId { get; set; } = "";        // Part.Id (template id or instance name)
    public uint? PartInstanceId { get; set; }       // NEW: Part.InstanceId, preferred when set
    public string SubPartId { get; set; } = "";
    public float OffsetX, OffsetY, OffsetZ = 0.05f; public float RotationX, RotationY, RotationZ;
    public float WidthMeters = 0.6f, HeightMeters = 0.2f;
    public float? BackgroundOpacity, ForegroundOpacity, CellBackgroundOpacity;
    public bool Billboard { get; set; }  public float BillboardDistance = 5f;  public bool BillboardAlwaysOnTop = true, BillboardClickToFocus = true;
    public bool Focus { get; set; }
}

public sealed class TerminalLaunchRequest
{
    public TerminalPlacementKind Placement { get; set; } = TerminalPlacementKind.Window;
    public TerminalProgramSpec Program { get; set; } = new();
    public TerminalWindowOptions? Window { get; set; }
    public TerminalInWorldOptions? InWorld { get; set; }
}

public enum TerminalLifecycleEvent { Created, Started, Focused, Unfocused, Resized, Exited, Closed }

public interface ITerminalHandle
{
    Guid Id { get; }                               // launcher-assigned
    string Name { get; }                           // registry name (may differ from requested)
    TerminalPlacementKind Placement { get; }
    bool IsOpen { get; }  bool IsRunning { get; }  int? ExitCode { get; }
    int Cols { get; }  int Rows { get; }
    event Action<ITerminalHandle, TerminalLifecycleEvent>? Lifecycle;   // raised on the MAIN thread
    Task FocusAsync();  Task CloseAsync();  Task RenameAsync(string name);
    Task<bool> ResizeAsync(int cols, int rows);    // windows: px from cell size; in-world: TrySetGridSize
    Task SetPlacementAsync(TerminalInWorldOptions placement);   // in-world only: live offset/rotation/size/anchor
    Task ApplyThemeAsync(string themeName);
    void WriteInput(ReadOnlyMemory<byte> bytes);   // inject into the session as if typed (session.SendInput)
}

public interface ITerminalLauncher
{
    bool IsAvailable { get; }                      // false until purrTTY's controller is up
    Task<ITerminalHandle> LaunchAsync(TerminalLaunchRequest request, CancellationToken ct = default);   // any thread
    IReadOnlyList<ITerminalHandle> Terminals { get; }           // snapshot, any thread
    ITerminalHandle? Find(string name);
    event Action<ITerminalHandle, TerminalLifecycleEvent>? Lifecycle;   // main thread
}

public static class TerminalLauncherRegistry
{
    public static ITerminalLauncher? Current { get; set; }   // purrTTY sets in InitializeTerminal, clears in DisposeResources
    public static event Action<ITerminalLauncher?>? Changed; // so a mod that loaded first can attach late
}
```

Everything above is POCOs + interfaces — no `ProcessLaunchOptions`, no ImGui/KSA types — so the contract
assembly keeps its current dependency set (`purrTTY.Logging` only).

### 6.2 purrTTY-side implementation (all in `purrTTY.GameMod`, no new exports)

New file `purrTTY.GameMod/Launcher/TerminalLauncher.cs` (`internal sealed class TerminalLauncher : ITerminalLauncher`),
built in `TerminalMod.InitializeTerminal` after `_controller` (and re-bound to `_inWorld` in
`TryInitializeInWorld`), published as `TerminalLauncherRegistry.Current`, nulled in `DisposeResources`.

- **Main-thread marshalling:** a `ConcurrentQueue<Action>` (`_mainThreadQueue`) plus `Pump()` called at the
  top of `TerminalMod.OnAfterUi` (before `_controller.Render()` and `_inWorld.OnAfterGui(dt)`), so queued
  work runs with an ImGui frame active — the precondition for `InWorldTerminalManager.Create`,
  `TerminalWindow` construction and `TerminalTargetRegistry`. `LaunchAsync` enqueues and completes a
  `TaskCompletionSource<ITerminalHandle>` (`RunContinuationsAsynchronously`) from the pump; if called on the
  main thread *inside* `OnAfterUi` it may execute inline (detect via a `[ThreadStatic]` "pumping" flag).
  Every `ITerminalHandle` method follows the same enqueue pattern; properties read volatile snapshots
  updated by the pump. `Lifecycle` events are raised from the pump (main thread) — consumers that need
  another thread marshal themselves (document it).
- **Mapping `TerminalProgramSpec` → `ProcessLaunchOptions`:** `CustomShellId != null` →
  `ProcessLaunchOptions.CreateCustomGame(id)` (+ env/cwd/StartupCommand copied); `Program != null` →
  `ProcessLaunchOptions.CreateCustom(program, args)`; both null → `controller.Configuration.CreateLaunchOptions()`.
  `Environment` merged into `EnvironmentVariables`. (`Configuration` needs one extra line in
  `CustomShellPtyBridge.StartAsync` to be forwarded — `purrTTY.Terminal`, 1 line — or carry it in env vars.)
- **(i) Window/Tab:** `Tab` → `controller.OpenTab(opts, title)` and resolve `controller.FocusTarget.Sessions`
  to find the new session (subscribe `SessionManager.SessionCreated` *before* the call to catch it);
  `Window` → `controller.CreateConfiguredWindow(new WindowLayoutRecord { Name, Position, Size, ThemeName, Launch }, theme)`
  (exact name + geometry); then `Settings.LockMode/...` from options, `window.RequestFocus()` if `Focus`,
  `controller.IsVisible = true` if `ShowTerminal` (after the window exists — §5 last bullet). `Cols/Rows` →
  px via the same `MeasureCell` used in-world (`InWorldTerminalRenderer.MeasureCell(config, catalog, themeName)`
  is `public static` already) plus menubar height (`ImGui.GetFrameHeight()`) — approximate but good.
  `NoResize` → add `public bool NoResize { get; set; }` to `TerminalWindowSettings` and OR the ImGui flag in
  `TerminalWindow.Render` (purrTTY.Display, ~3 lines).
- **(ii) In-world:** build an `InWorldTerminalRecord` from `TerminalInWorldOptions` (direct field copy;
  `PartInstanceId` needs a new `uint? TargetPartInstanceId` on the record and a branch in
  `InWorldQuad.ResolveTopLevelPart`/`ResolvePart` matching `p.InstanceId`, ~15 lines), call
  `_inWorld.Create(record)`, then `instance.SetOpacities(...)` and `_inWorld.Focus(instance)` if requested.
- **(iii) Close/focus/resize/rename:** window → `controller.CloseWindow(window)`, `window.RequestFocus()`,
  `window.RequestPlacement(pos, pxSize)`, `window.TryRename(name)`; in-world → `_inWorld.Remove(instance)`,
  `_inWorld.Focus(instance)`, `instance.TrySetGridSize(cols, rows)`, `instance.TryRename(name)`,
  `instance.ApplyTheme(catalog.Find(name))`, and live `Record.*` mutation for `SetPlacementAsync`.
  `WriteInput` → `session.SendInput(bytes)` (enqueue to main thread for ordering with user input).
- **(iv) Lifecycle:** wire `window.FocusChanged` (`Action<TerminalWindow,bool>`), `window.IsOpen` polled in
  the pump (there is no close event — add `public event Action<TerminalWindow>? Closed` raised in
  `TerminalWindow.Close()`/`Dispose` for cleanliness, purrTTY.Display, ~5 lines), `SessionManager.SessionCreated/SessionClosed`,
  `TerminalSession.ProcessExited` (**off-thread** — re-enqueue to the pump before raising `Exited`),
  in-world `HasFocus` transitions polled per pump tick (or add a `FocusChanged` event on
  `InWorldTerminalInstance`, ~5 lines), and `Surface.Cols/Rows` change detection for `Resized`.
- **(v) Real process (nvim):** already covered by `Program`/`Arguments`/`WorkingDirectory`/`Environment` →
  `ShellType.Custom`; `Exited` carries the real exit code via `ProcessExited`. The prior report's caveats
  (PATH on macOS GUI launch, pass an absolute path) stand.

Estimated size: contract ~200 lines (new files only, additive, no existing signature changes); GameMod
launcher ~350–450 lines; Display/Terminal touch-ups ~30 lines (`NoResize` flag, `Closed` event,
`Configuration` forwarding, `TargetPartInstanceId`). Tests: a `FakeMainThreadPump` harness in
`purrTTY.GameMod` is not testable without KSA; put the request→options mapping and handle state machine in
a pure class (`TerminalLaunchMapper`) under `purrTTY.Display` so `purrTTY.Display.Tests` can cover it.
Versioning: bump the contract assembly's `AssemblyVersion` minor; consumers that reference the old DLL still
bind (additive types only).

Consumer side (catcom): `RegisterShell("ksa-script", () => new ScriptTerminalShell(...))` in
`[StarMapAllModsLoaded]`, then `TerminalLauncherRegistry.Current?.LaunchAsync(new TerminalLaunchRequest {
Placement = InWorldPart, Program = new { CustomShellId = "ksa-script", Environment = { ["KSA_SCRIPT"] = path } },
InWorld = new { Cols = 40, Rows = 12, PartInstanceId = …, WidthMeters = 0.6f, HeightMeters = 0.2f } })`.
Because the shell factory takes no arguments, pass the script identity through `Environment` (available in
`CustomShellStartOptions.EnvironmentVariables` today) — that is how `GameConsoleShell` receives its prompt
(`WellKnownShellEnvironment.GameShellPrompt`).

---

## 7. ImGui helpers worth copying

`purrTTY.GameMod/UI/ImGuiWidgets.cs` — `public static class ImGuiWidgets`:

```csharp
public static readonly float4 ErrorColor, SuccessColor, WarningColor;
public static void SetNextModalWidth(float width);                       // SetNextWindowSize((w,0), Always) before BeginPopupModal(AlwaysAutoResize)
public static bool BeginFormTable(string id);                            // 2-col table, label ¼ / widget ¾, CellPadding 6, NoPadOuterX
public static bool BeginFormTableFixed(string id, float labelWidth);     // fixed label gutter (use MeasureLabelWidth)
public static void EndFormTable();
public static float MeasureLabelWidth(IReadOnlyList<string> labels);     // widest label + 16
public static void FormRow(string label);                                // label cell + SetNextItemWidth(-1) for the widget cell
public static bool DestructiveButton(string label, float2? size = null); // red KSA "Destroy" style
public static bool FilterCombo(string id, string preview, ImInputString filter,
                               IReadOnlyList<(string Key, string Label)> items, out string? selectedKey);
```

`FilterCombo` is a `BeginCombo` whose first row is an `InputText` filter (`SetKeyboardFocusHere` on
`IsWindowAppearing`), rows are `Selectable($"{label}##{id}_{i}")`, and the filter is cleared on choice.
The BRUTAL `ImInputString` quirk (gotcha 24, `iminputstring-enterreturns-true`): `.Length` refreshes only on
a `true` `InputText` return, so **never pass `EnterReturnsTrue` and call `filter.EvaluateLength()` every frame
before `.ToString()`** — `FilterCombo` does both; `ThemeDialog` (`_renameInput`, `_saveNameInput`) and
`InWorldManagerUI` (`_nameInput`, `_startupCommandInput`) follow it (`ThemeDialog.cs:172-173, 225-226`).
Buffers are allocated once per field: `private readonly ImInputString _nameInput = new(64);`.

Dialog pattern (`ThemeDialog`, `InWorldManagerUI`, `LayoutManagerUI`): non-modal movable window —
`RequestOpen()` sets `_visible = true; _requestFocus = true;`; `Render()/Draw()` early-outs when hidden,
`ImGui.SetNextWindowFocus()` once, `SetNextWindowSize(.., FirstUseEver)`, `ImGui.Begin(PopupId, ref _visible, ImGuiWindowFlags.None)`
with a `"Title##unique_id"` id, always `End()`. Pumped from `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`).
Per-instance child windows cascade by `(id % 6) * 28f` (`InWorldManagerUI.cs:356`). Modals
(hotkey settings) use `OpenPopup` + `BeginPopupModal(id, ref open, AlwaysAutoResize)` with a 2-column
summary table (`TerminalMod.RenderHotkeySummaryTable`).

Hotkey suppression while typing: purrTTY does **not** suppress per-widget; it relies on
`Patch03_HotkeyGuard` (prefix on `GameSettings.OnKeyAll`): `if (!consoleOpen && (ImGui.GetIO().WantTextInput || InWorldTerminalManager.IsInputFocused)) { __result = true; return false; }`
(`Patcher.cs:135-152`). Because purrTTY installs this process-wide, a sibling mod's `InputText` fields are
**already protected while purrTTY is loaded**; if catcom must work without purrTTY it needs its own
copy of the same 10-line prefix (Harmony id distinct; both prefixes returning `false` is harmless). The
toggle hotkey itself is read with `ImGui.IsKeyPressed(key, repeat:false)` and skipped while
`io.WantTextInput` (`ToggleHotkeyBinding.MatchesPress`).

Menus: `TerminalMod.DrawMenu()` is `[ModMenuEntry("purrTTY")] public static void` (ModMenu.Attributes.dll,
shipped) and delegates to `TerminalMenus.DrawMenuContent()`; the fallback `Patch02` postfixes
`KSA.Program.DrawProgramMenusHook()` and draws `ImGui.BeginMenu("purrTTY")` only when
`ModLibrary.Find("ModMenu") is null` (cached, reset on unload), calling
`((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true)` to keep the auto-hiding bar open.
Menu content is static-field seams (`TerminalMenus.Toggle`, `OpenThemeDialog`, `MenuController`…) set at
init and nulled at dispose, so the draw path never NREs. Registered custom shells are enumerated **live on
every draw** (`DrawRegisteredCustomShellItems`), so a mod registering late still appears.

---

## 8. Fonts, glyphs and the canvas resolution of a 40×12 panel

Bundled (`purrTTY.GameMod/TerminalFonts/`, copied to the dist dir): `HackNerdFontMono` (R/B/I/BI),
`JetBrainsMonoNerdFontMono` (R/B/I/BI), `SpaceMonoNerdFontMono` (R/B/I/BI), `ProFontWindowsNerdFontMono`,
`ProggyCleanNerdFontMono`, `ShureTechMonoNerdFontMono` (regular only, `.iamttf`) and
`DepartureMonoNerdFont-Regular.otf` (listed in the registry but the loader globs `*.iamttf` only —
`PurrTTYFontManager.LoadFonts:56` — so **Departure Mono does not actually load**; it falls back to Hack).
Family display names: "Hack" (default), "Jet Brains Mono", "Space Mono", "Pro Font", "Proggy Clean",
"Shure Tech Mono", "Departure Mono".

Glyph coverage: KSA's ImGui is **1.92.2** (`brutal-imgui-standalone/native/upstream/imgui.h:31`,
`IMGUI_HAS_TEXTURES`), i.e. the dynamic font atlas — `atlas.AddFontFromFileTTF(new ImString(fontPath), 32.0f)`
with no glyph ranges rasterises **any codepoint the TTF contains on demand**, sized at draw time via
`ImGui.PushFont(font, fontSize)`. So the full Nerd Font Mono set renders in-world and in 2D: box drawing
U+2500–257F (single/double/rounded/heavy lines, all junctions), geometric shapes U+25A0–25FF, arrows,
Powerline U+E0A0–E0D4, Nerd Font PUA icons U+E000–F8FF and U+F0000–F1AF0 (Material Design), Font Awesome,
Devicons, Octicons, Weather, Codicons (U+EA60–EBEB), Pomicons, plus Latin/Greek/Cyrillic from the base font.
Two ranges bypass the font entirely and therefore always render regardless of font: **Block Elements
U+2580–259F** (half/eighth blocks, quadrants, ░▒▓ shades as exact rects — pixel-perfect, no seams,
`FrameGridRenderer.DrawBlockStrip/DrawBlockCell`) and **Braille U+2800–28FF** (2×4 dot matrix via
`AddCircleFilled`; the fonts ship no braille). Not covered: CJK, emoji (no colour font), and any symbol the
Nerd Font lacks → U+FFFD tofu. Wide (`CellWidth.Wide`) cells are drawn per-cell, unbatched. For buttons and
panels the reliable palette is: box drawing for frames, `▀▄█▌▐░▒▓` for fills/bars, `●○◆◇■□▲▼◀▶` for
indicators, Powerline arrows for tabs, and the Nerd Font PUA for icons (e.g. `nf-md-door_open` U+F081C).

Cell size: the in-world cell is measured as `ImGui.CalcTextSize("M")` for the theme's font at the theme's
`FontSize` (default **32 px**; `LayoutConstants.MIN_FONT_SIZE = 4`, `MAX = 72`); fallback `(0.6·size, 1.2·size)`.
For Hack Nerd Font Mono the advance is ≈0.602 em and the line height ≈1.19 em (measured values are
font-dependent; the renderer uses whatever ImGui returns), so:

| FontSize | cell ≈ (w × h px) | 40×12 texture (px, after 256 floor) | shell sees |
|---|---|---|---|
| 32 (default) | 19.3 × 38 | 772 × 456 | 40 × 12 |
| 24 | 14.4 × 29 | 576 × 348 | 40 × 12 |
| 16 | 9.6 × 19 | 384 × 256 (height floored from 228) | 40 × **13** (`(int)(256/19)`) |
| 12 | 7.2 × 14 | 288 × 256 (floored from 288×168) | 40 × **18** |

The script must therefore honour `NotifyTerminalResize` rather than trusting the requested rows (§2c). The
quad's physical size is independent of the texture (`PartWidthMeters × PartHeightMeters`); pick the
texture so that `cols·cellW / WidthMeters ≈ rows·cellH / HeightMeters` to avoid anisotropic stretching
(e.g. 40×12 at 32 px → 772×456 → aspect 1.69 → a 0.6 m × 0.355 m panel). Note the renderer paints the
theme background over the whole texture, but the grid occupies only `cols·cellW × rows·cellH` of it — the
remainder (from ceil/floor) is bare background colour at the right/bottom edge.

---

## 9. Discrepancies found (doc vs code)

- `docs/gotchas.md` §18 says the inbox cap is 8 MiB; code is `MaxInboxBytes = 24 MiB` with
  `MaxBytesPerTick = 8 MiB` (`GhosttyTerminalSurface.cs:114, 148`).
- `InWorldTerminalRenderer` class doc says "Input/focus is wired in a later phase; for now the terminal
  renders unfocused" — stale; keyboard + app-mouse are wired (`ProcessMouse`, manager lines 288-309).
- `DepartureMonoNerdFont-Regular.otf` is registered as a family but never loaded (`*.iamttf` glob).
- The earlier internals report says "`CustomShellPtyBridge.Write` … `CustomShellPtyBridge.cs:939-978`" —
  the file is 380 lines; the relevant lines are 211-250.
- `ICustomShell.StopAsync` is documented as the graceful stop, but purrTTY only invokes it on
  `RestartSessionAsync`; every ordinary close goes straight to `Dispose()` on a pool thread.
