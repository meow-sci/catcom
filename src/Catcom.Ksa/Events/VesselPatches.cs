using HarmonyLib;
using Catcom.Core.Snapshots;
using Catcom.Core.Values;
using KSA;

namespace Catcom.Ksa.Events;

// Harmony event hooks for the vessel lifecycle: separation, docking, spawn/remove, launch, impact,
// destruction, part failure. Each class is one row of docs/research/ksa-host/event-hook-points.md.

/// <summary>Patch for <c>split</c>: any separation (decouple, undock, debris) via <c>Vehicle.Split</c>.</summary>
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.Split),
    new[] { typeof(Part.Connection), typeof(Part.Connection.IConnector), typeof(double), typeof(PoseChange), typeof(string) },
    new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal })]
internal static class SplitPatch
{
    private static bool _faulted;

    [KsaAnchor("Vehicle.Split(Part.Connection splitConnection, Part.Connection.IConnector splitEndpoint, "
            + "double splitImpulse, out PoseChange combinedToSplit, string? splitVehicleId = null) → Vehicle? instance postfix",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-10-22", GameVersion = "2026.10.19.5638", Risk = ChurnRisk.Medium,
        Notes = "Vehicle.cs:1527 — the connection-based overload that the (Part.Connector, double, out PoseChange, string?) "
            + "overload at :1516 (used by Decoupler.Decouple and DockingPort.Undock) forwards to, so patching this one "
            + "fires exactly once for every separation including part-failure debris, and survives JIT inlining of the "
            + "thin wrapper. The result is the new vehicle (null when the split failed: nothing is published). "
            + "vesselId is the vehicle that was split, payload.newVesselId the new one."
            + " 5638: Vehicle.cs:1608; now also repartitions symmetry links by tree (Part.PartitionSymmetryLinksByTree). Parameters and split semantics unchanged.")]
    private static void Postfix(Vehicle __instance, Vehicle? __result)
    {
        if (_faulted || __result is null || EventPatches.Sink is null)
            return;
        try
        {
            EventPatches.Emit("split", __instance.Id, EventPatches.Payload(("newVesselId", ScriptValue.From(__result.Id))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(SplitPatch), ex);
        }
    }
}

/// <summary>Patch for <c>dock</c>: <c>DockingPort.Dock</c> postfix.</summary>
[HarmonyPatch(typeof(DockingPort), nameof(DockingPort.Dock))]
internal static class DockPatch
{
    private static bool _faulted;

    [KsaAnchor("DockingPort.Dock(Vehicle thisVehicle, Vehicle otherVehicle, DockingPort otherVehicleDockingPort, "
            + "out PoseChange consumedToCombined) → Vehicle? instance postfix; DockingPort.Docked",
        SourceFile = "KSA/DockingPort.cs", Verified = "2026-10-22", GameVersion = "2026.10.19.5638",
        Risk = ChurnRisk.Medium,
        Notes = "DockingPort.cs:422. Merges thisVehicle into otherVehicle (otherVehicle.MergeFrom) and returns "
            + "otherVehicle — the survivor, which becomes vesselId; payload.otherVesselId is the vehicle that was "
            + "merged away. The result is non-null even if the merge failed, so the postfix also requires "
            + "DockingPort.Docked (Connector.Connection != null)."
            + " 5638: DockingPort.cs:439, byte-identical.")]
    private static void Postfix(DockingPort __instance, Vehicle thisVehicle, Vehicle? __result)
    {
        if (_faulted || __result is null || EventPatches.Sink is null)
            return;
        try
        {
            if (!__instance.Docked)
                return;
            EventPatches.Emit("dock", __result.Id, EventPatches.Payload(("otherVesselId", ScriptValue.From(thisVehicle.Id))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(DockPatch), ex);
        }
    }
}

/// <summary>Patch for <c>undock</c>: <c>DockingPort.Undock</c> postfix.</summary>
[HarmonyPatch(typeof(DockingPort), nameof(DockingPort.Undock))]
internal static class UndockPatch
{
    private static bool _faulted;

