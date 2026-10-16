---
type: Design
title: Terminal scripts - script-owned virtual terminals rendered by purrTTY
description: Design for scripts that run forever and own a terminal - emitting escape codes and consuming keyboard and mouse bytes with no OS shell - rendered by purrTTY in a 2D window or on an in-world part quad; covers the host-side virtual terminal, the purrTTY custom-shell adapter, the script-facing term API, lifecycle and attachment rules, the shipped TUI helper library, and the door-panel example.
tags: [design, terminal, virtual-pty, tui, in-world, purrtty, forever-scripts]
sources:
  - id: bridge
    resource: /existing-mods/purrtty-virtual-pty-bridge.md
    title: purrTTY custom shells as a virtual pty
  - id: inworld
    resource: /existing-mods/purrtty-in-world-terminals.md
    title: purrTTY in-world terminals and 2D windows
  - id: launcher
    resource: /existing-mods/purrtty-terminal-launcher-gap.md
    title: purrTTY terminal launcher gap
  - id: hooks
    resource: /architecture/hook-model-sync-async.md
    title: Hook model
  - id: bindings
    resource: /architecture/script-exports-and-bindings.md
    title: Script exports and bindings
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-17T00:00:00Z }
status: draft
---

# Concept

A **terminal script** is a service (forever) script whose context carries a `term`: a virtual terminal the script draws into with ordinary escape sequences and reads keyboard/mouse bytes from, exactly like a TUI program on a pty — except there is no pty and no OS process. purrTTY renders it through its custom-shell "mock pty" path (verified identical to real pty sessions for every VT mode) in a 2D window, as a tab, or on an **in-world quad anchored to a vehicle part**, where clicks are ray-cast to cells and arrive as SGR mouse reports.[^bridge][^inworld]

```
script (Lua/TS, game thread)              host (KsaScripting)                                purrTTY
──────────────────────────                ──────────────────────────────────                 ──────────────────────────
term.write("\x1b[?1049h…")  ──────────▶   ScriptTerminal.ring buffer ──QueueOutput──▶  ScriptTerminalShell : BaseChannelOutputShell
                                               ▲ flushed once per Step slot                     │ OutputReceived → Surface.Write → libghostty-vt
await term.read() ◀─── input queue ◀── game-thread queue ◀── WriteInputAsync(bytes) ◀──────────┘ keys / SGR mouse / engine replies
term.on('resize')  ◀────────────────────── NotifyTerminalResize(cols, rows)
                                                                                           2D window ─ or ─ in-world quad on a Part
```

# Host side: `ScriptTerminal` and `ScriptTerminalShell`

