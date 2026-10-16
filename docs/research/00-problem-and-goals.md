---
type: Research Brief
title: Problem statement and goals for in-band KSA scripting
description: What ksa-scripting must deliver - in-process, in-band scripting of Kitten Space Agency in LuaJIT and TypeScript with a gatOS-like read/write model, event hooks that run inside the game loop, and a shipped Neovim editing experience inside purrTTY - and the constraints inherited from the game, the loader, and the sibling mods.
tags: [brief, goals, constraints, ksa-scripting]
sources:
  - id: brief
    resource: ../../REFERENCES.md
    title: Task brief (REFERENCES.md)
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: stable
---

# The problem

gatOS exposes live KSA state and control to a Linux guest as a filesystem (`/sim`) plus HTTP, MQTT and MCP. It is deliberately **out of band**: a separate VM, a serialization boundary, and a 2-second command timeout. That is the right shape for shells and external programs, but it cannot participate in the game loop: a script cannot run *inside* the frame when an engine ignites, cannot mutate state in the solver-safe window, and pays a transport hop per read.[^brief]

ksa-scripting is the **in-band** counterpart: scripts run inside the game process, on the game thread, with the same data model and action vocabulary as gatOS, and with hooks that fire from the game's own code paths.

# Goals

1. **Two user-facing languages:** Lua on LuaJIT, and TypeScript on a JavaScript engine, with **no user-visible build step** (the mod transpiles TypeScript transparently).
2. **gatOS-shaped API:** reads return the same quantities, units and frames as `/sim`; writes use the same action keys and phase rules (Frame / Solver), so knowledge, docs and tutorials transfer.
3. **Hooks:** ad-hoc scripts run on demand; event hooks ("run this whenever any engine ignites") run synchronously inside the game call or asynchronously in the script scheduler; tick hooks run per frame / per sim step / on timers.
4. **Editing experience:** a shipped, platform-native Neovim launched into a purrTTY terminal straight at the script file, with LSP completion from generated type stubs; saving re-registers the script.
5. **Knowledge retention:** this bundle and the plans must let a future session implement any of the runtimes without re-deriving the research.

# Constraints discovered

- KSA is **.NET 10 self-contained**; mods are C# 13 class libraries loaded by StarMap into per-mod `AssemblyLoadContext`s with no `LoadUnmanagedDll` override, so native runtimes need an explicit resolver ([StarMap loader](/ksa-host/starmap-loader-and-assembly-load-context.md), [purrTTY native loading pattern](/existing-mods/purrtty-native-library-loading.md)).
- The **vehicle simulation runs concurrently with StarMap hooks**; only two main-thread windows are sim-safe for writes ([game loop and threading](/ksa-host/game-loop-and-threading.md)).
- KSA has **no gameplay events and no scripting**; everything is Harmony or polling ([event hook points](/ksa-host/event-hook-points.md)).
- **No engine runs TypeScript natively**; Node's "native TS" is a transpile step, and the only embeddable libnode is Node 20 ([TypeScript execution options](/runtimes/typescript-execution-options.md)).
- LuaJIT errors **cannot unwind through managed frames** ([LuaJIT spike](/spikes/luajit-pinvoke-spike.md)).
- purrTTY cannot yet open a terminal from another mod; a small contract extension is required ([purrTTY launcher gap](/existing-mods/purrtty-terminal-launcher-gap.md)).

# Reference material

| Resource | Path |
|---|---|
| Decompiled KSA 2026.10.10.5554 + reference DLLs | `/Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/{decomp,dll}` |
| Full Linux game install (Content/, shaders, natives) | `/Users/asherwin/repos/meow-sci/ksa-linux` |
| gatOS (the out-of-band sibling, 179 KSA anchors) | `/Users/asherwin/repos/meow-sci/gatOS` |
| purrTTY (terminal emulator mod) | `/Users/asherwin/repos/meow-sci/purrtty` |
| StarMap loader source | `/Users/asherwin/repos/meow-sci/gatOS/thirdparty/starmap` |
| KSA modding skill | `/Users/asherwin/repos/meow-sci/gatOS/.claude/skills/ksa` |
| Spikes in this repo | `/spikes` |

[^brief]: Task brief (REFERENCES.md)
