---
type: Reference
title: KSA event hook points for script events
description: For each gameplay event a script might subscribe to (engine ignite/shutdown, throttle, staging, decouple, dock/undock, SOI change, launch, landing, destruction, vehicle spawn/remove, controlled-vehicle switch, editor enter/exit, save/load, warp change, burn start/end, resource depletion, part activation), the concrete Harmony target with signature, thread, and whether polling is required; plus the finding that KSA exposes no gameplay C# events.
resource: /Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/decomp/KSA
tags: [ksa, events, harmony, hooks, engine, staging, docking, save-load]
sources:
  - id: report
    resource: /references/ksa-hooks-report-2026-10-16.md
    title: KSA hook-point research report (2026-10-16, build 5554)
  - id: compilecheck
    resource: ../../../spikes/HookCompileCheck/Mod.cs
    title: HookCompileCheck spike (compiles patches against KSA.dll 5554)
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: stable
---

# No gameplay events exist

An exhaustive `event` grep over `KSA`, `KSA.*`, `Core`, `Brutal*`, `RenderCore` finds only `ViewportRegistry.ViewportRegistered/Unregistered`, `TerminalInterface.OnOutput`, and three networking events. No `Action<>`/`Func<>` callback fields exist on gameplay types. **Every script event requires a Harmony patch or polling.** All targets below run on the main thread; all `InputEvents.*Data.Apply()` run inside `PrepareFrame`.[^report]

Two complementary mechanisms, both worth implementing:

- **Harmony "intent/transition" hooks** give synchronous, same-call events with full arguments (the engine controller, the new vehicle from a split, the destruction cause).
- **Snapshot-diff polling** in an `ApplyVehicleSolvers` postfix gives derived-state events the game never signals (landed, SOI changed, propellant depleted), exactly as gatOS's `EventDiffer` does.

# Hook table (build 5554)