- **`ScriptTerminal`** (Core, one per terminal script instance): output ring buffer (default 256 KiB, holds the last full repaint so a surface attached later shows the current screen), input queue, current size, attached-surface count, and a tiny mode tracker that notices the DEC private modes the script sets (for the status window only; purrTTY interprets them).
- **`ScriptTerminalShell`** (GameMod, `BaseChannelOutputShell`), registered once as custom shell id `ksa-script`; the target script is passed in `EnvironmentVariables["KSA_SCRIPT"]` because shell factories take no arguments. Behaviour, all derived from the verified bridge facts:[^bridge]
  - `OnStartingAsync`: bind to the `ScriptTerminal` (start the script if `KSA_SCRIPT_AUTOSTART=1`), note `InitialWidth/Height` as provisional, queue the ring buffer contents as the first output so the surface repaints.
  - `WriteInputAsync` (synchronous on purrTTY's tick thread): copy bytes into the game-thread queue, return `Task.CompletedTask`; never run script code here.
  - `NotifyTerminalResize`: enqueue a `resize` event; the script re-lays out (the real grid arrives within a frame or two of start; in-world grids can be larger than requested).
  - Ctrl+C arrives as `0x03`; the host surfaces it as a `cancel` event; purrTTY never calls `RequestCancellation`.
  - `Dispose`: detach from the terminal (script keeps running unless its policy says "stop when last surface closes"); teardown here, not in `OnStoppingAsync`.
  - When the script stops: queue `"\r\n[script exited]\r\n"` then `RaiseTerminated(0)`, because a single-session window shows no exit marker.
- **Flush discipline:** the host wraps each Step-slot flush in DEC 2026 so multi-write repaints land atomically; the per-frame volume for a 40×12 panel (2–5 KB) is far below purrTTY's 8 MiB per-tick drain.

# Script-facing API (`ctx.term`)

| Member | Meaning |
|---|---|
| `write(string | bytes)` | append output (buffered; flushed every Step slot or on `flush()`) |
| `read(): Promise<bytes>` / Lua yielding `read()` | next input chunk (keys, mouse reports, replies); use the `tui` parser on top |
| `on('data' | 'resize' | 'attach' | 'detach' | 'cancel', fn)` | push-style alternatives to `read()`; `resize` carries `{cols, rows}` |
| `size` → `{cols, rows}`, `attached` → count | current grid and number of attached surfaces |
| `clear()`, `flush()`, `title(s)` | convenience (title via OSC 2) |
| `policy({ stopWhenDetached: boolean, autoAttach: [...] })` | lifecycle policy; default keeps running and buffers while detached |

Modes the script typically sets at start: `\x1b[?1049h` (alt screen), `\x1b[?25l` (hide cursor), `\x1b[?1000h\x1b[?1006h` (press/release mouse, SGR), optionally `\x1b[>1u` (kitty keys for unambiguous Esc). On exit it restores them.

# Attachment and placement

- Surfaces are created through the purrTTY launcher contract (Plan 00b): `TerminalLaunchRequest { Placement = Window | Tab | InWorldPart | InWorldBillboard, Program = { CustomShellId = "ksa-script", Environment = { KSA_SCRIPT = "<name>" } }, Window/InWorld options }`. The Script Manager's Terminal tab drives this; placements persist in `terminals.toml` keyed by script, with `VehicleId`, `PartInstanceId` (new purrTTY field) or `PartId`, offset/rotation/size, cols×rows, theme, opacities.[^launcher][^inworld]
- Several surfaces may attach to one terminal (a 2D window and an in-world quad at once): each gets the same output stream; input from any surface merges into one queue. Each surface may report a different grid (2D windows derive the grid from pixels; in-world from the record); the host reports the **smallest** attached grid as `term.size` so one drawing fits all, or the script can opt into per-surface sizing later.
- The focusing click on an in-world quad is also delivered as a mouse press; the `tui` helpers therefore fire buttons on **release**.
- Vehicle removal or decouple: purrTTY follows the `Part` by reference; if the part disappears the surface is removed and the script receives `detach`.

# Lifecycle

| Event | Effect |
|---|---|
| Script `Start` (UI, autostart, console) | runs `main(ctx)` as a service coroutine/async; `ctx.term` exists even with no surface |
| Surface attached | `attach` event; ring buffer replay; `resize` follows |
| Script `Stop` / reload | `cancel` signal, then the shell prints the exit line and terminates; configured placements are re-created on the next `Start` |
| Last surface closed | by policy: keep running (default) or stop |
| Watchdog trip | script disabled; shell prints the error and terminates |

# Shipped helper library (`scripts/lib/tui.ts`, `tui.lua`)

Thin, dependency-free, generated from one spec: screen helpers (alt screen, cursor, SGR colours, clear, DEC 2026 frames), an incremental input parser returning typed events (`key` with name/modifiers under both legacy and kitty encodings, `mouse` with button/action/col/row/modifiers from SGR reports, `reply` for engine responses, `text`), a rect/hit-test helper, and a few widgets (label, button, toggle, progress bar, list). No layout engine; scripts draw with box characters and block elements, which render identically in 2D and in-world.[^inworld]

# Example: door panel on a part (TypeScript)

```ts
import { ksa } from "ksa";
import { Screen, parseInput, Button } from "./lib/tui";

export async function main(ctx: ksa.TerminalContext) {
  const t = ctx.term, s = new Screen(t);
  const btn = new Button({ x: 2, y: 2, w: 16, label: "OPEN DOOR" });
  const draw = () => s.frame(() => { s.clear(); s.box(1, 1, t.size.cols - 2, t.size.rows - 2, "DOOR CONTROL"); btn.draw(s, ctx.ksa.active?.lights[0]?.on ?? false); });
  s.enterApp({ mouse: "press-release" }); draw();
  t.on("resize", draw);
  for await (const ev of parseInput(t)) {
    if (ev.kind === "mouse" && ev.action === "release" && btn.hit(ev)) { const l = ctx.ksa.active?.lights[0]; if (l) l.on(!(l.on)); draw(); }
    if (ev.kind === "key" && ev.name === "q") break;
    if (ctx.signal.cancelled) break;
  }
  s.leaveApp();
}
```

The Lua version is the same shape with `tui.parse_input(t)` as a yielding iterator.

[^bridge]: purrTTY custom shells as a virtual pty
[^inworld]: purrTTY in-world terminals and 2D windows
[^launcher]: purrTTY terminal launcher gap
[^hooks]: Hook model
[^bindings]: Script exports and bindings
