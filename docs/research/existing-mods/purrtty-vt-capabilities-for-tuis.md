---
type: Capability Matrix
title: purrTTY VT and input capabilities relevant to running neovim
description: Which terminal features libghostty-vt plus the purrTTY ImGui frontend support or lack (truecolor, kitty keyboard, SGR mouse, bracketed paste, DEC 2026, focus events, OSC 52, sixel), how keys are captured from the game, and the known full-screen-TUI issues.
tags: [purrtty, vt, neovim, keyboard, mouse, ghostty]
sources:
  - id: report
    resource: /references/purrtty-internals-report-2026-10-16.md
    title: purrTTY internals research report (2026-10-16)
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:45:00Z }
status: stable
---

# Feature matrix

| Feature | Status |
|---|---|
| True color / 256 / 16 color | yes (`COLORTERM=truecolor`) |
| Alternate screen (1049) | yes |
| Scroll regions, insert/delete line | yes (engine, conformance-tested) |
| Bracketed paste (2004) | yes |
| Mouse 1000/1002/1003 + SGR 1006 | yes; Shift bypasses tracking for selection |
| Kitty keyboard protocol | yes (engine key encoder) |
| Synchronized output (DEC 2026) | yes, 1 s safety timeout |
| DECSCUSR cursor shapes | yes |
| Kitty graphics | yes (raw/zlib/PNG/JPEG) |
| Focus events (1004) | **no** (never sent) |
| OSC 52 clipboard | set: yes; read: **no** |
| OSC 8 hyperlinks | parsed, **not clickable** |
| Sixel | **no** |
| XTVERSION | not answered |
| DA1/DA2 | VT220 level 62 with ANSI color + clipboard features |
| Braille / block elements | drawn as geometry (fonts lack braille) |

Verdict from the research: **nvim will run fine** on the VT side.[^report]

# Key capture and game interaction

- While any terminal is focused, purrTTY's Harmony prefix on `Program.OnKey` swallows every key press from the game; `GameSettings.OnKeyAll` hotkeys are blocked while ImGui wants text input.
- Named keys (Enter, Backspace, Tab, Esc, arrows, Home/End, Delete/Insert, PgUp/PgDn, F1–F12) always go through the engine encoder; letters/digits only when Ctrl or Alt is held; plain text comes from the ImGui character queue.
- **Not mapped:** F13+, Super/Cmd, `Ctrl+-`, `Ctrl+/`, `Ctrl+;` and other Ctrl+punctuation.
- **Reserved:** Ctrl+Shift+C / Ctrl+Shift+V (copy / paste) never reach the app.
- **The purrTTY toggle hotkey (default F12) is consumed before encoding**, so pressing F12 inside nvim hides the terminal. The shipped nvim config must not bind F12, or the user must rebind the toggle.
- Esc is emitted immediately with no ambiguity timer; nvim's `ttimeoutlen` handles Alt sequences as with xterm.[^report]

# Known full-screen-TUI issues

- Wheel scrolling outside mouse-tracking mode is not translated to arrow keys (open): `less`/nvim without `mouse=a` get nothing from the wheel. Ship nvim with `vim.o.mouse = 'a'`.
- Hidden-terminal inbox growth, mouse cell drift, Shift-override selection, Ctrl/Alt+letter under kitty keyboard, pump tearing: all fixed.[^report]

[^report]: purrTTY internals research report (2026-10-16)