| Event | Target (declaring type, kind, verbatim signature) | Notes |
|---|---|---|
| Engine on/off **intent** | `EngineController.SetIsActive(Vehicle? vehicle, bool activationState)` instance | enqueues `InputEvents.IActivateInputData { Operation = Engine }`; covers manual and staging activations; `EngineController.IsActive` has an internal setter and is updated in `ApplyInputEvents` |
| Engine physically ignited/shut down | poll `vehicle.Parts.RocketCores.States[i].ActivatedThisFrame / DeactivatedThisFrame` (`RocketCoreState`) in an `ApplyVehicleSolvers` postfix | the physical edge computed by the worker |
| Thruster (RCS) on/off | `ThrusterController.SetIsActive(Vehicle?, bool)` instance | same shape |
| Throttle | prefix `Vehicle.PrepareWorker(SimStep simStep)` (virtual) to override `_manualControlInputs.EngineThrottle` before the copy to the worker; observe via `Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)` | `GetManualThrottle()` / `GetMinThrottle()` public readers; no public setter |
| Staging | `SequenceList.ActivateNextSequence(Vehicle vehicle)` instance on `vehicle.Parts.SequenceList` | sets `ActiveSequence`, activates `ISequenced` modules in the stage (their own `SetIsActive` enqueue lands next frame) |
| Decouple | postfix `Decoupler.Decouple(Vehicle oldVehicle)` → `Vehicle?` (the new vehicle) | reached via `Decoupler.SetIsActive` → `IActivateInputData { Decouple }` |
| Any separation | postfix `Vehicle.Split(Part.Connector splitConnector, double splitImpulse, out PoseChange combinedToSplit, string? splitVehicleId = null)` | decouple, undock, debris |
| Dock / undock | postfix `DockingPort.Dock(Vehicle thisVehicle, Vehicle otherVehicle, DockingPort otherVehicleDockingPort, out PoseChange consumedToCombined)` / `DockingPort.Undock(Vehicle oldVehicle, out PoseChange combinedToSplit)` | physical dock is detected by the worker (`DockingEvent`) and applied in `ApplyRenderEventsToVehicles` |
| SOI / parent change | postfix `Vehicle.UpdateFromTaskResultsSynchronized()` and diff `vehicle.Parent` against a cache | `Vehicle.NextPatchEventTime` pre-warns of `Escape`/`Encounter` transitions |
| Vehicle created | `Vehicle.CreateVehicle(CelestialSystem system, VehicleTemplate template, IParentBody parent, string id)` / `CreateVehicle(CelestialSystem, doubleQuat body2Cce, double3 bodyRates, IParentBody parent, string id, Part root, Orbit orbit)` static; or postfix `CelestialSystem.Register(Astronomical)` for every registration (vehicles, debris, EVA kittens) | |
| Liftoff | postfix `Vehicle.MarkLaunched()`; or diff `Vehicle.HasLaunched` / `Vehicle.Situation` after `ApplyVehicleSolvers` | `StartCrewMissions` (private) runs when situation first changes |
| Landing / splashdown | **poll** `Vehicle.Situation` (`Maneuvering, Freefall, Rolling, Landed, Sailing, Floating, Dragging, Bottomed`); impact FX: prefix `Celestial.TrySpawnGroundImpact(GroundImpactEvent)` / `TrySpawnWaterSplash(WaterSplashEvent)` | no main-thread "landed" callback exists |
| Destruction with cause | prefix `Universe.DestroyVehicleFromEvent(Vehicle vehicle, VehicleDestructionEvent destructionEvent)` static (`VehicleDestructionCause { GroundImpact, OceanImpact, Collision, ExcessiveGForce, AerodynamicForces, HydrodynamicForces }`, `PeakGLoad`, `PeakDynamicPressure`) | |
| Any removal | prefix `Universe.DestroyVehicle(Vehicle vehicle, CrewDisposition crew)` static; postfix `CelestialSystem.Deregister(Astronomical)` | recovery and console `destroy` also land here |
| Part failure | prefix `PartFailureEvent.Apply(Vehicle vehicle)` (`List<Part> FailedParts; bool DestroyWholeVehicle`) | |
| Controlled vehicle switch | `AccessTools.PropertySetter(typeof(Program), nameof(Program.ControlledVehicle))` (static `set_ControlledVehicle(Vehicle?)`) | setter is called with the same value often; diff in the patch |
| Editor (VAB) enter / exit | postfix `VehicleEditor..ctor(Vehicle? inEditingVehicle, bool newVehicle)`; prefix `VehicleEditor.Dispose()` | `Program.EditorFlag`, `Program.Editor`, `Program.IsEditorOpen` |
| Launch menu | postfix `VehicleLaunchMenu..ctor` / `Dispose` | enqueues `CreateVehicleData` |
| Save written | postfix `UncompressedSave.Write()` (`__instance.Directory`) | write the script sidecar here |
| Save loading | prefix `Universe.DeserializeSave(UniverseData universeData)` (all vehicles about to be destroyed); postfix `Program.OnGameLoaded()` | restore script state |
| New system loaded | postfix `Universe.LoadSystem(string id)` / `Universe.OnLoaded()` | `OnLoaded` also runs the console `onLoad` lists |
| Warp change | prefix `Universe.SetSimulationSpeed(double simulationSpeed, bool alert = true)` static, diff against `GetSimulationSpeed()` | called every frame during auto-warp/menu pause |
| Auto-burn complete / fuel-out | prefix `FlightComputer.RaisePendingAlerts(Vehicle vehicle)` and read `__instance.AutoBurnCompleted` / `AutoBurnStoppedOutOfPropellant` (public fields) before they are cleared | only called for vehicles in frame; diff fields after `ApplyVehicleSolvers` for off-screen vehicles. Burn start: diff `FlightComputer.Burn?.StartedAt` |
| Burn plan edits | `FlightComputer.AddBurn(Burn)` / `RemoveBurn(Burn)` / `BurnUpdated(Burn)` instance | via `InputEvents.BurnUpdateData` |
| Resource depleted | **poll** `RocketCoreState.IsPropellantAvailable`, `EngineControllerState.IsPropellantAvailable`, `Vehicle.IsOutOfNeededPropellant()` (controlled vehicle), `Tank.FilledFraction(vehicle.Parts.Moles.States)`, `Battery.FilledFraction(BatteryState)` | no event |
| Part enable / lights / animations | `IEnable.SetIsEnabled(bool)` → `InputEvents.IEnableInputData`; `LightModule.IsActive`, `Vehicle.LightsOn`, `Vehicle.ToggleLights()`; `KeyframeAnimationModule.TimeGoal` + `State.DeploymentState` | poll for animation completion |
| Console output | postfix `ConsoleWindow.Print(ReadOnlySpan<char>, ImColor8, int)` / `Print(ReadOnlySpan<byte>, ImColor8, int)` | thread-safe entry points; see [console and commands](/ksa-host/console-and-commands.md) |

`EngineController.SetIsActive`, `Universe.ExecuteNextVehicleSolvers` and `Program.OnFrame` patches were compiled against the 5554 reference assemblies in the compile-check spike; the remaining targets were read from the decompiled source and should be added to that spike before implementation.[^compilecheck]

# Design implications

1. Scripts subscribe by **event name**; the host maps names to a Harmony patch or a differ rule. Both produce the same event record shape (`ut`, `type`, `vesselId`, payload).
2. Harmony patch bodies must be try/catch, log-once, self-disabling; a throw on the sim path crashes the game.
3. Hooks fire on the main thread inside `PrepareFrame` or UI phases; a sync script handler therefore runs inside that call (keep it short), while an async handler is queued to the script scheduler.
4. Every patched target is a churn point; wrap each in a `[KsaAnchor]`-style attribute and re-verify on game updates (see [gatOS compatibility discipline](/existing-mods/gatos-harmony-reflection-and-compat.md)).

[^report]: KSA hook-point research report (2026-10-16, build 5554)
[^compilecheck]: HookCompileCheck spike (compiles patches against KSA.dll 5554)
