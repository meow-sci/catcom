namespace Catcom.Runtime.V8;

/// <summary>A JavaScript stack trace rewritten to point at the original TypeScript sources.</summary>
/// <param name="Traceback">The rewritten traceback text (one frame per line).</param>
/// <param name="File">The file of the top frame inside a served script file, or <c>null</c>.</param>
/// <param name="Line">The 1-based original line of that frame, or <c>null</c>.</param>
/// <param name="Column">The 1-based original column of that frame, or <c>null</c>.</param>
internal sealed record MappedStack(string Traceback, string? File, int? Line, int? Column);
