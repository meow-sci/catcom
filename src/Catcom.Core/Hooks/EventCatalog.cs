using Catcom.Core.Hosting;

namespace Catcom.Core.Hooks;

/// <summary>
///     The canonical v1 script event catalog (guide §5): every event name a script may pass to
///     <c>ksa.on</c> / <c>ksa.waitEvent</c>, with its description, payload field names (camelCase) and
///     producer. Producers: <c>"harmony"</c> (a Harmony patch in Catcom.Ksa, delivered through
///     <c>EventBus.PublishSync</c>), <c>"differ"</c> (the snapshot differ ported from gatOS; the gatOS
///     hyphenated names are kept as aliases of the dotted ones), <c>"terminal"</c> (a script's own
///     virtual terminal; targeted at the owning script) and <c>"host"</c> (raised by the mod itself).
///     The API schema, the generated stubs, the Script Manager's event picker and the game bridge's
///     <c>IGameBridge.EventCatalog</c> all read this one list.
/// </summary>
public static class EventCatalog
{
    private static EventDescriptor E(string name, string description, string producer, params string[] payload)
        => new(name, description, payload, producer);

    /// <summary>Every v1 event, in documentation order.</summary>
    public static IReadOnlyList<EventDescriptor> All { get; } =
    [
        // ---- Harmony intent/transition hooks (sync-capable) ---------------------------------
        E("engine.activate", "An engine controller was switched on (manual, staging or script); fires at the call, before the engine physically ignites.", "harmony", "engine"),
        E("engine.deactivate", "An engine controller was switched off.", "harmony", "engine"),
        E("rcs.activate", "An RCS thruster controller was switched on or off (see payload.active).", "harmony", "thruster", "active"),
        E("stage", "The vessel activated its next stage (sequence).", "harmony", "sequence"),
        E("decouple", "A decoupler fired; payload.newVesselId is the vessel that separated.", "harmony", "newVesselId"),
        E("split", "The vessel split in two (decouple, undock or debris); payload.newVesselId is the new vessel.", "harmony", "newVesselId"),
        E("dock", "Two vessels docked; vesselId is the surviving vessel.", "harmony", "otherVesselId"),
        E("undock", "A docking port undocked; payload.newVesselId is the vessel that separated.", "harmony", "newVesselId"),
        E("vessel.spawn", "A vessel was registered in the current system (launch, split, EVA, load).", "harmony", "name"),
        E("vessel.remove", "A vessel was deregistered from the current system (destroyed, recovered, merged).", "harmony", "name"),
        E("vessel.launch", "The vessel was marked as launched (liftoff).", "harmony"),
        E("impact", "A ground or water impact effect was spawned.", "harmony", "kind", "body"),
        E("vessel.destroy", "The vessel is about to be destroyed; payload.cause is the destruction cause or \"removed\".", "harmony", "cause", "peakG", "peakQ"),
        E("part.failure", "One or more parts failed.", "harmony", "parts", "destroyWholeVehicle"),
        E("control.change", "The player took control of another vessel.", "harmony", "from", "to"),
        E("editor.enter", "The vehicle editor opened.", "harmony", "newVehicle"),
        E("editor.exit", "The vehicle editor closed.", "harmony"),
        E("save.write", "A save game was written; payload.directory is its folder.", "harmony", "directory"),
        E("save.load", "A save game finished loading.", "harmony", "directory"),
        E("system.load", "A planetary system was loaded.", "harmony", "system"),
        E("warp.change", "The simulation speed (time warp) changed.", "harmony", "from", "to"),
        E("burn.complete", "The flight computer finished an automatic burn.", "harmony"),
        E("burn.fuel_out", "The flight computer stopped an automatic burn because propellant ran out; payload.stageRanDry is true when only the current stage ran dry and the burn stays planned for the next stage.", "harmony", "stageRanDry"),

        // ---- Snapshot differ (gatOS /sim/events parity; evaluated ~10 times per second) -----
        E("engine.ignite", "An engine physically ignited (differ; alias of engine-state on).", "differ", "engine"),
        E("engine.shutdown", "An engine physically shut down (differ; alias of engine-state off).", "differ", "engine"),
        E("soi.change", "The vessel's parent body (sphere of influence) changed.", "differ", "from", "to"),
        E("situation.change", "The vessel's situation changed (Landed, Freefall, Rolling, …): covers liftoff, landing and splashdown.", "differ", "from", "to"),
        E("flameout", "A running engine lost its propellant supply.", "differ", "detail", "engine"),
        E("battery.depleted", "The vessel's battery reached empty.", "differ"),
        E("battery.charged", "The vessel's battery reached full.", "differ"),
        E("animation.complete", "A part animation reached its goal.", "differ", "animation"),
        E("docked", "A docking port reported docked (gatOS differ name).", "differ", "detail", "port"),
        E("undocked", "A docking port reported undocked (gatOS differ name).", "differ", "detail", "port"),
        E("decoupled", "A decoupler reported fired (gatOS differ name).", "differ", "detail", "decoupler"),
        E("active-changed", "The controlled vessel changed (gatOS differ name).", "differ", "detail", "from", "to"),
        E("warp-changed", "The warp factor changed (gatOS differ name).", "differ", "detail", "from", "to"),
        E("vessel-appeared", "A vessel appeared in the roster (gatOS differ name).", "differ", "detail", "name"),
        E("vessel-removed", "A vessel left the roster (gatOS differ name).", "differ", "detail", "name"),
        E("situation-change", "gatOS differ name of situation.change.", "differ", "detail", "from", "to"),
        E("soi-changed", "gatOS differ name of soi.change.", "differ", "detail", "from", "to"),
        E("engine-state", "An engine changed state (gatOS differ name; see engine.ignite / engine.shutdown).", "differ", "detail", "engine", "active"),
        E("animation-complete", "gatOS differ name of animation.complete.", "differ", "detail", "animation"),
        E("battery-depleted", "gatOS differ name of battery.depleted.", "differ", "detail"),
        E("battery-charged", "gatOS differ name of battery.charged.", "differ", "detail"),

        // ---- Script terminal (delivered only to the owning script) ---------------------------
        E("term.data", "Input bytes arrived on the script's terminal (keys, mouse reports, replies).", "terminal", "data", "termId"),
        E("term.resize", "The terminal grid changed (smallest attached surface).", "terminal", "cols", "rows", "termId"),
        E("term.attach", "A surface attached to the script's terminal.", "terminal", "attached", "termId"),
        E("term.detach", "A surface detached from the script's terminal.", "terminal", "attached", "termId"),
        E("term.cancel", "Ctrl+C (0x03) was pressed in the script's terminal.", "terminal", "termId"),

        // ---- Host ---------------------------------------------------------------------------
        E("signal.cancel", "The script is being stopped; delivered only to that script (ctx.signal.onCancel).", "host"),
    ];

    /// <summary>
    ///     gatOS differ names → the dotted v1 alias also published for them. <c>engine-state</c> maps to
    ///     <c>engine.ignite</c> when <c>active</c> is true and <c>engine.shutdown</c> otherwise (handled
    ///     by the producer); it is listed here with its "on" alias.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DifferAliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["situation-change"] = "situation.change",
        ["soi-changed"] = "soi.change",
        ["engine-state"] = "engine.ignite",
        ["animation-complete"] = "animation.complete",
        ["battery-depleted"] = "battery.depleted",
        ["battery-charged"] = "battery.charged",
    };

    private static readonly Dictionary<string, EventDescriptor> ByName =
        All.ToDictionary(e => e.Name, StringComparer.Ordinal);

    /// <summary>Looks up an event by exact name; null when it is not a catalog event.</summary>
    public static EventDescriptor? Find(string name) => ByName.GetValueOrDefault(name);
}
