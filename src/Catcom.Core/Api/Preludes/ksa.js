// ksa.js - the script-side half of the catcom host contract (JavaScript / TypeScript).
//
// This file is an ES module registered with the V8 runtime under the specifier "ksa". It is
// dependency-free and built ONLY on the `__host` object documented in the implementation guide
// (section 3.7): read, snapshot, list, act, wait, subscribe, unsubscribe, log, consoleExec,
// stateGet, stateSet, termWrite, termInfo, termSetTitle, termPolicy.
//
// The ergonomic object model (VesselRef with getters and methods, EngineRef, ...) lives here, in
// the script language, so there is no per-member interop binding. A ref holds only its handle
// ("vessel:<id>") and, for module refs, the vessel id and module ordinal; every property read is a
// fresh `__host.read` and every method is one `__host.act`.
//
// Conventions
//  * Method naming: value-setting methods carry a `set` prefix (setThrottle), verbs stay verbs
//    (ignite, stage, focus). Getters are plain properties (throttleCmd, engines, ...).
//  * Frame-phase actions return the CommandResult object directly; Solver-phase actions are `async`
//    and return a Promise of it (the host hands back a pending wait that settles after the solver
//    slot ran).
//  * `act` argument order: (action, vesselId, ordinal, value, values, token, aux). Unused arguments
//    are null; ordinal is -1 for vessel-level actions; value is 0 when unused; booleans are 1 / 0.
//
// The API surface is declared once in C# (ApiSchema); a test keeps this file and the schema in sync.

const host = globalThis.__host;
if (host === undefined || host === null) {
  throw new Error("ksa: the host object (__host) is not available in this runtime");
}

// ---------------------------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------------------------

/** Defines a non-enumerable own property (keeps handles out of JSON / console output). */
function hide(target, name, value) {
  Object.defineProperty(target, name, { value, enumerable: false, writable: true, configurable: true });
}

/** Copies `source` without its null / undefined members (option maps cross the host as plain maps). */
function compact(source) {
  const out = {};
  for (const key of Object.keys(source)) {
    const value = source[key];
    if (value !== undefined && value !== null) out[key] = value;
  }
  return out;
}

/** Normalises a vessel argument (a VesselRef, an id string, or nothing) to an id string or undefined. */
function vesselIdOf(vessel) {
  if (vessel === undefined || vessel === null) return undefined;
  if (typeof vessel === "string") return vessel;
  if (typeof vessel === "object" && typeof vessel.id === "string") return vessel.id;
  return String(vessel);
}

/** Converts a Vec3 ({x, y, z} or [x, y, z]) to an array of three numbers. */
function vec3Values(v, what) {
  if (Array.isArray(v) && v.length >= 3) return [Number(v[0]), Number(v[1]), Number(v[2])];
  if (v !== null && typeof v === "object") return [Number(v.x), Number(v.y), Number(v.z)];
  throw new TypeError(`${what} must be a Vec3 {x, y, z}`);
}

/** Converts a Quat ({x, y, z, w} or [x, y, z, w]) to an array of four numbers. */
function quatValues(q, what) {
  if (Array.isArray(q) && q.length >= 4) return [Number(q[0]), Number(q[1]), Number(q[2]), Number(q[3])];
  if (q !== null && typeof q === "object") return [Number(q.x), Number(q.y), Number(q.z), Number(q.w)];
  throw new TypeError(`${what} must be a Quat {x, y, z, w}`);
}

/** Returns `value` when it is an array, otherwise an empty array (a vanished vessel reads null). */
function asList(value) {
  return Array.isArray(value) ? value : [];
}

/**
 * One host action. Frame actions return the CommandResult object; Solver actions return a Promise
 * (the host decides, from the action key).
 */
function act(action, vesselId, ordinal, value, values, token, aux) {
  return host.act(action, vesselId, ordinal, value, values, token, aux);
}

// ---------------------------------------------------------------------------------------------
// Subscriptions and waits
// ---------------------------------------------------------------------------------------------

/** A hook registration: `id` plus `unsubscribe()`. */
class Subscription {
  constructor(id) {
    this.id = id;
  }

  /** Removes the hook. Safe to call more than once. */
  unsubscribe() {
    host.unsubscribe(this.id);
  }
}

