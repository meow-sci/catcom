namespace KsaScripting.Runtime.V8;

/// <summary>One diagnostic reported while transpiling a TypeScript file.</summary>
/// <param name="Message">The flattened diagnostic message.</param>
/// <param name="Line">The 1-based line, or 0 when unknown.</param>
/// <param name="Column">The 1-based column, or 0 when unknown.</param>
/// <param name="IsError">Whether the diagnostic has the Error category (anything else is a warning/suggestion).</param>
internal readonly record struct TranspileDiagnostic(string Message, int Line, int Column, bool IsError);
