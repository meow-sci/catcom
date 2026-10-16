using HarmonyLib;
using KsaScripting.Core.Values;
using KSA;

namespace KsaScripting.Ksa.Events;

// Harmony event hooks for the vessel control surface: engines, RCS, staging, decoupling. Each class is one
// row of docs/research/ksa-host/event-hook-points.md; see EventPatches for the shared contract.

/// <summary>Patch for <c>engine.activate</c> / <c>engine.deactivate</c>.</summary>
[HarmonyPatch(typeof(EngineController), nameof(EngineController.SetIsActive))]
internal static class EngineActivatePatch
{
    private static bool _faulted;

    [KsaAnchor("EngineController.SetIsActive(Vehicle? vehicle, bool activationState) instance postfix; "
            + "Parts.Modules.Get<EngineController>(); Part.FullPart.Tree.OwningVehicle",
        SourceFile = "KSA/EngineController.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Medium,
        Notes = "EngineController.cs:77. Only enqueues InputEvents.IActivateInputData (applied next ApplyInputEvents), "
            + "so this is the command intent, not the physical edge. `vehicle` may be null: the owner is then "
            + "recovered through Parent.FullPart.Tree.OwningVehicle. payload.engine is the index in "
            + "Parts.Modules.Get<EngineController>() (the same ordinal EngineActuator and VesselReader use), -1 if absent.")]
    private static void Postfix(EngineController __instance, Vehicle? vehicle, bool activationState)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            var owner = vehicle ?? __instance.Parent?.FullPart?.Tree?.OwningVehicle;
            var index = -1;
            if (owner is not null)
            {
                var engines = owner.Parts.Modules.Get<EngineController>();
                for (var i = 0; i < engines.Length; i++)
                {
                    if (!ReferenceEquals(engines[i], __instance))
                        continue;
                    index = i;
                    break;
                }
            }

            EventPatches.Emit(activationState ? "engine.activate" : "engine.deactivate", owner?.Id,
                EventPatches.Payload(("engine", ScriptValue.From(index))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(EngineActivatePatch), ex);
        }
    }
}

/// <summary>Patch for <c>rcs.activate</c>: <c>ThrusterController.SetIsActive</c> postfix.</summary>
[HarmonyPatch(typeof(ThrusterController), nameof(ThrusterController.SetIsActive))]
internal static class RcsActivatePatch
{
    private static bool _faulted;

    [KsaAnchor("ThrusterController.SetIsActive(Vehicle? vehicle, bool activationState) instance postfix; "
            + "Parts.Modules.Get<ThrusterController>()",
        SourceFile = "KSA/ThrusterController.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Medium,
        Notes = "ThrusterController.cs:61. Same shape as EngineController.SetIsActive (enqueues "
            + "IActivateInputData). payload.thruster is the index in Parts.Modules.Get<ThrusterController>() "
            + "(the rcs ordinal), payload.active the requested state. A master RCS toggle fires one event per thruster.")]
    private static void Postfix(ThrusterController __instance, Vehicle? vehicle, bool activationState)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            var owner = vehicle ?? __instance.Parent?.FullPart?.Tree?.OwningVehicle;
            var index = -1;
            if (owner is not null)
            {
                var thrusters = owner.Parts.Modules.Get<ThrusterController>();
                for (var i = 0; i < thrusters.Length; i++)
                {
                    if (!ReferenceEquals(thrusters[i], __instance))
                        continue;
                    index = i;
                    break;
                }
            }

            EventPatches.Emit("rcs.activate", owner?.Id,
                EventPatches.Payload(("thruster", ScriptValue.From(index)), ("active", ScriptValue.From(activationState))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(RcsActivatePatch), ex);
        }
    }
}

/// <summary>Patch for <c>stage</c>: <c>SequenceList.ActivateNextSequence</c> prefix + postfix.</summary>
[HarmonyPatch(typeof(SequenceList), nameof(SequenceList.ActivateNextSequence))]
internal static class StagePatch
{
    private const int Unset = int.MinValue;
    private static bool _faulted;

    [KsaAnchor("SequenceList.ActiveSequence (int, public getter)", SourceFile = "KSA/SequenceList.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "Captures the active sequence before ActivateNextSequence so the postfix can tell whether a stage "
            + "was really activated (the method does nothing when no un-activated sequence remains).")]
    private static void Prefix(SequenceList __instance, out int __state)
    {
        __state = Unset;
        if (_faulted)
            return;
        try
        {
            __state = __instance.ActiveSequence;
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(StagePatch), ex);
        }
    }

    [KsaAnchor("SequenceList.ActivateNextSequence(Vehicle vehicle) instance postfix; SequenceList.ActiveSequence",
        SourceFile = "KSA/SequenceList.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Medium,
        Notes = "SequenceList.cs:171. Sets ActiveSequence and activates the ISequenced modules of the stage (their own "
            + "SetIsActive enqueue lands next frame). Published only when ActiveSequence changed; payload.sequence is "
            + "the newly active sequence number.")]
    private static void Postfix(SequenceList __instance, Vehicle vehicle, int __state)
    {
        if (_faulted || __state == Unset || EventPatches.Sink is null)
            return;
        try
        {
            var active = __instance.ActiveSequence;
            if (active == __state)
                return;
            EventPatches.Emit("stage", vehicle?.Id, EventPatches.Payload(("sequence", ScriptValue.From(active))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(StagePatch), ex);
        }
    }
}

/// <summary>Patch for <c>decouple</c>: <c>Decoupler.Decouple</c> postfix.</summary>
[HarmonyPatch(typeof(Decoupler), nameof(Decoupler.Decouple))]
internal static class DecouplePatch
{
    private static bool _faulted;

    [KsaAnchor("Decoupler.Decouple(Vehicle oldVehicle) → Vehicle? instance postfix (__result)",
        SourceFile = "KSA/Decoupler.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Medium,
        Notes = "Decoupler.cs:163. Reached through Decoupler.SetIsActive → IActivateInputData { Decouple }; returns the "
            + "separated vehicle (null when the split failed, in which case nothing is published). vesselId is the "
            + "vehicle that decoupled, payload.newVesselId the one that separated.")]
    private static void Postfix(Vehicle oldVehicle, Vehicle? __result)
    {
        if (_faulted || __result is null || EventPatches.Sink is null)
            return;
        try
        {
            EventPatches.Emit("decouple", oldVehicle.Id,
                EventPatches.Payload(("newVesselId", ScriptValue.From(__result.Id))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(DecouplePatch), ex);
        }
    }
}
