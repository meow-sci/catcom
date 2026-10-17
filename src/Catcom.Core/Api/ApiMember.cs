namespace Catcom.Core.Api;

/// <summary>
///     One member of an <see cref="ApiClass"/>: a property, a method or an event. The script
///     preludes, the TypeScript declarations and the LuaCATS stubs are all produced from (or checked
///     against) these records.
/// </summary>
/// <param name="Name">The TypeScript (camelCase) member name; the Lua name is its snake_case form (see <see cref="ApiNames"/>).</param>
/// <param name="Kind">Property, method or event.</param>
/// <param name="Type">The property type, or the method's result type (without <c>Promise</c>; see <paramref name="Async"/>).</param>
/// <param name="Params">The method parameters (or an event's payload fields); empty for properties.</param>
/// <param name="Doc">The player-facing documentation shown in editor hovers.</param>
/// <param name="ActionKey">The <c>SimActions</c> key a method performs, or <c>null</c>.</param>
/// <param name="Phase">The game-thread phase of <paramref name="ActionKey"/> (<c>"Frame"</c> or <c>"Solver"</c>), or <c>null</c>.</param>
/// <param name="Async">Whether the method waits (TypeScript returns a <c>Promise</c>; Lua yields the coroutine).</param>
/// <param name="ReadPath">The host read path of a property (a key under its handle), or <c>null</c>.</param>
/// <param name="Languages"><c>null</c> for both languages, or <c>"ts"</c> / <c>"lua"</c> for a member that exists in one language only.</param>
public sealed record ApiMember(
    string Name,
    ApiKind Kind,
    ApiType Type,
    IReadOnlyList<ApiParam> Params,
    string Doc,
    string? ActionKey,
    string? Phase,
    bool Async,
    string? ReadPath,
    string? Languages = null);
