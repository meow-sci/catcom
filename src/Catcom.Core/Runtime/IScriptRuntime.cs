using Catcom.Core.Hosting;
using Catcom.Core.Values;

namespace Catcom.Core.Runtime;

/// <summary>
///     A script engine adapter (LuaJIT or V8). All members run on the game thread unless stated
///     otherwise. Implementations live in <c>Catcom.Runtime.Lua</c> and <c>Catcom.Runtime.V8</c>
///     and reference only <c>Catcom.Core</c>.
/// </summary>
/// <remarks>
///     <para>
///         Rules every runtime implements: <see cref="Invoke"/> of an async function returns immediately with
///         <see cref="ScriptValue.Null"/> (the continuation is scheduled and later resumed by
///         <see cref="Advance"/>); host waits (<see cref="IHostFacade.Wait"/>) are turned into the runtime's
///         native await; exceptions inside scripts never escape as anything but
///         <see cref="ScriptRuntimeException"/>; <see cref="Interrupt"/> makes the current call throw
///         <c>ScriptRuntimeException("interrupted")</c>.
///     </para>
/// </remarks>
public interface IScriptRuntime : IDisposable
{
    /// <summary>The runtime id: <c>"lua"</c> or <c>"v8"</c>.</summary>
    string Id { get; }

    /// <summary>The language it runs: <see cref="ScriptLanguages.Lua"/> or <see cref="ScriptLanguages.TypeScript"/>.</summary>
    string Language { get; }

    /// <summary>False when the native library failed to load (the reason is in <see cref="UnavailableReason"/>).</summary>
    bool IsAvailable { get; }

    /// <summary>Why the runtime is unavailable, or <c>null</c> when it is available.</summary>
    string? UnavailableReason { get; }

    /// <summary>Binds the runtime to the host and applies the options. Game thread; idempotent.</summary>
    /// <param name="host">The host facade the script API binds to.</param>
    /// <param name="options">Per-runtime settings.</param>
    void Initialize(IHostFacade host, RuntimeOptions options);

    /// <summary>Compiles/transpiles the source and evaluates its top level.</summary>
    /// <param name="source">The script source.</param>
    /// <returns>An opaque module handle.</returns>
    /// <exception cref="ScriptLoadException">The script failed to compile or its top level threw.</exception>
    ScriptModule Load(ScriptSource source);

    /// <summary>Lists the module's exported function names.</summary>
    /// <param name="module">The loaded module.</param>
    /// <returns>The export names.</returns>
    IReadOnlyList<string> Exports(ScriptModule module);

    /// <summary>Gets an exported function as an opaque callable.</summary>
    /// <param name="module">The loaded module.</param>
    /// <param name="name">The export name.</param>
    /// <returns>The callable.</returns>
    /// <exception cref="KeyNotFoundException">The module has no such export.</exception>
    ScriptCallable GetExport(ScriptModule module, string name);

    /// <summary>Calls a function synchronously on the game thread.</summary>
    /// <param name="fn">The function, owned by this runtime.</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The function's result (<see cref="ScriptValue.Null"/> for an async function).</returns>
    /// <exception cref="ScriptRuntimeException">The script raised an error or the call was interrupted.</exception>
    ScriptValue Invoke(ScriptCallable fn, ReadOnlySpan<ScriptValue> args);

    /// <summary>Runs pending continuations (promise jobs, coroutine resumes) for at most <paramref name="budget"/>.</summary>
    /// <param name="budget">The time budget.</param>
    /// <returns><c>true</c> if work remains.</returns>
    bool Advance(TimeSpan budget);

    /// <summary>Releases everything owned by the module.</summary>
    /// <param name="module">The module to unload.</param>
    void Unload(ScriptModule module);

    /// <summary>Aborts the current <see cref="Invoke"/>/<see cref="Advance"/>. May be called from any thread.</summary>
    void Interrupt();

    /// <summary>Live statistics.</summary>
    RuntimeStats Stats { get; }
}
