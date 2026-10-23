// Build-as-alarm: one [HarmonyPatch] stub per member the Ksa facade patches (SlotHost, EventPatches and
// VesselForceRender), with the SAME target attributes and the SAME Prefix/Postfix parameter lists as the real
// patch, plus a `Sig` method that calls the target with NAMED arguments. A game update that renames or
// removes a patched type/member, changes its parameter types, or renames a parameter the real patch binds
// by name (Harmony injects arguments by name) therefore breaks THIS build instead of surfacing as a
// runtime patch failure in-game. Nothing here is ever executed or shipped.
using HarmonyLib;
using KSA;
using StarMap.API;
// The game type is named explicitly so the signatures read unambiguously (as in VesselForceRender).
using KsaCamera = KSA.Camera;

namespace Catcom.Ksa.CompileCheck;

// ---------------------------------------------------------------------------------------------------------
// StarMap skeleton (from spikes/HookCompileCheck/Mod.cs): checks the StarMap attribute signatures the
// GameMod's lifecycle methods use.
// ---------------------------------------------------------------------------------------------------------

[StarMapMod]
public sealed class Mod
{
    private static readonly Harmony _harmony = new("catcom.compilecheck");
    private static readonly Queue<Action> _gameThreadQueue = new();

    [StarMapImmediateLoad]
    public void OnImmediateLoad(KSA.Mod definingMod) => _ = definingMod;

    [StarMapAllModsLoaded]
    public void OnAllModsLoaded() => _harmony.PatchAll(typeof(Mod).Assembly);

    [StarMapBeforeGui]
    public void OnBeforeGui(double dt)
    {
        _ = dt;
        lock (_gameThreadQueue)
            while (_gameThreadQueue.Count > 0)
                _gameThreadQueue.Dequeue()();
    }

    [StarMapAfterGui]
    public void OnAfterGui(double dt) => _ = dt;

    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        _ = currentPlayerTime;
        _ = dtPlayer;
    }

    [StarMapUnload]
    public void OnUnload() => _harmony.UnpatchAll(_harmony.Id);

    public static void Post(Action a)
    {
        lock (_gameThreadQueue)
            _gameThreadQueue.Enqueue(a);
    }
}

// ---------------------------------------------------------------------------------------------------------
// Slots (SlotHost)
// ---------------------------------------------------------------------------------------------------------

[HarmonyPatch(typeof(Universe), nameof(Universe.ApplyVehicleSolvers))]
internal static class StubStepSlot
{
    private static void Postfix() { }

    private static void Sig() => Universe.ApplyVehicleSolvers();
}

[HarmonyPatch(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers))]
internal static class StubSolverSlot
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(double dtPlayer, SimStep simStep)
    {
        _ = dtPlayer;
        _ = simStep;
    }

    private static void Sig(SimStep step) => Universe.ExecuteNextVehicleSolvers(dtPlayer: 0.0, simStep: step);

    // The members SlotHost reads each step.
    private static (double, double, UniverseTime, ulong, bool) Reads()
    {
        var sim = Universe.GetLastSimStep();
        return (Universe.GetElapsedSeconds(), sim.DeltaTime, sim.NextTime, Program.FrameNumber, sim.NextTime != sim.PreviousTime);
    }
}

// ---------------------------------------------------------------------------------------------------------
// Events (EventPatches): control surface
// ---------------------------------------------------------------------------------------------------------

[HarmonyPatch(typeof(EngineController), nameof(EngineController.SetIsActive))]
internal static class StubEngineActivate
{
    private static void Postfix(EngineController __instance, Vehicle? vehicle, bool activationState)
    {
        _ = __instance;
        _ = vehicle;
        _ = activationState;
    }

    private static void Sig(EngineController engine, Vehicle v) => engine.SetIsActive(vehicle: v, activationState: true);

    private static Vehicle? Owner(EngineController engine) => engine.Parent?.FullPart?.Tree?.OwningVehicle;
}

[HarmonyPatch(typeof(ThrusterController), nameof(ThrusterController.SetIsActive))]
internal static class StubRcsActivate
{
    private static void Postfix(ThrusterController __instance, Vehicle? vehicle, bool activationState)
    {
        _ = __instance;
        _ = vehicle;
        _ = activationState;
    }

    private static void Sig(ThrusterController thruster, Vehicle v) => thruster.SetIsActive(vehicle: v, activationState: true);
}

[HarmonyPatch(typeof(SequenceList), nameof(SequenceList.ActivateNextSequence))]
internal static class StubStage
{
    private static void Prefix(SequenceList __instance, out int __state)
    {
        __state = __instance.ActiveSequence;
    }

    private static void Postfix(SequenceList __instance, Vehicle vehicle, int __state)
    {
        _ = __instance;
        _ = vehicle;
        _ = __state;
    }

    private static void Sig(SequenceList list, Vehicle v) => list.ActivateNextSequence(vehicle: v);
}

