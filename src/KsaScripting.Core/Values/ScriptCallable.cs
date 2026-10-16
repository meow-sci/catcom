namespace KsaScripting.Core.Values;

/// <summary>
///     An opaque, runtime-owned function handle. Created by a script runtime when a script function
///     crosses into the host (an export, a hook callback, a predicate). Disposing it tells the owning
///     runtime to release its reference; it is idempotent and thread-safe.
/// </summary>
public sealed class ScriptCallable : IDisposable
{
    private readonly Action<ScriptCallable> _onDispose;
    private int _disposed;

    /// <summary>Creates a callable. Runtimes outside this assembly use <see cref="Create"/>.</summary>
    /// <param name="runtimeId">The owning runtime's id (<c>"lua"</c> | <c>"v8"</c>).</param>
    /// <param name="token">The runtime-private token identifying the function.</param>
    /// <param name="name">An optional diagnostic name.</param>
    /// <param name="onDispose">Invoked exactly once, the first time <see cref="Dispose"/> is called.</param>
    internal ScriptCallable(string runtimeId, long token, string? name, Action<ScriptCallable> onDispose)
    {
        ArgumentNullException.ThrowIfNull(runtimeId);
        ArgumentNullException.ThrowIfNull(onDispose);
        RuntimeId = runtimeId;
        Token = token;
        Name = name;
        _onDispose = onDispose;
    }

    /// <summary>The id of the runtime that owns the function.</summary>
    public string RuntimeId { get; }

    /// <summary>The runtime-private token identifying the function.</summary>
    public long Token { get; }

    /// <summary>An optional diagnostic name (export or function name).</summary>
    public string? Name { get; }

    /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Creates a callable (the public factory for runtime assemblies).</summary>
    /// <param name="runtimeId">The owning runtime's id (<c>"lua"</c> | <c>"v8"</c>).</param>
    /// <param name="token">The runtime-private token identifying the function.</param>
    /// <param name="name">An optional diagnostic name.</param>
    /// <param name="onDispose">Invoked exactly once, the first time <see cref="Dispose"/> is called.</param>
    /// <returns>The new callable.</returns>
    public static ScriptCallable Create(string runtimeId, long token, string? name, Action<ScriptCallable> onDispose)
        => new(runtimeId, token, name, onDispose);

    /// <summary>
    ///     Releases the runtime's reference. The first call invokes the <c>onDispose</c> callback (an
    ///     exception it throws propagates to that first caller); later and concurrent calls are no-ops.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _onDispose(this);
    }

    /// <summary>Formats as <c>&lt;runtimeId&gt;:fn#&lt;token&gt;(&lt;name&gt;)</c>.</summary>
    /// <returns>A diagnostic string.</returns>
    public override string ToString() => $"{RuntimeId}:fn#{Token}({Name})";
}
