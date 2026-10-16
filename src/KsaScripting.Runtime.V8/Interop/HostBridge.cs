using System.Collections;
using System.Globalization;
using System.Text;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Values;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;

namespace KsaScripting.Runtime.V8.Interop;

/// <summary>
///     The <c>__host</c> object the ksa prelude is written against (implementation guide section 3.7). Scripts see
///     camelCase methods; every call lands on the <see cref="IHostFacade"/> of the owning runtime.
///     This type is public only because ClearScript can bind members of public types only. It is NOT an API for C#
///     callers: its constructor is internal and it is only meaningful inside a script engine.
/// </summary>
public sealed class HostBridge
{
    private readonly IHostFacade _host;
    private readonly JsValues _values;
    private readonly TaskScheduler _scheduler;
    private readonly Func<ScriptObject, string?, ScriptCallable> _registerCallable;
    private readonly Action<Exception> _reportAsyncError;
    private long _nextPromiseId;

    internal HostBridge(
        IHostFacade host,
        JsValues values,
        TaskScheduler scheduler,
        Func<ScriptObject, string?, ScriptCallable> registerCallable,
        Action<Exception> reportAsyncError)
    {
        _host = host;
        _values = values;
        _scheduler = scheduler;
        _registerCallable = registerCallable;
        _reportAsyncError = reportAsyncError;
    }

    // ---------------------------------------------------------------- reads

    /// <summary>Reads a value from a host object (<c>"vessel:&lt;id&gt;"</c>, <c>"time"</c>, …).</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="path">The dotted path.</param>
    /// <returns>The value as a JS value.</returns>
    [ScriptMember("read")]
    public object? Read(object? handle, object? path = null) => _values.ToJs(_host.Read(Text(handle), Text(path)));

    /// <summary>Reads a whole record as an object.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>The record, or <c>null</c>.</returns>
    [ScriptMember("snapshot")]
    public object? Snapshot(object? handle) => _values.ToJs(_host.Snapshot(Text(handle)));

    /// <summary>Lists host objects.</summary>
    /// <param name="kind"><c>"vessels"</c>, <c>"bodies"</c>, <c>"events"</c>, …</param>
    /// <returns>An array.</returns>
    [ScriptMember("list")]
    public object? List(object? kind) => _values.ToJs(_host.List(Text(kind)));

    // ---------------------------------------------------------------- writes and waits

    /// <summary>
    ///     Performs a game action. Frame actions return the CommandResult object <c>{ok, outcome, message}</c>; Solver actions
    ///     return the id (a number) of a promise slot that the JS facade turns into a Promise of the result.
    /// </summary>
    /// <param name="action">The action key.</param>
    /// <param name="vesselId">The vessel id, or null/undefined.</param>
    /// <param name="ordinal">The module ordinal (default -1).</param>
    /// <param name="value">The numeric argument (default 0).</param>
    /// <param name="values">An array of numbers, or null/undefined.</param>
    /// <param name="token">The symbolic payload, or null/undefined.</param>
    /// <param name="aux">The secondary symbolic payload, or null/undefined.</param>
    /// <returns>The result object, or a promise slot id.</returns>
    [ScriptMember("act")]
    public object? Act(object? action, object? vesselId = null, object? ordinal = null, object? value = null, object? values = null, object? token = null, object? aux = null)
    {
        var result = _host.Act(
            Text(action),
            OptionalText(vesselId),
            ToInt(ordinal, -1),
            ToDouble(value, 0),
            ToDoubleArray(values),
            OptionalText(token),
            OptionalText(aux));

        if (result.Immediate is { } immediate)
            return _values.ToJs(immediate.ToScriptValue());

        return result.Pending is { } pending ? StartPromise(pending) : null;
    }

    /// <summary>Creates a host wait and returns the id of the promise slot the JS facade turns into a Promise.</summary>
    /// <param name="kind"><c>"sleep"</c>, <c>"nextStep"</c>, <c>"nextFrame"</c>, <c>"until"</c>, <c>"waitEvent"</c>, <c>"termRead"</c>.</param>
    /// <param name="arg">The kind-specific argument.</param>
    /// <returns>The promise slot id (a number).</returns>
    [ScriptMember("waitStart")]
    public double WaitStart(object? kind, object? arg = null)
        => StartPromise(_host.Wait(Text(kind), _values.ToScriptValue(arg)));

    // ---------------------------------------------------------------- hooks

