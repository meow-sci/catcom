using Catcom.Core.Config;

namespace Catcom.Core.Scheduling;

/// <summary>The scheduler's budgets and fault policy.</summary>
/// <param name="FrameBudget">Total script time per Step slot.</param>
/// <param name="SyncHookBudget">Watchdog budget for one sync hook call.</param>
/// <param name="CallBudget">Watchdog budget for every other invocation.</param>
/// <param name="FaultLimit">Consecutive faults after which a script is disabled.</param>
public sealed record ScheduleConfig(TimeSpan FrameBudget, TimeSpan SyncHookBudget, TimeSpan CallBudget, int FaultLimit)
{
    /// <summary>The documented defaults: 2 ms per slot, 5 ms per sync hook, 50 ms per call, 3 faults.</summary>
    public static ScheduleConfig Default { get; } =
        new(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(50), 3);

    /// <summary>Builds the schedule from <c>catcom.toml</c> values (call <see cref="ScriptingConfig.Normalize"/> first).</summary>
    /// <param name="cfg">The loaded configuration.</param>
    /// <returns>The matching schedule.</returns>
    public static ScheduleConfig FromConfig(ScriptingConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return new ScheduleConfig(
            TimeSpan.FromMilliseconds(cfg.FrameBudgetMs),
            TimeSpan.FromMilliseconds(cfg.SyncHookBudgetMs),
            TimeSpan.FromMilliseconds(cfg.CallBudgetMs),
            cfg.FaultLimit);
    }
}
