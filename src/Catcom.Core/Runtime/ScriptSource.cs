namespace Catcom.Core.Runtime;

/// <summary>A script's source text handed to a runtime for compilation.</summary>
/// <param name="Name">The script name (catalog key, e.g. <c>"ascent"</c> or <c>"lib/tui"</c>).</param>
/// <param name="Path">The file path the source came from (used in error locations).</param>
/// <param name="Code">The source text.</param>
/// <param name="Language"><see cref="ScriptLanguages.Lua"/> or <see cref="ScriptLanguages.TypeScript"/>.</param>
public sealed record ScriptSource(string Name, string Path, string Code, string Language);
