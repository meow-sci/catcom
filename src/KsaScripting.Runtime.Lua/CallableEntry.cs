namespace KsaScripting.Runtime.Lua;

/// <summary>A Lua function anchored in the registry on behalf of a <c>ScriptCallable</c>.</summary>
/// <param name="Token">The callable token.</param>
/// <param name="Ref">The registry reference holding the function.</param>
/// <param name="OwnerToken">The owning module's token (0 when none).</param>
internal sealed record CallableEntry(long Token, int Ref, long OwnerToken);
