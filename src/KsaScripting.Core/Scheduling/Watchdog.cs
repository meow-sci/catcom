using System.Diagnostics;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Runtime;

namespace KsaScripting.Core.Scheduling;

/// <summary>
///     Interrupts a runaway script call. One background thread keeps a stack of armed
///     (runtime, deadline) entries; when the innermost deadline passes it calls
///     <see cref="IScriptRuntime.Interrupt"/> once for that entry. Arm/Disarm may be called from any
///     thread and nest.
/// </summary>
public sealed class Watchdog : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Entry> _stack = [];
    private readonly Thread _thread;
    private bool _disposed;
    private bool _lastPoppedFired;
    private long _tripped;

    /// <summary>Starts the watchdog thread (a background thread named <c>ksa-scripting watchdog</c>).</summary>
    public Watchdog()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ksa-scripting watchdog",
        };
        _thread.Start();
    }

    /// <summary>The watchdog's background thread (exposed to tests).</summary>
    internal Thread WorkerThread => _thread;

    /// <summary>Whether the innermost armed entry (or, when none is armed, the entry most recently disarmed) expired.</summary>
    public bool TrippedSinceArm
    {
        get
        {
            lock (_gate)
            {
                return _stack.Count > 0 ? _stack[^1].Fired : _lastPoppedFired;
            }
        }
    }

    /// <summary>The total number of expiries.</summary>
    public long Tripped => Interlocked.Read(ref _tripped);

    /// <summary>Starts watching a call. The innermost armed deadline is the one enforced.</summary>
    /// <param name="runtime">The runtime to interrupt on expiry.</param>
    /// <param name="budget">How long the call may run.</param>
    public void Arm(IScriptRuntime runtime, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var ticks = (long)(Math.Max(budget.TotalSeconds, 0) * Stopwatch.Frequency);
        lock (_gate)
        {
            if (_disposed)
                return;

            _stack.Add(new Entry(runtime, Stopwatch.GetTimestamp() + ticks));
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Stops watching the innermost armed call (no-op when nothing is armed).</summary>
    public void Disarm()
    {
        lock (_gate)
        {
            if (_stack.Count == 0)
                return;

            _lastPoppedFired = _stack[^1].Fired;
            _stack.RemoveAt(_stack.Count - 1);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Stops the watchdog thread (joined with a bounded timeout).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            Monitor.PulseAll(_gate);
        }

        if (Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        Monitor.Enter(_gate);
        try
        {
            while (!_disposed)
            {
                if (_stack.Count == 0 || _stack[^1].Fired)
                {
                    Monitor.Wait(_gate);
                    continue;
                }

                var entry = _stack[^1];
                var remaining = entry.Deadline - Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    var millis = (int)Math.Min(int.MaxValue / 2, Math.Max(1, Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency)));
                    Monitor.Wait(_gate, millis);
                    continue;
                }

                entry.Fired = true;
                Interlocked.Increment(ref _tripped);

                // Interrupt outside the lock: a runtime's Interrupt may take a moment and Arm/Disarm
                // run on the game thread.
                Monitor.Exit(_gate);
                try
                {
                    entry.Runtime.Interrupt();
                }
                catch (Exception ex)
                {
                    Log.LogOnce("watchdog.interrupt", LogLevel.Warn, $"Interrupting a runtime failed: {ex.Message}");
                }
                finally
                {
                    Monitor.Enter(_gate);
                }
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private sealed class Entry(IScriptRuntime runtime, long deadline)
    {
        public IScriptRuntime Runtime { get; } = runtime;

        public long Deadline { get; } = deadline;

        public bool Fired { get; set; }
    }
}