/** Registers a callback with the host and wraps the returned id. */
function subscribe(kind, key, fn, opts) {
  if (typeof fn !== "function") {
    throw new TypeError(`ksa: the handler for ${kind} "${key}" must be a function`);
  }
  return new Subscription(host.subscribe(kind, key, fn, opts));
}

/** Creates a host wait; the host returns a Promise that settles when the scheduler resolves it. */
function wait(kind, arg) {
  return host.wait(kind, arg);
}

// ---------------------------------------------------------------------------------------------
// Console
// ---------------------------------------------------------------------------------------------

/** JSON.stringify that survives BigInt, cycles, functions, Errors, Maps / Sets and byte arrays. */
function safeStringify(value) {
  const ancestors = [];
  try {
    const text = JSON.stringify(value, function (_key, member) {
      if (typeof member === "bigint") return `${member}n`;
      if (typeof member === "function") return `[Function${member.name ? ` ${member.name}` : ""}]`;
      if (typeof member === "symbol") return member.toString();
      if (member instanceof Error) return member.stack || String(member);
      if (member instanceof Uint8Array) return `Uint8Array(${member.length})`;
      if (member instanceof Map) return Object.fromEntries(member);
      if (member instanceof Set) return Array.from(member);
      if (member !== null && typeof member === "object") {
        // `this` is the object that holds `member`; unwind to it so only true ancestors remain.
        while (ancestors.length > 0 && ancestors[ancestors.length - 1] !== this) ancestors.pop();
        if (ancestors.indexOf(member) !== -1) return "[Circular]";
        ancestors.push(member);
      }
      return member;
    });
    return text === undefined ? String(value) : text;
  } catch (error) {
    try {
      return String(value);
    } catch (_ignored) {
      return "[unprintable]";
    }
  }
}

/** Formats one console argument: strings verbatim, everything else as safe JSON. */
function formatArg(value) {
  if (typeof value === "string") return value;
  if (value instanceof Error) return value.stack || String(value);
  if (value === undefined) return "undefined";
  if (typeof value === "function") return `[Function${value.name ? ` ${value.name}` : ""}]`;
  if (typeof value === "bigint") return `${value}n`;
  if (typeof value === "symbol") return value.toString();
  if (value !== null && typeof value === "object") return safeStringify(value);
  return String(value);
}

function joinArgs(args) {
  let text = "";
  for (let i = 0; i < args.length; i++) {
    if (i > 0) text += " ";
    text += formatArg(args[i]);
  }
  return text;
}

/** ksa.console: log / info -> level 1, debug -> 0, warn -> 2, error -> 3; exec runs a game console command. */
const consoleApi = {
  log: (...args) => host.log(1, joinArgs(args)),
  info: (...args) => host.log(1, joinArgs(args)),
  debug: (...args) => host.log(0, joinArgs(args)),
  warn: (...args) => host.log(2, joinArgs(args)),
  error: (...args) => host.log(3, joinArgs(args)),
  exec: (command) => host.consoleExec(String(command)),
};

// ---------------------------------------------------------------------------------------------
// Persisted state: ksa.state.get / set, and ksa.state.counter = 1 through a Proxy
// ---------------------------------------------------------------------------------------------

const stateFunctions = {
  /** Reads a persisted value (null when unset). */
  get: (key) => host.stateGet(String(key)),
  /** Writes a persisted value (null / undefined clears it). */
  set: (key, value) => host.stateSet(String(key), value === undefined ? null : value),
};

const stateApi = new Proxy(stateFunctions, {
  get(target, prop) {
    if (prop === "get" || prop === "set") return target[prop];
    if (typeof prop === "symbol") return undefined;
    return host.stateGet(prop);
  },
  set(_target, prop, value) {
    if (prop === "get" || prop === "set") {
      throw new TypeError("ksa.state: 'get' and 'set' are reserved; use ksa.state.set(key, value) for such keys");
    }
    if (typeof prop === "symbol") return false;
    host.stateSet(prop, value === undefined ? null : value);
    return true;
  },
  has(target, prop) {
    if (prop === "get" || prop === "set") return true;
    if (typeof prop === "symbol") return false;
    const value = host.stateGet(prop);
    return value !== null && value !== undefined;
  },
  deleteProperty(_target, prop) {
    if (typeof prop === "symbol" || prop === "get" || prop === "set") return false;
    host.stateSet(prop, null);
    return true;
  },
});

