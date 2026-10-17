namespace Catcom.Core.Api;

/// <summary>One script-facing type (or the <c>ksa</c> module itself): its documentation and members.</summary>
/// <param name="Name">The class name, e.g. <c>"VesselRef"</c>; the module is <c>"ksa"</c>.</param>
/// <param name="Doc">The player-facing documentation shown in editor hovers.</param>
/// <param name="Members">The members, in documentation order.</param>
/// <param name="Extends">The base interface/class name (e.g. <c>"EngineInfo"</c>), or <c>null</c>.</param>
public sealed record ApiClass(string Name, string Doc, IReadOnlyList<ApiMember> Members, string? Extends = null);
