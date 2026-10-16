---
type: Component Analysis
title: gatOS snapshot and reader model (the read surface)
description: How gatOS samples KSA into immutable SimSnapshot records (cadence, readers, DTO shapes, units, frames, consistency), which is the read-API shape an in-process scripting layer should expose or reuse.
resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.GameMod/Game/TelemetrySampler.cs
tags: [gatos, telemetry, snapshot, readers, units, frames]
sources:
  - id: report
    resource: /references/gatos-internals-report-2026-10-16.md
    title: gatOS internals research report (2026-10-16)
  - id: sampler
    resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.GameMod/Game/TelemetrySampler.cs
    title: TelemetrySampler.cs
  - id: vesselreader
    resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs
    title: VesselReader.cs
  - id: snapshot
    resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.SimFs/Snapshots/SimSnapshot.cs
    title: SimSnapshot.cs
  - id: spec
    resource: /Users/asherwin/repos/meow-sci/gatOS/SPEC_9P_FILESYSTEM.md
    title: SPEC_9P_FILESYSTEM.md
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:00:00Z }
status: stable
---

# Cadence and gating

`TelemetrySampler.Tick(double dt, bool active)` is **frame-driven and rate-limited**, not a timer thread: a `SampleClock` accumulates frame dt and fires at `SampleRateHz` (default 10, clamped 1..120), dropping backlog rather than burst-replaying. The sampler idles when no client can read. Bodies have a sub-cadence; between ticks the same body list is republished by reference so consumers can reference-compare for "unchanged".[^sampler]

# What one sample does

1. `ut = Universe.GetElapsedSeconds()`, `warp = Universe.SimulationSpeed`, `activeId = Program.ControlledVehicle?.Id`.
2. `foreach (var a in Universe.CurrentSystem.All.UnsafeAsList()) if (a is Vehicle v)` → `VesselReader.Sample(v, activeId, ut, detail)` inside **its own try/catch** (a vehicle mid-teardown must not kill the loop), optionally `PartsReader.Sample`.
3. Despawn pruning for features rides the enumeration.
4. Bodies via `BodyReader` (paced).
5. `EventDiffer.Diff(previous, …)` plus feature-queued events.
6. `new SimSnapshot(++seq, ut, warp, activeId, vessels, events, gameVersion, rateHz, health)` with `SimDtSeconds = Universe.GetLastSimStep().DeltaTime`, warp speeds, auto-warp state, bodies, system, feature states.
7. Publish by one volatile swap in `SnapshotStore`; `WaitForNextAsync(seq)` wakes waiters.[^sampler]

# Readers

- **VesselReader** — single-pass `BuildCore` (always) + guarded `BuildFull` (detail; on any throw falls back to core, logged once). Core: `Parent`, `GetPositionCci()`, lat/lon via `parent.GetCci2Ccf()` + `GetLlaFromCcf`, `GetBody2Cci()`, `BodyRates`, `OrbitalSpeed`, `GetSurfaceSpeed()`, `GetInertialSpeed()`, `GetBarometricAltitude()`, `GetRadarAltitude()`, `TotalMass`, `InertMass`, `PropellantMass`, `Situation`, battery charge, `IsControllable`, `is KittenEva`, `IsSet(VehicleEngine.MainIgnite)`, `LightsOn`. Detail: `GetPositionEcl()`, `GetVelocityCci()`, `CenterOfMassAsmb`, `NavBallData{AttitudeAngles,ThrustWeightRatio,DeltaV,Frame,Speed}`, `PhysicsEnvironment{AtmosphericPressure,AtmosphericDensity,OceanDensity,TerrainRadius}`, dynamic pressure, accelerations, power, `GetManualThrottle()`, thruster flags, `FlightComputer.{AttitudeMode,AttitudeTrackTarget,AttitudeFrame,RCSMode}`, and per-module lists.[^vesselreader]
- Module mutable state is a struct-of-arrays reached through `ModuleStateful<TModule,TState,TGlobal,TFx>.TryGetFrom(vehicle.Parts.States, out states)` then `ref readonly var st = ref states.GetState(module)` (used for `EngineController`, `ThrusterController`, `SolarPanel`, `SolarTracker`, `Generator`, `KeyframeAnimationModule`).[^vesselreader]
- **PartsReader** — `ConditionalWeakTable<Vehicle, CacheEntry>` rebuilt when `Parts.Count` changes or every 10 s; reads `Part.{InstanceId,Id,DisplayName,Template.Id,PartParent,SubParts,PositionVehicleAsmb}`.
- **BodyReader** — statics cached per body (`Id, Class, ParentId, ChildIds, Mass, MeanRadius, Mu, SphereOfInfluence, GetAngularVelocity(), Orbit, Atmosphere, Ocean`), per tick only `GetPositionEcl()/GetVelocityEcl()`.
- **CameraReader** — published separately via `CameraStore`, not inside `SimSnapshot`.[^report]

