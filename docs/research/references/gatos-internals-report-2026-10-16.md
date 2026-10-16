---
type: Research Report
title: gatOS internals research report (2026-10-16)
description: Verbatim research report produced by a delegated research pass on 2026-10-16; the primary source the distilled concepts in this bundle cite. Claims inside are tagged verified/inference/measured by the author of the report.
tags: [reference, report, raw]
generated: { by: claude-fable-5.1/ksa-scripting-research-subagent, at: 2026-10-16T20:00:00Z }
status: stable
---

> Mirrored from the session scratchpad into the bundle so the cited source outlives the session. Scratchpad paths mentioned inside (e.g. harness locations) no longer exist.

# gatOS internals — the game read/write model, extracted for `ksa-scripting`

Source repo: `/Users/asherwin/repos/meow-sci/gatOS` (all paths below are relative to it unless absolute).
Game baseline the code is anchored against: KSA `2026.10.10.5554` (179 `[KsaAnchor]` sites; `AGENTS.md:77-89`).

The one sentence that bounds everything: **only `gatOS.GameMod` references KSA/Brutal/StarMap, and inside it every game-state touch lives under `gatOS.GameMod/Game/Ksa/**` plus a handful of Harmony hook targets in `Game/Mod.Game.cs`.** Two immutable records are the firewall: `SimSnapshot` (reads) and `SimCommand` (writes). Everything else (9P tree, HTTP, MQTT, MCP, scheduler, camera math) is game-free (`AGENTS.md:467-483`, `.claude/skills/upgrade-ksa/SKILL.md §1`).

---

## 0. Orientation: the data path in one diagram

```
                    ┌──────────── game thread ────────────────────────────────────────────┐
KSA Vehicle/Universe│ TelemetrySampler.Tick(dt,active) → VesselReader/PartsReader/BodyReader│
objects             │   → new SimSnapshot(...)  → SnapshotStore.Publish (volatile swap)     │
                    │ CommandQueue.Drain(Frame, KsaCatalog, 64)   ← per frame               │
                    │ CommandQueue.Drain(Solver, KsaCatalog, 64)  ← Harmony prefix on        │
                    │                                               Universe.ExecuteNextVehicleSolvers
                    └────────────────────────────────────────────────────────────────────────┘
                              ▲ SimCommand (immutable)              │ SimSnapshot (immutable)
      transport threads:  9P ControlFile/TriggerFile/BatchFile, HTTP POST /v1/command|/v1/fs,
                          MQTT gatos/command|gatos/sim/<path>/set, MCP tools, serial SCPI
                          → ICommandSink.SubmitAsync(cmd, ct) → await Task<CommandResult> (2 s timeout)
```

For an in-process script host the marshalling problem **inverts**: scripts already run on the game thread if you tick them from the same per-frame slot gatOS uses (`Mod.DrivePerFrame`), so you can read live KSA objects directly. What you must still copy exactly: (a) the **Solver-phase** write lane for the flight-computer setpoints and teleports, (b) the **post-solver** slot (`JobSystems.VehicleSolver.Wait()`) for anything that re-poses vehicles every frame, (c) the per-vehicle try/catch + health-latch discipline, and (d) the action vocabulary (§4) so scripts and `/sim` agree.

---

## 1. Threading & game-thread marshalling

### 1.1 StarMap lifecycle hooks gatOS uses (`gatOS.GameMod/Mod.cs`)

| Attribute | Method | What runs there |
|---|---|---|
| `[StarMapMod]` on `public sealed partial class Mod` with `public bool ImmediateUnload => false;` | — | `Mod.cs:41-43,172` |
| `[StarMapImmediateLoad] public void OnImmediateLoad()` | no-op (renderer not live) | `Mod.cs:202-205` |
| `[StarMapAllModsLoaded] public void OnFullyLoaded()` | builds stores/queue/tree, starts 9P/HTTP/MQTT/MCP servers, `InstallSolverHook()`, `InstallCameraHook()`, `InstallDisplayHook()`, `InstallMenuFallback()` | `Mod.cs:212-335` |
| `[StarMapBeforeGui] public void OnBeforeUi(double dt)` | `_uiHooksRanThisFrame = true; DrivePerFrame(dt);` — StarMap implements this as a Harmony prefix on `Program.OnDrawUiFrame` (inside `if (DrawUI)`, so **F2 stops it**) | `Mod.cs:347-352` |
| `[StarMapAfterGui] public void OnAfterUi(double dt)` | `DrivePostSolver(dt); DrawGameUi();` — postfix on `Program.OnDrawUiViewports`, also F2-gated | `Mod.cs:384-406` |
| `[StarMapAfterOnFrame] public void OnAfterFrame(double currentPlayerTime, double dtPlayer)` | postfix on `Program.OnFrame` itself (always fires); re-runs `DrivePerFrame`+`DrivePostSolver` **only** on frames the GUI hooks were skipped (boolean latch) | `Mod.cs:427-443` |
| `[StarMapUnload] public void Unload()` | `TeardownGameCheats(); RemoveCameraHook(); RemoveSolverHook(); RemoveMenuFallback(); DisposeDisplayCapture();` then VM/servers with bounded waits | `Mod.cs:484-544` |

The per-frame bodies (`Mod.cs:450-478`):

```csharp
private void DrivePerFrame(double dt)
{
    if (_mainViewportPreparedThisFrame) { _mainViewportPreparedThisFrame = false; }
    else { SampleTelemetry(dt); TickSchedules(dt); DrainCommands(); }   // fallback when no camera hook
    DriveAudio(); DrivePaint(); UpdateThugLife();
}
private void DrivePostSolver(double dt) { DriveWelds(dt); DriveIvaPhysics(dt); }
```

The **normal** path for sample→schedule→drain is actually the camera viewport prefix (`Game/Ksa/Camera/CameraViewportPatch.cs:52-65` → `Mod.PrepareMainViewportFrame(dt)` at `Mod.Game.cs:574-583`), which runs inside `GameViewport.OnFrame(double)` for `Program.MainViewport` **after simulation advance and before KSA rebuilds the camera matrices**. The GUI/F2 path consumes the `_mainViewportPreparedThisFrame` latch so the trio never double-steps (`Mod.cs:67-70`). Full pipeline diagram: `docs/ARCHITECTURE.md:32-76`.

### 1.2 The three game-thread write lanes

| Lane | Where | What drains there | Why |
|---|---|---|---|
| **Frame** | `Mod.DrainCommands()` → `queue.Drain(CommandPhase.Frame, _catalog!, Config.MaxCommandsPerFrame)` (`Mod.Game.cs:261-277`) | every action not in `SimCommand.SolverActions` | ordinary mutations |
| **Solver** | `Mod.SolverDrainPrefix()` → `DrainSolverCommands()` → `queue.Drain(CommandPhase.Solver, …)` (`Mod.Game.cs:849-856, 940-950`), installed by `InstallSolverHook()`:<br>`var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));`<br>`var prefix = new HarmonyMethod(typeof(Mod), nameof(SolverDrainPrefix)) { priority = Priority.First };`<br>`_solverHarmony.Patch(original, prefix: prefix);` (`Mod.Game.cs:859-880`) | `debug.refill_fuel`, `debug.refill_battery`, `debug.teleport`, `debug.impulse`, `vessel.attitude_mode`, `vessel.attitude_frame`, `vessel.attitude_target`, `vessel.burn`, `vessel.rcs_mode` (`gatOS.SimFs/Commands/SimCommand.cs:74-84`) | (1) resource state must be solver-visible the same tick; (2) `Vehicle.Teleport` detaches from a `PhysicsBubble` whose ownership lives on the solver worker since KSA 5348 — the prefix runs after `VehicleSolver.Wait()` and before the job is re-queued, "the one provably safe window"; (3) KSA's async solver **snapshots and restores `FlightComputer` every frame (`FlightComputer.CopyFrom`)**, so a Frame-phase FC write "flashes on, then reverts to manual" (`SimCommand.cs:47-73`, `AGENTS.md:536-545`) |
| **Post-solver** | `Mod.DriveWelds`/`DriveIvaPhysics` in `[StarMapAfterGui]` | per-frame re-posing (welds teleport every frame; IVA cabin physics) | both call `JobSystems.VehicleSolver.Wait()` first so kinematics are settled (`Game/Ksa/Welds/WeldManager.cs:97-110`, `AGENTS.md:546-573`) |

Phase is **derived from the action key** — `public CommandPhase Phase => PhaseFor(Action);` / `public static CommandPhase PhaseFor(string action) => SolverActions.Contains(action) ? CommandPhase.Solver : CommandPhase.Frame;` (`SimCommand.cs:87-96`) — never set at a construction site.

### 1.3 The queue (`gatOS.SimFs/Commands/CommandQueue.cs`) — verbatim API

```csharp
public interface IPostObserver { void OnCommandResult(int token, CommandResult result); }   // runs INLINE on the game thread inside Drain; must be trivial

public sealed class CommandQueue : ICommandSink
{
    public CommandQueue(bool controlEnabled, bool debugEnabled, TimeSpan timeout);   // timeout ≤ 0 → 2 s
    public bool ControlEnabled { get; }
    public bool DebugEnabled { get; }
    public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct);                 // enqueue by command.Phase, await with timeout
    public Task<CommandResult> SubmitBatchAsync(IReadOnlyList<SimCommand> commands, CancellationToken ct); // one Pending, same tick; all commands must share one phase (else Invalid)
    public void Post(SimCommand command, IPostObserver? observer = null, int token = 0);             // fire-and-forget; phase mixing across posts is free
    public int Drain(CommandPhase phase, ICommandExecutor executor, int maxCommands);               // GAME THREAD ONLY
}
```

Mechanics worth copying (`CommandQueue.cs:31-212`):
- Two `ConcurrentQueue<Pending>` (`_frame`, `_solver`); a `Pending` holds `SimCommand[] Commands`, an optional `TaskCompletionSource<CommandResult>` created with `TaskCreationOptions.RunContinuationsAsynchronously` ("the awaiting transport thread must never resume inline on the game thread inside Drain"), optional observer+token, and `volatile bool Abandoned`.
- `EnqueueAsync`: `ControlEnabled==false` → immediate `Denied`; `await pending.Completion!.Task.WaitAsync(_timeout, ct)`; on `TimeoutException` sets `Abandoned = true` and returns `TimedOut` ("the game thread did not execute the command in time (paused or loading?)"); `OperationCanceledException` propagates (write flushed/clunked).
- `Drain`: dequeues up to `maxCommands` (config `max_commands_per_frame`, default 64, `Configuration/GatOsConfig.cs:282`), skips `Abandoned`, executes a whole batch group even if it overshoots the bound, catches executor throws as `Fault`, returns the **first** failure for a batch annotated `command i/n (action): msg`, completes the TCS and calls the observer inline.
- Budget/timing: `command_timeout_ms` default 2000 (`GatOsConfig.cs:279`); drain time measured via `PerfStat _drainStats` (`Mod.cs:133`).

Executor contract: `public interface ICommandExecutor { CommandResult Execute(SimCommand command); }` — "must never throw (faults are returned as CommandOutcome.Fault)" (`Commands/ICommandExecutor.cs`). Sink contract: `public interface ICommandSink { bool ControlEnabled {get;} bool DebugEnabled {get;} Task<CommandResult> SubmitAsync(SimCommand, CancellationToken); async Task<CommandResult> SubmitBatchAsync(...) /* default sequential fallback */ }` (`Commands/ICommandSink.cs`).

### 1.4 The generic `GameThread` scheduler in the ksa skill (NOT used by gatOS, but it is the shared-lib idiom)

From `.claude/skills/ksa/lifecycle.md` ("GameThread scheduler — mutating game state from off-thread") and the sibling source `/Users/asherwin/repos/meow-sci/unscience_newux/ksa-abstractions.lib/{GameThread,IGameStateScheduler,GameStateQueue}.cs`:

```csharp
namespace MeowSci.KsaAbstractions;
public interface IGameStateScheduler { Task Schedule(Action action); Task<T> Schedule<T>(Func<T> func); }
public static class GameThread { public static IGameStateScheduler Scheduler { get; } /* singleton GameStateQueue */ public static void DrainOnGameThread(); }
public sealed class GameStateQueue : IGameStateScheduler   // ConcurrentQueue<WorkItem>; TCS RunContinuationsAsynchronously; exception → tcs.SetException; DrainOnGameThread() runs all in enqueue order, no budget
```

Drain point by convention: a submod's `Update(dt)` called from `[StarMapBeforeGui]`. gatOS deliberately replaced this with the typed `CommandQueue` (immutable `SimCommand`, phases, timeout, per-frame budget, batch atomicity, observer path) — the scripting mod should do the same for cross-thread callers, but in-process scripts ticked on the game thread need neither.

### 1.5 How results get back

`CommandResult(CommandOutcome Outcome, string? Message)` with `Ok`, `IsSuccess`, `ToErrno()`; `CommandOutcome` ∈ `Ok, Invalid(EINVAL/400), NotFound(ENOENT/404), Denied(EACCES/403), Busy(EBUSY/409), Fault(EIO/500), TimedOut(ETIMEDOUT/504), Unsupported(EOPNOTSUPP/501)` — single source of truth `Commands/CommandResult.cs:10-85`; HTTP mapping `SPEC_9P_FILESYSTEM.md:98-119`. A 9P write throws `VfsErrorException(result.ToErrno(), ...)` so `echo` carries the real errno on the failing `write(2)` (`Commands/CommandFile.cs:57-68`). JSON transports return `{"errno":"EINVAL","message":…}`. Fire-and-forget posts report through `IPostObserver` into `ctl/schedules/<id>/last_error` + `/sim/events`.

---

## 2. Snapshot / telemetry model

### 2.1 Cadence and gating (`gatOS.GameMod/Game/TelemetrySampler.cs`)

- `internal void Tick(double dt, bool active)` (`:141-166`): if `!active || !_settings.Enabled` → `_clock.Reset()` and return. `active` = VM Starting/Running **or** any 9P/HTTP/MQTT session or MCP request in flight (`Mod.Game.cs:240-246`) — the sampler idles for free when nobody can read.
- Rate: `TelemetrySettings.SampleRateHz` (volatile int, clamped 1..120, default 10; `gatOS.SimFs/TelemetrySettings.cs`). `SampleClock.Tick(dt)` accumulates frame dt, fires once per interval, **drops** backlog > 1 interval ("never burst-replayed"), keeps sub-interval phase (`gatOS.SimFs/Telemetry/SampleClock.cs`). So the sampler is **frame-driven, rate-limited** — not a timer thread.
- Bodies sub-cadence: `telemetry_bodies_rate_hz` (0 = every tick); between ticks the **same** `IReadOnlyList<BodySnapshot>`/`SystemSnapshot` are republished by reference (`SampleBodiesPaced`, `:304-316`) — consumers can reference-compare for "unchanged".
- Gates read every tick: `Enabled, VesselDetail, VesselParts, Bodies, Events, BodiesRateHz` (all `volatile`, menu-mutable; `TelemetrySettings.cs:28-34`).

### 2.2 What one `Sample()` does (`TelemetrySampler.cs:179-273`)

1. `ut = Universe.GetElapsedSeconds()`, `warp = Universe.SimulationSpeed`, `activeId = Program.ControlledVehicle?.Id`.
2. `foreach (var astronomical in Universe.CurrentSystem.All.UnsafeAsList()) if (astronomical is Vehicle vehicle)` → `VesselReader.Sample(vehicle, activeId, ut, detail)` inside **its own try/catch** (a mid-teardown vehicle must not kill the loop; logged once); `if (_settings.VesselParts) vessel = vessel with { Parts = PartsReader.Sample(vehicle, ut) }`.
3. `VesselForceRender.Prune(vessels)`; `_camera?.Prune(vessels)` — despawn cleanup rides the enumeration.
4. `(bodies, systemSummary) = SampleBodiesPaced(system)` if `Bodies`.
5. `events = _settings.Events ? EventDiffer.Diff(_previous, ut, warp, activeId, vessels) : []`, then fold in drained feature events: `_audio?.DrainEvents()`, `_iva.DrainEvents()`, `_schedules?.DrainEvents()`, `_camera?.DrainEvents()`, `_stickers?.DrainEvents()` (drained unconditionally so queues never pile up).
6. `new SimSnapshot(++_sequence, ut, warp, activeId, vessels, events, GameVersion(), _appliedRateHz, _health.Snapshot()) { SimDtSeconds = Universe.GetLastSimStep().DeltaTime, WarpSpeeds = Universe.GetSimulationSpeeds() (cached), AutoWarpActive = Universe.IsAutoWarpActive, AutoWarpTargetUt = Universe.AutoWarpTime?.Seconds(), Bodies, System, Welds = _welds.Snapshot(), AlwaysRenderIva = IvaForceRender.Enabled, ThugLife = _thugLife.Snapshot(), FaceFxLive, Iva = _iva.Snapshot(_ivaStats), FxEditors = _debugNamespace ? FxEditorReader.Sample(_health) : null }`.
7. `_previous = snapshot; _store.Publish(snapshot);`

### 2.3 Readers (the KSA-facing half)

