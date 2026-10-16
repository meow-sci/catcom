using HarmonyLib;
using KsaScripting.Core.Snapshots;
using KsaScripting.Core.Values;
using KSA;

namespace KsaScripting.Ksa.Events;

// Harmony event hooks for the session: control, editor, save/load, system load, time warp and the flight
// computer's burn alerts. Each class is one row of docs/research/ksa-host/event-hook-points.md.

/// <summary>Patch for <c>control.change</c>: the <c>Program.ControlledVehicle</c> setter, diffed.</summary>
[HarmonyPatch(typeof(Program), nameof(Program.ControlledVehicle), MethodType.Setter)]
internal static class ControlChangePatch
{
    private const string Unset = "\u0000unset";
    private static bool _faulted;

    [KsaAnchor("Program.ControlledVehicle getter (static, Vehicle?)", SourceFile = "KSA/Program.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "Captures the previous controlled vehicle id before the setter runs; the setter is called with the "
            + "same value often, so the postfix publishes only when the id changed.")]
    private static void Prefix(out string? __state)
    {
        __state = Unset;
        if (_faulted)
            return;
        try
        {
            __state = Program.ControlledVehicle?.Id;
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(ControlChangePatch), ex);
        }
    }

    [KsaAnchor("Program.ControlledVehicle setter set_ControlledVehicle(Vehicle? value) static postfix",
        SourceFile = "KSA/Program.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "Program.cs:502 (static property; the setter ClearHeldPlayerInput()s the previous vehicle on change). "
            + "payload.from / payload.to are vehicle ids or null for \"no vessel\"; vesselId is the newly controlled "
            + "vessel (null when control was released).")]
    private static void Postfix(string? __state)
    {
        if (_faulted || ReferenceEquals(__state, Unset) || EventPatches.Sink is null)
            return;
        try
        {
            var to = Program.ControlledVehicle?.Id;
            if (string.Equals(__state, to, StringComparison.Ordinal))
                return;
            EventPatches.Emit("control.change", to, EventPatches.Payload(
                ("from", ScriptValue.From(__state)),
                ("to", ScriptValue.From(to))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(ControlChangePatch), ex);
        }
    }
}

/// <summary>Patch for <c>editor.enter</c>: <c>VehicleEditor</c> constructor postfix.</summary>
[HarmonyPatch(typeof(VehicleEditor), MethodType.Constructor, typeof(Vehicle), typeof(bool))]
internal static class EditorEnterPatch
{
    private static bool _faulted;

    [KsaAnchor("VehicleEditor..ctor(Vehicle? inEditingVehicle, bool newVehicle) postfix", SourceFile = "KSA/VehicleEditor.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "VehicleEditor.cs:895. Constructed by Program.OnFrameEditor when EditorFlag is set. vesselId is the vehicle "
            + "being edited (null for a new vehicle), payload.newVehicle the constructor flag.")]
    private static void Postfix(Vehicle? inEditingVehicle, bool newVehicle)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            EventPatches.Emit("editor.enter", inEditingVehicle?.Id,
                EventPatches.Payload(("newVehicle", ScriptValue.From(newVehicle))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(EditorEnterPatch), ex);
        }
    }
}

/// <summary>Patch for <c>editor.exit</c>: <c>VehicleEditor.Dispose</c> prefix.</summary>
[HarmonyPatch(typeof(VehicleEditor), nameof(VehicleEditor.Dispose))]
internal static class EditorExitPatch
{
    private static WeakReference<VehicleEditor>? _last;
    private static bool _faulted;

    [KsaAnchor("VehicleEditor.Dispose() instance prefix; VehicleEditor.ExistingVehicle?.Id", SourceFile = "KSA/VehicleEditor.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "VehicleEditor.cs:2256. Dispose runs the launch/exit finalisation, so the prefix sees the editor still intact. "
            + "A repeated Dispose of the same editor instance publishes once. vesselId is the edited vehicle "
            + "(ExistingVehicle) when there is one.")]
    private static void Prefix(VehicleEditor __instance)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            if (_last is not null && _last.TryGetTarget(out var previous) && ReferenceEquals(previous, __instance))
                return;
            _last = new WeakReference<VehicleEditor>(__instance);
            EventPatches.Emit("editor.exit", __instance.ExistingVehicle?.Id, EventPatches.Payload());
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(EditorExitPatch), ex);
        }
    }
}

