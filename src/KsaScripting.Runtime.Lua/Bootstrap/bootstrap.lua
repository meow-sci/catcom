-- ksa-scripting LuaJIT runtime bootstrap (runtime-private, embedded resource).
--
-- Executed once per lua_State with the REAL globals, before any script runs. It receives
--   ksa_host  the host table (docs/IMPLEMENTATION_GUIDE.md section 3.7), and
--   internal  runtime-private host functions: read_module(name), interrupted().
-- and returns the table R of private helpers the C# side keeps (as registry references):
--   R.new_env(script_name, chunk_path)   -> a fresh sandbox environment table
--   R.run_prelude(env, prelude_source)   -> installs env.ksa from the prelude chunk
--   R.release_env(env)                   -> forgets the environment (module unloaded)
--   R.invoke(fn, ...)                    -> the trampoline every invocation runs inside (in a coroutine)
--   R.collect_exports(env, returned)     -> name -> function
--   R.jit_status(), R.jit_version()
--
-- Raising errors from Lua frames is safe; everything sandbox-related is implemented here rather than in C#.

local ksa_host, internal = ...

-- ---------------------------------------------------------------------------------------------
-- Capture everything we need from the real globals before anything is removed.
-- ---------------------------------------------------------------------------------------------
local real_debug        = debug
local real_traceback    = real_debug.traceback
local real_load         = load
local real_getfenv      = getfenv
local real_getmetatable = getmetatable
local real_setmetatable = setmetatable
local real_pcall        = pcall
local real_xpcall       = xpcall
local real_coroutine    = coroutine
local real_jit          = jit
local real_collectgarbage = collectgarbage
local real_error        = error
local real_tostring     = tostring
local real_type         = type
local real_select       = select
local real_unpack       = unpack
local real_pairs        = pairs
local real_rawget       = rawget
local real_concat       = table.concat
local real_os           = os
local real_string       = string

-- The string metatable is shared by every script: lock it so scripts cannot repoint string methods,
-- and drop string.dump from the real string table (reachable as ("x").dump through the metatable).
real_string.dump = nil
real_getmetatable("").__metatable = false

-- string.rep is the cheapest way to allocate gigabytes in one uninterruptible C call (the count hook only fires
-- between Lua instructions). Cap the result; the real table is patched so ("x"):rep(n) is covered as well.
local real_rep = real_string.rep
local MAX_REP_BYTES = 64 * 1024 * 1024
real_string.rep = function(s, n, sep)
  if real_type(n) == "number" and n > 0 then
    local sl = (real_type(s) == "string" or real_type(s) == "number") and #real_tostring(s) or 0
    local pl = real_type(sep) == "string" and #sep or 0
    if (sl + pl) * n > MAX_REP_BYTES then
      real_error("string.rep: result too large", 2)
    end
  end
  return real_rep(s, n, sep)
end

-- ---------------------------------------------------------------------------------------------
-- Interrupt-aware pcall / xpcall / coroutine.resume.
-- The timeout hook raises an error inside whatever script code is running; a script-level pcall
-- would otherwise swallow it. When the host has requested an interrupt, any failure caught by one of
-- these wrappers is re-raised so the interrupt keeps unwinding to the runtime's own xpcall.
-- ---------------------------------------------------------------------------------------------
local host_interrupted = internal.interrupted

local function was_interrupted()
  local _, flag = host_interrupted()
  return flag
end

local function reraise_if_interrupted(ok, ...)
  if not ok and was_interrupted() then
    real_error((...), 0)
  end
  return ok, ...
end

local function guarded_pcall(f, ...)
  return reraise_if_interrupted(real_pcall(f, ...))
end

local function guarded_xpcall(f, h, ...)
  return reraise_if_interrupted(real_xpcall(f, h, ...))
end

local real_resume = real_coroutine.resume
local function guarded_resume(co, ...)
  return reraise_if_interrupted(real_resume(co, ...))
end

-- ---------------------------------------------------------------------------------------------
-- The sandbox base: what every script environment starts from.
-- ---------------------------------------------------------------------------------------------
local function shallow_copy(src)
  local dst = {}
  for k, v in real_pairs(src) do dst[k] = v end
  return dst
end

local function print_to_host(...)
  local n = real_select("#", ...)
  local parts = {}
  for i = 1, n do
    parts[i] = real_tostring((real_select(i, ...)))
  end
  ksa_host.log(1, real_concat(parts, "\t"))
end

local function guarded_collectgarbage(opt, ...)
  if opt == "count" then
    return real_collectgarbage("count")
  end
  real_error("collectgarbage: only 'count' is allowed", 2)
end

local jit_proxy = real_setmetatable({}, {
  __index = {
    status  = function() return (real_jit.status()) end,
    version = real_jit.version,
    os      = real_jit.os,
    arch    = real_jit.arch,
  },
  __newindex = function() real_error("jit is read-only", 2) end,
  __metatable = false,
})

