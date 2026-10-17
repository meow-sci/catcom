using System.Buffers;
using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Catcom.Core.Values;

/// <summary>
///     The one value type that crosses the host/runtime boundary (and the one the game bridge returns
///     for script reads). Immutable: lists and maps are copied on construction. Structural equality.
/// </summary>
public sealed class ScriptValue : IEquatable<ScriptValue>
{
    private const int MaxObjectDepth = 32;
    private const int MaxJsonDepth = 1000;

    private static readonly ScriptValue TrueValue = new(ScriptValueKind.Bool, 1d, null);
    private static readonly ScriptValue FalseValue = new(ScriptValueKind.Bool, 0d, null);
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly double _number;
    private readonly object? _ref;

    private ScriptValue(ScriptValueKind kind, double number, object? reference)
    {
        Kind = kind;
        _number = number;
        _ref = reference;
    }

    /// <summary>The shared <see cref="ScriptValueKind.Null"/> value.</summary>
    public static readonly ScriptValue Null = new(ScriptValueKind.Null, 0d, null);

    /// <summary>The shared boolean <c>true</c>.</summary>
    public static ScriptValue True => TrueValue;

    /// <summary>The shared boolean <c>false</c>.</summary>
    public static ScriptValue False => FalseValue;

    /// <summary>The kind of this value.</summary>
    public ScriptValueKind Kind { get; }

    /// <summary>Whether this value is <see cref="ScriptValueKind.Null"/>.</summary>
    public bool IsNull => Kind == ScriptValueKind.Null;

    /// <summary>
    ///     Script truthiness: Null is false; Bool is its value; Number is true unless zero or NaN;
    ///     String is true unless empty; Bytes, List, Map, Function and Handle are always true.
    /// </summary>
    public bool IsTruthy => Kind switch
    {
        ScriptValueKind.Null => false,
        ScriptValueKind.Bool => _number != 0d,
        ScriptValueKind.Number => _number != 0d && !double.IsNaN(_number),
        ScriptValueKind.String => ((string)_ref!).Length != 0,
        _ => true,
    };

    // ---------------------------------------------------------------- construction

    /// <summary>Creates a boolean value (returns the cached <see cref="True"/>/<see cref="False"/>).</summary>
    /// <param name="v">The boolean.</param>
    /// <returns>The value.</returns>
    public static ScriptValue From(bool v) => v ? TrueValue : FalseValue;

    /// <summary>Creates a number value (stored as given, including NaN/infinity).</summary>
    /// <param name="v">The number.</param>
    /// <returns>The value.</returns>
    public static ScriptValue From(double v) => new(ScriptValueKind.Number, v, null);

    /// <summary>Creates a string value; <c>null</c> yields <see cref="Null"/>.</summary>
    /// <param name="v">The string, or <c>null</c>.</param>
    /// <returns>The value.</returns>
    public static ScriptValue From(string? v) => v is null ? Null : new(ScriptValueKind.String, 0d, v);

    /// <summary>Creates a bytes value; the buffer is copied.</summary>
    /// <param name="v">The bytes.</param>
    /// <returns>The value.</returns>
    public static ScriptValue From(ReadOnlyMemory<byte> v) => new(ScriptValueKind.Bytes, 0d, v.ToArray());

    /// <summary>Creates a list value; <paramref name="items"/> is copied (null items become <see cref="Null"/>).</summary>
    /// <param name="items">The elements.</param>
    /// <returns>The value.</returns>
    public static ScriptValue List(IReadOnlyList<ScriptValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var copy = new ScriptValue[items.Count];
        for (var i = 0; i < copy.Length; i++)
            copy[i] = items[i] ?? Null;
        return new ScriptValue(ScriptValueKind.List, 0d, Array.AsReadOnly(copy));
    }

    /// <summary>
    ///     Creates a map value; <paramref name="items"/> is copied into an ordinal-keyed dictionary that
    ///     keeps the enumeration (insertion) order of the source.
    /// </summary>
    /// <param name="items">The entries.</param>
    /// <returns>The value.</returns>
    public static ScriptValue Map(IReadOnlyDictionary<string, ScriptValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var copy = new Dictionary<string, ScriptValue>(items.Count, StringComparer.Ordinal);
        foreach (var pair in items)
            copy[pair.Key] = pair.Value ?? Null;
        return new ScriptValue(ScriptValueKind.Map, 0d, new ReadOnlyDictionary<string, ScriptValue>(copy));
    }

    /// <summary>Wraps a runtime-owned function.</summary>
    /// <param name="fn">The callable.</param>
    /// <returns>The value.</returns>
    public static ScriptValue Function(ScriptCallable fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return new ScriptValue(ScriptValueKind.Function, 0d, fn);
    }

