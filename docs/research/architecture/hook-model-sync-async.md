---
type: Design
title: Hook model - ad-hoc scripts, sync and async event hooks, tick hooks, timers
description: Precise semantics for every way a script can run in catcom - what thread and slot it runs in, what it may read and write, how sync hooks differ from async hooks, how awaits are resolved on the sim clock, ordering and budgets - plus the event catalog and example scripts in TypeScript and Lua.
tags: [design, hooks, events, async, coroutines, scheduler]
sources:
  - id: arch
    resource: /architecture/in-band-scripting-architecture.md
    title: In-band scripting architecture
  - id: hooks
    resource: /ksa-host/event-hook-points.md
    title: KSA event hook points
  - id: luaspike
    resource: /spikes/luajit-pinvoke-spike.md
    title: LuaJIT spike (coroutine await, interrupt rules)
  - id: v8spike
    resource: /spikes/v8-typescript-spike.md
    title: V8 spike (Promise/Task interop, interrupt)
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: draft
---

# Ways a script can run

| Kind | Registered by | Runs in | May write | Example |
|---|---|---|---|---|
| **Ad-hoc** | `script run file` (console/REPL/menu/nvim `:KsaRun`) | the next Step slot; top-level code may be `async`/coroutine | yes (Frame inline, Solver queued) | "print all vessels and refuel the active one" |
| **Sync event hook** | `ksa.on('engine.ignite', fn, { sync: true })` | **inline inside the Harmony patch** on the main thread, at the moment of the call | enqueue `InputEvents`; Frame actions; no awaits | "veto/log ignition with exact arguments" |
| **Async event hook** | `ksa.on('engine.ignite', fn)` (default) | the next Step slot after the event; handler may await | yes | "when any engine ignites, wait 2 s and start a camera track" |
| **Tick hook** | `ksa.every('step' \| 'frame' \| seconds, fn)` | Step slot each frame / each landed sim step / each timer expiry on the chosen clock (`ut`, `wall`, `render`) | yes | "every step, hold throttle so TWR = 1.5" |
| **Service** | `ksa.service(async fn)` / coroutine | started at load, suspended at awaits, resumed in Step slots | yes | "a landing autopilot that loops until touchdown" |
| **Command** | `ksa.command('name', fn)` | console `TerminalInterface` call (main thread) or REPL | yes | user-defined console verbs |

All script code runs on the **game thread**; there is no multithreaded script execution. Runtimes are single-threaded by construction (one `lua_State`, one V8 isolate entered only from the game thread, one Jint engine).

# Sync versus async hooks

**Sync** handlers run inside the patched game method (for example `EngineController.SetIsActive` postfix) with the exact arguments; they give the strongest guarantee (same call, before anything else observes the change) at the cost of strict rules: no awaits, bounded time (watchdog, default 5 ms for sync hooks), no direct sim-state mutation (the solver may be running when a UI-path patch fires), only `InputEvents`-backed actions and logging. A sync hook that violates the budget is interrupted and demoted to async with a logged error.

**Async** handlers receive an immutable event record (`ut`, `type`, `vesselId`, payload captured at the call) and run in the next Step slot, where the solver is idle; they may await and may issue any action. Ordering is FIFO per script and events are never dropped (bounded queue with back-pressure logging).

# Awaits on the sim clock

| Await | Resolved when |
|---|---|
| `sleep(seconds)` | sim time (`Universe.GetElapsedSeconds()`) advanced by `seconds`; a `wall`/`render` variant uses Stopwatch / dtPlayer |
| `nextStep()` | the next landed sim step (paused frames do not count) |
| `nextFrame()` | the next frame |
| `until(() => cond, { timeout })` | predicate true at a Step slot |
| `waitEvent('docked', { vessel })` | matching event delivered |
| `action(...)` results | immediately for Frame actions; after the Solver slot ran for Solver actions (`Promise<CommandResult>`) |

Runtime mapping: JavaScript `async/await` with host-created Promises (ClearScript `EnableTaskPromiseConversion`; Jint `TaskInterop`), advanced by draining the engine's job queue in the Step slot. Lua uses coroutines: `ksa.sleep(2)` is `coroutine.yield(ksa._wait(2))`, the scheduler holds the thread handle and `lua_resume`s it when due; host functions never raise errors across the managed boundary (see the LuaJIT spike).[^luaspike][^v8spike]

# Event catalog (initial)

