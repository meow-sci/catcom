// Replaces gatOS gatOS.GameMod/Game/Ksa/KsaCatalog.cs (MIT): same structure, cut down to the v1 vocabulary.
using KsaScripting.Core.Commands;
using KsaScripting.Ksa.Actuators;
using KsaScripting.Ksa.Render;
using KSA;

namespace KsaScripting.Ksa.Commands;

/// <summary>
///     The command executor: routes a game-free <see cref="SimCommand"/> to the matching actuator,
///     resolving the target vehicle against live game state. This is the only place actuator code is
///     reached, so it owns the cross-cutting concerns: authority gating (<c>control_all_vessels</c>),
///     the <c>debug_namespace</c> switch, and per-action health latches that turn a thrown KSA call into
///     a degraded accessor (<see cref="CommandOutcome.Unsupported"/>) instead of a crash. Always invoked on
///     the game thread by <see cref="Core.Scheduling.GameThreadQueue.Drain"/> (or directly by
///     <see cref="Bridge.KsaBridge.Execute"/>); never throws — faults are returned.
/// </summary>
/// <remarks>
///     The v1 vocabulary is the vessel / module / <c>debug.*</c> core plus <c>camera.focus</c>. The
///     gatOS families that do not exist in ksa-scripting (audio, schedule, paint, the camera director,
///     thug-life, face FX, IVA physics, welds, the FX editors) answer
///     <see cref="CommandOutcome.Unsupported"/> before any vehicle is resolved.
/// </remarks>
/// <param name="health">Accessor-health latches (shared with the readers' owner).</param>
/// <param name="allVessels">
///     <c>control_all_vessels</c>: false restricts non-debug actions to the controlled vehicle.
/// </param>
/// <param name="debugNamespace">
///     <c>debug_namespace</c>: false denies every <c>debug.*</c> action.
/// </param>
public sealed class ActionExecutor(KsaHealth health, bool allVessels, bool debugNamespace) : ICommandExecutor
{
    /// <summary>The message returned for the gatOS families dropped from v1.</summary>
    internal const string NotInV1 = "not available in ksa-scripting v1";

    /// <summary>
    ///     The first-class per-vessel controls exempt from the controlled-vessel authority gate: each is a
    ///     deliberate by-id operation on an arbitrary vessel. A GameMod authority policy — Core never
    ///     sees it.
    /// </summary>
    private static readonly HashSet<string> AnyVesselActions =
        new(StringComparer.Ordinal) { SimActions.VesselScale, SimActions.VesselAlwaysRender };

    /// <inheritdoc />
    [KsaAnchor("Program.ControlledVehicle?.Id; Vehicle.Id", SourceFile = "KSA/Program.cs / KSA/Vehicle.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "The authority gate (control_all_vessels=false): only the controlled vehicle is commandable. "
            + "debug.* and AnyVesselActions are exempt. Program.ControlledVehicle is a static property (:502); "
            + "its setter now ClearHeldPlayerInput()s the previous vehicle on change.")]
    public CommandResult Execute(SimCommand command)
    {
        var accessor = $"actuator.{command.Action}";
        try
        {
            // The v1 cut: families that only exist in gatOS answer before anything else is resolved.
            if (IsRemovedFamily(command.Action))
                return CommandResult.Unsupported(NotInV1);

            var isDebug = command.Action.StartsWith("debug.", StringComparison.Ordinal);
            if (isDebug && !debugNamespace)
                return CommandResult.Denied("debug namespace disabled in ksa-scripting.toml");

            if (health.IsDegraded(accessor))
                return CommandResult.Unsupported($"'{command.Action}' is latched degraded");

            // Vessel-agnostic debug action (no target vehicle to resolve).
            if (command.Action == SimActions.DebugWarp)
                return Finish(accessor, DebugActuator.SetWarp(command.Value));

            // camera.focus names any Astronomical (vehicle or celestial) by id and only moves the view.
            if (command.Action == SimActions.CameraFocus)
                return Finish(accessor, Focus(command));

            // control_vessel targets the vehicle named by the token (the one to take control of), not the
            // (sender) VesselId.
            var targetId = command.Action == SimActions.DebugControlVessel
                ? command.Token ?? command.VesselId
                : command.VesselId;
            var vehicle = ResolveVehicle(targetId);
            if (vehicle is null)
                return CommandResult.NotFound($"vessel '{targetId}' is gone");

            // Authority gate: with all_vessels=false only the controlled vehicle is commandable. The cheat
            // namespace is exempt (its own opt-in) and so is AnyVesselActions.
            var anyVessel = isDebug || AnyVesselActions.Contains(command.Action);
            if (!anyVessel && !allVessels && Program.ControlledVehicle?.Id != vehicle.Id)
                return CommandResult.Denied("control is restricted to the active vessel");

            return Finish(accessor, Dispatch(vehicle, command));
        }
        catch (Exception ex)
        {
            health.Fault(accessor, SafeUt(), ex.Message);
            return CommandResult.Fault(ex.Message);
        }
    }

