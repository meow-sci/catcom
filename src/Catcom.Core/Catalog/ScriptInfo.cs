namespace Catcom.Core.Catalog;

/// <summary>A snapshot of one script as the catalog knows it.</summary>
/// <param name="Name">The catalog name: the path relative to the scripts folder with <c>/</c> separators.</param>
/// <param name="Path">The full file path.</param>
/// <param name="Language">The script language (<c>lua</c> or <c>typescript</c>).</param>
/// <param name="State">The lifecycle state.</param>
/// <param name="LastError">The last load, start or runtime problem, or <see langword="null"/>.</param>
/// <param name="Exports">The exported function names of the loaded module (empty when not loaded).</param>
/// <param name="HasTerminal">Whether the header declares <c>terminal=true</c>.</param>
/// <param name="Autostart">Whether the header declares <c>autostart=true</c>.</param>
/// <param name="Title">The display title from the header's <c>name=</c>, or <see langword="null"/>.</param>
public sealed record ScriptInfo(
    string Name,
    string Path,
    string Language,
    ScriptState State,
    string? LastError,
    IReadOnlyList<string> Exports,
    bool HasTerminal,
    bool Autostart,
    string? Title = null);
