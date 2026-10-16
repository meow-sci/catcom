using System.Collections;
using System.Globalization;
using System.Numerics;
using KsaScripting.Core.Values;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace KsaScripting.Runtime.V8;

/// <summary>
///     Converts values between <see cref="ScriptValue"/> and the objects ClearScript hands across the V8 boundary, for ONE
///     engine. Scalars cross natively; compound values go through <c>JSON.parse</c> of <see cref="ScriptValue.ToJson"/> with
///     a reviver (defined once per engine) that rebuilds <c>{"$bytes"}</c> as <c>Uint8Array</c>, <c>{"$handle"}</c> as a string
///     and <c>{"$fn"}</c> as <c>null</c>.
/// </summary>
internal sealed class JsValues : IDisposable
{
    private const int MaxDepth = 32;

    private const string HelperSource = """
        (function () {
          "use strict";
          const jsonParse = JSON.parse;
          const isArray = Array.isArray;
          const ObjectKeys = Object.keys;
          const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
          const lookup = new Uint8Array(256).fill(255);
          for (let i = 0; i < 64; i++) lookup[alphabet.charCodeAt(i)] = i;

          // V8 has no atob: decode base64 by hand.
          function fromBase64(text) {
            let length = text.length;
            while (length > 0 && text.charCodeAt(length - 1) === 61) length--;
            const out = new Uint8Array((length * 6) >> 3);
            let bits = 0, acc = 0, o = 0;
            for (let i = 0; i < length; i++) {
              const v = lookup[text.charCodeAt(i) & 255];
              if (v === 255) continue;
              acc = ((acc << 6) | v) & 0xffff;
              bits += 6;
              if (bits >= 8) {
                bits -= 8;
                if (o < out.length) out[o++] = (acc >> bits) & 255;
              }
            }
            return out;
          }

          function reviver(key, value) {
            if (value !== null && typeof value === "object" && !isArray(value)) {
              let count = 0, only;
              for (const k in value) {
                if (++count > 1) return value;
                only = k;
              }
              if (count === 1) {
                if (only === "$bytes") {
                  const b = value.$bytes;
                  if (typeof b === "string") return fromBase64(b);
                } else if (only === "$handle") {
                  const h = value.$handle;
                  if (typeof h === "string") return h;
                } else if (only === "$fn") {
                  return null;
                }
              }
            }
            return value;
          }

          // Where the rejection of a promise returned by an invoked function is reported (set by the runtime).
          let rejectionSink = null;

          function observedRejection(reason) {
            const sink = rejectionSink;
            if (sink === null) return;
            try { sink(reason); } catch (_ignored) { /* reporting must never raise another rejection */ }
          }

          function describeReason(reason) {
            try {
              if (reason instanceof Error) return String(reason);
              if (typeof reason === "string") return reason;
              if (reason !== null && typeof reason === "object") {
                try {
                  const json = JSON.stringify(reason);
                  if (json !== undefined) return json;
                } catch (_ignored) { /* fall through to String() */ }
              }
              return String(reason);
            } catch (_ignored) {
              return "[unprintable]";
            }
          }

          // Promises handed to scripts for host waits. The host settles them by id from its own game-thread queue, so an
          // exception raised while the promise jobs run (an interrupt, a heap violation) surfaces in the caller.
          const pending = new Map();

          function newPromise(id) {
            return new Promise(function (resolve, reject) { pending.set(id, [resolve, reject]); });
          }

          return {
            parse: function (json) { return jsonParse(json, reviver); },
            // Calls an exported function / hook. When it returns a Promise (an async function) the Promise is observed HERE,
            // by attaching a rejection handler, so its failure is reported exactly once and never counts as unhandled;
            // the host gets undefined (an async function "returns immediately").
            invoke: function (fn, ...args) {
              const result = fn(...args);
              if (result !== null && typeof result === "object" && typeof result.then === "function") {
                Promise.resolve(result).then(undefined, observedRejection);
                return undefined;
              }
              return result;
            },
            setRejectionSink: function (sink) { rejectionSink = sink; },
            describe: function (reason) {
              let stack = null;
              try {
                if (reason !== null && typeof reason === "object" && typeof reason.stack === "string") stack = reason.stack;
              } catch (_ignored) { /* a hostile stack getter */ }
              return { text: describeReason(reason), stack: stack };
            },
            // The script-visible __host: a frozen JS facade over the native bridge. Promises are created HERE, in
            // JavaScript, so no Promise ever crosses the interop boundary (where ClearScript would convert it to a
            // Task and back, routing the resolution through its own continuations).
            installHost: function () {
              const n = globalThis.__hostNative;
              delete globalThis.__hostNative;
              const facade = Object.freeze({
                read: function (handle, path) { return n.read(handle, path); },
                snapshot: function (handle) { return n.snapshot(handle); },
                list: function (kind) { return n.list(kind); },
                act: function (action, vesselId, ordinal, value, values, token, aux) {
                  const r = n.act(action, vesselId, ordinal, value, values, token, aux);
                  return typeof r === "number" ? newPromise(r) : r;
                },
                wait: function (kind, arg) { return newPromise(n.waitStart(kind, arg)); },
                subscribe: function (kind, key, fn, opts) { return n.subscribe(kind, key, fn, opts); },
                unsubscribe: function (id) { return n.unsubscribe(id); },
                log: function (level, message) { return n.log(level, message); },
                consoleExec: function (command) { return n.consoleExec(command); },
                stateGet: function (key) { return n.stateGet(key); },
                stateSet: function (key, value) { return n.stateSet(key, value); },
                termWrite: function (termId, data) { return n.termWrite(termId, data); },
                termInfo: function (termId) { return n.termInfo(termId); },
                termSetTitle: function (termId, title) { return n.termSetTitle(termId, title); },
                termPolicy: function (termId, stopWhenDetached) { return n.termPolicy(termId, stopWhenDetached); }
              });
              Object.defineProperty(globalThis, "__host", { value: facade, writable: false, configurable: true, enumerable: false });
            },
            settle: function (id, success, value) {
              const entry = pending.get(id);
              if (entry === undefined) return;
              pending.delete(id);
              if (success) entry[0](value); else entry[1](new Error(String(value)));
            },
            exports: function (ns) {
              const names = [];
              for (const key of ObjectKeys(ns)) {
                let value;
                try { value = ns[key]; } catch (e) { continue; }
                if (typeof value === "function") names.push(key);
              }
              return names;
            },
            makeContext: function (obj) {
              const k = globalThis.ksa;
              return k && typeof k.makeContext === "function" ? k.makeContext(obj) : obj;
            },
            installConsole: function () {
              const k = globalThis.ksa;
              if (!k || !k.console) return;
              const noop = function () {};
              const names = ["assert", "clear", "count", "countReset", "dir", "dirxml", "group", "groupCollapsed",
                "groupEnd", "table", "time", "timeEnd", "timeLog", "trace"];
              const merged = {};
              for (const n of names) merged[n] = noop;
              Object.assign(merged, k.console);
              Object.defineProperty(globalThis, "console", { value: merged, writable: true, configurable: true, enumerable: false });
            }
          };
        })()
        """;

