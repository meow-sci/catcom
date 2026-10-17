-- PLACEHOLDER PRELUDE -- NOT THE REAL ksa.lua.
--
-- This minimal prelude is embedded only while src/Catcom.Core/Api/Preludes/ksa.lua does not exist
-- (see Catcom.Runtime.Lua.csproj). It follows the conventions of the real prelude (snake_case,
-- colon methods with set_ prefixes, ksa.vessel(id) returning nil for unknown vessels, ksa.on returning
-- { id, unsubscribe }, waits yielding the host's {["$wait"] = id} table) and implements just enough of
-- docs/IMPLEMENTATION_GUIDE.md sections 3.7 and 5 for the runtime tests to be meaningful.
--
-- Chunk contract: runs in the script environment (ksa_host is a global there) and returns the `ksa` table.

local host = ksa_host

local ksa = { __exports = {} }

-- ksa_host functions return (true, value) or (false, "message"): raise from this Lua frame.
local function call(fn, ...)
  local ok, value = fn(...)
  if not ok then error(value, 3) end
  return value
end

-- Yields the host wait; the runtime resumes with (result) or (nil, "message").
local function await(w)
  local result, err = coroutine.yield(w)
  if result == nil and err ~= nil then error(err, 3) end
  return result
end

local function wait(kind, arg)
  return await(call(host.wait, kind, arg))
end

local function act(action, id, ordinal, value, values, token, aux)
  local result = call(host.act, action, id, ordinal, value, values, token, aux)
  if type(result) == "table" and result["$wait"] ~= nil then return await(result) end
  return result
end

local VesselMethods = {}
function VesselMethods:set_throttle(f) return act("vessel.throttle", self.id, -1, f, nil, nil, nil) end
function VesselMethods:ignite() return act("vessel.ignite", self.id, -1, 0, nil, nil, nil) end
function VesselMethods:stage() return act("vessel.stage", self.id, -1, 0, nil, nil, nil) end
function VesselMethods:set_attitude_mode(m) return act("vessel.attitude_mode", self.id, -1, 0, nil, tostring(m), nil) end

function ksa.vessel(id)
  if call(host.read, "vessel:" .. tostring(id), "id") == nil then return nil end
  return setmetatable({ id = tostring(id) }, { __index = VesselMethods })
end

function ksa.sleep(seconds, clock)
  return wait("sleep", { seconds = seconds, clock = clock or "ut" })
end

function ksa.next_step() return wait("nextStep", nil) end
function ksa.next_frame() return wait("nextFrame", nil) end

function ksa.export(name, fn)
  ksa.__exports[tostring(name)] = fn
end

function ksa.on(event, fn, opts)
  local id = call(host.subscribe, "event", tostring(event), fn, {
    sync = opts and opts.sync and true or false,
    vessel = opts and opts.vessel,
  })
  return { id = id, unsubscribe = function() call(host.unsubscribe, id) end }
end

-- ksa.every("frame" | "step" | seconds, fn, { clock = ... })
function ksa.every(kind, fn, opts)
  local clock = opts and opts.clock or "ut"
  if kind == "frame" or kind == "step" then
    return { id = call(host.subscribe, "tick", kind, fn, { clock = clock }) }
  end
  local interval = tonumber(kind)
  if interval == nil or interval <= 0 then error('ksa.every: kind must be "frame", "step" or a positive number of seconds', 2) end
  return { id = call(host.subscribe, "tick", "every", fn, { intervalSeconds = interval, clock = clock }) }
end

local function log_at(level)
  return function(...)
    local parts = {}
    for i = 1, select("#", ...) do parts[i] = tostring((select(i, ...))) end
    host.log(level, table.concat(parts, " "))
  end
end

ksa.console = {
  log = log_at(1),
  warn = log_at(2),
  error = log_at(3),
  exec = function(cmd) call(host.console_exec, tostring(cmd)) end,
}

ksa.state = {
  get = function(key) return call(host.state_get, tostring(key)) end,
  set = function(key, value) call(host.state_set, tostring(key), value) end,
}

-- camelCase -> snake_case, recursively over tables.
local function snake_key(k)
  if type(k) ~= "string" then return k end
  return (k:gsub("(%u)", function(c) return "_" .. c:lower() end))
end

local function snake_copy(t)
  local out = {}
  for k, v in pairs(t) do
    if type(v) == "table" then v = snake_copy(v) end
    out[snake_key(k)] = v
  end
  return out
end

function ksa.make_context(t)
  local ctx = snake_copy(t)
  ctx["$ctx"] = nil
  ctx.ksa = ksa
  if t.vesselId then ctx.vessel = ksa.vessel(t.vesselId) end
  return ctx
end

_G.ksa = ksa

return ksa
