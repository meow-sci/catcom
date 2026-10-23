using Catcom.Core.Commands;
using static Catcom.Core.Api.Dsl;

namespace Catcom.Core.Api;

/// <summary>The hand-written classes of the API: the module, the clock, vessels and their module refs.</summary>
internal static class CoreClasses
{
    private static readonly ApiType UntilOptions = new(
        "{ timeoutSeconds?: number; clock?: \"ut\" | \"wall\" | \"render\" }",
        "{ timeout_seconds?: number, clock?: \"ut\"|\"wall\"|\"render\" }");

    private static readonly ApiType WaitEventOptions = new(
        "{ vessel?: string | VesselRef; timeoutSeconds?: number }",
        "{ vessel?: string|ksa.VesselRef, timeout_seconds?: number }");

    private static readonly ApiType EventOptions = new(
        "{ sync?: boolean; vessel?: string | VesselRef }",
        "{ sync?: boolean, vessel?: string|ksa.VesselRef }");

    private static readonly ApiType TickOptions = new(
        "{ clock?: \"ut\" | \"wall\" | \"render\" }",
        "{ clock?: \"ut\"|\"wall\"|\"render\" }");

    private static readonly ApiType TickKind = new("\"frame\" | \"step\" | number", "\"frame\"|\"step\"|number");

    private static readonly ApiType Vec3 = Ty.Ref("Vec3");
    private static readonly ApiType Quat = Ty.Ref("Quat");

    internal static IReadOnlyList<ApiClass> Create() =>
    [
        Module(),
        TimeClass(),
        VesselRef(),
        VesselDebug(),
        Ref("EngineRef", "EngineInfo",
            "A live engine on a vessel: its data fields (copied when you read `vessel.engines`; read the list again for fresh values) plus the methods that control it.",
            Act("setActive", SimActions.EngineActive, "Switches this engine on or off.", P("on", Ty.Boolean, "True to activate the engine, false to deactivate it.")),
            Act("setMinThrottle", SimActions.EngineMinThrottle, "Sets this engine's minimum throttle.", P("f", Ty.Number, "Minimum throttle fraction, 0 to 1."))),
        Ref("RcsRef", "RcsInfo",
            "A live RCS thruster controller: its data fields (a copy taken when you read `vessel.rcs`) plus the method that switches it.",
            Act("setActive", SimActions.RcsActive, "Switches this RCS thruster controller on or off.", P("on", Ty.Boolean, "True to activate, false to deactivate."))),
        Ref("SolarRef", "SolarInfo",
            "A live solar panel: its data fields (a copy taken when you read `vessel.solar`) plus the method that deploys or retracts it.",
            Act("setGoal", SimActions.AnimationGoal,
                "Sets the deploy-animation goal of this panel (1 deployed, 0 retracted). The action targets the panel's `animationIndex`; a fixed panel without an animation returns an `Invalid` result.",
                P("f", Ty.Number, "Goal fraction, 0 to 1."))),
        Ref("LightRef", "LightInfo",
            "A live light: its data fields (a copy taken when you read `vessel.lights`) plus the methods that control it.",
            Act("setOn", SimActions.LightOn, "Switches this light on or off.", P("b", Ty.Boolean, "True for on, false for off.")),
            Act("setBrightness", SimActions.LightBrightness, "Sets this light's intensity.", P("v", Ty.Number, "Light intensity in template units.")),
            Act("setColor", SimActions.LightColor, "Sets this light's colour.",
                P("r", Ty.Number, "Red, 0 to 1."), P("g", Ty.Number, "Green, 0 to 1."), P("b", Ty.Number, "Blue, 0 to 1.")),
            Act("setOuterAngle", SimActions.LightOuterAngle,
                "Sets the spotlight cone outer half-angle (the hard edge of the beam). KSA clamps it to about 0..89.9 degrees.",
                P("deg", Ty.Number, "Angle in degrees.")),
            Act("setInnerAngle", SimActions.LightInnerAngle,
                "Sets the spotlight cone inner half-angle (full brightness inside it, falling off to the outer angle). KSA clamps it to 0..outer.",
                P("deg", Ty.Number, "Angle in degrees.")),
            Act("setGoal", SimActions.AnimationGoal,
                "Sets the actuate-animation goal of this light (1 deployed, 0 retracted). The action targets the light's `animationIndex`; a light without an animation returns an `Invalid` result.",
                P("f", Ty.Number, "Goal fraction, 0 to 1."))),
        Ref("DockingRef", "DockingInfo",
            "A live docking port: its data fields (a copy taken when you read `vessel.docking`) plus the method that undocks it.",
            Act("undock", SimActions.DockingUndock, "Undocks this docking port from whatever it is docked to.")),
        Ref("DecouplerRef", "DecouplerInfo",
            "A live decoupler: its data fields (a copy taken when you read `vessel.decouplers`) plus the method that fires it.",
            Act("fire", SimActions.DecouplerFire, "Fires this decoupler, separating the stages it joins.")),
        Ref("AnimationRef", "AnimationInfo",
            "A live part animation (deployables, doors, actuators): its data fields (a copy taken when you read `vessel.animations`) plus the method that moves it.",
            Act("setGoal", SimActions.AnimationGoal, "Sets this animation's goal.", P("f", Ty.Number, "Goal fraction, 0 (start) to 1 (end).")))
    ];

