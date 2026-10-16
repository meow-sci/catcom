---
type: Research Report
title: KSA hook-point research report (2026-10-16)
description: Verbatim research report produced by a delegated research pass on 2026-10-16; the primary source the distilled concepts in this bundle cite. Claims inside are tagged verified/inference/measured by the author of the report.
tags: [reference, report, raw]
generated: { by: claude-fable-5.1/ksa-scripting-research-subagent, at: 2026-10-16T20:00:00Z }
status: stable
---

> Mirrored from the session scratchpad into the bundle so the cited source outlives the session. Scratchpad paths mentioned inside (e.g. harness locations) no longer exist.

# KSA in-process scripting hook reference

Source: decompiled Kitten Space Agency build **2026.10.10.5554** (`ksa-game-assemblies/current/version.json`), .NET 10 / C# 13.
All paths below are relative to `ksa-game-assemblies/current/decomp/`. Line numbers refer to that decompilation.
Signatures are copied verbatim from the source. "Verified" means read in the source; "not found" means a grep across `KSA/`, `KSA.*/`, `Core/`, `Brutal*/`, `RenderCore/` returned nothing.

Cross-check against the existing gatOS skill docs (`gatOS/.claude/skills/ksa/*.md`): corrections are flagged inline with **[DOC DRIFT]**.

---

## 0. Executive findings (read this first)

1. **The vehicle sim runs off the main thread, concurrently with everything a StarMap hook sees.** `Program.PrepareFrame` waits for last frame's solvers, applies their results into `Vehicle` objects, applies the deferred `InputEvents` buffers, then **kicks the next sim step** (`Universe.ExecuteNextVehicleSolvers`) and returns. ImGui, the StarMap `BeforeGui`/`AfterGui`/`OnFrame`-postfix hooks, and rendering all run while `JobSystems.VehicleSolver` + `VehicleWorkerPool` threads are reading `Vehicle`/`PartTree`/`Modules` (`KSA/Program.cs:2141-2236`, `KSA/Universe.cs:2126-2139`, `KSA/VehicleUpdateState.cs:380-392`).
   - **Reading** vehicle state from StarMap hooks is safe (workers write only into `VehicleUpdateData.New*` scratch; `Vehicle` fields are written only on the main thread in `Universe.ApplyVehicleSolvers`).
   - **Writing** vehicle/part/module state from StarMap hooks races with worker reads. The only sim-safe mutation window is inside `PrepareFrame` between `JobSystems.VehicleSolver.Wait()` and `Universe.ExecuteNextVehicleSolvers(...)`. A `Harmony` **prefix on `Universe.ExecuteNextVehicleSolvers`** (static) is the right synchronous "before solver" point; a **postfix on `Universe.ApplyVehicleSolvers`** is the right "sim step just landed" observation point.
2. **KSA already has a command queue**: `KSA.InputEvents` (`KSA/InputEvents.cs`) — ~24 static `TypedBuffer<T>` fields of `IApplicable` structs, drained once per frame in `InputEvents.ApplyInputEvents()` (called from `PrepareFrame`). Every gameplay mutation the UI performs (engine on/off, decouple, dock/undock, burn edits, stage/sequence edits, sim speed, auto-warp, target, control part, teleport, destroy/explode, refill, vehicle creation, EVA) goes through it. The buffers are **not thread-safe** (`TypedBuffer<T>.Add` is a plain array append, no lock) — enqueue only on the main thread. A scripting runtime should route mutations through these buffers rather than poking fields.
3. **The console is a first-class, public, mod-extensible command system** (`Brutal.ImGuiApi.Abstractions.TerminalInterface`), reachable as `Program.TerminalInterface`. Mods can `RegisterCommand(Delegate, object owner)` with a `[TerminalAction("name", ...)]`-attributed method and `UnregisterObject(owner)` on unload; `Execute(string)` runs `;`-separated command strings synchronously. KSA already runs command lists at boot/load from `mod.toml` `[console] onBoot/onLoad` (Tomlet) and from `GameSettings.Current.Console.OnBoot/OnLoad`.
4. **There is no scripting language, expression evaluator, macro or sequence/program runner in KSA.** "Sequence" means staging. The FlightComputer has an autopilot (burn/attitude modes + BurnPlan) but no program model.
5. **There are essentially no C# events for gameplay.** Only `ViewportRegistry.ViewportRegistered/Unregistered`, `TerminalInterface.OnOutput`, and three networking events. Everything else needs Harmony.
6. **Saves are `System.Xml.Serialization.XmlSerializer` XML** (`universe.xml` + `vehicle.xml`), type-closed; settings/manifests are TOML via **Tomlet**. Script state must be a sidecar file keyed off `UncompressedSave.Directory`.
7. **No main-thread dispatcher exists** (no `SynchronizationContext`, no dispatcher type). Mods must own a queue and drain it at one of the main-thread points in §8.
8. **Nothing in KSA is unload-aware** (no `AssemblyLoadContext` use). Every delegate/object a mod hands to the game (terminal commands, `OnOutput` subscribers, Harmony patches) must be removed before unloading.

---

## 1. Frame / game loop anatomy

### 1.1 Entry

- `KSA/Program.cs:1693` — `private static int Main(string[] inArgs)` → `ProcessCliArgs` → `using Program program = new Program(inArgs); return program.Run();`
- `KSA/App.cs:39` — `public int Run()` (base class `App`): a `while (!_exitCode.HasValue)` loop. Computes `dtPlayer = double.Min(frameDelta, 1f / GameSettings.Current.Simulation.MinTargetFrameRate)` and calls `protected abstract void OnFrame(double currentPlayerTime, double dtPlayer)` (`App.cs:37`). `App.RequestExit(int)` ends the loop. Static `App.FrameTimeMs / FramesPerSecond / InputToDisplayMs`.
- The `Program(string[])` constructor does all initialization synchronously (console window, logging, renderer, commands, mods, system selection, `Universe.LoadSystem`), so `Program.Instance` and `Program.ConsoleWindow` exist before any frame.

### 1.2 `Program.OnFrame` — exact order (`KSA/Program.cs:2238-2334`)

```csharp
protected override void OnFrame(double currentPlayerTime, double dtPlayer)
```
1. `Profiler.MainThread.BeginFrame(); ProfilerUi.OnFrame();`
2. **`PrepareFrame(currentPlayerTime, dtPlayer)`** (`private OnFrameResult PrepareFrame(double currentPlayerTime, double dtPlayer)`, `Program.cs:2141-2236`). This is the whole sim pipeline for the frame:
   1. `RebuildRenderer()` if needed; `_screenshotCapture.OnPrepareFrame`; `ExplosionFlashSystem.Update((float)Universe.GetNextSimStep().DeltaTime)`.
   2. **Wait** for last frame's jobs: `JobSystems.NearestOrbitAndPerformanceWorker.Wait(); JobSystems.OrbitSolvers.Wait(); JobSystems.VehicleSolver.Wait(); JobSystems.ClothSolvers.Wait();`
   3. **Apply** results on the main thread: `Universe.ApplyOrbitSolvers(); Universe.ApplyVehicleSolvers(); Universe.ApplyClothSolvers();`
   4. `JobSystems.*WorkerPool.SetParkTimeout(dtPlayer * 0.5)`; `_renderer.WaitForPreviousPresent(...)`.
   5. `Glfw.PollEvents();` (keyboard/mouse callbacks fire here → `Program.OnKey` etc. → they enqueue into `InputEvents`).
   6. **`InputEvents.ApplyInputEvents();`** — drains all deferred command buffers (engine/decouple/dock/burn/sim-speed/teleport/destroy/create ...).
   7. Editor teardown: `if (!EditorFlag && Editor != null) { Editor.Dispose(); Editor = null; CrewAssignmentWindow.Close(); VehicleSaves.Close(); }`
   8. `RefreshVehiclesInFrame();` then for each `VehiclesInFrame`: `vehicle.FlightComputer.RaisePendingAlerts(vehicle);` (auto-burn complete / out-of-propellant alerts).
   9. Queue + execute `_nearestOrbitPointJob` on `NearestOrbitAndPerformanceWorker`.
   10. `_lastPlayerTime = currentPlayerTime; _lastPlayerDeltaTime = dtPlayer; _frameViewport = MainViewport;`
   11. Pause logic: editor open → `Universe.AutoWarpStop(false); Universe.SetSimulationSpeed(0.0, alert:false)`; else `UpdateMenuPause()` (EscMenu/GameSettings/GameSaves/VehicleSaves open → speed 0, restore afterwards).
   12. `Universe.ProcessAutoWarp(dtPlayer);`
   13. **`SimStep jobSimStep = Universe.GetJobSimStep(dtPlayer);`**
   14. `Universe.ExecuteNextClothSolvers(dtPlayer, jobSimStep);`
   15. `PartTree.FlushDirtyDerived(); PartTree.FlushDirtyResourceManagers();` (static; `PartTree.cs:499`, `:816 internal`).
   16. **`Universe.ExecuteNextVehicleSolvers(dtPlayer, jobSimStep);`** — kicks the vehicle sim (async).
   17. `Universe.ExecuteNextOrbitSolvers(dtPlayer, jobSimStep);` — kicks celestial orbit jobs (async).
   18. `Network.Tick();` window-close / font-rebuild checks.
3. `GaugeCanvas.UpdateCanvasesOnFrame()` (if `ControlledVehicle != null`) else `BurnCanvas?.OnFrame()`.
4. `GizmosRenderer.ResetInstances(); PrepareImGui(dtPlayer);` (ImGui new frame).
5. `OnFrameEditor(dtPlayer);` (`Program.cs:2567` — creates `Editor = new VehicleEditor(ControlledVehicle, newVehicle:false)` when `EditorFlag && Editor == null`, sets `ControlledVehicle = null`, speed 0), `OnFrameViewports(dtPlayer);`
6. `if (DrawUI) { OnDrawUiFrame(dtPlayer); OnDrawUiViewports(dtPlayer); }` then `if (DrawUI) OnDrawUiThreadSafe(dtPlayer);` (menu bar, patch/resource windows, gauges, EscMenu, `Universe.OnDrawUi`, `TransferPlanner`, controller UI, `Editor?.OnDrawUi`, `LaunchMenu?.DrawLaunchUi`).
   - `private void OnDrawUiFrame(double dt)` (`Program.cs:3131`): alerts, popups, shadow/light debug, celestial windows, `GameSettings.OnDrawUi`, `ImGuiWindow.DrawAllStaticWindows`, renderer debug UIs, stats, etc. **StarMap prefixes this.**
   - `private void OnDrawUiViewports(double dt)` (`Program.cs:3162`): secondary game viewports' `DrawImGui()`. **StarMap postfixes this.**
7. `DrawFps(); OnDrawUiConsole(dtPlayer);` (`Program.cs:3119` → `ConsoleWindow.Draw(position, size, (float)dt)`).
8. Sequence-performance job (editor), **`ImGui.Render();`**
9. `if (!IsLoaded) return;`
10. `OnFrameLaunchMenu; OnFrameHoveredOrbiters; ProfilerCapture.OnFrame; UpdateGauges(ControlledVehicle, dt) / UpdateBurnEditorGauges; GaugeData.AnimateAll(dt * max(1, SimulationSpeed));`
11. `SafeDisposeBufferWrapper.CheckQueuedBuffers(); RetiredResourceQueue.CheckQueued(); StreamingUploader?.Poll(); OnFrameController(dt)` (camera controllers), `LightSystem.ClearLights(); LightSystem.OnFrame(...); OnFrameCelestials(dt);`
12. `OnPreRender(dt)` → `Render(dt, ...)` → `PostRender(dt)` (`TrySubmitFrame`, `FrameNumber++` after, `GameAudio.UpdateAudio(dt)`, `_vehiclesInFrame.Clear()`).

**StarMap's `Program.OnFrame` postfix therefore runs after the GPU submit, with the vehicle solver for this frame still running** (it is only waited on at the next `PrepareFrame`).

### 1.3 Where the simulation actually runs

- `KSA/Universe.cs:2126` —
  ```csharp
  public static void ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)
  {
      if (CurrentSystem != null) {
          PrepareVehicleWorkers(simStep);              // main thread: Vehicle.PrepareWorker(simStep) per vehicle
          SyncGroundClutter();
          _vehicleUpdateTask.PrepareFrame(CollectionsMarshal.AsSpan(_orphanVehicles), simStep);
          JobSystems.VehicleSolver.Queue(_vehicleUpdateTask, 1);
          _nextPlayerDeltaTime = dtPlayer; _nextSimStep = simStep;
          JobSystems.VehicleSolverProfiler.Prepare();
          JobSystems.VehicleSolver.ExecuteJobs();      // async kick
      }
  }
  ```
