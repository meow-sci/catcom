// Replaces gatOS gatOS.GameMod/Game/TelemetrySampler.cs (MIT): on-demand reads cached per step instead of a
// rate-limited sampler publishing a snapshot.
using System.Diagnostics;
using Catcom.Core.Logging;
using Catcom.Core.Snapshots;
using Catcom.Core.Values;
using Catcom.Ksa.Commands;
using Catcom.Ksa.Readers;
using Catcom.Ksa.Render;
using KSA;

namespace Catcom.Ksa.Bridge;

/// <summary>
///     Reads the game on demand and caches the result for the current step: a script that reads
///     <c>vessel.position</c> a hundred times in one Step slot costs one <c>VesselReader.Sample</c>.
///     <see cref="BeginStep"/> (called by the slot host at the start of every Step slot) drops the caches;
///     the celestial catalog is re-sampled at most once per second of wall time. Every vehicle is sampled
///     inside its own try/catch (a mid-teardown vehicle must not kill a read). Game thread only.
/// </summary>
public sealed class VesselCache
{
    private const double BodiesIntervalSeconds = 1.0;
    private const double DiffRateHz = 10;

    private readonly Dictionary<string, VesselSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VesselSnapshot> _snapshotsWithParts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptValue> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptValue> _viewsWithParts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptValue> _bodyViews = new(StringComparer.Ordinal);

    private List<VesselSnapshot>? _all;
    private long _stepIndex = -1;

    private long _bodiesTimestamp;
    private CelestialSystem? _bodiesSystem;
    private IReadOnlyList<BodySnapshot> _bodies = [];
    private SystemSnapshot? _system;

    private IReadOnlyList<double> _warpSpeeds = [];

    private SimSnapshot? _previous;
    private long _sequence;

    /// <summary>The step index passed to the last <see cref="BeginStep"/> (diagnostics).</summary>
    public long StepIndex => _stepIndex;

    /// <summary>The cadence, in Hz of player time, the slot host runs <see cref="DiffEvents"/> at.</summary>
    public static double DifferRateHz => DiffRateHz;

    /// <summary>
    ///     Starts a new generation: every per-step cache (snapshots, script views, the vessel list) is
    ///     dropped, so the next read samples the game again. Called at the start of every Step slot, after
    ///     the command queues drained.
    /// </summary>
    /// <param name="stepIndex">The simulation step counter of this slot.</param>
    public void BeginStep(long stepIndex)
    {
        _stepIndex = stepIndex;
        Invalidate();
    }

    /// <summary>
    ///     Drops the per-step caches without starting a new step — called after a command mutated game
    ///     state so a read in the same slot is not stale.
    /// </summary>
    public void Invalidate()
    {
        _snapshots.Clear();
        _snapshotsWithParts.Clear();
        _views.Clear();
        _viewsWithParts.Clear();
        _all = null;
    }

    /// <summary>Samples one vessel (cached for the current step).</summary>
    /// <param name="id">The vessel id.</param>
    /// <param name="withParts">Whether to include the part list (<c>parts</c>).</param>
    /// <returns>The snapshot, or <see langword="null"/> when the vessel is gone or could not be read.</returns>
    [KsaAnchor("VesselReader.Sample(Vehicle, activeId, ut, detail: true); PartsReader.Sample(Vehicle, ut); "
            + "Program.ControlledVehicle?.Id; Universe.GetElapsedSeconds()",
        SourceFile = "KSA/Universe.cs / KSA/Program.cs / KSA/Vehicle.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "On-demand vessel read with the full G3 detail pass (the differ and the module getters need it). "
            + "The parts list is its own cache (PartsReader rebuilds only when Parts.Count changes or every 10 s).")]
    public VesselSnapshot? Get(string id, bool withParts = false)
    {
        try
        {
            var cache = withParts ? _snapshotsWithParts : _snapshots;
            if (cache.TryGetValue(id, out var cached))
                return cached;

            var vehicle = ActionExecutor.ResolveVehicle(id);
            if (vehicle is null)
                return null;

            var ut = Sanitize.Finite(Universe.GetElapsedSeconds());
            VesselSnapshot snapshot;
            if (_all is not null && FindIn(_all, id) is { } sampled)
                snapshot = sampled;
            else
                snapshot = SampleVehicle(vehicle, ut)
                           ?? throw new InvalidOperationException("vehicle sample failed");
            if (withParts)
                snapshot = snapshot with { Parts = PartsReader.Sample(vehicle, ut) };
            cache[id] = snapshot;
            return snapshot;
        }
        catch (Exception ex)
        {
            Log.LogOnce("cache.get", LogLevel.Warn, $"vessel read failed (logged once): {ex.Message}");
            return null;
        }
    }

