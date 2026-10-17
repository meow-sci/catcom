---
type: Decision Record
title: Recommendation - which runtimes to build and in what order
description: The recommended path for catcom - a shared in-band host first, then LuaJIT (direct P/Invoke) and V8 (ClearScript with in-isolate TypeScript transpilation) as the two primary runtimes (Jint dropped by decision, libnode rejected for now), plus the 2026-10-16 scope additions (ImGui Script Manager, UI-created bindings, terminal scripts on purrTTY surfaces, nvim over TCP) - with the evidence behind each choice and the open risks to retire first.
tags: [decision, recommendation, roadmap]
sources:
  - id: matrix
    resource: /runtimes/runtime-comparison-matrix.md
    title: Runtime comparison matrix
  - id: arch
    resource: /architecture/in-band-scripting-architecture.md
    title: In-band scripting architecture
  - id: launcher
    resource: /existing-mods/purrtty-terminal-launcher-gap.md
    title: purrTTY launcher gap
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T22:00:00Z }
status: stable
---

# Decision

1. **Build the shared host first** ([plan](../../../plans/00-shared-foundation-plan.md)): lifecycle, the `ApplyVehicleSolvers`/`ExecuteNextVehicleSolvers` slots, the game API facade ported from gatOS's readers/actuators, the hook registry and scheduler, the script catalog, the stub generator, the REPL shells, and the nvim bridge. Every runtime is an adapter behind `IScriptRuntime`; none of the hard problems are runtime-specific.
2. **LuaJIT via direct P/Invoke** is the Lua runtime ([plan](../../../plans/01-luajit-runtime-plan.md)). Evidence: fastest option by one to two orders of magnitude, tiny native library, MIT, spike-verified host calls / coroutine await / sandbox / traceback. Non-negotiables: self-built binaries per RID (GC64, no FFI, CHECKHOOK, static CRT on Windows), a C shim for raising errors and the timeout hook, and host functions that never raise.
3. **V8 via ClearScript with `typescript@6` transpiled in-isolate** is the TypeScript runtime ([plan](../../../plans/02-v8-clearscript-typescript-runtime-plan.md)). Evidence: 7.5.1.1 maintained, MIT, sandbox-by-default, hard heap/interrupt limits, custom `DocumentLoader` makes `import "./x.ts"` transparent (4 ms per graph), 0.8 ms per transpile, inspector debugging. Non-negotiables: one ClearScript copy per process, soft heap limit set, TS pinned to 6.x (TS 7 has no JS API).
4. **Jint is not pursued** (decision 2026-10-16): the [research](/runtimes/jint.md) and spike stay in the bundle as reference, but no fallback runtime is built; V8 is the only JavaScript engine. Script-level CI tests run on V8 natives (ClearScript ships linux-x64 and win-x64).
5. **node-api-dotnet / libnode is rejected for now**: Node 20 only, embedding API unmerged and experimental, dedicated Node thread, full fs/net. Re-evaluate if Microsoft publishes a LibNode ≥ 22.18.
6. **Neovim 0.12.6 is the editor**, launched through a small purrTTY contract extension; LuaLS and TypeScript 7's native `tsc --lsp` give completion without Node; save detection via nvim RPC with a file-watcher fallback.

# Scope additions (2026-10-16)

The plans also cover: an ImGui **Script Manager** window as the primary user interface ([design](/architecture/script-manager-ui.md)); **UI-created bindings** of exported functions to events/ticks with a context object ([design](/architecture/script-exports-and-bindings.md)); **terminal scripts** that own a virtual terminal rendered by purrTTY in a 2D window or on an in-world part quad, emitting and consuming escape codes without any OS shell ([design](/architecture/terminal-scripts-and-virtual-terminals.md)); and **nvim over loopback TCP** launched directly as the PTY child with a stubbed shell ([recipe](/editor/neovim-tcp-launch.md)).

# Why not "V8 runs TypeScript natively"

No engine does. Node strips types with amaro before V8; the TC39 proposal is Stage 1. The mod transpiling in-process gives the same user experience (edit `.ts`, save, it runs) with full-language support that Node's strip-only mode lacks.

# Risks to retire first (in order)

| Risk | Retire by |
|---|---|
| Writing sim state from the wrong slot races the solver | implement the two slots and a stress test (teleport + FC setpoint under warp) before any runtime work |
| purrTTY cannot launch nvim from another mod | land the `ITerminalLauncher` contract extension in purrTTY early; keep a layout-based fallback |
| LuaJIT JIT-on stability inside the real game process on each platform | build per RID, run the spike's loop inside KSA on Windows and Linux; keep `jit.off()` switch and log `jit.status()` |
| Two mods loading ClearScript | document the single-copy rule; expose via `ExportedAssemblies` if ever needed |
| KSA churn on hook targets | compile-check project with one stub per hook; `[KsaAnchor]` attributes; probe tool in the upgrade playbook |
| Editor size (≈85–90 MB unpacked per platform) | ship editor as an optional sub-package / lazy download like gatOS's guest image |

[^matrix]: Runtime comparison matrix
[^arch]: In-band scripting architecture
[^launcher]: purrTTY launcher gap