**`Game/Ksa/Readers/VesselReader.cs`** — `internal static VesselSnapshot Sample(Vehicle vehicle, string? activeVesselId, double utSeconds, bool detail)`. Two single-pass builders: `BuildCore` (always) and `BuildFull` (G3 detail, guarded — on any throw it falls back to core and logs once, `:41-61`). Core reads (`ReadBasics`, `:82-135`): `vehicle.Parent` (IParentBody), `GetPositionCci()`, lat/lon via `double3.Transform(positionCci, parent.GetCci2Ccf())` + `parent.GetLlaFromCcf(positionCcf)`, `GetBody2Cci()`, `BodyRates`, `OrbitalSpeed`, `GetSurfaceSpeed()`, `GetInertialSpeed()`, `GetBarometricAltitude()`, `GetRadarAltitude()`, `TotalMass`, `InertMass`, `PropellantMass`, `Situation` (via `EnumText.Of`), battery (`Parts.Batteries.GetState(b).Charge.Value()` / `b.MaximumCapacity.Value()`), `IsControllable`, `vehicle is KittenEva`, `IsSet(VehicleEngine.MainIgnite, false)`, `LightsOn`, `ScaleActuator.Read`, `VesselForceRender.IsMarked`. Detail adds: `GetPositionEcl()`, `GetVelocityCci()`, `CenterOfMassAsmb`, `NavBallData{AttitudeAngles,ThrustWeightRatio,DeltaV,Frame,Speed}`, `PhysicsEnvironment{AtmosphericPressure,AtmosphericDensity,OceanDensity,TerrainRadius}` + `PhysicalAtmosphereReference.GetDynamicPressure(vehicle)` + `AccelerationBody`/`AngularAccelerationBody`, power produced/consumed (accumulated in the solar/generator passes), `GetManualThrottle()`, translate/rotate flag decode (`GetThrusterFlags()`), `FlightComputer.{AttitudeMode,AttitudeTrackTarget,AttitudeFrame,RCSMode}`, per-module lists (engines, tanks, SRBs, RCS, solar, generators, lights, docking, decouplers, encounters, animations). The SoA state idiom:

```csharp
private static bool TryStates<TModule, TState, TGlobal, TFx>(Vehicle vehicle, out ModuleStateful<TModule, TState, TGlobal, TFx>.StateList states)
    where TModule : ModuleStateful<TModule, TState, TGlobal, TFx>, IDisposable where TState : unmanaged where TGlobal : unmanaged where TFx : struct
    => ModuleStateful<TModule, TState, TGlobal, TFx>.TryGetFrom(vehicle.Parts.States, out states);
// then: ref readonly var st = ref states.GetState(module);
```
(`VesselReader.cs:973-979`; used with `EngineController/EngineControllerState/EngineControllerGlobalState/EmptyStruct`, `ThrusterController/ThrusterControllerState/ThrusterControllerGlobalState`, `SolarPanel/SolarPanelState`, `SolarTracker/SolarTrackerState`, `Generator/GeneratorState`, `KeyframeAnimationModule/KeyframeAnimationModule.State`.)

**`Readers/PartsReader.cs`** — `IReadOnlyList<PartSnapshot> Sample(Vehicle, double ut)`; `ConditionalWeakTable<Vehicle, CacheEntry>`; rebuilt when `vehicle.Parts.Count` changes or every 10 s; reads `Part.{InstanceId,Id,DisplayName,Template.Id,PartParent,SubParts,PositionVehicleAsmb}` for top-level parts and nested subparts.

**`Readers/BodyReader.cs`** — `(IReadOnlyList<BodySnapshot>, SystemSnapshot?) Sample(CelestialSystem)`; per-body statics (`Id, Class, ParentId, ChildIds, Mass, MeanRadius, Mu, SphereOfInfluence, GetAngularVelocity(), Orbit elements, Atmosphere{Height,ScaleHeight,SeaLevelPressure,SeaLevelDensity}, Ocean{Density}`) cached in `ConditionalWeakTable`s keyed by `Celestial`/`StellarBody`/`Barycenter`; per tick only `GetPositionEcl()`/`GetVelocityEcl()`; `SystemSnapshot(primaryStar = system.GetWorldSun()?.Id, HomeBody?.Id, primaryStar)` cached per system. Children filtered to `IParentBody` (vehicles excluded).

**`Readers/AnimationLinks.cs`** — per-vehicle cache of animation↔solar/light structural links (`KeyframeAnimationModule.Parent.SubtreeModules.HasAny<SolarPanel>()`, `SolarPanel.KeyframeAnimationModule`, `LightModule.Parent.FullPart.SubtreeModules.Get<KeyframeAnimationModule>()`), rebuilt on module-count change or 10 s.

**`Camera/CameraReader.cs`** — `CameraStatus Sample(IGameViewport viewport, KSA.Camera camera, bool owned, in CameraPose pose, in CameraTarget anchor, double3 resolvedPositionEcl, CameraPlayback? player)`; reads `IGameViewport.{Mode,MapController.Scope}`, `Camera.{Following,TidalLocking,PositionEcl,LocalRotation,GetFieldOfView() (radians!),Orthographic}`; published through `CameraStore.PublishStatus` (volatile swap), **not** inside `SimSnapshot`.

**`Fx/FxEditorReader.cs`** — the four FX-editor families, memoized; re-read on a `/sim` write or every 2 s.

### 2.4 DTO shapes (`gatOS.SimFs/Snapshots/SimSnapshot.cs`)

All `sealed record`s, immutable, plain finite doubles (every value passes `Sanitize.Finite` → NaN/±Inf become `0`; `gatOS.SimFs/Telemetry/Sanitize.cs`).

```csharp
public sealed record SimSnapshot(long Sequence, double UtSeconds, double WarpFactor, string? ActiveVesselId,
    IReadOnlyList<VesselSnapshot> Vessels, IReadOnlyList<SimEvent> NewEvents, string GameVersion, double SampleRateHz,
    IReadOnlyList<AccessorHealthSnapshot> Accessors)
{ SimDtSeconds; WarpSpeeds; AutoWarpActive; AutoWarpTargetUt; Bodies; System; Welds; AlwaysRenderIva; ThugLife; FaceFxLive; Iva; FxEditors; static Empty }

public sealed record VesselSnapshot(string Id, string Name, string Situation, double3Snap PositionCci, double LatitudeDeg, double LongitudeDeg,
    double OrbitalSpeed, double SurfaceSpeed, double InertialSpeed, QuatSnap AttitudeBody2Cci, double3Snap BodyRatesRadS,
    double BarometricAltitude, double RadarAltitude, double MassTotal, double MassDry, double MassPropellant, OrbitSnapshot? Orbit,
    IReadOnlyList<EngineSnapshot> Engines, IReadOnlyList<TankSnapshot> Tanks, double? BatteryChargeFraction, string? ParentBodyName,
    bool LightsMasterOn, IReadOnlyList<AnimationSnapshot> Animations)
{ PositionEcl; VelocityCci; CenterOfMass; Controlled; Controllable; IsKitten; Navball; ThrottleCmd; Scale=1.0; AlwaysRender; AttitudeMode; AttitudeFrame; RcsMode; RcsOn;
  TranslateCmd; RotateCmd; EngineOn; Environment; PowerProducedW; PowerConsumedW; BatteryCapacityJoules; Rcs; Solar; Generators; Lights; Docking; Decouplers; Srb; Encounters; Parts }

OrbitSnapshot(apoapsisAlt, periapsisAlt, ecc, incDeg, sma, period){LanDeg, ArgPeDeg, TrueAnomalyDeg, TimeToApoapsis, TimeToPeriapsis, NextPatchEventUt}
EngineSnapshot(int Index, bool Active, double VacThrustN, double IspS){MinThrottle, ThrottleCmd, PropellantAvailable}
TankSnapshot(string Resource, double Amount, double Capacity){Fraction}
RcsSnapshot(int Index, bool Active, bool PropellantAvailable, string ControlMap)
SolarSnapshot(Index, ProducedW, Occluded, SunAoaDeg, Efficiency, HasTracker, TrackerAngleDeg, AnimationIndex)
GeneratorSnapshot(int Index, bool Active, double ProducedW)
LightSnapshot(Index, On, Intensity, double3Snap Rgb, AnimationIndex){OuterAngleDeg, InnerAngleDeg}
DockingSnapshot(int Index, bool Docked, string? DockedToPart){PushoffImpulseNs}
DecouplerSnapshot(int Index, bool Fired, bool Enabled = true)
SrbSnapshot(int Index, bool Active, double MassKg, double MassInitialKg){EngineIndex, PartInstanceId, Substance, Grain, GrainShape, StackValid, StackError, PropellantAvailable, MassUnburnableKg, MassBurnableKg, Fraction, MassFlowKgS, BurnTimeRemainingS, ChamberPressurePa, ChamberTemperatureK, ExitPressurePa, ExitTemperatureK, BurningAreaM2, AreaRatio, Segments}
SrbSegmentSnapshot(Index, MassKg, MassInitialKg){PartInstanceId, Substance, Grain, MassUnburnableKg, Fraction, RadiusM, LengthM, VolumeM3, BurnDepthM}
EncounterSnapshot(string Body, double Ut, double DistanceMeters)
PartSnapshot(index, instance_id(uint), id, display_name, template, is_root, subpart_count, double3Snap position_vehicle_asmb){Subparts}
SubpartSnapshot(index, instance_id, id, display_name, template, position)
NavballSnapshot(pitch, yaw, roll (int deg), twr, deltav, frame, speed)
EnvironmentSnapshot(pressurePa, densityKgM3, dynamicPressurePa, oceanDensity, terrainRadius, double3Snap accelBody, double3Snap angularAccelBody, gForce)
BodySnapshot(Id, Class, ParentId, ChildIds, Mass, MeanRadius, Mu, SoiMeters, RotationRateRadS, PositionEcl, VelocityEcl, Orbit?, Atmosphere?, Ocean?)
AtmosphereSnapshot(heightM, scaleHeightM, seaLevelPressurePa, seaLevelDensity); OceanSnapshot(double DensityKgM3)
SystemSnapshot(string Name, string? HomeBodyId, string? SunId)
AnimationSnapshot(Index, GoalFraction, CurrentFraction, DeploymentState, IsSolar)
AccessorHealthSnapshot(string Name, double SinceUtSeconds, string Error)
SimEvent(double UtSeconds, string Type, string? VesselId, string Detail)
```

### 2.5 Units & frames (as published; `SPEC_9P_FILESYSTEM.md §8:1819-1843`, `docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`)

- Lengths m; speeds m/s; mass **kg**; μ m³/s²; thrust N; Isp s; time/UT s; power W; energy J; pressure Pa; density kg/m³.
- Angles in files: **degrees** (lat, lon, inc, lan, argpe, true anomaly, navball, sun AoA, light cone angles); body rates/angular accel **rad/s**, **rad/s²**; `bodies/<id>/rotation_rate` rad/s. KSA stores `Orbit.Inclination`/LAN/ArgPe in **radians** (converted on read); `Camera.GetFieldOfView()` returns radians while `SetFieldOfView(float)` takes degrees.
- Frames: `position/cci` = Celestial-Centered Inertial about `parent` (Z = parent spin axis north, X = vernal point); `position/ecl`, bodies = system ECL; `attitude/quat` = **Body→CCI** `x y z w`; KSA body frame = **X-nose / Y-right / Z-down**; `com`/parts positions = vehicle **assembly** frame; `environment/accel` body frame.
- Apsides: KSA `Orbit.Apoapsis/Periapsis` are **radii from centre**; `/sim` publishes **altitudes** (`Sanitize.RadiusToAltitude`), apoapsis `0` when `!Orbit.IsBound()` (hyperbolic `a·(1+e)` is a finite negative leak otherwise). `UniverseTime` sentinels (`EndOfTime` = Int128.Max ns ≈ 1.7e29 s) are mapped to `0` via `IsSaturated()` (`VesselReader.cs:362-378`).

### 2.6 Consistency & consumer model

- `SnapshotStore` (`gatOS.SimFs/Snapshots/SnapshotStore.cs`): `volatile SimSnapshot _current`; `Publish(snapshot)` = reference swap then `Interlocked.Exchange(ref _signal, NewSignal()).SetResult()`; `ValueTask<SimSnapshot> WaitForNextAsync(long afterSequence, CancellationToken ct)` captures-and-rechecks so a racing publish is never missed; intermediate snapshots are skipped, never replayed. Readers never lock; one snapshot = one self-consistent sample (the "atomic `telemetry` document", `SPEC §4`).
- Cost: measured live, not fixed — `PerfStat _sampleStats` (avg/max/last µs) and `ValueStat _sampleAllocStats` (bytes allocated per sample via `GC.GetAllocatedBytesForCurrentThread` delta — the GP3 "zero steady-state alloc" tripwire) shown in the status window (`Mod.Game.cs:1214-1236`, `docs/ARCHITECTURE.md:196-210`). Scaling: per vessel × (core reads + per-module spans) with the detail pass as "the single most expensive per-vessel work" (`TelemetrySettings.cs:70-74`); parts and bodies statics are cached so they are ~free per tick. 9P/HTTP/MCP consumers are lazy (per-snapshot memoized formatting); MQTT is the one eager pusher (subscription-gated, changed-only).

---

## 3. Command / actuator model

### 3.1 The envelope (`gatOS.SimFs/Commands/SimCommand.cs`)

```csharp
public enum CommandPhase { Frame, Solver }
public sealed record SimCommand(string VesselId, string Action, int Ordinal, double Value)
{
    public const int NoOrdinal = -1;
    public static CommandPhase PhaseFor(string action);
    public CommandPhase Phase => PhaseFor(Action);
    public IReadOnlyList<double>? Values { get; init; }   // vector payloads (quat 4, burn 4, color 3, teleport 6, impulse 3, audio slots…)
    public string? Token { get; init; }                   // enum tokens, target ids, clip names, impulse frame kw, FX entity id
    public string? Aux { get; init; }                     // second string: audio channel id=, impulse unit kw, FX field path, sticker anchor descriptor
}
```
Addressing modes (`AGENTS.md:736`): **global** (`VesselId == ""`), **registry-keyed** (`Ordinal` = entry id + `Token`/`Aux`), **path-implied per-vessel** (`VesselId`, module index in `Ordinal`). JSON transports require `vessel_id` to be a string (`""` for global) (`SPEC §5.2:1635-1649`). Action-key constants: `gatOS.SimFs/Commands/SimActions.cs` (the full list is §4.7 below).

### 3.2 From a write to a command (parse layer, game-free)

| Archetype | Class | Accepts | Builds |
|---|---|---|---|
| STATE flag/fraction/number/ranged | `ControlFile.Flag/Fraction/Number/Ranged(name, qid, sink, read, [min,max,] Func<double,SimCommand> build)` | `double.TryParse` invariant, finite; `0|1` / `[0,1]` / any / `[min,max]` else EINVAL | `build(value)` |
| TRIGGER | `TriggerFile(name, qid, sink, SimCommand command, Func<string>? read, string token="1")` | exactly the fire token | the fixed command |
| vector / enum / token / line | `VectorControlFile`, `EnumControlFile`, `TokenControlFile`, `LineControlFile` (+ `<Feature>Rules` game-free validators: `TranslateRules`, `RotateRules`, `ImpulseRules`, `ScaleRules`, `CameraRules`, `FaceFxRules`, `StickerRules`, `FxCatalog`) | per grammar | — |
| BATCH | `BatchFile` (`/sim/ctl/batch`), `TimedBatchFile` (`/sim/ctl/timed_batch`) | `<path> <value>` lines + `commit`; resolves each path to a `CommandFile` and calls `internal SimCommand? ParseToken(string token)` | `SubmitBatchAsync` / `ScheduleStore.Submit` |
| JSON (HTTP/MQTT/MCP) | `CommandCatalog.Validate(SimCommand)`, `CommandBatchBuilder.Build(IReadOnlyList<SimCommand>)` (≤ 64, one phase), `ScheduleBuilder.Build(ScheduleStore, ScheduleDefinition)` (`Commands/StructuredCommands.cs`) | typed envelope; **bypasses the file grammars** — which is why every actuator re-validates game-side | — |

`CommandFile` (`Commands/CommandFile.cs`): reads snapshot the value per open (`Open() => new ReadHandle(_read()+"\n")`); writes are line-buffered — the first `\n` triggers `ExecuteLineAsync` → `Parse` (null ⇒ `VfsErrorException(EINVAL)`) → `_sink.SubmitAsync` → non-success ⇒ `VfsErrorException(result.ToErrno())`; an unterminated write actuates best-effort on clunk (fire-and-forget, errno only logged).

### 3.3 The executor/router — `Game/Ksa/KsaCatalog.cs` (`internal sealed class KsaCatalog(...) : ICommandExecutor`)

