using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.Lua.Tests;

[TestFixture]
public sealed class CallableLifecycleTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp() => _fx = new LuaRuntimeFixture();

    [TearDown]
    public void TearDown() => _fx.Dispose();

    [Test]
    public void DisposeOnAnotherThread_IsSafe_AndTakesEffectAtTheNextEntry()
    {
        var module = _fx.Load("threads", "return { f = function() return 1 end }");
        var f = _fx.Export(module, "f");
        Assert.That(_fx.Runtime.Invoke(f, []).AsNumber(), Is.EqualTo(1d));

        Task.Run(f.Dispose).Wait(TimeSpan.FromSeconds(10));

        Assert.That(f.IsDisposed, Is.True);
        Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(f, []));
        Assert.That(_fx.Runtime.Invoke(_fx.Export(module, "f"), []).AsNumber(), Is.EqualTo(1d), "a fresh callable for the same export works");
    }

    [Test]
    public void CallableFromAnotherRuntimeOrUnknownToken_IsRejected()
    {
        var foreign = ScriptCallable.Create("v8", 1, "x", _ => { });
        var unknown = ScriptCallable.Create("lua", 99999, "x", _ => { });

        Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(foreign, []));
        Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(unknown, []));
        Assert.Throws<ArgumentNullException>(() => _fx.Runtime.Invoke(null!, []));
    }

    [Test]
    public void ManyCallables_AreReleased_WithoutGrowingTheRegistryUnboundedly()
    {
        var module = _fx.Load("churn", "return { make = function() return function() return 1 end end }");
        var make = _fx.Export(module, "make");
        for (var i = 0; i < 2000; i++)
        {
            using var made = _fx.Runtime.Invoke(make, []).AsFunction();
            Assert.That(_fx.Runtime.Invoke(made, []).AsNumber(), Is.EqualTo(1d));
        }

        // Disposed callables are unref'd at the next entry point; the heap does not keep 2000 closures alive.
        _fx.Runtime.Invoke(make, []).AsFunction().Dispose();
        _fx.Runtime.Unload(module);
        Assert.That(_fx.Runtime.Stats.HeapBytes, Is.LessThan(8 * 1024 * 1024));
    }

    [Test]
    public void ExportCallables_AreOwnedByTheirModule()
    {
        var a = _fx.Load("a", "return { f = function() return 'a' end }");
        var b = _fx.Load("b", "return { f = function() return 'b' end }");
        var fa = _fx.Export(a, "f");
        var fb = _fx.Export(b, "f");

        _fx.Runtime.Unload(a);

        Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(fa, []));
        Assert.That(_fx.Runtime.Invoke(fb, []).AsString(), Is.EqualTo("b"));
    }
}
