using Catcom.Core.Logging;
using Catcom.Core.Persistence;
using Catcom.Core.Tests.Support;
using Catcom.Core.Values;

namespace Catcom.Core.Tests.Persistence;

[TestFixture]
public sealed class ScriptStateStoreTests
{
    [Test]
    public void SetThenGet_ReturnsTheValue_AndMissingKeysAreNull()
    {
        var store = new ScriptStateStore();

        store.Set("a.lua", "count", ScriptValue.From(3));

        Assert.Multiple(() =>
        {
            Assert.That(store.Get("a.lua", "count").AsNumber(), Is.EqualTo(3));
            Assert.That(store.Get("a.lua", "missing").IsNull, Is.True);
            Assert.That(store.Get("b.lua", "count").IsNull, Is.True, "state is per script");
        });
    }

    [Test]
    public void SetToNull_RemovesTheKey()
    {
        var store = new ScriptStateStore();
        store.Set("a.lua", "k", ScriptValue.From("v"));

        store.Set("a.lua", "k", ScriptValue.Null);

        Assert.Multiple(() =>
        {
            Assert.That(store.Get("a.lua", "k").IsNull, Is.True);
            Assert.That(store.All("a.lua"), Is.Empty);
            Assert.That(store.SerializeToml(), Is.Empty, "a script without keys is omitted");
        });
    }

