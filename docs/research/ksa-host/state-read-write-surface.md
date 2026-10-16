---
type: Reference
title: KSA state read/write surface, serialization, and unload safety
description: The exact public (and necessary non-public) KSA members a scripting bridge reads and writes - globals, collections, Vehicle, Orbit/bodies, PartTree/modules/resources, FlightComputer - with access-level gotchas, how saves are serialized (XmlSerializer; use a sidecar for script state), and what must be cleaned up for safe unload.
resource: /Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/decomp/KSA/Vehicle.cs
tags: [ksa, api-surface, vehicle, parts, modules, flight-computer, serialization, unload]
sources:
  - id: report
    resource: /references/ksa-hooks-report-2026-10-16.md
    title: KSA hook-point research report (2026-10-16, build 5554)
  - id: probe
    resource: /spikes/ksa-probe-tool.md
    title: KsaProbe metadata tool
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: stable
---

# Globals

| Member | Notes |
|---|---|
| `Universe.CurrentSystem` (`CelestialSystem?`), `Universe.Stars`, `Roots`, `WorldSun`, `KittenRoster` | null before load |
| `Universe.GetElapsedTime()` / `GetElapsedSeconds()` / `GetLastSimStep()` / `GetNextSimStep()` | time |
| `Universe.SimulationSpeed`, `GetSimulationSpeed()`, `SetSimulationSpeed(double, bool)`, `IsPaused()`, `IsAutoWarpActive`, `AutoWarpTime`, `AutoWarpTo(...)`, `AutoWarpStop(bool)` | warp |
| `Universe.SetTarget(Vehicle, IOrbiter, Part?)` / `UnsetTarget(Vehicle)` | prefer `InputEvents.ChangeTargetData` |
| `Program.ControlledVehicle` (get/set), `Program.VehiclesInFrame` (`ReadOnlySpan<Vehicle>`), `Program.MainViewport` (`IGameViewport`: `BaseCamera`, `MapCamera`, `GetCamera()`, `Mode`), `Program.GetMainCamera()`, `IsLoaded`, `DrawUI`, `EditorFlag`, `Editor`, `IsEditorOpen`, `FrameNumber`, `ConsoleWindow`, `TerminalInterface`, `GetPlayerTime()` | |
| `Camera.SetFollow(IFollowable, bool tidalLocking, bool changeControl = true, bool alert = true)` / `Unfollow(bool)`, `Following`, `NearbyCelestial` | ECL frame |
| `GameSettings.Current`, `GameSettings.IsUserTyping`, `TimedAlert.Create(string, byte4, double)`, `TimedAlert.CreateWarning(string, double)` | toasts |
| `ModLibrary.Get<T>(string)` / `TryGet<T>` / `Has<T>` | templates (`TryGet` does not support `PartTemplate`) |

# Collections

`CelestialSystem`: `LookupCollection<Astronomical> All`, `HomeBody`, `Count`, `Id`, `Get(string)`, `Get<T>(string)`, `Exists<T>`, `CountOf<T>()`, `Register/Deregister(Astronomical)`, `Rename(Vehicle, string)`, `DestroyAllVehicles()`, `ForEach(...)`. `LookupCollection<T>`: `Get`, `AsSpan()`, `UnsafeAsList()`, `OfType<T2>()` (struct enumerator), `TryGet`, `CountOf<T1>()`. **There is no `Vehicles` member**: enumerate `CurrentSystem.All.OfType<Vehicle>()`.[^probe]

# Vehicle (selected public members)

