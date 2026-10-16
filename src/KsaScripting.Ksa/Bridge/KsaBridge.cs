using KsaScripting.Core.Commands;
using KsaScripting.Core.Hooks;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Logging;
using KsaScripting.Core.Scheduling;
using KsaScripting.Core.Snapshots;
using KsaScripting.Core.Values;
using KsaScripting.Ksa.Commands;
using KSA;

namespace KsaScripting.Ksa.Bridge;

/// <summary>
///     The game side of the host: implements <see cref="IGameBridge"/> over the vessel cache
///     (<c>vessel:&lt;id&gt;</c>), the celestial catalog (<c>body:&lt;id&gt;</c>, <c>system</c>), the game clock
///     (<c>time</c>) and the command executor. Game thread only; nothing here throws — failures read as
///     <see cref="ScriptValue.Null"/> / zero / <see langword="false"/> and are logged once.
/// </summary>
/// <param name="cache">The per-step vessel / body cache.</param>
/// <param name="executor">Executes Frame-phase commands.</param>
/// <param name="queue">The game-thread queue Solver-phase commands are posted to (drained by <c>SlotHost</c>).</param>
public sealed class KsaBridge(VesselCache cache, ActionExecutor executor, GameThreadQueue queue) : IGameBridge
{
    private const string VesselPrefix = "vessel:";
    private const string BodyPrefix = "body:";

