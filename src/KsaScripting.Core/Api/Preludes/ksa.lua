-- ksa.lua - the script-side half of the ksa-scripting host contract (LuaJIT).
--
-- This chunk runs inside the script's sandbox environment and returns the `ksa` module table (it
-- also publishes it as the global `ksa`). It is dependency-free: base library + string / table /
-- math / coroutine only (no io / os / debug / ffi / require), Lua 5.1 / LuaJIT 2.1 semantics.
--
-- It is built ONLY on the host table `ksa_host` documented in the implementation guide
-- (section 3.7): read, snapshot, list, act, wait, subscribe, unsubscribe, log, console_exec,
-- state_get, state_set, term_write, term_info, term_set_title, term_policy. Every host function
-- returns (true, value) or (false, "message") and never raises; this prelude raises with
-- error(msg, level) when ok is false.
--
-- Conventions
--  * Everything is snake_case: members (set_throttle, refill_fuel) and data keys (vac_thrust_n,
--    step_index, vessel_id). The host speaks camelCase; tables coming back are deep-converted to
--    snake_case and option tables going out are converted to camelCase (interval_seconds ->
--    intervalSeconds). Persisted state (ksa.state) is stored verbatim, keys untouched.
--  * Methods on refs use colon syntax (v:set_throttle(0.5), v.debug:teleport(p, v),
--    e:set_active(true)); module functions use dot syntax (ksa.sleep(2), ksa.vessel(id)).
--    ksa.console, ksa.state, ctx.term, ctx.signal and subscriptions work with either syntax.
--  * Method naming: value-setting methods carry a set_ prefix (set_throttle), verbs stay verbs
--    (ignite, stage, focus). Getters are plain fields (throttle_cmd, engines, ...).
--  * Waiting (ksa.sleep, ksa.next_step, ksa.next_frame, ksa.wait_until, ksa.wait_event,
--    ctx.term.read, and the Solver-phase actions) yields the running coroutine with the host's
--    {["$wait"] = id} table; the runtime resumes it with (result), or (nil, "error message") when
--    the wait fails or is cancelled, and the prelude then raises. Such functions must be called
--    from a script coroutine (main, hooks and services all are). `until` is a reserved word in Lua,
--    so it is ksa.wait_until.
--  * Frame-phase actions return the CommandResult table {ok, outcome, message} directly.
--  * act argument order: (action, vessel_id, ordinal, value, values, token, aux). Unused arguments
--    are nil; ordinal is -1 for vessel-level actions; value is 0 when unused; booleans are 1 / 0.
--
-- The API surface is declared once in C# (ApiSchema); a test keeps this file and the schema in sync.

local host = ksa_host
if host == nil then
  error("ksa: the host table (ksa_host) is not available in this runtime", 2)
end

local type, pairs, ipairs, select = type, pairs, ipairs, select
local tostring, tonumber, error = tostring, tonumber, error
local setmetatable, getmetatable, rawget = setmetatable, getmetatable, rawget
local sformat, sgsub, slower, supper = string.format, string.gsub, string.lower, string.upper
local tconcat, tsort = table.concat, table.sort
local mfloor = math.floor
local cyield, crunning = coroutine.yield, coroutine.running

-- ---------------------------------------------------------------------------------------------
-- Case conversion
-- ---------------------------------------------------------------------------------------------

local snake_cache = {}

--- "vacThrustN" -> "vac_thrust_n": an underscore goes before an uppercase letter that follows a
--- lowercase letter or a digit; the result is lowercased. Names that are already snake_case stay.
local function snake_key(key)
  if type(key) ~= "string" then
    return key
  end
  local converted = snake_cache[key]
  if converted == nil then
    converted = slower((sgsub(key, "([%l%d])(%u)", "%1_%2")))
    snake_cache[key] = converted
  end
  return converted
end

--- Deep-converts every string key of a host table to snake_case (non-tables pass through).
local function snake(value, depth)
  if type(value) ~= "table" then
    return value
  end
  depth = depth or 0
  if depth > 32 then
    return value
  end
  local out = {}
  for k, v in pairs(value) do
    out[snake_key(k)] = snake(v, depth + 1)
  end
  return out
end