    /// <summary>Samples every vessel of the current system (cached for the current step).</summary>
    /// <returns>The snapshots in the system's stable order; empty when no system is loaded.</returns>
    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); VesselReader.Sample(...); VesselForceRender.Prune(...)",
        SourceFile = "KSA/Universe.cs / KSA/CelestialSystem.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "Each vehicle sampled in its own try/catch; force-render marks of despawned vessels are pruned on "
            + "the enumeration that just happened (as the gatOS sampler did).")]
    public IReadOnlyList<VesselSnapshot> All()
    {
        if (_all is not null)
            return _all;

        var vessels = new List<VesselSnapshot>(_previous?.Vessels.Count ?? 4);
        try
        {
            if (Universe.CurrentSystem is { } system)
            {
                var ut = Sanitize.Finite(Universe.GetElapsedSeconds());
                foreach (var astronomical in system.All.UnsafeAsList())
                {
                    if (astronomical is not Vehicle vehicle)
                        continue;
                    if (SampleVehicle(vehicle, ut) is { } snapshot)
                        vessels.Add(snapshot);
                }

                // Drop force-render marks whose vessel despawned (tears the render patches down when the last
                // one goes); self-gates when nothing is marked.
                VesselForceRender.Prune(vessels);
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("cache.all", LogLevel.Warn, $"vessel enumeration failed (logged once): {ex.Message}");
        }

        _all = vessels;
        return vessels;
    }

    /// <summary>Lists the vehicle ids of the current system without sampling anything.</summary>
    /// <returns>The ids in the system's stable order.</returns>
    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Id", SourceFile = "KSA/Universe.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "Enumeration only; fragment/debris vehicles are listed like any other.")]
    public IReadOnlyList<string> Ids()
    {
        var ids = new List<string>();
        try
        {
            if (Universe.CurrentSystem is { } system)
                foreach (var astronomical in system.All.UnsafeAsList())
                    if (astronomical is Vehicle vehicle)
                        ids.Add(vehicle.Id);
        }
        catch (Exception ex)
        {
            Log.LogOnce("cache.ids", LogLevel.Warn, $"vessel id enumeration failed (logged once): {ex.Message}");
        }

        return ids;
    }

    /// <summary>The script-facing view of a vessel (cached for the current step).</summary>
    /// <param name="id">The vessel id.</param>
    /// <param name="withParts">Whether the view includes the part list.</param>
    /// <returns>The view map, or <see cref="ScriptValue.Null"/> when the vessel is gone.</returns>
    public ScriptValue View(string id, bool withParts = false)
    {
        var views = withParts ? _viewsWithParts : _views;
        if (views.TryGetValue(id, out var cached))
            return cached;
        if (Get(id, withParts) is not { } snapshot)
            return ScriptValue.Null;
        var view = ScriptViews.Vessel(snapshot);
        views[id] = view;
        return view;
    }

    /// <summary>The celestial bodies of the current system (re-sampled at most once per second).</summary>
    /// <returns>The body snapshots; empty when no system is loaded or the read failed.</returns>
    public IReadOnlyList<BodySnapshot> Bodies()
    {
        SampleBodiesPaced();
        return _bodies;
    }

    /// <summary>The summary of the current system (re-sampled at most once per second).</summary>
    /// <returns>The summary, or <see langword="null"/> when no system is loaded or the read failed.</returns>
    public SystemSnapshot? System()
    {
        SampleBodiesPaced();
        return _system;
    }

    /// <summary>The script-facing view of one body (cached until the catalog is re-sampled).</summary>
    /// <param name="id">The body id.</param>
    /// <returns>The view map, or <see cref="ScriptValue.Null"/> when there is no such body.</returns>
    public ScriptValue BodyView(string id)
    {
        var bodies = Bodies();
        if (_bodyViews.TryGetValue(id, out var cached))
            return cached;
        foreach (var body in bodies)
        {
            if (!string.Equals(body.Id, id, StringComparison.Ordinal))
                continue;
            var view = ScriptViews.Body(body);
            _bodyViews[id] = view;
            return view;
        }

        return ScriptValue.Null;
    }

    /// <summary>
    ///     The warp ladder (<c>Universe.GetSimulationSpeeds()</c>): a fixed per-session list, cached after
    ///     the first successful read.
    /// </summary>
    /// <returns>The selectable warp factors; empty when unavailable.</returns>
    [KsaAnchor("Universe.GetSimulationSpeeds() (SimulationSpeed.Value per warp rung)",
        SourceFile = "KSA/Universe.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Medium,
        Notes = "The session-fixed warp ladder (time.warpSpeeds); cached after the first successful read. "
            + "GetSimulationSpeeds() is at Universe.cs:2522 and returns List<SimSpeed>.")]
    public IReadOnlyList<double> WarpSpeeds()
    {
        // The ladder is fixed per session; cache it after the first successful read so a hot Read("time")
        // does not allocate an array (+ a LINQ enumerator and delegate) every call.
        if (_warpSpeeds.Count > 0)
            return _warpSpeeds;
        try
        {
            _warpSpeeds = Universe.GetSimulationSpeeds().Select(s => Sanitize.Finite(s.Value)).ToArray();
        }
        catch
        {
            _warpSpeeds = [];
        }

        return _warpSpeeds;
    }

    /// <summary>Whether an auto-warp-to-time is running.</summary>
    /// <returns><see langword="true"/> while auto-warp is engaged; false on any failure.</returns>
    [KsaAnchor("Universe.IsAutoWarpActive", SourceFile = "KSA/Universe.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "time.autoWarp active flag (public static bool property :118).")]
    public bool AutoWarpActive()
    {
        try
        {
            return Universe.IsAutoWarpActive;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The target UT of the running auto-warp.</summary>
    /// <returns>The target in seconds, or 0 when none is scheduled (or on any failure).</returns>
    [KsaAnchor("Universe.AutoWarpTime?.Seconds()", SourceFile = "KSA/Universe.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Medium,
        Notes = "time.autoWarpTargetUt; AutoWarpTime is UniverseTime? (:120), null when no auto-warp is scheduled.")]
    public double AutoWarpTargetUt()
    {
        try
        {
            return Universe.AutoWarpTime is { } t ? Sanitize.Finite(t.Seconds()) : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    ///     Diffs the current vessel roster against the previous call and returns the events in between. The
    ///     first call is the baseline and returns none — players care about changes, not the starting roster.
    /// </summary>
    /// <param name="ut">Universal time, in seconds, stamped on the events.</param>
    /// <returns>The new events (possibly empty).</returns>
    [KsaAnchor("Universe.SimulationSpeed; Program.ControlledVehicle?.Id; VesselCache.All()",
        SourceFile = "KSA/Universe.cs / KSA/Program.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Low,
        Notes = "The sampler-direct warp/active reads that feed EventDiffer.Diff; the same fields the gatOS "
            + "sampler used. Universe.SimulationSpeed is a public static property (:122), Program.ControlledVehicle :502.")]
    public IReadOnlyList<SimEvent> DiffEvents(double ut)
    {
        try
        {
            var vessels = All();
            var warp = Sanitize.Finite(Universe.SimulationSpeed);
            var activeId = Program.ControlledVehicle?.Id;
            var events = EventDiffer.Diff(_previous, ut, warp, activeId, vessels);
            _previous = new SimSnapshot(++_sequence, ut, warp, activeId, vessels, events, "", DiffRateHz, []);
            return events;
        }
        catch (Exception ex)
        {
            Log.LogOnce("cache.diff", LogLevel.Warn, $"event diff failed (logged once): {ex.Message}");
            return [];
        }
    }

    private static VesselSnapshot? FindIn(List<VesselSnapshot> all, string id)
    {
        foreach (var vessel in all)
            if (string.Equals(vessel.Id, id, StringComparison.Ordinal))
                return vessel;
        return null;
    }

    [KsaAnchor("VesselReader.Sample(Vehicle, activeId, ut, detail: true); Program.ControlledVehicle?.Id",
        SourceFile = "KSA/Vehicle.cs / KSA/Program.cs", Verified = "2026-10-16", GameVersion = "2026.10.10.5554",
        Risk = ChurnRisk.Low,
        Notes = "One mid-teardown vehicle must not kill the read: failures are swallowed per vehicle (logged once).")]
    private static VesselSnapshot? SampleVehicle(Vehicle vehicle, double ut)
    {
        try
        {
            return VesselReader.Sample(vehicle, Program.ControlledVehicle?.Id, ut, detail: true);
        }
        catch (Exception ex)
        {
            Log.LogOnce("cache.vehicle", LogLevel.Warn, $"a vehicle sample failed (logged once): {ex.Message}");
            return null;
        }
    }

    [KsaAnchor("Universe.CurrentSystem; BodyReader.Sample(CelestialSystem)", SourceFile = "KSA/Universe.cs",
        Verified = "2026-10-16", GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "Celestial catalog sampled at most once per second of wall time (SampleBodiesPaced logic); a new "
            + "CelestialSystem instance (system.load) resamples immediately.")]
    private void SampleBodiesPaced()
    {
        try
        {
            var system = Universe.CurrentSystem;
            if (system is null)
            {
                _bodies = [];
                _system = null;
                _bodiesSystem = null;
                _bodiesTimestamp = 0;
                _bodyViews.Clear();
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (ReferenceEquals(system, _bodiesSystem) && _bodiesTimestamp != 0
                && now - _bodiesTimestamp < (long)(Stopwatch.Frequency * BodiesIntervalSeconds))
                return;

            _bodiesSystem = system;
            _bodiesTimestamp = now;
            _bodyViews.Clear();
            try
            {
                (_bodies, _system) = BodyReader.Sample(system);
            }
            catch (Exception ex)
            {
                Log.LogOnce("cache.bodies", LogLevel.Warn, $"body catalog sample failed (logged once): {ex.Message}");
                _bodies = [];
                _system = null;
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("cache.bodies.outer", LogLevel.Warn, $"body catalog read failed (logged once): {ex.Message}");
        }
    }
}
