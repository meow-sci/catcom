namespace KsaScripting.Core.Hosting;

/// <summary>One entry of the event catalog.</summary>
/// <param name="Name">The event name, e.g. <c>"engine.activate"</c>.</param>
/// <param name="Description">A short description.</param>
/// <param name="PayloadFields">The payload field names.</param>
/// <param name="Producer"><c>"harmony"</c> (a game-code patch) or <c>"differ"</c> (derived by diffing snapshots).</param>
public sealed record EventDescriptor(string Name, string Description, IReadOnlyList<string> PayloadFields, string Producer);
