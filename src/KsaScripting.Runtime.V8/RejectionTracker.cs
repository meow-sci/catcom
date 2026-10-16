using Microsoft.ClearScript;

namespace KsaScripting.Runtime.V8;

/// <summary>One promise rejection waiting to be reported to the host.</summary>
/// <param name="Promise">The rejected promise (V8 reported it as having no handler), or <c>null</c> for a rejection the runtime itself observed.</param>
/// <param name="Reason">The rejection reason as marshalled by ClearScript (a <see cref="ScriptObject"/> for an <c>Error</c>, otherwise a primitive).</param>
/// <param name="FromInvoke"><c>true</c> when it is the failure of an invoked async function (reported like a failed <c>Invoke</c>); <c>false</c> for an unhandled rejection.</param>
internal sealed record PendingRejection(ScriptObject? Promise, object? Reason, bool FromInvoke)
{
    /// <summary>Releases the V8 handles the entry holds.</summary>
    public void Release()
    {
        DisposeQuietly(Promise);
        DisposeQuietly(Reason);
    }

    /// <summary>Disposes a marshalled value when it is a script object.</summary>
    /// <param name="value">The value.</param>
    public static void DisposeQuietly(object? value)
    {
        try
        {
            (value as IDisposable)?.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The engine is already gone: the handle died with it.
        }
    }
}

/// <summary>
///     The promise rejections of ONE script engine that have not (yet) been given a handler. V8 reports a rejection without
///     handler synchronously and, if a handler is attached later in the same turn, a second notification; the runtime reports
///     what is still pending once the outermost engine call has returned (and so every microtask has run).
/// </summary>
internal sealed class RejectionTracker
{
    private readonly object _gate = new();
    private readonly List<PendingRejection> _pending = [];

    /// <summary>Records a rejection.</summary>
    /// <param name="rejection">The rejection; the tracker owns its handles from now on.</param>
    public void Add(PendingRejection rejection)
    {
        lock (_gate)
            _pending.Add(rejection);
    }

    /// <summary>A handler was attached to a promise that had been reported as rejected without one: forgets it.</summary>
    /// <param name="promise">The promise.</param>
    public void Handled(ScriptObject promise)
    {
        PendingRejection? removed = null;
        lock (_gate)
        {
            var index = _pending.FindIndex(p => p.Promise is not null && p.Promise.Equals(promise));
            if (index >= 0)
            {
                removed = _pending[index];
                _pending.RemoveAt(index);
            }
        }

        removed?.Release();
    }

    /// <summary>Removes and returns everything pending; the caller owns the entries.</summary>
    /// <returns>The pending rejections, oldest first.</returns>
    public PendingRejection[] Take()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
                return [];

            var all = _pending.ToArray();
            _pending.Clear();
            return all;
        }
    }

    /// <summary>Drops (and releases) everything pending.</summary>
    public void Clear()
    {
        foreach (var rejection in Take())
            rejection.Release();
    }
}