    /// <summary>
    ///     The simulation step counter reported as <c>time.stepIndex</c>; the slot host keeps it current
    ///     (it advances only when a simulation step landed).
    /// </summary>
    public long StepIndex { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<EventDescriptor> EventCatalog => Core.Hooks.EventCatalog.All;

    /// <inheritdoc />
    public double UtSeconds => Guard(0d, () => Sanitize.Finite(Universe.GetElapsedSeconds()), "ut");

    /// <inheritdoc />
    public double SimDt => Guard(0d, () => Sanitize.Finite(Universe.GetLastSimStep().DeltaTime), "simDt");

    /// <inheritdoc />
    public double Warp => Guard(0d, () => Sanitize.Finite(Universe.SimulationSpeed), "warp");

    /// <inheritdoc />
    public bool Paused => Guard(false, Universe.IsPaused, "paused");

    /// <inheritdoc />
    public ulong FrameNumber => Guard(0UL, () => Program.FrameNumber, "frame");

    /// <inheritdoc />
    public ScriptValue Read(string handle, string path)
    {
        try
        {
            if (handle == "time")
                return SnapshotPath.Resolve(ScriptViews.Time(CurrentTime()), path);

            if (handle.StartsWith(VesselPrefix, StringComparison.Ordinal))
            {
                var id = handle[VesselPrefix.Length..];
                return SnapshotPath.Resolve(cache.View(id, StartsWithParts(path)), path);
            }

            if (handle.StartsWith(BodyPrefix, StringComparison.Ordinal))
                return SnapshotPath.Resolve(cache.BodyView(handle[BodyPrefix.Length..]), path);

            if (handle == "system")
                return SnapshotPath.Resolve(ScriptViews.System(cache.System()), path);

            return ScriptValue.Null;
        }
        catch (Exception ex)
        {
            Log.LogOnce("bridge.read", LogLevel.Warn, $"Read('{handle}', '{path}') failed (logged once): {ex.Message}");
            return ScriptValue.Null;
        }
    }

    /// <inheritdoc />
    public ScriptValue Snapshot(string handle)
    {
        try
        {
            if (handle.StartsWith(VesselPrefix, StringComparison.Ordinal))
                return ScriptValue.FromObject(cache.Get(handle[VesselPrefix.Length..], withParts: true));

            if (handle.StartsWith(BodyPrefix, StringComparison.Ordinal))
                return cache.BodyView(handle[BodyPrefix.Length..]);

            return handle switch
            {
                "time" => ScriptViews.Time(CurrentTime()),
                "system" => ScriptViews.System(cache.System()),
                _ => ScriptValue.Null,
            };
        }
        catch (Exception ex)
        {
            Log.LogOnce("bridge.snapshot", LogLevel.Warn, $"Snapshot('{handle}') failed (logged once): {ex.Message}");
            return ScriptValue.Null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string kind)
    {
        try
        {
            switch (kind)
            {
                case "vessels":
                    return cache.Ids();
                case "bodies":
                    var bodies = cache.Bodies();
                    var ids = new List<string>(bodies.Count);
                    foreach (var body in bodies)
                        ids.Add(body.Id);
                    return ids;
                default:
                    return [];
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("bridge.list", LogLevel.Warn, $"List('{kind}') failed (logged once): {ex.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    public CommandResult Execute(SimCommand command)
    {
        try
        {
            if (command.Phase == CommandPhase.Solver)
                return CommandResult.Invalid("solver-phase action; use QueueSolver");

            var result = executor.Execute(command);
            // A command mutates game state: a read in the same slot must not see the cached pre-state.
            cache.Invalidate();
            return result;
        }
        catch (Exception ex)
        {
            Log.LogOnce("bridge.execute", LogLevel.Error, $"Execute('{command.Action}') threw (logged once): {ex.Message}");
            return CommandResult.Fault(ex.Message);
        }
    }

    /// <inheritdoc />
    public void QueueSolver(SimCommand command, Action<CommandResult> onResult)
    {
        try
        {
            queue.PostCommand(command, onResult);
        }
        catch (Exception ex)
        {
            Log.LogOnce("bridge.queue", LogLevel.Error, $"QueueSolver('{command.Action}') failed (logged once): {ex.Message}");
            try
            {
                onResult(CommandResult.Fault(ex.Message));
            }
            catch
            {
                // The callback contract is "trivial, never throws"; a broken one is not our problem here.
            }
        }
    }

    /// <inheritdoc />
    [KsaAnchor("Program.TerminalInterface.Execute(string)", SourceFile = "KSA/Program.cs / Brutal.ImGuiApi.Abstractions/TerminalInterface.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "Program.TerminalInterface => ConsoleWindow.Terminal (:500); Execute(string) -> bool splits on ';' outside "
            + "quotes and runs each command synchronously on the caller's thread, so it must be the game thread. "
            + "Errors surface through TerminalInterface.OnOutput, not exceptions.")]
    public void ConsoleExec(string command)
    {
        try
        {
            Program.TerminalInterface.Execute(command);
        }
        catch (Exception ex)
        {
            Log.Warn($"console exec '{command}' failed: {ex.Message}");
        }
    }

    [KsaAnchor("Universe.GetElapsedSeconds(); Universe.SimulationSpeed; Universe.GetLastSimStep().DeltaTime; "
            + "Universe.IsPaused(); Program.ControlledVehicle?.Id; Program.FrameNumber",
        SourceFile = "KSA/Universe.cs / KSA/Program.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Low,
        Notes = "The time handle. IsPaused() is `_simulationSpeed == 0.0` (Universe.cs:1799); FrameNumber is a public "
            + "static ulong field incremented in PostRender (Program.cs:301). All doubles are sanitized (NaN/Inf -> 0).")]
    private TimeView CurrentTime() => new(
        Ut: Sanitize.Finite(Universe.GetElapsedSeconds()),
        Warp: Sanitize.Finite(Universe.SimulationSpeed),
        SimDt: Sanitize.Finite(Universe.GetLastSimStep().DeltaTime),
        Paused: Universe.IsPaused(),
        WarpSpeeds: cache.WarpSpeeds(),
        AutoWarp: cache.AutoWarpActive(),
        AutoWarpTargetUt: cache.AutoWarpTargetUt(),
        ActiveVesselId: Program.ControlledVehicle?.Id,
        Frame: Program.FrameNumber,
        StepIndex: StepIndex);

    /// <summary>Whether the first segment of <paramref name="path"/> is <c>parts</c> (the lazily sampled list).</summary>
    private static bool StartsWithParts(string path)
        => path.StartsWith("parts", StringComparison.OrdinalIgnoreCase)
           && (path.Length == 5 || path[5] == '.');

    [KsaAnchor("Universe / Program time reads (see CurrentTime)", SourceFile = "KSA/Universe.cs / KSA/Program.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "try/catch wrapper for the live scalar properties: a throwing getter reads as the fallback.")]
    private static T Guard<T>(T fallback, Func<T> read, string name)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.LogOnce("bridge.live." + name, LogLevel.Warn, $"live read '{name}' failed (logged once): {ex.Message}");
            return fallback;
        }
    }
}
