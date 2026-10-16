---
type: Platform Analysis
title: KSA frame loop, simulation threading, and the safe write windows
description: The exact per-frame order of KSA 2026.10.10.5554 (Program.OnFrame / PrepareFrame), the fact that the vehicle simulation runs on worker threads concurrently with every StarMap hook, the InputEvents deferred command queue, time/warp APIs, and the only sim-safe mutation windows a scripting host may use.
resource: /Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/decomp/KSA/Program.cs
tags: [ksa, game-loop, threading, solver, input-events, harmony, time-warp]
sources:
  - id: report
    resource: /references/ksa-hooks-report-2026-10-16.md
    title: KSA hook-point research report (2026-10-16, build 5554)
  - id: program
    resource: /Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/decomp/KSA/Program.cs
    title: decomp KSA/Program.cs
  - id: universe
    resource: /Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/decomp/KSA/Universe.cs
    title: decomp KSA/Universe.cs
  - id: inputevents
    resource: /Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/decomp/KSA/InputEvents.cs
    title: decomp KSA/InputEvents.cs
  - id: probe
    resource: /spikes/ksa-probe-tool.md
    title: KsaProbe metadata tool
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: stable
---

# The one fact that shapes everything

**The vehicle simulation runs off the main thread, concurrently with everything a StarMap hook sees.** `Program.PrepareFrame` waits for last frame's solvers, applies their results into `Vehicle` objects, drains the deferred `InputEvents` buffers, then **kicks the next sim step** (`Universe.ExecuteNextVehicleSolvers`) and returns. ImGui, the StarMap `BeforeGui` / `AfterGui` / `OnFrame`-postfix hooks and rendering all run while `JobSystems.VehicleSolver` and the `VehicleWorker` threads are reading `Vehicle`, `PartTree`, modules and a per-step copy of `FlightComputer`.[^report]

- **Reading** vehicle state from StarMap hooks is safe: workers write only into `VehicleUpdateData.New*` scratch, and `Vehicle` fields are written only on the main thread in `Universe.ApplyVehicleSolvers`.
- **Writing** vehicle/part/module state from StarMap hooks races with worker reads. The only sim-safe mutation window is inside `PrepareFrame` between `JobSystems.VehicleSolver.Wait()` and `Universe.ExecuteNextVehicleSolvers(...)`.
- `PartTree.StampMainThread()` is never called in this build, so KSA's own off-thread-write assertion is disabled: races are silent.[^report]

# Per-frame order (`Program.OnFrame(double currentPlayerTime, double dtPlayer)`)

1. `PrepareFrame`:
   1. Wait: `JobSystems.NearestOrbitAndPerformanceWorker.Wait(); OrbitSolvers.Wait(); VehicleSolver.Wait(); ClothSolvers.Wait();`
   2. Apply on main: `Universe.ApplyOrbitSolvers(); Universe.ApplyVehicleSolvers(); Universe.ApplyClothSolvers();` (`ApplyVehicleSolvers` copies kinematics/props/flight plan into each `Vehicle`, `FlightComputer.CopyFrom(NewFlightComputer)`, then applies docking / impact / part-failure / destruction render events).
   3. `Glfw.PollEvents()` → key callbacks enqueue `InputEvents`.
   4. **`InputEvents.ApplyInputEvents()`** drains every deferred buffer (engine/thruster/decouple activation, dock/undock, burns, stage/sequence edits, sim speed, auto-warp, target, teleport, destroy/explode, refill, vehicle creation).
   5. Editor teardown; `RefreshVehiclesInFrame()`; `FlightComputer.RaisePendingAlerts` for vehicles in frame.
   6. Pause logic; `Universe.ProcessAutoWarp(dtPlayer)`; `SimStep jobSimStep = Universe.GetJobSimStep(dtPlayer)`.
   7. `PartTree.FlushDirtyDerived(); PartTree.FlushDirtyResourceManagers();`
   8. **`Universe.ExecuteNextVehicleSolvers(dtPlayer, jobSimStep)`** (async kick) and `ExecuteNextOrbitSolvers`.
2. Gauges, `PrepareImGui`, `OnFrameEditor`, `OnFrameViewports`.
3. `if (DrawUI) { OnDrawUiFrame(dt); OnDrawUiViewports(dt); OnDrawUiThreadSafe(dt); }` — StarMap's `BeforeGui` is a prefix on `OnDrawUiFrame`, `AfterGui` a postfix on `OnDrawUiViewports`; **both are skipped when the UI is hidden (F2)**.
4. Console draw, `ImGui.Render()`, camera controllers, lights, celestials, `OnPreRender` → `Render` → `PostRender` (`FrameNumber++`, audio update).
5. StarMap's `Program.OnFrame` postfix runs **after GPU submit, with this frame's solver still running**; ImGui is no longer valid there.[^program][^report]

# Safe points for a scripting host

