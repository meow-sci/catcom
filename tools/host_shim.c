/* catcom host shim, compiled into the LuaJIT library (appended to ljamalg.c).
 * Managed code must never raise Lua errors (Lua unwinding cannot cross .NET frames);
 * these three native entry points do it from a native frame instead. */
#include "lua.h"
#include "lauxlib.h"

/* Count hook armed by the watchdog thread via lua_sethook(L, host_timeout_hook, LUA_MASKCOUNT, 1).
 * lua_sethook is the only Lua API that may be called from another thread.
 * The hook deliberately stays armed (LuaJIT hooks are global to the state): a script that wraps
 * its runaway loop in pcall would otherwise swallow the single error and keep spinning. With the
 * hook still set, the next Lua instruction outside the pcall raises again, so the error always
 * reaches the host's lua_pcall/lua_resume. The host clears the hook (lua_sethook(L, NULL, 0, 0))
 * as soon as the outermost call returns. */
LUA_API void host_timeout_hook(lua_State *L, lua_Debug *ar)
{
  (void)ar;
  luaL_error(L, "script timed out");
}

/* Raise the value on top of the stack as an error from a native frame. */
LUA_API int host_raise(lua_State *L)
{
  return lua_error(L);
}

/* Message handler for lua_pcall/xpcall: message + traceback. Non-string error objects are
 * converted with tostring semantics where possible so the traceback is never lost. */
LUA_API int host_traceback(lua_State *L)
{
  const char *m = lua_tostring(L, 1);
  if (m == NULL && !lua_isnoneornil(L, 1)) {
    if (luaL_callmeta(L, 1, "__tostring") && lua_type(L, -1) == LUA_TSTRING)
      m = lua_tostring(L, -1);
    else
      m = lua_pushfstring(L, "(error object is a %s value)", luaL_typename(L, 1));
  }
  luaL_traceback(L, L, m, 1);
  return 1;
}