// ---------------------------------------------------------------------------------------------
// Module refs: EngineRef, RcsRef, SolarRef, LightRef, DockingRef, DecouplerRef, AnimationRef
// ---------------------------------------------------------------------------------------------

/**
 * A snapshot of one part module (the record's fields copied on) plus the methods that act on it.
 * The vessel id and module ordinal are kept in non-enumerable fields.
 */
class ModuleRef {
  constructor(vesselId, ordinal, data) {
    Object.assign(this, data);
    hide(this, "_vessel", vesselId);
    hide(this, "_ordinal", ordinal);
  }
}

/** Resolves a module's ordinal: the record's own index when present, otherwise its list position. */
function ordinalOf(data, position) {
  return data !== null && typeof data === "object" && Number.isInteger(data.index) ? data.index : position;
}

/** Builds the ref list of a vessel's module collection (engines, rcs, ...). */
function moduleRefs(Ctor, vesselId, records) {
  return asList(records).map((data, i) => new Ctor(vesselId, ordinalOf(data, i), data));
}

class EngineRef extends ModuleRef {
  /** Switches this engine on or off (engine.active). */
  setActive(on) {
    return act("engine.active", this._vessel, this._ordinal, on ? 1 : 0, null, null, null);
  }

  /** Sets this engine's minimum throttle, 0..1 (engine.min_throttle). */
  setMinThrottle(f) {
    return act("engine.min_throttle", this._vessel, this._ordinal, Number(f), null, null, null);
  }
}

class RcsRef extends ModuleRef {
  /** Switches this RCS thruster controller on or off (rcs.active). */
  setActive(on) {
    return act("rcs.active", this._vessel, this._ordinal, on ? 1 : 0, null, null, null);
  }
}

/** Sets the deploy-animation goal of the animation a module is linked to (animation.goal). */
function linkedAnimationGoal(ref, f, what) {
  const animation = ref.animationIndex;
  if (!Number.isInteger(animation) || animation < 0) {
    return { ok: false, outcome: "Invalid", message: `${what} has no animation` };
  }
  return act("animation.goal", ref._vessel, animation, Number(f), null, null, null);
}

class SolarRef extends ModuleRef {
  /** Sets the deploy-animation goal of this solar panel, 0..1 (animation.goal on its animationIndex). */
  setGoal(f) {
    return linkedAnimationGoal(this, f, "solar panel");
  }
}

class LightRef extends ModuleRef {
  /** Switches this light on or off (light.on). */
  setOn(b) {
    return act("light.on", this._vessel, this._ordinal, b ? 1 : 0, null, null, null);
  }

  /** Sets the light intensity (light.brightness). */
  setBrightness(v) {
    return act("light.brightness", this._vessel, this._ordinal, Number(v), null, null, null);
  }

  /** Sets the light colour, each component 0..1 (light.color). */
  setColor(r, g, b) {
    return act("light.color", this._vessel, this._ordinal, 0, [Number(r), Number(g), Number(b)], null, null);
  }

  /** Sets the spotlight outer cone half-angle in degrees (light.outer_angle). */
  setOuterAngle(deg) {
    return act("light.outer_angle", this._vessel, this._ordinal, Number(deg), null, null, null);
  }

  /** Sets the spotlight inner cone half-angle in degrees (light.inner_angle). */
  setInnerAngle(deg) {
    return act("light.inner_angle", this._vessel, this._ordinal, Number(deg), null, null, null);
  }

  /** Sets the actuate-animation goal of this light, 0..1 (animation.goal on its animationIndex). */
  setGoal(f) {
    return linkedAnimationGoal(this, f, "light");
  }
}

class DockingRef extends ModuleRef {
  /** Undocks this docking port (docking.undock). */
  undock() {
    return act("docking.undock", this._vessel, this._ordinal, 1, null, null, null);
  }
}

class DecouplerRef extends ModuleRef {
  /** Fires this decoupler (decoupler.fire). */
  fire() {
    return act("decoupler.fire", this._vessel, this._ordinal, 1, null, null, null);
  }
}

