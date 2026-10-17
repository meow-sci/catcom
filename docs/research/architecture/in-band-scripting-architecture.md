---
type: Architecture
title: In-band scripting architecture for KSA (runtime-agnostic host)
description: The proposed layered design of the catcom mod - StarMap lifecycle, the two Harmony-anchored execution slots, the game API facade with gatOS's read/write firewall, the hook registry and script scheduler, the runtime abstraction that LuaJIT, V8 and Jint plug into, the script catalog and hot reload, the editor bridge, and packaging - with the reasoning behind each choice.
tags: [architecture, design, host, scheduler, facade, runtime-abstraction]
sources:
  - id: gameloop
    resource: /ksa-host/game-loop-and-threading.md
    title: KSA frame loop and threading
  - id: gatos-threading
    resource: /existing-mods/gatos-architecture-and-threading.md
    title: gatOS architecture and threading
  - id: hooks
    resource: /ksa-host/event-hook-points.md
    title: KSA event hook points
  - id: launcher
    resource: /existing-mods/purrtty-terminal-launcher-gap.md
    title: purrTTY launcher gap
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: draft
---

# Layering

```
Catcom.GameMod      StarMap entry: lifecycle, Harmony slots, menus, REPL shells, editor launcher   (refs KSA, purrTTY contract)
Catcom.Ksa          the ONLY assembly besides GameMod that references KSA: readers, actuators,
                          event patches, phase executor, [KsaAnchor]s, KsaHealth                          (ported from gatOS Game/Ksa)
Catcom.Core         game-free: DTO records, action vocabulary, hook registry, scheduler, script
                          catalog, manifest parsing, sidecar persistence, stub generators, nvim RPC client
Catcom.Runtime.Lua  LuaJIT binding + Lua adapter        }  implement IScriptRuntime; no KSA types
Catcom.Runtime.V8   ClearScript binding + TS loader     }  (only Core DTOs cross the boundary)
Catcom.Runtime.Jint Jint adapter (fallback)             }
```

The gatOS rule "only the GameMod references KSA; two immutable records are the firewall" is kept: scripts never see live `Vehicle`/`Part` objects, only DTOs and handles. This keeps every runtime adapter game-free and testable, and keeps KSA churn confined to `Catcom.Ksa`.[^gatos-threading]

# Execution slots (Harmony-anchored)

| Slot | Anchor | Runs |
|---|---|---|
| **Step** (primary script slot) | postfix `Universe.ApplyVehicleSolvers()` | solver idle and results just applied; before `InputEvents.ApplyInputEvents`. Runs: event differ, Harmony-captured event delivery, tick hooks (`frame`, `step`, timers), coroutine/promise advancement, Frame-phase actuator dispatch, `InputEvents` enqueue (applied this same frame) |
| **Solver** | `Priority.First` prefix `Universe.ExecuteNextVehicleSolvers(double, SimStep)` | Solver-phase actions queued during the Step slot (attitude/burn/RCS-mode setpoints, teleport, impulse, refills) — exactly gatOS's lane, because `FlightComputer.CopyFrom` would otherwise clobber them |
| **Post-solver** | `[StarMapAfterGui]` after `JobSystems.VehicleSolver.Wait()` | per-frame drivers that re-pose vehicles (welds-style), if ever exposed |
| **UI** | `[StarMapBeforeGui]` / `[StarMapAfterGui]` | ImGui status window, REPL rendering (purrTTY), editor dialogs; F2-gated |
| **Frame fallback** | `[StarMapAfterOnFrame]` | latch-based re-run of the Step slot work if a frame produced no `ApplyVehicleSolvers` (paused/loading), so timers on the `wall`/`render` clocks still advance |

Why the `ApplyVehicleSolvers` postfix rather than StarMap's `BeforeGui`: `BeforeGui` runs **while the solver is running**, so any script write races worker reads; the postfix is inside the idle window, sees fresh state, and `InputEvents` it enqueues land the same frame.[^gameloop]

Every slot body is try/catch, budgeted, and self-disabling on repeated faults; a throw on the sim path crashes the game.

# Game API facade (`Catcom.Ksa`)