    private readonly ScriptObject _helper;
    private readonly ScriptObject _parse;
    private readonly ScriptObject _invoke;
    private readonly ScriptObject _setRejectionSink;
    private readonly ScriptObject _describe;
    private readonly ScriptObject _exports;
    private readonly ScriptObject _makeContext;
    private readonly ScriptObject _installConsole;
    private readonly ScriptObject _installHost;
    private readonly ScriptObject _settle;
    private readonly Func<ScriptObject, string?, ScriptCallable> _registerCallable;

    private JsValues(ScriptObject helper, Func<ScriptObject, string?, ScriptCallable> registerCallable)
    {
        _helper = helper;
        _parse = (ScriptObject)helper.GetProperty("parse");
        _invoke = (ScriptObject)helper.GetProperty("invoke");
        _setRejectionSink = (ScriptObject)helper.GetProperty("setRejectionSink");
        _describe = (ScriptObject)helper.GetProperty("describe");
        _exports = (ScriptObject)helper.GetProperty("exports");
        _makeContext = (ScriptObject)helper.GetProperty("makeContext");
        _installConsole = (ScriptObject)helper.GetProperty("installConsole");
        _installHost = (ScriptObject)helper.GetProperty("installHost");
        _settle = (ScriptObject)helper.GetProperty("settle");
        _registerCallable = registerCallable;
    }

