namespace KsaScripting.Core.Api;

/// <summary>One parameter of a method, or one payload field of an event.</summary>
/// <param name="Name">The TypeScript (camelCase) name; a name starting with <c>...</c> is variadic (TS rest parameter, Lua <c>...</c>).</param>
/// <param name="Type">The parameter type.</param>
/// <param name="Optional">Whether the parameter may be omitted.</param>
/// <param name="Doc">A one-line description shown in editor hovers.</param>
public sealed record ApiParam(string Name, ApiType Type, bool Optional, string Doc);