- `KSA/Universe.cs:2141` — `public static void PrepareVehicleWorkers(SimStep simStep)` iterates `CurrentSystem.All.OfType<Vehicle>()` calling `current.PrepareWorker(simStep)` (`KSA/Vehicle.cs:2396`, `public virtual void PrepareWorker(SimStep simStep)`): **main thread**, copies `_manualControlInputs` (throttle/engine-on/RCS flags) into the worker state. This is the last main-thread touch before the step.
- `KSA/JobSystems.cs` — `public static JobScheduler VehicleSolver` is `new JobScheduler(1u, "VehicleSolver")` with `ThreadPriority.Highest`; `OrbitSolvers` (N runners), `ClothSolvers`, `NearestOrbitAndPerformanceWorker`; `DynamicWorkerPool VehicleWorkerPool` / `RenderWorkerPool`.
- `Brutal.Concurrency.Jobs/JobRunner.cs:98` — `public void ExecuteJobs(JobCompletedDelegate? onCompleteHandler = null)` → `ThreadPool.UnsafeQueueUserWorkItem(_workExecutor, preferLocal: false)`. **JobRunner jobs execute on the .NET ThreadPool**, not dedicated threads. `JobScheduler.Queue(IJob, int)` throws `InvalidOperationException("Jobs in progress")` if not idle; `Wait()` blocks.
- `KSA.Concurrency/DynamicWorkerPool.cs:120` — the pool creates dedicated `new Thread(...) { Name = "VehicleWorker{i}", IsBackground = true, Priority = ThreadPriority.Highest }`; `ParallelBatch()` fans out `IBatchJob`s; `AwaitBatch` spins (and executes queued items inline) until the batch is done.
- `KSA/VehicleUpdateTask.cs:285` — `public void Run()` (the `IJob` body, on the ThreadPool runner): `TrimBubbles(); AssignOrphanVehicles(); MergeBubbles(); bubble.Prepare(_simStep); BuildContactIslands();` then `parallelBatch.Execute(this, _islandJobs...)` → `ContactIsland.Execute` → `PhysicsBubble.RunStep()` (`PhysicsBubble.cs:1065`: `BeginStepFrame(); StepFrameTo(SimStep.NextTime); EndStepFrame();`) then `CommitIslandClearances(); ReassignDivergentVehicles(); SplitBubbles(); bubble.PublishResults();`.
- Per-vehicle module/flight-computer updates run on worker threads: `KSA/VehicleUpdateState.cs:380` `public void UpdateModules(SimStep step)` builds a `ModuleUpdateContext { Step, States = GetReadOnlyStates(), Modules = ReadOnlyVehicle.Parts.Modules, Updaters = UpdateData.Updaters, Ctrl2Body = ReadOnlyVehicle.Ctrl2Body }` and calls `Gimbal/Tank/MagneticScoop/Parachute/Fx*/Generator/SolarPanel/PowerConsumer/SolarTracker.UpdateModules(in context)` (also `Rocket.UpdateModules`, `KeyframeAnimationModule.UpdateModules`). `FlightComputer.ComputeControl` is invoked from `PhysicsBubble.cs:1277, 1414, 1794` on the worker **against a per-step copy** `VehicleUpdateData.NewFlightComputer` (`VehicleUpdateData.cs:46,72,100`).
  - Note the worker reads `ReadOnlyVehicle.Parts.Modules` and `ReadOnlyVehicle.Ctrl2Body` etc. directly — this is the race a main-thread writer must avoid.

### 1.4 How results come back (main thread)

- `KSA/Universe.cs:1922` — `public static void ApplyVehicleSolvers()`: computes `_achievedSpeedFraction` (adaptive slowdown based on `JobSystems.VehicleSolver.GetMaxLastTickTime()`), then `_vehicleUpdateTask.ApplyResultsToMainThread(); _lastSimStep = _nextSimStep; _lastPlayerDeltaTime = _nextPlayerDeltaTime; CurrentSystem.UpdatePerFrameData();`
- `KSA/VehicleUpdateTask.cs:259` — `public void ApplyResultsToMainThread()`:
  1. `using (ParallelBatch ...) foreach bubble with IsResultsReady: parallelBatch.Execute<BubbleApplyResultsJob, PhysicsBubble>(bubble);` → per-vehicle `Vehicle.UpdateFromTaskResultsUnsynchronized(...)` (`Vehicle.cs:2435`) — runs **in parallel on worker threads while the main thread blocks in the batch**; copies `NewKinematicStates/NewVehicleProperties/NewPhysicsEnvironment/NewFlightPlan/NewStateVectors/NewBody2Cce/NewBodyRates/NewKinematicMeasurements/NewStructuralLoad` into the `Vehicle`, `FlightComputer.CopyFrom(updateData.NewFlightComputer)`, `Parts.UpdateFromTaskResults(in updateData)`.
  2. `bubble.RethrowApplyFailure()` — **rethrows worker exceptions on the main thread** (crash).
  3. `bubble.ApplySynchronizedResultsToVehicles()` → `Vehicle.UpdateFromTaskResultsSynchronized()` (`Vehicle.cs:2547`): `ApplyPendingParentChildrenFix()` (SOI re-parenting) and `StartCrewMissions()` (first launch).
  4. `bubble.ApplyRenderEventsToVehicles()` (`PhysicsBubble.cs:919`): applies `DockingEvent`, `GroundImpactEvent`, `WaterSplashEvent`, `PartFailureEvent`, then `VehicleDestructionEvent` (all `IVehicleRenderEvent.Apply(Vehicle)`).

### 1.5 Time, warp, timestep

- `KSA/SimStep.cs` — `public readonly struct SimStep { UniverseTime PreviousTime; UniverseTime NextTime; double DeltaTime; }`
- `KSA/Universe.cs:2615` —
  ```csharp
  public static SimStep GetJobSimStep(double dtPlayer)
  {   double num = _achievedSpeedFraction * GetSimulationSpeed();
      UniverseTime nextTime = _lastSimStep.NextTime; double num2 = dtPlayer * num;
      return new SimStep { PreviousTime = nextTime, NextTime = nextTime + num2, DeltaTime = num2 }; }
  ```
  **The outer sim step is variable (frame dt × speed × achieved fraction), not fixed.** Sub-stepping happens inside `PhysicsBubble` (contact islands step to `EarliestContact`; `VehicleUpdateTask.SegmentFloorSeconds() = Math.Max(1.0/60.0, _simStep.DeltaTime/1024.0)`).
- `public static UniverseTime GetElapsedTime() => _lastSimStep.NextTime` (`Universe.cs:2399`), `public static double GetElapsedSeconds()` (`:2394`), `GetLastSimStep()` (`:2381`), `GetNextSimStep()` (`:2387`, the step currently being computed).
  **[DOC DRIFT]** `Universe.GetElapsedSimTime()` (used throughout `SKILL.md`/`telemetry.md`) does **not exist** in 5554; it is `GetElapsedTime()` (returns `UniverseTime`, call `.Seconds()`).
- `UniverseTime` (`KSA/UniverseTime.cs`): readonly struct over `Int128 Nanoseconds`; `new UniverseTime(double seconds)`, `Seconds()`, `Zero`, `BeginningOfTime`, `EndOfTime`, `+`/`-` with `double` and `UniverseTime`, `Minutes/Hours/Days` etc.
- Warp API (`KSA/Universe.cs`): `public static double SimulationSpeed { get; private set; }` (`:122`, setter → `SetSimulationSpeed`), `public static void SetSimulationSpeed(double simulationSpeed, bool alert = true)` (`:2288` — **every path funnels here**, including per-frame `ProcessAutoWarp` and `UpdateMenuPause`), `SetSimulationSpeed(SimSpeed)` (`:700`), `SetNearestSimulationSpeed(double)`, `GetNearestQuickSpeed(double)`, `ResetSimulationSpeed()`, `IncreaseSimulationSpeed()`, `DecreaseSimulationSpeed()`, `IsPaused()` (`:1799`, `_simulationSpeed == 0.0`), `GetAchivedSpeedFraction()`, `CanAutoWarp()`, `WarpToNext()`, `AutoWarpTo(UniverseTime endTime, double simTimeMargin = 0.0, bool isUncapped = false)` (`:2259`), `AutoWarpStop(bool resetSimulationSpeed)` (`:2246`), `IsAutoWarpActive`, `AutoWarpTime`. Deferred equivalents: `InputEvents.SimSpeedBuffer` (`SimSpeedData { SimSpeed? Speed; InputAction Action; }`) and `InputEvents.AutoWarpBuffer` (`AutoWarpData { bool StopWarp; UniverseTime WarpToTime; double SimTimeMargin; bool IsUncapped; }`).
- Player (wall) time: `public static double GetPlayerTime()` / `GetPlayerDeltaTime()` (`Program.cs:5263/5268`).

### 1.6 Which thread, and the safe synchronous points

| Point | Thread | Solver state | Safe for |
|---|---|---|---|
| Harmony **postfix `Universe.ApplyVehicleSolvers()`** (`Universe.cs:1922`, static) | main | idle (just applied) | read fresh state; enqueue `InputEvents` (applied this same frame, since `ApplyInputEvents` runs after) |
| Harmony **prefix `InputEvents.ApplyInputEvents()`** (`InputEvents.cs`, static) | main | idle | enqueue into buffers for same-frame apply |
| Harmony **prefix `Universe.ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)`** (static) | main | idle, input applied, derived data flushed | **direct mutation of any Vehicle/Part/Module state** (what gatOS/eternal-flame/flexo already do) |
| Harmony prefix `Universe.PrepareVehicleWorkers(SimStep)` / `Vehicle.PrepareWorker(SimStep)` | main | idle | override manual control inputs per vehicle right before copy to worker |
| StarMap `BeforeGui` (prefix `Program.OnDrawUiFrame`) | main | **running** | read-only on sim state; ImGui ok; enqueue `InputEvents` ok (drained next frame) |
| StarMap `AfterGui` (postfix `Program.OnDrawUiViewports`) | main | running | same |
| StarMap `OnFrame` postfix | main | running | same; ImGui **not** ok (after `ImGui.Render`) |
| Anything on another thread | n/a | n/a | only thread-safe APIs: `ConsoleWindow.Print*` (uses `Lock _pendingLock`), logging |

Additional guard: `KSA/PartTree.cs:36-38, 194-222` has `private static readonly ReaderWriterLockSlim _structureLock`, `public static ReadScope ReadStructure()`, `public static bool TryEnterWriteStructure()` (50 ms), `public static void ExitWriteStructure()`, `public static void StampMainThread()` and a private `AssertStructureWritable()` that throws "A part tree was restructured off the main thread without PartTree.WriteStructure()". **In this build `StampMainThread()` is never called (no callers found) so `_mainThreadId == -1` and the assertion is a no-op; `ReadStructure()`/`TryEnterWriteStructure()` have no callers either.** Off-thread structural writes are therefore not detected — they silently race.

---

## 2. Event hook candidates

Legend: **T** = thread where it runs. All `InputEvents.*Data.Apply()` run on main in `PrepareFrame`. "`IActivate`" = `KSA/IActivate.cs` (`bool IsActive { get; } void SetIsActive(Vehicle? vehicle, bool activationState);` + default `Activate/Deactivate/ToggleIsActive`). "`IEnable`" = `KSA/IEnable.cs` (`bool IsEnabled {get;} void SetIsEnabled(bool enabled)`).

### 2.1 Engine ignition / shutdown
- **Request** (public, any caller, deferred): `KSA/EngineController.cs:79`
  `public void SetIsActive(Vehicle? vehicle, bool activationState)` — instance; builds `InputEvents.IActivateInputData { Operation = ActivateOp.Engine, Vehicle, EngineController = this, ActivationState }` and `InputEvents.IActivateInputBuffer.Add(data)`.
  Same shape: `ThrusterController.SetIsActive(Vehicle?, bool)` (`ThrusterController.cs:61`, `ActivateOp.Thruster`).
- **Apply** (T main): `InputEvents.IActivateInputData.Apply()` (`InputEvents.cs`, struct instance) → `EngineController.IsActive = ActivationState` (`public bool IsActive { get; internal set; }`, `EngineController.cs:49`); then queues `VehicleFlightComputerReset` → `Vehicle.FlightComputer.ReadUpdatedVehicleConfiguration(Vehicle)`. Batched by `private static void ApplyIActivateBatched()` which sorts by `ActivateOp` (Decouple first) and wraps decouples in `Parts.BeginBulkChange()/EndBulkChange()`.
- **Actual firing** (T worker, on the per-step copy): `KSA/FlightComputer.cs:400` `public void ComputeControl(ref readonly FlightComputerNavigation nav, ref readonly ManualControlInputs inputs, ref FlightComputerOutput outputs)` → `private static bool CommandEngineThrottles(UniverseTime commandTime, ref FlightComputerOutput outputs, out bool doIgnite)` (`:631`): `doIgnite = outputs.EngineThrottle > 0f && outputs.EngineBurnDuration > 0.0`; per active engine with propellant sets `EngineControllerState.CommandThrottle/CommandBurnTime/CommandTime`.
- **Observables on main thread after apply**: `EngineController.IsActive`; `KSA/EngineControllerState.cs` `{ bool IsPropellantAvailable; float CommandThrottle; double CommandBurnTime; UniverseTime CommandTime; bool WasActive; }`; `KSA/RocketCoreState.cs` `{ RocketCoreConditions Conditions; bool IsPropellantAvailable; float Throttle; double ThrustTimeRemaining; double MinThrustTimeRemaining; float MassFlowRate; bool ActivatedThisFrame; bool DeactivatedThisFrame; UniverseTime LastCommandTime; }` via `vehicle.Parts.RocketCores.States` (`ReadOnlySpan<RocketCoreState>`); `Vehicle.IsAnyEngineActive()` (`Vehicle.cs:6575`), `Vehicle.ComputeActiveThrust(float ambientPressure)` (`:6694`), `Vehicle.IsAnyEnginePropellantAvailable()` (`:6685`).
- **Harmony targets**: `EngineController.SetIsActive` (intent, any caller incl. staging), `InputEvents.ApplyInputEvents` (batch boundary), or poll `RocketCoreState.ActivatedThisFrame/DeactivatedThisFrame` in an `ApplyVehicleSolvers` postfix for the physical ignition/shutdown edge.

