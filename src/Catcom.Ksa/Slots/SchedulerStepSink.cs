using Catcom.Core.Scheduling;

namespace Catcom.Ksa.Slots;

/// <summary>
///     The production <see cref="IStepSink"/>: builds a <see cref="StepInfo"/> and runs the Core
///     <see cref="Scheduler"/>'s Step slot. <see cref="Scheduler.StepSlot"/> contracts not to throw; if it
///     does, <see cref="SlotHost"/> counts the fault and disables itself after three in a row.
/// </summary>
/// <param name="scheduler">The scheduler whose Step slot runs.</param>
public sealed class SchedulerStepSink(Scheduler scheduler) : IStepSink
{
    /// <inheritdoc />
    public void StepSlot(double ut, double dt, long stepIndex, ulong frame, bool stepLanded, double playerDt)
        => scheduler.StepSlot(new StepInfo(ut, dt, stepIndex, frame, stepLanded, playerDt));
}
