using System.Buffers;
using System.Globalization;
using System.Text;
using Catcom.Core.Values;
using Catcom.Runtime.Lua.Native;
using static Catcom.Runtime.Lua.Native.LuaNative;

namespace Catcom.Runtime.Lua.Interop;

// NON-RAISING marshalling between Lua values and ScriptValue.
//
// These routines run inside [UnmanagedCallersOnly] host functions (and on the game thread between
// lua_resume calls), so a Lua error raised from here would unwind through a managed frame and panic
// the process. The ONLY Lua API used on script values is:
//   lua_type, lua_to* (strings/numbers/booleans/pointers), lua_objlen (raw, no __len), lua_rawget,
//   lua_rawgeti, lua_rawseti, lua_rawset, lua_next, lua_createtable, lua_push*, lua_checkstack,
//   lua_gettop, lua_settop, lua_pushvalue, luaL_ref / lua_rawgeti on the registry.
// Never lua_getfield / lua_setfield / lua_gettable / lua_settable on script values (metamethods may
// raise), never luaL_check*, never lua_error / lua_call.

/// <summary>Converts between Lua stack values and <see cref="ScriptValue"/> without ever raising a Lua error.</summary>
internal sealed unsafe class LuaValues
{
    /// <summary>Nesting depth beyond which tables convert to <see cref="ScriptValue.Null"/>.</summary>
    public const int MaxDepth = 32;

    /// <summary>
    ///     Upper bound on the number of values converted in one call, in either direction (guards shared sub-tables), and on
    ///     the number of elements of any single table pushed to Lua. Far below LuaJIT's own table limits, which raise.
    /// </summary>
    public const int MaxNodes = 1_000_000;

    /// <summary>Longest string pushed to Lua; longer ones are truncated (LuaJIT raises "string length overflow" at about 2 GiB).</summary>
    public const int MaxStringBytes = 0x7fff0000;

    private readonly LuaRuntime _runtime;

    /// <summary>Creates a marshaller bound to a runtime (which owns the registered function references).</summary>
    /// <param name="runtime">The owning runtime.</param>
    public LuaValues(LuaRuntime runtime)
    {
        _runtime = runtime;
    }

    // ------------------------------------------------------------------ Lua -> ScriptValue

    /// <summary>Converts the value at <paramref name="index"/> to a <see cref="ScriptValue"/>. The stack is unchanged.</summary>
    /// <param name="L">The Lua state (any coroutine).</param>
    /// <param name="index">A stack index (negative indices are made absolute).</param>
    /// <returns>The converted value.</returns>
    public ScriptValue ToScriptValue(nint L, int index)
    {
        var abs = Absolute(L, index);
        var budget = MaxNodes;
        List<nint>? path = null;
        return Convert(L, abs, 0, ref budget, ref path);
    }

    private ScriptValue Convert(nint L, int idx, int depth, ref int budget, ref List<nint>? path)
    {
        if (--budget < 0)
            return ScriptValue.Null;

        switch (lua_type(L, idx))
        {
            case LUA_TBOOLEAN:
                return ScriptValue.From(lua_toboolean(L, idx) != 0);
            case LUA_TNUMBER:
                return ScriptValue.From(lua_tonumber(L, idx));
            case LUA_TSTRING:
                return ScriptValue.From(ReadString(L, idx));
            case LUA_TTABLE:
                return ConvertTable(L, idx, depth, ref budget, ref path);
            case LUA_TFUNCTION:
                return ScriptValue.Function(_runtime.RegisterFunction(L, idx));
            default: // nil, none, userdata, lightuserdata, thread
                return ScriptValue.Null;
        }
    }

    private ScriptValue ConvertTable(nint L, int idx, int depth, ref int budget, ref List<nint>? path)
    {
        if (depth >= MaxDepth)
            return ScriptValue.Null;

        // Cycle guard: a table that contains itself (directly or indirectly) becomes Null at the repeat.
        var ptr = lua_topointer(L, idx);
        var current = path ??= new List<nint>(8);
        if (current.Contains(ptr))
            return ScriptValue.Null;
        current.Add(ptr);
        try
        {
            if (lua_checkstack(L, 8) == 0)
                return ScriptValue.Null;

            var n = (long)lua_objlen(L, idx);
            var isSequence = n > 0;
            long entries = 0;

            // Classification pass: a proper sequence has exactly the integer keys 1..n.
            lua_pushnil(L);
            while (lua_next(L, idx) != 0)
            {
                entries++;
                if (isSequence)
                {
                    if (lua_type(L, -2) != LUA_TNUMBER)
                    {
                        isSequence = false;
                    }
                    else
                    {
                        var k = lua_tonumber(L, -2);
                        if (k < 1 || k > n || k != Math.Floor(k))
                            isSequence = false;
                    }
                }

                lua_settop(L, -2); // pop value, keep key for the next iteration
            }

            if (isSequence && entries == n)
            {
                var items = new ScriptValue[n];
                for (var i = 1; i <= n; i++)
                {
                    lua_rawgeti(L, idx, i);
                    items[i - 1] = Convert(L, lua_gettop(L), depth + 1, ref budget, ref path);
                    lua_settop(L, -2);
                }

                return ScriptValue.List(items);
            }

            var map = new Dictionary<string, ScriptValue>();
            lua_pushnil(L);
            while (lua_next(L, idx) != 0)
            {
                // Stack: ... key value. Never call lua_tolstring on a number key (it converts in place and breaks lua_next).
                string? key = null;
                switch (lua_type(L, -2))
                {
                    case LUA_TSTRING:
                        key = ReadString(L, lua_gettop(L) - 1);
                        break;
                    case LUA_TNUMBER:
                        key = lua_tonumber(L, -2).ToString("R", CultureInfo.InvariantCulture);
                        break;
                }

                if (key is not null)
                    map[key] = Convert(L, lua_gettop(L), depth + 1, ref budget, ref path);

                lua_settop(L, -2);
            }

            return ScriptValue.Map(map);
        }
        finally
        {
            current.RemoveAt(current.Count - 1);
        }
    }