### 2.2 Throttle change
- Manual throttle lives in `private ManualControlInputs _manualControlInputs` (`Vehicle.cs:239`; fields seen: `EngineOn`, `EngineThrottle`, `ThrusterCommandFlags`, `Sprint`, `GrabHeld`). No public setter.
- Ramp applied on main thread in `Vehicle.PrepareWorker(SimStep)` (`Vehicle.cs:2396-2422`): `_engineFlags` ThrottleUp/Down → `EngineThrottle += dtPlayer * 0.7f`, clamped to `[GetMinThrottle(), 1]`; `if (FlightComputer.BurnMode == Auto) EngineOn = false`.
- Input path: `KSA/Vehicle.cs:3536` `public virtual void ProcessInput(InputAction action, GlfwKeyAction keyAction, GlfwModifier modifiers)` (MainEngineThrottleUp/Down flags, `MainEngineStartup/Shutdown` → `EngineOn`, `CameraUp` release → staging, `ToggleStabilization`, `ToggleManualThrustMode`, `ToggleRCS`, RCS translate/rotate flags). It is invoked via `InputEvents.VehicleInputData.Apply()` (`{ Vehicle; InputAction Action; GlfwKeyAction KeyAction; GlfwModifier Modifiers; }`), which `Vehicle.OnKey(GlfwKeyEvent)` (`:3274`) enqueues after `ControlsLockout` checks.
- Read: `public float GetManualThrottle()` (`:1259`), `public float GetMinThrottle()` (`:1264`), `FlightComputer.PlannedBurnThrottle` (public field), `FlightComputerOutput.EngineThrottle` (worker-side).
- Harmony targets: prefix `Vehicle.PrepareWorker(SimStep)` to set `_manualControlInputs.EngineThrottle` via reflection/AccessTools before the copy; or postfix `Vehicle.ProcessInput` to observe. A script API "set throttle" = reflection write to `_manualControlInputs` inside the `ExecuteNextVehicleSolvers` prefix window, or enqueue `VehicleInputData { Action = MainEngineThrottleUp, KeyAction = Press }` then `Release`.

### 2.3 Staging (sequence activation)
- `KSA/SequenceList.cs:171` `public void ActivateNextSequence(Vehicle vehicle)` — instance on `vehicle.Parts.SequenceList`; sets `ActiveSequence`, `TimedAlert.Create($"Sequence {ActiveSequence} activated", ...)`, calls `Part.ActivateSubtreeInStage(vehicle, number)` for each part in reverse, `RemoveSpentSequences()`. Caller: `Vehicle.ProcessInput` on `InputAction.CameraUp` + Release (`Vehicle.cs:3562-3565`). **T main** (inside `ApplyInputEvents`).
- `KSA/Part.cs:2629` `public void ActivateSubtreeInStage(Vehicle? vehicle, int sequenceNumber)` → `ISequenced.Activate(vehicle)` for each sequenced module (engines, decouplers, parachutes...) → their `SetIsActive` → **more `IActivateInputBuffer` entries, applied in the same `ApplyIActivateBatched` pass only if added before iteration finishes** (they are added during `ApplyAll`, which iterates a snapshot span of `Count` — so they land next frame; verify empirically).
- Observables: `SequenceList.ActiveSequence { get; private set; }`, `GetNextSequenceNumber()`, `Sequences` (`ReadOnlySpan<Sequence>`), `Sequence.Number/Activated/Parts/Environment`, `SetActiveSequence(int)` (public), `ISequenced.Sequence` per module. Editing sequences: `InputEvents.SequenceChangeData` / `StageChangeData` (ops enumerated in `InputEvents.SequenceOp`/`StageOp`).
- Harmony target: `SequenceList.ActivateNextSequence(Vehicle)` prefix/postfix.

### 2.4 Decoupling / separation
- Request: `KSA/Decoupler.cs:149` `public void SetIsActive(Vehicle? vehicle, bool activationState)` → `IActivateInputData { Operation = Decouple, Vehicle, Decoupler = this }` (only if `IsEnabled && !IsActive && activationState && vehicle != null && Connector.Connection?.OtherPart(...) != null`).
- Apply (T main): `Decoupler.Decouple(Vehicle oldVehicle)` (`:163`, `public Vehicle? Decouple(Vehicle oldVehicle)`) → `vehicle.Split(Connector, Force, out combinedToSplit)` → plays "Decouple" sound, spawns particles, returns the **new** vehicle.
- `KSA/Vehicle.cs:1516` `public Vehicle? Split(Part.Connector splitConnector, double splitImpulse, out PoseChange combinedToSplit, string? splitVehicleId = null)` and `:1527` overload with `Part.Connection`. Used by decouple, undock, debris.
- Harmony: postfix `Decoupler.Decouple` (`__result` = new vehicle) or `Vehicle.Split` (covers all separations). `Decoupler.IsActive/IsEnabled/Sequence/Force/Connector` public.

### 2.5 Docking / undocking
- Physical dock (worker detects): `VehicleUpdateState.DockingEvent` (`VehicleUpdateState.cs:68`) → main `PhysicsBubble.ApplyRenderEventsToVehicles()` → `KSA/DockingEvent.cs` `public void Apply(Vehicle vehicle)` → `DockingPort.Dock(vehicle, OtherVehicle, OtherDockingPort, out consumedToCombined)` and camera re-follow.
- `KSA/DockingPort.cs:422` `public Vehicle? Dock(Vehicle thisVehicle, Vehicle otherVehicle, DockingPort otherVehicleDockingPort, out PoseChange consumedToCombined)` → `otherVehicle.MergeFrom(otherVehicleDockingPort.Connector, thisVehicle, Connector, out ...)` (`Vehicle.cs:1738 public bool MergeFrom(Part.Connector thisConnector, Vehicle otherVehicle, Part.Connector otherConnector, out PoseChange consumedToCombined)`); records `PreDock`.
- `KSA/DockingPort.cs:460` `public Vehicle? Undock(Vehicle oldVehicle, out PoseChange combinedToSplit)` → `oldVehicle.Split(Connector, PushoffImpulse, out ...)`.
- UI path: `InputEvents.VehicleDockingInputData { Vehicle; NearbyVehicle; DockingPort; NearbyDockingPort; bool Undock; }.Apply()` handles camera/control transfer and sets `Program.ControlledVehicle`.
- Observables: `DockingPort.Docked => Connector.Connection != null`, `DockedToPart`, `vehicle.Parts.DockingPorts` (`Module<DockingPort>.List`).
- Harmony: postfix `DockingPort.Dock` / `DockingPort.Undock` (both T main).

### 2.6 SOI / parent body change
- `Vehicle.Parent => Orbit.Parent`, `Orbit => Patch.Orbit`, `Patch => FlightPlan.Patches[0]` (`Vehicle.cs:369-373`).
- Worker computes a new `FlightPlan`; main thread `UpdateFromTaskResultsUnsynchronized` → `private IParentBody? SetFlightPlanUnsynchronized(FlightPlan newFlightPlan)` (`Vehicle.cs:649`): if `Parent != newFlightPlan.Patches[0].Orbit.Parent` sets `_parentChanged = true` and returns the old parent → stored in `_pendingParentChildrenFix` → `private void ApplyPendingParentChildrenFix()` (`:638`, called from `public void UpdateFromTaskResultsSynchronized()` `:2547`, T main) moves the vehicle between `IParentBody.Children` lists. Teleports use `public void SetFlightPlan(FlightPlan newFlightPlan)` (`:632`).
- Pre-warning: `Vehicle.NextPatchEventTime` (`:422`), set in `UpdatePerFrameData` when `FlightPlan.Patches[0].EndTransition` is `PatchTransition.Escape` or `Encounter` (`:2668-2675`).
- Harmony: postfix `Vehicle.UpdateFromTaskResultsSynchronized()` and diff `Parent` against a per-vehicle cache (the `_parentChanged` flag is private and already cleared by then; use `AccessTools.FieldRefAccess<Vehicle,bool>("_parentChanged")` in a postfix on `UpdateFromTaskResultsUnsynchronized` if you want the flag itself — but that method runs on worker threads inside the apply batch). Simplest robust approach: diff `vehicle.Parent` in an `ApplyVehicleSolvers` postfix (as gatOS already does).

### 2.7 Launch / liftoff
- Creation from the launch menu: `KSA/VehicleLaunchMenu.cs:256-266` builds `InputEvents.CreateVehicleData { System, Parent, Id, Body2Cce, BodyRates, Orbit, Root = partTree.Root }` → `Apply()` → `Vehicle.CreateVehicle(System, Body2Cce, BodyRates, System.HomeBody, Id, Root, Orbit)` (`Vehicle.cs:1426 public static Vehicle CreateVehicle(CelestialSystem system, doubleQuat body2Cce, double3 bodyRates, IParentBody parent, string id, Part root, Orbit orbit)`), `Parent.Children.Add`, `vehicle.UpdatePerFrameData()`, `Program.ControlledVehicle = vehicle`, cameras follow. Also `:1421 CreateVehicle(CelestialSystem, VehicleTemplate, IParentBody, string)` and `:1411 CreateVehicleFromSaveGameData(CelestialSystem, VehicleData)`.
- Liftoff proper: `public bool HasLaunched { get; private set; }` (`Vehicle.cs:391`). Set by `public void MarkLaunched()` (`:905`), by `private void StartCrewMissions()` (`:710`) which runs on main in `UpdateFromTaskResultsSynchronized` when `_startCrewMissionsPending` was set because `_props.Situation != _initialSituation` (`:2445-2454`), and on save load. `public UniverseTime LaunchGameTime` (`:164`, set in ctor to `Universe.GetElapsedTime()`). `public void TakeOffRails()` (`:933`).
- Harmony: postfix `Vehicle.MarkLaunched` + prefix on private `StartCrewMissions` (AccessTools) or diff `HasLaunched`/`Situation` after `ApplyVehicleSolvers`. For "vehicle spawned": postfix `CelestialSystem.Register(Astronomical)` (`CelestialSystem.cs:88`) catches every registration (vehicles, debris, EVA kittens).