    [Test]
    public void FunctionValues_AreRejected_AtAnyDepth()
    {
        var store = new ScriptStateStore();
        var fn = ScriptCallable.Create("test", 1, "f", _ => { });

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() => store.Set("a.lua", "k", ScriptValue.Function(fn)));
            Assert.Throws<ArgumentException>(() => store.Set("a.lua", "k", ScriptValue.List([ScriptValue.From(1), ScriptValue.Function(fn)])));
            Assert.Throws<ArgumentException>(() => store.Set("a.lua", "k", TestRig.Map(("inner", TestRig.Map(("fn", ScriptValue.Function(fn)))))));
            Assert.That(store.All("a.lua"), Is.Empty);
        });
    }

    [Test]
    public void All_ReturnsASnapshotCopy_AndClearRemovesTheScript()
    {
        var store = new ScriptStateStore();
        store.Set("a.lua", "one", ScriptValue.From(1));
        var snapshot = store.All("a.lua");

        store.Set("a.lua", "two", ScriptValue.From(2));
        Assert.That(snapshot.Keys, Is.EqualTo(new[] { "one" }));

        store.Clear("a.lua");
        Assert.Multiple(() =>
        {
            Assert.That(store.All("a.lua"), Is.Empty);
            Assert.That(store.Get("a.lua", "one").IsNull, Is.True);
        });
    }

    [Test]
    public void SerializeToml_WritesOneTablePerScript_WithJsonValues_InOrdinalOrder()
    {
        var store = new ScriptStateStore();
        store.Set("b.ts", "z", ScriptValue.From("last"));
        store.Set("a.lua", "beta", ScriptValue.From(2));
        store.Set("a.lua", "alpha", ScriptValue.From(true));

        var toml = store.SerializeToml();

        Assert.Multiple(() =>
        {
            Assert.That(toml, Does.Contain("[scripts.\"a.lua\"]"));
            Assert.That(toml, Does.Contain("[scripts.\"b.ts\"]"));
            Assert.That(toml, Does.Contain("alpha = \"true\""));
            Assert.That(toml, Does.Contain("beta = \"2\""));
            Assert.That(toml, Does.Contain("z = \"\\\"last\\\"\""));
            Assert.That(toml.IndexOf("alpha", StringComparison.Ordinal), Is.LessThan(toml.IndexOf("beta", StringComparison.Ordinal)));
            Assert.That(toml.IndexOf("a.lua", StringComparison.Ordinal), Is.LessThan(toml.IndexOf("b.ts", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void SerializeToml_IsDeterministic_RegardlessOfInsertionOrder()
    {
        var first = new ScriptStateStore();
        first.Set("b.lua", "y", ScriptValue.From(1));
        first.Set("a.lua", "x", ScriptValue.From(2));
        first.Set("a.lua", "w", ScriptValue.From(3));

        var second = new ScriptStateStore();
        second.Set("a.lua", "w", ScriptValue.From(3));
        second.Set("a.lua", "x", ScriptValue.From(2));
        second.Set("b.lua", "y", ScriptValue.From(1));

        Assert.That(second.SerializeToml(), Is.EqualTo(first.SerializeToml()));
    }

    [Test]
    public void TomlRoundTrip_PreservesNestedMapsListsBytesHandlesAndUnicode()
    {
        var store = new ScriptStateStore();
        var nested = TestRig.Map(
            ("name", ScriptValue.From("Käpt'n \"Kitty\" 🚀")),
            ("list", ScriptValue.List([ScriptValue.From(1.5), ScriptValue.From(false), ScriptValue.Null, ScriptValue.From("x")])),
            ("deep", TestRig.Map(("bytes", ScriptValue.From(new byte[] { 0, 1, 2, 255 })), ("handle", ScriptValue.Handle("vessel:Kitty-1")))));
        store.Set("a.lua", "nested", nested);
        store.Set("a.lua", "bytes", ScriptValue.From(new byte[] { 9, 8, 7 }));
        store.Set("a.lua", "handle", ScriptValue.Handle("body:Kerbin"));
        store.Set("a.lua", "text", ScriptValue.From("line1\nline2\ttab \\ backslash"));
        store.Set("a.lua", "number", ScriptValue.From(-0.000123));

        var restored = new ScriptStateStore();
        restored.LoadToml(store.SerializeToml());

        Assert.Multiple(() =>
        {
            Assert.That(restored.Get("a.lua", "nested"), Is.EqualTo(nested));
            Assert.That(restored.Get("a.lua", "nested")["name"].AsString(), Is.EqualTo("Käpt'n \"Kitty\" 🚀"));
            Assert.That(restored.Get("a.lua", "bytes").AsBytes().ToArray(), Is.EqualTo(new byte[] { 9, 8, 7 }));
            Assert.That(restored.Get("a.lua", "handle").AsHandle(), Is.EqualTo("body:Kerbin"));
            Assert.That(restored.Get("a.lua", "text").AsString(), Is.EqualTo("line1\nline2\ttab \\ backslash"));
            Assert.That(restored.Get("a.lua", "number").AsNumber(), Is.EqualTo(-0.000123));
            Assert.That(restored.SerializeToml(), Is.EqualTo(store.SerializeToml()));
        });
    }

    [Test]
    public void TomlRoundTrip_ScriptNamesAndKeysWithSlashesAndDots()
    {
        var store = new ScriptStateStore();
        store.Set("dir/sub/a.lua", "key.with.dots", ScriptValue.From(1));
        store.Set("dir/sub/a.lua", "key/with/slashes", ScriptValue.From(2));
        store.Set("dir/b.ts", "plain", ScriptValue.From(3));
        store.Set("weird \"name\".lua", "k", ScriptValue.From(4));

        var restored = new ScriptStateStore();
        restored.LoadToml(store.SerializeToml());

        Assert.Multiple(() =>
        {
            Assert.That(restored.Get("dir/sub/a.lua", "key.with.dots").AsNumber(), Is.EqualTo(1));
            Assert.That(restored.Get("dir/sub/a.lua", "key/with/slashes").AsNumber(), Is.EqualTo(2));
            Assert.That(restored.Get("dir/b.ts", "plain").AsNumber(), Is.EqualTo(3));
            Assert.That(restored.Get("weird \"name\".lua", "k").AsNumber(), Is.EqualTo(4));
        });
    }

    [Test]
    public void LoadToml_ReplacesEverything()
    {
        var store = new ScriptStateStore();
        store.Set("old.lua", "k", ScriptValue.From(1));
        var other = new ScriptStateStore();
        other.Set("new.lua", "k", ScriptValue.From(2));

        store.LoadToml(other.SerializeToml());

        Assert.Multiple(() =>
        {
            Assert.That(store.Get("old.lua", "k").IsNull, Is.True);
            Assert.That(store.Get("new.lua", "k").AsNumber(), Is.EqualTo(2));
        });

        store.LoadToml("");
        Assert.That(store.Get("new.lua", "k").IsNull, Is.True, "an empty document is a valid empty state");
    }

    [Test]
    public void LoadToml_MalformedText_WarnsAndLeavesTheStateUnchanged()
    {
        using var log = new LogCapture();
        var store = new ScriptStateStore();
        store.Set("a.lua", "k", ScriptValue.From("keep me"));

        store.LoadToml("[scripts.\"a.lua\"\nk = ");

        Assert.Multiple(() =>
        {
            Assert.That(store.Get("a.lua", "k").AsString(), Is.EqualTo("keep me"));
            Assert.That(log.Contains(LogLevel.Warn, "Script state could not be loaded"), Is.True);
        });
    }

    [Test]
    public void LoadToml_ABadJsonValue_LeavesTheStateUnchanged()
    {
        using var log = new LogCapture();
        var store = new ScriptStateStore();
        store.Set("a.lua", "k", ScriptValue.From("keep me"));

        store.LoadToml("[scripts.\"a.lua\"]\nk = \"{not json\"\n");

        Assert.That(store.Get("a.lua", "k").AsString(), Is.EqualTo("keep me"));
    }
}
