namespace KsaScripting.Core.Runtime;

/// <summary>A runtime's live statistics, shown in the Script Manager.</summary>
/// <param name="HeapBytes">Current script heap size in bytes.</param>
/// <param name="LastCallMs">Duration of the most recent <c>Invoke</c>, in milliseconds.</param>
/// <param name="CallsTotal">Total number of invocations since the runtime started.</param>
/// <param name="JitEnabled">Whether the JIT is enabled (LuaJIT: <c>jit.on()</c>).</param>
/// <param name="Version">The engine version string.</param>
public sealed record RuntimeStats(long HeapBytes, double LastCallMs, long CallsTotal, bool JitEnabled, string Version);
