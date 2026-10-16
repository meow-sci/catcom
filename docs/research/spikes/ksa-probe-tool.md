---
type: Tool
title: KsaProbe - metadata probe for the shipped KSA.dll
description: A MetadataLoadContext console tool in this repo that inspects the exact members of the reference KSA.dll (types, signatures, visibility, events) without running the game, used to verify hook targets and catch decompile/doc drift.
resource: ../../../spikes/KsaProbe/Program.cs
tags: [spike, tool, reflection, metadata, verification]
sources:
  - id: src
    resource: ../../../spikes/KsaProbe/Program.cs
    title: KsaProbe source
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: stable
---

# Usage

```bash
cd spikes/KsaProbe
dotnet run -- /Users/asherwin/repos/meow-sci/ksa-game-assemblies/current/dll "Program::OnFrame" "EngineController::SetIsActive" "grep:Vehicle" "Universe" events
```

Query forms: `Type` (dump all declared members), `Type::Member`, `grep:<substring>` (type names), `events` (every event and delegate-typed field in KSA.dll). Output shows visibility, static/instance, and full parameter lists.[^src]

# Findings it produced

- `KSA.dll 2026.10.10.5554` has 2629 types.
- `Program.OnFrame(double currentPlayerTime, double dtPlayer)` is **internal/protected**; patch it by name.
- `Universe.ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)` is public static.
- `EngineController.SetIsActive(Vehicle vehicle, bool activationState)` is public; `EngineController` derives from `ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>`.
- `Universe.GetElapsedSimTime()` **does not exist**; the members are `GetElapsedTime()`, `GetElapsedSeconds()`, `GetLastSimStep()`, `GetNextSimStep()`.
- `CelestialSystem` has **no `Vehicles` member**; vehicles are enumerated through `All` (`LookupCollection<Astronomical>`) with `OfType<Vehicle>()` / `UnsafeAsList()`, and `Vehicle : Astronomical`.
- `Program.ControlledVehicle` is a static property with public getter and setter.

Use it before every game update as the cheapest part of the upgrade playbook, alongside the compile check.

[^src]: KsaProbe source
