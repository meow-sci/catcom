using Catcom.Core.Values;

namespace Catcom.Runtime.Lua.Tests;

[TestFixture]
public sealed class ValueMarshallingTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp() => _fx = new LuaRuntimeFixture();

    [TearDown]
    public void TearDown() => _fx.Dispose();

    private ScriptValue Eval(string expression) => _fx.Call($"return {{ f = function() return {expression} end }}", "f");

    [Test]
    public void ProperSequence_BecomesList()
    {
        var value = Eval("{ 10, 20, 30 }");

        Assert.That(value.Kind, Is.EqualTo(ScriptValueKind.List));
        Assert.That(value.AsList().Select(v => v.AsNumber()), Is.EqualTo(new[] { 10d, 20d, 30d }));
    }

    [Test]
    public void EmptyTable_BecomesMap()
    {
        var value = Eval("{}");

        Assert.That(value.Kind, Is.EqualTo(ScriptValueKind.Map));
        Assert.That(value.AsMap(), Is.Empty);
    }

    [Test]
    public void MixedTable_BecomesMap_WithNumberKeysAsInvariantStrings()
    {
        var map = Eval("{ 1, 2, x = 3, [2.5] = 4 }").AsMap();

        Assert.That(map.Keys, Is.EquivalentTo(new[] { "1", "2", "x", "2.5" }));
        Assert.That(map["x"].AsNumber(), Is.EqualTo(3d));
        Assert.That(map["2.5"].AsNumber(), Is.EqualTo(4d));
    }

    [Test]
    public void SparseArray_BecomesMap()
    {
        var value = Eval("{ [1] = 'a', [3] = 'c' }");

        Assert.That(value.Kind, Is.EqualTo(ScriptValueKind.Map));
        Assert.That(value.AsMap().Keys, Is.EquivalentTo(new[] { "1", "3" }));
    }

    [Test]
    public void KeysOfOtherTypes_AreSkipped()
    {
        var map = Eval("{ [true] = 1, [{}] = 2, [print] = 3, keep = 4 }").AsMap();

        Assert.That(map.Keys, Is.EquivalentTo(new[] { "keep" }));
    }

    [Test]
    public void Cycles_BecomeNull_AtTheRepeat()
    {
        var value = _fx.Call("return { f = function() local t = { name = 'root' } t.self = t t.list = { t, t } return t end }", "f").AsMap();

        Assert.That(value["name"].AsString(), Is.EqualTo("root"));
        Assert.That(value["self"].IsNull, Is.True);
        Assert.That(value["list"].AsList().All(v => v.IsNull), Is.True);
    }

    [Test]
    public void SharedSubtables_AreConvertedEachTime()
    {
        var value = _fx.Call("return { f = function() local shared = { 1, 2 } return { a = shared, b = shared } end }", "f").AsMap();

        Assert.That(value["a"], Is.EqualTo(value["b"]));
        Assert.That(value["a"].Kind, Is.EqualTo(ScriptValueKind.List));
    }

    [Test]
    public void Depth_IsLimitedTo32()
    {
        var value = _fx.Call("return { f = function() local t = {} local cur = t for i = 1, 40 do cur.next = { depth = i } cur = cur.next end return t end }", "f");

        var depth = 0;
        var current = value;
        while (current.Kind == ScriptValueKind.Map && current.AsMap().TryGetValue("next", out var next))
        {
            depth++;
            current = next;
        }

        Assert.That(depth, Is.EqualTo(32));
    }

    [Test]
    public void ExplodingDagIsBounded()
    {
        // 2^40 paths through shared sub-tables must not hang the host.
        var value = _fx.Call("return { f = function() local t = {1} for i = 1, 40 do t = { t, t } end return t end }", "f");

        Assert.That(value.Kind, Is.EqualTo(ScriptValueKind.List));
    }

    [Test]
    public void UserdataThreadsAndOtherTypes_BecomeNull()
    {
        var map = Eval("{ co = coroutine.create(function() end), nothing = nil }").AsMap();

        Assert.That(map.ContainsKey("co"), Is.True);
        Assert.That(map["co"].IsNull, Is.True);
    }

    [Test]
    public void Strings_AreUtf8_AndInvalidBytesBecomeReplacementChars()
    {
        Assert.That(Eval("'h\\195\\169llo \\226\\156\\147'").AsString(), Is.EqualTo("héllo ✓"));
        Assert.That(Eval("'\\255A'").AsString(), Is.EqualTo("�A"));
        Assert.That(Eval("'a\\0b'").AsString(), Is.EqualTo("a\0b"));
    }

    [Test]
    public void LongStrings_RoundTrip()
    {
        var text = string.Concat(Enumerable.Repeat("ü✓x", 600));
        var module = _fx.Load("echo", "return { f = function(x) return x end }");

        var result = _fx.Runtime.Invoke(_fx.Export(module, "f"), [ScriptValue.From(text)]);

        Assert.That(result.AsString(), Is.EqualTo(text));
    }

    [Test]
    public void Bytes_ArrivePerByteAsALuaString()
    {
        var module = _fx.Load("bytes", "return { f = function(x) return { type(x), #x, x:byte(1, -1) } end }");

        var result = _fx.Runtime.Invoke(_fx.Export(module, "f"), [ScriptValue.From(new ReadOnlyMemory<byte>([0xFF, 0x00, 0x41]))]);

        Assert.That(result.AsList().Select(v => v.Kind == ScriptValueKind.String ? v.AsString() : v.AsNumber().ToString()),
            Is.EqualTo(new[] { "string", "3", "255", "0", "65" }));
    }

    [Test]
    public void Handle_ArrivesAsString()
    {
        var module = _fx.Load("handle", "return { f = function(x) return x end }");

        var result = _fx.Runtime.Invoke(_fx.Export(module, "f"), [ScriptValue.Handle("vessel:abc")]);

        Assert.That(result.AsString(), Is.EqualTo("vessel:abc"));
    }

    [Test]
    public void SpecialNumbers_PassThrough()
    {
        var module = _fx.Load("numbers", "return { f = function(x) return x end, nan = function() return 0/0 end }");

        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "f"), [ScriptValue.From(double.PositiveInfinity)]).AsNumber(), Is.EqualTo(double.PositiveInfinity));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "f"), [ScriptValue.From(-0.0)]).AsNumber(), Is.EqualTo(0d));
        Assert.That(double.IsNaN(_fx.Runtime.Invoke(_fx.Export(module, "nan"), []).AsNumber()), Is.True);
    }

    [Test]
    public void FunctionValues_RoundTripAsCallables()
    {
        var module = _fx.Load("fn", "return { make = function() local n = 0 return function() n = n + 1 return n end end, call = function(f) return f() * 10 end }");

        var counter = _fx.Runtime.Invoke(_fx.Export(module, "make"), []);

        Assert.That(counter.Kind, Is.EqualTo(ScriptValueKind.Function));
        var fn = counter.AsFunction();
        Assert.That(fn.RuntimeId, Is.EqualTo("lua"));
        Assert.That(_fx.Runtime.Invoke(fn, []).AsNumber(), Is.EqualTo(1d));
        Assert.That(_fx.Runtime.Invoke(fn, []).AsNumber(), Is.EqualTo(2d));
        // Passing the callable back in arrives as the very same Lua function.
        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "call"), [counter]).AsNumber(), Is.EqualTo(30d));
    }

    [Test]
    public void ForeignOrDisposedCallables_ArriveAsNil()
    {
        var module = _fx.Load("nilfn", "return { f = function(x) return type(x) end }");
        var foreign = ScriptCallable.Create("v8", 1, "foreign", _ => { });
        var disposed = _fx.Export(module, "f");
        disposed.Dispose();

        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "f"), [ScriptValue.Function(foreign)]).AsString(), Is.EqualTo("nil"));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "f"), [ScriptValue.Function(disposed)]).AsString(), Is.EqualTo("nil"));
    }

    [Test]
    public void ManyArguments_AndResults()
    {
        var module = _fx.Load("varargs", "return { f = function(...) return select('#', ...) end }");
        var args = Enumerable.Range(0, 200).Select(i => ScriptValue.From((double)i)).ToArray();

        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "f"), args).AsNumber(), Is.EqualTo(200d));
    }

    [Test]
    public void MultipleReturnValues_YieldTheFirst()
    {
        Assert.That(Eval("1, 2, 3").AsNumber(), Is.EqualTo(1d));
        Assert.That(_fx.Call("return { f = function() end }", "f").IsNull, Is.True);
    }
}