class AnimationRef extends ModuleRef {
  /** Sets this animation's goal fraction, 0..1 (animation.goal). */
  setGoal(f) {
    return act("animation.goal", this._vessel, this._ordinal, Number(f), null, null, null);
  }
}

// ---------------------------------------------------------------------------------------------
// VesselRef
// ---------------------------------------------------------------------------------------------

/** vessel.debug: cheat actions (teleport, impulse, refills, control switch). */
class VesselDebug {
  constructor(vesselId) {
    hide(this, "_vessel", vesselId);
  }

  /** Teleports the vessel to a CCI position and velocity (debug.teleport, Solver). */
  async teleport(pos, vel) {
    const values = vec3Values(pos, "teleport position").concat(vec3Values(vel, "teleport velocity"));
    return await act("debug.teleport", this._vessel, -1, 0, values, null, null);
  }

  /** Applies an impulse (Ns by default, or a delta-v) in the CCI or body frame (debug.impulse, Solver). */
  async impulse(v, frame, unit) {
    return await act("debug.impulse", this._vessel, -1, 0, vec3Values(v, "impulse vector"),
      frame === undefined ? null : frame, unit === undefined ? null : unit);
  }

  /** Refills all propellant (debug.refill_fuel, Solver). */
  async refillFuel() {
    return await act("debug.refill_fuel", this._vessel, -1, 0, null, null, null);
  }

  /** Recharges the batteries (debug.refill_battery, Solver). */
  async refillBattery() {
    return await act("debug.refill_battery", this._vessel, -1, 0, null, null, null);
  }

  /** Switches player control to this vessel (debug.control_vessel). */
  control() {
    return act("debug.control_vessel", this._vessel, -1, 0, null, this._vessel, null);
  }
}

/**
 * A live view of one vessel. Holds only the id; every property read goes to the host, so values
 * are always current. A vanished vessel reads null / empty lists.
 */
class VesselRef {
  constructor(id) {
    hide(this, "_id", id);
    hide(this, "_handle", `vessel:${id}`);
    hide(this, "_debug", new VesselDebug(id));
  }

  /** The vessel id (constant for the life of the ref). */
  get id() {
    return this._id;
  }

  /** Cheat actions. */
  get debug() {
    return this._debug;
  }

  /** The module lists build refs that can act on their parts. */
  get engines() {
    return moduleRefs(EngineRef, this._id, host.read(this._handle, "engines"));
  }

  get rcs() {
    return moduleRefs(RcsRef, this._id, host.read(this._handle, "rcs"));
  }

  get solar() {
    return moduleRefs(SolarRef, this._id, host.read(this._handle, "solar"));
  }

  get lights() {
    return moduleRefs(LightRef, this._id, host.read(this._handle, "lights"));
  }

  get docking() {
    return moduleRefs(DockingRef, this._id, host.read(this._handle, "docking"));
  }

  get decouplers() {
    return moduleRefs(DecouplerRef, this._id, host.read(this._handle, "decouplers"));
  }

  get animations() {
    return moduleRefs(AnimationRef, this._id, host.read(this._handle, "animations"));
  }

  /** The raw immutable snapshot record of this vessel (what /sim telemetry would show). */
  snapshot() {
    return host.snapshot(this._handle);
  }

  // ---- Frame actions ----------------------------------------------------------------------

  /** Ignites the engines (vessel.ignite). */
  ignite() {
    return act("vessel.ignite", this._id, -1, 0, null, null, null);
  }

  /** Shuts the engines down (vessel.shutdown). */
  shutdown() {
    return act("vessel.shutdown", this._id, -1, 0, null, null, null);
  }

  /** Master engine switch (vessel.engine). */
  setEngine(on) {
    return act("vessel.engine", this._id, -1, on ? 1 : 0, null, null, null);
  }

  /** Activates the next stage (vessel.stage). */
  stage() {
    return act("vessel.stage", this._id, -1, 0, null, null, null);
  }

  /** Sets the throttle command, 0..1 (vessel.throttle). */
  setThrottle(f) {
    return act("vessel.throttle", this._id, -1, Number(f), null, null, null);
  }