`Execute(SimCommand command)` (`:30-135`), in order:
1. `accessor = $"actuator.{command.Action}"`; if `health.IsDegraded(accessor)` → `Unsupported "latched degraded"`.
2. **Vessel-agnostic families routed before any vehicle resolution** (and exempt from the authority gate): `debug.warp` → `DebugActuator.SetWarp(Value)`; `debug.always_render_iva` → `IvaActuator.SetAlwaysRender`; `debug.weld_clear` → `welds.Clear()`; `debug.thug_life*` → `ThugLife(c)`; `debug.fx_*` → `FaceFx(c)`; `debug.iva_*` → `Iva(c)`; `debug.engineplume*`/`plumetrail*`/`clouds*`/`terrain*` → `PlumeActuator`/`TrailActuator`/`CloudActuator`/`TerrainActuator` (via `FxField` re-validation against `FxCatalog`); `audio.*` → `AudioActuator.Execute` (or `Unsupported "audio is disabled"`); `schedule.*` → `ScheduleStore.Execute` (game-free); `camera.*` → `Camera(c)` (`camera.focus` always works via `Universe.CurrentSystem.Get(id)` → `CameraActuator.Focus`; the rest need the `CameraDirector`); `paint.*` → `PaintManager.Execute`.
3. `targetId = Action == debug.control_vessel ? (Token ?? VesselId) : VesselId`; `ResolveVehicle(id)` = linear scan of `Universe.CurrentSystem.All.UnsafeAsList()` for `Vehicle.Id == id` (null → `NotFound "vessel '<id>' is gone"`).
4. Authority gate (G-D1): `anyVessel = isDebug || AnyVesselActions.Contains(action)` where `AnyVesselActions = { vessel.scale, vessel.always_render }`; `if (!anyVessel && !allVessels && Program.ControlledVehicle?.Id != vehicle.Id) → Denied "control is restricted to the active vessel"` (`control_all_vessels`, default true).
5. `Dispatch(vehicle, c)` switch (`:153-211`) → the actuator; `Finish()` clears the health latch on success.
6. Any exception → `health.Fault(accessor, SafeUt(), ex.Message)` and `Fault(ex.Message)` — **never throws**.

### 3.4 Actuator catalog (every actuator, with the exact KSA members)

Notation: *one-shot* = applied once per write; *latched setpoint* = KSA state persists until re-written; *driver* = gatOS re-applies every frame.

| Actuator (file under `Game/Ksa/`) | Action key(s) → method | Params / validation | KSA members called | Persistence |
|---|---|---|---|---|
| `Actuators/EngineActuator.cs` | `vessel.ignite` → `Ignite(Vehicle)`; `vessel.shutdown` → `Shutdown`; `vessel.engine` → `SetEngineOn(v, Value>0.5)` | trigger `1` / flag | `vehicle.SetEnum(VehicleEngine.MainIgnite)` / `SetEnum(VehicleEngine.MainShutdown)` (sets `_manualControlInputs.EngineOn`) | latched game state (one-shot call) |
| | `engine.active` → `SetActive(v, ordinal, bool)` | ordinal ∈ `[0, Modules.Get<EngineController>().Length)` else NotFound | `engines[ordinal].SetIsActive(vehicle, active)` | latched |
| | `engine.min_throttle` → `SetMinThrottle(v, ordinal, fraction)` | clamp 0..1 | `EngineController.MinimumThrottle = (float)…; vehicle.Parts.MarkDerivedDirty(DerivedData.RocketControls)` | latched |
| `Actuators/ThrottleActuator.cs` | `vessel.throttle` → `Set(v, fraction)` | clamp 0..1; `Unsupported` if field missing | **reflection**: `typeof(Vehicle).GetField("_manualControlInputs", Instance|NonPublic)` → box struct → `GetField("EngineThrottle", Instance|Public).SetValue((float)…)` → write box back | latched setpoint (KSA since 5497 caps applied throttle at 90% MaxGLoad) |
| `Actuators/StagingActuator.cs` | `vessel.stage` → `Stage(v)` | trigger | `vehicle.Parts.SequenceList.ActivateNextSequence(vehicle); vehicle.UpdateAfterPartTreeModification();` | one-shot (per-module since 5348: only `ISequenced` = `EngineController`, `Decoupler`, + parachutes; **not** RCS) |
| `Actuators/RcsActuator.cs` | `vessel.rcs` → `SetMaster(v,bool)`; `rcs.active` → `SetActive(v, ordinal, bool)` | ordinal bounds | `foreach ThrusterController t in Parts.Modules.Get<ThrusterController>() t.SetIsActive(vehicle, on)` | latched |
| `Actuators/TranslateActuator.cs` | `vessel.translate` → `SetTranslation(v, IReadOnlyList<double> axes)` | `TranslateRules.Validate` (3 finite) | reflection on `_manualControlInputs.ThrusterCommandFlags` (`ThrusterMapFlags`): replaces only translate bits; +X=`TranslateForward`, +Y=`TranslateRight`, +Z=`TranslateDown`; read-back `vehicle.GetThrusterFlags()` | **latches like a held key** (`0 0 0` stops) — but KSA clears it on vessel switch, focus loss, camera-mode switch, ImGui keyboard focus, warp > 30× (`Vehicle.ClearHeldPlayerInput()`); dead while `FlightComputer.RCSMode != Enabled` |
| `Actuators/RotateActuator.cs` | `vessel.rotate` → `SetRotation(v, axes)` | `RotateRules.Validate` | same field, rotation bits: +X=`RollRight`, +Y=`PitchUp`, +Z=`YawRight`; auto attitude strips rotation bits (`WithNoRotation()`), so full authority needs `attitude_mode=manual` | latched (same caveats) |
| `Actuators/FlightComputerActuator.cs` | `vessel.attitude_mode` → `SetAttitudeMode(v, token)` | `"manual"` or `Enum.TryParse<FlightComputerAttitudeTrackTarget>` (ignore case) | `fc.AttitudeMode = Manual` / `fc.AttitudeMode = Auto; fc.AttitudeTrackTarget = target` | latched **Solver** |
| | `vessel.attitude_frame` → `SetAttitudeFrame(v, token)` | `Enum.TryParse<VehicleReferenceFrame>` | `fc.AttitudeFrame = frame` | latched Solver |
| | `vessel.rcs_mode` → `SetRcsMode(v, token)`; `ReadRcsMode(v)` | `Enum.TryParse<FlightComputerRCSMode>` | `vehicle.FlightComputer.RCSMode = mode` | latched Solver (copied by `CopyFrom`) |
| | `vessel.attitude_target` → `SetAttitudeTarget(v, quat[4])` | count 4 | `frame2Cci = VehicleReferenceFrameEx.GetEclBody2Cci(vehicle.Orbit.Parent.GetCce2Cci()); fc.AttitudeMode=Auto; fc.AttitudeTrackTarget=Custom; fc.AttitudeFrame=EclBody; fc.CustomAttitudeTarget = frame.QuaternionToEulerAngles(doubleQuat.Concatenate(target2Cci, frame2Cci.Inverse()))` (KSA recomputes Target2Cci from the euler form every solver step, so a direct Target2Cci write is discarded) | latched Solver |
| | `vessel.burn` → `SetBurn(v, [ut,dvx,dvy,dvz])` | count 4, finite ut (`UniverseTime` ctor throws on NaN) | `vehicle.FlightComputer.Burn = new BurnTarget { ImpulsiveInstant = new UniverseTime(ut), DeltaVTargetCci = new float3(...) }` | latched Solver; the autopilot executes it |
| `Actuators/LightActuator.cs` | `vessel.lights` → `SetMaster(v,bool)` | flag | `vehicle.LightsOn = on; foreach PowerConsumer c in Modules.Get<PowerConsumer>() if (c.LightSwitch) c.LightIsActive = on` | latched |
| | `light.on` → `SetOn(v, ordinal, bool)` | ordinal; `Unsupported` if no `LightSwitch` | `light.Parent.FullPart.LightSwitch.LightIsActive = on` | latched |
| | `light.brightness` → `SetBrightness(v, ord, value)`; `light.color` → `SetColor(v, ord, rgb[3])`; `light.outer_angle`/`light.inner_angle` → `SetOuterAngle`/`SetInnerAngle(v, ord, degrees)` | rgb clamp 0..1; outer clamped `[1e-5, 1.5697963]` rad; inner `[0, outer]` | `EnsureUnshared(light)` first (clones the **shared** `LightModule.Template` + its `Intensity/ColorRgb/OuterAngle/InnerAngle` reference objects via `RuntimeHelpers.GetUninitializedObject` + field copy, tracked in a `ConditionalWeakTable`), then `light.Template.Intensity.Value = …`, `ColorRgb.{R,G,B} = …; color.OnDataLoad(null!)`, `OuterAngle.Value/InnerAngle.Value` | latched (template is per-instance after clone) |
| `Actuators/AnimationActuator.cs` | `animation.goal` → `SetGoal(v, ordinal, fraction)` (also backs `solar/<n>/goal`, `lights/<n>/goal`) | ordinal into `Modules.Get<KeyframeAnimationModule>()`; clamp 0..1 | `module.TimeGoal = (float)(fraction * module.Shared.Duration)` — the sim integrates it, warp-correct | latched setpoint |
| `Actuators/DecouplerActuator.cs` | `decoupler.fire` → `Fire(v, ordinal)` | `IsActive` → Busy; `!IsEnabled` → Unsupported | `decoupler.SetIsActive(vehicle, true)` | one-shot, irreversible |
| `Actuators/DockingActuator.cs` | `docking.undock` → `Undock(v, ordinal)` | `!port.Docked` → Busy | `InputEvents.VehicleDockingInputBuffer.Add(new InputEvents.VehicleDockingInputData { Vehicle, DockingPort, Undock = true })` (KSA applies in its input phase → `DockingPort.Undock` → `Vehicle.Split(Connector, PushoffImpulse)`) | one-shot, deferred through KSA's input buffer |
| | `debug.docking_pushoff` → `SetPushoffImpulse(v, ordinal, impulse)` | `< 0` → Invalid | `ports[ordinal].PushoffImpulse = (float)impulse` (N·s) | latched |
| `Actuators/ScaleActuator.cs` | `vessel.scale` → `Set(v, factor)`; `Read(v)` | `ScaleRules.IsValid` (finite > 0) | recursive `part.Scale = new double3(f,f,f)` over `Parts.Parts`/`SubParts`; for `KittenEva` reflect `_renderable._characterAvatar.Core.Scale = f*0.01f` | one-shot (transform-only; KSA saves top-level scale) |
| `Render/VesselForceRender.cs` | `vessel.always_render` → `Set(v, bool)`; `IsMarked(id)`; `Prune(vessels)`; `Teardown()` | — | installs dynamic `Harmony("gatos.always_render")` prefixes on first mark (see §6) | **driver** (prefixes active while ≥ 1 marked) |
| `Actuators/DebugActuator.cs` | `debug.warp` → `SetWarp(factor)` | finite ≥ 0 | `Universe.SetSimulationSpeed(factor, alert: false)` | one-shot |
| | `debug.control_vessel` → `ControlVessel(target)` | — | `Program.GetMainCamera()?.SetFollow(target, tidalLocking: true, changeControl: false); Program.ControlledVehicle = target;` | one-shot |
| | `debug.teleport` → `Teleport(v, state[6])` | 6 finite; no parent → Busy | `Orbit.CreateFromStateCci(parent, Universe.GetElapsedTime(), pos, vel, default); vehicle.Teleport(orbit, null, null); vehicle.UpdatePerFrameData();` | one-shot, **Solver** |
| | `debug.impulse` → `Impulse(v, vector[3], frame, unit)` | `ImpulseRules.Validate`; `body` rotates by `GetBody2Cci()`; `ns` divides by `TotalMass` | same teleport pattern with `GetPositionCci()` + bumped `GetVelocityCci()` | one-shot, **Solver** |
| | `debug.refill_fuel` → `RefillFuel(v)` | — | `vehicle.RefillConsumables()` | Solver |
| | `debug.refill_battery` → `RefillBattery(v)` | — | `foreach battery: var m = batteries.GetModuleAndAllMutableStatesForInitialization(battery); m.Module.Refill(ref m.State);` | Solver |
| `Actuators/IvaActuator.cs` | `debug.always_render_iva` → `SetAlwaysRender(bool)` | — | `IvaForceRender.SetEnabled(on)` (flips `PartModelModule.Template.Internal` on all `PartModel.Instances`, dynamic ctor postfix) | driver (patch while on) |
| `Actuators/CameraActuator.cs` | `camera.focus` → `Focus(Astronomical)` | — | `Program.MainViewport.{MapCamera,BaseCamera}.SetFollow(target, tidalLocking:true, changeControl:false, alert:false)` | one-shot |
| | 27 `camera.*` actions → `Execute(SimCommand, CameraDirector)` | re-validated via `CameraRules`/`TargetRef.TryParse`; `Missing(ref)` ENOENT | `director.Take()/Release()/Restore()/SetMode/SetFollow/SetTidal/SetMapScope/ResetPose`, `state.SetOverride(CameraChannel.X, …)`, playback via `director.Playback.Execute(c)` | pose channels are **overrides composed every frame** by `CameraDirector.Update` (driver) |
| `Actuators/AudioActuator.cs` (`internal sealed class AudioActuator(AudioStore store, int maxChannels)`) | `audio.play/set/stop` → `Execute(SimCommand)`; `Tick()`; `Shutdown()` | 7-slot values; clip lookup Missing/Uploading | `GameAudio.System.TryCreateSound(bytes, Mode.OpenMemory|_2d|CreateSample/CreateCompressedSample, in exInfo, out sound)`, `GameAudio.System.TryPlaySound(sound, GameAudio.GetChannelGroup(ChannelGroupType), paused:true, out channel)`, `Channel.TrySet{Position,Mode,LoopCount,LoopPoints,Volume,Pan,Pitch,Paused}`, `TryIsPlaying/TryGetPosition/TryStop`, `Sound.TryRelease` | driver (per-frame tick prunes/enforces `end=`, publishes status) |
| `Welds/WeldManager.cs` + `WeldEngine.cs` | `debug.weld_create/weld_here/weld_remove/weld_enable/weld_clear` → `welds.Create(source, target, partIid, pos, rot, lock)` / `CreateAtCurrentPose` / `Remove(id)` / `SetEnabled` / `Clear()` | arity 8 / 2; same parent body (else Busy) | per frame: `JobSystems.VehicleSolver.Wait()` then `WeldEngine.UpdateWeld` → `Orbit.CreateFromStateCci(parent, Universe.GetJobSimStep(dt).NextTime, …)`, `source.Teleport(orbit, body2Cce, rates)`, `UpdatePerFrameData()`; anchors via `Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}` | **driver** (every frame, post-solver) |
| `ThugLife/ThugLifeManager.cs` | `debug.thug_life_*` | ids = smallest free slot | anchor re-resolve per frame (`Vehicle.Parts.Parts`, `Part.InstanceId`); draws via `SuperMeshRenderSystem.RenderMainPass` postfix + Vulkan quad | driver + render patch while ≥ 1 entry |
| `Iva/IvaPhysicsManager.cs` | `debug.iva_physics/run_outside_iva/adopt/adopt_all/release/nudge/clear` | SubParts only; caps | gatOS-owned BepuPhysics `Simulation`; writes `Part.{PositionParentAsmb,Asmb2ParentAsmb}` on driven SubParts; reads accel/rates/CoM | driver (post-solver) |
| `Fx/FaceFxManager.cs` | `debug.fx_spawn/fx_clear` | `FaceFxRules` | `Program.Instance.ParticleSystem.{EmitterPool.Get, InitializeEmitter}` | one-shot bursts, self-retiring |
| `Fx/{Plume,Trail,Cloud,Terrain}Actuator.cs` | `debug.{engineplume,plumetrail,clouds,terrain}_set/_reset(/_clear)` | `FxCatalog` field table (arity/range) | `VolumetricExhaustTemplate.Get(id)` fields; `VolumetricTrailRenderer` public fields + reflected settings; `CloudsReference` layer writes + `CloudLayerReference.OnDataLoad` + renderer repopulate; `PlanetRenderer.PlanetUbo` mapped-memory writes + `Celestial.BodyTemplate.HeightReference`; `FxPristine` records first-touch values for `reset` | latched (session-scoped; restored at unload) |
| `Paint/PaintManager.cs` (+ `ClutterTextureBridge`, `Stickers/StickerManager`) | `paint.*` (46 keys) | `PaintStore`/`TextureStore`/`StickerRules` | shader-source prefix + state-bit postfixes; `BindlessTextureLibrary.SetTexture/AddTexture/FreeTexture`; sticker decal pass | driver (`PaintManager.Tick` per frame) + dynamic patches while armed/live |

### 3.5 Per-frame drivers that an in-process host would have to replicate for "persistent" commands

| Driver | Called from | Self-gate | Thread/phase note |
|---|---|---|---|
| `CameraDirector.Update(dt)` | `PrepareMainViewportFrame` (viewport prefix) | `IsIdle` while unowned | writes `Camera.PositionEcl/LocalRotation/SetFieldOfView/SetOrthographic/SetOrthoHalfHeight` via an installed `CameraPoseController : FixedController`; postfix publishes applied transform |
| `AudioActuator.Tick()` | `DriveAudio` | `IsEmpty` | FMOD pumped by the game on this thread |
| `PaintManager.Tick()` | `DrivePaint` | masters / revision / `IsEmpty` | before scene render |
| `ThugLifeManager.Update()` | `UpdateThugLife` | `IsEmpty` | before scene render |
| `WeldManager.Update(dt)` | `DriveWelds` (`[StarMapAfterGui]`) | `IsEmpty` | after `JobSystems.VehicleSolver.Wait()` |
| `IvaPhysicsManager.Update(dt)` | `DriveIvaPhysics` | `IsIdle` | after `VehicleSolver.Wait()`; single-threaded Bepu |
| `ScheduleStore.{Activate,AdvanceAll,Tick}` | `TickSchedules` — *immediately before* the drain | `Count == 0` | three clock bases: `render` = `dtPlayer` (clamped by engine), `wall` = `Stopwatch` parked while empty, `ut` = sim-time delta clamped ≥ 0 (`Mod.Game.cs:381-438`) |