    [KsaAnchor("DockingPort.Undock(Vehicle oldVehicle, out PoseChange combinedToSplit) → Vehicle? instance postfix",
        SourceFile = "KSA/DockingPort.cs", Verified = "2026-10-22", GameVersion = "2026.10.19.5638",
        Risk = ChurnRisk.Medium,
        Notes = "DockingPort.cs:460. A one-line forward to oldVehicle.Split(Connector, PushoffImpulse, out ...); returns the "
            + "separated vehicle (null when nothing split). vesselId is the vehicle that undocked, "
            + "payload.newVesselId the one that separated."
            + " 5638: DockingPort.cs:477, byte-identical.")]
    private static void Postfix(Vehicle oldVehicle, Vehicle? __result)
    {
        if (_faulted || __result is null || EventPatches.Sink is null)
            return;
        try
        {
            EventPatches.Emit("undock", oldVehicle.Id, EventPatches.Payload(("newVesselId", ScriptValue.From(__result.Id))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(UndockPatch), ex);
        }
    }
}

/// <summary>Patch for <c>vessel.spawn</c>: <c>CelestialSystem.Register</c> postfix (vehicles only).</summary>
[HarmonyPatch(typeof(CelestialSystem), nameof(CelestialSystem.Register))]
internal static class VesselSpawnPatch
{
    private static bool _faulted;

    [KsaAnchor("CelestialSystem.Register(Astronomical celestial) instance postfix; Vehicle.Id",
        SourceFile = "KSA/CelestialSystem.cs", Verified = "2026-10-22", GameVersion = "2026.10.19.5638",
        Risk = ChurnRisk.Low,
        Notes = "CelestialSystem.cs:88. Every registration (vehicles, debris, EVA kittens, save load); only Vehicle "
            + "instances publish. CelestialSystem.Rename re-registers a renamed vehicle through this method, so a "
            + "rename surfaces as vessel.spawn under the new id. payload.name is Vehicle.Id (KSA has no separate "
            + "display name)."
            + " 5638: CelestialSystem.cs:96, byte-identical.")]
    private static void Postfix(Astronomical celestial)
    {
        if (_faulted || celestial is not Vehicle vehicle || EventPatches.Sink is null)
            return;
        try
        {
            EventPatches.Emit("vessel.spawn", vehicle.Id, EventPatches.Payload(("name", ScriptValue.From(vehicle.Id))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(VesselSpawnPatch), ex);
        }
    }
}

/// <summary>Patch for <c>vessel.remove</c>: <c>CelestialSystem.Deregister</c> postfix (vehicles only).</summary>
[HarmonyPatch(typeof(CelestialSystem), nameof(CelestialSystem.Deregister))]
internal static class VesselRemovePatch
{
    private static bool _faulted;

    [KsaAnchor("CelestialSystem.Deregister(Astronomical astronomical) instance postfix; Vehicle.Id",
        SourceFile = "KSA/CelestialSystem.cs", Verified = "2026-10-22", GameVersion = "2026.10.19.5638",
        Risk = ChurnRisk.Low,
        Notes = "CelestialSystem.cs:93. Destroy, recovery and docking merges end here; DestroyAllVehicles (save load) and "
            + "Rename deregister through the private collection and do not publish."
            + " 5638: CelestialSystem.cs:101, byte-identical.")]
    private static void Postfix(Astronomical astronomical)
    {
        if (_faulted || astronomical is not Vehicle vehicle || EventPatches.Sink is null)
            return;
        try
        {
            EventPatches.Emit("vessel.remove", vehicle.Id, EventPatches.Payload(("name", ScriptValue.From(vehicle.Id))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(VesselRemovePatch), ex);
        }
    }
}

/// <summary>Patch for <c>vessel.launch</c>: <c>Vehicle.MarkLaunched</c> prefix + postfix.</summary>
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.MarkLaunched))]
internal static class VesselLaunchPatch
{
    private static bool _faulted;

