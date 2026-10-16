using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Runtime.Lua.Tests;

[TestFixture]
public sealed class AdvanceTests
{
    private LuaRuntimeFixture _fx = null!;

    [SetUp]
    public void SetUp() => _fx = new LuaRuntimeFixture();

    [TearDown]
    public void TearDown() => _fx.Dispose();

    private FakeHostFacade Host => _fx.Host;

    private ScriptModule LoadWaiter(string name, string logText)
        => _fx.Load(name, $"return {{ main = function() ksa.sleep(1) ksa.console.log('{logText}') end }}");

    [Test]
    public void NothingParked_ReturnsFalse()
    {
        Assert.That(_fx.Runtime.Advance(TimeSpan.FromMilliseconds(10)), Is.False);
    }

    [Test]
    public void IncompleteWaits_AreNotResumed()
    {
        _fx.Runtime.Invoke(_fx.Export(LoadWaiter("w", "done"), "main"), []);

        Assert.That(_fx.Runtime.Advance(TimeSpan.FromMilliseconds(10)), Is.False);
        Assert.That(Host.Logs, Is.Empty);
    }

    [Test]
    public void ContinuationsResumeInParkOrder_AtLeastOnePerCall_AndReportRemainingWork()
    {
        foreach (var name in new[] { "one", "two", "three" })
            _fx.Runtime.Invoke(_fx.Export(LoadWaiter(name, name), "main"), []);
        // Complete them out of order: the resume order still follows the park order.
        Host.Waits[2].Wait.Complete(ScriptValue.Null);
        Host.Waits[0].Wait.Complete(ScriptValue.Null);
        Host.Waits[1].Wait.Complete(ScriptValue.Null);

        Assert.That(_fx.Runtime.Advance(TimeSpan.Zero), Is.True, "work remains after the budget was used up");
        Assert.That(Host.Logs.Select(l => l.Message), Is.EqualTo(new[] { "one" }));
        Assert.That(_fx.Runtime.Advance(TimeSpan.Zero), Is.True);
        Assert.That(Host.Logs.Select(l => l.Message), Is.EqualTo(new[] { "one", "two" }));
        Assert.That(_fx.Runtime.Advance(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That(Host.Logs.Select(l => l.Message), Is.EqualTo(new[] { "one", "two", "three" }));
        Assert.That(Host.Logs.Select(l => l.Script), Is.EqualTo(new[] { "one", "two", "three" }));
    }

    [Test]
    public void RepeatedWaits_ParkAgain_AndResumeOncePerAdvance()
    {
        var module = _fx.Load("loop", """
            return { main = function()
              for i = 1, 3 do
                ksa.next_frame()
                ksa.console.log("frame " .. i)
              end
            end }
            """);
        _fx.Runtime.Invoke(_fx.Export(module, "main"), []);

        for (var i = 1; i <= 3; i++)
        {
            Assert.That(Host.Waits, Has.Count.EqualTo(i));
            Host.Waits[i - 1].Wait.Complete(ScriptValue.Null);
            // Even though the next wait could be completed immediately, one Advance resumes the coroutine once.
            Assert.That(_fx.Runtime.Advance(TimeSpan.FromSeconds(5)), Is.False);
            Assert.That(Host.Logs.Select(l => l.Message), Is.EqualTo(Enumerable.Range(1, i).Select(n => $"frame {n}")));
        }

        Assert.That(Host.Waits, Has.Count.EqualTo(3));
        Assert.That(Host.ReportedErrors, Is.Empty);
    }

    [Test]
    public void AlreadyCompletedReWait_IsReportedAsRemainingWork()
    {
        var module = _fx.Load("rewait", "return { main = function() ksa.next_frame() ksa.next_frame() end }");
        _fx.Runtime.Invoke(_fx.Export(module, "main"), []);
        Host.Waits[0].Wait.Complete(ScriptValue.Null);
        // Pre-complete the second wait the moment the host creates it.
        var runtimeHost = Host;
        Assert.That(_fx.Runtime.Advance(TimeSpan.FromSeconds(5)), Is.False);
        Assert.That(runtimeHost.Waits, Has.Count.EqualTo(2));

        runtimeHost.Waits[1].Wait.Complete(ScriptValue.Null);
        Assert.That(_fx.Runtime.Advance(TimeSpan.FromSeconds(5)), Is.False);
    }

    [Test]
    public void ErrorInAContinuation_IsReported_WithFileAndLine_AndOtherContinuationsStillRun()
    {
        var failing = _fx.Write("failing", "return { main = function()\n  ksa.next_frame()\n  error('late failure')\nend }");
        var failingModule = _fx.Runtime.Load(failing);
        var good = LoadWaiter("good", "good-ran");
        _fx.Runtime.Invoke(_fx.Export(failingModule, "main"), []);
        _fx.Runtime.Invoke(_fx.Export(good, "main"), []);
        foreach (var wait in Host.Waits)
            wait.Wait.Complete(ScriptValue.Null);

        _fx.Runtime.Advance(TimeSpan.FromSeconds(5));

        var error = Host.ReportedErrors.Single();
        Assert.That(error.Message, Does.Contain("late failure"));
        Assert.That(error.File, Is.EqualTo(failing.Path));
        Assert.That(error.Line, Is.EqualTo(3));
        Assert.That(Host.Logs.Select(l => l.Message), Is.EqualTo(new[] { "good-ran" }));
    }

    [Test]
    public void ResultOfTheWait_IsPassedToTheScript()
    {
        var module = _fx.Load("result", """
            return { main = function()
              local r = ksa.sleep(1)
              ksa.console.log(type(r) .. ":" .. tostring(r))
            end }
            """);
        _fx.Runtime.Invoke(_fx.Export(module, "main"), []);

        Host.Waits[0].Wait.Complete(ScriptValue.From(2.5));
        _fx.Runtime.Advance(TimeSpan.FromSeconds(1));

        Assert.That(Host.Logs.Single().Message, Is.EqualTo("number:2.5"));
    }

    [Test]
    public void Subscribing_FromAContinuation_IsAttributedToTheOwnerScript()
    {
        var module = _fx.Load("late-subscriber", """
            return { main = function()
              ksa.next_frame()
              ksa.on("late", function() return 99 end)
            end }
            """);
        _fx.Runtime.Invoke(_fx.Export(module, "main"), []);
        var scriptAtSubscribe = (string?)null;
        Host.Waits[0].Wait.Complete(ScriptValue.Null);

        _fx.Runtime.Advance(TimeSpan.FromSeconds(1));
        scriptAtSubscribe = Host.EnteredScripts.Last();

        Assert.That(scriptAtSubscribe, Is.EqualTo("late-subscriber"));
        var sub = Host.Subscriptions.Single();
        Assert.That(_fx.Runtime.Invoke(sub.Fn, []).AsNumber(), Is.EqualTo(99d));
        // The callable belongs to the module: unloading releases it.
        _fx.Runtime.Unload(module);
        Assert.Throws<ScriptRuntimeException>(() => _fx.Runtime.Invoke(sub.Fn, []));
    }

    [Test]
    [CancelAfter(20000)]
    public void Interrupt_DuringAContinuation_IsReported_AndAdvanceReturns()
    {
        var module = _fx.Load("spin-later", """
            return { main = function()
              ksa.next_frame()
              ksa_host.log(1, "spinning")
              while true do end
            end }
            """);
        _fx.Runtime.Invoke(_fx.Export(module, "main"), []);
        var spinning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Host.OnLog = log =>
        {
            if (log.Message == "spinning")
                spinning.TrySetResult();
        };
        Host.Waits[0].Wait.Complete(ScriptValue.Null);

        using var interrupter = new BackgroundInterrupter(_fx.Runtime, spinning.Task);

        var remaining = _fx.Runtime.Advance(TimeSpan.FromSeconds(30));
        interrupter.Finish();

        Assert.That(remaining, Is.False);
        Assert.That(Host.ReportedErrors.Single().Message, Is.EqualTo("interrupted"));
        Assert.That(_fx.Call("return { f = function() return 'alive' end }", "f").AsString(), Is.EqualTo("alive"));
    }

    [Test]
    public void ParkedCoroutinesOfAnUnloadedModule_AreDropped_WhileOthersContinue()
    {
        var doomed = LoadWaiter("doomed", "doomed-ran");
        var survivor = LoadWaiter("survivor", "survivor-ran");
        _fx.Runtime.Invoke(_fx.Export(doomed, "main"), []);
        _fx.Runtime.Invoke(_fx.Export(survivor, "main"), []);
        foreach (var wait in Host.Waits)
            wait.Wait.Complete(ScriptValue.Null);

        _fx.Runtime.Unload(doomed);
        _fx.Runtime.Advance(TimeSpan.FromSeconds(1));

        Assert.That(Host.Logs.Select(l => l.Message), Is.EqualTo(new[] { "survivor-ran" }));
    }
}
