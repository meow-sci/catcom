using System.Diagnostics;
using HarmonyLib;
using KsaScripting.Core.Commands;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Scheduling;
using KsaScripting.Core.Snapshots;
using KsaScripting.Ksa.Bridge;
using KsaScripting.Ksa.Commands;
using KSA;

namespace KsaScripting.Ksa.Slots;

/// <summary>
///     Owns the two game-loop slots scripts run in. The <b>Step slot</b> is a postfix on
///     <c>Universe.ApplyVehicleSolvers()</c>: the solver is idle, fresh sim results are applied and
///     <c>InputEvents</c> enqueued here still land this frame, so it drains the posted work and the Frame
///     command queue, runs the snapshot differ at 10 Hz of player time, and calls the scheduler. The
///     <b>Solver slot</b> is a <c>Priority.First</c> prefix on <c>Universe.ExecuteNextVehicleSolvers</c>:
///     the only sim-safe window for direct mutation, so it drains the Solver command queue. A
///     <c>[StarMapAfterOnFrame]</c> latch (<see cref="OnAfterFrame"/>) re-runs the Step slot with
///     <c>stepLanded = false</c> on frames where the postfix did not fire, so wall and render timers still
///     advance while the game is loading.
/// </summary>
/// <remarks>
///     Nothing on the sim path may throw: every entry point is wrapped, a fault is logged once, and after
///     three consecutive faults the host disables itself (<see cref="Faulted"/>) rather than risk the game.
///     At most one instance is installed at a time (the static patch methods reach it through
///     <see cref="Current"/>).
/// </remarks>
public sealed class SlotHost
{
    /// <summary>The Harmony id of the slot patches.</summary>
    public const string HarmonyId = "ksa-scripting.slots";

    private const int FaultLimit = 3;

    private static readonly (string Name, Type Patch)[] PatchTable =
    [
        ("Universe.ApplyVehicleSolvers (Step slot)", typeof(StepSlotPatch)),
        ("Universe.ExecuteNextVehicleSolvers (Solver slot)", typeof(SolverSlotPatch)),
    ];

    private readonly IStepSink _step;
    private readonly IEventSink _events;
    private readonly GameThreadQueue _queue;
    private readonly ActionExecutor _executor;
    private readonly VesselCache _cache;
    private readonly KsaBridge _bridge;
    private readonly SampleClock _differClock = new(VesselCache.DifferRateHz);
    private readonly List<string> _installed = [];
    private readonly List<string> _failed = [];

    private Harmony? _harmony;
    private UniverseTime _lastNextTime;
    private long _stepIndex;
    private double _lastPlayerDt;
    private bool _stepRanThisFrame;
    private int _consecutiveFaults;

    /// <summary>Creates the host (nothing is patched until <see cref="Install"/>).</summary>
    /// <param name="step">Receives the Step slot call (the Core scheduler adapter).</param>
    /// <param name="events">Receives the differ's events (the Core event bus adapter).</param>
    /// <param name="queue">The game-thread queue drained inside the slots.</param>
    /// <param name="executor">Executes the queued commands.</param>
    /// <param name="cache">The per-step read cache (invalidated at the start of every slot).</param>
    /// <param name="bridge">The bridge whose <see cref="KsaBridge.StepIndex"/> this host keeps current.</param>
    public SlotHost(IStepSink step, IEventSink events, GameThreadQueue queue, ActionExecutor executor,
        VesselCache cache, KsaBridge bridge)
    {
        _step = step;
        _events = events;
        _queue = queue;
        _executor = executor;
        _cache = cache;
        _bridge = bridge;
    }

    /// <summary>The installed instance the static patch methods dispatch to; null when not installed.</summary>
    internal static SlotHost? Current { get; private set; }

    /// <summary>True once the host disabled itself after repeated faults (it then does no more work).</summary>
    public bool Faulted { get; private set; }

    /// <summary>The number of Step slots run (postfix and latch).</summary>
    public long Steps { get; private set; }

    /// <summary>The wall time, in milliseconds, of the last Step slot (drain + differ + scheduler).</summary>
    public double LastStepMs { get; private set; }

    /// <summary>The slot patches that installed successfully (for the UI).</summary>
    public IReadOnlyList<string> Installed => _installed;