  /** Master lights switch (vessel.lights). */
  setLights(on) {
    return act("vessel.lights", this._id, -1, on ? 1 : 0, null, null, null);
  }

  /** Master RCS switch (vessel.rcs). */
  setRcs(on) {
    return act("vessel.rcs", this._id, -1, on ? 1 : 0, null, null, null);
  }

  /** Latches the manual translation input, each axis -1..1 (vessel.translate). */
  translate(x, y, z) {
    return act("vessel.translate", this._id, -1, 0, [Number(x), Number(y), Number(z)], null, null);
  }

  /** Latches the manual rotation input, each axis -1..1 (vessel.rotate). */
  rotate(x, y, z) {
    return act("vessel.rotate", this._id, -1, 0, [Number(x), Number(y), Number(z)], null, null);
  }

  /** Sets the visual scale factor (vessel.scale). */
  setScale(f) {
    return act("vessel.scale", this._id, -1, Number(f), null, null, null);
  }

  /** Keeps the vessel rendered at any distance (vessel.always_render). */
  setAlwaysRender(on) {
    return act("vessel.always_render", this._id, -1, on ? 1 : 0, null, null, null);
  }

  /** Focuses the camera on this vessel (camera.focus). */
  focus() {
    return act("camera.focus", this._id, -1, 0, null, this._id, null);
  }

  // ---- Solver actions (flight-computer setpoints): Promise<CommandResult> ---------------------

  /** Sets the autopilot attitude mode, e.g. "Prograde" (vessel.attitude_mode, Solver). */
  async setAttitudeMode(m) {
    return await act("vessel.attitude_mode", this._id, -1, 0, null, String(m), null);
  }

  /** Sets the autopilot reference frame (vessel.attitude_frame, Solver). */
  async setAttitudeFrame(f) {
    return await act("vessel.attitude_frame", this._id, -1, 0, null, String(f), null);
  }

  /** Sets the autopilot target attitude quaternion (vessel.attitude_target, Solver). */
  async setAttitudeTarget(q) {
    return await act("vessel.attitude_target", this._id, -1, 0, quatValues(q, "attitude target"), null, null);
  }

  /** Enables or disables RCS in the flight computer: "Enabled" | "Disabled" (vessel.rcs_mode, Solver). */
  async setRcsMode(m) {
    return await act("vessel.rcs_mode", this._id, -1, 0, null, String(m), null);
  }

  /** Plans an automatic burn of delta-v `dv` (CCI, m/s) at universal time `ut` (vessel.burn, Solver). */
  async burn(ut, dv) {
    return await act("vessel.burn", this._id, -1, 0, [Number(ut)].concat(vec3Values(dv, "burn delta-v")), null, null);
  }

  /** JSON.stringify(ref) yields just the id. */
  toJSON() {
    return { id: this._id };
  }
}

// Plain-value getters: one per ScriptViews.Vessel key, each a fresh host read of that key.
const VESSEL_VALUE_KEYS = [
  "name", "situation", "parent", "controlled", "controllable", "isKitten",
  "position", "velocity", "attitude", "altitude", "mass",
  "orbit", "navball", "environment", "power", "battery",
  "tanks", "generators", "srb", "encounters", "parts",
  "throttleCmd", "attitudeMode", "attitudeFrame", "rcsMode", "rcsOn", "engineOn",
  "scale", "alwaysRender", "translateCmd", "rotateCmd",
];
for (const key of VESSEL_VALUE_KEYS) {
  Object.defineProperty(VesselRef.prototype, key, {
    get() {
      return host.read(this._handle, key);
    },
    enumerable: true,
    configurable: true,
  });
}

// ---------------------------------------------------------------------------------------------
// Vessel ref cache
// ---------------------------------------------------------------------------------------------

const vesselCache = new Map();

/** The cached VesselRef for an id (no existence check). */
function vesselRef(id) {
  let ref = vesselCache.get(id);
  if (ref === undefined) {
    ref = new VesselRef(id);
    vesselCache.set(id, ref);
  }
  return ref;
}

// ---------------------------------------------------------------------------------------------
// ksa.time
// ---------------------------------------------------------------------------------------------