---

## 4. The `/sim` catalog, condensed (from `SPEC_9P_FILESYSTEM.md`, 2109 lines, 663 path rows)

Legend — **A**: S sensor (read-only), St state (read current / write setpoint), T trigger (write `1`), B batch, Sm stream (NDJSON), Smb binary stream, D dir. "Phase" only where Solver. Gates in parentheses. Vessel paths exist at both `vessels/by-id/<id>/…` and the alias `vessels/active/…`.

### 4.1 `/time`, `/system`, `/bodies`
| Path | A | Format / units |
|---|---|---|
| `time/ut` | S | seconds (`Universe.GetElapsedSeconds()`) |
| `time/warp` | S | factor |
| `time/sim_dt` | S | seconds advanced by last tick (`0` ⇒ paused) |
| `time/warp_speeds` | S | `f f f …` |
| `time/auto_warp` | S | `0` or `1 <ut>` |
| `time/alarm` | St | write target ut; **read parks** until reached, returns reached ut (`AlarmFile`) |
| `system/{name,home,sun}` (bodies) | S | ids |
| `bodies/<id>/{id,class,parent,children,mass,radius,mu,soi,rotation_rate}` | S | string/list/kg/m/m³s⁻²/m/rad·s⁻¹ |
| `bodies/<id>/position/ecl`, `velocity/ecl` | S | vector m, m/s (ECL) |
| `bodies/<id>/orbit/{apoapsis,periapsis,ecc,inc,lan,argpe,sma,period}` | S | altitude m (ap `0` if unbound), deg, m, s |
| `bodies/<id>/atmosphere/{present,height,scale_height,sea_level_pressure,sea_level_density}` | S | `1`, m, m, Pa, kg/m³ (absent if airless) |
| `bodies/<id>/ocean/{present,density}` | S | |
| `bodies/<id>/focus` | T | `camera.focus` (view-only, no authority gate) |

### 4.2 `/vessels/by-id/<id>/…` — core (always) and detail (`telemetry_vessel_detail`)
| Path | A | Format / units |
|---|---|---|
| `id`, `name`, `situation` (composite flags string), `parent` (CCI centre id), `controlled`, `controllable`, `is_kitten` | S | |
| `com` | S | vector m (assembly frame) |
| `scale` | St | finite > 0 → `vessel.scale` (any vessel) |
| `always_render` | St | 0/1 → `vessel.always_render` (any vessel) |
| `telemetry` | S | atomic JSON doc (§4 of SPEC: `seq ut warp id sit controlled controllable parent pos_cci pos_ecl vel_cci vel{orb,surf,inr} alt{baro,radar} mass{t,d,p} att_q orbit{ap,pe,ecc,inc,sma,period,ta,t_ap,t_pe} power{prod,cons,battery}`) |
| `position/{cci,ecl}` vector m; `position/{lat,lon}` deg | S | |
| `velocity/{orbital,surface,inertial}` m/s; `velocity/cci` vector | S | |
| `attitude/quat` (Body→CCI xyzw); `attitude/rates` rad/s | S | |
| `altitude/{barometric,radar}` m | S | |
| `mass/{total,dry,propellant}` kg (propellant includes SRB grain) | S | |
| `stream` | Sm | NDJSON `{seq,ut,sit,alt,vel,att,mass}` per sample (growing log, `tail -f`) |
| `orbit/{apoapsis,periapsis,ecc,inc,lan,argpe,sma,period,true_anomaly,time_to_ap,time_to_pe,next_patch}` (detail) | S | m, deg, s (`0` = none) |
| `navball/{pitch,yaw,roll}` int deg; `navball/{twr,deltav,frame,speed}` (detail) | S | |
| `environment/{pressure,density,dynamic_pressure,ocean_density,terrain_radius}` ; `environment/{accel,angular_accel}` vectors body frame; `environment/g_force` (detail) | S | Pa, kg/m³, Pa, kg/m³, m, m/s², rad/s², g |
| `power/{produced,consumed}` W; `battery/{charge,fraction,capacity}` 0..1, 0..1, J | S | |
| `engines/<n>/active` | St | 0/1 → `engine.active` |
| `engines/<n>/{vac_thrust,isp,throttle,propellant}` | S | N, s, 0..1, flag |
| `engines/<n>/min_throttle` | St | 0..1 → `engine.min_throttle` |
| `tanks/<resource>/{amount,capacity,fraction}` | S | kg, kg, 0..1 (liquids only) |
| `srb/<n>/{engine,part,substance,grain,grain_shape,segment_count,valid,error,active,propellant,mass,mass_initial,mass_unburnable,mass_burnable,fraction,mass_flow,burn_time,burning_area,chamber_pressure,chamber_temp,exit_pressure,exit_temp,area_ratio}` | S | read-only SRB state |
| `srb/<n>/segments/<m>/{part,substance,grain,mass,mass_initial,mass_unburnable,fraction,radius,length,volume,burn_depth}` | S | |
| `rcs/<n>/active` | St | 0/1 → `rcs.active` |
| `rcs/<n>/{propellant,map}` | S | flag, `Pitch|Yaw…` |
| `solar/<n>/{produced,occluded,sun_aoa,efficiency,tracker_angle,current,state}` | S | W, flag, deg, 0..1, deg, 0..1, `Deployed/Retracted/Deploying/Retracting/Broken` |
| `solar/<n>/goal` | St | 0..1 → `animation.goal` (only with a deploy animation) |
| `generators/<n>/{active,produced}` | S | flag, W |
| `lights/<n>/on` | St | 0/1 → `light.on` |
| `lights/<n>/brightness` | St | number → `light.brightness` |
| `lights/<n>/color` | St | `r g b` 0..1 → `light.color` |
| `lights/<n>/{outer_angle,inner_angle}` | St | deg → `light.outer_angle`/`light.inner_angle` |
| `lights/<n>/goal` (St → `animation.goal`), `current`, `state` | | only when the light part has an animation |
| `docking/<n>/{docked,docked_to,pushoff_impulse}` | S | flag, part id, N·s |
| `docking/<n>/undock` | T | `docking.undock` (EBUSY if not docked) |
| `decouplers/<n>/{fired,enabled}` | S | flags |
| `decouplers/<n>/fire` | T | `decoupler.fire` (EBUSY re-fire; EOPNOTSUPP disabled) |
| `animations/<n>/goal` | St | 0..1 → `animation.goal` |
| `animations/<n>/{current,state}` | S | |
| `encounters` (detail) | S | NDJSON `{"body","ut","distance"}` |
| `parts/json` (`telemetry_vessel_parts`) | S | whole part/subpart tree JSON |
| `parts/<n>/{instance_id,id,display_name,template,is_root,subpart_count,position}` | S | `instance_id` = stable handle |
| `parts/<n>/subparts/<m>/{instance_id,id,display_name,template,position}` | S | |
| `parts/<n>/paint/{enabled,color,clear}`, `…/subparts/<m>/paint/…` | St/T | `paint.part_*` (token = instance_id) |
| `paint/parts/{enabled,color,clear}` | St/T | `paint.vessel_*` |
| `paint/kitten/default/{enabled,color,clear}`, `paint/kitten/materials/<name>/{…}` (EVA only) | St/T | `paint.kitten_*`, `paint.kitten_material_*` |

### 4.3 `/vessels/by-id/<id>/ctl/` (present when a command sink is wired)
| Path | A | Write | Action | Phase |
|---|---|---|---|---|
| `ctl/ignite`, `ctl/shutdown`, `ctl/stage`, `ctl/focus` | T | `1` | `vessel.ignite`, `vessel.shutdown`, `vessel.stage`, `camera.focus` | Frame |
| `ctl/engine` | St | 0/1 | `vessel.engine` | Frame |
| `ctl/throttle` | St | 0..1 | `vessel.throttle` | Frame |
| `ctl/lights`, `ctl/rcs` | St | 0/1 | `vessel.lights`, `vessel.rcs` | Frame |
| `ctl/translate`, `ctl/rotate` | St | `x y z` signs | `vessel.translate`, `vessel.rotate` | Frame (latch) |
| `ctl/attitude_mode` | St | `manual` or track-target token | `vessel.attitude_mode` | **Solver** |
| `ctl/attitude_frame` | St | `EclBody|EnuBody|Lvlh|VlfBody|BurnBody|Dock` | `vessel.attitude_frame` | **Solver** |
| `ctl/rcs_mode` | St | `Enabled|Disabled` | `vessel.rcs_mode` | **Solver** |
| `ctl/attitude_target` | St | `x y z w` Body→CCI | `vessel.attitude_target` | **Solver** |
| `ctl/burn` | St | `ut dvx dvy dvz` (CCI) | `vessel.burn` | **Solver** |

Attitude-mode tokens (`SPEC:580-588`): `manual, Prograde, Retrograde, Normal, AntiNormal, RadialOut, RadialIn, Toward, Away, Antivel, Align, Forward, Backward, Up, Down, Ahead, Behind, Outward, Inward, PositiveDv, NegativeDv, Custom, None`.

### 4.4 `/events`, `/status`, `/display`, `/audio`
| Path | A | Notes |
|---|---|---|
| `events` | Sm | **blocking-event** file: a read parks until the next event, delivers NDJSON `{"ut","type","vessel"?,"detail"}` (`EventsFile.cs`) — types in §5 |
| `status/game_version` | S | `VersionInfo.Current.VersionString` |
| `status/sampler` | S | `ok <hz>` / `idle` |
| `status/accessors` | S | NDJSON `{"name","since_ut","error"}` per degraded accessor |
| `status/transports` | S | `9p <port>\nhttp …\nmqtt …\nmcp …\nmnt …\nserial …\ncontrol on|off` |
| `display/{enabled,fps,width,height,encoding}` | St | host-side, **no SimCommand**; clamps 1..60, 16..1920, `rgba-zlib|rgba` |
| `display/format` | S | `WxH@fps enc` |
| `display/stream` | Smb | Kitty-protocol frames, never EOF |
| `audio/file/` + `audio/file/<name>` | writable dir | upload clips (`EFBIG`/`ENOSPC`/`EEXIST`/`EPERM`) |
| `audio/play` | St | `<name> [start=ms] [end=ms] [vol=] [loop=0|1] [group=sfx|music|ui] [id=] [pan=] [pitch=]` → `audio.play` |
| `audio/set` | St | `<id-or-name> [vol=] [pan=] [pitch=] [pause=] [resume=1] [seek=ms]` → `audio.set` |
| `audio/stop` | St | `all` \| `<id-or-name>` → `audio.stop` |
| `audio/status`, `audio/info` | S | per-channel rows / caps |

### 4.5 `/ctl` (global) and `/camera`
| Path | A | Notes |
|---|---|---|
| `ctl/batch` | B | `<path> <value>` lines + `commit`; ≤ 64 cmds, ≤ 64 KiB, **one phase**, atomic same tick |
| `ctl/timed_batch` (`schedule_enabled`) | B | `@id @clock(render|wall|ut) @rate @loop @group` directives + `<offsetMs> <path> <value>` + `commit`; non-blocking; phase mixing allowed; STATE entries coalesce on catch-up, TRIGGERs never dropped |
| `ctl/schedules/{count,help}`, `ctl/schedules/clear` (T → `schedule.clear`) | | |
| `ctl/schedules/<id>/{kind,group,state,t,duration,pending,dropped,clock,last_error}` | S | live (ms) |
| `ctl/schedules/<id>/{pause,scrub,rate,loop}` | St | `schedule.pause/scrub/rate/loop` (token = id) |
| `ctl/schedules/<id>/{stop,remove}` | T | `schedule.stop/remove` |
| `camera/{status,info,target,playback,last_error}` | S | status is multi-line `key value…` |
| `camera/enabled` | St | 0/1 → `camera.enabled` (take / eased release) |
| `camera/release` | T | `camera.release` (hard cut) |
| `camera/mode` | St | `orbit|free|map|iva|fixed` → `camera.mode` (EOPNOTSUPP while owned) |
| `camera/follow` | St | `vessel:<id>|body:<id>|none` → `camera.follow` |
| `camera/tidal` | St | 0/1 → `camera.tidal` |
| `camera/map/scope` | St | m → `camera.map_scope` |
| `camera/track/` + `camera/track/<name>` | writable dir | JSON tracks (parsed on close) |
| `camera/play` | St | `<track> [at s] [rate x] [loop 0|1] [group tok]` → `camera.play` |
| `camera/set` | St | `[t s] [rate x] [loop 0|1] [paused 0|1]` → `camera.set` |
| `camera/stop` | T | `camera.stop` |
| `camera/pose/position` | St | `x y z [frame]` → `camera.position` |
| `camera/pose/frame`, `pose/aim_frame` | St | `ecl|cce|bodyfixed|enu|lvlh|chase` |
| `camera/pose/anchor`, `pose/aim_target` | St | `vessel:<id>|body:<id>|part:<vessel>/<iid>|none` |
| `camera/pose/geo` | St | `lat lon alt [body:<id>]` |
| `camera/pose/orbit/{radius,azimuth,elevation}` | St | m, deg, deg[-90,90] |
| `camera/pose/rotation` | St | quat, norm ∈ [0.5,2] |
| `camera/pose/aim` | St | `<target> [off x y z] [frame f] [up world|target|velocity|free] [roll deg]` |
| `camera/pose/{aim_offset,aim_up,roll,fov,ortho,ortho_height,smoothing}` | St | m / token / deg / deg / 0-1 / m / s[0,10] |
| `camera/pose/reset` | T | `camera.pose_reset` |

### 4.6 `/debug/**` (`debug_namespace`) and `/paint`
| Path | A | Action / phase |
|---|---|---|
| `debug/vessels/<id>/teleport` | St | `px py pz vx vy vz` CCI → `debug.teleport` **Solver** |
| `debug/vessels/<id>/impulse` | St | `x y z [cci|body] [ns|dv]` → `debug.impulse` **Solver** |
| `debug/vessels/<id>/{refill_fuel,refill_battery}` | T | **Solver** |
| `debug/vessels/<id>/docking/<n>/pushoff_impulse` | St | N·s → `debug.docking_pushoff` |
| `debug/time/warp` | St | → `debug.warp` |
| `debug/focus` | St | id → `camera.focus` |
| `debug/control_vessel` | St | id → `debug.control_vessel` |
| `debug/always_render_iva` | St | → `debug.always_render_iva` |
| `debug/vessels/<id>/weld` (`<target> <part_iid> x y z pitch yaw roll lock` → `debug.weld_create`), `weld_here` (`<target> <part_iid> [lock]`), `unweld` (T) | | |
| `debug/welds/{clear(T),count}`, `debug/welds/<source>/{target,part,offset,rotation,lock_rotation}` (S), `…/enabled` (St → `debug.weld_enable`) | | |
| `debug/thug_life/{help,add(St),clear(T),count}`, `debug/thug_life/<id>/{vessel,part,position,rotation,size,visible,cameras,remove,spec}` | | `debug.thug_life_*` |
| `debug/fx/{help,profiles,spawn(St),count,clear(T)}` | | `debug.fx_spawn` (`<vessel> <profile> [scale s] [offset x y z]`), `debug.fx_clear` |
| `debug/iva/{enabled,run_outside_iva}` (St), `help`, `adopt`/`adopt_all` (St), `clear` (T), `count`, `stats`, `interior`; `debug/iva/<id>/{vessel,part,name,template,position,velocity,angular_velocity,mass,shape,size,asleep}` (S), `nudge` (St), `release` (T), `spec` | | `debug.iva_*` |
| `debug/engineplume/templates/<id>/{core/*,absorption/*,emission/*,mach_diamonds/*,noise/*,quality/*}` (St, ranged), `json` (S), `reset` (T), `help` | | `debug.engineplume_set/reset` (token = template id, aux = field path) |
| `debug/plumetrail/render/{max_distance,voxel_first_slice,min_step_size,step_size_distance_scale,expansion_time,erosion_max_depth,erosion_edge_sharpness,self_shadow_steps,light_brightness,sky_ambient_brightness}` (St), `json`, `clear` (T), `reset` (T), `help` | | `debug.plumetrail_set/clear/reset` |
| `debug/clouds/bodies/<id>/shared/*`, `layers/<n>/{rotation_speed,detail_tile_km,color,scroll_speed,two_d/*,raymarch/*,types/<m>/*}` (St), `json`, `reset` | | `debug.clouds_set/reset` |
| `debug/terrain/wireframe` (St, global), `debug/terrain/bodies/<id>/{min_height,max_height,slope_roughness_deg,hapke_albedo,biomes/*,tessellation/*}` (St), `json`, `reset` | | `debug.terrain_set/reset` |
| `paint/{status,help}` (S); `paint/parts/{enabled,blend,clear}`, `paint/parts/global/{enabled,color,clear}`, `paint/parts/templates/<template>/{…}` | St/T | `paint.parts_enabled`, `paint.blend` (`multiply|tint|replace`), `paint.parts_clear`, `paint.global_*`, `paint.template_*` |
| `paint/kittens/{enabled,clear}`, `paint/kittens/shared/{…}`, `paint/kittens/materials/<name>/{…}` | | `paint.kittens_*`, `paint.kitten_shared_*`, `paint.kitten_shared_material_*` (`body|fur|helmet|visor|mmu[.n]`) |
| `paint/textures/file/` (+ `<name>`), `status`, `info`, `help`, `bindings`, `applied`, `clutter` (S); `bind` (`<texture-id> <file> [faithful|raw]`), `unbind` (`<texture-id>|all`), `clear` (T) | | `paint.texture_bind/unbind/clear` |
| `paint/stickers/{help,info,status,last,last_error,count}` (S); `place`, `spray` (St), `clear` (T), `debug` (St); `paint/stickers/<id>/{spec,anchor,live}` (S), `{image,visible,size,depth,rotation,alpha,brightness}` (St), `remove` (T) | | 12 `paint.sticker_*` |

