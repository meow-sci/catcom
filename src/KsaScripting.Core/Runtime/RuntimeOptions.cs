namespace KsaScripting.Core.Runtime;

/// <summary>Per-runtime settings passed to <see cref="IScriptRuntime.Initialize"/>.</summary>
/// <param name="ScriptsDir">The user's scripts directory (module resolution root).</param>
/// <param name="StubsDir">The directory holding generated API stubs (<c>.d.ts</c> / <c>.lua</c>).</param>
/// <param name="HeapLimitBytes">Soft heap limit in bytes (0 = unlimited).</param>
/// <param name="JitEnabled">Whether to leave the JIT on (LuaJIT: <c>jit.off()</c> when false).</param>
/// <param name="DebugPort">A debugger port, or <c>null</c> for none.</param>
public sealed record RuntimeOptions(string ScriptsDir, string StubsDir, long HeapLimitBytes, bool JitEnabled, int? DebugPort);