    [KsaAnchor("Vehicle.HasLaunched (bool, public getter)", SourceFile = "KSA/Vehicle.cs", Verified = "2026-10-22",
        GameVersion = "2026.10.19.5638", Risk = ChurnRisk.Low,
        Notes = "Captured before MarkLaunched so the event fires only on the false → true edge (the method is idempotent)."
            + " 5638: Vehicle.cs:405, unchanged.")]
    private static void Prefix(Vehicle __instance, out bool __state)
    {
        __state = true;
        if (_faulted)
            return;
        try
        {
            __state = __instance.HasLaunched;
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(VesselLaunchPatch), ex);
        }
    }

    [KsaAnchor("Vehicle.MarkLaunched() instance postfix; Vehicle.HasLaunched", SourceFile = "KSA/Vehicle.cs",
        Verified = "2026-10-22", GameVersion = "2026.10.19.5638", Risk = ChurnRisk.Low,
        Notes = "Vehicle.cs:905. Sets HasLaunched = true. The situation-change differ event covers the physical liftoff."
            + " 5638: Vehicle.cs:982, byte-identical.")]
    private static void Postfix(Vehicle __instance, bool __state)
    {
        if (_faulted || __state || EventPatches.Sink is null)
            return;
        try
        {
            if (__instance.HasLaunched)
                EventPatches.Emit("vessel.launch", __instance.Id, EventPatches.Payload());
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(VesselLaunchPatch), ex);
        }
    }
}

/// <summary>Patch for <c>impact</c> (ground): <c>Celestial.TrySpawnGroundImpact</c> prefix.</summary>
[HarmonyPatch(typeof(Celestial), nameof(Celestial.TrySpawnGroundImpact))]
internal static class ImpactGroundPatch
{
    private static bool _faulted;

    [KsaAnchor("Celestial.TrySpawnGroundImpact(GroundImpactEvent impactEvent) instance prefix; "
            + "GroundImpactEvent.{Vehicle, ImpactKineticEnergy}; Celestial.Id",
        SourceFile = "KSA/Celestial.cs / KSA/GroundImpactEvent.cs", Verified = "2026-10-22",
        GameVersion = "2026.10.19.5638", Risk = ChurnRisk.Medium,
        Notes = "Celestial.cs:262. The impact effect spawn; it returns early below 50 J of kinetic energy and the caller "
            + "(GroundImpactEvent.Apply) already skipped suppressed vehicles, so the same 50 J floor is applied here. "
            + "payload.kind = \"ground\", payload.body = the celestial's id. No script-visible energy field: the "
            + "catalog lists only kind and body."
            + " 5638: Celestial.cs:271, decompiler reshaping only.")]
    private static void Prefix(Celestial __instance, GroundImpactEvent impactEvent)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            if (impactEvent.ImpactKineticEnergy < 50f)
                return;
            EventPatches.Emit("impact", impactEvent.Vehicle?.Id,
                EventPatches.Payload(("kind", ScriptValue.From("ground")), ("body", ScriptValue.From(__instance.Id))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(ImpactGroundPatch), ex);
        }
    }
}

/// <summary>Patch for <c>impact</c> (water): <c>Celestial.TrySpawnWaterSplash</c> prefix.</summary>
[HarmonyPatch(typeof(Celestial), nameof(Celestial.TrySpawnWaterSplash))]
internal static class ImpactWaterPatch
{
    private static bool _faulted;