- **Reads:** readers ported from gatOS (`VesselReader`, `PartsReader`, `BodyReader`, `CameraReader`) produce the same DTOs (`VesselSnapshot`, `BodySnapshot`, …) with the same units and frames. Two access styles: a per-step **snapshot** (zero-cost reads for many hooks; consistent) and **live handles** (`VesselRef`) whose property reads call the reader on demand (cheap for a few fields). Both are valid only on the game thread, which is where all script code runs.
- **Writes:** one `ActionExecutor` with the gatOS action vocabulary (`vessel.ignite`, `engine.active`, `vessel.attitude_target`, `debug.teleport`, …), validation, authority gate, health latch, and `PhaseFor(action)`; Frame actions execute inline in the Step slot, Solver actions are queued to the Solver slot. Off-thread callers (REPL output threads, nvim RPC, file watcher) go through a `CommandQueue` identical to gatOS's.
- **Events:** two producers feeding one stream — Harmony patches on the targets in [event hook points](/ksa-host/event-hook-points.md) (capture args, stamp `ut`, enqueue) and an `EventDiffer` over consecutive snapshots (situation, SOI, propellant, battery, animation). Delivered in the Step slot.[^hooks]

# Hook registry and scheduler (`Catcom.Core`)

- Hook kinds: `event` (named, optional vessel filter, `sync` or `async`), `tick` (`frame`, `step`, `every <seconds ut|wall>`), `command` (console/REPL verb), `service` (long-running coroutine/async function started at load).
- Scheduler: per-frame budget (default 2 ms, configurable), per-call watchdog (default 50 ms → runtime interrupt → script disabled with an error), FIFO per script, fairness across scripts. Async waits (`sleep(ut)`, `nextStep()`, `until(pred)`, `waitEvent(name)`) are resolved by the scheduler in the Step slot on the sim clock; wall/render clocks are also available (gatOS schedule semantics).
- See [hook model](/architecture/hook-model-sync-async.md).

# Runtime abstraction

```csharp
interface IScriptRuntime : IDisposable
{
    string Id { get; }                                     // "lua", "v8", "jint"
    ScriptModule Load(ScriptSource src);                   // compile/transpile; throws ScriptLoadException with line/col
    ScriptValue Invoke(ScriptCallable fn, ReadOnlySpan<ScriptValue> args);   // sync call, game thread
    bool Advance(TimeSpan budget);                         // drain promise/coroutine continuations; true if work remains
    void Interrupt();                                      // from the watchdog thread
    RuntimeStats Stats { get; }                            // heap, last call time
}
```

`ScriptValue` is a small tagged union over primitives, strings, arrays, maps and host handles; DTOs are marshalled into native tables/objects once per delivery. Host functions exposed to scripts are generated from one C# facade description so that `host.d.ts` (TypeScript) and `ksa.lua` (LuaCATS) stubs come from the same source of truth.

# Script catalog and hot reload

- Scripts live under `<Documents>/My Games/Kitten Space Agency/mods/catcom/scripts/` (user-writable; shared with nvim), with `lib/` for shared modules and `stubs/` for generated type stubs. A script's kind and hooks are declared in code (`ksa.on(...)`), not in a manifest; a header comment may carry metadata (name, autostart).
- Registration: on mod start (autostart list), on nvim `BufWritePost` RPC, on the debounced file watcher, or from the console (`script run <file>`). Reload = compile new version → swap hooks atomically → dispose old scope → optional `onReload(state)`.
- Persistence: per-script `state` table/object serialized to a sidecar next to the save (`UncompressedSave.Write` postfix) and restored on load.

# Editor bridge

Launch the shipped nvim via the purrTTY launcher extension (`ITerminalLauncher`), with `NVIM_APPNAME`/XDG isolation and `--listen`; connect the msgpack-RPC client; reload on save; push diagnostics back. Without the purrTTY extension the mod prints the file path and a layout recipe instead.[^launcher]

# REPL

One `BaseLineBufferedShell` per runtime registered in `CustomShellRegistry` ("Lua REPL", "TypeScript REPL"): the command handler already runs on the game thread, evaluates one line in the script's global scope within the watchdog budget, prints results, and mirrors output to the KSA console.

# Packaging

Deny-list DLL glob (gatOS style), `deps.json`, natives flat or under `runtimes/<rid>/native/`, editor blobs outside the wipe step, `mod.toml` with optional purrTTY dependency (`ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]`). Runtime assemblies are optional: a missing native (e.g. no LuaJIT build for a platform) degrades that runtime to "unavailable" with a console warning.

[^gameloop]: KSA frame loop and threading
[^gatos-threading]: gatOS architecture and threading
[^hooks]: KSA event hook points
[^launcher]: purrTTY launcher gap
