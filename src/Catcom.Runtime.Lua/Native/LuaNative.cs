using System.Runtime.InteropServices;

namespace Catcom.Runtime.Lua.Native;

/// <summary>
///     Hand-written <c>[LibraryImport]</c> bindings to the Lua 5.1 C API of our own LuaJIT 2.1 build
///     (<c>-DLUAJIT_DISABLE_FFI -DLUAJIT_ENABLE_CHECKHOOK</c>, GC64) plus the three ksa shim exports
///     compiled into the library (<c>tools/host_shim.c</c>). Signatures are copied from
///     <c>lua.h</c> / <c>lauxlib.h</c> / <c>luajit.h</c> of the pinned build.
/// </summary>
/// <remarks>
///     <para>
///         <c>lua_State*</c> is a <see cref="nint"/>. Every function here that can raise a Lua error
///         (<c>lua_call</c>, <c>lua_getfield</c> on a value with metamethods, <c>luaL_check*</c>, <c>lua_error</c>
///         …) must only ever be reached through <c>lua_pcall</c>/<c>lua_resume</c> from managed code: Lua errors
///         cannot unwind through a managed frame. The wrappers that are safe to call from
///         <c>[UnmanagedCallersOnly]</c> host functions are listed at the top of <c>HostFunctions.cs</c>.
///     </para>
///     <para>
///         The DllImport name <c>"lua51"</c> is resolved by <see cref="LuaNativeLoader"/> (module initializer).
///     </para>
/// </remarks>
internal static unsafe partial class LuaNative
{
    private const string Lib = "lua51";

    // ------------------------------------------------------------------ constants

    /// <summary>Pseudo-index of the registry.</summary>
    public const int LUA_REGISTRYINDEX = -10000;

    /// <summary>Pseudo-index of the running function's environment.</summary>
    public const int LUA_ENVIRONINDEX = -10001;

    /// <summary>Pseudo-index of the globals table.</summary>
    public const int LUA_GLOBALSINDEX = -10002;

    /// <summary>"Return all results" for <c>lua_pcall</c>.</summary>
    public const int LUA_MULTRET = -1;

    /// <summary>Success.</summary>
    public const int LUA_OK = 0;

    /// <summary>The coroutine yielded.</summary>
    public const int LUA_YIELD = 1;

    /// <summary>Runtime error.</summary>
    public const int LUA_ERRRUN = 2;

    /// <summary>Syntax error.</summary>
    public const int LUA_ERRSYNTAX = 3;

    /// <summary>Memory allocation error.</summary>
    public const int LUA_ERRMEM = 4;

    /// <summary>Error while running the error handler.</summary>
    public const int LUA_ERRERR = 5;

    /// <summary>Type tag: no value (invalid index).</summary>
    public const int LUA_TNONE = -1;

    /// <summary>Type tag: nil.</summary>
    public const int LUA_TNIL = 0;

    /// <summary>Type tag: boolean.</summary>
    public const int LUA_TBOOLEAN = 1;

    /// <summary>Type tag: light userdata.</summary>
    public const int LUA_TLIGHTUSERDATA = 2;

    /// <summary>Type tag: number.</summary>
    public const int LUA_TNUMBER = 3;

    /// <summary>Type tag: string.</summary>
    public const int LUA_TSTRING = 4;

    /// <summary>Type tag: table.</summary>
    public const int LUA_TTABLE = 5;

    /// <summary>Type tag: function.</summary>
    public const int LUA_TFUNCTION = 6;

    /// <summary>Type tag: full userdata.</summary>
    public const int LUA_TUSERDATA = 7;

    /// <summary>Type tag: thread.</summary>
    public const int LUA_TTHREAD = 8;

    /// <summary>Hook mask: call every N instructions.</summary>
    public const int LUA_MASKCOUNT = 8;

    /// <summary><c>lua_gc</c> option: full collection.</summary>
    public const int LUA_GCCOLLECT = 2;

    /// <summary><c>lua_gc</c> option: heap size in KiB.</summary>
    public const int LUA_GCCOUNT = 3;

    /// <summary><c>lua_gc</c> option: remainder of the heap size in bytes.</summary>
    public const int LUA_GCCOUNTB = 4;

    /// <summary><c>luaJIT_setmode</c> index: the whole engine.</summary>
    public const int LUAJIT_MODE_ENGINE = 0;