| Event | Producer | Payload |
|---|---|---|
| `engine.activate` / `engine.deactivate` (intent) | postfix `EngineController.SetIsActive` | vessel, engine ordinal, by (manual/staging/script) |
| `engine.ignite` / `engine.shutdown` (physical) | differ on `RocketCoreState.ActivatedThisFrame/DeactivatedThisFrame` | vessel, engine ordinal |
| `rcs.activate` | postfix `ThrusterController.SetIsActive` | |
| `stage` | postfix `SequenceList.ActivateNextSequence` | vessel, sequence number |
| `decouple` | postfix `Decoupler.Decouple` | vessel, new vessel id |
| `split` | postfix `Vehicle.Split` | vessel, new vessel id |
| `dock` / `undock` | postfix `DockingPort.Dock` / `Undock` | both vessels, ports |
| `soi.change` | diff `Vehicle.Parent` after `UpdateFromTaskResultsSynchronized` | vessel, from, to |
| `vessel.spawn` / `vessel.remove` | `CelestialSystem.Register` / `Deregister` | vessel id, class |
| `vessel.launch` | postfix `Vehicle.MarkLaunched` + situation diff | vessel |
| `situation.change` (covers landed/splashdown/liftoff) | differ | vessel, from, to |
| `impact` | prefix `Celestial.TrySpawnGroundImpact` / `TrySpawnWaterSplash` | energy, velocity |
| `vessel.destroy` | prefix `Universe.DestroyVehicleFromEvent` / `DestroyVehicle` | cause, peak g, peak q |
| `part.failure` | prefix `PartFailureEvent.Apply` | failed part ids |
| `control.change` | setter `Program.ControlledVehicle` (diffed) | from, to |
| `editor.enter` / `editor.exit`, `launchmenu.*` | `VehicleEditor`/`VehicleLaunchMenu` ctor/Dispose | |
| `save.write` / `save.load` / `system.load` | `UncompressedSave.Write`, `Universe.DeserializeSave`, `Program.OnGameLoaded`, `Universe.LoadSystem` | path/name |
| `warp.change` | prefix `Universe.SetSimulationSpeed` (diffed) | from, to |
| `burn.start` / `burn.complete` / `burn.fuel_out` | diff `FlightComputer.Burn?.StartedAt`; prefix `FlightComputer.RaisePendingAlerts` | vessel |
| `flameout`, `battery.depleted`, `battery.charged`, `animation.complete`, `docked`, `undocked`, `decoupled` (gatOS names) | differ (parity with gatOS `/sim/events`) | |

The gatOS event names are kept as aliases so `/sim/events` consumers and scripts share vocabulary.[^hooks]

# Examples

TypeScript:

```ts
import { ksa } from "ksa";

ksa.on("engine.ignite", async (ev) => {
  const v = ksa.vessel(ev.vesselId)!;
  ksa.console.log(`ignition on ${v.name} at ${v.altitude.barometric.toFixed(0)} m`);
  await ksa.sleep(2);
  await v.camera.focus();
});

ksa.every("step", (step) => {
  const v = ksa.active; if (!v) return;
  const twr = v.navball.twr;
  if (twr > 1.6) v.throttle(v.throttleCmd - 0.02 * step.dt);
});
```

Lua:

```lua
local ksa = require("ksa")

ksa.on("engine.ignite", function(ev)
  local v = ksa.vessel(ev.vessel_id)
  ksa.console.log(("ignition on %s at %.0f m"):format(v.name, v.altitude.barometric))
  ksa.sleep(2)                -- yields the coroutine; resumed 2 s of sim time later
  v.camera.focus()
end)

ksa.every("step", function(step)
  local v = ksa.active; if not v then return end
  if v.navball.twr > 1.6 then v.throttle(v.throttle_cmd - 0.02 * step.dt) end
end)
```

# Budgets and failure handling

- Per-frame script budget (all scripts): 2 ms default, measured per slot and shown in the status window.
- Per-call watchdog: sync hooks 5 ms, async/tick/ad-hoc 50 ms; on expiry the runtime is interrupted (`V8ScriptEngine.Interrupt()`, Jint `CancellationToken`/`TimeoutInterval`, Lua-side count hook polling a host flag), the script is disabled, the error is shown in console, REPL and nvim diagnostics.
- A script error never propagates into the game: every entry into a runtime is wrapped, and the Harmony patch bodies are try/catch.
- Hooks are removed when the script is unloaded or reloaded; a script may also `unsubscribe()`.

[^arch]: In-band scripting architecture
[^hooks]: KSA event hook points
[^luaspike]: LuaJIT spike (coroutine await, interrupt rules)
[^v8spike]: V8 spike (Promise/Task interop, interrupt)
