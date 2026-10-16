namespace KsaScripting.Core.Scheduling;

/// <summary>A snapshot of the scheduler's counters (the status window).</summary>
/// <param name="Slots">Step slots run.</param>
/// <param name="LastSlotMs">Duration of the last slot.</param>
/// <param name="MaxSlotMs">Longest slot so far.</param>
/// <param name="Invocations">Successful script invocations.</param>
/// <param name="Faults">Script errors reported.</param>
/// <param name="BudgetOverruns">Times work was deferred because the frame budget ran out.</param>
/// <param name="PendingWaits">Waits not yet resolved.</param>
/// <param name="PendingEvents">Events queued for async delivery.</param>
/// <param name="WatchdogTrips">Calls the watchdog interrupted.</param>
public sealed record ScheduleStats(
    long Slots,
    double LastSlotMs,
    double MaxSlotMs,
    long Invocations,
    long Faults,
    long BudgetOverruns,
    int PendingWaits,
    int PendingEvents,
    long WatchdogTrips);
