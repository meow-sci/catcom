// Ported from gatOS (MIT) gatOS.GameMod/Game/Ksa/Actuators/CameraActuator.cs; behaviour unchanged.
// Trimmed to the camera.focus write action only; the other camera.* actions depend on the camera
// director, which is not part of this port.
using Catcom.Core.Commands;
using KSA;

namespace Catcom.Ksa.Actuators;

/// <summary>
///     The <c>camera.focus</c> write action of <c>/sim/camera</c> (ctl/focus and bodies/&lt;id&gt;/focus).
///     Game thread only (threading rule 1): this runs inside the Frame-phase command drain.
/// </summary>
internal static class CameraActuator
{
    /// <summary>
    ///     Camera focus (<c>ctl/focus</c> on a vessel, <c>bodies/&lt;id&gt;/focus</c> on a celestial):
    ///     point the main view at any <see cref="Astronomical"/> — vehicle or body — the way the game's
    ///     own <c>follow</c> terminal action does. Uses the deterministic <c>Program.MainViewport</c>
    ///     (not the mouse-hovered viewport, which is meaningless when the player is typing in the SSH
    ///     terminal) and <c>changeControl: false</c> so it only moves the camera — it never switches or
    ///     clears the controlled vessel (that is <c>debug.control_vessel</c>). A pure view op, so it is
    ///     exempt from the authority gate. Game-thread only.
    /// </summary>
    /// <remarks>
    ///     <b>Both of the viewport's cameras are set</b> (plan §11.1, task C1.4). A viewport carries a
    ///     <c>BaseCamera</c> and a <c>MapCamera</c>, and <c>GetCamera()</c> returns whichever the current
    ///     mode uses; the game's own follow action sets the target on <b>both</b>
    ///     (<c>KSA/InputEvents.cs:759-760</c>). Setting only the active one — which this actuator
    ///     originally did, through <c>Program.GetMainCamera()</c> — left the map view pointing at the
    ///     previous target until the player happened to re-focus from inside map mode.
    /// </remarks>
    [KsaAnchor("Program.MainViewport.{BaseCamera,MapCamera}; Camera.SetFollow(IFollowable, "
            + "tidalLocking:true, changeControl:false, alert:false)",
        SourceFile = "KSA/Program.cs / KSA/IGameViewport.cs / KSA/Camera.cs / KSA/InputEvents.cs",
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "Mirrors the game's `follow` terminal action; Astronomical (Vehicle and celestials) is "
            + "IFollowable. changeControl:false leaves Program.ControlledVehicle untouched; alert:false "
            + "keeps the 'Following X' TimedAlert off screen, which matters now that the same camera "
            + "surface is used to shoot footage."
            + "5402: Program.MainViewport is an IGameViewport (BaseCamera/MapCamera get-only properties); InputEvents' follow action is unchanged.")]
    internal static CommandResult Focus(Astronomical target)
    {
        var viewport = Program.MainViewport;
        viewport.MapCamera.SetFollow(target, tidalLocking: true, changeControl: false, alert: false);
        viewport.BaseCamera.SetFollow(target, tidalLocking: true, changeControl: false, alert: false);
        return CommandResult.Ok;
    }
}