    /// <summary>Creates a handle value (an opaque string naming a host object).</summary>
    /// <param name="handle">The handle text, e.g. <c>"vessel:abc"</c>.</param>
    /// <returns>The value.</returns>
    public static ScriptValue Handle(string handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return new ScriptValue(ScriptValueKind.Handle, 0d, handle);
    }

    // ---------------------------------------------------------------- accessors

    /// <summary>The boolean payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScriptValueKind.Bool"/>.</exception>
    public bool AsBool()
    {
        Expect(ScriptValueKind.Bool);
        return _number != 0d;
    }

    /// <summary>The number payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScriptValueKind.Number"/>.</exception>
    public double AsNumber()
    {
        Expect(ScriptValueKind.Number);
        return _number;
    }

    /// <summary>The string payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScriptValueKind.String"/>.</exception>
    public string AsString()
    {
        Expect(ScriptValueKind.String);
        return (string)_ref!;
    }

    /// <summary>The bytes payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScriptValueKind.Bytes"/>.</exception>
    public ReadOnlyMemory<byte> AsBytes()
    {
        Expect(ScriptValueKind.Bytes);
        return (byte[])_ref!;
    }

    /// <summary>The list payload (read-only).</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScriptValueKind.List"/>.</exception>
    public IReadOnlyList<ScriptValue> AsList()
    {
        Expect(ScriptValueKind.List);
        return (IReadOnlyList<ScriptValue>)_ref!;
    }

    /// <summary>The map payload (read-only, insertion order).</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScriptValueKind.Map"/>.</exception>
    public IReadOnlyDictionary<string, ScriptValue> AsMap()
    {
        Expect(ScriptValueKind.Map);
        return (IReadOnlyDictionary<string, ScriptValue>)_ref!;
    }

    /// <summary>The function payload.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScriptValueKind.Function"/>.</exception>
    public ScriptCallable AsFunction()
    {
        Expect(ScriptValueKind.Function);
        return (ScriptCallable)_ref!;
    }

    /// <summary>The handle text.</summary>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="ScriptValueKind.Handle"/>.</exception>
    public string AsHandle()
    {
        Expect(ScriptValueKind.Handle);
        return (string)_ref!;
    }

    /// <summary>Map lookup helper: <c>false</c> (and <see cref="Null"/>) when this is not a map or the key is absent.</summary>
    /// <param name="key">The map key.</param>
    /// <param name="value">The value found, else <see cref="Null"/>.</param>
    /// <returns>Whether the key was found.</returns>
    public bool TryGet(string key, out ScriptValue value)
    {
        if (Kind == ScriptValueKind.Map && ((IReadOnlyDictionary<string, ScriptValue>)_ref!).TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }

        value = Null;
        return false;
    }

    /// <summary>Map lookup: the value for <paramref name="key"/>, or <see cref="Null"/> when absent or this is not a map.</summary>
    /// <param name="key">The map key.</param>
    public ScriptValue this[string key] => TryGet(key, out var value) ? value : Null;

    private void Expect(ScriptValueKind expected)
    {
        if (Kind != expected)
            throw new InvalidOperationException($"expected {expected}, got {Kind}");
    }

    // ---------------------------------------------------------------- JSON