    /// <summary>Registers a script callback.</summary>
    /// <param name="kind"><c>"event"</c>, <c>"tick"</c>, <c>"service"</c>, <c>"command"</c>.</param>
    /// <param name="key">The event name, tick key or command name.</param>
    /// <param name="fn">The JS function.</param>
    /// <param name="opts">An options object (<c>sync</c>, <c>vessel</c>, <c>clock</c>, <c>intervalSeconds</c>), or null/undefined.</param>
    /// <returns>The subscription id (a GUID in "D" format).</returns>
    [ScriptMember("subscribe")]
    public string Subscribe(object? kind, object? key, object? fn, object? opts = null)
    {
        if (fn is not ScriptObject function || function is not IJavaScriptObject { Kind: JavaScriptObjectKind.Function })
            throw new ArgumentException($"subscribe: the handler for {Text(kind)} \"{Text(key)}\" must be a function");

        var callable = _registerCallable(function, Text(key));
        Guid id;
        try
        {
            id = _host.Subscribe(Text(kind), Text(key), callable, ReadBindingOptions(opts));
        }
        catch
        {
            callable.Dispose();
            throw;
        }

        return id.ToString("D");
    }

    /// <summary>Removes a subscription.</summary>
    /// <param name="id">The id returned by <c>subscribe</c>.</param>
    [ScriptMember("unsubscribe")]
    public void Unsubscribe(object? id)
    {
        if (!Guid.TryParse(Text(id), out var guid))
            throw new ArgumentException($"unsubscribe: '{Text(id)}' is not a subscription id");

        _host.Unsubscribe(guid);
    }

    // ---------------------------------------------------------------- misc

    /// <summary>Writes a log line.</summary>
    /// <param name="level">0 debug, 1 info, 2 warn, 3 error.</param>
    /// <param name="message">The message.</param>
    [ScriptMember("log")]
    public void Log(object? level, object? message = null) => _host.Log(ToInt(level, 1), Text(message));

    /// <summary>Executes a game console command.</summary>
    /// <param name="command">The command line.</param>
    [ScriptMember("consoleExec")]
    public void ConsoleExec(object? command) => _host.ConsoleExec(Text(command));

    /// <summary>Reads a persisted per-script value.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null.</returns>
    [ScriptMember("stateGet")]
    public object? StateGet(object? key) => _values.ToJs(_host.StateGet(Text(key)));

    /// <summary>Writes a persisted per-script value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value (null clears it).</param>
    [ScriptMember("stateSet")]
    public void StateSet(object? key, object? value = null) => _host.StateSet(Text(key), _values.ToScriptValue(value));

    // ---------------------------------------------------------------- terminals

    /// <summary>Writes a string (UTF-8) or bytes (Uint8Array/ArrayBuffer) to a script terminal.</summary>
    /// <param name="termId">The terminal id.</param>
    /// <param name="data">The string or bytes.</param>
    [ScriptMember("termWrite")]
    public void TermWrite(object? termId, object? data)
    {
        byte[] bytes;
        if (data is string text)
            bytes = Encoding.UTF8.GetBytes(text);
        else if (!JsValues.TryGetBytes(data, out bytes))
            throw new ArgumentException("termWrite: data must be a string, a Uint8Array or an ArrayBuffer");

        _host.TermWrite(ToInt(termId, 0), bytes);
    }

    /// <summary>Describes a terminal.</summary>
    /// <param name="termId">The terminal id.</param>
    /// <returns><c>{cols, rows, attached}</c>.</returns>
    [ScriptMember("termInfo")]
    public object? TermInfo(object? termId) => _values.ToJs(_host.TermInfo(ToInt(termId, 0)));

    /// <summary>Sets a terminal's window title.</summary>
    /// <param name="termId">The terminal id.</param>
    /// <param name="title">The title.</param>
    [ScriptMember("termSetTitle")]
    public void TermSetTitle(object? termId, object? title) => _host.TermSetTitle(ToInt(termId, 0), Text(title));

    /// <summary>Sets whether the terminal's script stops when the terminal is detached.</summary>
    /// <param name="termId">The terminal id.</param>
    /// <param name="stopWhenDetached">Whether to stop when detached.</param>
    [ScriptMember("termPolicy")]
    public void TermPolicy(object? termId, object? stopWhenDetached)
        => _host.TermPolicy(ToInt(termId, 0), ToBool(stopWhenDetached));