/// <summary>Patch for <c>save.write</c>: <c>UncompressedSave.Write</c> postfix.</summary>
[HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Write))]
internal static class SaveWritePatch
{
    private static bool _faulted;

    [KsaAnchor("UncompressedSave.Write() → bool instance postfix; UncompressedSave.Directory (DirectoryInfo)",
        SourceFile = "KSA/UncompressedSave.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Medium,
        Notes = "UncompressedSave.cs:108 (override of GameSave.Write). Published only when the write succeeded; "
            + "payload.directory is the save folder. The script event is published first (handlers may update script "
            + "state), then KsaSaveHooks.SaveWritten lets the GameMod persist the state sidecar into the folder.")]
    private static void Postfix(UncompressedSave __instance, bool __result)
    {
        if (!__result)
            return;

        string? directory = null;
        try
        {
            directory = __instance.Directory.FullName;
            if (!_faulted && EventPatches.Sink is not null)
                EventPatches.Emit("save.write", null, EventPatches.Payload(("directory", ScriptValue.From(directory))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(SaveWritePatch), ex);
        }

        if (directory is not null)
            KsaSaveHooks.RaiseSaveWritten(directory);
    }
}

/// <summary>
///     Captures the directory of the save being loaded for <see cref="SaveLoadPatch"/>:
///     <c>UncompressedSave.Load</c> prefix, cleared by a finalizer.
/// </summary>
[HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Load))]
internal static class SaveLoadDirectoryPatch
{
    /// <summary>The directory of the save currently inside <c>UncompressedSave.Load()</c>; null otherwise.</summary>
    internal static string? Loading { get; private set; }

    [KsaAnchor("UncompressedSave.Load() instance prefix; UncompressedSave.Directory (DirectoryInfo)",
        SourceFile = "KSA/UncompressedSave.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Medium,
        Notes = "UncompressedSave.cs:45. Load() is the only caller of Universe.DeserializeSave and Program.OnGameLoaded, "
            + "and the game keeps no 'current save' field, so this prefix is the only place the loaded directory is "
            + "known. A finalizer clears it again, also when Load() throws.")]
    private static void Prefix(UncompressedSave __instance)
    {
        try
        {
            Loading = __instance.Directory.FullName;
        }
        catch
        {
            Loading = null;
        }
    }

