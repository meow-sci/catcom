---
type: Reference
title: The gatOS /sim API surface, condensed (what the in-process API must mirror)
description: A condensed catalog of the /sim tree (paths, read/write, units, action keys, phases), the complete action-key vocabulary, the event types and their detection logic, and the HTTP/MQTT/MCP mirrors; the agreed vocabulary that scripts and gatOS should share.
resource: /Users/asherwin/repos/meow-sci/gatOS/SPEC_9P_FILESYSTEM.md
tags: [gatos, sim, api-surface, events, action-keys, catalog]
sources:
  - id: report
    resource: /references/gatos-internals-report-2026-10-16.md
    title: gatOS internals research report (2026-10-16)
  - id: spec
    resource: /Users/asherwin/repos/meow-sci/gatOS/SPEC_9P_FILESYSTEM.md
    title: SPEC_9P_FILESYSTEM.md (2109 lines, 663 path rows)
  - id: differ
    resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.SimFs/Telemetry/EventDiffer.cs
    title: EventDiffer.cs
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:00:00Z }
status: stable
---

Legend — **S** sensor (read-only), **St** state (read/write setpoint), **T** trigger (write `1`), **B** batch, **Sm** stream. Vessel paths exist at `vessels/by-id/<id>/…` and the alias `vessels/active/…`. The full catalog with every errno and format is the SPEC.[^spec]

# Time, system, bodies

| Path | A | Format / units |
|---|---|---|
| `time/ut`, `time/warp`, `time/sim_dt`, `time/warp_speeds`, `time/auto_warp` | S | s, factor, s (`0` = paused), list, `0` or `1 <ut>` |
| `time/alarm` | St | write target ut; read parks until reached |
| `system/{name,home,sun}` | S | ids |
| `bodies/<id>/{id,class,parent,children,mass,radius,mu,soi,rotation_rate}` | S | kg, m, m³/s², m, rad/s |
| `bodies/<id>/position/ecl`, `velocity/ecl` | S | m, m/s |
| `bodies/<id>/orbit/{apoapsis,periapsis,ecc,inc,lan,argpe,sma,period}` | S | altitude m, deg, m, s |
| `bodies/<id>/atmosphere/{present,height,scale_height,sea_level_pressure,sea_level_density}`, `ocean/{present,density}` | S | |
| `bodies/<id>/focus` | T | `camera.focus` |

# Vessels (core + detail)

| Path | A | Notes |
|---|---|---|
| `id, name, situation, parent, controlled, controllable, is_kitten, com` | S | |
| `scale`, `always_render` | St | any vessel, no authority gate |
| `telemetry` | S | atomic JSON document |
| `position/{cci,ecl,lat,lon}`, `velocity/{orbital,surface,inertial,cci}`, `attitude/{quat,rates}`, `altitude/{barometric,radar}`, `mass/{total,dry,propellant}` | S | |
| `stream` | Sm | NDJSON per sample |
| `orbit/{apoapsis,periapsis,ecc,inc,lan,argpe,sma,period,true_anomaly,time_to_ap,time_to_pe,next_patch}` | S | detail |
| `navball/{pitch,yaw,roll,twr,deltav,frame,speed}`, `environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius,accel,angular_accel,g_force}`, `power/{produced,consumed}`, `battery/{charge,fraction,capacity}` | S | detail |
| `engines/<n>/active` (St), `engines/<n>/{vac_thrust,isp,throttle,propellant}` (S), `engines/<n>/min_throttle` (St) | | |
| `tanks/<resource>/{amount,capacity,fraction}`, `srb/<n>/…`, `srb/<n>/segments/<m>/…` | S | |
| `rcs/<n>/active` (St), `rcs/<n>/{propellant,map}` (S) | | |
| `solar/<n>/{produced,occluded,sun_aoa,efficiency,tracker_angle,current,state}` (S), `solar/<n>/goal` (St) | | |
| `generators/<n>/{active,produced}` | S | |
| `lights/<n>/on, brightness, color, outer_angle, inner_angle, goal` (St), `current, state` (S) | | |
| `docking/<n>/{docked,docked_to,pushoff_impulse}` (S), `docking/<n>/undock` (T) | | |
| `decouplers/<n>/{fired,enabled}` (S), `decouplers/<n>/fire` (T) | | |
| `animations/<n>/goal` (St), `animations/<n>/{current,state}` (S) | | |
| `encounters` (S, NDJSON), `parts/json`, `parts/<n>/{instance_id,id,display_name,template,is_root,subpart_count,position}`, `parts/<n>/subparts/<m>/…` | S | `instance_id` is the stable handle |
| `parts/<n>/paint/…`, `paint/parts/…`, `paint/kitten/…` | St/T | paint keys |

# Vessel control (`ctl/`)

| Path | Write | Action | Phase |
|---|---|---|---|
| `ctl/ignite`, `ctl/shutdown`, `ctl/stage`, `ctl/focus` | `1` | `vessel.ignite/shutdown/stage`, `camera.focus` | Frame |
| `ctl/engine`, `ctl/lights`, `ctl/rcs` | 0/1 | `vessel.engine/lights/rcs` | Frame |
| `ctl/throttle` | 0..1 | `vessel.throttle` | Frame |
| `ctl/translate`, `ctl/rotate` | `x y z` signs | `vessel.translate/rotate` | Frame (latch) |
| `ctl/attitude_mode` | `manual` or track-target token | `vessel.attitude_mode` | **Solver** |
| `ctl/attitude_frame` | `EclBody|EnuBody|Lvlh|VlfBody|BurnBody|Dock` | `vessel.attitude_frame` | **Solver** |
| `ctl/rcs_mode` | `Enabled|Disabled` | `vessel.rcs_mode` | **Solver** |
| `ctl/attitude_target` | `x y z w` Body→CCI | `vessel.attitude_target` | **Solver** |
| `ctl/burn` | `ut dvx dvy dvz` CCI | `vessel.burn` | **Solver** |

