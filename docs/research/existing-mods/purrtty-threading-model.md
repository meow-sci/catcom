---
type: Component Analysis
title: purrTTY threading model
description: Which thread each purrTTY stage runs on (PTY pumps, engine feed, render, input, custom-shell callbacks) and the resulting rules for a mod that bridges scripts to terminals.
tags: [purrtty, threading, game-thread]
sources:
  - id: report
    resource: /references/purrtty-internals-report-2026-10-16.md
    title: purrTTY internals research report (2026-10-16)
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:45:00Z }
status: stable
---

| Stage | Thread |
|---|---|
| PTY read | dedicated long-running thread per session |
| PTY bytes → engine inbox (`ITerminalSurface.Write`) | pump thread; the only thread-safe surface member (24 MiB bounded inbox) |
| Engine VT parse, render-state, frame build | **main/render thread only**, inside `BuildFrame()` from `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`) |
| User input encode → `IProcessManager.Write` | tick thread; never blocks (writer-thread queue) |
| Custom shell `WriteInputAsync` | **synchronously on the tick thread** |
| Custom shell `OutputReceived` / `Terminated` | any thread |
| Session create | `Task.Run`; `SessionConfigurator` pre-publication; `SessionCreated` post-publication (must not touch the surface) |
| Session close | must be on the tick thread; process teardown backgrounded |
| Registry / windows / dialogs | main thread only, lock-free |

Rules for ksa-scripting: a REPL shell's command handler is already on the game thread (no marshalling, but must be fast); anything that reacts to `Terminated`/`Exited` (e.g. "nvim exited → reload script") must post to the mod's own game-thread queue; never call purrTTY window APIs off-thread.[^report]

[^report]: purrTTY internals research report (2026-10-16)
