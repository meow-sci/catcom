namespace KsaScripting.Core.Snapshots;

/// <summary>The game clock as scripts read it (<c>time</c> handle). Built by the game bridge each step.</summary>
/// <param name="Ut">Universal time, seconds.</param>
/// <param name="Warp">The current time-warp factor.</param>
/// <param name="SimDt">The duration of the last simulation step, seconds.</param>
/// <param name="Paused">Whether the simulation is paused.</param>
/// <param name="WarpSpeeds">The selectable warp factors.</param>
/// <param name="AutoWarp">Whether auto-warp is engaged.</param>
/// <param name="AutoWarpTargetUt">The auto-warp target UT, seconds (0 when none).</param>
/// <param name="ActiveVesselId">The controlled vessel's id, or <c>null</c>.</param>
/// <param name="Frame">The render frame number.</param>
/// <param name="StepIndex">The simulation step index.</param>
public sealed record TimeView(
    double Ut,
    double Warp,
    double SimDt,
    bool Paused,
    IReadOnlyList<double> WarpSpeeds,
    bool AutoWarp,
    double AutoWarpTargetUt,
    string? ActiveVesselId,
    ulong Frame,
    long StepIndex);