    /// <summary><c>luaJIT_setmode</c> flag: turn the feature off.</summary>
    public const int LUAJIT_MODE_OFF = 0x0000;

    /// <summary><c>luaJIT_setmode</c> flag: turn the feature on.</summary>
    public const int LUAJIT_MODE_ON = 0x0100;

    /// <summary><c>luaJIT_setmode</c> flag: flush compiled code.</summary>
    public const int LUAJIT_MODE_FLUSH = 0x0200;

    /// <summary><c>luaL_ref</c> result for a nil value.</summary>
    public const int LUA_REFNIL = -1;

    /// <summary><c>luaL_ref</c> "no reference" marker.</summary>
    public const int LUA_NOREF = -2;

    /// <summary>The pseudo-index of upvalue <paramref name="i"/> (1-based) of a C closure.</summary>
    /// <param name="i">The upvalue number.</param>
    /// <returns>The pseudo-index.</returns>
    public static int lua_upvalueindex(int i) => LUA_GLOBALSINDEX - i;

    // ------------------------------------------------------------------ state

    [LibraryImport(Lib)]
    public static partial nint luaL_newstate();

    [LibraryImport(Lib)]
    public static partial void luaL_openlibs(nint L);

    [LibraryImport(Lib)]
    public static partial void lua_close(nint L);

    [LibraryImport(Lib)]
    public static partial nint lua_newthread(nint L);

    [LibraryImport(Lib)]
    public static partial int luaJIT_setmode(nint L, int idx, int mode);

    // ------------------------------------------------------------------ load / call / coroutines

    [LibraryImport(Lib)]
    public static partial int luaL_loadbufferx(nint L, byte* buff, nuint sz, byte* name, byte* mode);

