---
type: Spike Result
title: HookCompileCheck - StarMap mod skeleton and Harmony patches compile against KSA 5554
description: A mod-shaped class library that compiles a StarMap-attributed mod class, a game-thread queue, and Harmony patches on EngineController.SetIsActive, Universe.ExecuteNextVehicleSolvers and Program.OnFrame against the 5554 reference assemblies, proving the hook targets and the read surface used in the plans exist.
resource: ../../../spikes/HookCompileCheck/Mod.cs
tags: [spike, harmony, starmap, compile-check]
sources:
  - id: src
    resource: ../../../spikes/HookCompileCheck/Mod.cs
    title: HookCompileCheck source
  - id: csproj
    resource: ../../../spikes/HookCompileCheck/HookCompileCheck.csproj
    title: HookCompileCheck.csproj
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: stable
---

# What compiles

- `[StarMapMod]` class with `[StarMapImmediateLoad] void M(KSA.Mod)`, `[StarMapAllModsLoaded]`, `[StarMapBeforeGui] void M(double)`, `[StarMapAfterGui]`, `[StarMapAfterOnFrame] void M(double, double)`, `[StarMapUnload]` — the full attribute set from `StarMap.API 0.3.6`.
- A `Queue<Action>` game-thread queue drained in `[StarMapBeforeGui]` (the simplest marshalling shape; the real design drains in the `ApplyVehicleSolvers` postfix).
- Harmony patches: postfix `EngineController.SetIsActive(EngineController __instance, Vehicle? vehicle, bool activationState)`, `Priority.First` prefix `Universe.ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)`, postfix `Program.OnFrame` (internal, patched by name).
- Read surface: `Universe.CurrentSystem.All.OfType<Vehicle>()`, `Program.ControlledVehicle`, `Universe.GetElapsedSeconds()`, `vehicle.Parts.Modules.Get<EngineController>()`, `EngineController.SetIsActive(v, on)`, `vehicle.Parts.RecomputeAllDerivedData()`.[^src]

Build: `cd spikes/HookCompileCheck && dotnet build` with `TreatWarningsAsErrors`, references `Lib.Harmony 2.4.2` and `StarMap.API 0.3.6` (`PrivateAssets=all`) and the game DLLs with `Private=false`, resolved through `spikes/Directory.Build.props` (`KSA_DLL_DIR` or the sibling `ksa-game-assemblies` checkout).[^csproj]

# Why it matters

The first build failed on two members documented in the `ksa` skill (`CelestialSystem.Vehicles`, `Universe.GetElapsedSimTime`) that no longer exist in 5554; the probe tool found the replacements. This is the "build as alarm" step of the upgrade playbook, and the project should grow one patch stub per hook in [event hook points](/ksa-host/event-hook-points.md) before implementation starts.

[^src]: HookCompileCheck source
[^csproj]: HookCompileCheck.csproj
