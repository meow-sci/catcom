using KsaScripting.Core.Hooks;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;

namespace KsaScripting.Core.Scheduling;

public sealed partial class Scheduler
{
    private readonly List<PendingWait> _waits = [];
    private long _waitIds;

    /// <summary>
    ///     Creates a wait that the scheduler resolves in a later Step slot (or immediately, for a
    ///     <c>termRead</c> with queued input). Kinds and arguments: <c>sleep</c> (Number seconds, or Map
    ///     <c>{seconds, clock?}</c>), <c>nextStep</c>, <c>nextFrame</c>, <c>until</c> (Function, or Map
    ///     <c>{fn, timeoutSeconds?, clock?}</c>), <c>waitEvent</c> (Map <c>{name, vessel?, timeoutSeconds?}</c>),
    ///     <c>termRead</c> (Number terminal id, or Map <c>{termId}</c>) and <c>act</c> (completed by
    ///     <see cref="CompleteWait"/>).
    /// </summary>
    /// <param name="kind">The wait kind.</param>
    /// <param name="arg">The kind-specific argument.</param>
    /// <param name="script">The script that waits (empty when unknown).</param>
    /// <returns>The wait to hand to the runtime.</returns>
    /// <exception cref="ArgumentException">The kind is unknown or the argument is malformed.</exception>
    public HostWait CreateWait(string kind, ScriptValue arg, string script)
    {
        ArgumentNullException.ThrowIfNull(kind);
        arg ??= ScriptValue.Null;
        script ??= "";

        var pending = new PendingWait(new HostWait(Interlocked.Increment(ref _waitIds), kind), kind, arg, script)
        {
            CreatedSlot = _slotIndex,
            CreatedSeq = _events.LastSequence,
        };

        switch (kind)
        {
            case "sleep":
                ParseSleep(pending, arg);
                break;
            case "nextStep":
            case "nextFrame":
            case "act":
                break;
            case "until":
                ParseUntil(pending, arg);
                break;
            case "waitEvent":
                ParseWaitEvent(pending, arg);
                break;
            case "termRead":
                ParseTermRead(pending, arg);
                break;
            default:
                throw new ArgumentException($"unknown wait kind '{kind}'", nameof(kind));
        }

        if (_lastStep is not null)
        {
            pending.Base = ClockNow(pending.Clock, _lastStep, wallNow: pending.Clock == "wall" ? ReadWallClock() : 0);
            pending.HasBase = true;
        }

        if (kind == "termRead" && Terminals?.Get(pending.TermId) is { } terminal && terminal.TryReadInput(out var bytes))
        {
            pending.Wait.Complete(ScriptValue.From(bytes));
            return pending.Wait;
        }

        _waits.Add(pending);
        return pending.Wait;
    }

    /// <summary>Completes a wait with a result (game thread). Used for <c>act</c> waits when the Solver result arrives; a wait that already ended is left alone.</summary>
    /// <param name="wait">The wait.</param>
    /// <param name="result">The result handed to the awaiting script.</param>
    public void CompleteWait(HostWait wait, ScriptValue result)
    {
        ArgumentNullException.ThrowIfNull(wait);

        var index = _waits.FindIndex(w => ReferenceEquals(w.Wait, wait));
        if (index >= 0)
        {
            var pending = _waits[index];
            _waits.RemoveAt(index);
            DisposeFn(pending);
        }

        wait.Complete(result ?? ScriptValue.Null);
    }

    /// <summary>Cancels every pending wait of a script (the catalog calls it on Stop/Unload).</summary>
    /// <param name="script">The script's catalog name.</param>
    public void CancelWaits(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        CancelWhere(w => string.Equals(w.Script, script, StringComparison.Ordinal));
    }