Attitude-mode tokens: `manual, Prograde, Retrograde, Normal, AntiNormal, RadialOut, RadialIn, Toward, Away, Antivel, Align, Forward, Backward, Up, Down, Ahead, Behind, Outward, Inward, PositiveDv, NegativeDv, Custom, None`.

# Global control, camera, debug, paint, audio, events

- `ctl/batch` (≤ 64 commands, one phase, atomic same tick); `ctl/timed_batch` + `ctl/schedules/<id>/…` (offset-scripted playback on `render|wall|ut` clocks).
- `camera/{enabled,release,mode,follow,tidal,map/scope,track/,play,set,stop,pose/*}` (27 `camera.*` actions).
- `debug/vessels/<id>/{teleport,impulse,refill_fuel,refill_battery}` (**Solver**), `debug/time/warp`, `debug/focus`, `debug/control_vessel`, welds, thug-life, FX editors, IVA physics.
- `paint/…` (parts, kittens, textures, stickers), `audio/{file/,play,set,stop,status,info}`.
- `events` (blocking NDJSON), `status/{game_version,sampler,accessors,transports}`, `display/…` (kitty-graphics screen stream).

# Complete action-key vocabulary

`vessel.{ignite,shutdown,engine,stage,throttle,lights,rcs,translate,rotate,attitude_mode,attitude_frame,attitude_target,burn,rcs_mode,scale,always_render}` · `engine.{active,min_throttle}` · `rcs.active` · `light.{on,brightness,color,outer_angle,inner_angle}` · `animation.goal` · `docking.undock` · `decoupler.fire` · `camera.{focus,enabled,release,mode,follow,tidal,map_scope,position,frame,anchor,geo,orbit_radius,orbit_azimuth,orbit_elevation,rotation,aim,aim_target,aim_offset,aim_frame,aim_up,roll,fov,ortho,ortho_height,smoothing,pose_reset,play,set,stop}` · `schedule.{pause,scrub,rate,loop,stop,remove,clear}` · `audio.{play,set,stop}` · `debug.{warp,control_vessel,always_render_iva,teleport,impulse,refill_fuel,refill_battery,docking_pushoff,weld_*,thug_life_*,iva_*,fx_spawn,fx_clear,engineplume_*,plumetrail_*,clouds_*,terrain_*}` · `paint.{…46 keys…}`.[^report]

# Events (polling snapshot diff, not Harmony)

`EventDiffer.Diff(previous, ut, warp, activeId, vessels)` runs on the game thread each sample tick; first sample is the baseline; zero-alloc when nothing changed.[^differ]

| Type | Detection | KSA members |
|---|---|---|
| `warp-changed` | `previous.WarpFactor != warp` | `Universe.SimulationSpeed` |
| `active-changed` | active vessel id changed | `Program.ControlledVehicle?.Id` |
| `vessel-appeared` / `vessel-removed` | roster id-set diff | `Universe.CurrentSystem.All` |
| `situation-change` | `Situation` string changed (covers landing/liftoff/splashdown: `Landed/Rolling/Freefall/Floating/Sailing/Maneuvering`) | `Vehicle.Situation` |
| `soi-changed` | parent body changed | `Vehicle.Parent.Id` |
| `engine-state` | per-engine `Active` flipped (**ignition/shutdown proxy**) | `EngineController.IsActive` |
| `flameout` | active and propellant went unavailable | `EngineControllerState.IsPropellantAvailable` |
| `docked` / `undocked` | per-port `Docked` flipped | `DockingPort.Docked/DockedToPart` |
| `decoupled` | rising edge of `Fired` (**staging proxy**) | `Decoupler.IsActive` |
| `animation-complete` | deployment state settled | `KeyframeAnimationModule.State.DeploymentState` |
| `battery-depleted` / `battery-charged` | fraction crossed thresholds | `Battery` charge/capacity |

Feature-emitted: `audio.finished`, `paint.sticker_placed`, `schedule.{started,finished,failed,dropped,evicted}`, `camera.{shot,finished}`, `iva.{impact,escape,release}`. Not detected today: named liftoff, atmosphere entry/exit, stable-orbit/escape (recommended as debounced snapshot rules in the ksa skill, not implemented).[^report]

For an in-process host, a Harmony postfix on `EngineController.SetIsActive` gives a **synchronous, same-call** ignition hook that the polling differ cannot; see [KSA event hook points](/ksa-host/event-hook-points.md). Both models should coexist: Harmony for immediate hooks, snapshot-diff for derived state events.

# Transport mirrors (names only)

HTTP `/v1`: `GET /v1/snapshot, /time, /status, /system, /bodies, /bodies/{id}, /vessels, /vessels/{id}, /vessels/{id}/telemetry, /fs/<path>` (+ `?stream=1` SSE), `POST /v1/fs/<path>`, `POST /v1/command`, `GET /v1/events` (SSE), `/v1/time/wait?until=`, audio and paint-texture file routes. MQTT: retained `gatos/time|status|system|bodies|snapshot|vessels/{id}/…`, `gatos/events`, field mirror `gatos/sim/<path>` with `/set`. MCP: 28 tools (`gatos.get_world … execute_batch, schedule_batch`) and resources `gatos://…`.[^report]

[^report]: gatOS internals research report (2026-10-16)
[^spec]: SPEC_9P_FILESYSTEM.md (2109 lines, 663 path rows)
[^differ]: EventDiffer.cs