/** The game clock; every property is a fresh read of the "time" handle. */
class Time {
  /** Sets the time-warp factor (debug.warp). */
  setWarp(f) {
    return act("debug.warp", null, -1, Number(f), null, null, null);
  }
}
for (const key of ["ut", "warp", "simDt", "paused", "warpSpeeds", "autoWarp", "autoWarpTargetUt", "frame", "stepIndex"]) {
  Object.defineProperty(Time.prototype, key, {
    get() {
      return host.read("time", key);
    },
    enumerable: true,
    configurable: true,
  });
}
const timeApi = new Time();

// ---------------------------------------------------------------------------------------------
// Terminal (ctx.term) and Signal (ctx.signal)
// ---------------------------------------------------------------------------------------------

/**
 * The script's virtual terminal. Output is buffered by the host and flushed once per Step slot, so
 * flush() is a no-op kept for API symmetry.
 */
function makeTerminal(termId) {
  return {
    /** Appends output (a string, or bytes as a Uint8Array). */
    write(data) {
      host.termWrite(termId, data);
    },
    /** Resolves with the next chunk of input bytes (keys, mouse reports, replies). */
    read() {
      return wait("termRead", { termId });
    },
    /** Push-style input: evt is data | resize | attach | detach | cancel; fn receives the event payload. */
    on(evt, fn) {
      if (typeof fn !== "function") throw new TypeError("ksa: term.on needs a function");
      return subscribe("event", `term.${evt}`, (ctx) => fn(ctx.event.payload), {});
    },
    /** The current grid, the smallest attached surface. */
    get size() {
      const info = host.termInfo(termId);
      return { cols: info.cols, rows: info.rows };
    },
    /** The number of attached surfaces. */
    get attached() {
      return host.termInfo(termId).attached;
    },
    /** Clears the screen and homes the cursor. */
    clear() {
      host.termWrite(termId, "\x1b[2J\x1b[H");
    },
    /** No-op: output is flushed by the host every Step slot. */
    flush() {},
    /** Sets the terminal title. */
    title(s) {
      host.termSetTitle(termId, String(s));
    },
    /** Lifecycle policy: { stopWhenDetached } stops the script when the last surface closes. */
    policy(opts) {
      host.termPolicy(termId, Boolean(opts && opts.stopWhenDetached));
    },
  };
}

/** The cancellation signal of a service / main / terminal script (the script name keys the host handle). */
function makeSignal(scriptName) {
  const handle = `signal:${scriptName}`;
  return {
    /** True once the script is being stopped. */
    get cancelled() {
      return host.read(handle, "cancelled") === true;
    },
    /** Runs fn when the script is cancelled. */
    onCancel(fn) {
      if (typeof fn !== "function") throw new TypeError("ksa: signal.onCancel needs a function");
      subscribe("event", "signal.cancel", () => fn(), {});
    },
  };
}

// ---------------------------------------------------------------------------------------------
// The ksa module
// ---------------------------------------------------------------------------------------------

/** Builds the options map of a ksa.every subscription. */
function tickOptions(opts, extra) {
  return compact(Object.assign({ clock: (opts && opts.clock) || "ut" }, extra));
}