    private static ApiClass Ref(string name, string extends, string doc, params ApiMember[] members)
        => new(name, doc, members, extends);

    private static ApiClass Module() => new("ksa",
        "The catcom API. Available to every script as the global `ksa`<ts>, or with `import { ksa } from \"ksa\"`</ts>. " +
        "Reads are live. Waiting functions (`sleep`, `nextStep`, ...) <ts>return a Promise</ts><lua>yield the running coroutine, so call them from `main`, a hook or a service</lua>. " +
        "Write methods return a `CommandResult`; Solver-phase writes (flight-computer setpoints and debug cheats) wait for the solver slot first.",
        [
            Prop("time", Ty.Ref("Time"), "The game clock: universal time, warp, step and frame counters, and the warp control.", "time"),
            Prop("active", Ty.Optional(Ty.Ref("VesselRef")), "The vessel the player currently controls, or undefined (nil in Lua) when there is none.", "time.activeVesselId"),
            Prop("system", Ty.Ref("SystemInfo"), "The loaded planetary system: its name and the ids of its home body and sun.", "system"),
            Prop("console", Ty.Ref("Console"), "Logging to the mod log and the in-game console, and running game console commands."),
            Prop("state", Ty.Ref("State"),
                "Persistent per-script state, saved with the save game. Use `ksa.state.get(key)` and `ksa.state.set(key, value)`<ts>; TypeScript can also use it as a plain object (`ksa.state.counter = 1`)</ts><lua>; plain field access (`ksa.state.counter = 1`) works too</lua>."),
            Wait("sleep", Ty.Void,
                "Waits for the given amount of time. <ts>Use `await ksa.sleep(2)`.</ts><lua>`ksa.sleep(2)` yields the coroutine until the time has passed.</lua>",
                P("seconds", Ty.Number, "How long to wait, in seconds on the chosen clock."),
                O("clock", Ty.Clock, "`\"ut\"` (simulation time, the default; stops while paused and scales with warp), `\"wall\"` (real time) or `\"render\"` (unscaled player frame time).")),
            Wait("nextStep", Ty.Ref("StepInfo"),
                "Waits for the next simulation step that actually advanced (paused frames do not count) and returns its timing."),
            Wait("nextFrame", Ty.Void, "Waits for the next rendered frame."),
            Wait("until", Ty.Boolean,
                "Waits until `pred()` returns true. The predicate is evaluated once per step slot. Resolves true, or false when the timeout elapses first.<ts> (In Lua this is `ksa.wait_until`, because `until` is a reserved word there.)</ts>",
                P("pred", Ty.Func("", "boolean", "", "boolean"), "Returns true when the wait should end."),
                O("opts", UntilOptions, "`timeoutSeconds` gives up after that long on `clock` (default `\"ut\"`).")),
            Wait("waitEvent", Ty.Nullable(Ty.Ref("EventInfo")),
                "Waits for the next matching event and returns it, or null (nil in Lua) when the timeout elapses first.<ts> (In Lua this is `ksa.wait_event`.)</ts>",
                P("name", Ty.EventName, "The event name, e.g. `\"docked\"`."),
                O("opts", WaitEventOptions, "`vessel` restricts to one vessel (id or VesselRef); `timeoutSeconds` gives up after that much simulation time.")),
            Fn("vessels", Ty.Array(Ty.Ref("VesselRef")), "Every vessel in the loaded system."),
            Fn("vessel", Ty.Optional(Ty.Ref("VesselRef")), "Looks a vessel up by id; returns undefined (nil in Lua) when there is no such vessel.",
                P("id", Ty.String, "The vessel id.")),
            Fn("bodies", Ty.Array(Ty.Ref("BodyInfo")), "Every celestial body in the loaded system, as read-only data."),
            Fn("body", Ty.Optional(Ty.Ref("BodyInfo")), "Looks a celestial body up by id; returns undefined (nil in Lua) when there is no such body.",
                P("id", Ty.String, "The body id.")),
            Fn("on", Ty.Ref("Subscription"),
                "Runs `handler` whenever the event fires. By default the handler runs in the next step slot and may wait; with `sync` it runs inside the game call that raised the event, under a tight time budget, and must not wait.",
                P("event", Ty.EventName, "The event name; see the `Events` interface for the catalog."),
                P("handler", Ty.Func("ctx: EventContext", "void", "ctx: ksa.EventContext"), "Called with an EventContext; may be an async function unless `sync` is set."),
                O("opts", EventOptions, "`sync` runs the handler inline; `vessel` restricts the hook to one vessel (id or VesselRef).")),
            Fn("every", Ty.Ref("Subscription"),
                "Runs `handler` every frame, every landed simulation step, or every given number of seconds.",
                P("kind", TickKind, "`\"frame\"`, `\"step\"`, or an interval in seconds."),
                P("handler", Ty.Func("ctx: TickContext", "void", "ctx: ksa.TickContext"), "Called with a TickContext; may be an async function."),
                O("opts", TickOptions, "`clock` selects the clock an interval is measured on (default `\"ut\"`).")),
            Fn("service", Ty.Ref("Subscription"),
                "Registers a long-running service: `fn` starts when the script loads and should loop until `ctx.signal.cancelled` becomes true.",
                P("fn", Ty.Func("ctx: ServiceContext", "Promise<void>", "ctx: ksa.ServiceContext"), "The service body; may wait freely.")),
            Fn("command", Ty.Ref("Subscription"),
                "Registers a game console command that runs `fn` with the text typed after the command name.",
                P("name", Ty.String, "The console command name."),
                P("fn", Ty.Func("args: string", "void", "args: string"), "Called with the argument text."),
                O("description", Ty.String, "One line shown in the command's help.")),
            LuaFn("export", Ty.Void,
                "Exports a function so the mod can bind it to events, ticks or a terminal (Lua scripts may instead return a table of functions; on a name clash the returned table wins).",
                P("name", Ty.String, "The export name."),
                P("fn", new ApiType("Function", "function"), "The function to export."))
        ]);

