---
type: Reference
title: gatOS actuator catalog (the write surface, with exact KSA members)
description: Every gatOS actuator with its action keys, validation, the KSA members it calls, and whether it is one-shot, latched, or a per-frame driver; the command-dispatch router; and the per-frame drivers an in-process host must replicate for persistent commands.
resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.GameMod/Game/Ksa/Actuators
tags: [gatos, actuators, write-surface, ksa-api, flight-computer]
sources:
  - id: report
    resource: /references/gatos-internals-report-2026-10-16.md
    title: gatOS internals research report (2026-10-16)
  - id: catalog
    resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.GameMod/Game/Ksa/KsaCatalog.cs
    title: KsaCatalog.cs
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:00:00Z }
status: stable
---

# The router (`KsaCatalog.Execute`)

1. `accessor = $"actuator.{action}"`; if the health latch says degraded → `Unsupported`.
2. Vessel-agnostic families routed first and exempt from the authority gate: `debug.warp`, `debug.always_render_iva`, welds, thug-life, FX, audio, `schedule.*`, `camera.*`, `paint.*`.
3. `ResolveVehicle(id)` = linear scan of `Universe.CurrentSystem.All.UnsafeAsList()` for `Vehicle.Id == id` (null → `NotFound`).
4. Authority gate: unless the action is in `AnyVesselActions` (`vessel.scale`, `vessel.always_render`) or `control_all_vessels` is on, writes to a vehicle other than `Program.ControlledVehicle` are `Denied`.
5. `Dispatch(vehicle, cmd)` switch → actuator; success clears the latch; any exception → `health.Fault(accessor, ut, msg)` and a `Fault` result. **Never throws.**[^catalog]

# Actuators

Notation: *one-shot* = applied once; *latched* = KSA state persists until rewritten; *driver* = gatOS re-applies every frame. **Solver** = must run in the Solver lane.