--- "interval_seconds" -> "intervalSeconds" (shallow; used for option tables sent to the host).
local function camel(options)
  if type(options) ~= "table" then
    return {}
  end
  local out = {}
  for k, v in pairs(options) do
    if type(k) == "string" then
      k = sgsub(k, "_(%l)", function(c) return supper(c) end)
    end
    out[k] = v
  end
  return out
end

-- ---------------------------------------------------------------------------------------------
-- Host call helpers
-- ---------------------------------------------------------------------------------------------

--- Calls a host function and raises its error message when it reports failure.
local function call(fn, ...)
  local ok, value = fn(...)
  if not ok then
    error(value, 3)
  end
  return value
end

--- Wraps fn so it can be called as obj.f(...) or obj:f(...) (the obj argument is dropped).
local function tolerant(obj, fn)
  return function(...)
    if (...) == obj then
      return fn(select(2, ...))
    end
    return fn(...)
  end
end

--- Raises unless running inside a coroutine (waiting needs one to yield).
local function need_coroutine(what)
  local co, is_main = crunning()
  if co == nil or is_main == true then
    error("ksa: " .. what .. " waits, so it must be called from a script coroutine (main, a hook or a service)", 3)
  end
end

--- Parks the coroutine on a host wait; resumes with the (snake_case) result or raises.
local function await(w)
  local result, err = cyield(w)
  if result == nil and err ~= nil then
    error(err, 3)
  end
  return snake(result)
end

--- Creates a host wait and parks on it.
local function wait(kind, arg)
  need_coroutine(kind)
  return await(call(host.wait, kind, arg))
end

--- One host action. A table carrying "$wait" (a Solver-phase action) is awaited; otherwise the
--- immediate CommandResult table is returned.
local function act(action, vessel_id, ordinal, value, values, token, aux)
  local result = call(host.act, action, vessel_id, ordinal, value, values, token, aux)
  if type(result) == "table" and result["$wait"] ~= nil then
    return await(result)
  end
  return result
end

--- A Solver-phase action: same as act, but fails early (before the host creates a wait) when the
--- caller is not inside a coroutine.
local function act_solver(action, vessel_id, ordinal, value, values, token, aux)
  need_coroutine(action)
  return act(action, vessel_id, ordinal, value, values, token, aux)
end

local function num(value, what)
  local n = tonumber(value)
  if n == nil then
    error(what .. " must be a number", 3)
  end
  return n
end

--- Converts a Vec3 ({x=, y=, z=} or {x, y, z}) to an array of three numbers.
local function vec3_values(v, what)
  if type(v) == "table" then
    if v.x ~= nil then
      return { num(v.x, what), num(v.y, what), num(v.z, what) }
    end
    return { num(v[1], what), num(v[2], what), num(v[3], what) }
  end
  error(what .. " must be a Vec3 {x, y, z}", 3)
end

--- Converts a Quat ({x=, y=, z=, w=} or {x, y, z, w}) to an array of four numbers.
local function quat_values(q, what)
  if type(q) == "table" then
    if q.x ~= nil then
      return { num(q.x, what), num(q.y, what), num(q.z, what), num(q.w, what) }
    end
    return { num(q[1], what), num(q[2], what), num(q[3], what), num(q[4], what) }
  end
  error(what .. " must be a Quat {x, y, z, w}", 3)
end

--- Normalises a vessel argument (a VesselRef, an id string, or nil) to an id string or nil.
local function vessel_id_of(vessel)
  if vessel == nil then
    return nil
  end
  if type(vessel) == "table" then
    return vessel.id
  end
  return tostring(vessel)
end

local function flag(on)
  if on then
    return 1
  end
  return 0
end

-- ---------------------------------------------------------------------------------------------
-- Subscriptions
-- ---------------------------------------------------------------------------------------------

--- Registers a callback with the host and wraps the returned id: { id, unsubscribe() }.
local function subscribe(kind, key, fn, opts)
  if type(fn) ~= "function" then
    error("ksa: the handler for " .. kind .. ' "' .. tostring(key) .. '" must be a function', 3)
  end
  local id = call(host.subscribe, kind, key, fn, opts)
  return {
    id = id,
    unsubscribe = function()
      call(host.unsubscribe, id)
    end,
  }
end

-- ---------------------------------------------------------------------------------------------
-- Console
-- ---------------------------------------------------------------------------------------------

local MAX_DEPTH, MAX_ENTRIES = 4, 64

local function quote(s)
  return (sgsub(sformat("%q", s), "\\\n", "\\n"))
