---
okf_version: "0.2"
---

# catcom research bundle

Research distilled on 2026-10-16 for building **in-band scripting of Kitten Space Agency** (LuaJIT and TypeScript) with a gatOS-like read/write model, in-game-loop event hooks, and a shipped Neovim editor inside purrTTY. Start with the brief, then the host facts, then the runtime evaluations, then the architecture and recommendation. Implementation plans live in [/plans](../../plans/README.md).

## Start here

* [Problem statement and goals](00-problem-and-goals.md) - What catcom must deliver and the constraints discovered.
* [Recommendation](decisions/recommendation.md) - Which runtimes to build, in what order, and the risks to retire first.
* [Runtime comparison matrix](runtimes/runtime-comparison-matrix.md) - Every runtime side by side with measured numbers.

## KSA host (the game and its loader)

* [StarMap loader and AssemblyLoadContext](ksa-host/starmap-loader-and-assembly-load-context.md) - Mod discovery, managed resolution order, lifecycle attributes, no native resolution.
* [Runtime and packaging](ksa-host/runtime-and-packaging.md) - .NET 10 self-contained, mod folder layout, deploy conventions.
* [Game loop and threading](ksa-host/game-loop-and-threading.md) - Frame order, concurrent solver, InputEvents, the only safe write windows.
* [Event hook points](ksa-host/event-hook-points.md) - Harmony target per gameplay event, with signatures.
* [Console and commands](ksa-host/console-and-commands.md) - TerminalInterface command registration, execution, output pipeline.
* [State read/write surface](ksa-host/state-read-write-surface.md) - Exact KSA members, access gotchas, XML saves, unload safety.

## Existing mods (gatOS and purrTTY)

* [gatOS architecture and threading](existing-mods/gatos-architecture-and-threading.md) - Frame / Solver / post-solver lanes, CommandQueue, F2-proof drivers.
* [gatOS snapshot and reader model](existing-mods/gatos-snapshot-and-reader-model.md) - Sampler cadence, readers, DTOs, units and frames.
* [gatOS actuator catalog](existing-mods/gatos-actuator-catalog.md) - Every write with its KSA members and persistence class.
* [gatOS /sim API surface](existing-mods/gatos-sim-api-surface.md) - Condensed catalog, action vocabulary, events, transports.
* [gatOS Harmony, reflection and compatibility](existing-mods/gatos-harmony-reflection-and-compat.md) - Patch list, reflection anchors, upgrade discipline, prior art.
* [purrTTY native library loading](existing-mods/purrtty-native-library-loading.md) - The resolver pattern to copy for LuaJIT/V8.
* [purrTTY PTY and process launch](existing-mods/purrtty-pty-and-process-launch.md) - ConPTY / posix_spawn, launch options, how to run nvim.
* [purrTTY custom shell contract](existing-mods/purrtty-custom-shell-contract.md) - ICustomShell ABI and cross-mod registration.
* [purrTTY terminal launcher gap](existing-mods/purrtty-terminal-launcher-gap.md) - Why nvim cannot be launched from another mod today and the fix.
* [purrTTY VT capabilities for TUIs](existing-mods/purrtty-vt-capabilities-for-tuis.md) - What nvim gets and does not get from libghostty-vt.
* [purrTTY threading model](existing-mods/purrtty-threading-model.md) - Which thread each stage runs on.
* [purrTTY game console shell](existing-mods/purrtty-game-console-shell.md) - ConsoleWindow.Print bridge pattern.
* [purrTTY virtual-pty bridge](existing-mods/purrtty-virtual-pty-bridge.md) - Custom shells as a mock pty: raw input, engine replies, resize, threading, lifecycle caveats, exact key/mouse encodings.
* [purrTTY in-world terminals and 2D windows](existing-mods/purrtty-in-world-terminals.md) - Record, main-thread creation API, custom shells on quads, focus/mouse mapping, texture sizing, fonts and canvas.

## Runtimes