- Identity/state: `Id`, `Hash`, `Class`, `IsDisposed`, `IsDebris`, `HasLaunched`, `LaunchGameTime`, `IsEditedVehicle`, `IsControllable`, `Situation`, `HasMeaningfulOrbit`, `Crew`.
- Orbit/frames: `FlightPlan`, `Patch`, `Orbit`, `Parent`, `Periapsis`, `Apoapsis`, `NextPeriapsisTime/NextApoapsisTime/NextPatchEventTime`, `Body2Cce`, `BodyRates`, `GetBody2Cci()`, `GetLvlh2Cce()`, `GetEnu2Cce()`, `GetPositionCci/Cce/Ecl/Orb()`, `GetVelocityCci/Cce/Ecl/Orb()`, `GetStateVectorsAt(UniverseTime)`, `OrbitalSpeed`, `GetInertialSpeed()`, `GetSurfaceSpeed()`, `GetBarometricAltitude()`, `GetRadarAltitude()`.
- Physics: `Props`, `KinematicStates`, `PhysicsEnvironment` (`AtmosphericPressure`, `AtmosphericDensity`), `AccelerationBody`, `AngularAccelerationBody`, `TotalMass`, `InertMass`, `PropellantMass`, `CenterOfMassAsmb`, `NavBallData`.
- Control: `FlightComputer`, `BurnPlan`, `Target`/`TargetPart` (`SetTarget`), `ControlPart` (`SetControlPart`), `GetManualThrottle()`, `GetMinThrottle()`, `ProcessInput(...)`, `SetStabilization(bool)`, `SetEnum/ToggleEnum(Enum?)` (accepts `VehicleEngine`, `FlightComputer*` enums, `VehicleReferenceFrame`), `IsSet<T>(T, bool)`, `LightsOn`, `ToggleLights()`, `ClearHeldPlayerInput()`.
- Mutation: `Teleport(Orbit?, doubleQuat?, double3?)`, `TeleportToLocation(Celestial, lat, lon)`, `RefillConsumables()`, `DepleteConsumables()`, `Split(...)`, `MergeFrom(...)`, `Recover()`, `SetName(string)`, `MarkLaunched()`, `UpdateAfterPartTreeModification()`, `SetFlightPlan(FlightPlan)`, `Dispose()`.
- Static: `CreateVehicle(...)` ×2, `CreateVehicleFromSaveGameData`, `IsOutOfNeededPropellant()`, `AreAllEnginesInactive()`.
- Private but needed: `_manualControlInputs` (`EngineOn`, `EngineThrottle`, `ThrusterCommandFlags`) via `AccessTools.FieldRefAccess` — the only way to set throttle/translate/rotate, inside the solver-prefix window.[^report]

# Orbit and bodies

`Orbit`: `Parent`, `Mu`, `Eccentricity`, `SemiMajorAxis`, `Inclination`, `LongitudeOfAscendingNode`, `ArgumentOfPeriapsis` (radians), `Periapsis`/`Apoapsis` (radii), `PeriapsisAltitude`/`ApoapsisAltitude`, `Period`, `StateVectors`, `GetStateVectorsAt(UniverseTime)`, `IsBound()`, static `CreateFromStateCci(...)`. `IParentBody`: `Mass`, `Mu`, `SphereOfInfluence`, `Children`, `BodyTemplate`, `GetCci2Cce()/GetCce2Cci()/GetCcf2Cce()`, `GetAtmosphereReference()`, `GetOceanReference()`, `MeanRadius`, `GetPositionEcl()`. `Astronomical`: `Id`, `Class`, `MeanRadius`, `GetBodyFixed2Ecl()`, `GetPositionEcl()`, `DistanceTo(Astronomical)`.[^report]

# Parts, modules, resources

- `PartTree`: `Parts` (`ReadOnlySpan<Part>`), `Root`, `Count`, `Modules` (`ModuleList`), `States`, typed lists `Tanks`, `Moles`, `RocketCores`, `Batteries`, `Generators`, `SolarPanels`, `PowerConsumers`, `DockingPorts`, `Decouplers`, `Controls`, `SequenceList`, `FuelLinks`, `TotalFillFraction`, `RecomputeAllDerivedData()`, `MarkDerivedDirty(DerivedData)`, `RefillConsumables()`, `Merge(Part, Part, bool)`, `Split(Part, bool)`, `Find(ulong)`, `BeginBulkChange()/EndBulkChange()`.
- `ModuleList.Get<TModule>()` → `Span<TModule>`; `GetUsing<TInterface>()` (e.g. `IActivate`, `ISequenced`, `IEnable`); `HasAny<T>()`.
- `ModuleStateful<TModule,TState,TGlobal,TFx>.StateList`: `Modules`, `States` (`ReadOnlySpan<TState>`), `GetState(module)` (`ref readonly`), **`GetModuleAndAllMutableStatesForInitialization(module)`** (the only writable state accessor), `TryGetById(ulong instanceId, out TModule)`.
- `Part`: `Id`, `DisplayName`, `InstanceId` (uint, runtime), `Template`, `PartParent`, `Tree`, `TreeChildren`, `Connections`, `Modules`, `SubtreeModules`, `SubParts`, `FullPart`, `Stage` (`SetStage`), `Scale`, `PositionVehicleAsmb`, `Asmb2VehicleAsmb`, `ActivateSubtreeInStage(Vehicle?, int)`.
- Engines: `EngineController { Cores, MinimumThrottle, VacuumData, SeaLevelData, IsActive (internal set), Sequence }`, `EngineControllerState { IsPropellantAvailable, CommandThrottle, CommandBurnTime, CommandTime, WasActive }`, `RocketCoreState { Conditions, IsPropellantAvailable, Throttle, ThrustTimeRemaining, MassFlowRate, ActivatedThisFrame, DeactivatedThisFrame }`, `ThrusterController { Cores, ManualControlMap, IsActive }`.
- Tanks/batteries: `Tank { StorageVolume, Moles, PropellantUseEnabled, FilledFraction(ReadOnlySpan<MoleState>), Refill, Deplete }`, `Battery { MaximumCapacity, Refill(ref BatteryState), FilledFraction }`, `ResourceManager.RefillAllTanks(PartTree)`.
- `ref struct` / `ref readonly` returns (`Props`, `NavBallData`, mutable-state refs) cannot be captured by a dynamic bridge: copy fields out on the main thread.[^report]