    /// <summary>Cancels the given pending waits of a script (the catalog uses it to retire the waits of a replaced module without touching the new module's).</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <param name="waitIds">The ids from <see cref="PendingWaitIds"/>.</param>
    public void CancelWaits(string script, IReadOnlyCollection<long> waitIds)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(waitIds);
        CancelWhere(w => string.Equals(w.Script, script, StringComparison.Ordinal) && waitIds.Contains(w.Wait.Id));
    }

    /// <summary>The ids of the script's pending waits.</summary>
    /// <param name="script">The script's catalog name.</param>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<long> PendingWaitIds(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return _waits.Where(w => string.Equals(w.Script, script, StringComparison.Ordinal)).Select(w => w.Wait.Id).ToArray();
    }

    private void CancelWhere(Func<PendingWait, bool> predicate)
    {
        foreach (var pending in _waits.Where(predicate).ToArray())
        {
            _waits.Remove(pending);
            DisposeFn(pending);
            pending.Wait.Cancel();
        }
    }

    private static void DisposeFn(PendingWait pending)
    {
        try
        {
            pending.Fn?.Dispose();
        }
        catch (Exception ex)
        {
            Log.LogOnce("scheduler.dispose-fn", LogLevel.Warn, $"Disposing a wait predicate failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- argument parsing

    private static void ParseSleep(PendingWait pending, ScriptValue arg)
    {
        switch (arg.Kind)
        {
            case ScriptValueKind.Number:
                pending.Seconds = arg.AsNumber();
                break;
            case ScriptValueKind.Map:
                pending.Seconds = NumberOrDefault(arg, "seconds", 0) ?? 0;
                pending.Clock = ClockOf(arg);
                break;
            default:
                throw new ArgumentException("sleep expects a number of seconds or {seconds, clock}");
        }

        if (double.IsNaN(pending.Seconds))
            pending.Seconds = 0;
    }

    private static void ParseUntil(PendingWait pending, ScriptValue arg)
    {
        switch (arg.Kind)
        {
            case ScriptValueKind.Function:
                pending.Fn = arg.AsFunction();
                break;
            case ScriptValueKind.Map:
                if (arg["fn"].Kind != ScriptValueKind.Function)
                    throw new ArgumentException("until expects a predicate function (fn)");
                pending.Fn = arg["fn"].AsFunction();
                pending.TimeoutSeconds = NumberOrDefault(arg, "timeoutSeconds", null);
                pending.Clock = ClockOf(arg);
                break;
            default:
                throw new ArgumentException("until expects a predicate function or {fn, timeoutSeconds, clock}");
        }
    }

    private static void ParseWaitEvent(PendingWait pending, ScriptValue arg)
    {
        if (arg.Kind != ScriptValueKind.Map || arg["name"].Kind != ScriptValueKind.String)
            throw new ArgumentException("waitEvent expects {name, vessel?, timeoutSeconds?}");

        pending.EventName = arg["name"].AsString();
        pending.Vessel = arg["vessel"].Kind == ScriptValueKind.String ? arg["vessel"].AsString() : null;
        pending.TimeoutSeconds = NumberOrDefault(arg, "timeoutSeconds", null);
        pending.Clock = ClockOf(arg);
    }

    private static void ParseTermRead(PendingWait pending, ScriptValue arg)
    {
        switch (arg.Kind)
        {
            case ScriptValueKind.Number:
                pending.TermId = (int)arg.AsNumber();
                break;
            case ScriptValueKind.Map when arg["termId"].Kind == ScriptValueKind.Number:
                pending.TermId = (int)arg["termId"].AsNumber();
                break;
            default:
                throw new ArgumentException("termRead expects a terminal id or {termId}");
        }
    }

    private static double? NumberOrDefault(ScriptValue map, string key, double? fallback)
    {
        var value = map[key];
        return value.Kind switch
        {
            ScriptValueKind.Null => fallback,
            ScriptValueKind.Number => value.AsNumber(),
            _ => throw new ArgumentException($"'{key}' must be a number"),
        };
    }

    private static string ClockOf(ScriptValue map)
    {
        var value = map["clock"];
        if (value.Kind == ScriptValueKind.Null)
            return "ut";
        if (value.Kind != ScriptValueKind.String)
            throw new ArgumentException("'clock' must be \"ut\", \"wall\" or \"render\"");

        var clock = value.AsString();
        if (clock is "ut" or "wall" or "render")
            return clock;
        throw new ArgumentException($"unknown clock '{clock}' (expected \"ut\", \"wall\" or \"render\")");
    }

    private double ClockNow(string clock, StepInfo step, double wallNow) => clock switch
    {
        "wall" => wallNow,
        "render" => _renderTotal,
        _ => step.Ut,
    };

    // ---------------------------------------------------------------- resolution (step 1)

    private void ResolveWaits(StepInfo step, IReadOnlyList<(long Seq, ScriptEvent Event)> events)
    {
        if (_waits.Count == 0)
            return;

        // Waits created while resolving (a predicate may call back into the host) wait for the next slot.
        foreach (var pending in _waits.ToArray())
        {
            try
            {
                if (pending.Wait.IsCompleted)
                {
                    // Completed or cancelled behind the scheduler's back (e.g. by the runtime).
                    _waits.Remove(pending);
                    DisposeFn(pending);
                    continue;
                }

                if (!pending.HasBase)
                {
                    pending.Base = ClockNow(pending.Clock, step, _slotWall);
                    pending.HasBase = true;
                }

                if (pending.CreatedSlot >= _slotIndex || IsDisabled(pending.Script))
                    continue;

                ResolveOne(pending, step, events);
            }
            catch (Exception ex)
            {
                Log.LogOnce("scheduler.wait", LogLevel.Error, $"Resolving a '{pending.Kind}' wait failed: {ex}");
                Finish(pending, p => p.Wait.Fail(new ScriptRuntimeException($"wait '{pending.Kind}' failed: {ex.Message}")));
            }
        }
    }

    private void ResolveOne(PendingWait pending, StepInfo step, IReadOnlyList<(long Seq, ScriptEvent Event)> events)
    {
        switch (pending.Kind)
        {
            case "sleep":
                if (ClockNow(pending.Clock, step, _slotWall) >= pending.Base + Math.Max(pending.Seconds, 0))
                    Finish(pending, p => p.Wait.Complete(ScriptValue.Null));
                break;

            case "nextStep":
                if (step.StepLanded)
                    Finish(pending, p => p.Wait.Complete(step.ToScriptValue()));
                break;

            case "nextFrame":
                Finish(pending, p => p.Wait.Complete(ScriptValue.Null));
                break;

            case "until":
                ResolveUntil(pending, step);
                break;

            case "waitEvent":
                ResolveWaitEvent(pending, step, events);
                break;

            case "termRead":
                ResolveTermRead(pending);
                break;

            // "act" waits are completed only by CompleteWait.
        }
    }

    private void ResolveUntil(PendingWait pending, StepInfo step)
    {
        if (pending.Fn is null)
        {
            Finish(pending, p => p.Wait.Fail(new ScriptRuntimeException("until has no predicate")));
            return;
        }

        if (!BudgetExhausted)
        {
            var result = InvokeCore(pending.Script, pending.Fn, [], sync: false, out var faulted);
            if (faulted)
            {
                if (!IsDisabled(pending.Script))
                    Finish(pending, p => p.Wait.Fail(new ScriptRuntimeException("until predicate failed")));
                return;
            }

            if (result.IsTruthy)
            {
                Finish(pending, p => p.Wait.Complete(ScriptValue.True));
                return;
            }
        }
        else
        {
            Interlocked.Increment(ref _overruns);
        }

        if (TimedOut(pending, step))
            Finish(pending, p => p.Wait.Complete(ScriptValue.False));
    }

    private void ResolveWaitEvent(PendingWait pending, StepInfo step, IReadOnlyList<(long Seq, ScriptEvent Event)> events)
    {
        foreach (var (seq, ev) in events)
        {
            if (seq <= pending.CreatedSeq || !string.Equals(ev.Type, pending.EventName, StringComparison.Ordinal))
                continue;

            var target = ev.TargetScript;
            if (target is not null && !string.Equals(target, pending.Script, StringComparison.Ordinal))
                continue;

            if (!_hooks.MatchesVessel(pending.Vessel, ev.VesselId))
                continue;

            Finish(pending, p => p.Wait.Complete(ev.ToScriptValue()));
            return;
        }

        if (TimedOut(pending, step))
            Finish(pending, p => p.Wait.Complete(ScriptValue.Null));
    }

    private void ResolveTermRead(PendingWait pending)
    {
        var terminal = Terminals?.Get(pending.TermId);
        if (terminal is null)
        {
            Finish(pending, p => p.Wait.Fail(new ScriptRuntimeException($"terminal {pending.TermId} not found")));
            return;
        }

        if (terminal.TryReadInput(out var bytes))
            Finish(pending, p => p.Wait.Complete(ScriptValue.From(bytes)));
    }

    private bool TimedOut(PendingWait pending, StepInfo step)
        => pending.TimeoutSeconds is { } timeout
           && ClockNow(pending.Clock, step, _slotWall) - pending.Base >= timeout;

    private void Finish(PendingWait pending, Func<PendingWait, bool> settle)
    {
        _waits.Remove(pending);
        DisposeFn(pending);
        settle(pending);
    }
}