const ksa = {
  /** The game clock. */
  time: timeApi,

  /** The controlled vessel, or undefined. */
  get active() {
    const id = host.read("time", "activeVesselId");
    return id === null || id === undefined ? undefined : vesselRef(String(id));
  },

  /** The loaded planetary system {name, home, sun}. */
  get system() {
    return host.snapshot("system");
  },

  console: consoleApi,
  state: stateApi,

  // ---- waits ---------------------------------------------------------------------------------

  /** Resolves after `seconds` on the given clock ("ut" default, "wall", "render"). */
  sleep(seconds, clock) {
    return wait("sleep", { seconds: Number(seconds), clock: clock || "ut" });
  },

  /** Resolves at the next landed simulation step with {ut, dt, stepIndex, frame}. */
  nextStep() {
    return wait("nextStep", null);
  },

  /** Resolves at the next rendered frame. */
  nextFrame() {
    return wait("nextFrame", null);
  },

  /** Resolves true when pred() returns true (checked each Step slot), or false on timeout. */
  until(pred, opts) {
    if (typeof pred !== "function") throw new TypeError("ksa.until needs a predicate function");
    const o = opts || {};
    return wait("until", compact({ fn: pred, timeoutSeconds: o.timeoutSeconds, clock: o.clock }));
  },

  /** Resolves with the next matching event {ut, type, vesselId, payload}, or null on timeout. */
  waitEvent(name, opts) {
    const o = opts || {};
    return wait("waitEvent", compact({
      name: String(name),
      vessel: vesselIdOf(o.vessel),
      timeoutSeconds: o.timeoutSeconds,
    }));
  },

  // ---- vessels and bodies -----------------------------------------------------------------

  /** All vessels. */
  vessels() {
    const ids = asList(host.list("vessels"));
    const live = new Set(ids);
    for (const cached of vesselCache.keys()) {
      if (!live.has(cached)) vesselCache.delete(cached);
    }
    return ids.map((id) => vesselRef(id));
  },

  /** One vessel by id, or undefined when there is none. */
  vessel(id) {
    const key = vesselIdOf(id);
    if (key === undefined) return undefined;
    const known = host.read(`vessel:${key}`, "id");
    if (known === null || known === undefined) {
      vesselCache.delete(key);
      return undefined;
    }
    return vesselRef(key);
  },

  /** All celestial bodies. */
  bodies() {
    return asList(host.list("bodies")).map((id) => host.snapshot(`body:${id}`));
  },

  /** One body by id, or undefined. */
  body(id) {
    const snapshot = host.snapshot(`body:${id}`);
    return snapshot === null || snapshot === undefined ? undefined : snapshot;
  },

  // ---- hooks ---------------------------------------------------------------------------------

  /** Subscribes to an event. opts: { sync, vessel }. */
  on(event, handler, opts) {
    const o = opts || {};
    return subscribe("event", String(event), handler, compact({
      sync: o.sync ? true : false,
      vessel: vesselIdOf(o.vessel),
    }));
  },

  /** Runs handler every frame / landed step / n seconds. opts: { clock }. */
  every(kind, handler, opts) {
    if (kind === "frame" || kind === "step") {
      return subscribe("tick", kind, handler, tickOptions(opts, {}));
    }
    const interval = Number(kind);
    if (!(interval > 0)) {
      throw new TypeError('ksa.every: kind must be "frame", "step" or a positive number of seconds');
    }
    return subscribe("tick", "every", handler, tickOptions(opts, { intervalSeconds: interval }));
  },

  /** Registers a long-running service: fn(ctx) starts at load and ends when ctx.signal.cancelled. */
  service(fn) {
    return subscribe("service", "", fn, {});
  },

  /** Registers a console command; fn receives the argument text. */
  command(name, fn, description) {
    return subscribe("command", String(name), fn, compact({ description }));
  },

  // ---- context factory (called by the runtime, not by scripts) ---------------------------------

  /**
   * Builds the context object a hook / main receives from the scheduler's map (keys: $ctx, kind,
   * script, event, vesselId, sync, ut, dt, stepIndex, frame, signal, termId).
   */
  makeContext(map) {
    const ctx = {};
    for (const key of Object.keys(map)) {
      if (key === "$ctx" || key === "termId" || key === "signal") continue;
      ctx[key] = map[key]; // unknown keys pass straight through
    }
    ctx.ksa = ksa;
    ctx.log = consoleApi;

    const event = map.event;
    if (event !== undefined && event !== null) {
      const vesselId = map.vesselId !== undefined && map.vesselId !== null ? map.vesselId : event.vesselId;
      ctx.vesselId = vesselId === undefined ? null : vesselId;
      ctx.vessel = vesselId === undefined || vesselId === null ? undefined : vesselRef(String(vesselId));
      ctx.sync = Boolean(map.sync);
      ctx.ut = event.ut;
      ctx.type = event.type;
      ctx.payload = event.payload === undefined || event.payload === null ? {} : event.payload;
    }

    if (map.signal !== undefined && map.signal !== null) ctx.signal = makeSignal(String(map.signal));
    if (map.termId !== undefined && map.termId !== null) ctx.term = makeTerminal(map.termId);
    return ctx;
  },
};

globalThis.ksa = ksa;

export { ksa };
export default ksa;
