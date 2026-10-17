using Catcom.Core.Hosting;
using Catcom.Core.Values;
using Catcom.Runtime.Lua.Interop;
using static Catcom.Runtime.Lua.Native.LuaNative;

namespace Catcom.Runtime.Lua;

// Callable / wait bookkeeping and the internal surface the host functions use.
public sealed unsafe partial class LuaRuntime
{
    /// <summary>The bound host facade.</summary>
    internal IHostFacade Host => _host ?? throw new InvalidOperationException("The Lua runtime has not been initialized.");

    /// <summary>The non-raising value marshaller.</summary>
    internal LuaValues Values => _values ?? throw new InvalidOperationException("The Lua runtime has not been initialized.");

    /// <summary>The scripts directory (the root for <c>require</c>).</summary>
    internal string ScriptsDir => _options?.ScriptsDir ?? string.Empty;

    /// <summary>
    ///     Anchors the Lua function at <paramref name="index"/> in the registry and wraps it in a
    ///     <see cref="ScriptCallable"/> owned by the module that is currently executing.
    /// </summary>
    /// <param name="L">The Lua state (any coroutine).</param>
    /// <param name="index">The stack index of a function.</param>
    /// <returns>The callable.</returns>
    internal ScriptCallable RegisterFunction(nint L, int index)
    {
        if (index < 0 && index > LUA_REGISTRYINDEX)
            index = lua_gettop(L) + 1 + index;

        lua_pushvalue(L, index);
        var reference = luaL_ref(L, LUA_REGISTRYINDEX);
        return CreateCallable(reference, _currentModule, null);
    }

    /// <summary>Pushes the function behind a callable of this runtime, if it is still registered.</summary>
    /// <param name="L">The Lua state (any coroutine).</param>
    /// <param name="callable">The callable.</param>
    /// <returns><c>false</c> when the callable is foreign, disposed or unknown (nothing was pushed).</returns>
    internal bool TryPushFunction(nint L, ScriptCallable callable)
    {
        if (callable.RuntimeId != Id || callable.IsDisposed || !_callables.TryGetValue(callable.Token, out var entry))
            return false;

        lua_rawgeti(L, LUA_REGISTRYINDEX, entry.Ref);
        return true;
    }

    /// <summary>Remembers a wait the host created so that the coroutine yielding <c>{"$wait": id}</c> can be parked on it.</summary>
    /// <param name="wait">The wait.</param>
    internal void RegisterWait(HostWait wait)
    {
        _waits[wait.Id] = wait;
        _waitsThisCall.Add(wait.Id);
    }

    /// <summary>Records which file a chunk name (e.g. <c>scripts/lib/tui.lua</c>) came from, for error locations.</summary>
    /// <param name="chunkName">The chunk name without the leading <c>@</c>.</param>
    /// <param name="path">The absolute file path.</param>
    internal void NoteChunk(string chunkName, string path) => _chunkPaths[chunkName] = path;

    private ScriptCallable CreateCallable(int reference, ModuleState? owner, string? name)
    {
        var token = NextToken();
        _callables[token] = new CallableEntry(token, reference, owner?.Token ?? 0);
        owner?.OwnedCallables.Add(token);
        return ScriptCallable.Create(Id, token, name, OnCallableDisposed);
    }

    // ScriptCallable.Dispose may run on any thread: only enqueue; the game thread unrefs at its next entry point.
    private void OnCallableDisposed(ScriptCallable callable) => _pendingUnrefs.Enqueue(callable.Token);

    private void DrainPendingUnrefs()
    {
        if (!Ready)
            return;

        var L = _L;
        while (_pendingUnrefs.TryDequeue(out var token))
        {
            if (!_callables.Remove(token, out var entry))
                continue;

            luaL_unref(L, LUA_REGISTRYINDEX, entry.Ref);
            if (_modules.TryGetValue(entry.OwnerToken, out var owner))
                owner.OwnedCallables.Remove(token);
        }
    }

    // Waits created during an outermost call but never yielded are dropped at its end.
    private void PruneUnclaimedWaits()
    {
        foreach (var id in _waitsThisCall)
            _waits.Remove(id);
        _waitsThisCall.Clear();
    }
}