    [KsaAnchor("Celestial.TrySpawnWaterSplash(WaterSplashEvent splashEvent) instance prefix; "
            + "WaterSplashEvent.{Vehicle, ImpactKineticEnergy}; Celestial.Id",
        SourceFile = "KSA/Celestial.cs / KSA/WaterSplashEvent.cs", Verified = "2026-10-22",
        GameVersion = "2026.10.19.5638", Risk = ChurnRisk.Medium,
        Notes = "Celestial.cs:328. Returns early below 50 J (mirrored here) and when the body has no ocean "
            + "(BodyTemplate.OceanReference == null — not mirrored: a splash event only arises over an ocean). "
            + "payload.kind = \"water\", payload.body = the celestial's id."
            + " 5638: Celestial.cs:335; the ocean check joined the 50 J guard (same behavior).")]
    private static void Prefix(Celestial __instance, WaterSplashEvent splashEvent)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            if (splashEvent.ImpactKineticEnergy < 50f)
                return;
            EventPatches.Emit("impact", splashEvent.Vehicle?.Id,
                EventPatches.Payload(("kind", ScriptValue.From("water")), ("body", ScriptValue.From(__instance.Id))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(ImpactWaterPatch), ex);
        }
    }
}

/// <summary>
///     The frame-scoped hand-off between the two destruction prefixes: <c>DestroyVehicleFromEvent</c> always
///     ends in <c>DestroyVehicle</c>, so the second must not publish a second <c>vessel.destroy</c> for the
///     vehicle the first already reported this frame.
/// </summary>
internal static class DestroyDedupe
{
    private static string? _vesselId;
    private static ulong _frame;

    /// <summary>Records that <c>vessel.destroy</c> was published for <paramref name="vesselId"/> this frame.</summary>
    /// <param name="vesselId">The vessel id.</param>
    /// <param name="frame">The render frame number.</param>
    internal static void Record(string vesselId, ulong frame)
    {
        _vesselId = vesselId;
        _frame = frame;
    }

    /// <summary>Whether <c>vessel.destroy</c> was already published for the vessel in this frame.</summary>
    /// <param name="vesselId">The vessel id.</param>
    /// <param name="frame">The render frame number.</param>
    /// <returns><see langword="true"/> when the destruction was already reported.</returns>
    internal static bool Seen(string vesselId, ulong frame)
        => _frame == frame && string.Equals(_vesselId, vesselId, StringComparison.Ordinal);
}

/// <summary>Patch for <c>vessel.destroy</c> with a cause: <c>Universe.DestroyVehicleFromEvent</c> prefix.</summary>
[HarmonyPatch(typeof(Universe), nameof(Universe.DestroyVehicleFromEvent))]
internal static class DestroyFromEventPatch
{
    private static bool _faulted;

    [KsaAnchor("Universe.DestroyVehicleFromEvent(Vehicle vehicle, VehicleDestructionEvent destructionEvent) static prefix; "
            + "VehicleDestructionEvent.{Cause, PeakGLoad, PeakDynamicPressure}; Vehicle.IsDisposed; Program.FrameNumber",
        SourceFile = "KSA/Universe.cs / KSA/VehicleDestructionEvent.cs", Verified = "2026-10-22",
        GameVersion = "2026.10.19.5638", Risk = ChurnRisk.Medium,
        Notes = "Universe.cs:1971. Destruction with a cause (GroundImpact, OceanImpact, Collision, ExcessiveGForce, "
            + "AerodynamicForces, HydrodynamicForces — published as the enum name); it ends in DestroyVehicle, which "
            + "DestroyRemovedPatch de-duplicates per (vessel id, Program.FrameNumber). payload.peakG / peakQ are "
            + "PeakGLoad (g) / PeakDynamicPressure (Pa) as numbers. Skipped when the vehicle is already disposed "
            + "(the game returns immediately)."
            + " 5638: Universe.cs:1969, byte-identical.")]
    private static void Prefix(Vehicle vehicle, VehicleDestructionEvent destructionEvent)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            if (vehicle.IsDisposed)
                return;
            DestroyDedupe.Record(vehicle.Id, Program.FrameNumber);
            EventPatches.Emit("vessel.destroy", vehicle.Id, EventPatches.Payload(
                ("cause", ScriptValue.From(destructionEvent.Cause.ToString())),
                ("peakG", ScriptValue.From(Sanitize.Finite(destructionEvent.PeakGLoad))),
                ("peakQ", ScriptValue.From(Sanitize.Finite(destructionEvent.PeakDynamicPressure)))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(DestroyFromEventPatch), ex);
        }
    }
}

