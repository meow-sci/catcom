using System.Text.Json;
using KsaScripting.Core.Snapshots;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Tests.Values;

[TestFixture]
public sealed class ScriptValueTests
{
    private enum Color
    {
        Red,
        Green,
    }

    private sealed class Booby
    {
        public int Ok => 1;

        public int Boom => throw new InvalidOperationException("getter failed");

        public int this[int index] => index;

        public static int StaticValue => 5;
    }

    private static ScriptCallable Callable(long token = 7, string? name = "f")
        => ScriptCallable.Create("lua", token, name, _ => { });

    private static ScriptValue Dict(params (string Key, ScriptValue Value)[] entries)
    {
        var map = new Dictionary<string, ScriptValue>();
        foreach (var (key, value) in entries)
            map[key] = value;
        return ScriptValue.Map(map);
    }

    // ---------------------------------------------------------------- From / As

    [Test]
    public void Null_IsNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptValue.Null.Kind, Is.EqualTo(ScriptValueKind.Null));
            Assert.That(ScriptValue.Null.IsNull, Is.True);
            Assert.That(ScriptValue.From((string?)null), Is.SameAs(ScriptValue.Null));
        });
    }

    [Test]
    public void Bool_RoundTrips_AndTrueFalseAreCached()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptValue.From(true).Kind, Is.EqualTo(ScriptValueKind.Bool));
            Assert.That(ScriptValue.From(true).AsBool(), Is.True);
            Assert.That(ScriptValue.From(false).AsBool(), Is.False);
            Assert.That(ScriptValue.From(true), Is.SameAs(ScriptValue.True));
            Assert.That(ScriptValue.From(false), Is.SameAs(ScriptValue.False));
        });
    }

    [Test]
    public void Number_RoundTrips()
    {
        var v = ScriptValue.From(3.25);
        Assert.Multiple(() =>
        {
            Assert.That(v.Kind, Is.EqualTo(ScriptValueKind.Number));
            Assert.That(v.AsNumber(), Is.EqualTo(3.25));
        });
    }

    [Test]
    public void String_RoundTrips()
    {
        var v = ScriptValue.From("héllo");
        Assert.Multiple(() =>
        {
            Assert.That(v.Kind, Is.EqualTo(ScriptValueKind.String));
            Assert.That(v.AsString(), Is.EqualTo("héllo"));
        });
    }

    [Test]
    public void Bytes_RoundTrip_AndAreCopied()
    {
        byte[] source = [1, 2, 3];
        var v = ScriptValue.From(new ReadOnlyMemory<byte>(source));
        source[0] = 99;
        Assert.Multiple(() =>
        {
            Assert.That(v.Kind, Is.EqualTo(ScriptValueKind.Bytes));
            Assert.That(v.AsBytes().ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        });
    }

    [Test]
    public void List_RoundTrips_AndIsACopy()
    {
        var source = new List<ScriptValue> { ScriptValue.From(1), ScriptValue.From("a") };
        var v = ScriptValue.List(source);
        source.Add(ScriptValue.Null);
        Assert.Multiple(() =>
        {
            Assert.That(v.Kind, Is.EqualTo(ScriptValueKind.List));
            Assert.That(v.AsList(), Has.Count.EqualTo(2));
            Assert.That(v.AsList()[1].AsString(), Is.EqualTo("a"));
        });
    }

    [Test]
    public void Map_RoundTrips_KeepsInsertionOrder_AndIsACopy()
    {
        var source = new Dictionary<string, ScriptValue>
        {
            ["z"] = ScriptValue.From(1),
            ["a"] = ScriptValue.From(2),
            ["m"] = ScriptValue.From(3),
        };
        var v = ScriptValue.Map(source);
        source["extra"] = ScriptValue.Null;
        Assert.Multiple(() =>
        {
            Assert.That(v.Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(v.AsMap().Keys, Is.EqualTo(new[] { "z", "a", "m" }));
            Assert.That(v.AsMap()["a"].AsNumber(), Is.EqualTo(2));
        });
    }

    [Test]
    public void Function_And_Handle_RoundTrip()
    {
        var callable = Callable();
        var fn = ScriptValue.Function(callable);
        var handle = ScriptValue.Handle("vessel:abc");
        Assert.Multiple(() =>
        {
            Assert.That(fn.Kind, Is.EqualTo(ScriptValueKind.Function));
            Assert.That(fn.AsFunction(), Is.SameAs(callable));
            Assert.That(handle.Kind, Is.EqualTo(ScriptValueKind.Handle));
            Assert.That(handle.AsHandle(), Is.EqualTo("vessel:abc"));
        });
    }

    [Test]
    public void AsWrongKind_ThrowsInvalidOperation_NamingBothKinds()
    {
        var number = ScriptValue.From(1);
        Assert.Multiple(() =>
        {
            Assert.That(() => number.AsString(), Throws.InvalidOperationException.With.Message.EqualTo("expected String, got Number"));
            Assert.That(() => number.AsBool(), Throws.InvalidOperationException);
            Assert.That(() => number.AsBytes(), Throws.InvalidOperationException);
            Assert.That(() => number.AsList(), Throws.InvalidOperationException);
            Assert.That(() => number.AsMap(), Throws.InvalidOperationException);
            Assert.That(() => number.AsFunction(), Throws.InvalidOperationException);
            Assert.That(() => number.AsHandle(), Throws.InvalidOperationException);
            Assert.That(() => ScriptValue.Null.AsNumber(), Throws.InvalidOperationException.With.Message.EqualTo("expected Number, got Null"));
        });
    }

    // ---------------------------------------------------------------- truthiness

    private static IEnumerable<TestCaseData> TruthyCases()
    {
        yield return new TestCaseData(ScriptValue.Null, false).SetArgDisplayNames("null");
        yield return new TestCaseData(ScriptValue.True, true).SetArgDisplayNames("true");
        yield return new TestCaseData(ScriptValue.False, false).SetArgDisplayNames("false");
        yield return new TestCaseData(ScriptValue.From(0), false).SetArgDisplayNames("0");
        yield return new TestCaseData(ScriptValue.From(-0.0), false).SetArgDisplayNames("-0");
        yield return new TestCaseData(ScriptValue.From(double.NaN), false).SetArgDisplayNames("NaN");
        yield return new TestCaseData(ScriptValue.From(1), true).SetArgDisplayNames("1");
        yield return new TestCaseData(ScriptValue.From(double.PositiveInfinity), true).SetArgDisplayNames("Infinity");
        yield return new TestCaseData(ScriptValue.From(""), false).SetArgDisplayNames("empty string");
        yield return new TestCaseData(ScriptValue.From("x"), true).SetArgDisplayNames("string");
        yield return new TestCaseData(ScriptValue.From(ReadOnlyMemory<byte>.Empty), true).SetArgDisplayNames("empty bytes");
        yield return new TestCaseData(ScriptValue.List([]), true).SetArgDisplayNames("empty list");
        yield return new TestCaseData(ScriptValue.Map(new Dictionary<string, ScriptValue>()), true).SetArgDisplayNames("empty map");
        yield return new TestCaseData(ScriptValue.Function(Callable()), true).SetArgDisplayNames("function");
        yield return new TestCaseData(ScriptValue.Handle(""), true).SetArgDisplayNames("handle");
    }

    [TestCaseSource(nameof(TruthyCases))]
    public void IsTruthy_FollowsTheTable(ScriptValue value, bool expected)
        => Assert.That(value.IsTruthy, Is.EqualTo(expected));

    // ---------------------------------------------------------------- JSON

    private static ScriptValue Nested() => Dict(
        ("nothing", ScriptValue.Null),
        ("flag", ScriptValue.True),
        ("num", ScriptValue.From(1.5)),
        ("text", ScriptValue.From("he said \"hi\" → ok")),
        ("bytes", ScriptValue.From(new byte[] { 1, 2, 3 })),
        ("handle", ScriptValue.Handle("vessel:x")),
        ("list", ScriptValue.List([ScriptValue.From(1), ScriptValue.From("a"), ScriptValue.Null])),
        ("map", Dict(("k", ScriptValue.List([ScriptValue.True])))));

    [Test]
    public void ToJson_WritesTheDocumentedShapes_InInsertionOrder()
    {
        Assert.That(Nested().ToJson(), Is.EqualTo(
            "{\"nothing\":null,\"flag\":true,\"num\":1.5,\"text\":\"he said \\\"hi\\\" → ok\","
            + "\"bytes\":{\"$bytes\":\"AQID\"},\"handle\":{\"$handle\":\"vessel:x\"},"
            + "\"list\":[1,\"a\",null],\"map\":{\"k\":[true]}}"));
    }

    [Test]
    public void FromJson_InvertsToJson()
    {
        var original = Nested();
        var parsed = ScriptValue.FromJson(original.ToJson());
        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.EqualTo(original));
            Assert.That(parsed["bytes"].Kind, Is.EqualTo(ScriptValueKind.Bytes));
            Assert.That(parsed["handle"].Kind, Is.EqualTo(ScriptValueKind.Handle));
        });
    }

    [Test]
    public void ToJson_NonFiniteNumbers_WriteZero()
    {
        var list = ScriptValue.List([
            ScriptValue.From(double.NaN),
            ScriptValue.From(double.PositiveInfinity),
            ScriptValue.From(double.NegativeInfinity),
        ]);
        Assert.Multiple(() =>
        {
            Assert.That(ScriptValue.From(double.NaN).ToJson(), Is.EqualTo("0"));
            Assert.That(list.ToJson(), Is.EqualTo("[0,0,0]"));
        });
    }

    [Test]
    public void ToJson_Function_WritesTheToken()
        => Assert.That(ScriptValue.Function(Callable(token: 42)).ToJson(), Is.EqualTo("{\"$fn\":42}"));

    [Test]
    public void FromJson_FnObject_StaysAMap()
    {
        var v = ScriptValue.FromJson("{\"$fn\":42}");
        Assert.Multiple(() =>
        {
            Assert.That(v.Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(v["$fn"].AsNumber(), Is.EqualTo(42));
        });
    }

    [Test]
    public void FromJson_SpecialObjects_OnlyWhenExactlyOneKeyOfTheRightType()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptValue.FromJson("{\"$bytes\":\"AQID\"}").Kind, Is.EqualTo(ScriptValueKind.Bytes));
            Assert.That(ScriptValue.FromJson("{\"$handle\":\"h\"}").AsHandle(), Is.EqualTo("h"));
            Assert.That(ScriptValue.FromJson("{\"$bytes\":1}").Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(ScriptValue.FromJson("{\"$bytes\":\"not base64!\"}").Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(ScriptValue.FromJson("{\"$handle\":\"h\",\"other\":1}").Kind, Is.EqualTo(ScriptValueKind.Map));
        });
    }

    [Test]
    public void FromJson_Scalars_AndMalformed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptValue.FromJson("null").IsNull, Is.True);
            Assert.That(ScriptValue.FromJson("false"), Is.SameAs(ScriptValue.False));
            Assert.That(ScriptValue.FromJson("12").AsNumber(), Is.EqualTo(12d));
            Assert.That(ScriptValue.FromJson("\"s\"").AsString(), Is.EqualTo("s"));
            Assert.That(ScriptValue.FromJson("[]").AsList(), Is.Empty);
            Assert.That(() => ScriptValue.FromJson("{nope"), Throws.InstanceOf<JsonException>());
        });
    }

    [Test]
    public void ToString_IsTheJson()
        => Assert.That(Nested().ToString(), Is.EqualTo(Nested().ToJson()));

    // ---------------------------------------------------------------- FromObject

    [Test]
    public void FromObject_VesselSnapshot_UsesCamelCaseKeys()
    {
        var v = ScriptValue.FromObject(TestData.Vessel());
        Assert.Multiple(() =>
        {
            Assert.That(v.Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(v["id"].AsString(), Is.EqualTo("test-1"));
            Assert.That(v["positionCci"]["x"].AsNumber(), Is.EqualTo(1234.5));
            Assert.That(v["positionCci"]["y"].AsNumber(), Is.EqualTo(-6789.25));
            Assert.That(v["positionCci"]["z"].AsNumber(), Is.EqualTo(42));
            Assert.That(v["engines"].AsList(), Has.Count.EqualTo(2));
            Assert.That(v["engines"].AsList()[0]["index"].AsNumber(), Is.EqualTo(0));
            Assert.That(v["engines"].AsList()[0]["active"].AsBool(), Is.True);
            Assert.That(v["engines"].AsList()[1]["active"].AsBool(), Is.False);
            Assert.That(v["batteryChargeFraction"].AsNumber(), Is.EqualTo(0.87));
            Assert.That(v["orbit"]["eccentricity"].AsNumber(), Is.EqualTo(0.0012));
        });
    }

    [Test]
    public void FromObject_NullableNull_IsNull()
    {
        var v = ScriptValue.FromObject(TestData.Vessel(battery: null, withOrbit: false));
        Assert.Multiple(() =>
        {
            Assert.That(v["batteryChargeFraction"].IsNull, Is.True);
            Assert.That(v["orbit"].IsNull, Is.True);
            Assert.That(v.AsMap().ContainsKey("orbit"), Is.True, "the key is present with a Null value");
        });
    }

    [Test]
    public void FromObject_Primitives()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptValue.FromObject(null).IsNull, Is.True);
            Assert.That(ScriptValue.FromObject(true), Is.SameAs(ScriptValue.True));
            Assert.That(ScriptValue.FromObject(5).AsNumber(), Is.EqualTo(5d));
            Assert.That(ScriptValue.FromObject(5L).AsNumber(), Is.EqualTo(5d));
            Assert.That(ScriptValue.FromObject((byte)5).AsNumber(), Is.EqualTo(5d));
            Assert.That(ScriptValue.FromObject((sbyte)-5).AsNumber(), Is.EqualTo(-5d));
            Assert.That(ScriptValue.FromObject((short)-5).AsNumber(), Is.EqualTo(-5d));
            Assert.That(ScriptValue.FromObject((ushort)5).AsNumber(), Is.EqualTo(5d));
            Assert.That(ScriptValue.FromObject(5u).AsNumber(), Is.EqualTo(5d));
            Assert.That(ScriptValue.FromObject(5UL).AsNumber(), Is.EqualTo(5d));
            Assert.That(ScriptValue.FromObject(2.5f).AsNumber(), Is.EqualTo(2.5d));
            Assert.That(ScriptValue.FromObject(2.5m).AsNumber(), Is.EqualTo(2.5d));
            Assert.That(ScriptValue.FromObject("s").AsString(), Is.EqualTo("s"));
            Assert.That(ScriptValue.FromObject('c').AsString(), Is.EqualTo("c"));
        });
    }

    [Test]
    public void FromObject_NonFiniteNumbers_BecomeZero()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScriptValue.FromObject(float.NaN).AsNumber(), Is.Zero);
            Assert.That(ScriptValue.FromObject(double.NaN).AsNumber(), Is.Zero);
            Assert.That(ScriptValue.FromObject(double.PositiveInfinity).AsNumber(), Is.Zero);
            Assert.That(ScriptValue.FromObject(double.NegativeInfinity).AsNumber(), Is.Zero);
            Assert.That(ScriptValue.FromObject(new[] { 1.0, double.NaN }).AsList()[1].AsNumber(), Is.Zero);
        });
    }

    [Test]
    public void FromObject_EnumGuidBytes()
    {
        var guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Multiple(() =>
        {
            Assert.That(ScriptValue.FromObject(Color.Green).AsString(), Is.EqualTo("Green"));
            Assert.That(ScriptValue.FromObject(guid).AsString(), Is.EqualTo("0f8fad5b-d9cb-469f-a165-70867728950e"));
            Assert.That(ScriptValue.FromObject(new byte[] { 9, 8 }).AsBytes().ToArray(), Is.EqualTo(new byte[] { 9, 8 }));
            Assert.That(ScriptValue.FromObject(new ReadOnlyMemory<byte>([7])).Kind, Is.EqualTo(ScriptValueKind.Bytes));
            Assert.That(ScriptValue.FromObject(new Memory<byte>([7])).Kind, Is.EqualTo(ScriptValueKind.Bytes));
        });
    }

    [Test]
    public void FromObject_ScriptValueAndCallable_PassThrough()
    {
        var value = ScriptValue.From(1);
        var callable = Callable();
        Assert.Multiple(() =>
        {
            Assert.That(ScriptValue.FromObject(value), Is.SameAs(value));
            Assert.That(ScriptValue.FromObject(callable).AsFunction(), Is.SameAs(callable));
        });
    }

    [Test]
    public void FromObject_Dictionaries_KeepKeysUnchanged()
    {
        var typed = ScriptValue.FromObject(new Dictionary<string, int> { ["Alpha"] = 1, ["beta_two"] = 2 });
        var readOnly = ScriptValue.FromObject(new ReadOnlyOnlyDictionary(new Dictionary<string, int> { ["K"] = 3 }));
        Assert.Multiple(() =>
        {
            Assert.That(typed.Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(typed.AsMap().Keys, Is.EqualTo(new[] { "Alpha", "beta_two" }));
            Assert.That(typed["beta_two"].AsNumber(), Is.EqualTo(2));
            Assert.That(readOnly.Kind, Is.EqualTo(ScriptValueKind.Map));
            Assert.That(readOnly["K"].AsNumber(), Is.EqualTo(3));
        });
    }

    [Test]
    public void FromObject_Enumerables_BecomeLists()
    {
        var v = ScriptValue.FromObject(new List<object?> { 1, "a", null, new[] { 2, 3 } });
        Assert.Multiple(() =>
        {
            Assert.That(v.Kind, Is.EqualTo(ScriptValueKind.List));
            Assert.That(v.AsList(), Has.Count.EqualTo(4));
            Assert.That(v.AsList()[2].IsNull, Is.True);
            Assert.That(v.AsList()[3].AsList()[1].AsNumber(), Is.EqualTo(3));
            Assert.That(ScriptValue.FromObject(Enumerable.Range(0, 3)).AsList(), Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void FromObject_ReadonlyRecordStruct_BecomesMap()
    {
        var v = ScriptValue.FromObject(new double3Snap(1, 2, 3));
        Assert.Multiple(() =>
        {
            Assert.That(v.AsMap().Keys, Is.EqualTo(new[] { "x", "y", "z" }));
            Assert.That(v["y"].AsNumber(), Is.EqualTo(2));
        });
    }

    [Test]
    public void FromObject_ThrowingGetter_IsNull_IndexersAndStaticsAreSkipped()
    {
        var v = ScriptValue.FromObject(new Booby());
        Assert.Multiple(() =>
        {
            Assert.That(v.AsMap().Keys, Is.EqualTo(new[] { "ok", "boom" }));
            Assert.That(v["ok"].AsNumber(), Is.EqualTo(1));
            Assert.That(v["boom"].IsNull, Is.True);
        });
    }

    [Test]
    public void FromObject_DepthIsLimited_DeeperIsNull()
    {
        object chain = 1;
        for (var i = 0; i < 50; i++)
            chain = new[] { chain };

        var v = ScriptValue.FromObject(chain);
        var depth = 0;
        while (v.Kind == ScriptValueKind.List)
        {
            v = v.AsList()[0];
            depth++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(v.IsNull, Is.True, "cut off with Null rather than reaching the number");
            Assert.That(depth, Is.InRange(30, 40));
        });
    }

    // ---------------------------------------------------------------- equality

    [Test]
    public void Equals_Maps_IgnoreInsertionOrder_AndHashesAgree()
    {
        var a = Dict(("x", ScriptValue.From(1)), ("y", ScriptValue.List([ScriptValue.True])));
        var b = Dict(("y", ScriptValue.List([ScriptValue.True])), ("x", ScriptValue.From(1)));
        var c = Dict(("x", ScriptValue.From(2)), ("y", ScriptValue.List([ScriptValue.True])));
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a, Is.Not.EqualTo(c));
            Assert.That(a.Equals((object)b), Is.True);
            Assert.That(a.Equals(null), Is.False);
        });
    }

    [Test]
    public void Equals_Lists_AreOrderSensitiveAndDeep()
    {
        var a = ScriptValue.List([ScriptValue.From(1), ScriptValue.From(2)]);
        var b = ScriptValue.List([ScriptValue.From(1), ScriptValue.From(2)]);
        var reversed = ScriptValue.List([ScriptValue.From(2), ScriptValue.From(1)]);
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a, Is.Not.EqualTo(reversed));
            Assert.That(a, Is.Not.EqualTo(ScriptValue.List([ScriptValue.From(1)])));
        });
    }

    [Test]
    public void Equals_ByKind()
    {
        var callable = Callable();
        var nan1 = ScriptValue.From(double.NaN);
        var nan2 = ScriptValue.From(double.NaN);
        var text1 = ScriptValue.From("a");
        var text2 = ScriptValue.From("a");
        var handle1 = ScriptValue.Handle("a");
        var handle2 = ScriptValue.Handle("a");
        var bytes1 = ScriptValue.From(new byte[] { 1, 2 });
        var bytes2 = ScriptValue.From(new byte[] { 1, 2 });
        var bytes3 = ScriptValue.From(new byte[] { 1, 3 });
        var fn1 = ScriptValue.Function(callable);
        var fn2 = ScriptValue.Function(callable);
        var fnOther = ScriptValue.Function(Callable());
        Assert.Multiple(() =>
        {
            Assert.That(ScriptValue.Null, Is.EqualTo(ScriptValue.FromJson("null")));
            Assert.That(nan1, Is.EqualTo(nan2));
            Assert.That(ScriptValue.From(1), Is.Not.EqualTo(ScriptValue.From("1")));
            Assert.That(text1, Is.EqualTo(text2));
            Assert.That(text1, Is.Not.EqualTo(handle1));
            Assert.That(handle1, Is.EqualTo(handle2));
            Assert.That(bytes1, Is.EqualTo(bytes2));
            Assert.That(bytes1, Is.Not.EqualTo(bytes3));
            Assert.That(bytes1.GetHashCode(), Is.EqualTo(bytes2.GetHashCode()));
            Assert.That(fn1, Is.EqualTo(fn2));
            Assert.That(fn1, Is.Not.EqualTo(fnOther), "functions compare by reference");
        });
    }

    // ---------------------------------------------------------------- lookup helpers

    [Test]
    public void IndexerAndTryGet_OnMaps()
    {
        var map = Dict(("a", ScriptValue.From(1)));
        Assert.Multiple(() =>
        {
            Assert.That(map["a"].AsNumber(), Is.EqualTo(1));
            Assert.That(map["missing"].IsNull, Is.True);
            Assert.That(map.TryGet("a", out var found), Is.True);
            Assert.That(found.AsNumber(), Is.EqualTo(1));
            Assert.That(map.TryGet("missing", out var absent), Is.False);
            Assert.That(absent.IsNull, Is.True);
        });
    }

    [Test]
    public void IndexerAndTryGet_OnNonMaps_AreNullAndFalse()
    {
        var list = ScriptValue.List([ScriptValue.From(1)]);
        Assert.Multiple(() =>
        {
            Assert.That(list["0"].IsNull, Is.True);
            Assert.That(list.TryGet("0", out _), Is.False);
            Assert.That(ScriptValue.Null["a"].IsNull, Is.True);
            Assert.That(ScriptValue.From("s").TryGet("a", out _), Is.False);
        });
    }

    /// <summary>A read-only dictionary that is NOT an <see cref="System.Collections.IDictionary"/>.</summary>
    private sealed class ReadOnlyOnlyDictionary(Dictionary<string, int> inner) : IReadOnlyDictionary<string, int>
    {
        public int this[string key] => inner[key];

        public IEnumerable<string> Keys => inner.Keys;

        public IEnumerable<int> Values => inner.Values;

        public int Count => inner.Count;

        public bool ContainsKey(string key) => inner.ContainsKey(key);

        public bool TryGetValue(string key, out int value) => inner.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
