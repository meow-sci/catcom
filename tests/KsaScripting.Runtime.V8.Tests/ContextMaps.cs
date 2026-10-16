using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.V8.Tests;

/// <summary>Builds the <c>"$ctx"</c> maps the scheduler passes as the single argument of hooks, services and <c>main</c>.</summary>
internal static class ContextMaps
{
    /// <summary>A context of the given kind for a script, plus any extra keys (camelCase, as the host produces them).</summary>
    /// <param name="kind"><c>event</c>, <c>tick</c>, <c>service</c>, <c>main</c> or <c>terminal</c>.</param>
    /// <param name="source">The script the context belongs to.</param>
    /// <param name="extra">Additional context keys.</param>
    /// <returns>The context map.</returns>
    public static ScriptValue Create(string kind, ScriptSource source, params (string Key, ScriptValue Value)[] extra)
    {
        var map = new Dictionary<string, ScriptValue>
        {
            ["$ctx"] = ScriptValue.True,
            ["kind"] = ScriptValue.From(kind),
            ["script"] = Map(("name", ScriptValue.From(source.Name)), ("path", ScriptValue.From(source.Path))),
        };
        foreach (var (key, value) in extra)
            map[key] = value;
        return ScriptValue.Map(map);
    }

    /// <summary>A plain map value.</summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The map.</returns>
    public static ScriptValue Map(params (string Key, ScriptValue Value)[] entries)
        => ScriptValue.Map(entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));

    /// <summary>The <c>event</c> member of an event context: <c>{ut, type, vesselId, payload}</c>.</summary>
    /// <param name="type">The event type.</param>
    /// <param name="vesselId">The vessel id, if any.</param>
    /// <param name="payload">The payload map.</param>
    /// <returns>The event map.</returns>
    public static ScriptValue Event(string type, string? vesselId, ScriptValue payload)
        => Map(("ut", ScriptValue.From(1d)), ("type", ScriptValue.From(type)), ("vesselId", ScriptValue.From(vesselId)), ("payload", payload));
}