/// <summary>Patch for <c>vessel.destroy</c> with cause <c>"removed"</c>: <c>Universe.DestroyVehicle</c> prefix.</summary>
[HarmonyPatch(typeof(Universe), nameof(Universe.DestroyVehicle))]
internal static class DestroyRemovedPatch
{
    private static bool _faulted;

    [KsaAnchor("Universe.DestroyVehicle(Vehicle vehicle, CrewDisposition crew = EndMission) static prefix; "
            + "Vehicle.IsDisposed; Program.FrameNumber",
        SourceFile = "KSA/Universe.cs", Verified = "2026-10-22", GameVersion = "2026.10.19.5638", Risk = ChurnRisk.Medium,
        Notes = "Universe.cs:2018. Every removal: recovery, the console `destroy`, part-failure whole-vehicle loss and the "
            + "tail of DestroyVehicleFromEvent. Published with cause \"removed\" and peakG = peakQ = 0 unless "
            + "DestroyFromEventPatch already reported this vessel in the same Program.FrameNumber. Skipped when the "
            + "vehicle is already disposed. 2026.10.19.5638: Universe.cs:2016; kitten boarding now ends here too "
            + "(KittenEva.BoardVehicle → DestroyVehicle(this, CrewDisposition.Boarded), new enum value) where it used to "
            + "Dispose the kitten directly — skipped, since boarding is not a destruction (vessel.remove still fires).")]
    private static void Prefix(Vehicle vehicle, CrewDisposition crew)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            if (crew == CrewDisposition.Boarded || vehicle.IsDisposed
                || DestroyDedupe.Seen(vehicle.Id, Program.FrameNumber))
                return;
            EventPatches.Emit("vessel.destroy", vehicle.Id, EventPatches.Payload(
                ("cause", ScriptValue.From("removed")),
                ("peakG", ScriptValue.From(0)),
                ("peakQ", ScriptValue.From(0))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(DestroyRemovedPatch), ex);
        }
    }
}

/// <summary>Patch for <c>part.failure</c>: <c>PartFailureEvent.Apply</c> prefix.</summary>
[HarmonyPatch(typeof(PartFailureEvent), nameof(PartFailureEvent.Apply))]
internal static class PartFailurePatch
{
    private static bool _faulted;

    [KsaAnchor("PartFailureEvent.Apply(Vehicle vehicle) instance prefix; PartFailureEvent.{FailedParts, DestroyWholeVehicle}; "
            + "Part.Id; Vehicle.IsDisposed",
        SourceFile = "KSA/PartFailureEvent.cs", Verified = "2026-10-22", GameVersion = "2026.10.19.5638",
        Risk = ChurnRisk.Medium,
        Notes = "PartFailureEvent.cs:16 (IVehicleRenderEvent, applied in ApplyVehicleSolvers). payload.parts is the list of "
            + "failed Part.Id strings (not unique across instances of one template — the part is gone afterwards, so "
            + "InstanceId could not be resolved by a script anyway), payload.destroyWholeVehicle the flag. Skipped when "
            + "the vehicle is already disposed (the game returns immediately)."
            + " 5638: PartFailureEvent.cs:16, decompiler reshaping only.")]
    private static void Prefix(PartFailureEvent __instance, Vehicle vehicle)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            if (vehicle.IsDisposed)
                return;
            var failed = __instance.FailedParts;
            var parts = new List<ScriptValue>(failed.Count);
            foreach (var part in failed)
                parts.Add(ScriptValue.From(part.Id));
            EventPatches.Emit("part.failure", vehicle.Id, EventPatches.Payload(
                ("parts", ScriptValue.List(parts)),
                ("destroyWholeVehicle", ScriptValue.From(__instance.DestroyWholeVehicle))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(PartFailurePatch), ex);
        }
    }
}