    /// <summary>Installs the per-engine helper script and returns the converter.</summary>
    /// <param name="engine">The script engine.</param>
    /// <param name="registerCallable">Wraps a JS function (and its name) as a <see cref="ScriptCallable"/> owned by the engine's module.</param>
    /// <returns>The converter.</returns>
    public static JsValues Create(V8ScriptEngine engine, Func<ScriptObject, string?, ScriptCallable> registerCallable)
    {
        // "[internal]" in the name keeps the helper's own stack frames out of script tracebacks (see StackTraceMapper).
        var helper = (ScriptObject)engine.Evaluate(new DocumentInfo("[internal] ksa-runtime-helper.js"), HelperSource);
        return new JsValues(helper, registerCallable);
    }

    // ---------------------------------------------------------------- ScriptValue -> JS

    /// <summary>Converts a host value to the object ClearScript marshals into JavaScript.</summary>
    /// <param name="value">The value.</param>
    /// <returns><c>null</c>, a boolean, a double, a string, or a JS object/array/Uint8Array.</returns>
    public object? ToJs(ScriptValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            ScriptValueKind.Null => null,
            ScriptValueKind.Bool => value.AsBool(),
            ScriptValueKind.Number => value.AsNumber(),
            ScriptValueKind.String => value.AsString(),
            ScriptValueKind.Handle => value.AsHandle(),
            _ => _parse.InvokeAsFunction(value.ToJson()),
        };
    }

    /// <summary>Calls a script function. If it returns a Promise the Promise is observed (see the helper) and <c>undefined</c> is returned.</summary>
    /// <param name="function">The JS function.</param>
    /// <param name="args">The converted arguments.</param>
    /// <returns>The function's result as ClearScript marshals it.</returns>
    public object? Invoke(ScriptObject function, object?[] args)
    {
        var all = new object?[args.Length + 1];
        all[0] = function;
        Array.Copy(args, 0, all, 1, args.Length);
        return _invoke.InvokeAsFunction(all);
    }

    /// <summary>Sets where rejections of promises returned by invoked functions are delivered (called by the script's JS, on the game thread).</summary>
    /// <param name="sink">The receiver of the rejection reason.</param>
    public void InstallRejectionSink(Action<object?> sink) => _setRejectionSink.InvokeAsFunction(sink);

    /// <summary>Describes a rejection reason: the text <c>String(reason)</c>-like and, for an Error, its JS stack.</summary>
    /// <param name="reason">The reason as marshalled by ClearScript.</param>
    /// <returns>The text and the stack (or <c>null</c>).</returns>
    public (string Text, string? Stack) Describe(object? reason)
    {
        try
        {
            using var described = (ScriptObject)_describe.InvokeAsFunction(reason);
            return (described.GetProperty("text") as string ?? "", described.GetProperty("stack") as string);
        }
        catch (Exception ex) when (ex is ScriptEngineException or ObjectDisposedException or InvalidOperationException or InvalidCastException)
        {
            // Describing may not run (an interrupt is pending, the engine is going away): fall back to what the host can see.
            return (reason switch { null => "null", Undefined => "undefined", _ => reason.ToString() ?? "" }, null);
        }
    }

    /// <summary>Runs a context map through the prelude's <c>ksa.makeContext</c> (when the prelude defines it).</summary>
    /// <param name="contextObject">The JS object converted from the <c>$ctx</c> map.</param>
    /// <returns>The context object the script function receives.</returns>
    public object? MakeContext(object? contextObject) => _makeContext.InvokeAsFunction(contextObject);

    /// <summary>
    ///     Defines the script-visible <c>globalThis.__host</c>: a frozen JS facade over the native bridge, which must already be
    ///     exposed as the global host object <c>__hostNative</c> (removed from the global scope here).
    /// </summary>
    public void InstallHost() => _installHost.InvokeAsFunction();

    /// <summary>Settles a promise the JS facade created for a host wait; the script's continuation runs before this returns.</summary>
    /// <param name="id">The promise id.</param>
    /// <param name="success"><c>true</c> to resolve with <paramref name="value"/>, <c>false</c> to reject with an Error.</param>
    /// <param name="value">The result (success) or the error message (failure).</param>
    public void Settle(long id, bool success, object? value) => _settle.InvokeAsFunction((double)id, success, value);

    /// <summary>Replaces <c>globalThis.console</c> with the prelude's <c>ksa.console</c> (plus no-op extras).</summary>
    public void InstallConsole() => _installConsole.InvokeAsFunction();

    /// <summary>Lists the function-valued own enumerable members of a module namespace.</summary>
    /// <param name="moduleNamespace">The namespace object.</param>
    /// <returns>The names, sorted ordinally.</returns>
    public string[] ExportedFunctionNames(ScriptObject moduleNamespace)
    {
        var names = new List<string>();
        if (_exports.InvokeAsFunction(moduleNamespace) is IEnumerable list)
        {
            foreach (var item in list)
            {
                if (item is string name)
                    names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    // ---------------------------------------------------------------- JS -> ScriptValue

    /// <summary>Converts an object received from JavaScript to a <see cref="ScriptValue"/>.</summary>
    /// <param name="value">The marshalled value.</param>
    /// <returns>The converted value (never <c>null</c>).</returns>
    public ScriptValue ToScriptValue(object? value) => Convert(value, 0);

    /// <summary>Extracts bytes from a Uint8Array/ArrayBuffer/view or a <c>byte[]</c>.</summary>
    /// <param name="value">The marshalled value.</param>
    /// <param name="bytes">The bytes (a copy).</param>
    /// <returns><c>false</c> when the value is not a byte container.</returns>
    public static bool TryGetBytes(object? value, out byte[] bytes)
    {
        switch (value)
        {
            case byte[] array:
                bytes = (byte[])array.Clone();
                return true;
            case IArrayBuffer buffer:
                bytes = buffer.GetBytes();
                return true;
            case IArrayBufferView view:
                bytes = view.GetBytes();
                return true;
            default:
                bytes = [];
                return false;
        }
    }

    private ScriptValue Convert(object? value, int depth)
    {
        switch (value)
        {
            case null:
            case Undefined:
                return ScriptValue.Null;
            case bool b:
                return ScriptValue.From(b);
            case string s:
                return ScriptValue.From(s);
            case char c:
                return ScriptValue.From(c.ToString());
            case Task:
                // A Promise (converted to a Task) cannot cross as data.
                return ScriptValue.Null;
            case ScriptObject scriptObject:
                return ConvertObject(scriptObject, depth);
            case DateTime dateTime:
                return ScriptValue.From(dateTime.ToString("O", CultureInfo.InvariantCulture));
            case DateTimeOffset dateTimeOffset:
                return ScriptValue.From(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
            case TimeSpan timeSpan:
                return ScriptValue.From(timeSpan.ToString("c", CultureInfo.InvariantCulture));
            case Guid guid:
                return ScriptValue.From(guid.ToString("D"));
            case byte[] bytes:
                return ScriptValue.From(bytes);
        }

        if (TryGetNumber(value, out var number))
            return ScriptValue.From(number);

        if (value is ScriptValue already)
            return already;

        return ScriptValue.FromObject(value);
    }

    private static bool TryGetNumber(object value, out double number)
    {
        switch (value)
        {
            case double d:
                number = d;
                return true;
            case float f:
                number = f;
                return true;
            case int i:
                number = i;
                return true;
            case long l:
                number = l;
                return true;
            case uint ui:
                number = ui;
                return true;
            case ulong ul:
                number = ul;
                return true;
            case short sh:
                number = sh;
                return true;
            case ushort us:
                number = us;
                return true;
            case byte by:
                number = by;
                return true;
            case sbyte sb:
                number = sb;
                return true;
            case decimal dec:
                number = (double)dec;
                return true;
            case BigInteger big:
                number = (double)big;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private ScriptValue ConvertObject(ScriptObject scriptObject, int depth)
    {
        if (depth >= MaxDepth)
            return ScriptValue.Null;

        if (scriptObject is IJavaScriptObject javaScriptObject)
        {
            switch (javaScriptObject.Kind)
            {
                case JavaScriptObjectKind.Function:
                    return ScriptValue.Function(_registerCallable(scriptObject, FunctionName(scriptObject)));
                case JavaScriptObjectKind.Promise:
                    return ScriptValue.Null;
                case JavaScriptObjectKind.Array:
                    return ConvertArray(scriptObject, depth);
                case JavaScriptObjectKind.ArrayBuffer:
                case JavaScriptObjectKind.DataView:
                    return TryGetBytes(scriptObject, out var bytes) ? ScriptValue.From(bytes) : ScriptValue.Null;
                case JavaScriptObjectKind.TypedArray:
                    if (scriptObject is ITypedArray<byte>)
                        return TryGetBytes(scriptObject, out var typedBytes) ? ScriptValue.From(typedBytes) : ScriptValue.Null;
                    return ConvertIndexed(scriptObject, depth);
                case JavaScriptObjectKind.Unknown:
                    var date = TryConvertDate(scriptObject);
                    if (date is not null)
                        return date;
                    break;
            }
        }

        return ConvertMap(scriptObject, depth);
    }

    private ScriptValue ConvertArray(ScriptObject array, int depth)
    {
        var length = ToLength(array.GetProperty("length"));
        var items = new List<ScriptValue>(length);
        for (var i = 0; i < length; i++)
            items.Add(Convert(array.GetProperty(i), depth + 1));
        return ScriptValue.List(items);
    }

    private ScriptValue ConvertIndexed(ScriptObject typedArray, int depth)
    {
        var length = ToLength(typedArray.GetProperty("length"));
        var items = new List<ScriptValue>(length);
        for (var i = 0; i < length; i++)
            items.Add(Convert(typedArray.GetProperty(i), depth + 1));
        return ScriptValue.List(items);
    }

    private ScriptValue ConvertMap(ScriptObject scriptObject, int depth)
    {
        var map = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
        foreach (var name in scriptObject.PropertyNames)
            map[name] = Convert(scriptObject.GetProperty(name), depth + 1);
        return ScriptValue.Map(map);
    }

    private static ScriptValue? TryConvertDate(ScriptObject scriptObject)
    {
        // A JS Date has no own enumerable members; ask it for its ISO form.
        if (scriptObject.PropertyNames.Any())
            return null;

        if (scriptObject.GetProperty("toISOString") is not ScriptObject { } method
            || method is not IJavaScriptObject { Kind: JavaScriptObjectKind.Function })
            return null;

        try
        {
            return scriptObject.InvokeMethod("toISOString") is string iso ? ScriptValue.From(iso) : null;
        }
        catch (ScriptEngineException)
        {
            // An invalid Date throws RangeError from toISOString: not representable.
            return ScriptValue.Null;
        }
    }

    private static string? FunctionName(ScriptObject function)
        => function.GetProperty("name") is string { Length: > 0 } name ? name : null;

    private static int ToLength(object? value)
        => value switch
        {
            int i => Math.Max(0, i),
            double d => (int)Math.Clamp(d, 0, int.MaxValue),
            long l => (int)Math.Clamp(l, 0, int.MaxValue),
            _ => 0,
        };

    /// <inheritdoc />
    public void Dispose()
    {
        _parse.Dispose();
        _invoke.Dispose();
        _setRejectionSink.Dispose();
        _describe.Dispose();
        _exports.Dispose();
        _makeContext.Dispose();
        _installConsole.Dispose();
        _installHost.Dispose();
        _settle.Dispose();
        _helper.Dispose();
    }
}
