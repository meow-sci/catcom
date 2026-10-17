namespace Catcom.Runtime.V8;

/// <summary>The output of transpiling one file to ES2022 modules.</summary>
/// <param name="Code">The generated JavaScript (with a trailing inline source-map comment when a map exists).</param>
/// <param name="SourceMapJson">The decoded source-map JSON, or <c>null</c>.</param>
/// <param name="Diagnostics">The diagnostics reported by the transpiler.</param>
internal sealed record TranspileResult(string Code, string? SourceMapJson, IReadOnlyList<TranspileDiagnostic> Diagnostics);