# DTO shapes (abridged)

All are `sealed record`s with finite doubles (`Sanitize.Finite` turns NaN/±Inf into 0):[^snapshot]

```csharp
SimSnapshot(long Sequence, double UtSeconds, double WarpFactor, string? ActiveVesselId,
    IReadOnlyList<VesselSnapshot> Vessels, IReadOnlyList<SimEvent> NewEvents, string GameVersion, double SampleRateHz,
    IReadOnlyList<AccessorHealthSnapshot> Accessors) { SimDtSeconds; WarpSpeeds; AutoWarpActive; AutoWarpTargetUt; Bodies; System; … }
VesselSnapshot(Id, Name, Situation, PositionCci, LatitudeDeg, LongitudeDeg, OrbitalSpeed, SurfaceSpeed, InertialSpeed,
    AttitudeBody2Cci, BodyRatesRadS, BarometricAltitude, RadarAltitude, MassTotal, MassDry, MassPropellant, Orbit?,
    Engines, Tanks, BatteryChargeFraction?, ParentBodyName?, LightsMasterOn, Animations)
    { PositionEcl; VelocityCci; CenterOfMass; Controlled; Controllable; IsKitten; Navball; ThrottleCmd; AttitudeMode; AttitudeFrame; RcsMode;
      TranslateCmd; RotateCmd; EngineOn; Environment; PowerProducedW; PowerConsumedW; Rcs; Solar; Generators; Lights; Docking; Decouplers; Srb; Encounters; Parts }
OrbitSnapshot(apoapsisAlt, periapsisAlt, ecc, incDeg, sma, period){LanDeg, ArgPeDeg, TrueAnomalyDeg, TimeToApoapsis, TimeToPeriapsis, NextPatchEventUt}
EngineSnapshot(Index, Active, VacThrustN, IspS){MinThrottle, ThrottleCmd, PropellantAvailable}
TankSnapshot(Resource, Amount, Capacity){Fraction}   RcsSnapshot(Index, Active, PropellantAvailable, ControlMap)
SolarSnapshot(…)  GeneratorSnapshot(…)  LightSnapshot(…)  DockingSnapshot(Index, Docked, DockedToPart){PushoffImpulseNs}
DecouplerSnapshot(Index, Fired, Enabled)  SrbSnapshot(…)  EncounterSnapshot(Body, Ut, DistanceMeters)
PartSnapshot(index, instance_id, id, display_name, template, is_root, subpart_count, position_vehicle_asmb){Subparts}
NavballSnapshot(pitch, yaw, roll, twr, deltav, frame, speed)  EnvironmentSnapshot(pressurePa, densityKgM3, dynamicPressurePa, oceanDensity, terrainRadius, accelBody, angularAccelBody, gForce)
BodySnapshot(Id, Class, ParentId, ChildIds, Mass, MeanRadius, Mu, SoiMeters, RotationRateRadS, PositionEcl, VelocityEcl, Orbit?, Atmosphere?, Ocean?)
SimEvent(double UtSeconds, string Type, string? VesselId, string Detail)
```

# Units and frames

- Lengths m; speeds m/s; mass kg; μ m³/s²; thrust N; Isp s; time s; power W; energy J; pressure Pa; density kg/m³.
- Angles published in **degrees** (lat, lon, inc, lan, argpe, true anomaly, navball, sun AoA, light cones); rates in **rad/s**. KSA stores orbital angles in radians; `Camera.GetFieldOfView()` returns radians while `SetFieldOfView(float)` takes degrees.
- Frames: `position/cci` = Celestial-Centered Inertial about the parent; `ecl` = system ecliptic; `attitude/quat` = Body→CCI `x y z w`; KSA body frame = X-nose / Y-right / Z-down; part positions are in the vehicle **assembly** frame.
- Apsides: KSA `Orbit.Apoapsis/Periapsis` are radii; gatOS publishes **altitudes** and `0` apoapsis when `!Orbit.IsBound()`. `UniverseTime` saturation sentinels (`EndOfTime`) map to `0`.[^spec]

# Consistency and cost

One snapshot is one self-consistent sample; readers never lock. The sampler is zero-steady-state-alloc (tripwire in the status window). The detail pass is the single most expensive per-vessel work; parts and body statics are cached.[^report]

# Relevance to scripting

An in-process API can either hand scripts these immutable records (cheap, warp-safe, no KSA types leak into the script-facing surface) or wrap live KSA objects with the same reader functions. The DTO vocabulary above is the natural shape for `.d.ts` / LuaCATS stubs.

[^report]: gatOS internals research report (2026-10-16)
[^sampler]: TelemetrySampler.cs
[^vesselreader]: VesselReader.cs
[^snapshot]: SimSnapshot.cs
[^spec]: SPEC_9P_FILESYSTEM.md
