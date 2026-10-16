---
type: Component Analysis
title: purrTTY custom shells as a virtual pty (CustomShellPtyBridge data path)
description: Verified facts for using an ICustomShell as the mock pty behind a script-owned terminal - raw input delivery (keys, SGR mouse, engine replies), resize semantics, initial-output and termination behaviour, thread of every callback, output buffering limits, the exact byte encodings a script receives, and the lifecycle caveats (Dispose without StopAsync, no RequestCancellation, no exit marker on single-session windows).
resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.Terminal/Pty/CustomShellPtyBridge.cs
tags: [purrtty, custom-shell, virtual-pty, input-encoding, mouse, keyboard, threading]
sources:
  - id: report
    resource: /references/purrtty-virtual-pty-report-2026-10-16.md
    title: purrTTY virtual-pty research report (2026-10-16)
  - id: bridge
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.Terminal/Pty/CustomShellPtyBridge.cs
    title: CustomShellPtyBridge.cs
  - id: surface
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs
    title: GhosttyTerminalSurface.cs
  - id: encoder
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.Display/Ghostty/TerminalInputEncoder.cs
    title: TerminalInputEncoder.cs
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-17T00:00:00Z }
status: stable
---

# Why this works without an OS pty

A `ShellType.CustomGame` session wraps a registered `ICustomShell` in `CustomShellPtyBridge : IProcessManager`. From there the session is **identical** to a real pty session: the same `TerminalSession`, the same `GhosttyTerminalSurface`, the same frontends. Nothing checks `ShellType`, so every VT mode a script sets (alt screen, SGR mouse, bracketed paste, kitty keyboard, cursor hide, DEC 2026) is honoured for custom shells exactly as for a shell process.[^report][^bridge]

# Data path

| Direction | Mechanism | Thread |
|---|---|---|
| Script → screen | `QueueOutput(bytes)` → `OutputReceived` → `TerminalSession.OnProcessDataReceived` → `Surface.Write` (24 MiB inbox, 8 MiB drained per tick) | shell's thread (pool pump for `BaseChannelOutputShell`) |
| Screen → script | `CustomShellPtyBridge.Write(bytes)` → `ICustomShell.WriteInputAsync(bytes)`; **raw, never line-buffered**, one call per chunk, fresh `byte[]` per call | **main/tick thread, synchronously inside ImGui rendering** (keys, mouse, engine replies); pool thread for `StartupCommand` |
| Resize | `CustomShellPtyBridge.Resize` → `NotifyTerminalResize(cols, rows)` when the frontend's derived grid differs from the surface | main/tick thread |
| Start | `StartAsync(CustomShellStartOptions { InitialWidth, InitialHeight, WorkingDirectory, EnvironmentVariables })` then `SendInitialOutput()` once | pool thread |
| Stop | ordinary close → `Dispose()` on a pool thread; **`StopAsync` is only called by `RestartSessionAsync`** | pool thread |

Chunk granularity: a named key or chord is one complete sequence per call; typed text arrives **one Unicode scalar per call**; a mouse report is one complete SGR sequence; a paste or `StartupCommand` is many keys in one call; engine replies are batched once per `BuildFrame`. A script's input parser must be an incremental state machine over a byte stream and must not assume one call equals one key.[^report]

# Engine query replies reach the script

Every reply the VT engine generates is written back through `WriteInputAsync` as a real pty child would read it: DA1 `\x1b[?62;22;52c` (test-pinned), DA2 `\x1b[>1;10;0c`, CPR `\x1b[<row>;<col>R`, DSR `\x1b[0n`, kitty keyboard query `\x1b[?<flags>u`, kitty graphics `\x1b_G…;OK\x1b\\`. XTVERSION and ENQ are not answered; focus events are never sent; OSC 52 read is not answered. Replies arrive on the next tick after the query. A script should not query (it knows it is on purrTTY); its parser must tolerate CSI with `?`/`>`/`=` markers and APC sequences anyway.[^report][^surface]

# Initial size is provisional

