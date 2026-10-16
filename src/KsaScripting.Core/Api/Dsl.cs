using KsaScripting.Core.Commands;

namespace KsaScripting.Core.Api;

/// <summary>Member constructors used by the schema definitions (internal builder vocabulary).</summary>
internal static class Dsl
{
    private static readonly ApiParam[] NoParams = [];

    /// <summary>A required parameter.</summary>
    internal static ApiParam P(string name, ApiType type, string doc) => new(name, type, false, doc);

    /// <summary>An optional parameter.</summary>
    internal static ApiParam O(string name, ApiType type, string doc) => new(name, type, true, doc);

    /// <summary>A readable property; <paramref name="readPath"/> is the host read key (null when computed in the prelude).</summary>
    internal static ApiMember Prop(string name, ApiType type, string doc, string? readPath = null, string? languages = null)
        => new(name, ApiKind.Property, type, NoParams, doc, null, null, false, readPath, languages);

    /// <summary>A property whose host read path equals its own name.</summary>
    internal static ApiMember View(string name, ApiType type, string doc) => Prop(name, type, doc, name);

    /// <summary>A plain synchronous method (no action key).</summary>
    internal static ApiMember Fn(string name, ApiType result, string doc, params ApiParam[] parameters)
        => new(name, ApiKind.Method, result, parameters, doc, null, null, false, null);

    /// <summary>A method that waits: TypeScript returns a Promise, Lua yields the running coroutine.</summary>
    internal static ApiMember Wait(string name, ApiType result, string doc, params ApiParam[] parameters)
        => new(name, ApiKind.Method, result, parameters, doc, null, null, true, null);

    /// <summary>A method that exists in the Lua API only.</summary>
    internal static ApiMember LuaFn(string name, ApiType result, string doc, params ApiParam[] parameters)
        => new(name, ApiKind.Method, result, parameters, doc, null, null, false, null, "lua");

    /// <summary>
    ///     A method that performs a <see cref="SimActions"/> key. The phase comes from
    ///     <see cref="SimCommand.PhaseFor"/>; Solver-phase actions are asynchronous, Frame-phase actions
    ///     return the <c>CommandResult</c> directly.
    /// </summary>
    internal static ApiMember Act(string name, string actionKey, string doc, params ApiParam[] parameters)
    {
        var phase = SimCommand.PhaseFor(actionKey);
        return new ApiMember(name, ApiKind.Method, Ty.Ref("CommandResult"), parameters, doc, actionKey, phase.ToString(),
            phase == CommandPhase.Solver, null);
    }

    /// <summary>An event with its payload fields (all of type <c>unknown</c>).</summary>
    internal static ApiMember Event(string name, string doc, IEnumerable<string> payloadFields)
        => new(name, ApiKind.Event, Ty.Void,
            payloadFields.Select(f => new ApiParam(f, Ty.Unknown, false, $"payload.{f}")).ToArray(),
            doc, null, null, false, null);
}
