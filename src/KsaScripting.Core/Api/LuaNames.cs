namespace KsaScripting.Core.Api;

/// <summary>The Lua spelling of schema members (snake_case, with the few explicit overrides).</summary>
internal static class LuaNames
{
    /// <summary>TypeScript member names whose Lua name is not simply the snake_case form (`until` is reserved in Lua).</summary>
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.Ordinal)
    {
        ["until"] = "wait_until",
        ["waitEvent"] = "wait_event",
    };

    /// <summary>The Lua name of a member of the <c>ksa</c> module or of any class.</summary>
    internal static string Member(string tsName)
        => Overrides.TryGetValue(tsName, out var lua) ? lua : ApiNames.ToSnake(tsName);

    /// <summary>
    ///     Classes whose methods are called with dot syntax (<c>ksa.sleep(2)</c>, <c>ksa.console.log(...)</c>,
    ///     <c>ctx.term.write(s)</c>); every other class uses colon syntax (<c>v:set_throttle(0.5)</c>).
    /// </summary>
    internal static bool UsesDotSyntax(string className)
        => className is "ksa" or "Console" or "State" or "Terminal";
}
