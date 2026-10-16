namespace KsaScripting.Ksa.Slots;

/// <summary>
///     Receives the per-step slot call from <see cref="SlotHost"/>. Decouples the Harmony postfix from
///     the Core scheduler: the adapter <see cref="SchedulerStepSink"/> builds a <c>StepInfo</c> and calls
///     <c>Scheduler.StepSlot</c>. Called on the game thread; must not throw.
/// </summary>
public interface IStepSink
{
    /// <summary>Runs one Step slot.</summary>
    /// <param name="ut">Universal time, in seconds.</param>
    /// <param name="dt">The simulation step length in seconds (0 on a latch frame).</param>
    /// <param name="stepIndex">The simulation step counter (advances only when a step landed).</param>
    /// <param name="frame">The render frame number.</param>
    /// <param name="stepLanded">Whether a new simulation step landed since the previous slot.</param>
    /// <param name="playerDt">The wall-clock (player) frame time in seconds, for the render clock.</param>
    void StepSlot(double ut, double dt, long stepIndex, ulong frame, bool stepLanded, double playerDt);
}