[HarmonyPatch(typeof(Decoupler), nameof(Decoupler.Decouple))]
internal static class StubDecouple
{
    private static void Postfix(Vehicle oldVehicle, Vehicle? __result)
    {
        _ = oldVehicle;
        _ = __result;
    }

    private static Vehicle? Sig(Decoupler decoupler, Vehicle v) => decoupler.Decouple(oldVehicle: v);
}

// ---------------------------------------------------------------------------------------------------------
// Events: vessel lifecycle
// ---------------------------------------------------------------------------------------------------------

[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.Split),
    new[] { typeof(Part.Connection), typeof(Part.Connection.IConnector), typeof(double), typeof(PoseChange), typeof(string) },
    new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal })]
internal static class StubSplit
{
    private static void Postfix(Vehicle __instance, Vehicle? __result)
    {
        _ = __instance;
        _ = __result;
    }

    private static Vehicle? Sig(Vehicle v, Part.Connection connection, Part.Connection.IConnector endpoint)
        => v.Split(splitConnection: connection, splitEndpoint: endpoint, splitImpulse: 0.0,
            combinedToSplit: out _, splitVehicleId: null);
}

[HarmonyPatch(typeof(DockingPort), nameof(DockingPort.Dock))]
internal static class StubDock
{
    private static void Postfix(DockingPort __instance, Vehicle thisVehicle, Vehicle? __result)
    {
        _ = __instance.Docked;
        _ = thisVehicle;
        _ = __result;
    }

    private static Vehicle? Sig(DockingPort port, Vehicle v, DockingPort other)
        => port.Dock(thisVehicle: v, otherVehicle: v, otherVehicleDockingPort: other, consumedToCombined: out _);
}

[HarmonyPatch(typeof(DockingPort), nameof(DockingPort.Undock))]
internal static class StubUndock
{
    private static void Postfix(Vehicle oldVehicle, Vehicle? __result)
    {
        _ = oldVehicle;
        _ = __result;
    }

    private static Vehicle? Sig(DockingPort port, Vehicle v) => port.Undock(oldVehicle: v, combinedToSplit: out _);
}

[HarmonyPatch(typeof(CelestialSystem), nameof(CelestialSystem.Register))]
internal static class StubVesselSpawn
{
    private static void Postfix(Astronomical celestial) => _ = celestial;

    private static void Sig(CelestialSystem system, Astronomical a) => system.Register(celestial: a);
}

[HarmonyPatch(typeof(CelestialSystem), nameof(CelestialSystem.Deregister))]
internal static class StubVesselRemove
{
    private static void Postfix(Astronomical astronomical) => _ = astronomical;

    private static void Sig(CelestialSystem system, Astronomical a) => system.Deregister(astronomical: a);
}

[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.MarkLaunched))]
internal static class StubVesselLaunch
{
    private static void Prefix(Vehicle __instance, out bool __state) => __state = __instance.HasLaunched;

    private static void Postfix(Vehicle __instance, bool __state)
    {
        _ = __instance;
        _ = __state;
    }

    private static void Sig(Vehicle v) => v.MarkLaunched();
}

[HarmonyPatch(typeof(Celestial), nameof(Celestial.TrySpawnGroundImpact))]
internal static class StubImpactGround
{
    private static void Prefix(Celestial __instance, GroundImpactEvent impactEvent)
    {
        _ = __instance.Id;
        _ = impactEvent.ImpactKineticEnergy;
        _ = impactEvent.Vehicle?.Id;
    }

    private static void Sig(Celestial body, GroundImpactEvent e) => body.TrySpawnGroundImpact(impactEvent: e);
}

[HarmonyPatch(typeof(Celestial), nameof(Celestial.TrySpawnWaterSplash))]
internal static class StubImpactWater
{
    private static void Prefix(Celestial __instance, WaterSplashEvent splashEvent)
    {
        _ = __instance.Id;
        _ = splashEvent.ImpactKineticEnergy;
        _ = splashEvent.Vehicle?.Id;
    }

    private static void Sig(Celestial body, WaterSplashEvent e) => body.TrySpawnWaterSplash(splashEvent: e);
}

[HarmonyPatch(typeof(Universe), nameof(Universe.DestroyVehicleFromEvent))]
internal static class StubDestroyFromEvent
{
    private static void Prefix(Vehicle vehicle, VehicleDestructionEvent destructionEvent)
    {
        _ = vehicle.IsDisposed;
        _ = destructionEvent.Cause.ToString();
        _ = destructionEvent.PeakGLoad;
        _ = destructionEvent.PeakDynamicPressure;
    }

    private static void Sig(Vehicle v, VehicleDestructionEvent e)
        => Universe.DestroyVehicleFromEvent(vehicle: v, destructionEvent: e);
}

[HarmonyPatch(typeof(Universe), nameof(Universe.DestroyVehicle))]
internal static class StubDestroyRemoved
{
    private static void Prefix(Vehicle vehicle, CrewDisposition crew) => _ = vehicle.IsDisposed || crew == CrewDisposition.Boarded;