# FlightComputer (main-thread copy)

Public fields `BurnMode, AttitudeMode, AttitudeFrame, AttitudeTrackTarget, CustomAttitudeTarget, ManualThrustMode, RollMode, RCSMode, Burn (BurnTarget?), PlannedBurnThrottle, IsThrottleGLoadLimited, AutoBurnStoppedOutOfPropellant, AutoBurnCompleted, MaxGLoad, …`; properties `BurnPlan`, `VehicleConfig`; methods `AddBurn/RemoveBurn/BurnUpdated(Burn)`, `Handle(FlightComputerAction)`, `TrackTarget(...)`, toggles. **Writes outside the `ExecuteNextVehicleSolvers` prefix are clobbered** by `CopyFrom` on the next apply. `vehicle-api.md`'s `VehicleConfig.TotalEngineVacuumThrust` was not visible in 5554 (`VehicleConfigInfo { Thrusters, Gimbals, HasRcs, HasTvc }`); verify before use.[^report]

# Access-level gotchas

Internal/private setters: `EngineController.IsActive`, `ThrusterController.IsActive` (use `SetIsActive`), `Vehicle.HasLaunched/IsDebris/Target/ControlPart/FlightPlan` (use public methods), `Universe.SimulationSpeed` (use `SetSimulationSpeed`), `SequenceList.ActiveSequence` (use `SetActiveSequence`), `Orbit.Parent`, `Part.Stage` (use `SetStage`).[^report]

# Serialization

Saves are **`System.Xml.Serialization.XmlSerializer`** (`universe.xml` + `vehicle.xml`, polymorphic module `SaveData` via `XmlType`); settings and manifests are TOML via Tomlet; MemoryPack/MessagePack/System.Text.Json/Newtonsoft are not used anywhere in KSA. `XmlSerializer` is type-closed, so script state goes into a **sidecar file** `<save dir>/<mod>.{toml|json}` written in a postfix of `UncompressedSave.Write()` and read in a postfix of `Universe.DeserializeSave` / `Program.OnGameLoaded`. Vehicle identity across saves: `Vehicle.Id`; `Part.InstanceId` is **not** stable across loads (`Universe.ResetRunningId()`); use `PartInstance.LocalInstanceId/GlobalInstanceId` from save data when persistence must survive reload.[^report]

# Unload and long-session safety

- Nothing in KSA is `AssemblyLoadContext`-aware. Before `[StarMapUnload]`: `TerminalInterface.UnregisterObject(owner)`, unsubscribe `TerminalInterface.OnOutput` and `ViewportRegistry.*`, `Harmony.UnpatchAll(id)` (a patch body in an unloaded assembly crashes on the next hot-path call), release secondary viewports, and never create a `LogCategory` you cannot remove.
- Exceptions in a Harmony prefix on the sim path crash the process (heap dump via Brutal.Monitor); worker exceptions are rethrown on the main thread. A script host catches everything and never blocks the main thread.
- ImGui is single-context, main-thread, valid only between `PrepareImGui` and `ImGui.Render()` (the two StarMap GUI hooks).
- A native scripting runtime must not be unloaded while any callback or finalizer can still fire; ALCs are never unloaded anyway.[^report]

[^report]: KSA hook-point research report (2026-10-16, build 5554)
[^probe]: KsaProbe metadata tool
