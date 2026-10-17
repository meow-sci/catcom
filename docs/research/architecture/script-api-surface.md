---
type: API Design
title: Script API surface (mapping the gatOS /sim catalog to in-process objects)
description: The proposed script-facing API for both languages - namespaces, read DTOs, write actions with their gatOS action keys and phases, events, time, camera, console and persistence - defined once in C# so the TypeScript .d.ts and the LuaCATS stubs are generated from the same source.
tags: [api, design, typescript, lua, stubs, action-vocabulary]
sources:
  - id: sim
    resource: /existing-mods/gatos-sim-api-surface.md
    title: gatOS /sim API surface (condensed)
  - id: actuators
    resource: /existing-mods/gatos-actuator-catalog.md
    title: gatOS actuator catalog
  - id: readers
    resource: /existing-mods/gatos-snapshot-and-reader-model.md
    title: gatOS snapshot and reader model
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: draft
---

# Principles

1. **Same vocabulary as `/sim`:** every `/sim` path has an object/property counterpart with identical units (m, m/s, kg, deg in published angles, rad/s rates), frames (CCI/ECL/Body→CCI, assembly frame for parts) and `0`-for-unbound conventions.[^sim]
2. **Same action keys and phases:** every write maps to one gatOS action key; the host applies `PhaseFor(action)` so flight-computer setpoints, teleports and refills run in the Solver slot without the script knowing.[^actuators]
3. **One definition:** a C# facade description (`ApiSchema`) lists namespaces, members, parameter types, docs and the action key; generators emit `host.d.ts` and `ksa.lua` (LuaCATS `---@class`/`---@field`) plus the runtime bindings.
4. **No live KSA types** cross into scripts; handles are opaque ids resolved per call on the game thread.

# Namespaces (TypeScript shown; Lua mirrors with snake_case where idiomatic)

```ts
declare namespace ksa {
  // time (/time)
  const time: { ut: number; warp: number; simDt: number; paused: boolean; warpSpeeds: number[]; autoWarp?: number;
                setWarp(f: number): CommandResult;                      // debug.warp
                alarm(ut: number): Promise<void> };                     // time/alarm
  function sleep(seconds: number, clock?: "ut" | "wall" | "render"): Promise<void>;
  function nextStep(): Promise<StepInfo>;  function nextFrame(): Promise<void>;
  function until(pred: () => boolean, opts?: { timeoutSeconds?: number }): Promise<boolean>;

  // vessels (/vessels)
  function vessels(): VesselRef[];  function vessel(id: string): VesselRef | undefined;  const active: VesselRef | undefined;
  interface VesselRef {
    readonly id: string; readonly name: string; readonly situation: string; readonly parent: string; readonly controlled: boolean; readonly controllable: boolean; readonly isKitten: boolean;
    readonly position: { cci: Vec3; ecl: Vec3; lat: number; lon: number };
    readonly velocity: { orbital: number; surface: number; inertial: number; cci: Vec3 };
    readonly attitude: { quat: Quat; rates: Vec3 };            // Body→CCI xyzw; rad/s
    readonly altitude: { barometric: number; radar: number };
    readonly mass: { total: number; dry: number; propellant: number };
    readonly orbit: OrbitInfo | undefined;                     // apoapsis/periapsis are altitudes; 0 when unbound
    readonly navball: NavballInfo; readonly environment: EnvironmentInfo; readonly power: PowerInfo; readonly battery: BatteryInfo;
    readonly engines: EngineRef[]; readonly tanks: TankInfo[]; readonly rcs: RcsRef[]; readonly solar: SolarRef[]; readonly generators: GeneratorInfo[];
    readonly lights: LightRef[]; readonly docking: DockingRef[]; readonly decouplers: DecouplerRef[]; readonly animations: AnimationRef[];
    readonly srb: SrbInfo[]; readonly encounters: EncounterInfo[]; readonly parts: PartInfo[];
    readonly throttleCmd: number; readonly attitudeMode: string; readonly attitudeFrame: string; readonly rcsMode: string; readonly rcsOn: boolean; readonly engineOn: boolean;
    snapshot(): VesselSnapshot;                                 // the whole immutable record (== /sim telemetry)
    // writes (gatOS action key → phase)
    ignite(): CommandResult;                                    // vessel.ignite → Frame
    shutdown(): CommandResult;                                  // vessel.shutdown
    engine(on: boolean): CommandResult;                         // vessel.engine
    stage(): CommandResult;                                     // vessel.stage
    throttle(fraction: number): CommandResult;                  // vessel.throttle
    lights(on: boolean): CommandResult;  rcs(on: boolean): CommandResult;
    translate(x: number, y: number, z: number): CommandResult;  // vessel.translate (latched; KSA clears on focus loss etc.)
    rotate(x: number, y: number, z: number): CommandResult;     // vessel.rotate
    attitudeMode(mode: AttitudeMode): Promise<CommandResult>;   // vessel.attitude_mode → Solver
    attitudeFrame(frame: AttitudeFrame): Promise<CommandResult>;// vessel.attitude_frame → Solver
    attitudeTarget(q: Quat): Promise<CommandResult>;            // vessel.attitude_target → Solver
    rcsMode(mode: "Enabled" | "Disabled"): Promise<CommandResult>; // vessel.rcs_mode → Solver
    burn(ut: number, dvCci: Vec3): Promise<CommandResult>;      // vessel.burn → Solver
    scale(factor: number): CommandResult;  alwaysRender(on: boolean): CommandResult;
    focus(): CommandResult;                                     // camera.focus
    readonly debug: { teleport(posCci: Vec3, velCci: Vec3): Promise<CommandResult>; impulse(v: Vec3, frame?: "cci"|"body", unit?: "ns"|"dv"): Promise<CommandResult>;
                      refillFuel(): Promise<CommandResult>; refillBattery(): Promise<CommandResult>; control(): CommandResult };
  }
  interface EngineRef { readonly index: number; readonly active: boolean; readonly vacThrust: number; readonly isp: number; readonly throttle: number; readonly propellant: boolean; readonly minThrottle: number;
                        setActive(on: boolean): CommandResult; setMinThrottle(f: number): CommandResult }      // engine.active / engine.min_throttle
  interface LightRef   { on(b: boolean): CommandResult; brightness(v: number): CommandResult; color(r: number, g: number, b: number): CommandResult; outerAngle(deg: number): CommandResult; innerAngle(deg: number): CommandResult; goal(f: number): CommandResult }
  interface DockingRef { readonly docked: boolean; readonly dockedTo?: string; undock(): CommandResult }  // docking.undock
  interface DecouplerRef { readonly fired: boolean; readonly enabled: boolean; fire(): CommandResult }     // decoupler.fire
  interface AnimationRef { readonly current: number; readonly state: string; goal(f: number): CommandResult } // animation.goal

  // bodies (/bodies, /system)
  function bodies(): BodyInfo[];  function body(id: string): BodyInfo | undefined;  const system: { name: string; home: string; sun: string };

  // camera (/camera) — the 27 camera.* actions as methods; pose channels compose per frame
  const camera: CameraApi;

  // events and hooks
  function on(event: string, handler: (ev: Event) => void | Promise<void>, opts?: { sync?: boolean; vessel?: string }): Subscription;
  function every(kind: "frame" | "step" | number, handler: (tick: StepInfo) => void | Promise<void>, opts?: { clock?: "ut" | "wall" | "render" }): Subscription;
  function service(fn: () => Promise<void>): Subscription;
  function command(name: string, fn: (args: string) => void, description?: string): Subscription;

  // console, audio, schedules, paint, debug fx — same keys as gatOS
  const console: { log(...a: unknown[]): void; warn(...a: unknown[]): void; error(...a: unknown[]): void; exec(command: string): void };
  const audio: AudioApi; const schedules: ScheduleApi; const paint: PaintApi; const fx: FxApi;

  // persistence: per-script state saved in the save-game sidecar
  const state: Record<string, unknown>;
  const script: { name: string; path: string; reload(): void; onReload?: (prev: Record<string, unknown>) => void };

  type CommandResult = { ok: boolean; outcome: "Ok"|"Invalid"|"NotFound"|"Denied"|"Busy"|"Fault"|"TimedOut"|"Unsupported"; message?: string };
}
```

