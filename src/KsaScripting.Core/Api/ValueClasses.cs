using static KsaScripting.Core.Api.Dsl;

namespace KsaScripting.Core.Api;

/// <summary>The small value and service types the API hands to scripts (vectors, readouts, results, console, state, ...).</summary>
internal static class ValueClasses
{
    private static readonly ApiType Vec3 = Ty.Ref("Vec3");

    internal static IReadOnlyList<ApiClass> Create() =>
    [
        new("Vec3", "A three-component vector.",
        [
            View("x", Ty.Number, "X component."),
            View("y", Ty.Number, "Y component."),
            View("z", Ty.Number, "Z component.")
        ]),
        new("Quat", "A unit quaternion (x, y, z, w).",
        [
            View("x", Ty.Number, "X component."),
            View("y", Ty.Number, "Y component."),
            View("z", Ty.Number, "Z component."),
            View("w", Ty.Number, "Scalar component.")
        ]),
        new("Position", "Where a vessel is.",
        [
            View("cci", Vec3, "Position in the parent body's body-centred inertial (CCI) frame, in metres."),
            View("ecl", Vec3, "Position in the system's ecliptic frame, in metres."),
            View("lat", Ty.Number, "Latitude over the parent body, in degrees."),
            View("lon", Ty.Number, "Longitude over the parent body, in degrees.")
        ]),
        new("Velocity", "How fast a vessel moves.",
        [
            View("orbital", Ty.Number, "Orbital speed, in m/s."),
            View("surface", Ty.Number, "Speed relative to the rotating surface, in m/s."),
            View("inertial", Ty.Number, "Inertial speed, in m/s."),
            View("cci", Vec3, "Velocity vector in the parent body's CCI frame, in m/s.")
        ]),
        new("Attitude", "A vessel's orientation and rotation rate.",
        [
            View("quat", Ty.Ref("Quat"), "Body-to-CCI rotation."),
            View("rates", Vec3, "Angular rates about the body axes, in rad/s.")
        ]),
        new("Altitude", "A vessel's altitude, in metres.",
        [
            View("barometric", Ty.Number, "Altitude above the reference surface used for pressure."),
            View("radar", Ty.Number, "Height above the terrain below.")
        ]),
        new("Mass", "A vessel's mass properties.",
        [
            View("total", Ty.Number, "Total mass, in kilograms."),
            View("dry", Ty.Number, "Mass without propellant, in kilograms."),
            View("propellant", Ty.Number, "Propellant mass, in kilograms."),
            View("centerOfMass", Vec3, "Centre of mass in the vessel frame, in metres.")
        ]),
        new("Power", "A vessel's electrical power balance.",
        [
            View("producedW", Ty.Number, "Power produced, in watts."),
            View("consumedW", Ty.Number, "Power consumed, in watts.")
        ]),
        new("Battery", "A vessel's battery state; both fields are null when the vessel has no battery.",
        [
            View("fraction", Ty.Nullable(Ty.Number), "Charge fraction, 0 to 1."),
            View("capacityJ", Ty.Nullable(Ty.Number), "Capacity, in joules.")
        ]),
        new("SystemInfo", "The loaded planetary system.",
        [
            View("name", Ty.String, "The system name."),
            View("home", Ty.Nullable(Ty.String), "The id of the home body, or null."),
            View("sun", Ty.Nullable(Ty.String), "The id of the sun, or null.")
        ]),
        new("CommandResult", "The outcome of a write. `ok` is true exactly when `outcome` is `\"Ok\"`.",
        [
            View("ok", Ty.Boolean, "Whether the command applied."),
            View("outcome", new ApiType(
                "\"Ok\" | \"Invalid\" | \"NotFound\" | \"Denied\" | \"Busy\" | \"Fault\" | \"TimedOut\" | \"Unsupported\"",
                "\"Ok\"|\"Invalid\"|\"NotFound\"|\"Denied\"|\"Busy\"|\"Fault\"|\"TimedOut\"|\"Unsupported\""),
                "`Ok`; `Invalid` (bad argument); `NotFound` (the vessel or module vanished); `Denied` (control disabled in the config); `Busy` (cannot fire right now); `Fault` (a game API failed); `TimedOut` (the game thread did not run it in time); `Unsupported` (disabled after a fault, or not available)."),
            View("message", Ty.Nullable(Ty.String), "A human-readable detail, or null.")
        ]),
        new("StepInfo", "Timing of one simulation step.",
        [
            View("ut", Ty.Number, "Universal time at the step, in seconds."),
            View("dt", Ty.Number, "Duration of the step, in seconds."),
            View("stepIndex", Ty.Number, "The simulation step counter."),
            View("frame", Ty.Number, "The rendered frame counter.")
        ]),
        new("EventInfo", "A game event as delivered to hooks and returned by `waitEvent`.",
        [
            View("ut", Ty.Number, "Universal time when the event was raised, in seconds."),
            View("type", Ty.EventName, "The event name."),
            View("vesselId", Ty.Nullable(Ty.String), "The vessel the event concerns, or null."),
            View("payload", Ty.UnknownMap, "Event-specific fields; see the `Events` interface for each event's payload.")
        ]),
        new("Subscription", "A registered hook. Hooks are removed automatically when the script unloads or reloads.",
        [
            View("id", Ty.String, "The subscription id."),
            Fn("unsubscribe", Ty.Void, "Removes the hook. Safe to call more than once.")
        ]),
        new("Signal", "The cancellation signal of a service, main or terminal script.",
        [
            View("cancelled", Ty.Boolean, "True once the script is being stopped or reloaded; long-running loops should check it."),
            Fn("onCancel", Ty.Void, "Runs `fn` when the script is being stopped.",
                P("fn", Ty.Func("", "void", ""), "Called once on cancellation."))
        ]),
        new("State", "Persistent per-script state, saved in the save game's sidecar and restored on load. Values must be plain data (numbers, strings, booleans, lists, maps); a stored value is returned exactly as it was stored.<ts> TypeScript can also use it as a plain object (`ksa.state.counter = 1`).</ts>",
        [
            Fn("get", Ty.Unknown, "Reads a stored value; null (nil in Lua) when the key is unset.", P("key", Ty.String, "The state key.")),
            Fn("set", Ty.Void, "Stores a value; storing null (nil in Lua) clears the key.",
                P("key", Ty.String, "The state key."), P("value", Ty.Unknown, "The value to store."))
        ]),
        new("Console", "Logging and game console access (`ksa.console`, also `ctx.log`). Arguments are joined with a space; non-strings are formatted readably.",
        [
            Fn("log", Ty.Void, "Writes an info line to the mod log and the in-game console.", P("...args", Ty.Array(Ty.Unknown), "Values to print.")),
            Fn("info", Ty.Void, "Writes an info line (same as `log`).", P("...args", Ty.Array(Ty.Unknown), "Values to print.")),
            Fn("warn", Ty.Void, "Writes a warning line.", P("...args", Ty.Array(Ty.Unknown), "Values to print.")),
            Fn("error", Ty.Void, "Writes an error line.", P("...args", Ty.Array(Ty.Unknown), "Values to print.")),
            Fn("debug", Ty.Void, "Writes a debug line (hidden unless the log level allows it).", P("...args", Ty.Array(Ty.Unknown), "Values to print.")),
            Fn("exec", Ty.Void, "Runs a game console command, as if typed into the in-game console.", P("command", Ty.String, "The console command line."))
        ]),
        new("ScriptInfo", "Identifies the running script.",
        [
            View("name", Ty.String, "The script name."),
            View("path", Ty.String, "The script's file path.")
        ])
    ];
}