`InitialWidth/Height` come from the `SessionManager`'s last-known dimensions (default 80×25), overriding the launch options; the real grid is imposed on the first rendered frame and delivered via `NotifyTerminalResize`. For in-world terminals the texture is clamped to a 256 px minimum, so the shell may receive **more** columns/rows than requested (e.g. 8 requested cols → 13). Always re-layout on resize.[^report]

# Lifecycle caveats

- **No `RequestCancellation`:** purrTTY never calls it; Ctrl+C arrives as byte `0x03` (or `\x1b[99;5u` under kitty flags). A raw-mode shell interprets it itself.
- **Termination:** `RaiseTerminated` → `ProcessExited`; a single-session 2D window shows **no exit indication** (the tab bar is hidden), in-world quads keep the last texture; keystrokes after exit are silently dropped; no auto-close or restart; reattach is impossible (closing disposes the session). Print a final line such as `[script exited]` before terminating.
- **`Dispose` does not run `OnStoppingAsync`** for `BaseChannelOutputShell`; put teardown in `Dispose`.
- **Output with no subscriber is lost**, not buffered (`OutputReceived?.Invoke`; `QueueOutput` is a no-op before `StartAsync`). A script that outlives its terminal must keep its own screen model and repaint on (re)attach.
- `SendInitialOutput` runs after `StartAsync` succeeded, on the pool thread, with `DataReceived` already wired; the order of first bytes is `OnStartingAsync` output → `SendInitialOutput` → `StartupCommand` (as input).[^report]

# Output budget

`BaseChannelOutputShell` uses an unbounded channel and a single pump; the surface inbox is 24 MiB with an 8 MiB per-tick drain and a 500 ms back-pressure wait for non-tick writers. A 40×12 panel repainting fully is 2–5 KB per frame, three orders of magnitude under the cap. Wrap each repaint in DEC 2026 (`\x1b[?2026h … \x1b[?2026l`, 1 s safety timeout) to make multi-write frames land atomically. Drain cadence: visible 2D every frame, hidden 2D every 250 ms, in-world every game frame.[^report]

# Exact input encodings (legacy mode)

Mouse (only when the script enabled a tracking mode; otherwise no mouse bytes at all): with `?1006h`, press `\x1b[<Cb;Cx;CyM`, release `\x1b[<Cb;Cx;Cym`, 1-based cells; `Cb` 0/1/2 = left/middle/right, +32 motion (1002 drag, 1003 hover = 35), 64/65 wheel up/down (one report per notch), +4 Shift, +8 Alt, +16 Ctrl. Motion only on cell change; the reported cell is the glyph under the cursor. Shift-click never reaches the app in 2D (selection bypass); in-world has no bypass. Buttons: left/middle/right + wheel.[^report][^encoder]

Keyboard: Enter `\r`, Tab `\t`, Backspace `\x7f`, Escape `\x1b` immediately (no timer), arrows `\x1b[A/B/C/D` (`\x1bOA…` under DECCKM), Home/End `\x1b[H`/`\x1b[F`, Insert/Delete/PgUp/PgDn `\x1b[2~ 3~ 5~ 6~`, F1–F4 `\x1bOP…S`, F5–F12 `\x1b[15~ 17~ 18~ 19~ 20~ 21~ 23~ 24~` (F13+ unmapped; F12 is purrTTY's toggle in 2D but reaches an in-world terminal), modified named keys `CSI 1;<m> X`, Ctrl+letter control bytes, Alt+letter `\x1b` + letter, Ctrl+Space/[/\\/] `0x00 0x1b 0x1c 0x1d`, plain text as UTF-8 per codepoint (control chars dropped), Ctrl+Shift+C/V never delivered, Super not read, **no key release or repeat events**. With kitty flag 1 (`\x1b[>1u`): `CSI <codepoint>;<mods>u` for non-text keys (Ctrl+C → `\x1b[99;5u`, Escape → `\x1b[27u`, Enter → `\x1b[13u`); pop with `\x1b[<u` on exit.[^report][^encoder]

[^report]: purrTTY virtual-pty research report (2026-10-16)
[^bridge]: CustomShellPtyBridge.cs
[^surface]: GhosttyTerminalSurface.cs
[^encoder]: TerminalInputEncoder.cs