| Point | Thread | Solver | Safe for |
|---|---|---|---|
| Harmony **postfix `Universe.ApplyVehicleSolvers()`** (static) | main | idle (just applied) | read fresh state; enqueue `InputEvents` (applied this same frame) |
| Harmony prefix `InputEvents.ApplyInputEvents()` (static) | main | idle | enqueue into buffers for same-frame apply |
| Harmony **prefix `Universe.ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)`** (static) | main | idle, inputs applied, derived data flushed | **direct mutation of Vehicle/Part/Module/FlightComputer state** (the gatOS Solver lane) |
| prefix `Universe.PrepareVehicleWorkers(SimStep)` / `Vehicle.PrepareWorker(SimStep)` | main | idle | override manual control inputs right before copy to worker |
| StarMap `BeforeGui` / `AfterGui` | main | running | read-only sim access, ImGui, `InputEvents` enqueue (next frame) |
| StarMap `OnFrame` postfix | main | running | read-only; no ImGui |
| any other thread | — | — | only `ConsoleWindow.Print*` and logging |

`FlightComputer` writes made outside the `ExecuteNextVehicleSolvers` prefix are **clobbered** by `CopyFrom` on the next apply, which is why gatOS routes attitude/burn/RCS-mode setpoints through the Solver lane.[^report]

# The game's own command queue: `InputEvents`

`KSA.InputEvents` holds ~24 static `TypedBuffer<T>` fields of `IApplicable` structs (`IActivateInputData` for engine/thruster/decouple, `VehicleDockingInputData`, `BurnUpdateData`, `SequenceChangeData`, `StageChangeData`, `SimSpeedData`, `AutoWarpData`, `ChangeTargetData`, `VehicleInputData`, `FlightComputerInputData`, `VehicleResourcesChangeData`, `CreateVehicleData`, `VehicleDestroyData`, `VehicleExplodeData`, …) drained once per frame. `TypedBuffer<T>.Add` is a plain array append with **no lock**: enqueue only on the main thread. A scripting runtime should route mutations through these buffers (via the public methods that enqueue them, e.g. `EngineController.SetIsActive`, `Vehicle.SetEnum(...)`) rather than poking fields, exactly as the UI does.[^inputevents]

# Time, warp, timestep

- `Universe.GetElapsedTime()` → `UniverseTime` (Int128 nanoseconds; `.Seconds()`), `GetElapsedSeconds()`, `GetLastSimStep()`, `GetNextSimStep()`; `SimStep { PreviousTime, NextTime, DeltaTime }`.
- **The outer sim step is variable**: `GetJobSimStep(dtPlayer)` = `dtPlayer × SimulationSpeed × achievedSpeedFraction`; sub-stepping happens inside `PhysicsBubble`.
- Warp: `Universe.SimulationSpeed` (private set), `SetSimulationSpeed(double, bool alert = true)` is the single funnel (called every frame by auto-warp and menu pause, often with the same value), `IsPaused()`, `AutoWarpTo(UniverseTime, margin, uncapped)`, `AutoWarpStop(bool)`, `IsAutoWarpActive`, `AutoWarpTime`; deferred equivalents `InputEvents.SimSpeedBuffer` / `AutoWarpBuffer`.
- Wall time: `Program.GetPlayerTime()` / `GetPlayerDeltaTime()`; frame identity `Program.FrameNumber`.[^universe]

**Doc drift vs the ksa skill:** `Universe.GetElapsedSimTime()` does not exist in 5554 (use `GetElapsedTime()` / `GetElapsedSeconds()`); `CelestialSystem.Vehicles` does not exist (use `CurrentSystem.All.OfType<Vehicle>()`); `Situation` has 8 values (`Maneuvering, Freefall, Rolling, Landed, Sailing, Floating, Dragging, Bottomed`). Both missing members were confirmed by the compile check and the metadata probe.[^probe]

# Threads and job system

`JobSystems.VehicleSolver` is a 1-runner `JobScheduler` whose `IJob.Run()` executes on the .NET ThreadPool and fans out to dedicated `VehicleWorker{i}` threads (`DynamicWorkerPool`, `ThreadPriority.Highest`). Worker exceptions are captured per `PhysicsBubble` and **rethrown on the main thread** in `ApplyResultsToMainThread` (process crash). Any throw inside a Harmony prefix on the sim path propagates out of `OnFrame` and crashes the game with a heap dump, so a script host must catch everything on these paths.[^report]

# No dispatcher exists

KSA and Brutal provide no `SynchronizationContext`, dispatcher or "run on main thread" primitive (only the physics `BepuWorkerDispatcher`). A mod must own its queue (gatOS `CommandQueue` shape: `ConcurrentQueue` + `TaskCompletionSource` with `RunContinuationsAsynchronously`) and drain it in the `ExecuteNextVehicleSolvers` prefix (writes) or the `ApplyVehicleSolvers` postfix (reads + `InputEvents` enqueue).[^report]

[^report]: KSA hook-point research report (2026-10-16, build 5554)
[^program]: decomp KSA/Program.cs
[^universe]: decomp KSA/Universe.cs
[^inputevents]: decomp KSA/InputEvents.cs
[^probe]: KsaProbe metadata tool