    // ------------------------------------------------------------------ ScriptValue -> Lua

    /// <summary>Pushes <paramref name="value"/> onto the stack of <paramref name="L"/> (exactly one new slot).</summary>
    /// <param name="L">The Lua state (any coroutine).</param>
    /// <param name="value">The value to push.</param>
    public void Push(nint L, ScriptValue value)
    {
        var budget = MaxNodes;
        Push(L, value, 0, ref budget);
    }

    // Values beyond the depth, node or table-size limits become nil (as in the other direction); nothing here raises.
    private void Push(nint L, ScriptValue value, int depth, ref int budget)
    {
        if (--budget < 0 || depth > MaxDepth || lua_checkstack(L, 6) == 0)
        {
            lua_pushnil(L);
            return;
        }

        switch (value.Kind)
        {
            case ScriptValueKind.Bool:
                lua_pushboolean(L, value.AsBool() ? 1 : 0);
                break;
            case ScriptValueKind.Number:
                lua_pushnumber(L, value.AsNumber());
                break;
            case ScriptValueKind.String:
                PushUtf8(L, value.AsString());
                break;
            case ScriptValueKind.Handle:
                PushUtf8(L, value.AsHandle());
                break;
            case ScriptValueKind.Bytes:
                PushBytes(L, value.AsBytes().Span);
                break;
            case ScriptValueKind.List:
            {
                var items = value.AsList();
                if (items.Count > MaxNodes)
                {
                    lua_pushnil(L);
                    break;
                }

                lua_createtable(L, items.Count, 0);
                for (var i = 0; i < items.Count; i++)
                {
                    Push(L, items[i], depth + 1, ref budget);
                    lua_rawseti(L, -2, i + 1);
                }

                break;
            }
            case ScriptValueKind.Map:
            {
                var map = value.AsMap();
                if (map.Count > MaxNodes)
                {
                    lua_pushnil(L);
                    break;
                }

                lua_createtable(L, 0, map.Count);
                foreach (var (key, item) in map)
                {
                    PushUtf8(L, key);
                    Push(L, item, depth + 1, ref budget);
                    lua_rawset(L, -3);
                }

                break;
            }
            case ScriptValueKind.Function:
                if (!_runtime.TryPushFunction(L, value.AsFunction()))
                    lua_pushnil(L);
                break;
            default: // Null
                lua_pushnil(L);
                break;
        }
    }

    // ------------------------------------------------------------------ strings and bytes

    /// <summary>Pushes a managed string as UTF-8 bytes.</summary>
    /// <param name="L">The Lua state.</param>
    /// <param name="s">The string.</param>
    public static void PushUtf8(nint L, string s)
    {
        var byteCount = Encoding.UTF8.GetByteCount(s);
        if (byteCount > MaxStringBytes)
        {
            PushUtf8(L, s[..(s.Length / 2)]); // pathological size: keep halving until it fits (never raise)
            return;
        }

        if (byteCount <= 512)
        {
            Span<byte> buffer = stackalloc byte[512];
            var written = Encoding.UTF8.GetBytes(s, buffer);
            fixed (byte* p = buffer)
                lua_pushlstring(L, p, (nuint)written);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(s, rented);
            fixed (byte* p = rented)
                lua_pushlstring(L, p, (nuint)written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Pushes raw bytes as a Lua string (no encoding).</summary>
    /// <param name="L">The Lua state.</param>
    /// <param name="bytes">The bytes.</param>
    public static void PushBytes(nint L, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxStringBytes)
            bytes = bytes[..MaxStringBytes];

        fixed (byte* p = bytes)
            lua_pushlstring(L, p, (nuint)bytes.Length);
    }

    /// <summary>Reads the string (or number) at <paramref name="index"/> as UTF-8 text (invalid bytes become U+FFFD).</summary>
    /// <param name="L">The Lua state.</param>
    /// <param name="index">The stack index. A number is converted in place, so only pass argument slots.</param>
    /// <returns>The decoded text.</returns>
    public static string ReadString(nint L, int index)
    {
        nuint len;
        var p = lua_tolstring(L, index, &len);
        if (p == null)
            return string.Empty;
        return Encoding.UTF8.GetString(p, checked((int)len));
    }

    /// <summary>
    ///     Gets the raw bytes of the Lua string at <paramref name="index"/> (no decoding). The span is valid only
    ///     while the value stays on the stack (i.e. for the duration of a host function call).
    /// </summary>
    /// <param name="L">The Lua state.</param>
    /// <param name="index">The stack index.</param>
    /// <param name="bytes">The string's bytes.</param>
    /// <returns><c>false</c> when the value is not a string or number.</returns>
    public static bool TryGetRawBytes(nint L, int index, out ReadOnlySpan<byte> bytes)
    {
        var type = lua_type(L, index);
        if (type != LUA_TSTRING && type != LUA_TNUMBER)
        {
            bytes = default;
            return false;
        }

        nuint len;
        var p = lua_tolstring(L, index, &len);
        bytes = p == null ? default : new ReadOnlySpan<byte>(p, checked((int)len));
        return p != null;
    }

    // ------------------------------------------------------------------ helpers

    private static int Absolute(nint L, int index)
        => index > 0 || index <= LUA_REGISTRYINDEX ? index : lua_gettop(L) + 1 + index;
}