    private static void Sig(Vehicle v) => Universe.DestroyVehicle(vehicle: v, crew: CrewDisposition.Kill);
}

[HarmonyPatch(typeof(PartFailureEvent), nameof(PartFailureEvent.Apply))]
internal static class StubPartFailure
{
    private static void Prefix(PartFailureEvent __instance, Vehicle vehicle)
    {
        _ = vehicle.IsDisposed;
        foreach (var part in __instance.FailedParts)
            _ = part.Id;
        _ = __instance.DestroyWholeVehicle;
    }

    private static void Sig(PartFailureEvent e, Vehicle v) => e.Apply(vehicle: v);
}

// ---------------------------------------------------------------------------------------------------------
// Events: session
// ---------------------------------------------------------------------------------------------------------

[HarmonyPatch(typeof(Program), nameof(Program.ControlledVehicle), MethodType.Setter)]
internal static class StubControlChange
{
    private static void Prefix(out string? __state) => __state = Program.ControlledVehicle?.Id;

    private static void Postfix(string? __state) => _ = __state;

    private static void Sig(Vehicle? v) => Program.ControlledVehicle = v;
}

[HarmonyPatch(typeof(VehicleEditor), MethodType.Constructor, typeof(Vehicle), typeof(bool))]
internal static class StubEditorEnter
{
    private static void Postfix(Vehicle? inEditingVehicle, bool newVehicle)
    {
        _ = inEditingVehicle;
        _ = newVehicle;
    }

    private static VehicleEditor Sig(Vehicle? v) => new(inEditingVehicle: v, newVehicle: false);
}

[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.Dispose))]
internal static class StubEditorExit
{
    private static void Prefix(VehicleEditor __instance) => _ = __instance.ExistingVehicle?.Id;

    private static void Sig(VehicleEditor editor) => editor.Dispose();
}

[HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Write))]
internal static class StubSaveWrite
{
    private static void Postfix(UncompressedSave __instance, bool __result)
    {
        _ = __instance.Directory.FullName;
        _ = __result;
    }

    private static bool Sig(UncompressedSave save) => save.Write();
}

[HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Load))]
internal static class StubSaveLoadDirectory
{
    private static void Prefix(UncompressedSave __instance) => _ = __instance.Directory.FullName;

    private static void Finalizer() { }

    private static void Sig(UncompressedSave save) => save.Load();
}

[HarmonyPatch(typeof(Program), nameof(Program.OnGameLoaded))]
internal static class StubSaveLoad
{
    private static void Postfix() { }

    private static void Sig() => Program.OnGameLoaded();
}

[HarmonyPatch(typeof(Universe), nameof(Universe.LoadSystem))]
internal static class StubSystemLoad
{
    private static void Postfix(string id) => _ = id;

    private static void Sig() => Universe.LoadSystem(id: "x");
}

[HarmonyPatch(typeof(Universe), nameof(Universe.SetSimulationSpeed), typeof(double), typeof(bool))]
internal static class StubWarpChange
{
    private static void Prefix(double simulationSpeed) => _ = simulationSpeed + Universe.GetSimulationSpeed();

    private static void Sig() => Universe.SetSimulationSpeed(simulationSpeed: 1.0, alert: true);
}

[HarmonyPatch(typeof(FlightComputer), nameof(FlightComputer.RaisePendingAlerts))]
internal static class StubBurnAlerts
{
    private static void Prefix(FlightComputer __instance, Vehicle vehicle)
    {
        _ = __instance.AutoBurnCompleted;
        _ = __instance.AutoBurnStoppedOutOfPropellant;
        _ = __instance.AutoBurnStageRanDry;
        _ = vehicle.Id;
    }

    private static void Sig(FlightComputer fc, Vehicle v) => fc.RaisePendingAlerts(vehicle: v);
}

// ---------------------------------------------------------------------------------------------------------
// Render (VesselForceRender): patched lazily on the first always_render mark
// ---------------------------------------------------------------------------------------------------------

[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.GetWorldMatrix), typeof(KsaCamera))]
internal static class StubForceRenderWorldMatrix
{
    private static bool Prefix(Vehicle __instance, KsaCamera camera, ref Brutal.Numerics.float4x4? __result)
    {
        _ = __instance;
        _ = camera;
        _ = __result;
        return true;
    }

    private static Brutal.Numerics.float4x4? Sig(Vehicle v, KsaCamera camera) => v.GetWorldMatrix(camera: camera);
}

[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.UpdateRenderData), typeof(IViewport), typeof(int))]
internal static class StubForceRenderUpdateRenderData
{
    private static bool Prefix(Vehicle __instance, IViewport viewport, int inFrameIndex)
    {
        _ = __instance;
        _ = viewport;
        _ = inFrameIndex;
        return true;
    }

    private static void Sig(Vehicle v, IViewport viewport) => v.UpdateRenderData(viewport: viewport, inFrameIndex: 0);
}
