using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KsaScripting.Core.Commands;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Values;
using KsaScripting.Runtime.Lua.Native;
using static KsaScripting.Runtime.Lua.Native.LuaNative;

namespace KsaScripting.Runtime.Lua.Interop;

// THE NON-RAISING RULE.
//
// Everything in this file is called by Lua through [UnmanagedCallersOnly] frames. A Lua error raised
// while such a frame is on the native stack would unwind (via the platform C++ unwinder / SEH) through a
// managed frame, which .NET does not support: the process panics. Therefore:
//   * every host function body is wrapped in try/catch (Exception) and reports failure by RETURNING
//     (false, "message"); success is (true, value). Nothing here ever raises a Lua error.
//   * the ONLY Lua API calls allowed on script-controlled values are:
//       lua_gettop, lua_settop, lua_checkstack, lua_type, lua_toboolean, lua_tonumber, lua_tolstring
//       (strings and numbers only, argument slots only), lua_objlen (raw), lua_topointer, lua_next,
//       lua_rawget, lua_rawgeti, lua_rawset, lua_rawseti, lua_createtable, lua_push*, lua_touserdata
//       (on our own light-userdata upvalue), luaL_ref / luaL_unref on the registry.
//   * lua_createtable / lua_pushlstring are only reached with sizes below LuaJIT's table and string limits
//     (LuaValues bounds them), because exceeding those limits raises.
//   * never lua_getfield / lua_setfield / lua_gettable / lua_settable / lua_call / luaL_check* /
//     luaL_error / lua_error on script values.
// Raising happens in the Lua prelude (error(msg, 2) after a false result) or via the host_raise shim.

/// <summary>The functions of the <c>ksa_host</c> table (docs/IMPLEMENTATION_GUIDE.md section 3.7) and the runtime-private <c>internal</c> table.</summary>
internal static unsafe class HostFunctions
{
    private const int MaxVectorLength = 4096;

    /// <summary>The names of the functions in the <c>ksa_host</c> table, in registration order.</summary>
    public static readonly string[] HostTableNames =
    [
        "read", "snapshot", "list", "act", "wait", "subscribe", "unsubscribe", "log", "console_exec",
        "state_get", "state_set", "term_write", "term_info", "term_set_title", "term_policy",
    ];

    /// <summary>Pushes a new <c>ksa_host</c> table whose closures all carry <paramref name="runtimeHandle"/> as upvalue 1.</summary>
    /// <param name="L">The Lua state.</param>
    /// <param name="runtimeHandle">A <see cref="GCHandle.ToIntPtr"/> value of the owning <see cref="LuaRuntime"/>.</param>
    public static void PushHostTable(nint L, nint runtimeHandle)
    {
        lua_createtable(L, 0, 16);
        var t = lua_gettop(L);
        Add(L, t, "read", &Read, runtimeHandle);
        Add(L, t, "snapshot", &Snapshot, runtimeHandle);
        Add(L, t, "list", &List, runtimeHandle);
        Add(L, t, "act", &Act, runtimeHandle);
        Add(L, t, "wait", &Wait, runtimeHandle);
        Add(L, t, "subscribe", &Subscribe, runtimeHandle);
        Add(L, t, "unsubscribe", &Unsubscribe, runtimeHandle);
        Add(L, t, "log", &Log, runtimeHandle);
        Add(L, t, "console_exec", &ConsoleExec, runtimeHandle);
        Add(L, t, "state_get", &StateGet, runtimeHandle);
        Add(L, t, "state_set", &StateSet, runtimeHandle);
        Add(L, t, "term_write", &TermWrite, runtimeHandle);
        Add(L, t, "term_info", &TermInfo, runtimeHandle);
        Add(L, t, "term_set_title", &TermSetTitle, runtimeHandle);
        Add(L, t, "term_policy", &TermPolicy, runtimeHandle);
    }

    /// <summary>Pushes the runtime-private <c>internal</c> table (<c>read_module</c>, <c>interrupted</c>).</summary>
    /// <param name="L">The Lua state.</param>
    /// <param name="runtimeHandle">A <see cref="GCHandle.ToIntPtr"/> value of the owning <see cref="LuaRuntime"/>.</param>
    public static void PushInternalTable(nint L, nint runtimeHandle)
    {
        lua_createtable(L, 0, 2);
        var t = lua_gettop(L);
        Add(L, t, "read_module", &ReadModule, runtimeHandle);
        Add(L, t, "interrupted", &Interrupted, runtimeHandle);
    }

