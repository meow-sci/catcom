using KsaScripting.Core.Values;

namespace KsaScripting.Core.Hosting;

/// <summary>
///     A pending host operation a script is waiting on (<c>sleep</c>, <c>nextStep</c>, <c>until</c>, an
///     <c>act</c> result, …). The scheduler completes it on the game thread; the runtime turns it into
///     its native await (a promise, a coroutine yield).
/// </summary>
public sealed class HostWait
{
    private readonly TaskCompletionSource<ScriptValue> _source;

    /// <summary>Creates a pending wait.</summary>
    /// <param name="id">The wait id (unique per scheduler).</param>
    /// <param name="kind">The wait kind, e.g. <c>"sleep"</c>, <c>"nextStep"</c>, <c>"act"</c>.</param>
    public HostWait(long id, string kind)
    {
        Id = id;
        Kind = kind;
        // RunContinuationsAsynchronously: completing a wait on the game thread must never run the
        // awaiting continuation inline.
        _source = new TaskCompletionSource<ScriptValue>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The wait id.</summary>
    public long Id { get; }

    /// <summary>The wait kind.</summary>
    public string Kind { get; }

    /// <summary>A task that completes with the wait's result.</summary>
    public Task<ScriptValue> Task => _source.Task;

    internal TaskCompletionSource<ScriptValue> Source => _source;

    /// <summary>Whether the wait has finished (completed, failed or canceled).</summary>
    public bool IsCompleted => _source.Task.IsCompleted;

    /// <summary>Completes the wait with a result.</summary>
    /// <param name="result">The result.</param>
    /// <returns><c>false</c> if it had already finished.</returns>
    public bool Complete(ScriptValue result) => _source.TrySetResult(result);

    /// <summary>Fails the wait.</summary>
    /// <param name="error">The error.</param>
    /// <returns><c>false</c> if it had already finished.</returns>
    public bool Fail(Exception error) => _source.TrySetException(error);

    /// <summary>Cancels the wait.</summary>
    /// <returns><c>false</c> if it had already finished.</returns>
    public bool Cancel() => _source.TrySetCanceled();

    /// <summary>The wait object runtimes hand to the script preludes: a map <c>{"$wait": Id}</c>.</summary>
    /// <returns>The map value.</returns>
    public ScriptValue ToScriptValue()
        => ScriptValue.Map(new Dictionary<string, ScriptValue> { ["$wait"] = ScriptValue.From((double)Id) });
}