* [LuaJIT](runtimes/luajit.md) - Status, API, hard rules, bindings verdict, patterns, measurements, platform notes.
* [Lua alternatives](runtimes/lua-alternatives.md) - Lua-CSharp, MoonSharp, NuLua, Lua.NET, KeraLua/NLua, Laylua.
* [V8 via ClearScript](runtimes/v8-clearscript.md) - Packaging, native loading, modules, interop, limits, debugging.
* [TypeScript execution options](runtimes/typescript-execution-options.md) - No engine runs TS; transpiler options with measured costs.
* [Jint](runtimes/jint.md) - Pure-C# fallback with measurements.
* [Node.js via libnode](runtimes/nodejs-libnode.md) - Why it is rejected for now.
* [Other JS engines](runtimes/other-js-engines.md) - QuickJS, PuerTS, Deno, Bun, Hermes and the rest.

## Editor

* [Neovim shipping](editor/neovim-shipping.md) - Artifacts, isolation, ConPTY, LSP servers without Node, init.lua, sizes.
* [Save detection and reload](editor/save-detection-and-reload.md) - nvim RPC first, watcher fallback, backupcopy=yes.
* [Editor alternatives](editor/editor-alternatives.md) - Helix, micro, Kakoune, vis, nano.
* [Neovim TCP launch](editor/neovim-tcp-launch.md) - Verified loopback-TCP RPC, OS-assigned ports, --clean + init.lua, stubbed shell so :! and :terminal cannot open an OS shell.

## Architecture

* [In-band scripting architecture](architecture/in-band-scripting-architecture.md) - Layers, execution slots, facade, scheduler, runtime abstraction, catalog, editor bridge.
* [Hook model: sync and async](architecture/hook-model-sync-async.md) - Semantics of every hook kind, awaits, event catalog, examples.
* [Script API surface](architecture/script-api-surface.md) - The script-facing API mapped to gatOS action keys and phases.
* [Sandboxing, limits and safety](architecture/sandboxing-limits-and-safety.md) - Threat model, host rules, per-runtime mechanisms.
* [Script Manager UI](architecture/script-manager-ui.md) - The ImGui window: panes, actions, pickers, templates, hotkey suppression.
* [Script exports and bindings](architecture/script-exports-and-bindings.md) - Exports, UI-created persisted bindings, context objects, the bridge from script calls to KSA.
* [Terminal scripts and virtual terminals](architecture/terminal-scripts-and-virtual-terminals.md) - Forever scripts that own a terminal rendered by purrTTY in a window or on a part quad; term API, lifecycle, tui helpers, door-panel example.

## Spikes (runnable evidence in /spikes)

* [V8 TypeScript spike](spikes/v8-typescript-spike.md) - typescript.js and sucrase inside V8, module loader, async, interrupt.
* [Jint TypeScript spike](spikes/jint-typescript-spike.md) - typescript.js and sucrase inside Jint.
* [LuaJIT P/Invoke spike](spikes/luajit-pinvoke-spike.md) - Binding shape, coroutine await, the unwinding panic and its fix.
* [KsaProbe tool](spikes/ksa-probe-tool.md) - Metadata probe of the shipped KSA.dll.
* [Hook compile check](spikes/hook-compile-check.md) - StarMap mod and Harmony patches compiled against KSA 5554.

## References (raw research reports mirrored into the bundle)

* [KSA hook-point report](references/ksa-hooks-report-2026-10-16.md) - Full decompiled-source survey behind the ksa-host concepts.
* [purrTTY internals report](references/purrtty-internals-report-2026-10-16.md) - Full purrTTY survey with verbatim signatures.
* [gatOS internals report](references/gatos-internals-report-2026-10-16.md) - Full gatOS survey including the 179-anchor census.
* [Lua/LuaJIT report](references/lua-runtimes-report-2026-10-16.md) - LuaJIT status, bindings, patterns, measurements, ~110 citations.
* [JS/TS runtimes report](references/js-runtimes-report-2026-10-16.md) - ClearScript, Jint, libnode, transpilers, ~110 citations.
* [Neovim shipping report](references/nvim-shipping-report-2026-10-16.md) - Neovim artifacts, isolation, RPC, ConPTY, LSP tooling.
* [purrTTY virtual-pty report](references/purrtty-virtual-pty-report-2026-10-16.md) - Mock-pty bridge, input encodings, in-world API, launcher contract design, ImGui helpers, fonts.