    private static ApiClass TimeClass() => new("Time",
        "The game clock. Every property is read live from the game on access.",
        [
            View("ut", Ty.Number, "Universal time, in seconds."),
            View("warp", Ty.Number, "The current time-warp factor."),
            View("simDt", Ty.Number, "Duration of the last simulation step, in seconds."),
            View("paused", Ty.Boolean, "Whether the simulation is paused."),
            View("warpSpeeds", Ty.Array(Ty.Number), "The selectable warp factors."),
            View("autoWarp", Ty.Boolean, "Whether automatic time warp is engaged."),
            View("autoWarpTargetUt", Ty.Number, "The universal time automatic warp is heading for, in seconds (0 when none)."),
            View("frame", Ty.Number, "The rendered frame counter."),
            View("stepIndex", Ty.Number, "The simulation step counter."),
            Act("setWarp", SimActions.DebugWarp, "Sets the time-warp factor.", P("f", Ty.Number, "The warp factor (one of `warpSpeeds`)."))
        ]);

    private static ApiClass VesselRef() => new("VesselRef",
        "A live handle to one vessel. It holds only the vessel id: every property read goes to the game, so values are always current, and a vessel that has vanished reads as null or empty. " +
        "Setter-style methods are named `setX`; verbs (`ignite`, `stage`, `focus`) keep their names. Frame-phase writes return a `CommandResult`; flight-computer and debug writes are Solver-phase and wait for the solver slot.",
        [
            View("id", Ty.String, "The vessel id."),
            View("name", Ty.String, "The vessel's display name."),
            View("situation", Ty.String, "The flight situation (Landed, Freefall, Rolling, ...)."),
            View("parent", Ty.String, "The name of the body the vessel is orbiting or standing on (empty when unknown)."),
            View("controlled", Ty.Boolean, "Whether this is the vessel the player controls."),
            View("controllable", Ty.Boolean, "Whether the vessel can currently be controlled."),
            View("isKitten", Ty.Boolean, "Whether the vessel is a kitten (EVA crew)."),
            View("position", Ty.Ref("Position"), "Where the vessel is: parent-centred inertial and ecliptic positions in metres, plus latitude and longitude in degrees."),
            View("velocity", Ty.Ref("Velocity"), "How fast the vessel moves: orbital, surface and inertial speeds in m/s, and the velocity vector."),
            View("attitude", Ty.Ref("Attitude"), "The vessel's orientation (body to CCI quaternion) and angular rates in rad/s."),
            View("altitude", Ty.Ref("Altitude"), "Barometric and radar altitude, in metres."),
            View("mass", Ty.Ref("Mass"), "Total, dry and propellant mass in kilograms, and the centre of mass."),
            View("orbit", Ty.Nullable(Ty.Ref("OrbitInfo")), "The current orbit, or null (nil in Lua) when the vessel is not on one."),
            View("navball", Ty.Nullable(Ty.Ref("NavballInfo")), "Navball readouts, or null when unavailable."),
            View("environment", Ty.Nullable(Ty.Ref("EnvironmentInfo")), "Atmosphere and acceleration readouts, or null when unavailable."),
            View("power", Ty.Ref("Power"), "Electrical power produced and consumed, in watts."),
            View("battery", Ty.Ref("Battery"), "Battery charge fraction and capacity (both null when the vessel has no battery)."),
            View("engines", Ty.Array(Ty.Ref("EngineRef")), "The vessel's engines. Each entry is a snapshot with control methods; read the property again for fresh values."),
            View("tanks", Ty.Array(Ty.Ref("TankInfo")), "Propellant tanks by resource."),
            View("rcs", Ty.Array(Ty.Ref("RcsRef")), "The vessel's RCS thruster controllers."),
            View("solar", Ty.Array(Ty.Ref("SolarRef")), "The vessel's solar panels."),
            View("generators", Ty.Array(Ty.Ref("GeneratorInfo")), "The vessel's power generators."),
            View("lights", Ty.Array(Ty.Ref("LightRef")), "The vessel's lights."),
            View("docking", Ty.Array(Ty.Ref("DockingRef")), "The vessel's docking ports."),
            View("decouplers", Ty.Array(Ty.Ref("DecouplerRef")), "The vessel's decouplers."),
            View("animations", Ty.Array(Ty.Ref("AnimationRef")), "The vessel's part animations (deployables, doors, actuators)."),
            View("srb", Ty.Array(Ty.Ref("SrbInfo")), "Solid rocket boosters with their internal ballistics."),
            View("encounters", Ty.Array(Ty.Ref("EncounterInfo")), "Predicted encounters with other bodies."),
            View("parts", Ty.Array(Ty.Ref("PartInfo")), "Every part on the vessel."),
            View("throttleCmd", Ty.Number, "The commanded throttle, 0 to 1."),
            View("attitudeMode", Ty.String, "The flight computer's attitude mode (Manual, Prograde, ...)."),
            View("attitudeFrame", Ty.String, "The flight computer's attitude reference frame."),
            View("rcsMode", Ty.String, "The flight computer's RCS mode (`Enabled` or `Disabled`)."),
            View("rcsOn", Ty.Boolean, "Whether RCS is switched on."),
            View("engineOn", Ty.Boolean, "Whether any engine is switched on."),
            View("scale", Ty.Number, "The visual scale factor (1 is normal size)."),
            View("alwaysRender", Ty.Boolean, "Whether the vessel is force-rendered at any distance."),
            View("translateCmd", Vec3, "The latched manual translation input (see translate)."),
            View("rotateCmd", Vec3, "The latched manual rotation input (see rotate)."),
            Prop("debug", Ty.Ref("VesselDebug"), "Cheat actions: teleport, impulse, refills and taking control."),
            Fn("snapshot", new ApiType("Record<string, unknown>", "table<string, any>"),
                "Reads the complete raw snapshot record of this vessel in one call (every field the game samples, with the game's own field names)."),
            Act("ignite", SimActions.VesselIgnite, "Ignites the vessel's engines."),
            Act("shutdown", SimActions.VesselShutdown, "Shuts the vessel's engines down."),
            Act("setEngine", SimActions.VesselEngine, "Master engine switch.", P("on", Ty.Boolean, "True to switch engines on, false to switch them off.")),
            Act("stage", SimActions.VesselStage, "Activates the next stage."),
            Act("setThrottle", SimActions.VesselThrottle, "Sets the throttle command.", P("f", Ty.Number, "Throttle fraction, 0 to 1.")),
            Act("setLights", SimActions.VesselLights, "Master lights switch.", P("on", Ty.Boolean, "True for on, false for off.")),
            Act("setRcs", SimActions.VesselRcs, "Master RCS switch.", P("on", Ty.Boolean, "True for on, false for off.")),
            Act("translate", SimActions.VesselTranslate,
                "Latches a manual RCS translation input along the vessel body axes. Only the sign of each component matters (bang-bang thrust, 0 turns that axis off); it stays in effect until you set it again, so send `0, 0, 0` to stop. While the engines are thrusting (or an automatic burn is armed) the flight computer uses a thrust-aligned frame instead, with +X along the net thrust.",
                P("x", Ty.Number, "Forward (+) or back (-) along the nose axis."), P("y", Ty.Number, "Right (+) or left (-)."), P("z", Ty.Number, "Down (+) or up (-).")),
            Act("rotate", SimActions.VesselRotate,
                "Latches a manual RCS rotation input about the vessel body axes. Only the sign of each component matters (bang-bang torque, 0 turns that axis off); it stays in effect until you set it again, so send `0, 0, 0` to stop. While the engines are thrusting (or an automatic burn is armed) the flight computer uses a thrust-aligned frame instead, with +X along the net thrust.",
                P("x", Ty.Number, "Roll right (+) or left (-)."), P("y", Ty.Number, "Pitch up (+) or down (-)."), P("z", Ty.Number, "Yaw right (+) or left (-).")),
            Act("setScale", SimActions.VesselScale, "Sets the vessel's uniform visual scale.", P("f", Ty.Number, "Scale factor, greater than 0 (1 is normal size).")),
            Act("setAlwaysRender", SimActions.VesselAlwaysRender, "Keeps the vessel rendered at any distance.", P("on", Ty.Boolean, "True to force rendering.")),
            Act("focus", SimActions.CameraFocus, "Points the camera at this vessel."),
            Act("setAttitudeMode", SimActions.VesselAttitudeMode,
                "Sets the flight computer's attitude mode (for example `\"Prograde\"`). Waits for the solver slot.",
                P("m", Ty.String, "The attitude mode name.")),
            Act("setAttitudeFrame", SimActions.VesselAttitudeFrame,
                "Sets the flight computer's attitude reference frame. Waits for the solver slot.",
                P("f", Ty.String, "The frame name.")),
            Act("setAttitudeTarget", SimActions.VesselAttitudeTarget,
                "Sets the flight computer's target attitude as a quaternion; the flight computer turns its control frame (the command part's axes) to match it. While the engines are thrusting (or an automatic burn is armed) that control frame is thrust-aligned instead, with +X along the net thrust. Waits for the solver slot.",
                P("q", Quat, "The target orientation.")),
            Act("setRcsMode", SimActions.VesselRcsMode,
                "Enables or disables RCS in the flight computer. Waits for the solver slot.",
                P("m", new ApiType("\"Enabled\" | \"Disabled\"", "\"Enabled\"|\"Disabled\""), "`\"Enabled\"` or `\"Disabled\"`.")),
            Act("burn", SimActions.VesselBurn,
                "Plans an automatic burn of the given delta-v at the given time. Waits for the solver slot.",
                P("ut", Ty.Number, "Universal time at which the burn starts, in seconds."),
                P("dv", Vec3, "Delta-v vector in the parent body's CCI frame, in m/s."))
        ]);