### 4.7 The complete action-key vocabulary (`gatOS.SimFs/Commands/SimActions.cs` + `CameraCommands`/`AudioCommands`/`FxCatalog` constants; catalog table `SPEC §5.1:1452-1602`)

`vessel.{ignite,shutdown,engine,stage,throttle,lights,rcs,translate,rotate,attitude_mode,attitude_frame,attitude_target,burn,rcs_mode,scale,always_render}` · `engine.{active,min_throttle}` · `rcs.active` · `light.{on,brightness,color,outer_angle,inner_angle}` · `animation.goal` · `docking.undock` · `decoupler.fire` · `camera.{focus,enabled,release,mode,follow,tidal,map_scope,position,frame,anchor,geo,orbit_radius,orbit_azimuth,orbit_elevation,rotation,aim,aim_target,aim_offset,aim_frame,aim_up,roll,fov,ortho,ortho_height,smoothing,pose_reset,play,set,stop}` · `schedule.{pause,scrub,rate,loop,stop,remove,clear}` · `audio.{play,set,stop}` · `debug.{warp,control_vessel,always_render_iva,teleport,impulse,refill_fuel,refill_battery,docking_pushoff,weld_create,weld_here,weld_remove,weld_clear,weld_enable,thug_life_add,thug_life_clear,thug_life_remove,thug_life_visible,thug_life_position,thug_life_rotation,thug_life_size,thug_life_cameras,iva_physics,iva_run_outside_iva,iva_clear,iva_release,iva_nudge,iva_adopt,iva_adopt_all,fx_spawn,fx_clear,engineplume_set,engineplume_reset,plumetrail_set,plumetrail_reset,plumetrail_clear,clouds_set,clouds_reset,terrain_set,terrain_reset}` · `paint.{parts_enabled,blend,parts_clear,global_enabled,global_color,global_clear,template_enabled,template_color,template_clear,vessel_enabled,vessel_color,vessel_clear,part_enabled,part_color,part_clear,kittens_enabled,kittens_clear,kitten_shared_enabled,kitten_shared_color,kitten_shared_clear,kitten_shared_material_enabled,kitten_shared_material_color,kitten_shared_material_clear,kitten_enabled,kitten_color,kitten_clear,kitten_material_enabled,kitten_material_color,kitten_material_clear,texture_bind,texture_unbind,texture_clear,sticker_place,sticker_spray,sticker_remove,sticker_clear,sticker_visible,sticker_size,sticker_depth,sticker_rotation,sticker_alpha,sticker_brightness,sticker_image,sticker_debug}`.

### 4.8 HTTP `/v1` (`gatOS.Http/SimHttpServer.cs`; `SPEC §7:1706-1817`) — names only
`GET /v1/snapshot`, `/v1/openapi.json`, `/v1/time`, `/v1/status`, `/v1/system`, `/v1/bodies`, `/v1/bodies/{id}`, `/v1/vessels`, `/v1/vessels/{id}`, `/v1/vessels/{id}/telemetry`, `/v1/fs/<path>` (+ `?stream=1` SSE), `POST /v1/fs/<path>`, `POST /v1/command`, `GET /v1/events` (SSE), `GET /v1/vessels/{id}/stream` (SSE), `GET /v1/time/wait?until=<ut>` (long-poll), `GET /v1/audio/files`, `PUT|POST /v1/audio/file/{name}[?offset&complete]`, `DELETE /v1/audio/file/{name}`, `GET /v1/paint/texture/files`, `PUT|POST /v1/paint/texture/file/{name}`, `DELETE /v1/paint/texture/file/{name}`. Default `127.0.0.1:4242`, no auth, raw `TcpListener`, keep-alive.

### 4.9 MQTT (`gatOS.Mqtt/SimMqttBroker.cs`; default `127.0.0.1:1883`)
Retained world topics `gatos/time`, `gatos/status`, `gatos/system`, `gatos/bodies`, `gatos/snapshot`, `gatos/vessels/{id}/snapshot`, `gatos/vessels/{id}/telemetry`, `gatos/sim/vessels/active_id`; `gatos/events` (non-retained); field mirror `gatos/sim/<path>` (retained, `field_feed_hz`) with writes at `gatos/sim/<path>/set`; command `gatos/command` (JSON) → `gatos/command/result`. Subscribe `gatos/#`.

### 4.10 MCP (`gatOS.Mcp/McpRegistry.cs:42-81`; `SPEC_MCP.md`; default `127.0.0.1:4243/mcp`)
Tools: `gatos.get_world, list_celestials, get_celestial, list_vessels, get_vessel, list_kittens, get_kitten, get_runtime_state, get_capabilities, wait, ignite_engines, shutdown_engines, activate_stage, vessel_control, module_control, camera_control, camera_track, audio_control, audio_clip, schedule_control, debug_control, render_fx_control, paint_control, paint_texture, paint_sticker, command, execute_batch, schedule_batch`. Resources: `gatos.world, celestials, celestial, vessels, vessel, kittens, kitten, runtime, capabilities` (`gatos://vessels/{id}` etc.). `/sim/display` is the explicit MCP exclusion.

---

## 5. Flight events

### 5.1 Detection = polling snapshot diff, not Harmony (`gatOS.SimFs/Telemetry/EventDiffer.cs`)

`public static IReadOnlyList<SimEvent> Diff(SimSnapshot? previous, double utSeconds, double warpFactor, string? activeVesselId, IReadOnlyList<VesselSnapshot> vessels)` — runs on the game thread every sample tick; first sample is the baseline (no events); zero-alloc when nothing changed (positional walk when the roster is unchanged; dictionary match only on roster change).

| Event `type` | Scope | Detection logic (prev vs now) | KSA members behind the fields |
|---|---|---|---|
| `warp-changed` | global | `previous.WarpFactor != warpFactor`; detail `a→b` | `Universe.SimulationSpeed` |
| `active-changed` | global, `vessel`=new id | `previous.ActiveVesselId != activeVesselId` | `Program.ControlledVehicle?.Id` |
| `vessel-appeared` / `vessel-removed` | per vessel | roster id-set diff | `Universe.CurrentSystem.All` enumeration |
| `situation-change` | per vessel | `was.Situation != now.Situation`; detail `A→B` (covers **landing/liftoff/splashdown**: `Landed/Rolling/Freefall/Floating/Sailing/Maneuvering`) | `Vehicle.Situation` |
| `soi-changed` | per vessel | `was.ParentBodyName != now.ParentBodyName` | `Vehicle.Parent.Id` |
| `engine-state` | per vessel | per engine index `prev.Active != e.Active`; detail `engine n on|off` (**ignition/shutdown proxy**) | `EngineController.IsActive` |
| `flameout` | per vessel | `e.Active && prev.PropellantAvailable && !e.PropellantAvailable` | `EngineControllerState.IsPropellantAvailable` |
| `docked` / `undocked` | per vessel | per port `prev.Docked != d.Docked`; detail = docked_to part or `port n` | `DockingPort.Docked/DockedToPart` |
| `decoupled` | per vessel | rising edge `!prev.Fired && d.Fired` (**staging proxy** — there is no `staged` event; `ctl/stage` manifests as `decoupled` + `engine-state`) | `Decoupler.IsActive` |
| `animation-complete` | per vessel | `DeploymentState` goes `Deploying|Retracting → Deployed|Retracted` | `KeyframeAnimationModule.State.DeploymentState` |
| `battery-depleted` / `battery-charged` | per vessel | fraction crosses `≤0.001` from `>0.01` / `≥0.999` from `<0.999` | `Battery` charge/capacity |

Module events require `telemetry_vessel_detail` (the differ cannot see what the sampler did not read). Not detected today: launch/liftoff as a named event (use `situation-change Landed→Freefall`), atmosphere entry/exit, stable-orbit/escape (the ksa skill `telemetry.md` lists these as *recommended* rules with ~2 s debounce, not implemented in gatOS).

### 5.2 Feature-emitted events (queued by their managers, drained into `SimSnapshot.NewEvents` each sample; `TelemetrySampler.cs:231-251`, `SPEC §3.5:599-638`)
`audio.finished` (`<id> <clip> ended|stopped|replaced`), `paint.sticker_placed`, `schedule.{started,finished,failed,dropped,evicted}`, `camera.{shot,finished}`, `iva.{impact,escape,release}`.

### 5.3 Subscription / delivery models
| Channel | Mechanism |
|---|---|
| `/sim/events` (9P) | `EventsFile`: `IsStreaming`, `Size=1`; a read parks on `SnapshotStore.WaitForNextAsync(lastSeq)` and delivers `NewEvents` since open; Ctrl-C = Tflush |
| `/sim/vessels/<id>/stream` | `StreamFile`: growing NDJSON log per publish (256 KiB cap, drops whole lines + `{"notice":"dropped"}`) |
| `/sim/time/alarm` | `AlarmFile`: blocking read until `ut ≥ target` |
| HTTP | `GET /v1/events` SSE; `GET /v1/fs/<path>?stream=1` SSE per value change; `GET /v1/vessels/{id}/stream`; `GET /v1/time/wait?until=` |
| MQTT | `gatos/events`; per-field retained `gatos/sim/<path>` (subscription-gated, changed-only, `field_feed_hz` default 4) |
| MCP | `gatos.wait` (a later snapshot or event satisfying a condition, `timeout_ms`); resources are snapshot reads, not subscriptions |
| In-process | `SnapshotStore.Current` / `WaitForNextAsync(seq, ct)`; `AudioStore/IvaPhysicsManager/ScheduleStore/CameraDirector/StickerStore.DrainEvents()` |

---

## 6. Harmony patches (complete list)

