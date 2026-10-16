namespace KsaScripting.Core.Hosting;

/// <summary>Options of a hook subscription.</summary>
/// <param name="Sync">Run synchronously inside the Harmony patch under the sync budget (events only).</param>
/// <param name="Vessel">Restrict to one vessel id, or <c>null</c> for all.</param>
/// <param name="Clock">The clock for <c>every</c> ticks: <c>"ut"</c> | <c>"wall"</c> | <c>"render"</c>.</param>
/// <param name="IntervalSeconds">The interval for <c>every</c> ticks, in seconds.</param>
public sealed record BindingOptions(bool Sync = false, string? Vessel = null, string Clock = "ut", double IntervalSeconds = 0);