    // Raw set into a fresh table (no metatable, so nothing can run); `table` is an absolute index.
    private static void Add(nint L, int table, string name, delegate* unmanaged[Cdecl]<nint, int> fn, nint runtimeHandle)
    {
        LuaValues.PushUtf8(L, name);
        lua_pushlightuserdata(L, (void*)runtimeHandle);
        lua_pushcclosure(L, fn, 1);
        lua_rawset(L, table);
    }

    // ================================================================== ksa_host functions

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var handle = ArgString(L, 1, "read", "handle");
            var path = OptString(L, 2, "read", "path") ?? string.Empty;
            return Ok(L, rt, rt.Host.Read(handle, path));
        }
        catch (Exception ex)
        {
            return Fail(L, "read", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Snapshot(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var handle = ArgString(L, 1, "snapshot", "handle");
            return Ok(L, rt, rt.Host.Snapshot(handle));
        }
        catch (Exception ex)
        {
            return Fail(L, "snapshot", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int List(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var kind = ArgString(L, 1, "list", "kind");
            return Ok(L, rt, rt.Host.List(kind));
        }
        catch (Exception ex)
        {
            return Fail(L, "list", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Act(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var action = ArgString(L, 1, "act", "action");
            var vesselId = OptString(L, 2, "act", "vesselId");
            var ordinal = (int)OptNumber(L, 3, "act", "ordinal", SimCommand.NoOrdinal);
            var value = OptNumber(L, 4, "act", "value", 0);
            var values = OptVector(L, 5, "act", "values");
            var token = OptString(L, 6, "act", "token");
            var aux = OptString(L, 7, "act", "aux");

            var result = rt.Host.Act(action, vesselId, ordinal, value, values, token, aux);
            if (result.Pending is { } pending)
            {
                rt.RegisterWait(pending);
                return Ok(L, rt, pending.ToScriptValue());
            }

            if (result.Immediate is { } immediate)
                return Ok(L, rt, immediate.ToScriptValue());

            throw new InvalidOperationException("the host returned neither an immediate result nor a wait");
        }
        catch (Exception ex)
        {
            return Fail(L, "act", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Wait(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var kind = ArgString(L, 1, "wait", "kind");
            var arg = rt.Values.ToScriptValue(L, 2); // none/nil -> Null
            var wait = rt.Host.Wait(kind, arg);
            rt.RegisterWait(wait);
            return Ok(L, rt, wait.ToScriptValue());
        }
        catch (Exception ex)
        {
            return Fail(L, "wait", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Subscribe(nint L)
    {
        ScriptCallable? callable = null;
        try
        {
            var rt = Runtime(L);
            var kind = ArgString(L, 1, "subscribe", "kind");
            var key = ArgString(L, 2, "subscribe", "key");
            if (lua_type(L, 3) != LUA_TFUNCTION)
                throw new ArgumentException("ksa_host.subscribe: fn must be a function");
            var options = ReadBindingOptions(L, 4);

            callable = rt.RegisterFunction(L, 3);
            var id = rt.Host.Subscribe(kind, key, callable, options);
            callable = null; // the host owns it now
            return Ok(L, rt, ScriptValue.From(id.ToString("D")));
        }
        catch (Exception ex)
        {
            try
            {
                callable?.Dispose();
            }
            catch
            {
                // never let cleanup raise
            }

            return Fail(L, "subscribe", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unsubscribe(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var text = ArgString(L, 1, "unsubscribe", "id");
            if (!Guid.TryParse(text, out var id))
                throw new ArgumentException("ksa_host.unsubscribe: id is not a valid subscription id");
            rt.Host.Unsubscribe(id);
            return OkNil(L);
        }
        catch (Exception ex)
        {
            return Fail(L, "unsubscribe", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Log(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var level = (int)ArgNumber(L, 1, "log", "level");
            var message = ArgString(L, 2, "log", "message");
            rt.Host.Log(level, message);
            return OkNil(L);
        }
        catch (Exception ex)
        {
            return Fail(L, "log", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ConsoleExec(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var command = ArgString(L, 1, "console_exec", "command");
            rt.Host.ConsoleExec(command);
            return OkNil(L);
        }
        catch (Exception ex)
        {
            return Fail(L, "console_exec", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int StateGet(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var key = ArgString(L, 1, "state_get", "key");
            return Ok(L, rt, rt.Host.StateGet(key));
        }
        catch (Exception ex)
        {
            return Fail(L, "state_get", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int StateSet(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var key = ArgString(L, 1, "state_set", "key");
            var value = rt.Values.ToScriptValue(L, 2);
            rt.Host.StateSet(key, value);
            return OkNil(L);
        }
        catch (Exception ex)
        {
            return Fail(L, "state_set", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TermWrite(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var termId = (int)ArgNumber(L, 1, "term_write", "termId");
            if (!LuaValues.TryGetRawBytes(L, 2, out var bytes))
                throw new ArgumentException("ksa_host.term_write: bytes must be a string");
            rt.Host.TermWrite(termId, bytes);
            return OkNil(L);
        }
        catch (Exception ex)
        {
            return Fail(L, "term_write", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TermInfo(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var termId = (int)ArgNumber(L, 1, "term_info", "termId");
            return Ok(L, rt, rt.Host.TermInfo(termId));
        }
        catch (Exception ex)
        {
            return Fail(L, "term_info", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TermSetTitle(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var termId = (int)ArgNumber(L, 1, "term_set_title", "termId");
            var title = ArgString(L, 2, "term_set_title", "title");
            rt.Host.TermSetTitle(termId, title);
            return OkNil(L);
        }
        catch (Exception ex)
        {
            return Fail(L, "term_set_title", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TermPolicy(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var termId = (int)ArgNumber(L, 1, "term_policy", "termId");
            if (lua_type(L, 2) != LUA_TBOOLEAN)
                throw new ArgumentException("ksa_host.term_policy: stopWhenDetached must be a boolean");
            rt.Host.TermPolicy(termId, lua_toboolean(L, 2) != 0);
            return OkNil(L);
        }
        catch (Exception ex)
        {
            return Fail(L, "term_policy", ex);
        }
    }

    // ================================================================== internal table

    /// <summary><c>internal.read_module(name)</c> returns <c>(true, source, chunkName)</c> or <c>(false, message)</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadModule(nint L)
    {
        try
        {
            var rt = Runtime(L);
            var name = ArgString(L, 1, "read_module", "name");
            if (!ModuleReader.TryRead(rt.ScriptsDir, name, out var source, out var chunkName, out var fullPath, out var error))
            {
                lua_pushboolean(L, 0);
                LuaValues.PushUtf8(L, error);
                return 2;
            }

            rt.NoteChunk(chunkName[1..], fullPath);
            lua_pushboolean(L, 1);
            LuaValues.PushBytes(L, source);
            LuaValues.PushUtf8(L, chunkName);
            return 3;
        }
        catch (Exception ex)
        {
            return Fail(L, "read_module", ex);
        }
    }

    /// <summary><c>internal.interrupted()</c> returns <c>(true, boolean)</c>: whether the host requested an interrupt.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Interrupted(nint L)
    {
        try
        {
            var rt = Runtime(L);
            lua_pushboolean(L, 1);
            lua_pushboolean(L, rt.IsInterruptRequested ? 1 : 0);
            return 2;
        }
        catch (Exception ex)
        {
            return Fail(L, "interrupted", ex);
        }
    }

    // ================================================================== helpers

    private static LuaRuntime Runtime(nint L)
    {
        var p = lua_touserdata(L, lua_upvalueindex(1));
        if (p == null)
            throw new InvalidOperationException("ksa_host: runtime handle missing");
        if (GCHandle.FromIntPtr((nint)p).Target is not LuaRuntime rt)
            throw new InvalidOperationException("ksa_host: the Lua runtime has been disposed");
        return rt;
    }

    private static int Ok(nint L, LuaRuntime rt, ScriptValue value)
    {
        lua_pushboolean(L, 1);
        rt.Values.Push(L, value);
        return 2;
    }

    private static int OkNil(nint L)
    {
        lua_pushboolean(L, 1);
        lua_pushnil(L);
        return 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Fail(nint L, string fn, Exception ex)
    {
        try
        {
            var message = ex is ArgumentException ? ex.Message : $"ksa_host.{fn}: {ex.Message}";
            lua_settop(L, 0);
            lua_checkstack(L, 4);
            lua_pushboolean(L, 0);
            LuaValues.PushUtf8(L, message);
            return 2;
        }
        catch
        {
            return 0;
        }
    }

    private static string ArgString(nint L, int index, string fn, string name)
    {
        var type = lua_type(L, index);
        if (type != LUA_TSTRING && type != LUA_TNUMBER)
            throw new ArgumentException($"ksa_host.{fn}: {name} must be a string");
        return LuaValues.ReadString(L, index);
    }

    private static string? OptString(nint L, int index, string fn, string name)
    {
        var type = lua_type(L, index);
        if (type is LUA_TNIL or LUA_TNONE)
            return null;
        if (type != LUA_TSTRING && type != LUA_TNUMBER)
            throw new ArgumentException($"ksa_host.{fn}: {name} must be a string or nil");
        return LuaValues.ReadString(L, index);
    }

    private static double ArgNumber(nint L, int index, string fn, string name)
    {
        if (lua_type(L, index) != LUA_TNUMBER)
            throw new ArgumentException($"ksa_host.{fn}: {name} must be a number");
        return lua_tonumber(L, index);
    }

    private static double OptNumber(nint L, int index, string fn, string name, double fallback)
    {
        var type = lua_type(L, index);
        if (type is LUA_TNIL or LUA_TNONE)
            return fallback;
        if (type != LUA_TNUMBER)
            throw new ArgumentException($"ksa_host.{fn}: {name} must be a number or nil");
        return lua_tonumber(L, index);
    }

    private static double[]? OptVector(nint L, int index, string fn, string name)
    {
        var type = lua_type(L, index);
        if (type is LUA_TNIL or LUA_TNONE)
            return null;
        if (type != LUA_TTABLE)
            throw new ArgumentException($"ksa_host.{fn}: {name} must be an array of numbers or nil");

        var n = (long)lua_objlen(L, index);
        if (n > MaxVectorLength)
            throw new ArgumentException($"ksa_host.{fn}: {name} has too many elements");
        var result = new double[n];
        for (var i = 1; i <= n; i++)
        {
            lua_rawgeti(L, index, i);
            var elementType = lua_type(L, -1);
            var number = elementType == LUA_TNUMBER ? lua_tonumber(L, -1) : 0;
            lua_settop(L, -2);
            if (elementType != LUA_TNUMBER)
                throw new ArgumentException($"ksa_host.{fn}: {name}[{i}] must be a number");
            result[i - 1] = number;
        }

        return result;
    }

    private static BindingOptions ReadBindingOptions(nint L, int index)
    {
        var defaults = new BindingOptions();
        if (lua_type(L, index) != LUA_TTABLE)
            return defaults;

        var sync = defaults.Sync;
        var vessel = defaults.Vessel;
        var clock = defaults.Clock;
        var interval = defaults.IntervalSeconds;

        if (RawField(L, index, "sync") == LUA_TBOOLEAN)
            sync = lua_toboolean(L, -1) != 0;
        lua_settop(L, -2);

        if (RawField(L, index, "vessel") == LUA_TSTRING)
            vessel = LuaValues.ReadString(L, lua_gettop(L));
        lua_settop(L, -2);

        if (RawField(L, index, "clock") == LUA_TSTRING)
            clock = LuaValues.ReadString(L, lua_gettop(L));
        lua_settop(L, -2);

        if (RawField(L, index, "intervalSeconds") == LUA_TNUMBER)
            interval = lua_tonumber(L, -1);
        lua_settop(L, -2);

        return new BindingOptions(sync, vessel, clock, interval);
    }

    // Pushes table[key] (raw, no metamethods) and reports its type; the caller pops it.
    private static int RawField(nint L, int table, string key)
    {
        lua_checkstack(L, 4);
        LuaValues.PushUtf8(L, key);
        lua_rawget(L, table);
        return lua_type(L, -1);
    }
}