    /// <summary>
    ///     Serializes to compact JSON. Null → <c>null</c>; NaN/±Infinity → <c>0</c>; Bytes →
    ///     <c>{"$bytes":"&lt;base64&gt;"}</c>; Function → <c>{"$fn":&lt;token&gt;}</c>; Handle →
    ///     <c>{"$handle":"&lt;h&gt;"}</c>; Map → an object in insertion order; List → an array.
    /// </summary>
    /// <returns>The JSON text.</returns>
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            Write(writer, this);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter w, ScriptValue v)
    {
        switch (v.Kind)
        {
            case ScriptValueKind.Null:
                w.WriteNullValue();
                break;
            case ScriptValueKind.Bool:
                w.WriteBooleanValue(v._number != 0d);
                break;
            case ScriptValueKind.Number:
                w.WriteNumberValue(double.IsFinite(v._number) ? v._number : 0d);
                break;
            case ScriptValueKind.String:
                w.WriteStringValue((string)v._ref!);
                break;
            case ScriptValueKind.Bytes:
                w.WriteStartObject();
                w.WriteString("$bytes", Convert.ToBase64String((byte[])v._ref!));
                w.WriteEndObject();
                break;
            case ScriptValueKind.List:
                w.WriteStartArray();
                foreach (var item in (IReadOnlyList<ScriptValue>)v._ref!)
                    Write(w, item);
                w.WriteEndArray();
                break;
            case ScriptValueKind.Map:
                w.WriteStartObject();
                foreach (var pair in (IReadOnlyDictionary<string, ScriptValue>)v._ref!)
                {
                    w.WritePropertyName(pair.Key);
                    Write(w, pair.Value);
                }

                w.WriteEndObject();
                break;
            case ScriptValueKind.Function:
                w.WriteStartObject();
                w.WriteNumber("$fn", ((ScriptCallable)v._ref!).Token);
                w.WriteEndObject();
                break;
            case ScriptValueKind.Handle:
                w.WriteStartObject();
                w.WriteString("$handle", (string)v._ref!);
                w.WriteEndObject();
                break;
            default:
                w.WriteNullValue();
                break;
        }
    }

    /// <summary>
    ///     The inverse of <see cref="ToJson"/>. An object with exactly one key <c>"$bytes"</c> (a base64
    ///     string) becomes Bytes, and exactly one key <c>"$handle"</c> (a string) becomes a Handle;
    ///     <c>{"$fn":n}</c> stays a Map (callables cannot be reconstructed). JSON numbers become doubles.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The value.</returns>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static ScriptValue FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        return FromElement(doc.RootElement);
    }

    private static ScriptValue FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.True:
                return TrueValue;
            case JsonValueKind.False:
                return FalseValue;
            case JsonValueKind.Number:
                return From(e.TryGetDouble(out var d) ? d : 0d);
            case JsonValueKind.String:
                return From(e.GetString());
            case JsonValueKind.Array:
            {
                var items = new List<ScriptValue>(e.GetArrayLength());
                foreach (var item in e.EnumerateArray())
                    items.Add(FromElement(item));
                return List(items);
            }

            case JsonValueKind.Object:
            {
                var map = new Dictionary<string, ScriptValue>(StringComparer.Ordinal);
                foreach (var property in e.EnumerateObject())
                    map[property.Name] = FromElement(property.Value);

                if (map.Count == 1)
                {
                    if (map.TryGetValue("$bytes", out var b) && b.Kind == ScriptValueKind.String
                        && TryDecodeBase64((string)b._ref!, out var bytes))
                        return new ScriptValue(ScriptValueKind.Bytes, 0d, bytes);
                    if (map.TryGetValue("$handle", out var h) && h.Kind == ScriptValueKind.String)
                        return Handle((string)h._ref!);
                }

                return Map(map);
            }

            default:
                return Null;
        }
    }

    private static bool TryDecodeBase64(string text, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    // ---------------------------------------------------------------- FromObject

    /// <summary>
    ///     Converts an arbitrary CLR object graph (records, primitives, lists, dictionaries) into a
    ///     <see cref="ScriptValue"/>. Numbers become doubles (NaN/±Infinity → 0); enums become their name;
    ///     <see cref="Guid"/> its <c>"D"</c> string; byte buffers Bytes; string-keyed dictionaries Maps
    ///     (keys unchanged); other enumerables Lists; any other object a Map of its public instance
    ///     readable properties with camelCase keys (a throwing getter yields Null). Recursion is limited
    ///     to 32 levels (deeper → Null).
    /// </summary>
    /// <param name="o">The object, or <c>null</c>.</param>
    /// <returns>The converted value; never <c>null</c>.</returns>
    public static ScriptValue FromObject(object? o) => ConvertObject(o, 0);

    private static ScriptValue ConvertObject(object? o, int depth)
    {
        if (depth > MaxObjectDepth)
            return Null;

        switch (o)
        {
            case null:
                return Null;
            case ScriptValue sv:
                return sv;
            case bool b:
                return From(b);
            case string s:
                return From(s);
            case char c:
                return From(c.ToString());
            case double d:
                return Num(d);
            case float f:
                return Num(f);
            case int i:
                return From((double)i);
            case long l:
                return From((double)l);
            case uint ui:
                return From((double)ui);
            case ulong ul:
                return From((double)ul);
            case short sh:
                return From((double)sh);
            case ushort us:
                return From((double)us);
            case byte by:
                return From((double)by);
            case sbyte sb:
                return From((double)sb);
            case decimal m:
                return From((double)m);
            case Enum e:
                return From(e.ToString());
            case Guid g:
                return From(g.ToString("D"));
            case byte[] bytes:
                return From(bytes);
            case ReadOnlyMemory<byte> rom:
                return From(rom);
            case Memory<byte> mem:
                return From((ReadOnlyMemory<byte>)mem);
            case ScriptCallable callable:
                return Function(callable);
        }

        if (ReflectionCache.TryGetStringEntries(o, out var entries))
        {
            var map = new Dictionary<string, ScriptValue>(entries.Count, StringComparer.Ordinal);
            foreach (var pair in entries)
                map[pair.Key] = ConvertObject(pair.Value, depth + 1);
            return new ScriptValue(ScriptValueKind.Map, 0d, new ReadOnlyDictionary<string, ScriptValue>(map));
        }

        if (o is IEnumerable enumerable)
        {
            var items = new List<ScriptValue>();
            foreach (var item in enumerable)
                items.Add(ConvertObject(item, depth + 1));
            return new ScriptValue(ScriptValueKind.List, 0d, items.AsReadOnly());
        }

        var accessors = ReflectionCache.GetAccessors(o.GetType());
        var props = new Dictionary<string, ScriptValue>(accessors.Length, StringComparer.Ordinal);
        foreach (var accessor in accessors)
        {
            var raw = accessor.Read(o, out var faulted);
            props[accessor.Key] = faulted ? Null : ConvertObject(raw, depth + 1);
        }

        return new ScriptValue(ScriptValueKind.Map, 0d, new ReadOnlyDictionary<string, ScriptValue>(props));
    }

    private static ScriptValue Num(double d) => From(double.IsFinite(d) ? d : 0d);

    // ---------------------------------------------------------------- equality

    /// <summary>
    ///     Structural equality: same kind and payload; lists element-wise, maps by key set and values
    ///     (insertion order is ignored), bytes by content, numbers by <see cref="double.Equals(double)"/>,
    ///     handles by text, functions by reference.
    /// </summary>
    /// <param name="other">The other value.</param>
    /// <returns>Whether they are equal.</returns>
    public bool Equals(ScriptValue? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || Kind != other.Kind)
            return false;

        switch (Kind)
        {
            case ScriptValueKind.Null:
                return true;
            case ScriptValueKind.Bool:
                return (_number != 0d) == (other._number != 0d);
            case ScriptValueKind.Number:
                return _number.Equals(other._number);
            case ScriptValueKind.String:
            case ScriptValueKind.Handle:
                return string.Equals((string)_ref!, (string)other._ref!, StringComparison.Ordinal);
            case ScriptValueKind.Bytes:
                return ((byte[])_ref!).AsSpan().SequenceEqual((byte[])other._ref!);
            case ScriptValueKind.Function:
                return ReferenceEquals(_ref, other._ref);
            case ScriptValueKind.List:
            {
                var a = (IReadOnlyList<ScriptValue>)_ref!;
                var b = (IReadOnlyList<ScriptValue>)other._ref!;
                if (a.Count != b.Count)
                    return false;
                for (var i = 0; i < a.Count; i++)
                {
                    if (!a[i].Equals(b[i]))
                        return false;
                }

                return true;
            }

            case ScriptValueKind.Map:
            {
                var a = (IReadOnlyDictionary<string, ScriptValue>)_ref!;
                var b = (IReadOnlyDictionary<string, ScriptValue>)other._ref!;
                if (a.Count != b.Count)
                    return false;
                foreach (var pair in a)
                {
                    if (!b.TryGetValue(pair.Key, out var bv) || !pair.Value.Equals(bv))
                        return false;
                }

                return true;
            }

            default:
                return false;
        }
    }

    /// <inheritdoc cref="Equals(ScriptValue?)" />
    public override bool Equals(object? obj) => obj is ScriptValue other && Equals(other);

    /// <summary>A hash consistent with <see cref="Equals(ScriptValue?)"/> (map hashes ignore entry order).</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        switch (Kind)
        {
            case ScriptValueKind.Null:
                return 0;
            case ScriptValueKind.Bool:
                return _number != 0d ? 1 : 2;
            case ScriptValueKind.Number:
                return HashCode.Combine(ScriptValueKind.Number, _number);
            case ScriptValueKind.String:
            case ScriptValueKind.Handle:
                return HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode((string)_ref!));
            case ScriptValueKind.Bytes:
            {
                var hash = new HashCode();
                hash.Add(ScriptValueKind.Bytes);
                hash.AddBytes((byte[])_ref!);
                return hash.ToHashCode();
            }

            case ScriptValueKind.Function:
                return HashCode.Combine(ScriptValueKind.Function, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_ref));
            case ScriptValueKind.List:
            {
                var hash = new HashCode();
                hash.Add(ScriptValueKind.List);
                foreach (var item in (IReadOnlyList<ScriptValue>)_ref!)
                    hash.Add(item.GetHashCode());
                return hash.ToHashCode();
            }

            case ScriptValueKind.Map:
            {
                var sum = 0;
                foreach (var pair in (IReadOnlyDictionary<string, ScriptValue>)_ref!)
                    sum = unchecked(sum + HashCode.Combine(StringComparer.Ordinal.GetHashCode(pair.Key), pair.Value.GetHashCode()));
                return HashCode.Combine(ScriptValueKind.Map, sum);
            }

            default:
                return 0;
        }
    }

    /// <summary>The JSON form (<see cref="ToJson"/>).</summary>
    /// <returns>The JSON text.</returns>
    public override string ToString() => ToJson();
}
