---
type: Design
title: Script Manager - the ImGui window users drive everything from
description: Design of the in-game ImGui window where a player creates, edits (in nvim inside purrTTY), runs, attaches and monitors scripts - panes, actions, pickers (events, exported functions, vehicle parts), persisted bindings, terminal placement, hotkey suppression, and menu integration.
tags: [design, imgui, ui, script-manager, bindings]
sources:
  - id: ksa-skill
    resource: /Users/asherwin/repos/meow-sci/gatOS/.claude/skills/ksa/SKILL.md
    title: ksa skill (ImGui input chain and focus traps, menu injection)
  - id: purrtty
    resource: /existing-mods/purrtty-custom-shell-contract.md
    title: purrTTY custom shell contract
  - id: hooks
    resource: /architecture/hook-model-sync-async.md
    title: Hook model
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T23:30:00Z }
status: draft
---

# Purpose

The window is the single entry point for players: no console commands or config files are required to author a script, attach it to a game event, run it once, keep it running, or put its terminal on a part. Console commands and the REPL remain as power-user shortcuts.

# Layout

```
┌ Scripting ───────────────────────────────────────────────────────────────────┐
│ [New ▾] [Edit] [Run] [Start/Stop] [Reload] [Open folder]      filter: [____] │
│ ┌ Scripts ──────────────┐ ┌ Selected: door-panel.ts ───────────────────────┐ │
│ │ ● door-panel.ts  TERM │ │ Overview | Bindings | Terminal | Output | Code │ │
│ │ ○ ascent.lua     IDLE │ │                                                │ │
│ │ ◐ telemetry.ts   SVC  │ │  (tab content)                                 │ │
│ │ ✖ broken.lua     ERR  │ │                                                │ │
│ └───────────────────────┘ └────────────────────────────────────────────────┘ │
│ Events | Runtimes | Settings                                                  │
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Scripts pane:** every file under `scripts/` (recursively, excluding `lib/` and `stubs/`), language icon, state badge (idle, running ad-hoc, service, terminal attached, error, disabled), filter box.
- **Toolbar:** `New` opens a dialog (name, language Lua / TypeScript, template: *ad-hoc*, *event handler*, *service loop*, *terminal app*) and writes the file from a template, then opens the editor. `Edit` launches nvim on the file inside a purrTTY window (see [neovim TCP launch](/editor/neovim-tcp-launch.md)). `Run` executes an ad-hoc script now. `Start/Stop` toggles a service (forever) script. `Reload` recompiles. `Open folder` shows the path (no OS shell).
- **Overview tab:** path, language, runtime, exports discovered (function names with signatures from the stub generator when known), declared hooks (`ksa.on(...)` found at load), last load error (file:line, with "Jump in editor"), resource stats.
- **Bindings tab:** table of UI-created bindings for this script: `event/tick → exported function`, options (sync, vessel filter, clock/interval), enabled toggle, remove. `Add binding…` opens a picker: an **event catalog** list (name, description, payload fields; from the hook model) or tick kind, a **function picker** (the script's exports), and options. Bindings persist in `<data>/bindings.toml` and are applied every time the script loads. Code-declared hooks appear greyed as "declared in code".
- **Terminal tab (terminal scripts):** `Open 2D window` (cols, rows, theme, title), `Place on part…` (vehicle picker defaulting to the controlled vessel, part tree picker by `DisplayName`/`InstanceId`, offset/rotation/size, cols×rows, theme), list of attached terminals with focus/close, and a live mini-preview of the script's output buffer. Uses the purrTTY launcher contract; disabled with a hint when purrTTY is absent.
- **Output tab:** per-script log (print/console output, errors, hook timings), copy button, clear.
- **Code tab:** read-only view of the source (ImGui multiline, monospace) for quick inspection without the editor.
- **Events (global):** browsable event catalog with live counters ("engine.activate fired 12× this session"), useful to find the right event name.
- **Runtimes (global):** per-runtime health (V8 heap, Lua memory, JIT status), per-frame script budget usage, watchdog hits, disable/enable a runtime.
- **Settings (global):** budgets, autostart list, editor options (port range, theme), `control_all_vessels`, debug namespace, purrTTY integration status, version banner (KSA build vs verified build).

# Behaviours

- **Hotkeys:** while the window is focused and an `InputText` is active, block game hotkeys with the mod-scoped flag + `GameSettings.OnKeyAll` prefix pattern (never `WantTextInput` alone).[^ksa-skill]
- **Menu:** add a "Scripting" top-level entry via ModMenu.Attributes when present, else the `Program.DrawProgramMenusHook` postfix fallback (gatOS/purrTTY pattern); hotkey toggle configurable (not F12, which purrTTY uses).
- **Threading:** the window runs in `[StarMapAfterGui]`; every action it triggers is posted to the Step slot (no direct runtime calls from the UI phase, because the solver may be running).
- **Destructive actions** (delete script, stop a service with an attached terminal) confirm with a modal.
- **Discoverability:** empty state shows a three-step hint (New → Edit → Run) and links to the stub files for API docs.

# Templates (written by `New`)

- *ad-hoc* — prints the active vessel's telemetry and exits.
- *event handler* — exports `onEvent(ctx)` and a commented list of event names.
- *service loop* — `main(ctx)` with a `while (!ctx.signal.cancelled) { …; await ksa.sleep(1) }` loop (Lua: `while not ctx.signal.cancelled do … ksa.sleep(1) end`).
- *terminal app* — a minimal TUI: alt screen, mouse on, draws a button, toggles a light on click, redraws on resize, exits on `q`.

[^ksa-skill]: ksa skill (ImGui input chain and focus traps, menu injection)
[^purrtty]: purrTTY custom shell contract
[^hooks]: Hook model
