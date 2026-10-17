namespace Catcom.Core.Api;

/// <summary>A member or parameter type spelled once per target language.</summary>
/// <param name="TsType">The TypeScript spelling, as written inside <c>declare namespace ksa</c> (e.g. <c>VesselRef | undefined</c>).</param>
/// <param name="LuaType">The LuaCATS spelling (e.g. <c>ksa.VesselRef?</c>).</param>
public sealed record ApiType(string TsType, string LuaType);