| Harmony id | Target | Kind | Installed | Purpose | Source |
|---|---|---|---|---|---|
| (StarMap's own) | `Program.OnDrawUiFrame`, `Program.OnDrawUiViewports`, `Program.OnFrame` | prefix / postfix / postfix | by StarMap for `[StarMapBeforeGui]`/`[StarMapAfterGui]`/`[StarMapAfterOnFrame]` | the per-frame hooks | `AGENTS.md:620-632` |
| `gatos.solver` | `Universe.ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)` | prefix, `Priority.First` | `OnFullyLoaded` → `InstallSolverHook` | Solver-phase command drain | `Mod.Game.cs:859-894` |
| `gatos.menu` | `Program.DrawProgramMenusHook()` | postfix | `OnFullyLoaded` → `InstallMenuFallback` (only draws when `ModLibrary.Find("ModMenu") is null`) | fallback "gatOS" menu-bar entry | `Mod.Game.cs:902-938` |
| `gatos.display` | `Program.RenderGame` (private; `AccessTools.Method(typeof(Program), "RenderGame")`) | **transpiler** — inserts `OnRenderGameRecorded(Program, CommandBuffer)` before the final 1-arg `Brutal.VulkanApi.*.End` call | `InstallDisplayHook` | in-band screen-stream capture into the engine's frame command buffer | `Game/Ksa/DisplayRenderPatch.cs:63-77,214-256` |
| `gatos.display` | `GameSettings.UiPixelCulling()` | prefix (`ref bool __result`; returns false while capturing) | same | suppress UI-coverage depth stamp so captured frames have no holes | `DisplayRenderPatch.cs:83-155` |
| `gatos.camera` | `GameViewport.OnFrame(double)` (`AccessTools.Method(typeof(GameViewport), nameof(GameViewport.OnFrame), [typeof(double)])`) | prefix + postfix, both gated on `ReferenceEquals(__instance, Program.MainViewport)` | `InstallCameraHook` (when `camera_enabled`) | same-frame sample/schedule/drain + camera prepare; postfix publishes applied transform | `Game/Ksa/Camera/CameraViewportPatch.cs` |
| `gatos.iva` | `PartModel..ctor(PartModelModule.Template)` (`AccessTools.Constructor`) | postfix | **dynamic**: only while `always_render_iva` is on | flips `Template.Internal=false` for part models created after enabling | `Game/Ksa/Render/IvaForceRender.cs:73-120` |
| `gatos.always_render` | `Vehicle.GetWorldMatrix(Camera)`; `Vehicle.UpdateRenderData(IViewport,int)` | two prefixes (replace bodies minus the < 1 px cull for marked vessels; `return false`) | **dynamic**: while ≥ 1 vessel marked | per-vessel render-distance override | `Game/Ksa/Render/VesselForceRender.cs:128-242` |
| `gatos.thug_life` | `SuperMeshRenderSystem.RenderMainPass(IViewport, CommandBuffer)` | postfix (`RenderMainPassPostfix(CommandBuffer commandBuffer)`) | **dynamic**: while ≥ 1 entry | records textured-quad draws into the offscreen scene pass | `Game/Ksa/ThugLife/ThugLifeRenderPatches.cs`, manager `:301` |
| `StickerManager.HarmonyId` (`"gatos.stickers"`) | `KSA.Rendering.RenderTarget.ResolveAttachments(CommandBuffer, bool)` | postfix (bails unless `__1` (full resolve) && `__instance == Program.OffscreenTarget` && `Program.RenderedViewport == Program.MainViewport` && `!Program.EditorFlag`) | **dynamic**: 0→1 live stickers | sticker decal pass after MSAA resolve | `Game/Ksa/Paint/Stickers/StickerRenderPatches.cs` |
| PaintManager `_harmony` | `ShaderModuleUtils.FromFile(Device, string, ref VkShaderStageFlags, CompileOptions?)` | prefix (substitutes transformed GLSL source) | **dynamic**: while `paint/parts/enabled` armed | shader-source paint injection | `Game/Ksa/Paint/PartPaintPatches.cs:27-29,73-101`, `PaintManager.cs:580-609` |
| PaintManager `_harmony` | `PartTreeRenderData.WriteState(Batch,int,Part)` / `WriteDynamicState(DynamicBatch,int,PartModelDynamicModule)` (private; nested private types) | postfixes (OR paint bits into `Batch.StateBitFlags[slot]` via `AccessTools.FieldRef<object,int[]>`) | same | per-part paint state bits | `PartPaintPatches.cs:31-68,106-130` |

Conventions: one `Harmony` instance per feature, string ids; every patch body is try/catch with log-once + self-disable (`_faulted`/`Active=false`); dynamic patches install on first use and `UnpatchAll(id)` on last use/despawn-prune/unload; targets resolved by `AccessTools` so a rename fails at install time (logged, feature dark) rather than at compile time. Unload order: `TeardownGameCheats` → `RemoveCameraHook` → `RemoveSolverHook` → `RemoveMenuFallback` → `DisposeDisplayCapture` (which `GraphicsAndCompute.WaitIdle()`s before freeing Vulkan buffers).

---

## 7. Reflection / private access (what a bridge must treat the same way)

All are `ChurnRisk.High` anchors; a miss degrades to `EOPNOTSUPP` + `/sim/status/accessors`, never a crash.

| KSA member (private/internal/protected) | How reached | Used by | Notes |
|---|---|---|---|
| `Vehicle._manualControlInputs` (struct field) → `.EngineThrottle` (float) | `typeof(Vehicle).GetField("_manualControlInputs", Instance\|NonPublic)`; box, `GetField("EngineThrottle", Instance\|Public).SetValue`, write box back | `ThrottleActuator` | public reader is `GetManualThrottle()`; no public setter |
| `Vehicle._manualControlInputs.ThrusterCommandFlags` (`ThrusterMapFlags`) | same pattern | `TranslateActuator`, `RotateActuator` | public reader `GetThrusterFlags()` |
| `LightModule.Template` (shared `LightModule.TemplateData`) and its `Intensity/ColorRgb/OuterAngle/InnerAngle` reference objects | `RuntimeHelpers.GetUninitializedObject` + per-field copy over the type hierarchy (`BindingFlags.DeclaredOnly` loop) | `LightActuator.EnsureUnshared` | avoids recolouring every light of the same template |
| `KittenEva._renderable` → `KittenRenderable._characterAvatar` → `CharacterAvatar.Core.Scale` (float) | `GetField(..., Instance\|Public\|NonPublic)` chain; struct write-back of `Core` | `ScaleActuator`, `EvaPaintBridge` (`EvaRenderable`, `AvatarField` statics + `GetProperty("Values")`, protected `MaterialIndices` via `GetField`) | `GetType().Name == "KittenEva"` string gate |
| `GameViewport.FixedController { get; protected set; }`, `ViewportBase.Mode { get; protected set; }` | `GetProperty(...).GetSetMethod(nonPublic: true).Invoke` | `Game/Ksa/Camera/ViewportSeam.cs` | fallback `SetCameraMode` drops latched translate/rotate flags |
| `Program.Instance._volumetricTrailRenderer` | `typeof(Program).GetField(name, Instance\|Public\|NonPublic)` | `FxReflect.Trail` | |
| `VolumetricTrailRenderer._plumeTrailSegmentsManager` → `PlumeTrailSegmentsManager._settings` | two private field hops | `FxReflect.TrailSettings` | backs `plumetrail/render/expansion_time` |
| `VolumetricExhaustTemplate.References` (internal static `SerializedCollection<>`) | `GetField(..., Static\|NonPublic)` | `FxReflect.PlumeTemplates` | enumeration only; falls back to live nozzles |
| `Program.Instance._planetTransparenciesRenderer` → public `GetCloudRenderer()` | private field hop | `FxReflect.Clouds` | |
| `CloudRenderer._renderer`, `_cloudShadowsRenderer`, `_worleyNoise3dTarget` | private fields | `FxReflect.CloudApply` | retyped `RenderTarget→RenderImage` at 5154 |
| `PlanetRenderer._renderUboMap`, `_meshUboMap` (`MappedMemory`) | private readonly fields | `FxReflect.TerrainUbo` | terrain live-apply |
| `PartTreeRenderData` nested `Batch`/`DynamicBatch` (private) + `StateBitFlags` (int[]) + private `WriteState`/`WriteDynamicState` | `GetNestedType(..., NonPublic)`, `AccessTools.FieldRefAccess<int[]>`, `AccessTools.Method(type, "WriteState", [batch, typeof(int), typeof(Part)])` | `PartPaintPatches.Resolve` | compiler-blind; any null → paint refuses to arm |
| `Program.RenderGame` (private) | `AccessTools.Method(typeof(Program), "RenderGame")` + IL scan for `End` | `DisplayRenderPatch` | |
| `PartModel..ctor(PartModelModule.Template)` (protected) | `AccessTools.Constructor` | `IvaForceRender` | |

Everything else gatOS touches is public — the 179-anchor census is in Appendix A.

---

## 8. Catalog / addressing

| Entity | Identity key | Resolution | Stability |
|---|---|---|---|
| Vessel | `Vehicle.Id` (string; `Name == Id`, `Vehicle.SetName` assigns Id) | `KsaCatalog.ResolveVehicle(id)`: scan `Universe.CurrentSystem.All.UnsafeAsList()` for `Vehicle` with `Id == id`; `vessels/active` = `Program.ControlledVehicle?.Id` | stable; debris/fragments appear as `<id>_N` (KSA ≥ 5402) |
| Any astronomical (vessel or body) | `Astronomical.Id` | `Universe.CurrentSystem.Get(id)` (what `camera.focus`, `CameraTargets`, stickers use) | stable |
| Celestial body | `Celestial/StellarBody/Barycenter.Id` | enumeration of `CelestialSystem.All` by type | session-constant |
| Module (engine/rcs/light/solar/generator/docking/decoupler/animation) | **ordinal** = index into `vehicle.Parts.Modules.Get<T>()` span (`SimCommand.Ordinal`) | direct index, bounds-checked → `NotFound` | stable until the vehicle is edited/staged (KSA content changes can shift ordinals, `SPEC:495-498`) |
| SRB | index among `SolidMotor`s in `Parts.RocketCores.Modules`; cross-links to engine ordinal | read-only | |
| Tank | sanitized substance name (`tanks/<resource>`) | — | |
| Part / subpart | `Part.InstanceId` (uint, runtime-unique) — the **stable handle** for welds, IVA adopt, stickers, paint `part_*` (token), thug-life; `parts/<n>` index is **not** stable | `FindPart(vehicle, iid)` walks `Parts.Parts` + `SubParts` | stable for the session |
| Camera target | `TargetRef` grammar `vessel:<id>` \| `body:<id>` \| `part:<vessel>/<iid>` \| `none` | `CameraTargets.TryResolve` | |
| Registry entries | welds keyed by **source vessel id** (one weld per source); thug-life / IVA objects / stickers = integer **smallest free slot** (reused); schedules = `@id` token `[A-Za-z0-9_.-]{1,64}` or auto `#N`; audio channels = `id=` token or auto `#N`; FX entities = template id / body id / `""` for globals | `Ordinal` carries the integer id; `Token` the vessel/body/template id | runtime-only |
| Paths | ids sanitized to `[A-Za-z0-9._-]` with `~N` collision suffixes for 9P/`/v1/fs`/MQTT; HTTP aggregate routes use raw ids (`SPEC §2.2`) | | |

Roles: **`KsaCatalog`** = the one executor/router and the only place actuators are reached (owns authority gating + health latching). **`KsaAnchorAttribute`** (`Game/Ksa/KsaAnchor.cs`) = purely documentary: `[KsaAnchor(string member) { SourceFile, Verified, GameVersion, Risk (ChurnRisk.Low|Medium|High), Notes }]` on every method that touches a KSA type, so "the churn playbook is a grep". **`KsaHealth`** = per-accessor latches.

---

## 9. Health / compatibility patterns to copy

**`Game/Ksa/KsaHealth.cs`** (game-thread only, no locks):
```csharp
internal sealed class KsaHealth {
    internal bool IsDegraded(string accessor);
    internal void Fault(string accessor, double utSeconds, string error);   // latch + log once
    internal void Clear(string accessor);                                   // on next success; logs recovery
    internal IReadOnlyList<AccessorHealthSnapshot> Snapshot();              // → /sim/status/accessors
}
```
Accessor keys: `actuator.<action>` (KsaCatalog), `fx.trail_renderer`, `fx.trail_settings`, `fx.plume_templates`, `fx.cloud_renderer`, `fx.cloud_apply`, `fx.terrain_renderer`, `fx.terrain_ubo` (`FxReflect.cs:26-45`), paint/texture/sticker latches.

Graceful-degradation ladder (all in use):
1. Per-vehicle try/catch in the sampler (`TelemetrySampler.cs:195-212`) and `BuildFull` → `BuildCore` fallback (`VesselReader.cs:46-61`), logged once.
2. Executor never throws; `KsaCatalog.Execute` catch → `health.Fault` + `Fault` result; subsequent calls short-circuit to `Unsupported`.
3. Session kill-switches with one error log: `_samplerDead`, `_commandsDead`, `_weldsDead`, `_ivaDead`, `_audioDead`, `_cameraDead`, `_thugLifeDead`, `_schedulesDead`, `_uiDead` (`Mod.Game.cs:67-139`).
4. Reflection accessors are null-tolerant and return a reason string (`FxReflect`), or return `CommandOutcome.Unsupported "… field not found in this build"` (`ThrottleActuator`).
5. Hook install is best-effort: missing target → warn + feature dark (`InstallSolverHook`, `DisplayRenderPatch.Install`).
6. `Sanitize.Finite` on every published double; `UniverseTime.IsSaturated()` sentinel handling.

Build/compat scaffolding:
- `Directory.Build.props`: `KSAFolder` = `KSA_DLL_DIR` env → sibling `../ksa-game-assemblies/current/dll/` → per-OS default; KSA refs `<Private>false</Private>` + `Condition="Exists(...)"`; `Game/**/*.cs` compile-removed when `KSA.dll` is absent (`gatOS.GameMod.csproj:19-25`); `VerifyBepuReference` target errors loudly on a stale assemblies checkout.
- `Mod` is a `partial` class split on the game-assembly boundary; game-coupled partial impls are `[MethodImpl(NoInlining)]` so a missing assembly faults at the guarded call site (`Mod.Game.cs:215-219`); game-typed statics live in a nested `Palette` class so `Mod` type-loads without Brutal (`Mod.Game.cs:45-56`).
- Version checks: none at runtime beyond publishing `VersionInfo.Current.VersionString` as `status/game_version`; compatibility is a **build-time + review** process — the `upgrade-ksa` skill (`.claude/skills/upgrade-ksa/SKILL.md`): (1) diff `version.json` changelogs CURRENT vs PREVIOUS, (2) `KSA_DLL_DIR=<CURRENT>/current/dll dotnet build gatOS.GameMod` = the alarm (each error lands on a `[KsaAnchor]`), (3) decomp diff for silent semantic drift (unit/frame/gating changes), (4) re-verify reflection accessors by hand (compiler-blind), (5) re-verify render internals, (6) fix + bump `Verified`/`GameVersion` on the anchor + update `scope/`, `docs/KSA_INTEGRATION_MATRIX.md`, SPEC. Risk tiers = `ChurnRisk`. The two un-anchorable Harmony targets (`Universe.ExecuteNextVehicleSolvers`, `Program.DrawProgramMenusHook`) are checked by hand every pass.
- Runtime discovery when decomp lags the binary: the reflection-dump strategy in `.claude/skills/ksa/debug.md` (walk fields with `BindingFlags.DeclaredOnly`, nested types as `Outer+Inner`, `FloatReference.Value`/`ColorReference.OnDataLoad(null)`).

---

## 10. Prior art on in-process scripting (search of plans/, ideas/, OS_*.md, KSA_GAME_INTEGRATION_*.md, notes.md, IDEA_TODOs.md, docs/)

**Result: no prior design for in-process Lua/JS/Roslyn scripting exists in the repo.** The closest things, with quotes:

- `KSA_GAME_INTEGRATION_IDEA.md:13-16` — "this abstraction must be practical to interface with from any general purpose programming or scripting language … i am still thinking our device file idea should be OK … i expect players would be able to build up an sdk in their favorite language or tech stack … (and we will create at least one exmaple one such as in typescript using a bun runtime would be my preference)". And `:24`: "another idea is if it would be possible to have some kind of a "magic" HTTP server which is just always available on a magic port on the VM (lets say port 4242)". And `:30-32`: "it's important to note that KSA is in constant flux … special care should be taken to abstract our interface for both reading KSA data and changing KSA game state. it must be documented in detail for each data point … co-located and well documented" — the origin of `[KsaAnchor]`/`KsaCatalog`.
- `OS_IDEA.md:210` — "it has devices for things like the vehicles in the simulation, then in-game code or mods can hook into those to drive TUI interfaces in shell sessions that the purrtty terminal emulator provides" (hooks = mods consuming device files, not scripts in-process).
- `IDEA_TODOs.md:42` — "explore wasm kernel runtime to avoid qemu and be truly platform agnostic".
- `OS_ANALYSIS.md:39,459-475` — "honorable mention is **container2wasm + wasmtime-dotnet** (§4): the only true *in-process* option" … "Cons (disqualifying as primary): the CPU is an interpreter in wasm — a reported ~16 s for a node hello-world". In-process was considered only as a *whole-OS* embedding, and rejected for speed.
- `plans/GATOS_CUSTOM_AUDIO_PLAN.md:116-118` — "**Execution domains:** gatOS.GameMod *is* the KSA mod, in-process with the game. Userland is real Alpine in QEMU; `cat`/`echo` to `/sim/...` become 9P `Twrite`s over TCP/slirp into the C# 9P server (`gatOS.NineP`) running in the game process. No extra IPC hop exists or is needed."
- `KSA_GAME_INTEGRATION_PLAN.md:573-584` (G6) — "Example SDK: TypeScript on Bun … **Reactive reads**: `for await (const line of sdk.vessel("x").stream())` (tails `stream`)" — the only "event-driven script" model, and it is out-of-process.
- `docs/TUTORIAL_DATA_REFERENCE.md:30-31` — programs run "inside the guest … Python/Rust/Bun shipped into Alpine" or "on the host … language SDKs; dashboards".
- In-host *declarative* programs that are the nearest in-process analogue: the **timed scheduler** (`/sim/ctl/timed_batch`, `plans/SCHEDULER_ASBUILT.md`, "100 % game-free, zero KSA bindings"), the **camera track JSON DSL** evaluated at render rate (`SPEC §3.11:1300-1384`), and the **MCP server** as "the concise, logical JSON interface for AI agents, deliberately not a filesystem mirror" (`AGENTS.md:17-19`). The `examples/agc` (yaAGC/Luminary099 in-guest) and `examples/photog-rs` are external consumers.
- No mention of Roslyn, `csx`, MoonSharp/NLua, ClearScript/Jint anywhere; "event-driven"/"callback" hits are all Bepu/MQTT/SSH internals.

---

## 11. Packaging

**`gatOS.GameMod/mod.toml`** (verbatim):
```toml
name = "gatOS"
description = "A real, minimal Alpine Linux running in a QEMU microVM, surfaced through purrTTY terminals, with live KSA vehicle telemetry exposed as a /sim filesystem."
version = "0.1.0"
author = "meow sci"

[StarMap]
EntryAssembly = "gatOS.GameMod"

[[StarMap.ModDependencies]]
ModId = "purrTTY"
Optional = true
ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```
(`ImportedAssemblies` makes StarMap resolve those two from purrTTY's ALC first — one `CustomShellRegistry.Instance`; the vendored copies in `vendor/purrTTY/` are referenced `<Private>true</Private>` so they land in the dist **and** in `deps.json` as the no-purrTTY fallback — `gatOS.GameMod.csproj:126-140`, `AGENTS.md:683-690`.)

**`gatOS.GameMod.csproj`** essentials: `OutputType Library`, `AssemblyName gatOS.GameMod`, `CopyLocalLockFileAssemblies true`, `AllowUnsafeBlocks true`, `DistDir = $(SelectedDistModDir)gatOS\`; `PackageReference`s `ModMenu.Attributes 1.0.1`, `Tomlyn 2.6.0`, `Lib.Harmony 2.4.2` (PrivateAssets all), `StarMap.API 0.3.6` (PrivateAssets all); game DLL `<Reference HintPath="$(KSAFolder)/X.dll"><Private>false</Private>` for `KSA, Brutal.Core.{Common,Logging,Numerics,Strings,Memory}, Brutal.Concurrency, Brutal.Fmod, BepuPhysics, BepuUtilities, Brutal.ImGui(.Abstractions), Brutal.Vulkan(.Abstractions,.Vma), Brutal.ShaderC, Brutal.Texture(.Abstractions), Brutal.Ktx, Planet.Core, Planet.Render.Core`; `<None Update="mod.toml"><CopyToOutputDirectory>Always</CopyToOutputDirectory></None>`.

**`CopyCustomContent` target** (`AfterTargets="AfterBuild"`): wipe `$(DistDir)*` except `gatos.toml`; copy `mod.toml`, `$(ProjectDepsFilePath)` (StarMap's `ModAssemblyLoadContext` resolves mod-local deps via `AssemblyDependencyResolver` reading `deps.json`), `Configuration/gatos.default.toml`, **all `$(TargetDir)*.dll` except `0Harmony.dll` and `StarMap.API.dll`** (loader-supplied; a second copy risks type-identity splits), `LICENSE`, `THIRD-PARTY-NOTICES.md`, `third-party-licenses/*`, `../guest/out/**` → `guest/` (SkipUnchangedFiles; High-importance message if empty), `../vendor/qemu/win-x64/**` → `qemu/win-x64/` (SkipUnchangedFiles).

**Dist layout**: `<mods>/gatOS/{gatOS.GameMod.dll, gatOS.*.dll, purrTTY.*.dll, MQTTnet/SSH.NET/Tomlyn/… .dll, gatOS.GameMod.deps.json, mod.toml, gatos.default.toml, LICENSE, THIRD-PARTY-NOTICES.md, third-party-licenses/, guest/{manifest.toml,kernel,initrd,base.qcow2,ssh key}, qemu/win-x64/**}`. Release zip `gatOS-<version>.zip` built by `.github/workflows/mod-release.yml` (one archive, payload + guest + Windows QEMU), validated by `tools/check-dist.py`.

**Native deps per platform**: Windows ships pinned QEMU in `qemu/win-x64/` (`tools/fetch-qemu.{sh,ps1}` + `tools/qemu-win64-files.txt`, `Get-QemuImportClosure.ps1`); Linux/macOS use system QEMU (`QemuLocator`: PATH, then `/opt/homebrew/bin`, `/usr/local/bin`; hints `brew install qemu` / `apt install qemu-system-x86 qemu-utils`; `gatOS.Vm/QemuLocator.cs:40-137`).

**Paths** (`gatOS.Vm/GatOsPaths.cs`): data dir `<MyDocuments>/My Games/Kitten Space Agency/mods/gatOS` (`disks/`, `logs/`, `gatos.toml`); mod dir = `Path.GetDirectoryName(typeof(Mod).Assembly.Location)` set at init; `guest/`, `qemu/`, `gatos.default.toml` under it. Deploy target (`Directory.Build.props`): `GATOS_DIST_DIR` env else `KSAUserModDir` = Windows `%USERPROFILE%\Documents\My Games\Kitten Space Agency\mods\`, macOS/Linux dev default `$(HOME)/repos/meow-sci/mods/`. Config: TOML via Tomlyn, snake_case, clamp-and-log, atomic temp+rename save (`Configuration/GatOsConfig.cs`, `AtomicFile.cs`); every key has a `Sections` doc entry mirrored in `gatos.default.toml`.

---

## 12. What this implies for `ksa-scripting` (brief, derived from the above)

1. **Tick scripts where gatOS drains**: expose a Frame-phase slot (viewport prefix or `[StarMapBeforeGui]` + `[StarMapAfterOnFrame]` F2-proof fallback with the same latch trick), a **Solver-phase** slot (`Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers`) for FC setpoints/teleports/refills, and a **post-solver** slot (`JobSystems.VehicleSolver.Wait()` first) for per-frame re-posing. Keep `SimCommand.SolverActions` as the phase truth table.
2. **Reads**: either hand scripts the immutable `SimSnapshot`-style records (cheap, consistent, warp-safe) or live KSA objects wrapped by the same reader functions (`VesselReader.*`, `BodyReader.*`, `PartsReader.Sample`) — the readers are already single-pass, cached and NaN-scrubbed.
3. **Writes**: route script calls through the identical action vocabulary (§4.7) into `KsaCatalog`-style dispatch so validation, authority gating, health latching and errno vocabulary are shared with `/sim`; in-process callers can skip the queue when already on the game thread but should keep batch/phase semantics.
4. **Events**: reuse `EventDiffer.Diff` on consecutive snapshots (or run it at the script tick) rather than Harmony; add the few missing events (liftoff/atmosphere/stable-orbit/escape) as snapshot rules per `.claude/skills/ksa/telemetry.md`.
5. **Reflection set** to mirror: `_manualControlInputs{EngineThrottle,ThrusterCommandFlags}`, light-template unshare, `KittenEva` avatar chain, `GameViewport.FixedController`/`ViewportBase.Mode` setters, the FX private fields, `PartTreeRenderData` internals.
6. **Compat**: annotate every KSA touch with a `[KsaAnchor]`-style attribute, keep KSA types out of the script-facing DTOs, compile-gate on `KSA.dll`, and ship `ImportedAssemblies` if sharing types with gatOS/purrTTY.

---

## Appendix A — `[KsaAnchor]` census (179 sites; file:line → member, `grep -o 'KsaAnchor("[^"]*"'` over `gatOS.GameMod/Game`)

```
TelemetrySampler.cs:168  Universe.GetElapsedSeconds(); Universe.SimulationSpeed; Universe.GetLastSimStep().DeltaTime; Program.ControlledVehicle?.Id; Universe.CurrentSystem.All.UnsafeAsList()
TelemetrySampler.cs:336  Universe.GetSimulationSpeeds() (SimulationSpeed.Value per warp rung)
TelemetrySampler.cs:360  Universe.IsAutoWarpActive
TelemetrySampler.cs:375  Universe.AutoWarpTime?.Seconds()
TelemetrySampler.cs:390  VersionInfo.Current.VersionString
Ksa/KsaCatalog.cs:491    Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Id
Ksa/KsaCatalog.cs:505    Universe.CurrentSystem.Get(id) → Astronomical (vehicle or celestial)
Ksa/DisplayRenderPatch.cs:135  GameSettings.UiPixelCulling() (Harmony prefix)
Ksa/DisplayRenderPatch.cs:190  Program.RenderGame (Harmony transpiler) + Brutal.VulkanApi.VkDeviceExtensions.End
Ksa/FrameCapture.cs:111  Program.GetRenderer(), Program.MainViewport.OffscreenTarget.ColorImage/.Extent, …
Ksa/Paint/UserTextureGpu.cs:44  TextureLoader.LoadFromMemory; TextureAsset.LoadOptions(R8G8B8A8UNorm, Rgba32); …
Ksa/Paint/PaintManager.cs:140  Part.InstanceId; Part.Template.Id; PartModel/PartModelDynamic PerInstanceData.StateBitFlag bits 11..31 …
Ksa/Paint/PaintManager.cs:263  ShaderReference.ModPath; MeshIndirect(.Raytraced).frag sampledColor/inStateFlags anchors
Ksa/Paint/PaintManager.cs:326  Vehicle.Parts (PartTree).RenderData (PartTreeRenderData).InvalidateStates()
Ksa/Paint/ClutterTextureBridge.cs:209  Program.Instance.BindlessTextures (public field) → BindlessTextureLibrary.SetTexture(int,VkImageView,VkImageLayout); …
Ksa/Paint/ClutterTextureBridge.cs:283  PlanetRenderer.GroundClutterRenderer (public) → CelestialsWithGroundClutter; …
Ksa/Paint/PartPaintPatches.cs:31  PartTreeRenderData.WriteState(Batch,int,Part) / WriteDynamicState(DynamicBatch,int,PartModelDynamicModule) …
Ksa/Paint/EvaPaintBridge.cs:118  KittenEva._renderable -> KittenRenderable._characterAvatar; CharacterAvatar Core/Fur/Attachments; protected MaterialIndices
Ksa/Paint/EvaPaintBridge.cs:168  GpuMaterialSystem.CreateObject; AssetManager.AssetMap; GpuObjectAssetRef.Dispose -> Free; MaterialData layout
Ksa/Paint/Stickers/StickerRenderPatches.cs:27  KSA.Rendering.RenderTarget.ResolveAttachments(CommandBuffer,bool) — Harmony postfix
Ksa/Paint/Stickers/StickerTextureBinder.cs:188  Program.GetRenderer(); Program.Instance.BindlessTextures (public field) → …
Ksa/Paint/Stickers/StickerTextureBinder.cs:237  Program.Instance.BindlessTextures → BindlessTextureLibrary.FreeTexture(int)
Ksa/Paint/Stickers/StickerAnchors.cs:63   Celestial.{GetDirCcfFromLatLon,GetTerrainHeightFromDirCcf,GetCcf2Cce,GetCci2Cce,…
Ksa/Paint/Stickers/StickerAnchors.cs:133  Vehicle.GetMatrixAsmb2Ego(Camera); Part.MatrixAsmb2Ego(in double4x4)
Ksa/Paint/Stickers/StickerDecalRenderer.cs:164  GlobalShaderBindings.DescriptorSetLayout; Program.Instance.BindlessTextures.…
Ksa/Paint/Stickers/StickerDecalRenderer.cs:223  ShaderModuleUtils.FromString(Device, ReadOnlySpan<byte>, VkShaderStageFlags, …
Ksa/Paint/Stickers/StickerDecalRenderer.cs:362  Renderer.{Allocator,Graphics}; BufferEx.CreateInfo; VkUtils.StageAndUploadToBuffer
Ksa/Paint/Stickers/StickerDecalRenderer.cs:422  Program.{OffscreenTarget,SetViewport,Instance.ResourceFrameIndex,PointClampedSampler,…
Ksa/Paint/Stickers/StickerManager.cs:442  Universe.CurrentSystem.Get(string) → Astronomical; Vehicle; Celestial; …
Ksa/Paint/Stickers/StickerManager.cs:511  Program.GetRenderer()
Ksa/Paint/Stickers/StickerManager.cs:646  Program.GetRenderer().GraphicsAndCompute.WaitIdle()
Ksa/Paint/Stickers/StickerPicker.cs:57   Program.GetMainCamera(); Program.MainViewport; Camera.{ScreenToEgoRay(float2),FramebufferSize}; Cursor.GetEgoRay(IViewport)
Ksa/Paint/Stickers/StickerPicker.cs:104  Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.{BoundingSphereRadiusBody,Parts.Parts,…
Ksa/Paint/Stickers/StickerPicker.cs:170  Camera.{NearbyCelestial,GetPositionEgo}; Celestial.{GetCce2Ccf,GetCcf2Cce,GetCci2Cce,…
Ksa/Fx/FxReflect.cs:86   Program.Instance._volumetricTrailRenderer (private instance field)
Ksa/Fx/FxReflect.cs:127  VolumetricTrailRenderer._plumeTrailSegmentsManager (private) → PlumeTrailSegmentsManager._settings (private) → PlumeTrailSettings.ExpansionTimeSeconds
Ksa/Fx/FxReflect.cs:162  VolumetricExhaustTemplate.References (internal static field) .GetList()
Ksa/Fx/FxReflect.cs:183  Program.Instance._planetTransparenciesRenderer (private) .GetCloudRenderer() (public)
Ksa/Fx/FxReflect.cs:226  CloudRenderer._renderer/_cloudShadowsRenderer/_worleyNoise3dTarget (private fields)
Ksa/Fx/FxReflect.cs:259  Program.GetPlanetRenderer() (public static)
Ksa/Fx/FxReflect.cs:283  PlanetRenderer._renderUboMap/_meshUboMap (private readonly MappedMemory fields)
Ksa/Fx/CloudActuator.cs:76   Universe.CurrentSystem.All.UnsafeAsList(); AtmosphericBody.BodyTemplate.CloudsReference
Ksa/Fx/CloudActuator.cs:111  CloudsReference.{OrbitTransitionStartAltitude,OrbitTransitionEndAltitude,MaxShadowsAltitude,…
Ksa/Fx/CloudActuator.cs:182  Cloud reference writes: DistanceReference/Vector3Reference/DoubleReference/ColorRgbReference …
Ksa/Fx/CloudActuator.cs:289  CloudLayerReference.OnDataLoad(Mod.Empty); CloudRenderer._planetToCloudRenderData (public); …
Ksa/Fx/PlumeActuator.cs:62   VolumetricExhaustTemplate.Get(string) (public static)
Ksa/Fx/PlumeActuator.cs:74   VolumetricExhaustTemplate.{LengthWeights,Absorption,Emission,Noise,Quality} → …
Ksa/Fx/PlumeActuator.cs:123  VolumetricExhaustTemplate.{LengthWeights,Absorption,Emission,Noise,Quality} writes: …
Ksa/Fx/PlumeActuator.cs:195  Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Parts.RocketNozzles.ModulesAndAllStates; …
Ksa/Fx/TrailActuator.cs:69   Program.Instance.ClearPlumeTrails() → VolumetricTrailRenderer.ClearPlumeTrails()
Ksa/Fx/TrailActuator.cs:100  VolumetricTrailRenderer.{MaxDistance,VoxelDepthFirstSliceThickness,MinStepSize,…
Ksa/Fx/TerrainActuator.cs:108  Universe.CurrentSystem.All.UnsafeAsList(); PlanetRenderer.RenderUboSlot(Celestial); …
Ksa/Fx/TerrainActuator.cs:133  PlanetRenderer.PlanetUbo.{TanMeanSlopeRoughness,HapkeMeanAlbedo,BiomeBlendStrength,…
Ksa/Fx/TerrainActuator.cs:166  Celestial.BodyTemplate.HeightReference.{Minimum,Maximum}; …
Ksa/Fx/FaceFxManager.cs:82   Program.Instance.ParticleSystem.{EmitterPool.Get,InitializeEmitter}; …
Ksa/Fx/FxEditorReader.cs:110  Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Parts.RocketNozzles.ModulesAndAllStates; …
Ksa/Fx/FxEditorReader.cs:168  Universe.CurrentSystem.All.UnsafeAsList(); AtmosphericBody.BodyTemplate.CloudsReference
Ksa/Fx/FxEditorReader.cs:229  Universe.CurrentSystem.All.UnsafeAsList(); PlanetRenderer.RenderUboSlot/MeshUboSlot
Ksa/Fx/FxEditorReader.cs:265  PlanetRenderer.Wireframe (public instance field)
Ksa/Camera/CameraReader.cs:42   IGameViewport.{Mode,MapController} (interface properties); MapController.Scope (public double); Camera.{Following,TidalLocking,PositionEcl,LocalRotation,GetFieldOfView,Orthographic}
Ksa/Camera/CameraReader.cs:125  CameraMode enum { Orbit, Free, Map, IVA, Fixed }
Ksa/Camera/CameraDirector.cs:201  Program.MainViewport (IGameViewport); IGameViewport.{Mode,GetCamera,SetCameraMode,BaseCamera,FixedController}; GameViewport.FixedController setter via ViewportSeam; ViewportBase.Mode setter via ViewportSeam; …
Ksa/Camera/CameraDirector.cs:342  Camera.{SetFollow,Unfollow,LocalPosition,LocalRotation,NoRotation,SetFieldOfView,…
Ksa/Camera/CameraDirector.cs:637  Camera.{LookAtRotation,SetFieldOfView,SetOrthographic,SetOrthoHalfHeight}; …
Ksa/Camera/CameraDirector.cs:750  Universe.SetSimulationSpeed(double, alert:false); Universe.GetSimulationSpeed(); …
Ksa/Camera/CameraDirector.cs:977  Camera.PositionCce composition (LocalPosition ⇄ IFollowable.GetBodyFixed2Ecl unless …
Ksa/Camera/CameraDirector.cs:1021 Program.MainViewport; IGameViewport.SetCameraMode(CameraMode)
Ksa/Camera/CameraDirector.cs:1049 Program.MainViewport.{BaseCamera,MapCamera}; Camera.{SetFollow,Unfollow}
Ksa/Camera/CameraDirector.cs:1088 Camera.{Following,TidalLocking,SetFollow,PositionEcl}
Ksa/Camera/CameraDirector.cs:1129 Program.MainViewport.MapController; MapController.Scope
Ksa/Camera/CameraViewportPatch.cs:23  GameViewport.OnFrame(double) (Harmony prefix/postfix); Program.MainViewport (IGameViewport)
Ksa/Camera/ViewportSeam.cs:48  GameViewport.FixedController { get; protected set; } (compiler-generated setter, reached by reflection)
Ksa/Camera/ViewportSeam.cs:81  ViewportBase.Mode { get; protected set; } (compiler-generated setter, reached by reflection)
Ksa/Camera/CameraTargets.cs:69   Universe.CurrentSystem.Get(id) → Astronomical; Vehicle; IParentBody (Celestial, StellarBody, Barycenter)
Ksa/Camera/CameraTargets.cs:121  Astronomical.GetPositionEcl(); Vehicle.{CenterOfMassAsmb,GetBodyFixed2Ecl}; …
Ksa/Camera/CameraTargets.cs:142  Astronomical.GetVelocityEcl()
Ksa/Camera/CameraTargets.cs:153  IOrientation.GetBodyFixed2Ecl(); Part.Asmb2VehicleAsmb; …
Ksa/Camera/CameraTargets.cs:183  IParentBody.GetBodyFixed2Ecl() (north +Z); Vehicle.ComputeBody2Cce axis convention (+X fwd, +Y …
Ksa/Camera/CameraTargets.cs:210  Camera.Following → IFollowable; Astronomical.Id
Ksa/Camera/CameraTargets.cs:228  Universe.CurrentSystem.All.UnsafeAsList()
Ksa/Camera/CameraFrames.cs:55   Vehicle.{GetEnu2Cce,GetLvlh2Cce,Body2Cce,ComputeEnu2Cce,ComputeLvlh2Cce,GetPositionCce,…
Ksa/Camera/CameraFrames.cs:216  Celestial.{GetDirCcfFromLatLon,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius,…
Ksa/Camera/CameraFrames.cs:245  Celestial.{GetPositionCceFromEcl,GetCcf2Cce,GetTerrainHeightFromDirCce,MeanRadius}
Ksa/Camera/CameraFollowable.cs:86  IFollowable : IObjectId, IPosition, IVelocity, IOrientation, IRadius { OrbitView }; …
Ksa/Camera/CameraPoseController.cs:76  GameViewport.FixedController (protected-set property, installed through ViewportSeam); FixedController(Camera, string) unsealed, …
Ksa/Render/IvaForceRender.cs:43  PartModel.Instances; PartModel..ctor(PartModelModule.Template) (protected); PartModelModule.Template.Internal; …
Ksa/Render/VesselForceRender.cs:114  Vehicle.GetWorldMatrix(Camera) (prefix); Vehicle.UpdateRenderData(IViewport,int) (prefix, virtual)
Ksa/Render/VesselForceRender.cs:183  Camera.GetPositionEgo(Vehicle); Vehicle.Body2Cce; float3.Pack; floatQuat.Pack
Ksa/Render/VesselForceRender.cs:213  Vehicle.GetMatrixAsmb2Ego(Camera); IViewport.GetCamera(); Vehicle.IsEditedVehicle; PartTree.UpdateRenderData(in double4x4,bool,IViewport,int)
Ksa/ThugLife/ThugLifeTextureFactory.cs:48  SimpleVkTexture(name, Renderer.Allocator, …, VkFormat.R8G8B8A8UNorm, …); …
Ksa/ThugLife/ThugLifeManager.cs:129  Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Parts.Parts; Part.InstanceId
Ksa/ThugLife/ThugLifeManager.cs:225  Program.RenderedViewport (IViewport; _renderedViewport, set at the top of RenderViewport and …
Ksa/ThugLife/ThugLifeManager.cs:271  Program.GetRenderer()
Ksa/ThugLife/ThugLifeManager.cs:345  Universe.CurrentSystem.All.UnsafeAsList()
Ksa/ThugLife/ThugLifeQuadRenderer.cs:106  Program.OffscreenTarget.SetupGraphicsPipeline(ref VkGraphicsPipelineCreateInfo); …
Ksa/ThugLife/ThugLifeQuadRenderer.cs:264  Program.GetRenderCamera() (RenderedViewport.GetCamera()); Camera.MVP.viewProjection; …
Ksa/ThugLife/ThugLifeRenderPatches.cs:20  SuperMeshRenderSystem.RenderMainPass(IViewport, CommandBuffer) — Harmony postfix
Ksa/Iva/IvaPhysicsManager.cs:118  Vehicle.{Id,Parts}; Part.{InstanceId,SubParts,PartParent,DisplayName,Template.Id,…
Ksa/Iva/IvaPhysicsManager.cs:303  JobSystems.VehicleSolver.Wait(); Universe.{CurrentSystem.All.UnsafeAsList,SimulationSpeed}; …
Ksa/Iva/IvaPhysicsManager.cs:677  Part.{Modules,Scale}; ModuleList.Get<PartModelModule>(); PartModelModule.PartModel.Template; …
Ksa/Iva/IvaPhysicsManager.cs:721  ModuleList.Get<PartModelModule>(); PartModelModule.Template.{Internal,RayTracing}
Ksa/Iva/IvaPhysicsManager.cs:735  Vehicle.Parts.Parts; Part.{SubParts,InstanceId}
Ksa/Iva/IvaPhysicsManager.cs:748  Universe.CurrentSystem.All.UnsafeAsList()
Ksa/Iva/InteriorGeometry.cs:64  Vehicle.Parts.Parts; Part.{SubParts,InstanceId,Modules,MatrixAsmb2VehicleAsmb,…
Ksa/Iva/FloatingObject.cs:96   Part.{PositionParentAsmb(set),Asmb2ParentAsmb(set),PartParent,PositionVehicleAsmb,…
Ksa/Iva/FloatingObject.cs:147  Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}
Ksa/Iva/FloatingObject.cs:160  Part.{PositionParentAsmb(set),Asmb2ParentAsmb(set)}
Ksa/Actuators/ThrottleActuator.cs:20  Vehicle._manualControlInputs.EngineThrottle (private; set by reflection)
Ksa/Actuators/TranslateActuator.cs:29  Vehicle._manualControlInputs.ThrusterCommandFlags (private; set by reflection); ThrusterMapFlags
Ksa/Actuators/TranslateActuator.cs:81  Vehicle.GetThrusterFlags()
Ksa/Actuators/RotateActuator.cs:32  Vehicle._manualControlInputs.ThrusterCommandFlags (private; set by reflection); ThrusterMapFlags
Ksa/Actuators/RotateActuator.cs:85  Vehicle.GetThrusterFlags()
Ksa/Actuators/LightActuator.cs:30   Vehicle.LightsOn; PowerConsumer.LightSwitch/.LightIsActive
Ksa/Actuators/LightActuator.cs:43   LightModule.Parent.FullPart.LightSwitch.LightIsActive
Ksa/Actuators/LightActuator.cs:55   LightModule.Template.Intensity.Value (per-instance, after template clone)
Ksa/Actuators/LightActuator.cs:67   LightModule.Template.ColorRgb.{R,G,B} + OnDataLoad (per-instance, after template clone)
Ksa/Actuators/LightActuator.cs:85   LightModule.Template.OuterAngle.Value (per-instance, after template clone)
Ksa/Actuators/LightActuator.cs:106  LightModule.Template.InnerAngle.Value (per-instance, after template clone)
Ksa/Actuators/AudioActuator.cs:59   GameAudio.System.TryPlaySound(sound, group, paused, out channel); GameAudio.GetChannelGroup(ChannelGroupType); Fmod Channel.TrySet{…}
Ksa/Actuators/AudioActuator.cs:150  GameAudio.System.TryCreateSound(bytes, Mode.OpenMemory|…, in exInfo{Length}, out sound); Sound.TryGetLength/TryRelease
Ksa/Actuators/AudioActuator.cs:273  Fmod Channel.TryIsPlaying/TryGetPosition/TryStop; Sound.TryRelease
Ksa/Actuators/EngineActuator.cs:14   Vehicle.SetEnum(VehicleEngine.MainIgnite)
Ksa/Actuators/EngineActuator.cs:23   Vehicle.SetEnum(VehicleEngine.MainShutdown)
Ksa/Actuators/EngineActuator.cs:35   EngineController.SetIsActive(Vehicle, bool)
Ksa/Actuators/EngineActuator.cs:47   EngineController.MinimumThrottle (float, settable); PartTree.MarkDerivedDirty(DerivedData.RocketControls)
Ksa/Actuators/StagingActuator.cs:13  vehicle.Parts.SequenceList.ActivateNextSequence(vehicle); Vehicle.UpdateAfterPartTreeModification()
Ksa/Actuators/RcsActuator.cs:12  ThrusterController.SetIsActive(Vehicle, bool) over all controllers
Ksa/Actuators/RcsActuator.cs:25  ThrusterController.SetIsActive(Vehicle, bool)
Ksa/Actuators/AnimationActuator.cs:15  KeyframeAnimationModule.TimeGoal = fraction × Shared.Duration
Ksa/Actuators/DebugActuator.cs:16   Universe.SetSimulationSpeed(double, alert:false)
Ksa/Actuators/DebugActuator.cs:26   Program.GetMainCamera().SetFollow(vehicle, changeControl:false); Program.ControlledVehicle = vehicle
Ksa/Actuators/DebugActuator.cs:37   Orbit.CreateFromStateCci + Vehicle.Teleport + Vehicle.UpdatePerFrameData (physics-bypass teleport pattern)
Ksa/Actuators/DebugActuator.cs:68   Vehicle.{GetPositionCci,GetVelocityCci,GetBody2Cci,TotalMass,Parent} + Orbit.CreateFromStateCci + Vehicle.Teleport + Vehicle.UpdatePerFrameData
Ksa/Actuators/DebugActuator.cs:113  Vehicle.RefillConsumables() → PartTree.RefillConsumables → ResourceManager.RefillAllTanks
Ksa/Actuators/DebugActuator.cs:128  Battery.Refill(ref state) via Batteries.GetModuleAndAllMutableStatesForInitialization (eternal-flame)
Ksa/Actuators/ScaleActuator.cs:29   Vehicle.Parts.Parts; Part.{Scale(set),SubParts}; KittenEva._renderable._characterAvatar.Core.Scale (reflected)
Ksa/Actuators/ScaleActuator.cs:70   Part.Scale (get); KittenEva avatar Core.Scale (reflected)
Ksa/Actuators/CameraActuator.cs:50  Program.MainViewport.{BaseCamera,MapCamera}; Camera.SetFollow(IFollowable, tidalLocking:true, changeControl:false, alert:false)
Ksa/Actuators/DecouplerActuator.cs:15  Decoupler.IsActive / IsEnabled / SetIsActive(Vehicle, true)
Ksa/Actuators/FlightComputerActuator.cs:15   FlightComputer.{AttitudeMode,AttitudeTrackTarget}; FlightComputerAttitudeMode/...TrackTarget
Ksa/Actuators/FlightComputerActuator.cs:44   FlightComputer.RCSMode (FlightComputerRCSMode)
Ksa/Actuators/FlightComputerActuator.cs:61   FlightComputer.RCSMode (read)
Ksa/Actuators/FlightComputerActuator.cs:65   FlightComputer.AttitudeFrame (VehicleReferenceFrame)
Ksa/Actuators/FlightComputerActuator.cs:75   FlightComputer.{CustomAttitudeTarget,AttitudeFrame}; VehicleReferenceFrameEx.{GetEclBody2Cci,QuaternionToEulerAngles}
Ksa/Actuators/FlightComputerActuator.cs:107  FlightComputer.Burn = new BurnTarget{ImpulsiveInstant,DeltaVTargetCci}
Ksa/Actuators/DockingActuator.cs:24  DockingPort.Docked; InputEvents.VehicleDockingInputData{Undock=true} → DockingPort.Undock → Vehicle.Split(Connector, PushoffImpulse)
Ksa/Actuators/DockingActuator.cs:50  DockingPort.PushoffImpulse (public required float, seeded from DockingPortTemplate.PushoffImpulse)
Ksa/Readers/PartsReader.cs:30   Vehicle.Parts.Parts (ReadOnlySpan<Part>), .Count; Part.{InstanceId,Id,DisplayName,Template.Id,PartParent,SubParts,PositionVehicleAsmb}
Ksa/Readers/BodyReader.cs:54    CelestialSystem.{All,HomeBody,GetWorldSun()}; Celestial/StellarBody/Barycenter type dispatch
Ksa/Readers/BodyReader.cs:88    Celestial.{Id,Class,Parent,Children,Mass,MeanRadius,SphereOfInfluence,GetAngularVelocity,Orbit}; IParentBody.{Mu,GetAtmosphereReference,GetOceanReference}; Orbit.IsBound()
Ksa/Readers/BodyReader.cs:124   StellarBody.{Id,Mass,MeanRadius,SphereOfInfluence,Children}; OrbitingStar.{Parent,Orbit}; IParentBody.Mu
Ksa/Readers/BodyReader.cs:151   Barycenter.{Id,Children,Mass,MeanRadius,SphereOfInfluence,GetPositionEcl,GetVelocityEcl}; IParentBody.Mu
Ksa/Readers/AnimationLinks.cs:46  KeyframeAnimationModule.Parent.SubtreeModules.HasAny<SolarPanel>(); SolarPanel.KeyframeAnimationModule; LightModule.Parent.FullPart.SubtreeModules.Get<KeyframeAnimationModule>()
Ksa/Readers/VesselReader.cs:38   Vehicle core state (Id/Situation/GetPositionCci/OrbitalSpeed/masses/attitude)
Ksa/Readers/VesselReader.cs:175  Vehicle.{GetPositionEcl,GetVelocityCci,CenterOfMassAsmb,GetManualThrottle}
Ksa/Readers/VesselReader.cs:261  Vehicle.IsSet(VehicleEngine.MainIgnite, false) => _manualControlInputs.EngineOn
Ksa/Readers/VesselReader.cs:269  Vehicle.IsControllable => _overrideIsControllable || Parts.Controls.NumModules > 0
Ksa/Readers/VesselReader.cs:278  Vehicle.GetManualThrottle(); FlightComputer.{AttitudeMode,AttitudeTrackTarget,AttitudeFrame}
Ksa/Readers/VesselReader.cs:323  Orbit.StateVectors.TrueAnomaly.Degrees; LongitudeOfAscendingNode/ArgumentOfPeriapsis (rad); Orbit.{Apoapsis,Periapsis,IsBound()}; Vehicle.NextApoapsisTime/NextPeriapsisTime/NextPatchEventTime (UniverseTime)
Ksa/Readers/VesselReader.cs:380  Vehicle.NavBallData{AttitudeAngles(int3,deg),ThrustWeightRatio,DeltaV,Frame,Speed}
Ksa/Readers/VesselReader.cs:406  Vehicle.PhysicsEnvironment{AtmosphericPressure,AtmosphericDensity,OceanDensity,TerrainRadius}; PhysicalAtmosphereReference.GetDynamicPressure(Vehicle); AccelerationBody/AngularAccelerationBody
Ksa/Readers/VesselReader.cs:434  vehicle.Parts.Modules.Get<EngineController>(); .IsActive, .VacuumData{ThrustMax,MassFlowRateMax}
Ksa/Readers/VesselReader.cs:446  EngineControllerState{CommandThrottle,IsPropellantAvailable} via ModuleStateful.TryGetFrom
Ksa/Readers/VesselReader.cs:497  vehicle.Parts.Modules.Get<Tank>().Moles; vehicle.Parts.Moles.GetState(mole).Mass; Mole.FilledFraction; Mole.GetStoredMass(ContainerVolume)
Ksa/Readers/VesselReader.cs:540  vehicle.Parts.RocketCores.{Modules,GetState} filtered to SolidMotor; SolidMotor.{…}; SolidGrainSegment.{…}; RocketCoreState.{…}
Ksa/Readers/VesselReader.cs:668  vehicle.Parts.Batteries.GetState(b).Charge, b.MaximumCapacity
Ksa/Readers/VesselReader.cs:688  vehicle.Parts.PowerConsumers.GetState(c).Consumed (Watts; .Value() float)
Ksa/Readers/VesselReader.cs:701  vehicle.Parts.Modules.Get<ThrusterController>(); .IsActive; ThrusterControllerState{ControlMap,IsPropellantAvailable}
Ksa/Readers/VesselReader.cs:732  vehicle.Parts.Modules.Get<SolarPanel>(); SolarPanelState{Produced,IsOccluded,SunAoA,SunEfficiency}; SolarTrackerState.CurrentAngle
Ksa/Readers/VesselReader.cs:786  vehicle.Parts.Modules.Get<Generator>(); GeneratorState{Active,Produced (Watts)}
Ksa/Readers/VesselReader.cs:809  vehicle.Parts.Modules.Get<LightModule>(); .Template.Intensity.Value/.ColorRgb/.OuterAngle.Value/.InnerAngle.Value; Parent.FullPart.LightSwitch.LightIsActive
Ksa/Readers/VesselReader.cs:841  vehicle.Parts.Modules.Get<DockingPort>(); .Docked, .DockedToPart.Id, .PushoffImpulse
Ksa/Readers/VesselReader.cs:866  vehicle.Parts.Modules.Get<Decoupler>(); .IsActive (fired, irreversible); .IsEnabled
Ksa/Readers/VesselReader.cs:894  vehicle.Patch.Encounters; Encounter{Body.Id,GameTime,ClosestDistance}
Ksa/Readers/VesselReader.cs:931  vehicle.Parts.Modules.Get<KeyframeAnimationModule>(); .TimeGoal, .Shared.Duration, State.{TimeCurrent,DeploymentState}
Ksa/Welds/WeldManager.cs:97   JobSystems.VehicleSolver.Wait()
Ksa/Welds/WeldManager.cs:187  Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Parts.Parts; Part.{InstanceId,SubParts}
Ksa/Welds/WeldEngine.cs:19    Vehicle.{GetPositionCci,GetVelocityCci,GetBody2Cci,BodyRates,CenterOfMassAsmb,Parent,Orbit,Teleport,UpdatePerFrameData}; Orbit.OrbitLineColor; IParentBody.GetCci2Cce; Orbit.CreateFromStateCci(IParentBody,UniverseTime,double3,double3,byte4); Universe.GetJobSimStep(double).NextTime; Program.GetPlayerDeltaTime; Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}
Ksa/Welds/WeldEngine.cs:116   Vehicle.{GetPositionCci,GetBody2Cci,CenterOfMassAsmb}; Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}
```

## Appendix B — Key config gates (`gatOS.GameMod/Configuration/gatos.default.toml`; defaults)

`sample_rate_hz=10`, `telemetry_enabled=true`, `telemetry_vessel_detail=true`, `telemetry_vessel_parts=true`, `telemetry_bodies=true`, `telemetry_bodies_rate_hz=0`, `telemetry_events=true`, `control_enabled=true`, `control_all_vessels=true`, `debug_namespace=true`, `command_timeout_ms=2000`, `max_commands_per_frame=64`, `http_enabled=true` (`127.0.0.1:4242`, `http_field_endpoints=true`), `mcp_enabled=true` (`:4243`), `mqtt_enabled=true` (`:1883`, `mqtt_field_topics=true`, `field_feed_hz=4`, `mqtt_publish_hz=0`), `serial_*` off, `display_enabled=false` (15 fps 320×180 rgba-zlib), `paint_parts_enabled=false`, `paint_kittens_enabled=false`, `paint_textures_enabled=true`, `paint_stickers_enabled=true`, `audio_enabled=true` (16 channels), `camera_enabled=true` (fov 1..179, `camera_release_blend_s=0.6`, `camera_allow_time_channel=true`), `schedule_enabled=true` (`schedule_max_live=16`, `schedule_default_clock=render`), `iva_physics_enabled=false`. Full table with clamps: `SPEC_9P_FILESYSTEM.md §2.5:121-183`.

## Appendix C — Where to read next in the repo

- Architecture bible: `AGENTS.md` (threading rules `:531-640`, `/sim` constitution `:693-746`); runtime shape: `docs/ARCHITECTURE.md`.
- `/sim` catalog: `SPEC_9P_FILESYSTEM.md`; MCP: `SPEC_MCP.md`; OpenAPI: `sim_openapi.yml`.
- Per-member KSA bindings with game-version history: `docs/KSA_INTEGRATION_MATRIX.md` (136 KB), `scope/ksa-read-surface.md`, `scope/ksa-write-surface.md`, `scope/ksa-runtime-coupling.md` (Harmony/driver/reflection census), `scope/ksa-assets-and-versions.md`.
- As-built plans for the stateful features: `plans/SCHEDULER_ASBUILT.md`, `plans/CAMERA_ASBUILT.md`, `plans/PAINT_ASBUILT.md`, `plans/IVA_MOVEMENTS.md`, `plans/FX_EDITORS_PLAN.md`, `STREAM_PLAN.md`.
- Mod scaffolding idioms shared across the meow-sci mods: `.claude/skills/ksa/{lifecycle,telemetry,vehicle-api,debug}.md`, `.claude/skills/harmony/SKILL.md`.
