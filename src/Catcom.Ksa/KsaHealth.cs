// Ported from gatOS (MIT) gatOS.GameMod/Game/Ksa/KsaHealth.cs; behaviour unchanged.
using Catcom.Core.Logging;
using Catcom.Core.Snapshots;

namespace Catcom.Ksa;

/// <summary>
///     Per-accessor health latches (KSA_GAME_INTEGRATION_PLAN §3.4). When a reader or actuator's
///     KSA call throws — typically the decomp lagging the shipping binary — its accessor is
///     latched degraded: the fault is logged once, surfaced in <c>/sim/status/accessors</c>, and
///     the rest of gatOS keeps running (the guest "sees" a failed sensor, like real hardware,
///     instead of the mod crashing). A subsequent success clears the latch.
/// </summary>
/// <remarks>
///     Game-thread only by contract: both the sampler (reads) and the command drain (actuations)
///     run on the game thread, so this needs no locking. The published
///     <see cref="AccessorHealthSnapshot"/> list is what crosses to the 9p threads.
/// </remarks>
public sealed class KsaHealth
{
    private readonly Dictionary<string, AccessorHealthSnapshot> _degraded = new();

    /// <summary>True while the named accessor is latched degraded (callers may skip it → EOPNOTSUPP).</summary>
    public bool IsDegraded(string accessor) => _degraded.ContainsKey(accessor);

    /// <summary>Latches <paramref name="accessor"/> degraded on first fault; logs once.</summary>
    public void Fault(string accessor, double utSeconds, string error)
    {
        if (_degraded.ContainsKey(accessor))
            return;
        _degraded[accessor] = new AccessorHealthSnapshot(accessor, utSeconds, error);
        Log.Warn($"KSA accessor '{accessor}' degraded (logged once): {error}");
    }

    /// <summary>Clears the latch when an accessor succeeds again (logs the recovery once).</summary>
    public void Clear(string accessor)
    {
        if (_degraded.Remove(accessor))
            Log.Info($"KSA accessor '{accessor}' recovered.");
    }

    /// <summary>Snapshot of the currently degraded accessors for <c>/sim/status/accessors</c>.</summary>
    public IReadOnlyList<AccessorHealthSnapshot> Snapshot()
        => _degraded.Count == 0 ? [] : _degraded.Values.ToArray();
}
