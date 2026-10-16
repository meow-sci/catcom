using System.Diagnostics;
using KsaScripting.Core.Hooks;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Terminals;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Scheduling;

/// <summary>
///     Runs script code on the game thread. <see cref="StepSlot"/> is called once per Step slot and, within
///     the frame budget, resolves waits, delivers queued events, fires tick hooks, starts services,
///     advances the runtimes' continuations and flushes terminals. Every entry into a runtime goes
///     through <see cref="InvokeGuarded"/>, which attributes host calls to the script, arms the
///     watchdog, converts exceptions into script errors and latches faulty scripts as disabled.
/// </summary>
public sealed partial class Scheduler
{
    private const int MaxAdvanceRounds = 32;

    private readonly ScheduleConfig _cfg;
    private readonly HookRegistry _hooks;
    private readonly EventBus _events;
    private readonly IRuntimeSet _runtimes;
    private readonly Watchdog _watchdog;
    private readonly Action<string, Exception> _onScriptError;

    private readonly object _stateGate = new();
    private readonly Dictionary<string, string> _disabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _consecutiveFaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, TickState> _tickStates = [];
    private readonly Dictionary<Guid, string> _startedServices = [];
    private readonly HashSet<string> _advanceErrors = new(StringComparer.Ordinal);

    private long _slotStart;
    private long _slotIndex;
    private double _renderTotal;
    private double _slotWall;
    private StepInfo? _lastStep;
    private int _invokeDepth;
    private bool _inAdvance;
    private int _tickRotation;

    private long _slots;
    private double _lastSlotMs;
    private double _maxSlotMs;
    private long _invocations;
    private long _faults;
    private long _overruns;

    /// <summary>Creates the scheduler and installs itself as the event bus's sync dispatcher.</summary>
    /// <param name="cfg">Budgets and fault policy.</param>
    /// <param name="hooks">The binding registry.</param>
    /// <param name="events">The event bus.</param>
    /// <param name="runtimes">The available script runtimes.</param>
    /// <param name="watchdog">The watchdog that interrupts runaway calls.</param>
    /// <param name="onScriptError">Called for every script error with the script name (wire to <c>HostFacade.RecordError</c>).</param>
    public Scheduler(
        ScheduleConfig cfg,
        HookRegistry hooks,
        EventBus events,
        IRuntimeSet runtimes,
        Watchdog watchdog,
        Action<string, Exception> onScriptError)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(watchdog);
        ArgumentNullException.ThrowIfNull(onScriptError);

