using HarmonyLib;
using Catcom.Core.Logging;
using Catcom.Core.Snapshots;
using Catcom.Core.Values;
using Catcom.Ksa.Slots;
using KSA;

namespace Catcom.Ksa.Events;

/// <summary>
///     Installs the Harmony event hooks (Harmony id <c>catcom.events</c>): one patch class per row of
///     the event-hook table, each patched on its own inside a try/catch so a target that disappears in a
///     game update darkens one event, not all of them. Every patch publishes through
///     <see cref="Sink"/> with the canonical event names and payload keys of <c>EventCatalog</c>; the
///     subject vessel's id rides as <c>vesselId</c>. Patch bodies never throw: a fault is logged once and
///     disables that patch class until restart.
/// </summary>
public static class EventPatches
{
    /// <summary>The Harmony id of the event patches.</summary>
    public const string HarmonyId = "catcom.events";

    /// <summary>How deep a sync handler may re-enter an event patch (a handler that triggers its own event).</summary>
    private const int MaxDepth = 4;

    private static readonly (string Name, Type Patch)[] PatchTable =
    [
        ("engine.activate/deactivate", typeof(EngineActivatePatch)),
        ("rcs.activate", typeof(RcsActivatePatch)),
        ("stage", typeof(StagePatch)),
        ("decouple", typeof(DecouplePatch)),
        ("split", typeof(SplitPatch)),
        ("dock", typeof(DockPatch)),
        ("undock", typeof(UndockPatch)),
        ("vessel.spawn", typeof(VesselSpawnPatch)),
        ("vessel.remove", typeof(VesselRemovePatch)),
        ("vessel.launch", typeof(VesselLaunchPatch)),
        ("impact (ground)", typeof(ImpactGroundPatch)),
        ("impact (water)", typeof(ImpactWaterPatch)),
        ("vessel.destroy (cause)", typeof(DestroyFromEventPatch)),
        ("vessel.destroy (removed)", typeof(DestroyRemovedPatch)),
        ("part.failure", typeof(PartFailurePatch)),
        ("control.change", typeof(ControlChangePatch)),
        ("editor.enter", typeof(EditorEnterPatch)),
        ("editor.exit", typeof(EditorExitPatch)),
        ("save.write", typeof(SaveWritePatch)),
        ("save.load (directory)", typeof(SaveLoadDirectoryPatch)),
        ("save.load", typeof(SaveLoadPatch)),
        ("system.load", typeof(SystemLoadPatch)),
        ("warp.change", typeof(WarpChangePatch)),
        ("burn.complete/fuel_out", typeof(BurnAlertsPatch)),
    ];

    private static readonly List<string> InstalledNames = [];
    private static readonly List<string> FailedNames = [];

    [ThreadStatic]
    private static int _depth;

    private static Harmony? _harmony;
    private static volatile IEventSink? _sink;
    private static int _gameThreadId;

    /// <summary>Where the patches publish; null while not installed.</summary>
    public static IEventSink? Sink => _sink;

    /// <summary>The patch rows that installed successfully (for the UI).</summary>
    public static IReadOnlyList<string> Installed => InstalledNames;

    /// <summary>The patch rows that failed to install, with the reason (for the UI).</summary>
    public static IReadOnlyList<string> Failed => FailedNames;

    /// <summary>
    ///     Patches every event row. Idempotent; never throws. Must be called on the game thread: that thread
    ///     is the only one whose events run sync subscribers inline — a patch that happens to fire on another
    ///     thread publishes asynchronously instead.
    /// </summary>
    /// <param name="sink">Where the events are published.</param>
    public static void Install(IEventSink sink)
    {
        if (_harmony is not null)
            return;

        _gameThreadId = Environment.CurrentManagedThreadId;
        _sink = sink;
        InstalledNames.Clear();
        FailedNames.Clear();
        try
        {
            _harmony = new Harmony(HarmonyId);
            foreach (var (name, patch) in PatchTable)
            {
                try
                {
                    var patched = _harmony.CreateClassProcessor(patch).Patch();
                    if (patched is null || patched.Count == 0)
                        throw new InvalidOperationException("the patch class patched nothing");
                    InstalledNames.Add(name);
                }
                catch (Exception ex)
                {
                    FailedNames.Add($"{name}: {ex.Message}");
                    Log.LogOnce("event.install." + name, LogLevel.Error,
                        $"could not patch the '{name}' event hook (that event is dark): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogOnce("event.install", LogLevel.Error, $"event patch install failed: {ex.Message}");
        }

        Log.Info($"catcom event hooks: {InstalledNames.Count}/{PatchTable.Length} installed"
                 + (FailedNames.Count > 0 ? $", {FailedNames.Count} failed" : "") + ".");
    }

    /// <summary>Removes every event patch and detaches the sink. Never throws.</summary>
    public static void Uninstall()
    {
        _sink = null;
        try
        {
            _harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception ex)
        {
            Log.Warn($"event unpatch failed: {ex.Message}");
        }

        _harmony = null;
        InstalledNames.Clear();
    }

    /// <summary>Publishes one event from inside a patch. Re-entrancy is bounded; the sink must not throw.</summary>
    /// <param name="type">The event name.</param>
    /// <param name="vesselId">The subject vessel's id, or null.</param>
    /// <param name="payload">The payload fields.</param>
    internal static void Emit(string type, string? vesselId, IReadOnlyDictionary<string, ScriptValue> payload)
    {
        var sink = _sink;
        if (sink is null)
            return;
        if (_depth >= MaxDepth)
        {
            Log.LogOnce("event.reentrant", LogLevel.Warn,
                $"event '{type}' dropped: sync handlers re-entered the event hooks {MaxDepth} deep.");
            return;
        }

        // Script handlers may only run on the game thread: an event raised elsewhere is queued instead.
        if (Environment.CurrentManagedThreadId != _gameThreadId)
        {
            sink.Publish(type, vesselId, payload, Ut());
            return;
        }

        _depth++;
        try
        {
            sink.PublishSync(type, vesselId, payload, Ut());
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>Builds a payload map from key/value pairs.</summary>
    /// <param name="fields">The fields.</param>
    /// <returns>The payload.</returns>
    internal static IReadOnlyDictionary<string, ScriptValue> Payload(params (string Key, ScriptValue Value)[] fields)
    {
        var payload = new Dictionary<string, ScriptValue>(fields.Length, StringComparer.Ordinal);
        foreach (var (key, value) in fields)
            payload[key] = value;
        return payload;
    }

    /// <summary>Records a patch fault: logged once per patch class, visible at Error level.</summary>
    /// <param name="patch">The patch class name.</param>
    /// <param name="ex">The exception.</param>
    internal static void Fault(string patch, Exception ex)
        => Log.LogOnce("event." + patch, LogLevel.Error,
            $"event patch {patch} faulted and is disabled until restart: {ex}");

    /// <summary>The current universal time in seconds; 0 on any failure.</summary>
    /// <returns>The UT.</returns>
    [KsaAnchor("Universe.GetElapsedSeconds()", SourceFile = "KSA/Universe.cs", Verified = "2026-10-16",
        GameVersion = "2026.10.10.5554", Risk = ChurnRisk.Low,
        Notes = "UT stamp for every Harmony-produced event (= _lastSimStep.NextTime, Universe.cs:2393).")]
    internal static double Ut()
    {
        try
        {
            return Sanitize.Finite(Universe.GetElapsedSeconds());
        }
        catch
        {
            return 0;
        }
    }
}