-- Plain values copied into every environment.
local BASE = {
  assert = assert, error = error, ipairs = ipairs, pairs = pairs, next = next,
  pcall = guarded_pcall, xpcall = guarded_xpcall,
  select = select, tonumber = tonumber, tostring = tostring, type = type, unpack = unpack,
  rawequal = rawequal, rawget = rawget, rawset = rawset,
  setmetatable = setmetatable, getmetatable = getmetatable,
  _VERSION = _VERSION,
  collectgarbage = guarded_collectgarbage,
  print = print_to_host,
  jit = jit_proxy,
}

-- Library tables that are shallow-copied per environment, so a script mutating string.format (say)
-- only affects itself.
local LIB_math = math
local LIB_string = real_string
local LIB_table = table
local LIB_bit = bit
local LIB_coroutine = shallow_copy(real_coroutine)
LIB_coroutine.resume = guarded_resume
local LIB_os = {
  clock = real_os.clock, date = real_os.date, time = real_os.time, difftime = real_os.difftime,
}

-- ---------------------------------------------------------------------------------------------
-- Per-environment require.
-- ---------------------------------------------------------------------------------------------
-- env -> its ksa table. Strong on purpose: LuaJIT's weak-key tables are not ephemerons, so a weak table
-- whose values (the prelude's closures) reference the key would pin every environment forever.
-- R.release_env removes the entry when a module is unloaded.
local ksa_by_env = {}

local function make_require(E, loaded)
  local loading = {}
  return function(name)
    if real_type(name) ~= "string" or not name:match("^[%w_%.]+$") then
      real_error("require: invalid module name '" .. real_tostring(name) .. "'", 2)
    end
    if name == "ksa" then
      return ksa_by_env[E] or E.ksa
    end
    local cached = loaded[name]
    if cached ~= nil then
      return cached
    end
    if loading[name] then
      real_error("require: loop detected while loading module '" .. name .. "'", 2)
    end
    local ok, src, chunkname = internal.read_module(name)
    if not ok then
      real_error(src, 2)
    end
    local fn, err = real_load(src, chunkname, "t", E)
    if not fn then
      real_error(err, 0) -- the message already carries "scripts/<module>.lua:<line>:"
    end
    loading[name] = true
    local ran, r = real_pcall(fn, name)
    loading[name] = nil
    if not ran then
      real_error(r, 0)
    end
    if r == nil then r = true end
    loaded[name] = r
    return r
  end
end

local R = {}

function R.new_env(script_name, chunk_path)
  local E = {}
  for k, v in real_pairs(BASE) do E[k] = v end
  E.math = shallow_copy(LIB_math)
  E.string = shallow_copy(LIB_string)
  E.table = shallow_copy(LIB_table)
  E.bit = shallow_copy(LIB_bit)
  E.coroutine = shallow_copy(LIB_coroutine)
  E.os = shallow_copy(LIB_os)
  E.ksa_host = shallow_copy(ksa_host)
  E._G = E
  E.require = make_require(E, {})
  return E
end

function R.run_prelude(E, source)
  local fn, err = real_load(source, "=ksa", "t", E)
  if not fn then
    real_error("prelude: " .. real_tostring(err), 0)
  end
  local result = fn(E.ksa_host)
  local ksa
  if real_type(result) == "table" then
    ksa = result
  elseif real_type(E.ksa) == "table" then
    ksa = E.ksa
  else
    real_error("prelude did not provide a ksa table", 0)
  end
  E.ksa = ksa
  if real_type(ksa.__exports) ~= "table" then
    ksa.__exports = {}
  end
  ksa_by_env[E] = ksa
  return true
end

function R.release_env(E)
  ksa_by_env[E] = nil
end

-- ---------------------------------------------------------------------------------------------
-- Invocation trampoline (runs inside a fresh coroutine; yields from the prelude cross xpcall).
-- ---------------------------------------------------------------------------------------------
local function error_handler(m)
  if real_type(m) ~= "string" then
    local ok, s = real_pcall(real_tostring, m)
    if ok and real_type(s) == "string" then
      m = s
    else
      m = "(error object is a " .. real_type(m) .. " value)"
    end
  end
  return real_traceback(m, 2)
end

local function call_with_context(fn, n, ...)
  local args = { ... }
  local E = real_getfenv(fn)
  local ksa = real_type(E) == "table" and ksa_by_env[E] or nil
  local make_context = ksa and ksa.make_context
  if real_type(make_context) == "function" then
    for i = 1, n do
      local a = args[i]
      if real_type(a) == "table" and real_rawget(a, "$ctx") == true then
        args[i] = make_context(a)
      end
    end
  end
  return fn(real_unpack(args, 1, n))
end

function R.invoke(fn, ...)
  return real_xpcall(call_with_context, error_handler, fn, real_select("#", ...), ...)
end

function R.collect_exports(E, returned)
  local out = {}
  local ksa = ksa_by_env[E]
  local declared = ksa and ksa.__exports
  if real_type(declared) == "table" then
    for k, v in real_pairs(declared) do
      if real_type(k) == "string" and real_type(v) == "function" then out[k] = v end
    end
  end
  if real_type(returned) == "table" then
    for k, v in real_pairs(returned) do
      if real_type(k) == "string" and real_type(v) == "function" then out[k] = v end
    end
  end
  return out
end

function R.jit_status()
  return (real_jit.status())
end

function R.jit_version()
  return real_jit.version
end

return R