end

local function is_identifier(s)
  return type(s) == "string" and s:find("^[%a_][%w_]*$") ~= nil
end

--- A small table pretty-printer with a depth limit, entry limit and cycle detection.
local function inspect(value, depth, seen)
  local t = type(value)
  if t == "string" then
    return quote(value)
  end
  if t ~= "table" then
    return tostring(value)
  end
  local mt = getmetatable(value)
  if type(mt) == "table" and rawget(mt, "__tostring") ~= nil then
    return tostring(value)
  end
  if seen[value] then
    return "<cycle>"
  end
  if depth >= MAX_DEPTH then
    return "{...}"
  end
  seen[value] = true
  local parts, count = {}, 0
  local length = #value
  for i = 1, length do
    count = count + 1
    if count > MAX_ENTRIES then
      parts[#parts + 1] = "..."
      break
    end
    parts[#parts + 1] = inspect(value[i], depth + 1, seen)
  end
  if count <= MAX_ENTRIES then
    local keys = {}
    for k in pairs(value) do
      if not (type(k) == "number" and k >= 1 and k <= length and k == mfloor(k)) then
        keys[#keys + 1] = k
      end
    end
    tsort(keys, function(a, b)
      return tostring(a) < tostring(b)
    end)
    for _, k in ipairs(keys) do
      count = count + 1
      if count > MAX_ENTRIES then
        parts[#parts + 1] = "..."
        break
      end
      local label
      if is_identifier(k) then
        label = k
      else
        label = "[" .. inspect(k, depth + 1, seen) .. "]"
      end
      parts[#parts + 1] = label .. " = " .. inspect(value[k], depth + 1, seen)
    end
  end
  seen[value] = nil
  return "{" .. tconcat(parts, ", ") .. "}"
end

--- Formats one console argument: strings verbatim, everything else readably.
local function format_arg(value)
  if type(value) == "string" then
    return value
  end
  return inspect(value, 0, {})
end

local function join(...)
  local args = { ... }
  local parts = {}
  for i = 1, select("#", ...) do
    parts[i] = format_arg(args[i])
  end
  return tconcat(parts, " ")
end

local console = {}
-- log / info -> level 1, debug -> 0, warn -> 2, error -> 3; arguments are joined with a space.
console.log = tolerant(console, function(...) host.log(1, join(...)) end)
console.info = tolerant(console, function(...) host.log(1, join(...)) end)
console.debug = tolerant(console, function(...) host.log(0, join(...)) end)
console.warn = tolerant(console, function(...) host.log(2, join(...)) end)
console.error = tolerant(console, function(...) host.log(3, join(...)) end)
--- Executes a game console command.
console.exec = tolerant(console, function(command)
  call(host.console_exec, tostring(command))
end)

-- ---------------------------------------------------------------------------------------------
-- Persisted state: ksa.state.get / set, and ksa.state.counter = 1 through the metatable
-- ---------------------------------------------------------------------------------------------

local state = {}
--- Reads a persisted value (nil when unset). Values are returned exactly as stored.
state.get = tolerant(state, function(key)
  return call(host.state_get, tostring(key))
end)
--- Writes a persisted value (nil clears it).
state.set = tolerant(state, function(key, value)
  call(host.state_set, tostring(key), value)
end)
setmetatable(state, {
  __index = function(_, key)
    return call(host.state_get, tostring(key))
  end,
  __newindex = function(_, key, value)
    call(host.state_set, tostring(key), value)
  end,
})

-- ---------------------------------------------------------------------------------------------
-- Module refs: EngineRef, RcsRef, SolarRef, LightRef, DockingRef, DecouplerRef, AnimationRef
-- ---------------------------------------------------------------------------------------------

--- A module ref is a snapshot of one part module (snake_case fields copied on) plus the methods
--- of its class (found through the metatable) and the vessel id / module ordinal (_vessel, _ordinal).
local function module_refs(methods, vessel_id, records)
  local out = {}
  if type(records) ~= "table" then
    return out
  end
  local mt = { __index = methods }
  for i, data in ipairs(records) do
    local ref = snake(data)
    ref._vessel = vessel_id
    if type(data.index) == "number" then
      ref._ordinal = data.index
    else
      ref._ordinal = i - 1
    end
    out[i] = setmetatable(ref, mt)
  end
  return out
end

--- Sets the deploy-animation goal of the animation a module is linked to (animation.goal).
local function linked_animation_goal(ref, f, what)
  local animation = ref.animation_index
  if type(animation) ~= "number" or animation < 0 then
    return { ok = false, outcome = "Invalid", message = what .. " has no animation" }
  end
  return act("animation.goal", ref._vessel, animation, num(f, "goal"), nil, nil, nil)
end

local EngineMethods = {}
--- Switches this engine on or off (engine.active).
function EngineMethods:set_active(on)
  return act("engine.active", self._vessel, self._ordinal, flag(on), nil, nil, nil)
end
--- Sets this engine's minimum throttle, 0..1 (engine.min_throttle).
function EngineMethods:set_min_throttle(f)
  return act("engine.min_throttle", self._vessel, self._ordinal, num(f, "min throttle"), nil, nil, nil)
end

local RcsMethods = {}
--- Switches this RCS thruster controller on or off (rcs.active).
function RcsMethods:set_active(on)
  return act("rcs.active", self._vessel, self._ordinal, flag(on), nil, nil, nil)
end

local SolarMethods = {}
--- Sets the deploy-animation goal of this solar panel, 0..1 (animation.goal on its animation_index).
function SolarMethods:set_goal(f)
  return linked_animation_goal(self, f, "solar panel")
end

local LightMethods = {}
--- Switches this light on or off (light.on).
function LightMethods:set_on(b)
  return act("light.on", self._vessel, self._ordinal, flag(b), nil, nil, nil)
end
--- Sets the light intensity (light.brightness).
function LightMethods:set_brightness(v)
  return act("light.brightness", self._vessel, self._ordinal, num(v, "brightness"), nil, nil, nil)
end
--- Sets the light colour, each component 0..1 (light.color).
function LightMethods:set_color(r, g, b)
  return act("light.color", self._vessel, self._ordinal, 0, { num(r, "red"), num(g, "green"), num(b, "blue") }, nil, nil)
end
--- Sets the spotlight outer cone half-angle in degrees (light.outer_angle).
function LightMethods:set_outer_angle(deg)
  return act("light.outer_angle", self._vessel, self._ordinal, num(deg, "angle"), nil, nil, nil)
end
--- Sets the spotlight inner cone half-angle in degrees (light.inner_angle).
function LightMethods:set_inner_angle(deg)
  return act("light.inner_angle", self._vessel, self._ordinal, num(deg, "angle"), nil, nil, nil)
end
--- Sets the actuate-animation goal of this light, 0..1 (animation.goal on its animation_index).
function LightMethods:set_goal(f)
  return linked_animation_goal(self, f, "light")
end

local DockingMethods = {}
--- Undocks this docking port (docking.undock).
function DockingMethods:undock()
  return act("docking.undock", self._vessel, self._ordinal, 1, nil, nil, nil)
end

local DecouplerMethods = {}
--- Fires this decoupler (decoupler.fire).
function DecouplerMethods:fire()
  return act("decoupler.fire", self._vessel, self._ordinal, 1, nil, nil, nil)
end

local AnimationMethods = {}
--- Sets this animation's goal fraction, 0..1 (animation.goal).
function AnimationMethods:set_goal(f)
  return act("animation.goal", self._vessel, self._ordinal, num(f, "goal"), nil, nil, nil)
end

-- ---------------------------------------------------------------------------------------------
-- VesselRef
-- ---------------------------------------------------------------------------------------------

--- vessel.debug: cheat actions (teleport, impulse, refills, control switch).
local VesselDebug = {}
local VesselDebugMT = { __index = VesselDebug }

--- Teleports the vessel to a CCI position and velocity (debug.teleport, Solver).
function VesselDebug:teleport(pos, vel)
  local values = vec3_values(pos, "teleport position")
  local v = vec3_values(vel, "teleport velocity")
  values[4], values[5], values[6] = v[1], v[2], v[3]
  return act_solver("debug.teleport", self._vessel, -1, 0, values, nil, nil)
end
--- Applies an impulse (Ns by default, or a delta-v) in the CCI or body frame (debug.impulse, Solver).
function VesselDebug:impulse(v, frame, unit)
  return act_solver("debug.impulse", self._vessel, -1, 0, vec3_values(v, "impulse vector"), frame, unit)
end
--- Refills all propellant (debug.refill_fuel, Solver).
function VesselDebug:refill_fuel()
  return act_solver("debug.refill_fuel", self._vessel, -1, 0, nil, nil, nil)
end
--- Recharges the batteries (debug.refill_battery, Solver).
function VesselDebug:refill_battery()
  return act_solver("debug.refill_battery", self._vessel, -1, 0, nil, nil, nil)
end
--- Switches player control to this vessel (debug.control_vessel).
function VesselDebug:control()
  return act("debug.control_vessel", self._vessel, -1, 0, nil, self._vessel, nil)
end

local VesselMethods = {}

--- The raw immutable snapshot record of this vessel (what /sim telemetry would show).
function VesselMethods:snapshot()
  return snake(call(host.snapshot, self._handle))
end

-- Frame actions ----------------------------------------------------------------------------

--- Ignites the engines (vessel.ignite).
function VesselMethods:ignite()
  return act("vessel.ignite", self._id, -1, 0, nil, nil, nil)
end
--- Shuts the engines down (vessel.shutdown).
function VesselMethods:shutdown()
  return act("vessel.shutdown", self._id, -1, 0, nil, nil, nil)
end
--- Master engine switch (vessel.engine).
function VesselMethods:set_engine(on)
  return act("vessel.engine", self._id, -1, flag(on), nil, nil, nil)
end
--- Activates the next stage (vessel.stage).
function VesselMethods:stage()
  return act("vessel.stage", self._id, -1, 0, nil, nil, nil)
end
--- Sets the throttle command, 0..1 (vessel.throttle).
function VesselMethods:set_throttle(f)
  return act("vessel.throttle", self._id, -1, num(f, "throttle"), nil, nil, nil)
end
--- Master lights switch (vessel.lights).
function VesselMethods:set_lights(on)
  return act("vessel.lights", self._id, -1, flag(on), nil, nil, nil)
end
--- Master RCS switch (vessel.rcs).
function VesselMethods:set_rcs(on)
  return act("vessel.rcs", self._id, -1, flag(on), nil, nil, nil)
end
--- Latches the manual translation input, each axis -1..1 (vessel.translate).
function VesselMethods:translate(x, y, z)
  return act("vessel.translate", self._id, -1, 0, { num(x, "x"), num(y, "y"), num(z, "z") }, nil, nil)
end
--- Latches the manual rotation input, each axis -1..1 (vessel.rotate).
function VesselMethods:rotate(x, y, z)
  return act("vessel.rotate", self._id, -1, 0, { num(x, "x"), num(y, "y"), num(z, "z") }, nil, nil)
end
--- Sets the visual scale factor (vessel.scale).
function VesselMethods:set_scale(f)
  return act("vessel.scale", self._id, -1, num(f, "scale"), nil, nil, nil)
end
--- Keeps the vessel rendered at any distance (vessel.always_render).
function VesselMethods:set_always_render(on)
  return act("vessel.always_render", self._id, -1, flag(on), nil, nil, nil)
end
--- Focuses the camera on this vessel (camera.focus).
function VesselMethods:focus()
  return act("camera.focus", self._id, -1, 0, nil, self._id, nil)
end

-- Solver actions (flight-computer setpoints): yield until the solver slot ran -------------------

--- Sets the autopilot attitude mode, e.g. "Prograde" (vessel.attitude_mode, Solver).
function VesselMethods:set_attitude_mode(m)
  return act_solver("vessel.attitude_mode", self._id, -1, 0, nil, tostring(m), nil)
end
--- Sets the autopilot reference frame (vessel.attitude_frame, Solver).
function VesselMethods:set_attitude_frame(f)
  return act_solver("vessel.attitude_frame", self._id, -1, 0, nil, tostring(f), nil)
end
--- Sets the autopilot target attitude quaternion (vessel.attitude_target, Solver).
function VesselMethods:set_attitude_target(q)
  return act_solver("vessel.attitude_target", self._id, -1, 0, quat_values(q, "attitude target"), nil, nil)
end
--- Enables or disables RCS in the flight computer: "Enabled" | "Disabled" (vessel.rcs_mode, Solver).
function VesselMethods:set_rcs_mode(m)
  return act_solver("vessel.rcs_mode", self._id, -1, 0, nil, tostring(m), nil)
end
--- Plans an automatic burn of delta-v dv (CCI, m/s) at universal time ut (vessel.burn, Solver).
function VesselMethods:burn(ut, dv)
  local v = vec3_values(dv, "burn delta-v")
  return act_solver("vessel.burn", self._id, -1, 0, { num(ut, "burn time"), v[1], v[2], v[3] }, nil, nil)
end

--- A getter that reads one ScriptViews.Vessel key (camelCase on the host side) and snake-cases it.
local function plain(key)
  return function(self)
    return snake(call(host.read, self._handle, key))
  end
end

--- A getter that builds the ref list of a module collection.
local function modules(key, methods)
  return function(self)
    return module_refs(methods, self._id, call(host.read, self._handle, key))
  end
end

-- One getter per ScriptViews.Vessel key (plus debug), keyed by the Lua (snake_case) name.
local vessel_getters = {
  id = function(self) return self._id end,
  debug = function(self) return self._debug end,
  name = plain("name"),
  situation = plain("situation"),
  parent = plain("parent"),
  controlled = plain("controlled"),
  controllable = plain("controllable"),
  is_kitten = plain("isKitten"),
  position = plain("position"),
  velocity = plain("velocity"),
  attitude = plain("attitude"),
  altitude = plain("altitude"),
  mass = plain("mass"),
  orbit = plain("orbit"),
  navball = plain("navball"),
  environment = plain("environment"),
  power = plain("power"),
  battery = plain("battery"),
  engines = modules("engines", EngineMethods),
  tanks = plain("tanks"),
  rcs = modules("rcs", RcsMethods),
  solar = modules("solar", SolarMethods),
  generators = plain("generators"),
  lights = modules("lights", LightMethods),
  docking = modules("docking", DockingMethods),
  decouplers = modules("decouplers", DecouplerMethods),
  animations = modules("animations", AnimationMethods),
  srb = plain("srb"),
  encounters = plain("encounters"),
  parts = plain("parts"),
  throttle_cmd = plain("throttleCmd"),
  attitude_mode = plain("attitudeMode"),
  attitude_frame = plain("attitudeFrame"),
  rcs_mode = plain("rcsMode"),
  rcs_on = plain("rcsOn"),
  engine_on = plain("engineOn"),
  scale = plain("scale"),
  always_render = plain("alwaysRender"),
  translate_cmd = plain("translateCmd"),
  rotate_cmd = plain("rotateCmd"),
}

local VesselMT = {
  __index = function(self, key)
    local getter = vessel_getters[key]
    if getter ~= nil then
      return getter(self)
    end
    return VesselMethods[key]
  end,
  __newindex = function(_, key)
    error("VesselRef." .. tostring(key) .. " is read-only; use a set_ method", 2)
  end,
  __tostring = function(self)
    return "VesselRef(" .. self._id .. ")"
  end,
}

local vessel_cache = {}

--- The cached VesselRef for an id (no existence check). A ref holds only its id / handle.
local function vessel_ref(id)
  local ref = vessel_cache[id]
  if ref == nil then
    ref = setmetatable({
      _id = id,
      _handle = "vessel:" .. id,
      _debug = setmetatable({ _vessel = id }, VesselDebugMT),
    }, VesselMT)
    vessel_cache[id] = ref
  end
  return ref
end

-- ---------------------------------------------------------------------------------------------
-- ksa.time
-- ---------------------------------------------------------------------------------------------

-- Lua (snake_case) name -> TimeView key.
local TIME_KEYS = {
  ut = "ut",
  warp = "warp",
  sim_dt = "simDt",
  paused = "paused",
  warp_speeds = "warpSpeeds",
  auto_warp = "autoWarp",
  auto_warp_target_ut = "autoWarpTargetUt",
  frame = "frame",
  step_index = "stepIndex",
}

local time = {}
--- Sets the time-warp factor (debug.warp).
time.set_warp = tolerant(time, function(f)
  return act("debug.warp", nil, -1, num(f, "warp"), nil, nil, nil)
end)
setmetatable(time, {
  __index = function(_, key)
    local view = TIME_KEYS[key]
    if view ~= nil then
      return snake(call(host.read, "time", view))
    end
  end,
  __newindex = function(_, key)
    error("ksa.time." .. tostring(key) .. " is read-only", 2)
  end,
})

-- ---------------------------------------------------------------------------------------------
-- Terminal (ctx.term) and Signal (ctx.signal)
-- ---------------------------------------------------------------------------------------------

--- The script's virtual terminal. Output is buffered by the host and flushed once per Step slot,
--- so flush() is a no-op kept for API symmetry.
local function make_terminal(term_id)
  local term = {}
  --- Appends output (a string of bytes).
  term.write = tolerant(term, function(data)
    if type(data) ~= "string" then
      data = tostring(data)
    end
    call(host.term_write, term_id, data)
  end)
  --- Yields until input arrives; returns the next chunk of input bytes as a string.
  term.read = tolerant(term, function()
    return wait("termRead", { termId = term_id })
  end)
  --- Push-style input: evt is data | resize | attach | detach | cancel; fn receives the event payload.
  term.on = tolerant(term, function(evt, fn)
    if type(fn) ~= "function" then
      error("ksa: term.on needs a function", 3)
    end
    return subscribe("event", "term." .. tostring(evt), function(ctx)
      fn(ctx.event.payload)
    end, {})
  end)
  --- Clears the screen and homes the cursor.
  term.clear = tolerant(term, function()
    call(host.term_write, term_id, "\27[2J\27[H")
  end)
  --- No-op: output is flushed by the host every Step slot.
  term.flush = tolerant(term, function() end)
  --- Sets the terminal title.
  term.title = tolerant(term, function(s)
    call(host.term_set_title, term_id, tostring(s))
  end)
  --- Lifecycle policy: { stop_when_detached = true } stops the script when the last surface closes.
  term.policy = tolerant(term, function(opts)
    local o = camel(opts)
    call(host.term_policy, term_id, o.stopWhenDetached == true)
  end)
  setmetatable(term, {
    __index = function(_, key)
      if key == "size" then
        local info = snake(call(host.term_info, term_id))
        return { cols = info.cols, rows = info.rows }
      elseif key == "attached" then
        return snake(call(host.term_info, term_id)).attached
      end
    end,
  })
  return term
end

--- The cancellation signal of a service / main / terminal script (the script name keys the host handle).
local function make_signal(script_name)
  local handle = "signal:" .. script_name
  local signal = {}
  --- Runs fn when the script is cancelled.
  signal.on_cancel = tolerant(signal, function(fn)
    if type(fn) ~= "function" then
      error("ksa: signal.on_cancel needs a function", 3)
    end
    subscribe("event", "signal.cancel", function() fn() end, {})
  end)
  setmetatable(signal, {
    __index = function(_, key)
      if key == "cancelled" then
        return call(host.read, handle, "cancelled") == true
      end
    end,
  })
  return signal
end

-- ---------------------------------------------------------------------------------------------
-- The ksa module
-- ---------------------------------------------------------------------------------------------

local ksa = {}

--- The game clock.
ksa.time = time
ksa.console = console
ksa.state = state
--- Functions registered with ksa.export; the runtime merges them with the chunk's returned table.
ksa.__exports = {}

-- ---- waits (yield the running coroutine) --------------------------------------------------------

--- Yields for `seconds` on the given clock ("ut" default, "wall", "render").
function ksa.sleep(seconds, clock)
  return wait("sleep", { seconds = num(seconds, "seconds"), clock = clock or "ut" })
end

--- Yields until the next landed simulation step; returns {ut, dt, step_index, frame}.
function ksa.next_step()
  return wait("nextStep", nil)
end

--- Yields until the next rendered frame.
function ksa.next_frame()
  return wait("nextFrame", nil)
end

--- Yields until pred() returns true (checked each Step slot); returns true, or false on timeout.
--- (Called `until` in other languages; `until` is a reserved word in Lua.)
function ksa.wait_until(pred, opts)
  if type(pred) ~= "function" then
    error("ksa.wait_until needs a predicate function", 2)
  end
  local o = camel(opts)
  return wait("until", { fn = pred, timeoutSeconds = o.timeoutSeconds, clock = o.clock })
end

--- Yields until a matching event; returns {ut, type, vessel_id, payload}, or nil on timeout.
function ksa.wait_event(name, opts)
  local o = camel(opts)
  return wait("waitEvent", { name = tostring(name), vessel = vessel_id_of(o.vessel), timeoutSeconds = o.timeoutSeconds })
end

-- ---- vessels and bodies ---------------------------------------------------------------------

--- All vessels.
function ksa.vessels()
  local ids = call(host.list, "vessels")
  local out, live = {}, {}
  if type(ids) == "table" then
    for i, id in ipairs(ids) do
      live[id] = true
      out[i] = vessel_ref(id)
    end
  end
  for cached in pairs(vessel_cache) do
    if not live[cached] then
      vessel_cache[cached] = nil
    end
  end
  return out
end

--- One vessel by id, or nil when there is none.
function ksa.vessel(id)
  local key = vessel_id_of(id)
  if key == nil then
    return nil
  end
  if call(host.read, "vessel:" .. key, "id") == nil then
    vessel_cache[key] = nil
    return nil
  end
  return vessel_ref(key)
end

--- All celestial bodies.
function ksa.bodies()
  local ids = call(host.list, "bodies")
  local out = {}
  if type(ids) == "table" then
    for i, id in ipairs(ids) do
      out[i] = snake(call(host.snapshot, "body:" .. id))
    end
  end
  return out
end

--- One body by id, or nil.
function ksa.body(id)
  return snake(call(host.snapshot, "body:" .. tostring(id)))
end

-- ---- hooks ----------------------------------------------------------------------------------

--- Subscribes to an event. opts: { sync = bool, vessel = id or VesselRef }.
function ksa.on(event, fn, opts)
  local o = camel(opts)
  return subscribe("event", tostring(event), fn, { sync = o.sync and true or false, vessel = vessel_id_of(o.vessel) })
end

--- Runs fn every frame / landed step / n seconds. opts: { clock = "ut" | "wall" | "render" }.
function ksa.every(kind, fn, opts)
  local o = camel(opts)
  local clock = o.clock or "ut"
  if kind == "frame" or kind == "step" then
    return subscribe("tick", kind, fn, { clock = clock })
  end
  local interval = tonumber(kind)
  if interval == nil or interval <= 0 then
    error('ksa.every: kind must be "frame", "step" or a positive number of seconds', 2)
  end
  return subscribe("tick", "every", fn, { intervalSeconds = interval, clock = clock })
end

--- Registers a long-running service: fn(ctx) starts at load and ends when ctx.signal.cancelled.
function ksa.service(fn)
  return subscribe("service", "", fn, {})
end

--- Registers a console command; fn receives the argument text.
function ksa.command(name, fn, description)
  return subscribe("command", tostring(name), fn, { description = description })
end

--- Exports a function so the mod can bind it to events / ticks / terminals (merged with the
--- table the chunk returns; the returned table wins on a name clash).
function ksa.export(name, fn)
  if type(fn) ~= "function" then
    error("ksa.export: " .. tostring(name) .. " must be a function", 2)
  end
  ksa.__exports[tostring(name)] = fn
end

-- ---- context factory (called by the runtime, not by scripts) --------------------------------------

--- Builds the context table a hook / main receives from the scheduler's map (keys: $ctx, kind,
--- script, event, vesselId, sync, ut, dt, stepIndex, frame, signal, termId). Keys become
--- snake_case; unknown keys are copied through.
function ksa.make_context(tbl)
  local source = snake(tbl)
  local ctx = {}
  for key, value in pairs(source) do
    if key ~= "$ctx" and key ~= "term_id" and key ~= "signal" then
      ctx[key] = value
    end
  end
  ctx.ksa = ksa
  ctx.log = console

  local event = source.event
  if type(event) == "table" then
    local vessel_id = source.vessel_id
    if vessel_id == nil then
      vessel_id = event.vessel_id
    end
    ctx.vessel_id = vessel_id
    if vessel_id ~= nil then
      ctx.vessel = vessel_ref(tostring(vessel_id))
    end
    ctx.sync = source.sync == true
    ctx.ut = event.ut
    ctx.type = event.type
    ctx.payload = event.payload or {}
  end

  if source.signal ~= nil then
    ctx.signal = make_signal(tostring(source.signal))
  end
  if source.term_id ~= nil then
    ctx.term = make_terminal(source.term_id)
  end
  return ctx
end

-- ---- dynamic module fields: ksa.active, ksa.system -------------------------------------------

setmetatable(ksa, {
  __index = function(_, key)
    if key == "active" then
      local id = call(host.read, "time", "activeVesselId")
      if id ~= nil then
        return vessel_ref(tostring(id))
      end
    elseif key == "system" then
      return snake(call(host.snapshot, "system"))
    end
  end,
})

_G.ksa = ksa

return ksa
