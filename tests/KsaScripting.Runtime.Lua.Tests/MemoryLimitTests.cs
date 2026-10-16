using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.Lua.Tests;

[TestFixture]
public sealed class MemoryLimitTests
{
    private const long LimitBytes = 3 * 1024 * 1024;

    [Test]
    public void RetainedGarbage_OverTheLimit_ThrowsAfterCollecting()
    {
        using var fx = new LuaRuntimeFixture(heapLimitBytes: LimitBytes);
        var module = fx.Load("hog", """
            return {
              retain = function() hog = {} for i = 1, 200000 do hog[i] = { i } end end,
              churn = function() for i = 1, 20000 do local t = { i, tostring(i) } end return "ok" end,
            }
            """);

        // Short-lived garbage is collected and never trips the limit.
        Assert.That(fx.Runtime.Invoke(fx.Export(module, "churn"), []).AsString(), Is.EqualTo("ok"));

        var ex = Assert.Throws<ScriptRuntimeException>(() => fx.Runtime.Invoke(fx.Export(module, "retain"), []))!;

        Assert.That(ex.Message, Does.StartWith("memory limit exceeded ("));
        Assert.That(ex.Message, Does.Contain("KiB > 3072 KiB)"));

        // Releasing the module frees the memory again.
        fx.Runtime.Unload(module);
        Assert.That(fx.Runtime.Stats.HeapBytes, Is.LessThan(LimitBytes));
        var again = fx.Load("fine", "return { churn = function() for i = 1, 20000 do local t = { i, tostring(i) } end return 'ok' end }");
        Assert.That(fx.Runtime.Invoke(fx.Export(again, "churn"), []).AsString(), Is.EqualTo("ok"));
    }

    [Test]
    public void NoLimit_NeverThrows()
    {
        using var fx = new LuaRuntimeFixture(heapLimitBytes: 0);
        var module = fx.Load("big", "return { retain = function() hog = string.rep('x', 4 * 1024 * 1024) return #hog end }");

        Assert.That(fx.Runtime.Invoke(fx.Export(module, "retain"), []).AsNumber(), Is.EqualTo(4d * 1024 * 1024));
        Assert.That(fx.Runtime.Stats.HeapBytes, Is.GreaterThan(4 * 1024 * 1024));
    }

    [Test]
    public void Advance_ReportsMemoryOverrun_ViaReportError()
    {
        using var fx = new LuaRuntimeFixture(heapLimitBytes: LimitBytes);
        var module = fx.Load("late-hog", "return { main = function() ksa.next_frame() hog = {} for i = 1, 200000 do hog[i] = { i } end end }");
        fx.Runtime.Invoke(fx.Export(module, "main"), []);
        fx.Host.Waits[0].Wait.Complete(ScriptValue.Null);

        fx.Runtime.Advance(TimeSpan.FromSeconds(1));

        Assert.That(fx.Host.ReportedErrors.Single().Message, Does.StartWith("memory limit exceeded"));
    }
}