    [KsaAnchor("UncompressedSave.Load() instance finalizer", SourceFile = "KSA/UncompressedSave.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "Harmony finalizer: runs after Load() returns or throws, after Program.OnGameLoaded's postfix.")]
    private static void Finalizer() => Loading = null;
}

/// <summary>Patch for <c>save.load</c>: <c>Program.OnGameLoaded</c> postfix.</summary>
[HarmonyPatch(typeof(Program), nameof(Program.OnGameLoaded))]
internal static class SaveLoadPatch
{
    private static bool _faulted;

    [KsaAnchor("Program.OnGameLoaded() static postfix", SourceFile = "KSA/Program.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "Program.cs:2599; called once by UncompressedSave.Load() after Universe.DeserializeSave. KsaSaveHooks.GameLoaded "
            + "is raised first (the GameMod restores the script state sidecar), then the save.load script event is "
            + "published, so handlers see the restored state. payload.directory comes from SaveLoadDirectoryPatch "
            + "(null if that patch is dark).")]
    private static void Postfix()
    {
        var directory = SaveLoadDirectoryPatch.Loading;
        KsaSaveHooks.RaiseGameLoaded(directory);

        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            EventPatches.Emit("save.load", null, EventPatches.Payload(("directory", ScriptValue.From(directory))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(SaveLoadPatch), ex);
        }
    }
}

/// <summary>Patch for <c>system.load</c>: <c>Universe.LoadSystem</c> postfix.</summary>
[HarmonyPatch(typeof(Universe), nameof(Universe.LoadSystem))]
internal static class SystemLoadPatch
{
    private static bool _faulted;

    [KsaAnchor("Universe.LoadSystem(string id) static postfix", SourceFile = "KSA/Universe.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "Universe.cs:232. Builds a new CelestialSystem and runs Universe.OnLoaded(); payload.system is the system "
            + "template id passed in. Not raised for a save load (the system was already loaded).")]
    private static void Postfix(string id)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            EventPatches.Emit("system.load", null, EventPatches.Payload(("system", ScriptValue.From(id))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(SystemLoadPatch), ex);
        }
    }
}

/// <summary>Patch for <c>warp.change</c>: <c>Universe.SetSimulationSpeed(double, bool)</c> prefix, diffed.</summary>
[HarmonyPatch(typeof(Universe), nameof(Universe.SetSimulationSpeed), typeof(double), typeof(bool))]
internal static class WarpChangePatch
{
    /// <summary>The game's own "changed" threshold in <c>SetSimulationSpeed</c> (it only alerts above this).</summary>
    private const double Epsilon = 0.001;

    private static bool _faulted;

    [KsaAnchor("Universe.SetSimulationSpeed(double simulationSpeed, bool alert = true) static prefix; "
            + "Universe.GetSimulationSpeed()",
        SourceFile = "KSA/Universe.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "Universe.cs:2288, the single funnel for the warp factor (the private SimulationSpeed setter, auto-warp, menu "
            + "pause and the console all end here, often every frame with the same value). Published only when the "
            + "value moves by more than 0.001 (the game's own threshold). payload.from / payload.to are the old and "
            + "new warp factors. The SetSimulationSpeed(SimSpeed) overload forwards to this one.")]
    private static void Prefix(double simulationSpeed)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            var from = Universe.GetSimulationSpeed();
            if (Math.Abs(from - simulationSpeed) <= Epsilon)
                return;
            EventPatches.Emit("warp.change", null, EventPatches.Payload(
                ("from", ScriptValue.From(Sanitize.Finite(from))),
                ("to", ScriptValue.From(Sanitize.Finite(simulationSpeed)))));
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(WarpChangePatch), ex);
        }
    }
}

/// <summary>Patch for <c>burn.complete</c> / <c>burn.fuel_out</c>: <c>FlightComputer.RaisePendingAlerts</c> prefix.</summary>
[HarmonyPatch(typeof(FlightComputer), nameof(FlightComputer.RaisePendingAlerts))]
internal static class BurnAlertsPatch
{
    private static bool _faulted;

    [KsaAnchor("FlightComputer.RaisePendingAlerts(Vehicle vehicle) instance prefix; "
            + "FlightComputer.{AutoBurnCompleted, AutoBurnStoppedOutOfPropellant} (public bool fields)",
        SourceFile = "KSA/FlightComputer.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Medium,
        Notes = "FlightComputer.cs:542. Reads the two pending flags before RaisePendingAlerts clears them. Called only for "
            + "vehicles in frame (Program.PrepareFrame), so an off-screen vehicle's burn end is not seen. "
            + "Neither event carries a payload.")]
    private static void Prefix(FlightComputer __instance, Vehicle vehicle)
    {
        if (_faulted || EventPatches.Sink is null)
            return;
        try
        {
            if (__instance.AutoBurnCompleted)
                EventPatches.Emit("burn.complete", vehicle.Id, EventPatches.Payload());
            if (__instance.AutoBurnStoppedOutOfPropellant)
                EventPatches.Emit("burn.fuel_out", vehicle.Id, EventPatches.Payload());
        }
        catch (Exception ex)
        {
            _faulted = true;
            EventPatches.Fault(nameof(BurnAlertsPatch), ex);
        }
    }
}