    /// <summary>The slot patches that failed to install, with the reason (for the UI).</summary>
    public IReadOnlyList<string> Failed => _failed;

    /// <summary>
    ///     Installs the slot patches, each class on its own so a missing target loses one slot and not
    ///     both. Idempotent. Never throws.
    /// </summary>
    public void Install()
    {
        if (_harmony is not null)
            return;

        _installed.Clear();
        _failed.Clear();
        try
        {
            _harmony = new Harmony(HarmonyId);
            Current = this;
            foreach (var (name, patch) in PatchTable)
            {
                try
                {
                    var patched = _harmony.CreateClassProcessor(patch).Patch();
                    if (patched is null || patched.Count == 0)
                        throw new InvalidOperationException("the patch class patched nothing");
                    _installed.Add(name);
                }
                catch (Exception ex)
                {
                    _failed.Add($"{name}: {ex.Message}");
                    Log.LogOnce("slot.install." + name, LogLevel.Error,
                        $"could not patch {name} (the slot is dark): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("slot.install", LogLevel.Error, $"slot install failed: {ex.Message}");
        }
    }

    /// <summary>Removes every slot patch and detaches this host. Never throws.</summary>
    public void Uninstall()
    {
        try
        {
            _harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception ex)
        {
            Log.Warn($"slot unpatch failed: {ex.Message}");
        }
        finally
        {
            _harmony = null;
            if (ReferenceEquals(Current, this))
                Current = null;
            _installed.Clear();
        }
    }

    /// <summary>
    ///     The <c>[StarMapAfterOnFrame]</c> latch: when the Step postfix did not fire this frame (a paused or
    ///     loading frame), drains the posted work and the Frame queue and runs a Step slot with
    ///     <c>stepLanded = false</c> so wall and render timers advance; then re-arms for the next frame.
    /// </summary>
    /// <param name="dtPlayer">The wall-clock (player) frame time in seconds.</param>
    [KsaAnchor("Universe.GetElapsedSeconds(); Program.FrameNumber", SourceFile = "KSA/Universe.cs / KSA/Program.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "The latch for frames where Universe.ApplyVehicleSolvers did not run (it is skipped while "
            + "Universe.CurrentSystem is null, e.g. on the main menu or during a load). StarMap invokes it from "
            + "a Program.OnFrame postfix (after GPU submit, this frame's solver still running), so only reads "
            + "and InputEvents enqueues are safe.")]
    public void OnAfterFrame(double dtPlayer)
    {
        if (Faulted)
            return;
        try
        {
            // The true wall frame time, whether or not the Step postfix fired (keeps the differ and the render
            // clock ticking even if the Solver slot patch is dark).
            _lastPlayerDt = dtPlayer;
            if (!_stepRanThisFrame)
            {
                var started = Stopwatch.GetTimestamp();
                _queue.DrainWork(GameThreadQueue.DefaultDrainBatch);
                _queue.Drain(CommandPhase.Frame, _executor, GameThreadQueue.DefaultDrainBatch);
                _cache.BeginStep(_stepIndex);
                _step.StepSlot(Sanitize.Finite(Universe.GetElapsedSeconds()), 0, _stepIndex, Program.FrameNumber,
                    false, dtPlayer);
                Steps++;
                LastStepMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            _consecutiveFaults = 0;
        }
        catch (Exception ex)
        {
            RecordFault("latch", ex);
        }
        finally
        {
            _stepRanThisFrame = false;
        }
    }

    /// <summary>The Step slot body (called by the <c>ApplyVehicleSolvers</c> postfix). Never throws.</summary>
    [KsaAnchor("Universe.GetElapsedSeconds(); Universe.GetLastSimStep() (DeltaTime, NextTime); Program.FrameNumber",
        SourceFile = "KSA/Universe.cs / KSA/SimStep.cs / KSA/Program.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "StepLanded = dt > 0 && SimStep.NextTime moved since the previous slot (NextTime advances only when a "
            + "vehicle step lands, and is equal across frames when paused). UniverseTime has value equality.")]
    internal void OnStep()
    {
        if (Faulted)
            return;
        try
        {
            var started = Stopwatch.GetTimestamp();
            var ut = Sanitize.Finite(Universe.GetElapsedSeconds());
            var sim = Universe.GetLastSimStep();
            var dt = Sanitize.Finite(sim.DeltaTime);
            var landed = dt > 0 && sim.NextTime != _lastNextTime;
            _lastNextTime = sim.NextTime;
            if (landed)
                _stepIndex++;
            _bridge.StepIndex = _stepIndex;
            var frame = Program.FrameNumber;

            _queue.DrainWork(GameThreadQueue.DefaultDrainBatch);
            _queue.Drain(CommandPhase.Frame, _executor, GameThreadQueue.DefaultDrainBatch);
            _cache.BeginStep(_stepIndex);

            // The gatOS differ cadence, in player time: 10 Hz, independent of warp.
            if (_differClock.Tick(_lastPlayerDt))
                PublishDifferEvents(ut);

            _step.StepSlot(ut, dt, _stepIndex, frame, landed, _lastPlayerDt);
            _stepRanThisFrame = true;
            Steps++;
            LastStepMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _consecutiveFaults = 0;
        }
        catch (Exception ex)
        {
            _stepRanThisFrame = true; // do not let the latch double-run a slot that already faulted
            RecordFault("step", ex);
        }
    }

    /// <summary>The Solver slot body (called by the <c>ExecuteNextVehicleSolvers</c> prefix). Never throws.</summary>
    /// <param name="dtPlayer">The wall-clock (player) frame time of the frame being kicked.</param>
    internal void OnSolver(double dtPlayer)
    {
        if (Faulted)
            return;
        try
        {
            _lastPlayerDt = dtPlayer;
            _queue.Drain(CommandPhase.Solver, _executor, GameThreadQueue.DefaultDrainBatch);
            _consecutiveFaults = 0;
        }
        catch (Exception ex)
        {
            RecordFault("solver", ex);
        }
    }

    private void PublishDifferEvents(double ut)
    {
        var events = _cache.DiffEvents(ut);
        for (var i = 0; i < events.Count; i++)
        {
            foreach (var mapped in DifferEvents.Map(events[i], id => _cache.Get(id)))
                _events.Publish(mapped.Type, mapped.VesselId, mapped.Payload, mapped.Ut);
        }
    }

    private void RecordFault(string slot, Exception ex)
    {
        _consecutiveFaults++;
        Log.LogOnce($"slot.{slot}", LogLevel.Error, $"{slot} slot faulted (logged once): {ex}");
        if (_consecutiveFaults >= FaultLimit && !Faulted)
        {
            Faulted = true;
            Log.Error($"the {slot} slot faulted {FaultLimit} times in a row; ksa-scripting slots are disabled until restart.");
        }
    }

    /// <summary>Postfix on the end of the vehicle-solver apply: the Step slot.</summary>
    [HarmonyPatch(typeof(Universe), nameof(Universe.ApplyVehicleSolvers))]
    private static class StepSlotPatch
    {
        [KsaAnchor("Universe.ApplyVehicleSolvers() static postfix", SourceFile = "KSA/Universe.cs",
            Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
            Notes = "Universe.cs:1922. Runs inside Program.PrepareFrame right after JobSystems.VehicleSolver.Wait(): "
                + "fresh results are applied, the solver is idle, InputEvents enqueued here are applied this frame. "
                + "Called once per frame while Universe.CurrentSystem != null (also when paused). A throw here "
                + "propagates out of Program.OnFrame, so the body is wrapped in OnStep.")]
        private static void Postfix() => Current?.OnStep();
    }

    /// <summary>Prefix on the solver kick: the Solver slot.</summary>
    [HarmonyPatch(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers))]
    private static class SolverSlotPatch
    {
        [KsaAnchor("Universe.ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep) static prefix",
            SourceFile = "KSA/Universe.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
            Risk = ChurnRisk.Medium,
            Notes = "Universe.cs:2126. Solvers idle, inputs applied, derived data flushed: the only sim-safe window for "
                + "direct Vehicle/Part/Module/FlightComputer writes (FlightComputer writes made elsewhere are "
                + "clobbered by CopyFrom). Priority.First so the drain happens before other mods' prefixes.")]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(double dtPlayer, SimStep simStep)
        {
            _ = simStep;
            Current?.OnSolver(dtPlayer);
        }
    }
}