    /// <summary>
    ///     Whether <paramref name="action"/> belongs to a gatOS family that ksa-scripting v1 does not carry:
    ///     <c>audio.*</c>, <c>schedule.*</c>, <c>paint.*</c>, <c>camera.*</c> other than
    ///     <c>camera.focus</c>, and the <c>debug.thug_life*</c>, <c>debug.fx_*</c>, <c>debug.iva_*</c>,
    ///     <c>debug.always_render_iva</c>, <c>debug.weld*</c>, <c>debug.engineplume*</c>,
    ///     <c>debug.plumetrail*</c>, <c>debug.clouds*</c> and <c>debug.terrain*</c> actions.
    /// </summary>
    /// <param name="action">The action key.</param>
    /// <returns><see langword="true"/> when the action is not available in v1.</returns>
    internal static bool IsRemovedFamily(string action)
    {
        if (action.StartsWith("audio.", StringComparison.Ordinal)
            || action.StartsWith("schedule.", StringComparison.Ordinal)
            || action.StartsWith("paint.", StringComparison.Ordinal))
            return true;

        if (action.StartsWith("camera.", StringComparison.Ordinal))
            return action != SimActions.CameraFocus;

        if (!action.StartsWith("debug.", StringComparison.Ordinal))
            return false;

        return action.StartsWith("debug.thug_life", StringComparison.Ordinal)
               || action.StartsWith("debug.fx_", StringComparison.Ordinal)
               || action.StartsWith("debug.iva_", StringComparison.Ordinal)
               || action == "debug.always_render_iva"
               || action.StartsWith("debug.weld", StringComparison.Ordinal)
               || action.StartsWith("debug.engineplume", StringComparison.Ordinal)
               || action.StartsWith("debug.plumetrail", StringComparison.Ordinal)
               || action.StartsWith("debug.clouds", StringComparison.Ordinal)
               || action.StartsWith("debug.terrain", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Removes the Harmony patches the executor installs lazily (<c>vessel.always_render</c> patches the
    ///     render path on the first marked vessel) and clears every mark. Call once on mod unload, after
    ///     the command queue stopped draining. Never throws.
    /// </summary>
    public static void Teardown()
    {
        try
        {
            VesselForceRender.Teardown();
        }
        catch (Exception ex)
        {
            Core.Logging.Log.Warn($"executor teardown failed: {ex.Message}");
        }
    }

    private CommandResult Finish(string accessor, CommandResult result)
    {
        if (result.IsSuccess)
            health.Clear(accessor);
        return result;
    }

    [KsaAnchor("Vehicle (parameter) → EngineActuator / ThrottleActuator / StagingActuator / RcsActuator / "
            + "TranslateActuator / RotateActuator / FlightComputerActuator / LightActuator / AnimationActuator / "
            + "DecouplerActuator / DockingActuator / ScaleActuator / DebugActuator / VesselForceRender",
        SourceFile = "KSA/Vehicle.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "Pure routing table over the individually anchored actuators; it touches the KSA Vehicle type only "
            + "to pass it through.")]
    private CommandResult Dispatch(Vehicle vehicle, SimCommand c) => c.Action switch
    {
        // Engines / vessel-level
        SimActions.VesselIgnite => EngineActuator.Ignite(vehicle),
        SimActions.VesselShutdown => EngineActuator.Shutdown(vehicle),
        SimActions.VesselEngine => EngineActuator.SetEngineOn(vehicle, c.Value > 0.5),
        SimActions.EngineActive => EngineActuator.SetActive(vehicle, c.Ordinal, c.Value > 0.5),
        SimActions.EngineMinThrottle => EngineActuator.SetMinThrottle(vehicle, c.Ordinal, c.Value),
        SimActions.VesselLights => LightActuator.SetMaster(vehicle, c.Value > 0.5),
        SimActions.AnimationGoal => AnimationActuator.SetGoal(vehicle, c.Ordinal, c.Value),

        // Vessel control surface
        SimActions.VesselThrottle => ThrottleActuator.Set(vehicle, c.Value),
        SimActions.VesselStage => StagingActuator.Stage(vehicle),
        SimActions.VesselRcs => RcsActuator.SetMaster(vehicle, c.Value > 0.5),
        // Manual RCS translation (body-axis signs; latches until rewritten).
        SimActions.VesselTranslate => TranslateActuator.SetTranslation(vehicle, c.Values ?? []),
        // Manual RCS rotation (body-axis torque signs; latches until rewritten; full authority only in
        // manual attitude mode — auto strips the rotation bits).
        SimActions.VesselRotate => RotateActuator.SetRotation(vehicle, c.Values ?? []),
        SimActions.VesselAttitudeMode => FlightComputerActuator.SetAttitudeMode(vehicle, c.Token ?? ""),
        // The FC's RCS master switch (in-game R). Disabled zeroes the manual thruster flags, so
        // translate + rotate go dead until it is re-enabled.
        SimActions.VesselRcsMode => FlightComputerActuator.SetRcsMode(vehicle, c.Token ?? ""),
        SimActions.VesselAttitudeFrame => FlightComputerActuator.SetAttitudeFrame(vehicle, c.Token ?? ""),
        SimActions.VesselAttitudeTarget => FlightComputerActuator.SetAttitudeTarget(vehicle, c.Values ?? []),
        SimActions.VesselBurn => FlightComputerActuator.SetBurn(vehicle, c.Values ?? []),

        // First-class per-vessel nodes (any-vessel — see AnyVesselActions).
        SimActions.VesselScale => ScaleActuator.Set(vehicle, c.Value),
        SimActions.VesselAlwaysRender => VesselForceRender.Set(vehicle, c.Value > 0.5),

        // Per-module
        SimActions.RcsActive => RcsActuator.SetActive(vehicle, c.Ordinal, c.Value > 0.5),
        SimActions.LightOn => LightActuator.SetOn(vehicle, c.Ordinal, c.Value > 0.5),
        SimActions.LightBrightness => LightActuator.SetBrightness(vehicle, c.Ordinal, c.Value),
        SimActions.LightColor => LightActuator.SetColor(vehicle, c.Ordinal, c.Values ?? []),
        SimActions.LightOuterAngle => LightActuator.SetOuterAngle(vehicle, c.Ordinal, c.Value),
        SimActions.LightInnerAngle => LightActuator.SetInnerAngle(vehicle, c.Ordinal, c.Value),
        SimActions.DecouplerFire => DecouplerActuator.Fire(vehicle, c.Ordinal),
        SimActions.DockingUndock => DockingActuator.Undock(vehicle, c.Ordinal),

        // Cheat namespace
        SimActions.DebugControlVessel => DebugActuator.ControlVessel(vehicle),
        SimActions.DebugTeleport => DebugActuator.Teleport(vehicle, c.Values ?? []),
        // One-shot impulsive kick (frame keyword rides in Token, unit keyword in Aux).
        SimActions.DebugImpulse => DebugActuator.Impulse(vehicle, c.Values ?? [], c.Token, c.Aux),
        SimActions.DebugRefillFuel => DebugActuator.RefillFuel(vehicle),
        SimActions.DebugRefillBattery => DebugActuator.RefillBattery(vehicle),
        SimActions.DebugDockingPushoff => DockingActuator.SetPushoffImpulse(vehicle, c.Ordinal, c.Value),

        _ => CommandResult.Unsupported($"unknown action '{c.Action}'"),
    };

    /// <summary>
    ///     <c>camera.focus</c>: the id rides in <see cref="SimCommand.Token"/> (falling back to
    ///     <see cref="SimCommand.VesselId"/>) and names any <see cref="Astronomical"/>; only the view moves.
    /// </summary>
    [KsaAnchor("CameraActuator.Focus(Astronomical) via ResolveAstronomical(Token ?? VesselId)",
        SourceFile = "KSA/CelestialSystem.cs / KSA/Camera.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Low,
        Notes = "The only surviving camera route (the gatOS camera director is not part of v1). Routes before vehicle "
            + "resolution and before the authority gate, exactly as gatOS did.")]
    private static CommandResult Focus(SimCommand c)
    {
        var focusId = c.Token ?? c.VesselId;
        return ResolveAstronomical(focusId) is { } followable
            ? CameraActuator.Focus(followable)
            : CommandResult.NotFound($"'{focusId}' is gone");
    }

    /// <summary>Finds a vehicle of the current system by its id (null when absent or no system is loaded).</summary>
    /// <param name="id">The vehicle id.</param>
    /// <returns>The vehicle, or <see langword="null"/>.</returns>
    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Id", SourceFile = "KSA/Universe.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "Same enumeration the vessel cache uses to find vessels by id. Universe.CurrentSystem is a public "
            + "static property (:114); CelestialSystem.All is LookupCollection<Astronomical>. Fragment/debris "
            + "vehicles ('<id>_N') are ordinary Vehicles in All, so they resolve like any other id.")]
    internal static Vehicle? ResolveVehicle(string id)
    {
        if (Universe.CurrentSystem is not { } system)
            return null;
        foreach (var astronomical in system.All.UnsafeAsList())
            if (astronomical is Vehicle vehicle && vehicle.Id == id)
                return vehicle;
        return null;
    }

    /// <summary>Finds any astronomical (vehicle or celestial) of the current system by id.</summary>
    /// <param name="id">The object id.</param>
    /// <returns>The object, or <see langword="null"/>.</returns>
    [KsaAnchor("Universe.CurrentSystem.Get(id) → Astronomical (vehicle or celestial)",
        SourceFile = "KSA/CelestialSystem.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Low,
        Notes = "Same id lookup the game's follow/control terminal actions use (CelestialSystem.Get(string) :298, "
            + "a keyed LookupCollection read); returns null when absent.")]
    internal static Astronomical? ResolveAstronomical(string id)
        => Universe.CurrentSystem?.Get(id);

    [KsaAnchor("Universe.GetElapsedSeconds()", SourceFile = "KSA/Universe.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "UT stamp for a degraded-accessor record; never throws (0 on any failure).")]
    private static double SafeUt()
    {
        try
        {
            return Universe.GetElapsedSeconds();
        }
        catch
        {
            return 0;
        }
    }
}