    // ---------------------------------------------------------------- promises

    /// <summary>
    ///     Registers the settlement of a pending host wait and returns its promise slot id. The JS facade creates the Promise
    ///     for that id; the slot is settled ON THE GAME THREAD from the engine's queue (<see cref="_scheduler"/>), so the awaiting
    ///     script continues inside <c>Advance</c>. A failed or cancelled wait rejects with an <c>Error</c>. Settling is done here
    ///     (not by ClearScript's Task-to-Promise conversion) so that an exception raised while the script's continuation runs - an
    ///     interrupt, a heap violation - is reported instead of being swallowed by a ClearScript continuation.
    /// </summary>
    private double StartPromise(HostWait wait)
    {
        var id = ++_nextPromiseId;
        wait.Task.ContinueWith(
            static (task, state) =>
            {
                var (bridge, promiseId) = ((HostBridge, long))state!;
                bridge.SettlePromise(promiseId, task);
            },
            (this, id),
            CancellationToken.None,
            TaskContinuationOptions.None,
            _scheduler);
        return id;
    }

    private void SettlePromise(long id, Task<ScriptValue> task)
    {
        try
        {
            var success = task.IsCompletedSuccessfully;
            object? value;
            if (success)
            {
                try
                {
                    value = _values.ToJs(task.Result);
                }
                catch (Exception ex) when (ex is not ScriptInterruptedException)
                {
                    success = false;
                    value = ex.Message;
                }
            }
            else
            {
                value = task.IsCanceled ? "the wait was cancelled" : (task.Exception!.InnerException ?? task.Exception).Message;
            }

            _values.Settle(id, success, value);
        }
        catch (Exception ex)
        {
            _reportAsyncError(ex);
        }
    }

    // ---------------------------------------------------------------- argument normalisation

    private BindingOptions ReadBindingOptions(object? opts)
    {
        var defaults = new BindingOptions();
        if (opts is not ScriptObject options)
            return defaults;

        return new BindingOptions(
            Sync: options.GetProperty("sync") is { } sync && sync is not Undefined ? ToBool(sync) : defaults.Sync,
            Vessel: OptionalText(options.GetProperty("vessel")) ?? defaults.Vessel,
            Clock: OptionalText(options.GetProperty("clock")) ?? defaults.Clock,
            IntervalSeconds: ToDouble(options.GetProperty("intervalSeconds"), defaults.IntervalSeconds));
    }

    private static string Text(object? value) => OptionalText(value) ?? "";

    private static string? OptionalText(object? value) => value switch
    {
        null or Undefined => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static int ToInt(object? value, int fallback)
    {
        var number = ToDouble(value, fallback);
        return double.IsFinite(number) ? (int)Math.Clamp(Math.Truncate(number), int.MinValue, int.MaxValue) : fallback;
    }

    private static double ToDouble(object? value, double fallback) => value switch
    {
        null or Undefined => fallback,
        double d => double.IsFinite(d) ? d : fallback,
        float f => double.IsFinite(f) ? f : fallback,
        int i => i,
        long l => l,
        uint u => u,
        short s => s,
        byte b => b,
        bool flag => flag ? 1 : 0,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        IConvertible convertible => TryConvert(convertible, fallback),
        _ => fallback,
    };

    private static double TryConvert(IConvertible convertible, double fallback)
    {
        try
        {
            return convertible.ToDouble(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return fallback;
        }
    }

    private static bool ToBool(object? value) => value switch
    {
        null or Undefined => false,
        bool flag => flag,
        string text => text.Length > 0,
        _ => ToDouble(value, 0) != 0,
    };

    private static double[]? ToDoubleArray(object? value)
    {
        switch (value)
        {
            case null or Undefined:
                return null;
            case double[] doubles:
                return doubles;
            case IList list:
            {
                var result = new double[list.Count];
                for (var i = 0; i < result.Length; i++)
                    result[i] = ToDouble(list[i], 0);
                return result;
            }

            case ScriptObject { } array:
            {
                // A JS array (IList) was handled above; this covers array-likes such as typed arrays.
                var length = (int)ToDouble(array.GetProperty("length"), 0);
                var result = new double[Math.Max(0, length)];
                for (var i = 0; i < result.Length; i++)
                    result[i] = ToDouble(array.GetProperty(i), 0);
                return result;
            }

            default:
                return null;
        }
    }
}