        _cfg = cfg;
        _hooks = hooks;
        _events = events;
        _runtimes = runtimes;
        _watchdog = watchdog;
        _onScriptError = onScriptError;
        WallClock = static () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        events.SyncDispatcher = DispatchSync;
        events.InvokeDepthProvider = () => InvokeDepth;
    }

    /// <summary>Raised once when a script is disabled, with the script name and the reason.</summary>
    public event Action<string, string>? ScriptDisabled;

    /// <summary>The host facade; every invocation runs inside <see cref="IHostFacade.EnterScript"/>. Set by the facade's constructor.</summary>
    public IHostFacade? Host { get; set; }

    /// <summary>The terminal registry (<c>termRead</c> waits, the <c>termId</c> context field). Set by the facade's constructor.</summary>
    public TerminalRegistry? Terminals { get; set; }

    /// <summary>The wall clock in seconds (monotonic; tests inject one).</summary>
    public Func<double> WallClock { get; set; }

    /// <summary>
    ///     The number of script invocations currently on the stack (also counted while a runtime
    ///     advances its continuations). While above zero the event bus defers sync dispatch.
    /// </summary>
    public int InvokeDepth => Volatile.Read(ref _invokeDepth);

    /// <summary>The scheduler's counters.</summary>
    public ScheduleStats Stats => new(
        Volatile.Read(ref _slots),
        _lastSlotMs,
        _maxSlotMs,
        Interlocked.Read(ref _invocations),
        Interlocked.Read(ref _faults),
        Interlocked.Read(ref _overruns),
        _waits.Count,
        _events.PendingCount,
        _watchdog.Tripped);

    // ---------------------------------------------------------------- the slot

    /// <summary>Runs one Step slot. Never throws.</summary>
    /// <param name="step">What the game reports for this slot.</param>
    public void StepSlot(StepInfo step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var started = Stopwatch.GetTimestamp();
        _slotStart = started;
        _slotIndex++;
        _lastStep = step;
        _renderTotal += step.PlayerDt;
        _slotWall = ReadWallClock();

        IReadOnlyList<(long Seq, ScriptEvent Event)> pending = [];
        try
        {
            pending = _events.DrainPending();
        }
        catch (Exception ex)
        {
            LogStageFailure("drain", ex);
        }

        try
        {
            ResolveWaits(step, pending);
        }
        catch (Exception ex)
        {
            LogStageFailure("waits", ex);
        }

        try
        {
            DeliverEvents(pending);
        }
        catch (Exception ex)
        {
            LogStageFailure("events", ex);
        }

        try
        {
            RunTicks(step);
        }
        catch (Exception ex)
        {
            LogStageFailure("ticks", ex);
        }

        try
        {
            StartServices();
        }
        catch (Exception ex)
        {
            LogStageFailure("services", ex);
        }

        try
        {
            AdvanceRuntimes();
        }
        catch (Exception ex)
        {
            LogStageFailure("advance", ex);
        }

        try
        {
            Terminals?.FlushAll();
        }
        catch (Exception ex)
        {
            LogStageFailure("flush", ex);
        }

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _lastSlotMs = elapsedMs;
        if (elapsedMs > _maxSlotMs)
            _maxSlotMs = elapsedMs;
        Interlocked.Increment(ref _slots);
    }

    private static void LogStageFailure(string stage, Exception ex)
        => Log.LogOnce("scheduler." + stage, LogLevel.Error, $"Scheduler stage '{stage}' failed: {ex}");

    private bool BudgetExhausted => Stopwatch.GetElapsedTime(_slotStart) >= _cfg.FrameBudget;

    private TimeSpan BudgetRemaining
    {
        get
        {
            var remaining = _cfg.FrameBudget - Stopwatch.GetElapsedTime(_slotStart);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    private double ReadWallClock()
    {
        try
        {
            return WallClock();
        }
        catch (Exception ex)
        {
            Log.LogOnce("scheduler.wallclock", LogLevel.Warn, $"The wall clock failed: {ex.Message}");
            return _slotWall;
        }
    }

    // ---------------------------------------------------------------- events (step 2)

    private void DeliverEvents(IReadOnlyList<(long Seq, ScriptEvent Event)> pending)
    {
        if (pending.Count == 0)
            return;

        var next = 0;
        try
        {
            for (; next < pending.Count; next++)
            {
                if (BudgetExhausted)
                {
                    Interlocked.Increment(ref _overruns);
                    break;
                }

                try
                {
                    DeliverEvent(pending[next].Seq, pending[next].Event);
                }
                catch (Exception ex)
                {
                    Log.LogOnce("scheduler.deliver", LogLevel.Error, $"Delivering event '{pending[next].Event.Type}' failed: {ex}");
                }
            }
        }
        finally
        {
            if (next < pending.Count)
            {
                var rest = new (long Seq, ScriptEvent Event)[pending.Count - next];
                for (var i = 0; i < rest.Length; i++)
                    rest[i] = pending[next + i];
                _events.Requeue(rest);
            }
        }
    }

    private void DeliverEvent(long seq, ScriptEvent ev)
    {
        var deferredSync = _events.IsSyncDeferred(seq);
        var target = ev.TargetScript;
        ScriptValue? eventValue = null;

        if (deferredSync)
        {
            // These subscribers could not run inline in the Harmony patch; they get the event now,
            // asynchronously (ctx.sync = false).
            foreach (var (binding, fn) in _hooks.ForEvent(ev.Type, ev.VesselId, sync: true))
                DeliverTo(binding, fn, ev, target, ref eventValue);
            _events.ClearSyncDeferred(seq);
        }

        foreach (var (binding, fn) in _hooks.ForEvent(ev.Type, ev.VesselId, sync: false))
            DeliverTo(binding, fn, ev, target, ref eventValue);
    }

    private void DeliverTo(Binding binding, ScriptCallable fn, ScriptEvent ev, string? target, ref ScriptValue? eventValue)
    {
        if (target is not null && !string.Equals(binding.Script, target, StringComparison.Ordinal))
            return;

        eventValue ??= ev.ToScriptValue();
        InvokeGuarded(binding.Script, fn, [BuildEventContext(binding.Script, ev, eventValue, sync: false)]);
    }

    private ScriptValue BuildEventContext(string script, ScriptEvent ev, ScriptValue eventValue, bool sync)
        => BuildContext("event", script, new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["event"] = eventValue,
            ["vesselId"] = ev.VesselId is null ? ScriptValue.Null : ScriptValue.From(ev.VesselId),
            ["sync"] = ScriptValue.From(sync),
        });

    /// <summary>Delivers one event immediately to the matching async subscriptions (e.g. <c>signal.cancel</c> on Stop). Honors <c>"$script"</c> targeting; ignores the frame budget.</summary>
    /// <param name="ev">The event.</param>
    public void DeliverNow(ScriptEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        try
        {
            var target = ev.TargetScript;
            ScriptValue? eventValue = null;
            foreach (var (binding, fn) in _hooks.ForEvent(ev.Type, ev.VesselId, sync: false))
                DeliverTo(binding, fn, ev, target, ref eventValue);
        }
        catch (Exception ex)
        {
            Log.LogOnce("scheduler.delivernow", LogLevel.Error, $"Delivering event '{ev.Type}' failed: {ex}");
        }
    }

    private void DispatchSync(ScriptEvent ev)
    {
        var subscribers = _hooks.ForEvent(ev.Type, ev.VesselId, sync: true);
        if (subscribers.Count == 0)
            return;

        var target = ev.TargetScript;
        var eventValue = ev.ToScriptValue();
        foreach (var (binding, fn) in subscribers)
        {
            if (target is not null && !string.Equals(binding.Script, target, StringComparison.Ordinal))
                continue;

            InvokeGuarded(binding.Script, fn, [BuildEventContext(binding.Script, ev, eventValue, sync: true)], sync: true);
        }
    }

    // ---------------------------------------------------------------- ticks (step 3)

    private void RunTicks(StepInfo step)
    {
        var due = new List<(Binding Binding, ScriptCallable Fn)>();
        due.AddRange(_hooks.ForTick("frame"));
        if (step.StepLanded)
            due.AddRange(_hooks.ForTick("step"));

        var every = _hooks.ForTick("every");
        if (_tickStates.Count > every.Count)
            PruneTickStates(every);

        foreach (var item in every)
        {
            if (IsDisabled(item.Binding.Script))
                continue;

            var interval = item.Binding.Options.IntervalSeconds;
            if (interval <= 0)
            {
                due.Add(item);
                continue;
            }

            var now = ClockNow(item.Binding.Options.Clock, step, _slotWall);
            if (!_tickStates.TryGetValue(item.Binding.Id, out var state))
            {
                _tickStates[item.Binding.Id] = new TickState(item.Binding.Script, now + interval);
                continue;
            }

            if (now >= state.Due)
                due.Add(item);
        }

        if (due.Count == 0)
            return;

        var start = (int)(unchecked((uint)_tickRotation++) % (uint)due.Count);
        ScriptValue? stepFields = null;
        for (var n = 0; n < due.Count; n++)
        {
            if (BudgetExhausted)
            {
                Interlocked.Increment(ref _overruns);
                return;
            }

            var (binding, fn) = due[(start + n) % due.Count];
            if (IsDisabled(binding.Script))
                continue;

            stepFields ??= step.ToScriptValue();
            InvokeGuarded(binding.Script, fn, [BuildContext("tick", binding.Script, stepFields.AsMap())]);

            if (binding.Key == "every" && binding.Options.IntervalSeconds > 0
                && _tickStates.TryGetValue(binding.Id, out var fired))
            {
                var now = ClockNow(binding.Options.Clock, step, _slotWall);
                var nextDue = fired.Due + binding.Options.IntervalSeconds;
                if (now >= nextDue)
                    nextDue = now + binding.Options.IntervalSeconds;
                fired.Due = nextDue;
            }
        }
    }

    private void PruneTickStates(IReadOnlyList<(Binding Binding, ScriptCallable Fn)> current)
    {
        var live = new HashSet<Guid>(current.Select(c => c.Binding.Id));
        foreach (var id in _tickStates.Keys.Where(id => !live.Contains(id)).ToArray())
            _tickStates.Remove(id);
    }

    // ---------------------------------------------------------------- services (step 4)

    private void StartServices()
    {
        var services = _hooks.Services();
        if (_startedServices.Count > services.Count)
        {
            var live = new HashSet<Guid>(services.Select(s => s.Binding.Id));
            foreach (var id in _startedServices.Keys.Where(id => !live.Contains(id)).ToArray())
                _startedServices.Remove(id);
        }

        foreach (var (binding, fn) in services)
        {
            if (_startedServices.ContainsKey(binding.Id))
                continue;

            if (BudgetExhausted)
            {
                Interlocked.Increment(ref _overruns);
                return;
            }

            _startedServices[binding.Id] = binding.Script;
            if (IsDisabled(binding.Script))
                continue;

            var kind = Terminals?.ForScript(binding.Script) is not null ? "terminal" : "service";
            InvokeGuarded(binding.Script, fn, [BuildContext(kind, binding.Script)]);
        }
    }

    // ---------------------------------------------------------------- runtimes (step 5)

    private void AdvanceRuntimes()
    {
        var runtimes = _runtimes.All;
        if (runtimes.Count == 0)
            return;

        var hasWork = new bool[runtimes.Count];
        Array.Fill(hasWork, true);

        Interlocked.Increment(ref _invokeDepth);
        _inAdvance = true;
        try
        {
            for (var round = 0; round < MaxAdvanceRounds; round++)
            {
                var any = false;
                for (var i = 0; i < runtimes.Count; i++)
                {
                    if (!hasWork[i])
                        continue;

                    var runtime = runtimes[i];
                    if (!runtime.IsAvailable)
                    {
                        hasWork[i] = false;
                        continue;
                    }

                    if (BudgetExhausted)
                        return;

                    hasWork[i] = AdvanceOne(runtime, BudgetRemaining);
                    any |= hasWork[i];
                }

                if (!any)
                    break;
            }
        }
        finally
        {
            _inAdvance = false;
            _advanceErrors.Clear();
            Interlocked.Decrement(ref _invokeDepth);
        }
    }

    private bool AdvanceOne(IScriptRuntime runtime, TimeSpan remaining)
    {
        var tripped = false;
        _watchdog.Arm(runtime, _cfg.CallBudget);
        try
        {
            return runtime.Advance(remaining);
        }
        catch (Exception ex)
        {
            Log.LogOnce($"scheduler.advance:{runtime.Id}", LogLevel.Error, $"Runtime '{runtime.Id}' failed while advancing: {ex.Message}");
            if (ex is ScriptRuntimeException sre && Host is { CurrentScript: { Length: > 0 } current })
                ReportError(current, sre);
            return false;
        }
        finally
        {
            tripped = _watchdog.TrippedSinceArm;
            _watchdog.Disarm();
            if (tripped)
            {
                foreach (var script in _advanceErrors.ToArray())
                    Disable(script, $"watchdog: exceeded {_cfg.CallBudget.TotalMilliseconds:0} ms");
                Log.LogOnce($"scheduler.advance-trip:{runtime.Id}", LogLevel.Warn,
                    $"The watchdog interrupted runtime '{runtime.Id}' while it advanced continuations.");
            }
        }
    }

    // ---------------------------------------------------------------- invocation

    /// <summary>
    ///     The single place script code is entered: skips disabled scripts, attributes host calls to the
    ///     script, counts depth, arms the watchdog (sync budget for sync hooks, call budget otherwise),
    ///     and turns any exception into a reported script error. A call the watchdog interrupted
    ///     disables the script.
    /// </summary>
    /// <param name="script">The script the function belongs to.</param>
    /// <param name="fn">The function to call.</param>
    /// <param name="args">The arguments.</param>
    /// <param name="sync">Whether this is a sync hook (shorter watchdog budget).</param>
    /// <returns>The function's result, or <see cref="ScriptValue.Null"/> on error, interruption or when the script is disabled.</returns>
    public ScriptValue InvokeGuarded(string script, ScriptCallable fn, ScriptValue[] args, bool sync = false)
        => InvokeCore(script, fn, args, sync, out _);

    private ScriptValue InvokeCore(string script, ScriptCallable fn, ScriptValue[] args, bool sync, out bool faulted)
    {
        faulted = false;
        if (IsDisabled(script))
        {
            faulted = true;
            return ScriptValue.Null;
        }

        if (fn.IsDisposed)
        {
            faulted = true;
            return ScriptValue.Null;
        }

        var runtime = _runtimes.ForId(fn.RuntimeId);
        if (runtime is null || !runtime.IsAvailable)
        {
            Log.LogOnce($"scheduler.noruntime:{fn.RuntimeId}", LogLevel.Warn,
                $"Cannot call '{script}': runtime '{fn.RuntimeId}' is not available.");
            faulted = true;
            return ScriptValue.Null;
        }

        var budget = sync ? _cfg.SyncHookBudget : _cfg.CallBudget;
        var result = ScriptValue.Null;
        Exception? error = null;
        var tripped = false;

        IDisposable? scope = null;
        try
        {
            scope = Host?.EnterScript(script);
        }
        catch (Exception ex)
        {
            Log.LogOnce("scheduler.enterscript", LogLevel.Warn, $"EnterScript failed: {ex.Message}");
        }

        Interlocked.Increment(ref _invokeDepth);
        _watchdog.Arm(runtime, budget);
        try
        {
            result = runtime.Invoke(fn, args);
            lock (_stateGate)
            {
                _consecutiveFaults[script] = 0;
            }

            Interlocked.Increment(ref _invocations);
        }
        catch (ScriptRuntimeException ex)
        {
            error = ex;
        }
        catch (Exception ex)
        {
            error = new ScriptRuntimeException(ex.Message, ex.ToString(), null, null, ex);
        }
        finally
        {
            tripped = _watchdog.TrippedSinceArm;
            _watchdog.Disarm();
            Interlocked.Decrement(ref _invokeDepth);
            try
            {
                scope?.Dispose();
            }
            catch (Exception ex)
            {
                Log.LogOnce("scheduler.enterscript-dispose", LogLevel.Warn, $"Leaving a script scope failed: {ex.Message}");
            }
        }

        if (tripped)
            Disable(script, $"watchdog: exceeded {budget.TotalMilliseconds:0} ms");

        if (error is not null)
        {
            faulted = true;
            ReportError(script, error);
            return ScriptValue.Null;
        }

        if (tripped)
        {
            faulted = true;
            return ScriptValue.Null;
        }

        return result;
    }

    /// <summary>
    ///     Counts a script error (fault accounting), forwards it to the error callback and disables the
    ///     script when it reached the consecutive-fault limit. Used for invocation errors and by
    ///     <c>HostFacade.ReportError</c> for asynchronous errors.
    /// </summary>
    /// <param name="script">The script the error belongs to (empty when unknown: counted, but nothing is disabled).</param>
    /// <param name="error">The error.</param>
    public void ReportError(string script, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        script ??= "";

        Interlocked.Increment(ref _faults);
        var disable = false;
        if (script.Length > 0)
        {
            lock (_stateGate)
            {
                _consecutiveFaults.TryGetValue(script, out var count);
                count++;
                _consecutiveFaults[script] = count;
                disable = count >= _cfg.FaultLimit && !_disabled.ContainsKey(script);
            }

            if (_inAdvance)
                _advanceErrors.Add(script);
        }

        try
        {
            _onScriptError(script, error);
        }
        catch (Exception ex)
        {
            Log.LogOnce("scheduler.onerror", LogLevel.Warn, $"The script error callback failed: {ex.Message}");
        }

        if (disable)
            Disable(script, $"fault limit reached ({_cfg.FaultLimit} consecutive errors)");
    }

    // ---------------------------------------------------------------- script state

    /// <summary>Whether the script was disabled (fault limit or watchdog) and has not been reset.</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <returns><see langword="true"/> when the script is disabled.</returns>
    public bool IsDisabled(string script)
    {
        lock (_stateGate)
        {
            return _disabled.ContainsKey(script);
        }
    }

    /// <summary>Clears the disabled flag and the consecutive-fault count (the catalog calls it when a script (re)loads).</summary>
    /// <param name="script">The script's catalog name.</param>
    public void ResetFaults(string script)
    {
        lock (_stateGate)
        {
            _disabled.Remove(script);
            _consecutiveFaults.Remove(script);
        }
    }

    /// <summary>Records the script's file path (reported as <c>ctx.script.path</c>).</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <param name="path">The file path.</param>
    public void SetScriptPath(string script, string path)
    {
        lock (_stateGate)
        {
            _paths[script] = path;
        }
    }

    /// <summary>Sets the script's cancel signal (<c>ctx.signal.cancelled</c>).</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <param name="cancelled">The new state.</param>
    public void SetCancelled(string script, bool cancelled)
    {
        lock (_stateGate)
        {
            if (cancelled)
                _cancelled.Add(script);
            else
                _cancelled.Remove(script);
        }
    }

    /// <summary>Whether the script's cancel signal is set.</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <returns><see langword="true"/> when cancelled.</returns>
    public bool IsCancelled(string script)
    {
        lock (_stateGate)
        {
            return _cancelled.Contains(script);
        }
    }

    /// <summary>Drops per-script bookkeeping (path, tick due times, started services, cancel flag, faults) after the script was unloaded.</summary>
    /// <param name="script">The script's catalog name.</param>
    public void ForgetScript(string script)
    {
        lock (_stateGate)
        {
            _paths.Remove(script);
            _cancelled.Remove(script);
            _disabled.Remove(script);
            _consecutiveFaults.Remove(script);
        }

        foreach (var id in _tickStates.Where(p => p.Value.Script == script).Select(p => p.Key).ToArray())
            _tickStates.Remove(id);
        foreach (var id in _startedServices.Where(p => p.Value == script).Select(p => p.Key).ToArray())
            _startedServices.Remove(id);
    }

    private void Disable(string script, string reason)
    {
        lock (_stateGate)
        {
            if (!_disabled.TryAdd(script, reason))
                return;
        }

        Log.Warn($"Script '{script}' was disabled: {reason}. Reload it to re-enable.");
        try
        {
            ScriptDisabled?.Invoke(script, reason);
        }
        catch (Exception ex)
        {
            Log.LogOnce("scheduler.disabled-handler", LogLevel.Warn, $"A ScriptDisabled handler failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- commands and contexts

    /// <summary>Invokes every command binding named <paramref name="name"/> with the argument text as ONE String argument.</summary>
    /// <param name="name">The command name (ignoring case).</param>
    /// <param name="args">The argument text.</param>
    /// <returns><see langword="false"/> when no command binding exists.</returns>
    public bool InvokeCommand(string name, string args)
    {
        ArgumentNullException.ThrowIfNull(name);
        var commands = _hooks.Commands(name);
        if (commands.Count == 0)
            return false;

        foreach (var (binding, fn) in commands)
            InvokeGuarded(binding.Script, fn, [ScriptValue.From(args ?? "")]);
        return true;
    }

    /// <summary>
    ///     Builds the single context argument handed to hooks and <c>main</c>: a Map with
    ///     <c>"$ctx": true</c>, <c>kind</c>, <c>script {name, path}</c> and the caller's extra fields. Service,
    ///     main and terminal contexts also carry <c>signal</c> (the script name) and, when the script owns a
    ///     terminal, <c>termId</c>.
    /// </summary>
    /// <param name="kind"><c>event</c>, <c>tick</c>, <c>service</c>, <c>main</c> or <c>terminal</c>.</param>
    /// <param name="script">The script the context is for.</param>
    /// <param name="extra">Additional fields (they override the defaults).</param>
    /// <returns>The context Map.</returns>
    public ScriptValue BuildContext(string kind, string script, IReadOnlyDictionary<string, ScriptValue>? extra = null)
    {
        string path;
        lock (_stateGate)
        {
            path = _paths.GetValueOrDefault(script) ?? "";
        }

        var map = new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["$ctx"] = ScriptValue.True,
            ["kind"] = ScriptValue.From(kind),
            ["script"] = ScriptValue.Map(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
            {
                ["name"] = ScriptValue.From(script),
                ["path"] = ScriptValue.From(path),
            }),
        };

        if (kind is "service" or "main" or "terminal")
        {
            map["signal"] = ScriptValue.From(script);
            var terminal = Terminals?.ForScript(script);
            if (terminal is not null)
                map["termId"] = ScriptValue.From(terminal.Id);
        }

        if (extra is not null)
        {
            foreach (var pair in extra)
                map[pair.Key] = pair.Value;
        }

        return ScriptValue.Map(map);
    }

    private sealed class TickState(string script, double due)
    {
        public string Script { get; } = script;

        public double Due { get; set; } = due;
    }
}