# Lua shape

```lua
---@class ksa
---@field time ksa.Time
---@field active ksa.Vessel|nil
local ksa = require("ksa")
ksa.vessel("Kitty-1"):throttle(0.8)          -- vessel.throttle
ksa.vessel("Kitty-1"):attitude_mode("Prograde")  -- vessel.attitude_mode (Solver); returns CommandResult after the solver slot (coroutine yield)
ksa.on("docked", function(ev) ... end)
ksa.every(1.0, function() ... end, { clock = "ut" })
ksa.sleep(5)                                  -- yields
```

Lua returns `CommandResult` tables; Solver-phase calls yield the running coroutine until the result is known (so they must be called from a coroutine context — all hooks and ad-hoc scripts are).

# Mapping table (abridged; the complete list is the gatOS action vocabulary)

| Script member | Action key | Phase |
|---|---|---|
| `vessel.ignite()/shutdown()/engine()/stage()/throttle()/lights()/rcs()/translate()/rotate()` | `vessel.*` | Frame |
| `vessel.attitudeMode()/attitudeFrame()/attitudeTarget()/rcsMode()/burn()` | `vessel.attitude_*`, `vessel.rcs_mode`, `vessel.burn` | **Solver** |
| `engine.setActive()/setMinThrottle()`, `rcs[i].setActive()` | `engine.active`, `engine.min_throttle`, `rcs.active` | Frame |
| `light.*`, `animation.goal()`, `docking.undock()`, `decoupler.fire()` | `light.*`, `animation.goal`, `docking.undock`, `decoupler.fire` | Frame |
| `vessel.debug.teleport()/impulse()/refillFuel()/refillBattery()` | `debug.teleport/impulse/refill_fuel/refill_battery` | **Solver** |
| `vessel.debug.control()`, `time.setWarp()` | `debug.control_vessel`, `debug.warp` | Frame |
| `camera.*` | `camera.*` | Frame (driver) |
| `audio.*`, `schedules.*`, `paint.*`, `fx.*` | same keys | Frame |

# Stub generation

A reflection-based generator in `Catcom.Core` walks the `ApiSchema` and writes `stubs/host.d.ts` (with JSDoc from the schema docs) and `stubs/ksa.lua` (LuaCATS) into the scripts folder on every mod start, plus `tsconfig.json` (`"types": ["./stubs/host.d.ts"]`, `module: esnext`, `target: es2022`, `strict`) and `.luarc.json` (`runtime.version = "LuaJIT"`, `workspace.library = ["stubs"]`). nvim's LSPs pick them up with no user action.[^readers]

[^sim]: gatOS /sim API surface (condensed)
[^actuators]: gatOS actuator catalog
[^readers]: gatOS snapshot and reader model
