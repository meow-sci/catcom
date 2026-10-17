namespace Catcom.Core.Hooks;

/// <summary>One persisted UI binding (a <c>[[binding]]</c> table of <c>bindings.toml</c>).</summary>
/// <param name="Script">The script's catalog name.</param>
/// <param name="Export">The exported function to call.</param>
/// <param name="Kind">The kind text (<c>event</c>, <c>tick</c>, <c>service</c> or <c>command</c>); see <see cref="BindingKinds"/>.</param>
/// <param name="Key">The trigger key (event name, <c>frame</c>/<c>step</c>/<c>every</c>, command name).</param>
/// <param name="Sync">Whether an event binding runs inline in the Harmony patch.</param>
/// <param name="Vessel">The event vessel filter (<c>active</c>, a vessel id, <c>any</c>), or <see langword="null"/>.</param>
/// <param name="Clock">The tick clock: <c>ut</c>, <c>wall</c> or <c>render</c>.</param>
/// <param name="IntervalSeconds">The <c>every</c> interval in seconds.</param>
/// <param name="Enabled">Whether the binding is applied when the script loads.</param>
public sealed record BindingRecord(
    string Script,
    string Export,
    string Kind,
    string Key,
    bool Sync,
    string? Vessel,
    string Clock,
    double IntervalSeconds,
    bool Enabled);
