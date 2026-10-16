using KsaScripting.Core.Runtime;
using KsaScripting.Core.Scheduling;
using KsaScripting.Core.Tests.Support;
using KsaScripting.Core.Values;
using KsaScripting.Core.Hosting;

namespace KsaScripting.Core.Tests.Scheduling;

[TestFixture]
public sealed class WatchdogTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private sealed class CountingRuntime : IScriptRuntime
    {
        private readonly ManualResetEventSlim _interrupted = new(false);
        private int _count;

        public int InterruptCount => Volatile.Read(ref _count);

        public bool WaitForInterrupt(TimeSpan timeout) => _interrupted.Wait(timeout);

        public string Id => "counting";

        public string Language => ScriptLanguages.Lua;

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RuntimeStats Stats => new(0, 0, 0, false, "");

        public void Interrupt()
        {
            Interlocked.Increment(ref _count);
            _interrupted.Set();
        }

        public void Initialize(IHostFacade host, RuntimeOptions options) => throw new NotSupportedException();

        public ScriptModule Load(ScriptSource source) => throw new NotSupportedException();

        public IReadOnlyList<string> Exports(ScriptModule module) => throw new NotSupportedException();

        public ScriptCallable GetExport(ScriptModule module, string name) => throw new NotSupportedException();

        public ScriptValue Invoke(ScriptCallable fn, ReadOnlySpan<ScriptValue> args) => throw new NotSupportedException();

        public bool Advance(TimeSpan budget) => false;

        public void Unload(ScriptModule module) => throw new NotSupportedException();

        public void Dispose() => _interrupted.Dispose();
    }

    [Test]
    public void AnExpiredDeadline_InterruptsTheRuntimeOnce_AndMarksTheEntryTripped()
    {
        using var watchdog = new Watchdog();
        var runtime = new CountingRuntime();

        watchdog.Arm(runtime, TimeSpan.FromMilliseconds(10));

        Assert.That(runtime.WaitForInterrupt(Deadline), Is.True, "the watchdog never fired");
        Assert.Multiple(() =>
        {
            Assert.That(watchdog.TrippedSinceArm, Is.True);
            Assert.That(watchdog.Tripped, Is.EqualTo(1));
        });

        // It does not fire again for the same entry.
        Assert.That(SpinWait.SpinUntil(() => runtime.InterruptCount > 1, TimeSpan.FromMilliseconds(100)), Is.False);
        watchdog.Disarm();
        Assert.Multiple(() =>
        {
            Assert.That(runtime.InterruptCount, Is.EqualTo(1));
            Assert.That(watchdog.Tripped, Is.EqualTo(1));
        });
    }

    [Test]
    public void ADisarmedEntry_NeverFires()
    {
        using var watchdog = new Watchdog();
        var runtime = new CountingRuntime();

        watchdog.Arm(runtime, TimeSpan.FromMilliseconds(100));
        Assert.That(watchdog.TrippedSinceArm, Is.False);
        watchdog.Disarm();

        Assert.Multiple(() =>
        {
            Assert.That(runtime.WaitForInterrupt(TimeSpan.FromMilliseconds(400)), Is.False);
            Assert.That(watchdog.Tripped, Is.Zero);
            Assert.That(watchdog.TrippedSinceArm, Is.False);
        });
    }

    [Test]
    public void NestedEntries_EnforceTheInnermostDeadline_ThenReturnToTheOuterOne()
    {
        using var watchdog = new Watchdog();
        var outer = new CountingRuntime();
        var inner = new CountingRuntime();

        watchdog.Arm(outer, TimeSpan.FromSeconds(30));
        watchdog.Arm(inner, TimeSpan.FromMilliseconds(10));

        Assert.That(inner.WaitForInterrupt(Deadline), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(watchdog.TrippedSinceArm, Is.True);
            Assert.That(outer.InterruptCount, Is.Zero);
        });

        watchdog.Disarm();
        Assert.That(watchdog.TrippedSinceArm, Is.False, "the outer entry has not expired");
        watchdog.Disarm();
        Assert.That(outer.InterruptCount, Is.Zero);
    }

    [Test]
    public void ARuntimeWhoseInterruptThrows_DoesNotKillTheWatchdog()
    {
        using var watchdog = new Watchdog();
        var thrower = new ThrowingRuntime();
        var good = new CountingRuntime();

        watchdog.Arm(thrower, TimeSpan.FromMilliseconds(10));
        Assert.That(SpinWait.SpinUntil(() => watchdog.Tripped == 1, Deadline), Is.True);
        watchdog.Disarm();

        watchdog.Arm(good, TimeSpan.FromMilliseconds(10));
        Assert.That(good.WaitForInterrupt(Deadline), Is.True);
        watchdog.Disarm();
    }

    private sealed class ThrowingRuntime : IScriptRuntime
    {
        public string Id => "throwing";

        public string Language => ScriptLanguages.Lua;

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public RuntimeStats Stats => new(0, 0, 0, false, "");

        public void Interrupt() => throw new InvalidOperationException("interrupt failed");

        public void Initialize(IHostFacade host, RuntimeOptions options) => throw new NotSupportedException();

        public ScriptModule Load(ScriptSource source) => throw new NotSupportedException();

        public IReadOnlyList<string> Exports(ScriptModule module) => throw new NotSupportedException();

        public ScriptCallable GetExport(ScriptModule module, string name) => throw new NotSupportedException();

        public ScriptValue Invoke(ScriptCallable fn, ReadOnlySpan<ScriptValue> args) => throw new NotSupportedException();

        public bool Advance(TimeSpan budget) => false;

        public void Unload(ScriptModule module) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    [Test]
    public void Disarm_WithNothingArmed_IsANoOp()
    {
        using var watchdog = new Watchdog();

        Assert.DoesNotThrow(() => watchdog.Disarm());
        Assert.That(watchdog.TrippedSinceArm, Is.False);
    }

    [Test]
    public void Dispose_StopsTheThread_AndLaterCallsAreHarmless()
    {
        var watchdog = new Watchdog();
        var runtime = new CountingRuntime();

        watchdog.Dispose();
        watchdog.Dispose();

        Assert.DoesNotThrow(() =>
        {
            watchdog.Arm(runtime, TimeSpan.FromMilliseconds(1));
            watchdog.Disarm();
        });
        Assert.That(runtime.WaitForInterrupt(TimeSpan.FromMilliseconds(200)), Is.False);
    }

    [Test]
    public void TheWatchdogThread_IsABackgroundThreadWithTheDocumentedName()
    {
        using var watchdog = new Watchdog();

        Assert.Multiple(() =>
        {
            Assert.That(watchdog.WorkerThread.IsBackground, Is.True);
            Assert.That(watchdog.WorkerThread.Name, Is.EqualTo("ksa-scripting watchdog"));
            Assert.That(watchdog.WorkerThread.IsAlive, Is.True);
        });
    }

    [Test]
    public void Dispose_JoinsTheWatchdogThread()
    {
        var watchdog = new Watchdog();
        var thread = watchdog.WorkerThread;

        watchdog.Dispose();

        Assert.That(thread.IsAlive, Is.False);
    }
}