    [LibraryImport(Lib, EntryPoint = "luaL_loadbufferx", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int luaL_loadbufferx(nint L, byte* buff, nuint sz, string name, string mode);

    [LibraryImport(Lib)]
    public static partial int lua_pcall(nint L, int nargs, int nresults, int errfunc);

    /// <summary>Lua 5.1 signature: no <c>from</c> parameter.</summary>
    [LibraryImport(Lib)]
    public static partial int lua_resume(nint L, int narg);

    [LibraryImport(Lib)]
    public static partial int lua_yield(nint L, int nresults);

    [LibraryImport(Lib)]
    public static partial int lua_status(nint L);

    [LibraryImport(Lib)]
    public static partial void luaL_traceback(nint L, nint L1, byte* msg, int level);

    // ------------------------------------------------------------------ stack

    [LibraryImport(Lib)]
    public static partial int lua_gettop(nint L);

    [LibraryImport(Lib)]
    public static partial void lua_settop(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial void lua_pushvalue(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial void lua_remove(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial void lua_insert(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial void lua_replace(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial int lua_checkstack(nint L, int sz);

    [LibraryImport(Lib)]
    public static partial void lua_xmove(nint from, nint to, int n);

    // ------------------------------------------------------------------ access

    [LibraryImport(Lib)]
    public static partial int lua_type(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial byte* lua_typename(nint L, int tp);

    [LibraryImport(Lib)]
    public static partial byte* lua_tolstring(nint L, int idx, nuint* len);

    [LibraryImport(Lib)]
    public static partial double lua_tonumber(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial nint lua_tointeger(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial int lua_toboolean(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial void* lua_touserdata(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial nint lua_tothread(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial nint lua_topointer(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial nuint lua_objlen(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial int lua_rawequal(nint L, int idx1, int idx2);

    // ------------------------------------------------------------------ push

    [LibraryImport(Lib)]
    public static partial void lua_pushnil(nint L);

    [LibraryImport(Lib)]
    public static partial void lua_pushnumber(nint L, double n);

    [LibraryImport(Lib)]
    public static partial void lua_pushinteger(nint L, nint n);

    [LibraryImport(Lib)]
    public static partial void lua_pushboolean(nint L, int b);

    [LibraryImport(Lib)]
    public static partial void lua_pushlstring(nint L, byte* s, nuint l);

    [LibraryImport(Lib)]
    public static partial void lua_pushstring(nint L, byte* s);

    [LibraryImport(Lib, EntryPoint = "lua_pushstring", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void lua_pushstring(nint L, string s);

    [LibraryImport(Lib)]
    public static partial void lua_pushcclosure(nint L, delegate* unmanaged[Cdecl]<nint, int> fn, int n);

    /// <summary>Pushes a C function given by raw address (used for the native shim functions).</summary>
    [LibraryImport(Lib, EntryPoint = "lua_pushcclosure")]
    public static partial void lua_pushcclosure(nint L, nint fn, int n);

    [LibraryImport(Lib)]
    public static partial void lua_pushlightuserdata(nint L, void* p);

    [LibraryImport(Lib)]
    public static partial void* lua_newuserdata(nint L, nuint sz);

    // ------------------------------------------------------------------ tables

    /// <summary>Can raise through metamethods: never call on script-controlled values from a host function.</summary>
    [LibraryImport(Lib)]
    public static partial void lua_getfield(nint L, int idx, byte* k);

    /// <summary>Can raise through metamethods: never call on script-controlled values from a host function.</summary>
    [LibraryImport(Lib, EntryPoint = "lua_getfield", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void lua_getfield(nint L, int idx, string k);

    /// <summary>Can raise through metamethods: never call on script-controlled values from a host function.</summary>
    [LibraryImport(Lib)]
    public static partial void lua_setfield(nint L, int idx, byte* k);

    /// <summary>Can raise through metamethods: never call on script-controlled values from a host function.</summary>
    [LibraryImport(Lib, EntryPoint = "lua_setfield", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void lua_setfield(nint L, int idx, string k);

    [LibraryImport(Lib)]
    public static partial void lua_rawget(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial void lua_rawset(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial void lua_rawgeti(nint L, int idx, int n);

    [LibraryImport(Lib)]
    public static partial void lua_rawseti(nint L, int idx, int n);

    [LibraryImport(Lib)]
    public static partial void lua_createtable(nint L, int narr, int nrec);

    [LibraryImport(Lib)]
    public static partial int lua_next(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial int lua_getmetatable(nint L, int objindex);

    [LibraryImport(Lib)]
    public static partial int lua_setmetatable(nint L, int objindex);

    [LibraryImport(Lib)]
    public static partial void lua_getfenv(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial int lua_setfenv(nint L, int idx);

    [LibraryImport(Lib)]
    public static partial int luaL_newmetatable(nint L, byte* tname);

    // ------------------------------------------------------------------ references, hooks, gc

    [LibraryImport(Lib)]
    public static partial int luaL_ref(nint L, int t);

    [LibraryImport(Lib)]
    public static partial void luaL_unref(nint L, int t, int @ref);

    /// <summary>
    ///     The only Lua API that may be called from another thread. The hook is global to the whole Lua universe
    ///     (every coroutine); pass <c>null, 0, 0</c> to clear it.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial int lua_sethook(nint L, delegate* unmanaged[Cdecl]<nint, nint, void> func, int mask, int count);

    [LibraryImport(Lib)]
    public static partial int lua_gc(nint L, int what, int data);

    // ------------------------------------------------------------------ ksa shim (tools/host_shim.c)

    /// <summary>Count hook that raises "script timed out" from a native frame.</summary>
    [LibraryImport(Lib)]
    public static partial void host_timeout_hook(nint L, nint ar);

    /// <summary>A <c>lua_CFunction</c> raising the value on top of the stack.</summary>
    [LibraryImport(Lib)]
    public static partial int host_raise(nint L);

    /// <summary>A <c>lua_CFunction</c> message handler producing message plus traceback.</summary>
    [LibraryImport(Lib)]
    public static partial int host_traceback(nint L);

    // ------------------------------------------------------------------ raw shim addresses

    /// <summary>The address of <c>host_timeout_hook</c> (a <c>lua_Hook</c>; valid after <see cref="LuaNativeLoader.TryEnsureLoaded"/> succeeded).</summary>
    public static nint HostTimeoutHookAddress => LuaNativeLoader.GetShimExport("host_timeout_hook");

    /// <summary>The address of <c>host_raise</c> (a <c>lua_CFunction</c>).</summary>
    public static nint HostRaiseAddress => LuaNativeLoader.GetShimExport("host_raise");

    /// <summary>The address of <c>host_traceback</c> (a <c>lua_CFunction</c>).</summary>
    public static nint HostTracebackAddress => LuaNativeLoader.GetShimExport("host_traceback");
}