    private static ApiClass VesselDebug() => new("VesselDebug",
        "Cheat actions on a vessel (`vessel.debug`). They bypass the game's rules, so use them for testing and tools.",
        [
            Act("teleport", SimActions.DebugTeleport,
                "Teleports the vessel to a position and velocity in its parent body's CCI frame. Waits for the solver slot.",
                P("pos", Vec3, "Position in metres."), P("vel", Vec3, "Velocity in m/s.")),
            Act("impulse", SimActions.DebugImpulse,
                "Applies an instantaneous impulse. Waits for the solver slot.",
                P("v", Vec3, "The impulse vector."),
                O("frame", new ApiType("\"cci\" | \"body\"", "\"cci\"|\"body\""), "`\"cci\"` (default) or `\"body\"` (vessel frame, +X toward the nose)."),
                O("unit", new ApiType("\"ns\" | \"dv\"", "\"ns\"|\"dv\""), "`\"ns\"` newton-seconds (default) or `\"dv\"` metres per second.")),
            Act("refillFuel", SimActions.DebugRefillFuel, "Refills every propellant tank. Waits for the solver slot."),
            Act("refillBattery", SimActions.DebugRefillBattery, "Recharges every battery. Waits for the solver slot."),
            Act("control", SimActions.DebugControlVessel, "Switches player control to this vessel.")
        ]);
}
