namespace KsaScripting.Runtime.V8;

/// <summary>A module file served by the loader: its absolute path, original source and (for transpiled files) source map.</summary>
/// <param name="FullPath">The absolute path.</param>
/// <param name="Source">The original source text.</param>
/// <param name="Map">The source map from generated to original positions, or <c>null</c> for plain JavaScript.</param>
internal sealed record LoadedFile(string FullPath, string Source, SourceMap? Map);