### 2.8 Landing / touchdown / splashdown
- `KSA/Situation.cs` — `public enum Situation : byte { Maneuvering = 0, Freefall = 1, Rolling = 2, Landed = 3, Sailing = 4, Floating = 5, Dragging = 6, Bottomed = 7 }`. **[DOC DRIFT]** telemetry.md says "exactly 6 values"; there are 8 now (`Dragging`, `Bottomed` added). `SituationEx`: `HasAnyContact()`, `HasTerrainContact()`, `HasOceanContact()`, `IsFreeInOcean()`, `IsAsleepOnTerrain()`, plus `WithOnRails/WithSurfaceContact/...` builders.
- Situation is computed on workers (`PhysicsStates.cs:935 Props.Situation = Situation.Landed`, `ConstraintSim.cs:1234`, `PhysicsBubble.cs:1674`) and copied into `Vehicle._props` in `UpdateFromTaskResultsUnsynchronized`. `public Situation Situation => _props.Situation` (`Vehicle.cs:549`).
- FX events (main thread, only when the impact is energetic enough): `KSA/GroundImpactEvent.cs` `{ float ImpactKineticEnergy; float ImpactVelocity; IParentBody Parent; Vehicle Vehicle; double3 ImpactDirCcf; double3 ImpactPosCcf; bool IsLaunchPad; }` → `Celestial.TrySpawnGroundImpact(GroundImpactEvent)` (`Celestial.cs:262`); `KSA/WaterSplashEvent.cs` `{ float ImpactKineticEnergy; IParentBody Parent; Vehicle Vehicle; double OceanRadius; }` → `Celestial.TrySpawnWaterSplash(WaterSplashEvent)` (`:328`). Gated by `Vehicle.IsImpactFxSuppressed()`.
- No main-thread "landed" callback exists → poll/diff `vehicle.Situation` in an `ApplyVehicleSolvers` postfix (gatOS's snapshot-diff design is the correct one). Prefix `Celestial.TrySpawnGroundImpact/TrySpawnWaterSplash` for impact-with-energy events.

### 2.9 Crash / part destruction / explosion
- Worker sets `VehicleUpdateState.DestructionEvent` / `PartFailureEvent` (`VehicleUpdateState.cs:78/80`; `PartFailure.Detect(VehicleUpdateState)` `PartFailure.cs:45`). Main thread applies them last in `ApplyRenderEventsToVehicles`.
- `KSA/VehicleDestructionEvent.cs` `{ VehicleDestructionCause Cause; float PeakGLoad; float PeakDynamicPressure; }` → `Universe.DestroyVehicleFromEvent(Vehicle vehicle, VehicleDestructionEvent destructionEvent)` (`Universe.cs:1971`, public static): `ExplosionSystem.SpawnForDestruction(in ExplosionContext)` (`ExplosionSystem.cs:150`), `PartFailure.ShedDebris(vehicle, 12)` (`PartFailure.cs:88 public static Vehicle? ShedDebris(Vehicle source, int maxPieces, Part? keepAttached = null, bool hostIsDoomed = true)`), camera hand-off (`FollowWreckage`), then `DestroyVehicle(vehicle, CrewDisposition.Kill)`.
- `KSA/VehicleDestructionCause.cs` `{ GroundImpact, OceanImpact, Collision, ExcessiveGForce, AerodynamicForces, HydrodynamicForces }`.
- `Universe.DestroyVehicle(Vehicle vehicle, CrewDisposition crew = CrewDisposition.EndMission)` (`Universe.cs:2018`, public static): kill/end crew, `HandOffCameras`, clears `Program.ControlledVehicle`, clears others' targets, `vehicle.Dispose()`. Also reached by console `destroy`, recovery (`Vehicle.Recover()` `:2965` → `VehicleDestroyData { Recovered = true }`), `PartFailureEvent.Apply` (whole-vehicle case).
- `KSA/PartFailureEvent.cs` `public void Apply(Vehicle vehicle)` — `List<Part> FailedParts; bool DestroyWholeVehicle;` per-part explosions and fragment vehicles.
- `Vehicle.Dispose()` / `Dispose(bool endMission)` (`Vehicle.cs:3770/3775`): sets `IsDisposed`, `Universe.CurrentSystem?.Deregister(this)`, removes from bubble, `Parts.Dispose()`.
- Harmony: prefix `Universe.DestroyVehicleFromEvent` (has cause + g-load), prefix `Universe.DestroyVehicle` (every removal), postfix `ExplosionSystem.SpawnForDestruction`, prefix `PartFailureEvent.Apply` (per-part failure list), postfix `CelestialSystem.Deregister(Astronomical)`.

### 2.10 Vehicle creation / destroy (summary)
Create: `Vehicle.CreateVehicle` (2 overloads), `CreateVehicleFromSaveGameData`, `Vehicle.Split` (new vehicle), `SpawnSubPartDebris`, `CelestialSystem.Register`. Destroy: `Universe.DestroyVehicle`, `Vehicle.Dispose`, `CelestialSystem.Deregister`, `CelestialSystem.DestroyAllVehicles()` (save load).

### 2.11 Controlled-vehicle switch
- `KSA/Program.cs:502`:
  ```csharp
  public static Vehicle? ControlledVehicle { get { return _controlledVehicle; }
      set { if (_controlledVehicle != value) { _controlledVehicle?.ClearHeldPlayerInput(); } _controlledVehicle = value; } }
  ```
  Harmony target: `AccessTools.PropertySetter(typeof(Program), nameof(Program.ControlledVehicle))` (static `set_ControlledVehicle(Vehicle?)`). Note the setter is also assigned with the *same* value frequently (e.g. `Camera.SetFollow` with `changeControl: true` every follow) — diff in the patch.
- Writers (all main thread): `Camera.SetFollow(IFollowable target, bool tidalLocking, bool changeControl = true, bool alert = true)` (`Camera.cs:602`) / `Unfollow(bool changeControl = true)`; `InputEvents` (`VehicleResourcesChangeData.Control`, dock/undock, destroy fallback, `CreateVehicleData`); `Universe.DestroyVehicle`; `VehicleEditor` ctor sites (`Program.cs:2572, 3506`) and `VehicleEditor.Dispose` (`VehicleEditor.cs:2343` restores `ExistingVehicle`); `EscMenu.cs:81`; `MapController` (map toggles); `EVADoor.cs:187`, `KittenEva.cs:970`; `PartFailureEvent.cs:98`.
- Helpers: `Universe.SeekNextVehicle(SeekDirection)` (`Universe.cs:2549`), `Universe.FollowInHoveredViewport(IFollowable)` (`:2604`), console `control <id>` (`Universe.ControlVehicle`, deferred via `VehicleResourcesChangeBuffer`), `Program.IsControlledVehicleActive` (static bool).

### 2.12 Vehicle editor (VAB) enter / exit, launch menu
- Flags: `public static bool EditorFlag`, `public static VehicleEditor? Editor`, `public static bool IsEditorOpen => EditorFlag || Editor != null`, `public static bool LaunchMenuFlag`, `public static VehicleLaunchMenu? LaunchMenu` (`Program.cs:225-231, 476`).
- Enter: `Program.OnFrameEditor` (`:2567`: `Editor = new VehicleEditor(ControlledVehicle, newVehicle: false); ControlledVehicle = null; Universe.SetSimulationSpeed(0.0);`) or menu "Build New Vehicle" (`:3505`, `newVehicle: true`). Ctor: `public VehicleEditor(Vehicle? inEditingVehicle, bool newVehicle)` (`VehicleEditor.cs:895`).
- Exit: `VehicleEditor.FinalizeExit(bool launchNewVehicle)` (`:2225`, private) sets `Program.EditorFlag = false`; next `PrepareFrame` calls `Editor.Dispose()` (`VehicleEditor.cs:2256`, public) which restores `Program.ControlledVehicle = ExistingVehicle` and camera follow. Also `Universe.DeserializeSave` sets `EditorFlag = false`.
- Launch menu: `Program.LaunchMenuFlag = true` (menu `:3519`, `EscMenu.cs:94`); `OnFrameLaunchMenu` constructs/disposes `VehicleLaunchMenu`; `VehicleLaunchMenu.DrawLaunchUi` enqueues `CreateVehicleData` then `Program.LaunchMenuFlag = false` (`VehicleLaunchMenu.cs:270`).
- Harmony: postfix `VehicleEditor..ctor`, prefix `VehicleEditor.Dispose`, postfix `VehicleLaunchMenu..ctor` / `Dispose`.

### 2.13 Universe / save load and save
- System load: `Universe.LoadSystem(string id)` (`Universe.cs:232`, public static) → `CurrentSystem = new CelestialSystem(systemTemplate)`, stars/roots/`WorldSun`, `KittenRoster`, `AssignStartingCrew()`, `OnLoaded()`.
- `public static void OnLoaded()` (`Universe.cs:2534`): runs `Program.TerminalInterface.Execute("follow <StartVehicle>")`/`"control ..."` (Testing game type) or `"follow <HomeBody>"`, then `Program.TerminalInterface.Execute(ModLibrary.OnLoad.AsSpan())` and `Execute(GameSettings.Current.Console.OnLoad.AsSpan())`.
- Save: `GameSaves.MakeSave(string name)` (`GameSaves.cs:250`), `[TerminalAction("save")] public static void MakeUncompressedSave(string name)` (`:274`) → `UncompressedSave.Make(string)` (`UncompressedSave.cs:141`) → `GameSave.Populate()` (`UniverseData = UniverseData.Create()`) → `UncompressedSave.Write()` (`:108`: `SaveDirectory.TryReplace(...)`, `MetaData.Write()`, `UniverseData.WriteTo(Directory)`, `GameSaves.Register(this)`).
- Load: `[TerminalAction("load")] public static void LoadSaveGame(string name)` (`:283`) → `UncompressedSave.Load()` (`:45`): `UniverseData = UniverseData.LoadFrom(this); Universe.DeserializeSave(UniverseData); Program.OnGameLoaded();`
- `public static void DeserializeSave(UniverseData universeData)` (`Universe.cs:2430`): `SimulationSpeed = 0; JobSystems.WaitAll(); ... Program.EditorFlag = false; CurrentSystem.DestroyAllVehicles(); DestroyAllPhysicsBubbles(); ... cameras Unfollow(); _lastSimStep = new SimStep { PreviousTime = NextTime = universeData.GameTime, DeltaTime = 0 }; ResetRunningId(); ...` then recreates celestial state and vehicles (`CelestialSystem.DeserializeSave`).
- `public static void OnGameLoaded()` (`Program.cs:2596`): closes menus, resets menu pause.
- Harmony: prefix `UncompressedSave.Write()` / postfix (sidecar write, `__instance.Directory`), prefix `Universe.DeserializeSave(UniverseData)` (tear down script state; all vehicles are about to be destroyed) and postfix `Program.OnGameLoaded()` (restore), postfix `Universe.LoadSystem(string)` / `Universe.OnLoaded()` (new game).

### 2.14 Time warp change
- Single funnel: `Universe.SetSimulationSpeed(double simulationSpeed, bool alert = true)` (`Universe.cs:2288`). Called every frame during auto-warp and menu-pause with possibly identical values — diff `_simulationSpeed` (private static; `Universe.GetSimulationSpeed()`) in a prefix to detect real changes. Auto-warp: `AutoWarpTo` / `AutoWarpStop` / `ProcessAutoWarp`.

### 2.15 Flight computer burn start / end
- Worker (`FlightComputer.ComputeControl`, on the copy): burn start = `Burn.StartedAt = nav.Time` when `IgnitionTime` reached and attitude within deadband (`FlightComputer.cs:445-460`); `BurnTarget.LastIgnitionDenied`. Burn end = `AutoBurnCompleted = true` in `private float ComputeBurnControl(out float engineThrottle)` (`:1047`, set at `:1251`) when delta-v to go ≤ 0 or fixed duration elapsed; out of propellant → `BurnMode = Manual; AutoBurnStoppedOutOfPropellant = true` (`:508-512`).
- Main thread: `FlightComputer.CopyFrom(updateData.NewFlightComputer)` copies these flags into the live `Vehicle.FlightComputer`; then `public void RaisePendingAlerts(Vehicle vehicle)` (`FlightComputer.cs:542`), called from `PrepareFrame` **only for `Program.VehiclesInFrame`**, clears `AutoBurnCompleted`/`AutoBurnStoppedOutOfPropellant` and calls private `CompleteAutoBurn(Vehicle)` / `EndAutoBurn()` (`:617/589`) + `TimedAlert`.
- Harmony: **prefix `FlightComputer.RaisePendingAlerts(Vehicle vehicle)`** and read `__instance.AutoBurnCompleted` / `__instance.AutoBurnStoppedOutOfPropellant` (both public fields) before they are cleared; caveat: off-screen vehicles never get `RaisePendingAlerts`, so for those diff the fields in an `ApplyVehicleSolvers` postfix. Burn start: diff `FlightComputer.Burn?.StartedAt` (`BurnTarget` public fields: `ImpulsiveInstant, IgnitionTime, AlignmentStartTime, BurnDuration, BurnBody2Cci, DeltaVTargetCci, DeltaVAccumCci, Throttle, StartedAt, LastIgnitionDenied`, `DeltaVToGoCci`).
- Plan edits: `FlightComputer.AddBurn(Burn)`, `RemoveBurn(Burn)`, `BurnUpdated(Burn)` (`:2209/2194/2220`, public) via `InputEvents.BurnUpdateData { FlightComputer; Burn; bool AddBurn; bool DeleteBurn; }`; `Burn.Create(OrbitPointCce point, double time, double3 deltaVVlf, PatchedConic patch, Vehicle vehicle)` (`Burn.cs:185`). Mode toggles: `FlightComputer.ToggleBurnMode()/ToggleAttitudeMode()/ToggleRCSMode()/ToggleThrustMode()`, `Handle(FlightComputerAction)`, `TrackTarget(FlightComputerAttitudeTrackTarget)`, `RateHold(VehicleReferenceFrame)`, `SetAttitudeProfile(...)`, `SetManualThrustMode(...)`; or `Vehicle.SetEnum(Enum?)` / `ToggleEnum(Enum?)` (`Vehicle.cs:6321/6266`) accepting `FlightComputerManualThrustMode, FlightComputerAttitudeMode, FlightComputerRollMode, FlightComputerRCSMode, FlightComputerBurnMode, VehicleReferenceFrame, FlightComputerAttitudeTrackTarget, FlightComputerAttitudeProfile, FlightComputerAction {None, DeleteNextBurn, WarpToNextBurn}` — deferred via `InputEvents.FlightComputerInputData { Vehicle; bool Toggle; Enum EnumValue; SoundReference? Sound; }`.

### 2.16 Resource depleted
No event. Worker computes `RocketCoreState.IsPropellantAvailable` in `Rocket.UpdateModules` (`Rocket.cs:98-105`) and `EngineControllerState.IsPropellantAvailable`; `FlightComputer.AutoBurnStoppedOutOfPropellant`. Static helpers for the controlled vehicle: `Vehicle.IsOutOfNeededPropellant()` (`:6620`), `AreAllEnginesInactive()` (`:6584`), `IsFuelTransferUnpowered()`. Levels: `PartTree.TotalFillFraction`, `Tank.FilledFraction(ReadOnlySpan<MoleState>)` with `vehicle.Parts.Moles.States`, `Battery.FilledFraction(BatteryState)` with `vehicle.Parts.Batteries.States`. Poll after `ApplyVehicleSolvers`.

### 2.17 Part activation (lights / solar / animations / enable)
- `IEnable.SetIsEnabled(bool)` → deferred via `InputEvents.IEnableInputData { IEnable Target; bool IsEnabled; }` (e.g. `Decoupler.SetIsEnabled` `Decoupler.cs:134`).
- Lights: `LightModule.IsActive` (property, `LightModule.cs:64`), `Vehicle.LightsOn` (public field `:267`), `Vehicle.ToggleLights()` (`:1396`); `InputAction.ToggleLights`.
- Animations: `KeyframeAnimationModule.TimeGoal` (public field), `State { float TimeCurrent; DeploymentState DeploymentState; }` in `ModuleStateful<KeyframeAnimationModule, State, ...>.StateList`, `DeriveDeploymentState(float, float)`, `AnyAnimating(ModuleStateList)`; updated on worker in `UpdateModules`.
- Solar: `SolarPanel` (`ModuleStateful<SolarPanel, SolarPanelState, ...>`), `vehicle.Parts.SolarPanels`, `SolarTrackers`.
- Tanks: `Tank.PropellantUseEnabled` via `InputEvents.TankPropellantUseChangeData`, `TankMixAdoptData`; `FuelLinkChangeData`.

### 2.18 Existing C# events / delegates (no Harmony needed)
Exhaustive `event` grep across KSA/KSA.*/Core/Brutal*/RenderCore:
- `KSA/ViewportRegistry.cs:71/73` — `public static event Action<IViewport>? ViewportRegistered; public static event Action<IViewport>? ViewportUnregistered;`
- `Brutal.ImGuiApi.Abstractions/TerminalInterface.cs` — `public event Action<string, TerminalInterfaceOutputType>? OnOutput;` (console command errors and `help` output; KSA subscribes `Program.TerminalOutput`).
- `KSA.Networking/NetworkPeer.cs:22` — `public event PeerShutdownDelegate OnPeerShuttingDown;`
- `KSA.Networking.Messages/JoinGameRequestMessage.cs:17` — `public static event JoinGameRequestMessageDelegate OnJoinGameRequest;`; `JoinGameResponseMessage.cs:51` — `public static event ExecuteHandler MessageExecutionHandler;`
No `Action<>`/`Func<>` callback fields exist on gameplay types (the `Action<float4>`/`Action<float2>` in `GaugeComponent`/`GaugeCanvas` are private clipboard delegates). **There are no gameplay events; all of §2 requires Harmony or polling.**

---

## 3. In-game console / command system

### 3.1 Objects
- `Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs:14` — `public class ConsoleWindow : ILoggerProvider, IDisposable`. KSA instance: `public static ConsoleWindow ConsoleWindow;` (`Program.cs:285`), created in the ctor as `new ConsoleWindow(ByteSize.FromKilobytes(64))` (`:693`). `public TerminalInterface Terminal;` field (`ConsoleWindow.cs:242`).
- `public static TerminalInterface TerminalInterface => ConsoleWindow.Terminal;` (`Program.cs:500`).
- `Brutal.ImGuiApi.Abstractions/TerminalInterface.cs:12` — `public class TerminalInterface`:
  - `private readonly Dictionary<object, Dictionary<string, Command>> CommandLibrary` (per-owner) and `private readonly Dictionary<string, Command> StaticCommandLibrary` (owner == null).
  - `public void RegisterCommand(Delegate inCallback, object? inObject = null)` — reads `inCallback.Method.GetCustomAttribute<TerminalActionAttribute>()`; **throws `TerminalInterfaceException` if the method lacks the attribute** (so lambdas cannot be registered); name is `CommandName.ToLower()`; registering the same name under the same owner **replaces** silently.
  - `public void RegisterObject(object inObject)` — reflects `BindingFlags.Instance|Static|Public|NonPublic|FlattenHierarchy` methods with `[TerminalAction]` and registers each (instance or static) under `inObject`.
  - `public void UnregisterObject(object inObject)` — removes the whole owner dictionary. **This is the only removal API; commands registered with `inObject == null` can never be removed.**
  - `public bool Execute(string inInput)` — `Parser.SplitToCommands` (split on `;` outside double quotes, trimmed) → `ProcessCommand` each; catches `TerminalInterfaceException` → `OutputProcessCommandError` → `OnOutput(msg, Error)` and returns `false` at the first failure. `public void Execute(params ReadOnlySpan<string> inInput)` runs each.
  - Lookup order: static library first, then per-owner dictionaries in `Dictionary` enumeration order; unknown → `TerminalInterface.Log.Error("unknown command ...")` (no exception).
  - Built-ins: `help` (lists all; prints via `_consoleWindow.Print` + `TableString.ToConsole`), `clear`.
- `TerminalActionAttribute(string commandName, string description = "", ArgParseMode parsingMode = ArgParseMode.Default)` (`[AttributeUsage(AttributeTargets.Method)]`), `enum ArgParseMode { Default, Unparsed }`.
- `Command` struct: `Issue(string[]? args)` → `ParseArgs` → `_callback.Method.Invoke(_callback.Target, parsed)`. Args parsed with `Convert.ChangeType(arg, type)`; supported parameter types: `IsPrimitive`, `string`, `DateTime`; optional parameters (`HasDefaultValue`) become `Type.Missing`; too many args → exception. `Unparsed` mode requires exactly one `string` parameter and passes the raw remainder.
- `Parser.SplitCommand`: first whitespace-delimited token (lower-cased) is the name; `SplitToArgs` uses `CommandLineParser.SplitCommandLine` (quotes supported) in Default mode.

### 3.2 Registering commands from a mod (no Harmony)
```csharp
static class MyCmds {
  [TerminalAction("script", "run a script file", ArgParseMode.Unparsed)]
  static void Script(string path) { ... }
}
// register (any time after Program ctor; e.g. [StarMapAllModsLoaded])
Program.TerminalInterface.RegisterCommand(new Action<string>(MyCmds.Script), ownerToken);
// unload
Program.TerminalInterface.UnregisterObject(ownerToken);
```
Reference `Brutal.ImGui.Abstractions.dll` for `TerminalActionAttribute`/`ArgParseMode`. KSA's own registrations use exactly this (`Universe.RegisterCommands(Program program)` `Universe.cs:305-339`, `JobSchedulerCommands.RegisterCommands(object? data)`, `GameSaves.RegisterCommands`, `TerminalCommands.RegisterCommands`, private `Program.RegisterCommands()` `:1531`).

### 3.3 Invoking existing commands programmatically
`Program.TerminalInterface.Execute("control MyShip; refill MyShip")`. Must be called on the main thread (delegates are invoked synchronously, and most KSA commands enqueue `InputEvents` or touch UI). Known command names (from `[TerminalAction]` attributes read in source): `help`, `clear`, `save <name>`, `load <name>`, `savefiles`, `threads [list|reset]`, `openlogs`, `opencrashdumps`, `nethost`, `netded`, `burndebug`, `goto <loc|lat lon>`, `uimask`, `atmosphere` (Program) / `atmosphere <id> [setting] [value]` (Universe), `clouds`, `ocean`, `exhaust`, `exit`, plus `follow <id>` and `control <id>` (used by `Universe.OnLoaded`). Universe also registers methods `GetCelestial, ListCelestials, ListCelestialTree, MoveTo, FollowBody, UnfollowBody, ControlVehicle, GoTo, SetCameraSpeed, SetSimulationSpeedDirect, SetDisplayNames, SetShowOrbit, SetAltitude, LookAt, Duplicate, Destroy, Explode, Refill, Empty, PrintElapsedTime, OpenCelestialWindow, Atmosphere, OpenPatchWindow, SetTargetCommand, UnsetTargetCommand, BurnDebug, ShowAxis, RenderClouds, WarpToNext, ToggleVelocity, SetCamera, PrintSkyCoordinates, ToggleGalacticGrid` (`Universe.cs:305-339`; read each method's attribute for the exact name). Note `Destroy/Explode/Refill/Empty/ControlVehicle/Duplicate` all defer via `InputEvents`.

### 3.4 Startup / autoexec command lists (free hook for a scripting mod)
- `KSA/ConsoleSettings.cs` — Tomlet model `[TomlField("onBoot")] List<string> OnBoot; [TomlField("onLoad")] List<string> OnLoad;`. Each KSA content mod's `mod.toml` carries `Mod.Console` (`Mod.cs:76`), aggregated in `ModLibrary.PrepareAll` into `ModLibrary.OnBoot/OnLoad` (`ModLibrary.cs:62-64, 498-504`).
- Executed: `GameSettings.Current.Console.OnBoot` at `Program.cs:777` (early in ctor); `ModLibrary.OnBoot` at `Program.cs:972` (after render targets, before `JobSystems.Initialize`); `ModLibrary.OnLoad` and `GameSettings.Current.Console.OnLoad` in `Universe.OnLoaded()` (after system load, also after each save load? — no: `OnLoaded` is called only from `LoadSystem`; `DeserializeSave` does not call it). The settings UI exposes OnBoot/OnLoad editing (`GameSettings.cs:2203`).
- Caveat: StarMap mods load later than this (StarMap attributes fire after KSA init), so an `onBoot` entry naming a StarMap-registered command will log "unknown command" unless the command is registered before `Program.cs:972` — `onLoad` (at `Universe.OnLoaded`) runs after `[StarMapAllModsLoaded]`? That ordering depends on when StarMap invokes its hooks relative to `Universe.LoadSystem` (which happens inside the `Program` ctor, `Program.cs:~905-930`) — verify empirically; likely both run before StarMap's AllModsLoaded, so prefer re-running the command list yourself.

### 3.5 Output pipeline (what purrTTY patches)
- Entry points (all public, **thread-safe** via `Lock _pendingLock`): `Print(string text, int timeToShow = 5)`, `Print(ImString, int)`, `Print(ReadOnlySpan<char>, int)`, `Print(ReadOnlySpan<byte>, int)`, `Print(ImString, ImColor8 color, int)`, `Print(ReadOnlySpan<byte>, ImColor8, int)`, `Print(ReadOnlySpan<char>, ImColor8, int)` (`ConsoleWindow.cs:570-603`), `PrintMessage(string)` (light blue), `ClearConsole()`.
- All funnel into private `AddPendingMessage(ReadOnlySpan<byte|char> text, ImColor8 color, int timeToShow)` (`:605/624`) → double-buffered 1 MiB `_pendingText[2]` + `List<PendingMessage>[2]`; `timeToShow` forced to 0 when fading disabled (KSA calls `DisableMessageFading()` at startup and `EnableMessageFading()` at `Program.cs:1328`).
- `public void SwapBuffers()` (`:650`) is called at the top of `Draw(...)` each frame; `private void Apply(in PendingMessage, ReadOnlySpan<byte>)` (`:677`) appends to `Storage` (`RingBuffer`, 64 KB) and pushes a `ConsoleLogEntry { TextSpanHandle, Color, TimeSpanHandle, ActiveTime }` into `_consoleBuffer` (`GrowingRingBuffer<ConsoleLogEntry>`, initial 128). Old entries drop when `Storage.TokenIsStale`.
- Logging: `ConsoleWindow` is registered as an `ILoggerProvider` (`Program.cs:720 builder.AddProvider(ConsoleWindow)`), so every `LogCategory` (`DefaultCategory.Log.Info/Debug/Warning/Error/...`, `Brutal.Logging/LogCategory.cs`) reaches `public void OnLog(string categoryName, LogLevel logLevel, string message)` (`:388`) → `Print(message, levelColor)`. `Console.WriteLine` goes to stdout only (Microsoft console logger with `KSALogFormatter`), **not** to the in-game console.
- Terminal errors: `TerminalInterface.OnOutput += Program.TerminalOutput` (`Program.cs:305-314, 859`) → `ConsoleWindow.Print($"{type}: {s}")`.
- Drawing: `public void Draw(float2 viewportPosition, float2 viewportSize, float unscaledDeltaTime, bool noFade = false)` (`:448`) — an ImGui window `"ConsoleWindow"` docked at the bottom third; `DrawLog` draws every `ConsoleLogEntry` via `public void Draw(ref ConsoleLogEntry line, ref float2 inputSize, bool isShown, float unscaledDeltaTime, bool noFade = false)` (`:764`); input text `"###ConsoleInput"` (`ImInputString _consoleInput`, 512). Hidden state still draws fading lines. Called from `Program.OnDrawUiConsole(double dt)` after `OnDrawUiThreadSafe`.
- Input: `KSA/ConsoleWindowEx.cs` `public static bool OnKey(this ConsoleWindow console, GlfwWindow window, GlfwKey key, int scanCode, GlfwKeyAction action, GlfwModifier mods)` — `ToggleKey = GlfwKey.Backslash` (public static, settable), `SubmitKey = GlfwKey.Enter`, Up/Down history → `public bool HandleInputAction(Actions action)` (`enum Actions { Up, Down, Submit, Toggle }`). `public void Submit(string input)` (`:535`) runs `Terminal.Execute(...)`. `IsOpen => _show`, `Show()`, `Hide()`. In the key chain (`Program.OnKey` `:1745-1754`): `GameSettings.OnKeyAll → Popup.OnKeyAll → ConsoleWindow.OnKey → !ConsoleWindow.IsOpen → !ImGui WantCaptureKeyboard → DispatchKeyEvent` (editor, camera, controller, vehicle...).
- Good purrTTY-style targets: postfix on the three-arg `Print(ReadOnlySpan<char>, ImColor8, int)` and `Print(ReadOnlySpan<byte>, ImColor8, int)` (every message passes one of them, including `OnLog`), or prefix `Apply` for the already-UTF-8 view; `Draw(float2, float2, float, bool)` prefix returning false to replace rendering.

---

## 4. Existing automation / scripting in KSA

- **No scripting language.** Case-insensitive grep for `Lua|Roslyn|CSharpScript|Scripting|IronPython|Jint|MoonSharp|NLua` over `KSA`, `KSA.*`, `Core` → no hits. `Expression` hits are `System.Linq.Expressions` (used only by `TerminalInterface.GetDelegateTypeForMethod`) and facial `CatExpressionAnim`/`CharacterExpressions`. `Macro` → none.
- **"Sequence" = staging**: `KSA/Sequence.cs` (`int Number; bool Activated; PerformanceEnvironment Environment; ReadOnlySpan<Part> Parts`), `KSA/SequenceList.cs` (per-`PartTree` list, UI drawing, drag/drop reorder ops), `ISequenced` (`int Sequence {get;set;}` + `MoveToSequence`), `SequencePerformance*` (per-stage Δv/TWR computed by `SequencePerformanceJob` on `NearestOrbitAndPerformanceWorker`).
- **Command-ish structs** are physics data, not a command system: `RocketCoreCommand { float Throttle; double BurnTime; UniverseTime LastCommandTime; }`, `ThrusterPulseCommand { int ThrusterIdx; float PulseTime; }`, `LocomotionCommand` (kitten locomotion).
- **FlightComputer autopilot** (`KSA/FlightComputer.cs`): modes (`BurnMode {Manual, Auto}`, `AttitudeMode`, `AttitudeFrame: VehicleReferenceFrame`, `AttitudeTrackTarget`, `CustomAttitudeTarget: double3`, `ManualThrustMode`, `RollMode`, `RCSMode`, profile deadband/rate limit), `BurnPlan` (`FirstBurn/LastBurn/BurnCount/HasActiveBurns/FindFirstExecutableBurn()/TryGetBurn(...)`), `BurnTarget` (the burn being executed), auto-warp to ignition (`FlightComputerAction.WarpToNextBurn`, `IGNITION_TIME_AUTO_WARP_MARGIN = 3.0`). It is a closed-loop controller, not a program runner.
- **Deferred command buffers**: `KSA.InputEvents` (§2) — the game's own queue of typed `IApplicable` structs. No generic "run this delegate" buffer exists; `TypedBuffer<T>` is constrained to KSA struct types.
- **Console command lists** at boot/load (§3.4) are the only data-driven "automation".
- `TransferPlanner` (porkchop/transfer window planning UI), `OrbitalTransfers` (interstellar arrival solver) — computational helpers.
- Nothing named `Automation`, `Autopilot`, `Program*` (other than `Program`), `Script*`, `Macro*` exists as a type.

---

## 5. State read/write surface (exact members)

### 5.1 Globals
| Member | Decl | Notes |
|---|---|---|
| `Universe.CurrentSystem` | `public static CelestialSystem? CurrentSystem { get; private set; }` (`Universe.cs:114`) | null before load |
| `Universe.Stars` / `Roots` / `WorldSun` / `KittenRoster` | `ReadOnlySpan<StellarBody>`, `ReadOnlySpan<IIndependentRoot>`, `StellarBody?`, `KittenRosterData` | |
| `Universe.GetElapsedTime()` / `GetElapsedSeconds()` / `GetLastSimStep()` / `GetNextSimStep()` | static | see §1.5 |
| `Universe.SimulationSpeed` / `GetSimulationSpeed()` / `SetSimulationSpeed(double,bool)` / `IsPaused()` / `IsAutoWarpActive` / `AutoWarpTime` | static | |
| `Universe.SetTarget(Vehicle vehicle, IOrbiter orbiter, Part? targetPart = null)` / `UnsetTarget(Vehicle)` | `Universe.cs:517/588` | prefer `InputEvents.ChangeTargetData` |
| `Program.ControlledVehicle` | `public static Vehicle?` get/set (`Program.cs:502`) | |
| `Program.VehiclesInFrame` | `public static ReadOnlySpan<Vehicle>` (`:474`) | vehicles near camera this frame |
| `Program.MainViewport` / `HoveredViewport` / `InputViewport` / `RenderedViewport` / `FrameViewport` | `IGameViewport` / `IViewport` (`:484-492`) | `IGameViewport.BaseCamera`, `.MapCamera`, `.GetCamera()`, `.Mode`, `.GetActiveController()` |
| `Program.GetCamera()` / `GetMainCamera()` / `GetHoveredCamera()` / `GetRenderCamera()` | `public static Camera` (`:631-646`) | |
| `Program.GetController()` / `GetOrbitController()` / `GetFlyController()` / `GetMapController()` | static (`:656-671`) | |
| `Program.IsLoaded` (`{get;set;}`), `DrawUI`, `EditorFlag`, `Editor`, `IsEditorOpen`, `LaunchMenuFlag`, `LaunchMenu`, `ActiveBurn: Burn?`, `IsControlledVehicleActive`, `FrameNumber: ulong`, `ShiftDown/AltDown`, `ConsoleWindow`, `TerminalInterface`, `Instance`, `GetPlayerTime()/GetPlayerDeltaTime()`, `CurrentAltitudeKm` | static | |
| `Camera` | `Following: IFollowable?`, `SetFollow(IFollowable, bool tidalLocking, bool changeControl = true, bool alert = true)`, `Unfollow(bool changeControl = true)`, `GetPositionEgo(IPosition)`, `NearbyCelestial`, `CurrentAltitudeKm`, `TidalLocking` | `Camera.cs` | `Camera : Transform3D` (ECL frame) |
| `GameSettings.Current` | `public static GameSettings Current { get; private set; }` (`GameSettings.cs:1380`) | `.Simulation.MinTargetFrameRate`, `.Console.OnBoot/OnLoad`, `.System.StartGameType/StartVehicle`; `GameSettings.Show`, `IsUserTyping` (static bool), `OnKeyAll(GlfwKeyEvent)`, `SaveChanges()`, dozens of `Show*()`/`Get*()` getters |
| `TimedAlert.Create(string displayName, byte4 color, double displayPlayerTime = 5.0)` / `CreateWarning(string, double = 5.0)` | `TimedAlert.cs:69/75` | on-screen toast |
| `ModLibrary.Get<T>(string id) where T : IKeyed` / `TryGet<T>(string, out T?)` / `Has<T>(string)` | `ModLibrary.cs:1059/776/575` | templates, sounds, etc. (`TryGet` does not support `PartTemplate` per parts.md) |
| `SystemLibrary.Default`, `Find(string)` | static | |

### 5.2 Collections
- `CelestialSystem` (`KSA/CelestialSystem.cs`): `LookupCollection<Astronomical> All`, `IParentBody? HomeBody`, `int Count`, `string Id`, `Astronomical? Get(string id)`, `T? Get<T>(string id) where T : Astronomical`, `bool Exists<T>(string id, out T body)`, `int CountOf<T>()`, `Register(Astronomical)`, `Deregister(Astronomical)`, `Rename(Vehicle, string)`, `DestroyAllVehicles()`, `ForEach(Action<Astronomical>)`, `GetNearbyAstronomical(...)`, `SerializeSave()/DeserializeSave(CelestialSystemData)`.
  **[DOC DRIFT]** There is no `CelestialSystem.Vehicles` member; `SKILL.md`'s `Universe.CurrentSystem?.Vehicles.GetList()` does not exist. Use `CurrentSystem.All.OfType<Vehicle>()` (struct enumerator) or `All.UnsafeAsList().OfType<Vehicle>()` as telemetry.md does.
- `LookupCollection<T>` (`KSA/LookupCollection.cs`): `Count`, `Get(string)`, `Get(KeyHash)`, `AsSpan()`, `UnsafeAsList()`, `OfType<T2>()`, `OfTypeReverse<T2>()`, `TryGet(string, out T?)`, `TryGet<T2>(string, out T2?)`, `CountOf<T1>()`.

### 5.3 Vehicle (`KSA/Vehicle.cs`) — selected public members
Identity/state: `Id`, `Hash`, `Class` ("Vehicle"/"Debris"), `IsDisposed`, `IsDebris`, `HasLaunched`, `LaunchGameTime`, `IsEditedVehicle`, `IsControllable` (virtual), `Situation`, `HasMeaningfulOrbit`, `CanRecover`, `Crew: ReadOnlySpan<IVASeat>`, `SeatCount`.
Orbit/frames: `FlightPlan`, `Patch`, `Orbit`, `Parent: IParentBody`, `Periapsis`, `Apoapsis`, `TimeSincePeriapsis`, `NextPeriapsisTime/NextApoapsisTime/NextPatchEventTime`, `Body2Cce`, `Body2Ego`, `Asmb2Cce`, `Asmb2Ego`, `BodyRates: double3`, `Ctrl2Body`, `CtrlRates`, `GetBody2Cci()`, `GetCtrl2Cce()/GetCtrl2Cci()/GetAsmb2Cci()`, `GetLvlh2Cce()`, `GetEnu2Cce()`, `GetPositionCci/Cce/Ecl/Orb()` (+ `UniverseTime` overloads), `GetVelocityCci/Cce/Ecl/Orb()`, `GetStateVectorsAt(UniverseTime)`, `OrbitalSpeed`, `GetInertialSpeed()`, `GetSurfaceSpeed()`, `GetSurfaceVelocityCci()`, `GetBarometricAltitude()`, `GetRadarAltitude()`, `VehicleRegion`.
Physics: `Props` (`ref readonly VehicleProperties`), `KinematicStates`, `PhysicsEnvironment` (`.AtmosphericPressure/.AtmosphericDensity`), `KinematicMeasurements`, `StructuralLoad`, `AccelerationBody`, `AngularAccelerationBody`, `TotalMass`, `InertMass`, `PropellantMass`, `TotalMassPropsBody`, `CenterOfMassAsmb`, `BoundingSphereRadiusBody`, `BubbleOrigin`, `HasPhysicsBubble`, `BubbleLeader`, `NearbyVehicles`, `NavBallData` (`.ThrustWeightRatio`), `GetPhysicsStates()`, `GetPhysicsStatesMutable()`.
Control: `FlightComputer`, `BurnPlan`, `Target`/`TargetPart` (`SetTarget(IOrbiter?, Part?)`), `ControlPart`/`ControlConnector` (`SetControlPart(Part?, Part.Connector?)`), `GetManualThrottle()`, `GetMinThrottle()`, `ProcessInput(...)`, `ToggleStabilization()/SetStabilization(bool)/IsStabilizationOn()`, `ToggleManualThrustMode()`, `SetEnum/ToggleEnum(Enum?)`, `IsSet<T>(T, bool)`, `SetNavBallFrame(VehicleReferenceFrame)`, `ClearHeldPlayerInput()`, `LightsOn`, `ToggleLights()`.
Mutation: `Teleport(Orbit? orbit, doubleQuat? body2Cce, double3? bodyRates)` (`:2241`), `TeleportToLocation(Celestial, double lat, double lon)` (`:4219`), `RefillConsumables()` (`:3201`), `DepleteConsumables()`, `Split(...)`, `MergeFrom(...)`, `SpawnSubPartDebris(...)`, `Recover()`, `KillCrew()`, `AddCrew/RemoveCrew/...`, `SetName(string)`, `MarkLaunched()`, `MarkAsDebris()`, `TakeOffRails()`, `UpdateVehicleConfiguration()`, `UpdateAfterPartTreeModification()`, `SetFlightPlan(FlightPlan)`, `Dispose()`.
Static: `CreateVehicle(...)`, `CreateVehicleFromSaveGameData(...)`, `IsOutOfNeededPropellant()`, `AreAllEnginesInactive()`, `IsThrottleGLoadLimited()`, `GetControlledStructuralLoad()`, `GetInitialKinematicStateForLocation(...)`.
Private but useful via reflection/`AccessTools`: `_manualControlInputs` (ManualControlInputs), `_engineFlags`, `_props`, `_kinematicStates`, `_environment`, `_threadWorkerUpdateState` (VehicleUpdateState), `_parentChanged`, `_initialSituation`, `_lastTeleportTime`.

### 5.4 Orbit / bodies
- `Orbit` (`KSA/Orbit.cs:1179-1219`): `Parent { get; private set; }`, `ParentMass`, `Mu`, `Eccentricity`, `SemiMajorAxis`, `SemiMinorAxis`, `Inclination`, `LongitudeOfAscendingNode`, `ArgumentOfPeriapsis`, `Periapsis`, `Apoapsis` (radii), `PeriapsisAltitude`, `ApoapsisAltitude` (minus `Parent.MeanRadius`), `Period`, `TimeAtPeriapsis`, `Orb2ParentCci`, `StateVectors`, `GetStateVectorsAt(UniverseTime)`, `UpdatePosition(StateVectors)`, `CachedPoints`; subclasses `Orbit.Elliptical/Parabolic/Hyperbolic`; static `CreateFromStateCci` (per SKILL.md).
- `IParentBody` (`KSA/IParentBody.cs`): `double Mass`, `uint Rank`, `double Mu => Mass * 6.6743E-11`, `MaxTerrainRadius/MinTerrainRadius`, `SphereOfInfluence`, `List<IOrbiter> Children`, `AstronomicalTemplate BodyTemplate`, `GetCci2Cce()/GetCce2Cci()/GetCcf2Cce()/...`, `GetNearSurfaceRadius()`, `AtmosphereReference? GetAtmosphereReference()`, `OceanReference? GetOceanReference()`; plus `IFollowable` → `Id`, `MeanRadius`, `GetPositionEcl()`, ...
- `Celestial` (`KSA/Celestial.cs`): `Children`, `Parent`, `Mass {get; protected set}`, `SphereOfInfluence {get; protected set}`, `GetCci2Cce()`, `TrySpawnGroundImpact/WaterSplash`, `ComputeSphereOfInfluenceRadius()`.
- `Astronomical` (`KSA/Astronomical.cs`): `Id`, `Hash`, `Class`, `System`, `MeanRadius`, `OrbitView`, `ShowCelestialWindow`, `IsMoon()/IsStar()/HasOrbit()`, `GetBodyFixed2Ecl()`, `GetBodyRates()`, `GetPositionEcl()`, `DistanceTo(Astronomical)`.

### 5.5 Parts / modules / resources
- `PartTree` (`KSA/PartTree.cs`): `ReadOnlySpan<Part> Parts`, `Part Root`, `int Count`, `Vehicle? OwningVehicle`, `ModuleList Modules`, `ModuleStateList States`, typed lists — `Module<Tank>.List Tanks`, `Module<SolidGrainSegment>.List Segments`, `ModuleStateful<Mole,...>.StateList Moles`, `Gimbals`, `RocketCores`, `RocketNozzles`, `Batteries`, `Generators`, `SolarPanels`, `PowerConsumers`, `SolarTrackers`, `Module<DockingPort>.List DockingPorts`, `Decouplers`, `EVADoors`, `GrabRails`, `Controls` — `SequenceList SequenceList`, `ResourceGroupList`, `SequencePerformanceList PerformanceSequences`, `FuelLinkList FuelLinks`, `TotalFillFraction`, `ElectricalCircuits`, `FlowTopology`, `SubstanceStores`, `EngineThrottleMin`, `RecomputeAllDerivedData()`, `MarkDerivedDirty(DerivedData)`, `EnsureDerived(DerivedData)`, `RefillConsumables()`, `ReconfigureTanks()`, `Merge(Part parentPart, Part childPart, bool recomputeDerivedData = true)`, `Split(Part, bool)`, `Reroot(Part)`, `Find(ulong id)`, `ResetStaging()`, `Serialize()`, `DeepCopy()`, `BeginBulkChange()/EndBulkChange()`, `Contains(Part)`. Internal: `RebuildResourceGraphs()`, `MarkResourceManagersDirty()`, `RecreateResourceManagers()`, static `FlushDirtyResourceManagers()`.
- `ModuleList` (`KSA/ModuleList.cs:180`): `public Span<TModule> Get<TModule>() where TModule : Module<TModule>, IDisposable`, `GetUsing<TInterface>()` (enumerate modules implementing an interface, e.g. `IActivate`, `ISequenced`, `IEnable`), `HasAny<TModule>()`, `Add(module)`.
- `ModuleStateful<TModule,TState,TGlobalState,TFxState>.StateList` (`KSA/ModuleStateful.cs:14-575`): `Span<TModule> Modules`, `ReadOnlySpan<TState> States`, `ref readonly TGlobalState GlobalState`, `Span<TFxState> FxStates`, `int NumModules`, `ModulesAndStates` / `ModulesAndAllStates` enumerators, `ref readonly TState GetState(TModule)`, `GetStateByIdx(int)`, `ref TFxState GetFxState(TModule)`, **`ModuleAndAllMutableStatesRef GetModuleAndAllMutableStatesForInitialization(TModule module)`** (`:479`, the only writable state accessor; `.Module`, `ref TState State`, `ref TFxState FxState`), `GetMutableGlobalStateForInitialization()`, `TryGetById(ulong instanceId, out TModule)`, `TryGetModuleAndStateById(...)`. The worker-side `StateUpdater` (`:578`) exposes `GetNewState(TModule)`, `GetModulesAndNewStates()`, `GetReadOnlyStates()`.
- `ModuleBase` (`KSA/ModuleBase.cs`): `ulong InstanceId`, `string TemplateId`, `Part Parent`, `DrawsUi`, nested `abstract class SaveDataBase { [XmlAttribute] string InstanceOf; [XmlAttribute] uint LocalInstanceId; abstract void OnDataLoad(Mod) }`; virtuals `GetSaveData(ref uint localRunningId, ModuleStateList states)` / `ApplySaveData(SaveDataBase, ModuleStateList)` / `OnFullPartCreated()`.
- `Part` (`KSA/Part.cs`): `string Id {get; init;}`, `DisplayName`, `uint InstanceId`, `PartTemplate Template`, `Part? PartParent`, `PartTree? Tree`, `List<Part> TreeChildren`, `List<Connection> Connections`, `ModuleList Modules`, `ModuleList SubtreeModules`, `ReadOnlySpan<Part> SubParts`, `IsSubPart`, `FullPart`, `int Stage {get; private set}` + `SetStage(int)`, `SequenceOrder`, `double3 Scale`, `PositionVehicleAsmb`, `Asmb2VehicleAsmb`, `MatrixAsmb2VehicleAsmb`, `CrashTolerancePascals`, `GetSubtreeSequencedModules()`, `ActivateSubtreeInStage(Vehicle?, int)`, `GetConnectorInstance(string)`, nested `Connector`, `Connection` (`static Connect(IConnector, IConnector)` per parts.md), `ConnectorReference`.
- Engines: `EngineController` (`required RocketCore[] Cores; required float MinimumThrottle; RocketControllerData VacuumData/SeaLevelData; bool IsActive {get; internal set;}; int Sequence {get;set;}`), `RocketCore` (abstract; `MinimumPulseTime`, `MinimumThrottle`, `Reaction`, `Rocket Rocket`, `IActivate Controller`, `FeedConnectors`, `DesignConditions`, `MaxChamberPressure`), `ThrusterController` (`Cores`, `ManualControlMap`, `ThrustLocationVehicleAsmb`, `IsActive {get; internal set;}`).
- Tanks/batteries: `Tank` (`StorageVolume`, `List<Mole> Moles`, `RoleAffinity`, `PropellantUseEnabled`, `TransferMode`, `FilledFraction(ReadOnlySpan<MoleState>)`, `ComputeSubstanceMass(...)`, `Refill(Moles StateList)`, `Deplete(...)`, `TryGetMole(...)`), `Battery` (`required Joules MaximumCapacity`, `Refill(ref BatteryState)`, `ConsumeCharge/ProduceCharge(ref BatteryState, Joules)`, `FilledFraction(BatteryState)`), `BatteryState { Joules Charge }`, `ResourceManager` (`Consume/Produce`, static `RefillAllTanks(PartTree)/DepleteAllTanks(PartTree)`, `ResourceAvailable(ReadOnlySpan<MoleState>)`).
- Docking/decouplers: see §2.4-2.5. Lights/animations/solar: see §2.17.

### 5.6 FlightComputer public fields (main-thread copy)
`BurnMode, AttitudeMode, AttitudeFrame, AttitudeTrackTarget, CustomAttitudeTarget, ManualThrustMode, AngleDeadband, RateLimit, RollMode, RCSMode, AttitudeTarget, RateDeadbandStates[3], ErrorAngleIntegrals, SteeringInterval, SteeringAuthority, BurnTarget? Burn, LastControlTime, LastThrusterPulseTime, LastThrustTime, TotalMassPropsBody, TotalMassPropsInvBody, CenterOfMassAsmb, TotalAcceleration, AngularAccelerationBody, AmbientPressure, MaxGLoad, Ctrl2Asmb, ControlDeltaTime, ActiveControlSystem, ActiveEnginePerformanceMax, PlannedBurnThrottle, IsThrottleGLoadLimited, AutoBurnStoppedOutOfPropellant, AutoBurnCompleted, TvcTorqueAuthority(Max), RcsTorqueAuthority, AngleTurnaround, RateBit, RateDeadband, ErrorAngles, ErrorRates, ConservativeFlipTime, DetumbleRateLimit, MaxRcsAttitudeControlAccel`; properties `BurnPlan {get; private set}`, `VehicleConfig {get; private set}` (`VehicleConfigInfo { List<ThrusterController> Thrusters; List<GimbalController> Gimbals; HasRcs; HasTvc }` — note vehicle-api.md's `VehicleConfig.TotalEngineVacuumThrust` was not seen in the 5554 `VehicleConfigInfo` class header; verify before relying on it), `Ctrl2AsmbF`. Methods: `ReadUpdatedVehicleConfiguration(Vehicle)`, `SetNullRot(VehicleReferenceFrame)`, `SetInertialNullRot()`, `DeleteInvalidBurns()`, `Handle(FlightComputerAction)`, toggles (see §2.15), `SerializeSave()/DeserializeSave(FlightComputerData, Vehicle)`.
Since `FlightComputer.CopyFrom(NewFlightComputer)` overwrites the live instance every applied step, **main-thread writes to FlightComputer fields are clobbered unless made in the `ExecuteNextVehicleSolvers` prefix window** (then `VehicleUpdateData.Prepare` copies them into the worker copy via `NewFlightComputer.CopyFrom(flightComputer)`, `VehicleUpdateData.cs:100`).

### 5.7 Access-level gotchas for a bridge
- `internal`/private setters: `EngineController.IsActive`, `ThrusterController.IsActive` (use `SetIsActive`), `Vehicle.HasLaunched/IsDebris/Target/ControlPart/FlightPlan` (use public methods), `Universe.SimulationSpeed` setter (use `SetSimulationSpeed`), `SequenceList.ActiveSequence` (use `SetActiveSequence`), `Program.ControlledVehicle._controlledVehicle`, `Orbit.Parent`, `Part.Stage` (use `SetStage`), `VehicleUpdateState.Bubble`.
- `internal` statics: `Program.CliArgs`, `PartTree.DebugSinglePartTreeCount`, `PartTree.RecreateResourceManagers/MarkResourceManagersDirty/EnsureResourceManagersBuilt/FlushDirtyResourceManagers`, `VehicleUpdateTask.EvaluateMergePairs/EvaluateSplitClusters`, `PhysicsBubble.MarkFailed`.
- `ref struct`/`ref readonly` returns (`Props`, `NavBallData`, `GetModuleAndAllMutableStatesForInitialization`) cannot be boxed or captured by a dynamic bridge — copy fields out on the main thread.

---

## 6. Save / serialization

- **Saves: `System.Xml.Serialization`.** `GameSaves.UniverseSerializer = new XmlSerializer(typeof(UniverseData), XmlHelper.AttributeOverrides)` (`GameSaves.cs:126, 157`); `VehicleSaves.VehicleSerializer` for `VehicleSaveData`; `XmlHelper.Serializers: Dictionary<Type, XmlSerializer>`, `XmlHelper.AttributeOverrides: XmlAttributeOverrides`, `XmlHelper.SerializeWithoutNaN<T>(XmlSerializer, T, string filePath)` and a `MemoryStream` overload; `XmlLoader.Load<T>(string filePath, Mod mod) where T : ILibraryData` for content XML.
- Layout: `GameSaves.SaveFolderPath = Path.Combine(Constants.DocumentsFolderPath, "saves")`; each `UncompressedSave` is a directory (`public readonly DirectoryInfo Directory; public readonly SaveMetaData MetaData;`) holding `universe.xml` (`UniverseData.WriteTo(DirectoryInfo)`) and metadata (`SaveMetaData.Write()`); vehicle saves hold `vehicle.xml` (`VehicleSaveData.WriteTo`, const `VEHICLE_XML`). `GameSave` base: `Id`, `Hash`, `Size`, `Version`, `LastUpdate`, abstract `Load()/Delete()/Overwrite()/Write()`.
- `UniverseData` (`KSA/UniverseData.cs`): `[XmlElement("GameTime")] UniverseTimeReference GameTime; [XmlElement("CameraData")] CameraData Camera; [XmlElement("System")] List<CelestialSystemData> CelestialSystems; [XmlElement("KittenRoster")] KittenRosterData KittenRoster;` + `static Create()`, `WriteTo(DirectoryInfo|MemoryStream)`, `LoadFrom(UncompressedSave|UnmanagedMemoryStream|BinaryReader)`.
- `VehicleData : VehicleSaveData : AstronomicalData` (`KSA/VehicleData.cs`): `ParentBody, AnalyticState, KinematicState, KinematicMeasurements, FlightComputerData, ResourceManagerData, OrbitCameraViewData, VehicleRegion, TargetId, TargetPartId, ControlPartId, ControlConnectorId, EngineOn, EngineThrottle, NavBallFrame, LightsOn, HasLaunched, IsDebris, OnLadder..., DistantGlint...`; `VehicleSaveData`: `[XmlElement("RootPartRef")] PartInstance? RootPartInstance; Character; ActiveSequence; SequenceEnvironments; FuelLinks; LaunchGameTime`.
- `PartInstance` (`KSA/PartInstance.cs`): tree of parts with `Components: List<ModuleBase.SaveDataBase>` — polymorphic module save data via `[XmlType(TypeName = "EngineController")]`-style nested `SaveData` classes (`EngineController.SaveData { ActiveInStage; Sequence; FlowRule? }`, `Decoupler.SaveData`, `DockingPort.SaveData`, `Tank.SaveData`, `Battery.SaveData`, `KeyframeAnimationModule.SaveData`, `ThrusterController.SaveData`) produced by `ModuleBase.GetSaveData(ref uint, ModuleStateList)` and consumed by `ApplySaveData`.
- `FlightComputerData` (`KSA/FlightComputerData.cs`) and `BurnPlanData`/`BurnTargetData` persist autopilot state.
- **Everything else is TOML via Tomlet** (`Tomlet.Attributes`): `GameSettings` (`LoadFromFile`/`SaveChanges`), `mod.toml` (`Mod`, `ModLibrary.Manifest`, `ConsoleSettings`), input bindings (`Input.TomlNames`). HUD layouts: XML (`GaugeXmlWriter`, `SerializedCanvas/Layout`).
- Not used anywhere in KSA: MemoryPack, MessagePack, System.Text.Json, Newtonsoft, protobuf (grep negative). Network messages are hand-rolled binary.
- Implication: `XmlSerializer` is type-closed; mods cannot add elements to `UniverseData` without replacing `GameSaves.UniverseSerializer` (static get-only, built in the static ctor — would need reflection + `XmlAttributeOverrides` before first use; fragile). **Recommended: sidecar file `<save dir>/<mod>.{toml|json}`**, written in a postfix of `UncompressedSave.Write()` (`__instance.Directory`), read in a postfix of `UncompressedSave.Load()` / `Universe.DeserializeSave`. Vehicle identity across saves: `Vehicle.Id` (string) / `Hash`; parts: `PartInstance.LocalInstanceId`/`GlobalInstanceId`, `Part.InstanceId` (uint, runtime; **not** stable across loads — `Universe.ResetRunningId()` on load).

---

## 7. Unload / reload safety

- **No unload support in the game.** No `AssemblyLoadContext`, no plugin lifecycle; reflection use is limited to `GaugeEditor` (`typeof(GaugeBinding).Assembly.GetTypes()`), `GaugeInspector` (`Activator.CreateInstance`), `VersionInfo` (`Assembly.Load("KSA")`). Hot reload in KSA exists only for shaders (`KSA.AssetReloader/ShaderReloader.cs`, `HotReloadingEnabled`, `OnFrame()`).
- **Objects the game retains that will pin a mod's ALC** (remove all in `[StarMapUnload]`):
  1. `TerminalInterface.CommandLibrary[owner]` entries → `UnregisterObject(owner)`. Never register with `inObject == null` (unremovable).
  2. `TerminalInterface.OnOutput` subscriptions.
  3. `ViewportRegistry.ViewportRegistered/Unregistered` subscriptions.
  4. Harmony patches (incl. StarMap's three on `Program`) — a patched method whose patch body lives in an unloaded assembly crashes on next call; `Program.OnFrame`, `Universe.ExecuteNextVehicleSolvers`, `ApplyVehicleSolvers`, `InputEvents.ApplyInputEvents`, `Universe.SetSimulationSpeed`, `ConsoleWindow.Print`, `Camera`/controller `OnFrame`, `Vehicle.GetWorldMatrix/UpdateRenderData` are per-frame hot paths. Unpatch with the same `Harmony` id before unload.
  5. `ILoggerProvider`/`LogCategory` instances (`LogCategory.Make(name)` registers globally in `LogSystem`) — if a mod creates its own `LogCategory`, it is retained forever.
  6. `ImGuiWindow.DrawAllStaticWindows` registrations, `GaugeCanvas` injections, `ViewportRegistry.TryClaimSecondaryViewport(IViewportOwner, ...)` (release via `ReleaseSecondaryViewport`).
  7. `InputEvents` buffers only hold KSA types (no delegates) — nothing to clean up, but queued structs referencing a `Vehicle` are applied next frame regardless.
- **Static state that bites hot reload / long sessions**: `Program.Instance` (singleton), `JobSystems.*` (threads created once; `DynamicWorkerPool` threads `IsBackground = true`), `PartTree._structureLock` (static `ReaderWriterLockSlim` with recursion — a mod taking `ReadStructure()` and not disposing the scope deadlocks writers), `VehicleUpdateTask` `[ThreadStatic]` scratch lists, `LightModule.PointLightsRayTrace/SpotLightsRayTrace` static lists, `ModLibrary` static libraries (content; cannot be reloaded), `ExplosionSystem` statics, `Universe` private statics (`_vehicleUpdateTask`, `_orphanVehicles`, `_autoWarp`), `ConsoleWindowEx.ToggleKey` (static; if a mod changes it, restore on unload).
- **Exceptions**: any throw inside a Harmony prefix on the sim path propagates out of `OnFrame` → process crash (Brutal.Monitor writes a heap dump: `Program.cs:701 CollectorOptions { LastChance = DumpType.Heap }`). Worker exceptions are captured per bubble (`PhysicsBubble.MarkFailed`) and **rethrown on the main thread** by `RethrowApplyFailure()` ("physics bubble result apply failed"). A script runtime must catch everything and must not block the main thread (the frame loop has no watchdog, but all solvers are waited on each frame).
- **ImGui**: single context, main thread only, valid only between `PrepareImGui` and `ImGui.Render()` (i.e. within the two StarMap GUI hooks). `ImGui.GetIO().WantCaptureKeyboard` and `GameSettings.IsUserTyping` gate game hotkeys.
- **Thread affinity not enforced**: because `PartTree.StampMainThread()` is never invoked, off-thread structure edits are not caught (§1.6). Treat *all* of `Vehicle`, `PartTree`, modules and `FlightComputer` as main-thread-only, and only in the idle-solver window for writes.
- **Native libs**: KSA P/Invokes Vulkan/GLFW/FMOD/ImGui through `Brutal.*` wrappers; a scripting runtime that loads its own native library (e.g. a Lua/JS engine) must not unload it while any callback/finalizer can still fire; `NativeLibrary.Free` after the ALC is collected only.

---

## 8. Threading primitives for marshalling onto the game thread

- **None provided by KSA or Brutal for "run this on main".** Grep for `SynchronizationContext|Dispatcher|MainThreadQueue|RunOnMainThread|InvokeOnMain` finds only `KSA.Concurrency.BepuWorkerDispatcher` (Bepu `IThreadDispatcher` for physics fan-out). `Brutal.Concurrency.Jobs.JobScheduler/JobRunner` (ThreadPool-backed `IJob.Run()`), `KSA.Concurrency.DynamicWorkerPool.ParallelBatch()`, `MpmcRing`, `PinnedBuffers` are worker-side only.
- The game's own deferred mechanism is `InputEvents` (main-thread-only `Add`). A mod must build its own queue (gatOS `GameThread.Scheduler` with `ConcurrentQueue` + `TaskCompletionSource` is the right shape) and drain it at one of:
  1. **Prefix `Universe.ExecuteNextVehicleSolvers(double, SimStep)`** — solvers idle, inputs applied, derived data flushed: drain here for anything that writes sim state (recommended default for a scripting runtime's command dispatch).
  2. **Postfix `Universe.ApplyVehicleSolvers()`** — fresh sim results, before `InputEvents.ApplyInputEvents()`: drain here for scripts that read state and enqueue `InputEvents` (applied the same frame).
  3. StarMap `BeforeGui`/`AfterGui`/`OnFrame` postfix — read-only sim access, UI work, and `InputEvents` enqueue (applied next frame).
- Thread-safe sinks usable from any thread: `ConsoleWindow.Print*` (`Lock`-guarded pending buffers), `LogCategory.*` (Microsoft.Extensions.Logging pipeline → console provider → `Print`), Brutal.Monitor file logger. Not verified thread-safe (treat as main-only): `TimedAlert.Create`, `Popup`, anything ImGui.
- Frame identity for coalescing: `Program.FrameNumber` (ulong, incremented in `PostRender`), `Universe.GetLastSimStep().NextTime` (advances only when a vehicle step lands; equal across frames when paused).

---

## Appendix A — Harmony target cheat-sheet

| Purpose | Target (declaring type, static/instance, signature) | File:line | Thread |
|---|---|---|---|
| Sim step landed (observe) | `Universe.ApplyVehicleSolvers()` static, postfix | Universe.cs:1922 | main |
| Pre-solver mutation window | `Universe.ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)` static, prefix | Universe.cs:2126 | main |
| Same-frame command injection | `InputEvents.ApplyInputEvents()` static, prefix | InputEvents.cs | main |
| Engine on/off intent | `EngineController.SetIsActive(Vehicle? vehicle, bool activationState)` instance | EngineController.cs:79 | main |
| Thruster on/off intent | `ThrusterController.SetIsActive(Vehicle?, bool)` instance | ThrusterController.cs:61 | main |
| Staging | `SequenceList.ActivateNextSequence(Vehicle vehicle)` instance | SequenceList.cs:171 | main |
| Decouple | `Decoupler.Decouple(Vehicle oldVehicle)` instance → `Vehicle?` | Decoupler.cs:163 | main |
| Any separation | `Vehicle.Split(Part.Connector, double, out PoseChange, string?)` instance | Vehicle.cs:1516 | main |
| Dock / undock | `DockingPort.Dock(Vehicle, Vehicle, DockingPort, out PoseChange)` / `Undock(Vehicle, out PoseChange)` instance | DockingPort.cs:422/460 | main |
| SOI change | `Vehicle.UpdateFromTaskResultsSynchronized()` instance, postfix (diff `Parent`) | Vehicle.cs:2547 | main |
| Vehicle spawn / remove | `CelestialSystem.Register(Astronomical)` / `Deregister(Astronomical)` instance | CelestialSystem.cs:88/93 | main |
| Vehicle created | `Vehicle.CreateVehicle(...)` static (2 overloads) | Vehicle.cs:1421/1426 | main |
| Destruction w/ cause | `Universe.DestroyVehicleFromEvent(Vehicle, VehicleDestructionEvent)` static, prefix | Universe.cs:1971 | main |
| Any destroy | `Universe.DestroyVehicle(Vehicle, CrewDisposition)` static, prefix | Universe.cs:2018 | main |
| Part failure | `PartFailureEvent.Apply(Vehicle)` instance, prefix | PartFailureEvent.cs | main |
| Ground/water impact FX | `Celestial.TrySpawnGroundImpact(GroundImpactEvent)` / `TrySpawnWaterSplash(WaterSplashEvent)` | Celestial.cs:262/328 | main |
| Controlled vehicle | `Program.set_ControlledVehicle(Vehicle?)` static setter | Program.cs:502 | main |
| Editor enter/exit | `VehicleEditor..ctor(Vehicle?, bool)` / `VehicleEditor.Dispose()` | VehicleEditor.cs:895/2256 | main |
| Save written | `UncompressedSave.Write()` instance, postfix (`__instance.Directory`) | UncompressedSave.cs:108 | main |
| Save loading | `Universe.DeserializeSave(UniverseData)` static, prefix/postfix; `Program.OnGameLoaded()` static | Universe.cs:2430; Program.cs:2596 | main |
| New system loaded | `Universe.LoadSystem(string)` / `Universe.OnLoaded()` static | Universe.cs:232/2534 | main |
| Warp change | `Universe.SetSimulationSpeed(double, bool)` static, prefix (diff) | Universe.cs:2288 | main |
| Auto-burn complete / fuel-out | `FlightComputer.RaisePendingAlerts(Vehicle)` instance, prefix | FlightComputer.cs:542 | main |
| Burn plan edits | `FlightComputer.AddBurn/RemoveBurn/BurnUpdated(Burn)` instance | FlightComputer.cs:2209/2194/2220 | main |
| Throttle override | `Vehicle.PrepareWorker(SimStep)` instance virtual, prefix | Vehicle.cs:2396 | main |
| Key input | `Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)` instance virtual | Vehicle.cs:3536 | main |
| Console output | `ConsoleWindow.Print(ReadOnlySpan<char>, ImColor8, int)` / `Print(ReadOnlySpan<byte>, ImColor8, int)` instance | ConsoleWindow.cs:600/595 | any |
| Console draw | `ConsoleWindow.Draw(float2, float2, float, bool)` instance | ConsoleWindow.cs:448 | main |
| Console submit | `ConsoleWindow.Submit(string)` / `TerminalInterface.Execute(string)` instance | ConsoleWindow.cs:535; TerminalInterface.cs | main |

## Appendix B — Doc-drift list vs `gatOS/.claude/skills/ksa/*.md`
1. `Universe.GetElapsedSimTime()` → does not exist; use `Universe.GetElapsedTime()` (`UniverseTime`) / `GetElapsedSeconds()`.
2. `Universe.CurrentSystem?.Vehicles.GetList()` (SKILL.md) → no `Vehicles` member; use `All.OfType<Vehicle>()` / `All.UnsafeAsList()`.
3. `Situation` has 8 values (`Dragging`, `Bottomed` added), not 6.
4. `FlightComputer.VehicleConfig.TotalEngineVacuumThrust` — not visible in the `VehicleConfigInfo` header read here (`FlightComputer.cs:21-30` shows `Thrusters`, `Gimbals`, `HasRcs`, `HasTvc`); verify before use.
5. lifecycle.md's statement that `ExecuteNextVehicleSolvers` is "the static per-sim-step entry" is confirmed; add that it is the **only** sim-safe write window and that StarMap GUI hooks run concurrently with the solver.
6. telemetry.md's advice to refill batteries in the `ExecuteNextVehicleSolvers` prefix is correct for the reason above (worker reads `Parts.Batteries` state); the same applies to *every* module-state write, tank refills included (`Vehicle.RefillConsumables()` in `Update` races with the worker; it only "works" because the console path defers it through `InputEvents.VehicleResourcesChangeData`).