| Action key(s) | KSA members called | Persistence |
|---|---|---|
| `vessel.ignite` / `vessel.shutdown` | `vehicle.SetEnum(VehicleEngine.MainIgnite)` / `SetEnum(VehicleEngine.MainShutdown)` | latched game state |
| `vessel.engine` | same pair, by flag | latched |
| `engine.active` (ordinal) | `vehicle.Parts.Modules.Get<EngineController>()[n].SetIsActive(vehicle, bool)` | latched |
| `engine.min_throttle` | `EngineController.MinimumThrottle = f; vehicle.Parts.MarkDerivedDirty(DerivedData.RocketControls)` | latched |
| `vessel.throttle` | **reflection**: `Vehicle._manualControlInputs` (struct field) → `.EngineThrottle` (float), box/write-back; public reader `GetManualThrottle()` | latched setpoint (KSA caps applied throttle at 90% MaxGLoad since 5497) |
| `vessel.stage` | `vehicle.Parts.SequenceList.ActivateNextSequence(vehicle); vehicle.UpdateAfterPartTreeModification();` | one-shot |
| `vessel.rcs` / `rcs.active` | `ThrusterController.SetIsActive(vehicle, bool)` over `Modules.Get<ThrusterController>()` | latched |
| `vessel.translate` / `vessel.rotate` | reflection on `_manualControlInputs.ThrusterCommandFlags` (`ThrusterMapFlags`); +X=`TranslateForward`, +Y=`TranslateRight`, +Z=`TranslateDown`; rotation +X=`RollRight`, +Y=`PitchUp`, +Z=`YawRight`; read-back `GetThrusterFlags()` | latches like a held key, but KSA clears it on vessel switch, focus loss, camera-mode switch, ImGui keyboard focus, warp > 30× (`Vehicle.ClearHeldPlayerInput()`); dead while `FlightComputer.RCSMode != Enabled`; auto attitude strips rotation bits |
| `vessel.attitude_mode` | `fc.AttitudeMode = Manual` or `Auto` + `fc.AttitudeTrackTarget = <enum>` | latched **Solver** |
| `vessel.attitude_frame` | `fc.AttitudeFrame = Enum.Parse<VehicleReferenceFrame>` | latched **Solver** |
| `vessel.rcs_mode` | `vehicle.FlightComputer.RCSMode = <FlightComputerRCSMode>` | latched **Solver** |
| `vessel.attitude_target` (quat) | `frame2Cci = VehicleReferenceFrameEx.GetEclBody2Cci(vehicle.Orbit.Parent.GetCce2Cci()); fc.AttitudeMode=Auto; fc.AttitudeTrackTarget=Custom; fc.AttitudeFrame=EclBody; fc.CustomAttitudeTarget = frame.QuaternionToEulerAngles(doubleQuat.Concatenate(target2Cci, frame2Cci.Inverse()))` (KSA recomputes Target2Cci from the Euler form each solver step) | latched **Solver** |
| `vessel.burn` (ut, dv xyz CCI) | `vehicle.FlightComputer.Burn = new BurnTarget { ImpulsiveInstant = new UniverseTime(ut), DeltaVTargetCci = new float3(...) }` | latched **Solver**; autopilot executes |
| `vessel.lights` / `light.on` | `vehicle.LightsOn = on; foreach PowerConsumer c with LightSwitch: c.LightIsActive = on` / `light.Parent.FullPart.LightSwitch.LightIsActive` | latched |
| `light.brightness/color/outer_angle/inner_angle` | `EnsureUnshared(light)` clones the shared `LightModule.Template` + reference objects via `RuntimeHelpers.GetUninitializedObject`, then writes `Template.Intensity.Value`, `ColorRgb.{R,G,B}` + `OnDataLoad(null!)`, `OuterAngle.Value`, `InnerAngle.Value` | latched per instance |
| `animation.goal` (also solar/light goals) | `KeyframeAnimationModule.TimeGoal = fraction * module.Shared.Duration` | latched setpoint; sim integrates it |
| `decoupler.fire` | `Decoupler.SetIsActive(vehicle, true)` (Busy if already fired; Unsupported if `!IsEnabled`) | one-shot, irreversible |
| `docking.undock` | `InputEvents.VehicleDockingInputBuffer.Add(new VehicleDockingInputData { Vehicle, DockingPort, Undock = true })` → KSA applies in its input phase | one-shot, deferred |
| `debug.docking_pushoff` | `DockingPort.PushoffImpulse = f` | latched |
| `vessel.scale` | recursive `part.Scale = new double3(f)`; `KittenEva` via reflected `_renderable._characterAvatar.Core.Scale = f*0.01f` | one-shot |
| `vessel.always_render` | dynamic Harmony prefixes on `Vehicle.GetWorldMatrix(Camera)` / `Vehicle.UpdateRenderData(IViewport,int)` | driver |
| `debug.warp` | `Universe.SetSimulationSpeed(factor, alert: false)` | one-shot |
| `debug.control_vessel` | `Program.GetMainCamera()?.SetFollow(target, tidalLocking: true, changeControl: false); Program.ControlledVehicle = target;` | one-shot |
| `debug.teleport` (6 CCI values) | `Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), pos, vel, default); vehicle.Teleport(orbit, null, null); vehicle.UpdatePerFrameData();` | one-shot **Solver** |
| `debug.impulse` | same teleport pattern with bumped `GetVelocityCci()` | one-shot **Solver** |
| `debug.refill_fuel` / `debug.refill_battery` | `vehicle.RefillConsumables()` / `batteries.GetModuleAndAllMutableStatesForInitialization(b).Module.Refill(ref state)` | **Solver** |
| `camera.focus` | `Program.MainViewport.{MapCamera,BaseCamera}.SetFollow(target, tidalLocking:true, changeControl:false, alert:false)` | one-shot |
| 27 `camera.*` | `CameraDirector` (take/release/mode/follow/pose channels composed every frame through an installed `CameraPoseController : FixedController`) | driver |
| `audio.play/set/stop` | `GameAudio.System.TryCreateSound(...)`, `TryPlaySound(...)`, `Channel.TrySet{Volume,Pan,Pitch,Paused,...}` | driver (per-frame tick) |
| `debug.weld_*` | per frame after `JobSystems.VehicleSolver.Wait()`: `Orbit.CreateFromStateCci(parent, Universe.GetJobSimStep(dt).NextTime, …)`, `source.Teleport(orbit, body2Cce, rates)`, `UpdatePerFrameData()` | driver (post-solver) |
| `debug.iva_*` | gatOS-owned BepuPhysics `Simulation`; writes `Part.{PositionParentAsmb,Asmb2ParentAsmb}` | driver (post-solver) |
| `debug.fx_*`, `debug.{engineplume,plumetrail,clouds,terrain}_*` | particle emitters; `VolumetricExhaustTemplate.Get(id)` fields; `VolumetricTrailRenderer` fields; clouds references; `PlanetRenderer.PlanetUbo` mapped-memory writes | latched session-scoped, restored at unload |
| `paint.*` (46 keys) | shader-source prefix + render-state postfixes; `BindlessTextureLibrary.SetTexture/AddTexture/FreeTexture` | driver + dynamic patches |

# Per-frame drivers to replicate for persistent commands

| Driver | Slot | Note |
|---|---|---|
| `CameraDirector.Update(dt)` | viewport prefix | writes `Camera.PositionEcl/LocalRotation/SetFieldOfView/…`; postfix publishes applied transform |
| `AudioActuator.Tick()` | Frame | FMOD pumped by the game on this thread |
| `WeldManager.Update(dt)`, `IvaPhysicsManager.Update(dt)` | post-solver | after `JobSystems.VehicleSolver.Wait()` |
| `ScheduleStore.{Activate,AdvanceAll,Tick}` | immediately before the Frame drain | three clock bases: `render` (dtPlayer), `wall` (Stopwatch), `ut` (sim-time delta ≥ 0) |

# Addressing

Vessel = `Vehicle.Id`; any astronomical = `Universe.CurrentSystem.Get(id)`; module = **ordinal** into `vehicle.Parts.Modules.Get<T>()` (stable until the vehicle is edited/staged); part = `Part.InstanceId` (uint, the stable session handle); camera target grammar `vessel:<id> | body:<id> | part:<vessel>/<iid> | none`.[^report]

[^report]: gatOS internals research report (2026-10-16)
[^catalog]: KsaCatalog.cs
