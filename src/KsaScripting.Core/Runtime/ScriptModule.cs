namespace KsaScripting.Core.Runtime;

/// <summary>An opaque handle to a loaded script, issued by a runtime's <c>Load</c>.</summary>
/// <param name="RuntimeId">The owning runtime's id.</param>
/// <param name="Name">The script name.</param>
/// <param name="Token">The runtime-private module token.</param>
public sealed record ScriptModule(string RuntimeId, string Name, long Token);
