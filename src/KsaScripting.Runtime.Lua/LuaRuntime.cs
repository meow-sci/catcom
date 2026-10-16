using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KsaScripting.Core.Hosting;
using KsaScripting.Core.Runtime;
using KsaScripting.Core.Values;
using KsaScripting.Runtime.Lua.Interop;
using KsaScripting.Runtime.Lua.Native;
using static KsaScripting.Runtime.Lua.Native.LuaNative;

namespace KsaScripting.Runtime.Lua;

/// <summary>
///     The LuaJIT 2.1 script runtime: one <c>lua_State</c> per runtime, a sandbox environment per loaded script,
///     every invocation inside its own coroutine, host waits turned into coroutine yields, and a watchdog
///     interrupt that works with the JIT on or off. All members run on the game thread except
///     <see cref="Interrupt"/> (any thread) and <see cref="IDisposable.Dispose"/> of the callables it hands out.
/// </summary>
/// <remarks>
///     Lua errors never unwind through managed frames: every C# to Lua entry point is <c>lua_pcall</c> or
///     <c>lua_resume</c>, and the host functions Lua calls (see <c>HostFunctions</c>) only use non-raising API calls.
/// </remarks>
public sealed unsafe partial class LuaRuntime : IScriptRuntime
{
    private const string ResourcePrefix = "KsaScripting.Runtime.Lua.";

    // ---- native state (game thread; _L is also read under _hookGate by Interrupt/Dispose) ----
    private nint _L;
    private GCHandle _handle;
    private LuaValues? _values;
    private nint _hookAddress;
    private int _refTraceback = LUA_NOREF;
    private int _refNewEnv = LUA_NOREF;
    private int _refRunPrelude = LUA_NOREF;
    private int _refInvoke = LUA_NOREF;
    private int _refCollectExports = LUA_NOREF;
    private int _refReleaseEnv = LUA_NOREF;
    private byte[] _preludeBytes = [];

    // ---- lifecycle ----
    private IHostFacade? _host;
    private RuntimeOptions? _options;
    private bool _hostBound;
    private int _disposed;
    private bool _availabilityKnown;
    private string? _unavailableReason;
    private bool _jitEnabled;
    private string _version = string.Empty;

    // ---- registry bookkeeping (game thread only, except _pendingUnrefs) ----
    private long _nextToken;
    private readonly Dictionary<long, ModuleState> _modules = [];
    private readonly Dictionary<long, CallableEntry> _callables = [];
    private readonly ConcurrentQueue<long> _pendingUnrefs = new();
    private readonly Dictionary<long, HostWait> _waits = [];
    private readonly List<long> _waitsThisCall = [];
    private readonly List<ParkedCoroutine> _parked = [];
    private readonly Dictionary<string, string> _chunkPaths = new(StringComparer.Ordinal);
    private ModuleState? _currentModule;

    // ---- interrupt / call depth: all writes under _hookGate ----
    private readonly object _hookGate = new();
    private nint _hookState;
    private int _depth;
    private int _interruptRequested;
    private bool _hookArmed;
    private long _hookArmedAt;
    private bool _pendingInterrupt;
    private long _pendingInterruptAt;

    // ---- stats ----
    private double _lastCallMs;
    private long _callsTotal;

    // Probes the native library; replaced in tests to simulate an unavailable runtime.
    private readonly Func<string?> _availabilityProbe;

    /// <summary>Creates a runtime. The constructor does not touch native code.</summary>
    public LuaRuntime()
        : this(static () =>
        {
            LuaNativeLoader.TryEnsureLoaded(out var reason);
            return reason;
        })
    {
    }

    /// <summary>Creates a runtime with a custom availability probe (tests).</summary>
    /// <param name="availabilityProbe">Returns the reason the native library is unusable, or <c>null</c> when it is usable.</param>
    internal LuaRuntime(Func<string?> availabilityProbe)
    {
        _availabilityProbe = availabilityProbe;
    }

    /// <inheritdoc />
    public string Id => "lua";

    /// <inheritdoc />
    public string Language => ScriptLanguages.Lua;

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            EnsureAvailabilityKnown();
            return _unavailableReason is null;
        }
    }

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            EnsureAvailabilityKnown();
            return _unavailableReason;
        }
    }

    /// <inheritdoc />
    public RuntimeStats Stats
    {
        get
        {
            long heap = 0;
            var L = _L;
            if (L != 0 && Volatile.Read(ref _disposed) == 0)
                heap = HeapBytes(L);
            return new RuntimeStats(heap, Volatile.Read(ref _lastCallMs), Volatile.Read(ref _callsTotal), _jitEnabled, _version);
        }
    }

    // ------------------------------------------------------------------ Initialize / Dispose

    /// <inheritdoc />
    public void Initialize(IHostFacade host, RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_hostBound)
            return;

        _host = host;
        _options = options;
        _hostBound = true;

        if (!IsAvailable)
            return;

        try
        {
            InitializeState(options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _unavailableReason = $"LuaJIT runtime failed to initialize: {ex.Message}";
            CloseState();
        }
    }

    private void InitializeState(RuntimeOptions options)
    {
        var L = luaL_newstate();
        if (L == 0)
            throw new InvalidOperationException("luaL_newstate returned null");

        _L = L;
        luaL_openlibs(L);
        if (!options.JitEnabled)
            luaJIT_setmode(L, LUAJIT_MODE_ENGINE, LUAJIT_MODE_ENGINE | LUAJIT_MODE_OFF);

        _hookAddress = HostTimeoutHookAddress;
        _handle = GCHandle.Alloc(this);
        var handlePtr = GCHandle.ToIntPtr(_handle);
        _values = new LuaValues(this);
        lock (_hookGate)
            _hookState = L;

        // The traceback message handler is a native shim function, kept as a registry reference.
        lua_pushcclosure(L, HostTracebackAddress, 0);
        _refTraceback = luaL_ref(L, LUA_REGISTRYINDEX);

        _preludeBytes = ReadResource("ksa.lua");

        // Run the bootstrap with the real globals: bootstrap(ksa_host, internal) -> R.
        var baseIdx = lua_gettop(L);
        lua_rawgeti(L, LUA_REGISTRYINDEX, _refTraceback);
        var status = LoadBuffer(L, ReadResource("bootstrap.lua"), "=ksa-runtime");
        if (status != LUA_OK)
            throw new InvalidOperationException($"bootstrap.lua failed to compile: {TextAt(L, -1)}");

        HostFunctions.PushHostTable(L, handlePtr);
        HostFunctions.PushInternalTable(L, handlePtr);
        status = lua_pcall(L, 2, 1, baseIdx + 1);
        if (status != LUA_OK)
            throw new InvalidOperationException($"bootstrap.lua failed: {TextAt(L, -1)}");

        var r = lua_gettop(L);
        _refNewEnv = FieldRef(L, r, "new_env");
        _refRunPrelude = FieldRef(L, r, "run_prelude");
        _refInvoke = FieldRef(L, r, "invoke");
        _refCollectExports = FieldRef(L, r, "collect_exports");
        _refReleaseEnv = FieldRef(L, r, "release_env");
        var refJitStatus = FieldRef(L, r, "jit_status");
        var refJitVersion = FieldRef(L, r, "jit_version");
        lua_settop(L, baseIdx);

        _jitEnabled = CallForBoolean(L, refJitStatus);
        _version = CallForString(L, refJitVersion);
        luaL_unref(L, LUA_REGISTRYINDEX, refJitStatus);
        luaL_unref(L, LUA_REGISTRYINDEX, refJitVersion);
    }

    private static int FieldRef(nint L, int table, string name)
    {
        lua_getfield(L, table, name);
        if (lua_type(L, -1) != LUA_TFUNCTION)
            throw new InvalidOperationException($"bootstrap.lua did not return R.{name}");
        return luaL_ref(L, LUA_REGISTRYINDEX);
    }

    private bool CallForBoolean(nint L, int fnRef)
    {
        var baseIdx = PrepareCall(L, fnRef);
        FinishCall(L, baseIdx, 0, 1);
        var result = lua_toboolean(L, -1) != 0;
        lua_settop(L, baseIdx);
        return result;
    }

    private string CallForString(nint L, int fnRef)
    {
        var baseIdx = PrepareCall(L, fnRef);
        FinishCall(L, baseIdx, 0, 1);
        var result = lua_type(L, -1) == LUA_TSTRING ? LuaValues.ReadString(L, -1) : string.Empty;
        lua_settop(L, baseIdx);
        return result;
    }

    private static byte[] ReadResource(string name)
    {
        var assembly = typeof(LuaRuntime).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException($"embedded resource '{ResourcePrefix}{name}' is missing");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void EnsureAvailabilityKnown()
    {
        if (_availabilityKnown)
            return;

        lock (_hookGate)
        {
            if (_availabilityKnown)
                return;
            _unavailableReason ??= _availabilityProbe();
            _availabilityKnown = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CloseState();
        _modules.Clear();
        _callables.Clear();
        _waits.Clear();
        _waitsThisCall.Clear();
        _parked.Clear();
        _chunkPaths.Clear();
        _pendingUnrefs.Clear();
    }

    private void CloseState()
    {
        nint L;
        lock (_hookGate)
        {
            L = _L;
            _L = 0;
            _hookState = 0;
        }

        if (L != 0)
            lua_close(L);
        if (_handle.IsAllocated)
            _handle.Free();
    }

    // ------------------------------------------------------------------ Interrupt

    // An interrupt requested while idle applies to a call that begins within this window. The watchdog arms
    // its deadline before it calls Invoke, so a very short deadline can expire between the two.
    private static readonly long PendingInterruptWindowTicks = Stopwatch.Frequency / 4;

    // lua_sethook from another thread rewrites LuaJIT's dispatch table while the VM thread may be running or
    // compiling; a hammering caller was seen to crash the JIT. Re-arming an already armed hook is therefore
    // rate limited: one arm per call is enough, later requests only repeat it after this interval.
    private static readonly long MinRearmTicks = Stopwatch.Frequency / 50;

    /// <summary>
    ///     Aborts the call that is running right now (any thread). Arms the native count hook, which raises an error
    ///     inside the running script; the call then fails with <c>ScriptRuntimeException("interrupted")</c>. Scripts
    ///     cannot swallow the interrupt with <c>pcall</c>/<c>xpcall</c>/<c>coroutine.resume</c>. With nothing running
    ///     the request is remembered for 250 ms so that it still stops a call that is just about to start; after that,
    ///     and after <see cref="Dispose"/>, it is a no-op. In nested calls only the innermost call is interrupted.
    /// </summary>
    public void Interrupt()
    {
        lock (_hookGate)
        {
            if (_hookState == 0)
                return;

            if (_depth > 0)
            {
                Volatile.Write(ref _interruptRequested, 1);
                if (!_hookArmed || Stopwatch.GetTimestamp() - _hookArmedAt >= MinRearmTicks)
                    ArmHookLocked();
            }
            else
            {
                _pendingInterruptAt = Stopwatch.GetTimestamp();
                _pendingInterrupt = true;
            }
        }
    }

    // Must be called with _hookGate held and _hookState != 0.
    private void ArmHookLocked()
    {
        lua_sethook(_hookState, (delegate* unmanaged[Cdecl]<nint, nint, void>)_hookAddress, LUA_MASKCOUNT, 1);
        _hookArmed = true;
        _hookArmedAt = Stopwatch.GetTimestamp();
    }

    // Must be called with _hookGate held and _hookState != 0. The native hook clears itself when it fires, but the
    // runtime never relies on that: every call scope that may have been armed clears the hook on exit.
    private void ClearHookLocked()
    {
        if (!_hookArmed)
            return;

        lua_sethook(_hookState, null, 0, 0);
        _hookArmed = false;
    }

    /// <summary>Whether an interrupt was requested for the call in progress.</summary>
    internal bool IsInterruptRequested => Volatile.Read(ref _interruptRequested) != 0;

    // Enters a call scope. The outermost scope starts from a clean slate, except that an interrupt requested
    // moments earlier (see Interrupt) starts the call already interrupted.
    private void BeginCall()
    {
        lock (_hookGate)
        {
            if (_depth == 0 && _hookState != 0)
            {
                ClearHookLocked();
                Volatile.Write(ref _interruptRequested, 0);
                if (_pendingInterrupt)
                {
                    _pendingInterrupt = false;
                    if (Stopwatch.GetTimestamp() - _pendingInterruptAt < PendingInterruptWindowTicks)
                    {
                        Volatile.Write(ref _interruptRequested, 1);
                        ArmHookLocked();
                    }
                }
            }

            _depth++;
        }
    }

    // Leaves a call scope: the interrupt (if any) was meant for the call that just ended, so the hook and the flag
    // are cleared at every depth. The watchdog interrupts the innermost armed call, so an outer call is unaffected.
    private void EndCall()
    {
        lock (_hookGate)
        {
            if (_depth > 0)
                _depth--;
            if (_hookState == 0)
                return;

            ClearHookLocked();
            Volatile.Write(ref _interruptRequested, 0);
        }
    }

    // ------------------------------------------------------------------ Exports / Unload

    /// <inheritdoc />
    public IReadOnlyList<string> Exports(ScriptModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!_modules.TryGetValue(module.Token, out var state))
            return [];

        var names = state.ExportRefs.Keys.ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    /// <inheritdoc />
    public ScriptCallable GetExport(ScriptModule module, string name)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(name);
        if (!_modules.TryGetValue(module.Token, out var state))
            throw new KeyNotFoundException($"module '{module.Name}' is not loaded");
        if (!state.ExportRefs.TryGetValue(name, out var exportRef))
            throw new KeyNotFoundException($"module '{module.Name}' has no export '{name}'");

        if (state.ExportCallables.TryGetValue(name, out var existing) && !existing.IsDisposed)
            return existing;

        var L = _L;
        lua_rawgeti(L, LUA_REGISTRYINDEX, exportRef);
        var reference = luaL_ref(L, LUA_REGISTRYINDEX);
        var callable = CreateCallable(reference, state, name);
        state.ExportCallables[name] = callable;
        return callable;
    }

    /// <inheritdoc />
    public void Unload(ScriptModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!Ready)
            return;

        DrainPendingUnrefs();
        if (!_modules.Remove(module.Token, out var state))
            return;

        var L = _L;

        // Parked coroutines are dropped, never resumed.
        for (var i = _parked.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_parked[i].Owner, state))
                continue;
            luaL_unref(L, LUA_REGISTRYINDEX, _parked[i].ThreadRef);
            _parked.RemoveAt(i);
        }

        ReleaseModuleResources(state);
        lua_gc(L, LUA_GCCOLLECT, 0);
    }

    // Unrefs everything a module owns inside the Lua state.
    private void ReleaseModuleResources(ModuleState state)
    {
        var L = _L;
        foreach (var token in state.OwnedCallables)
        {
            if (_callables.Remove(token, out var entry))
                luaL_unref(L, LUA_REGISTRYINDEX, entry.Ref);
        }

        state.OwnedCallables.Clear();
        state.ExportCallables.Clear();

        foreach (var reference in state.ExportRefs.Values)
            luaL_unref(L, LUA_REGISTRYINDEX, reference);
        state.ExportRefs.Clear();

        if (state.EnvRef != LUA_NOREF)
        {
            try
            {
                var baseIdx = PrepareCall(L, _refReleaseEnv);
                lua_rawgeti(L, LUA_REGISTRYINDEX, state.EnvRef);
                FinishCall(L, baseIdx, 1, 0);
            }
            catch (LuaCallException)
            {
                // best effort: the environment stays pinned until the state is closed
            }

            luaL_unref(L, LUA_REGISTRYINDEX, state.EnvRef);
            state.EnvRef = LUA_NOREF;
        }
    }

    // ------------------------------------------------------------------ helpers

    private bool Ready => _L != 0 && Volatile.Read(ref _disposed) == 0;

    private long NextToken() => Interlocked.Increment(ref _nextToken);

    private static long HeapBytes(nint L)
        => ((long)lua_gc(L, LUA_GCCOUNT, 0) * 1024L) + lua_gc(L, LUA_GCCOUNTB, 0);

    private static int LoadBuffer(nint L, ReadOnlySpan<byte> code, string chunkName)
    {
        fixed (byte* p = code)
            return luaL_loadbufferx(L, p, (nuint)code.Length, chunkName, "t");
    }

    // The text of the value at idx (an error object). Numbers are converted in place; other types are described.
    private static string TextAt(nint L, int idx)
    {
        var type = lua_type(L, idx);
        if (type is LUA_TSTRING or LUA_TNUMBER)
            return LuaValues.ReadString(L, idx);

        var name = Marshal.PtrToStringUTF8((nint)lua_typename(L, type)) ?? "unknown";
        return $"(error object is a {name} value)";
    }

    // ---- protected calls of bootstrap functions from the main state (never inside a coroutine) ----

    // Pushes [traceback handler, function]; the caller then pushes the arguments and calls FinishCall.
    private int PrepareCall(nint L, int fnRef)
    {
        lua_checkstack(L, 12);
        var baseIdx = lua_gettop(L);
        lua_rawgeti(L, LUA_REGISTRYINDEX, _refTraceback);
        lua_rawgeti(L, LUA_REGISTRYINDEX, fnRef);
        return baseIdx;
    }

    // lua_pcall with the traceback handler; on success the handler is removed and results start at baseIdx + 1.
    private static void FinishCall(nint L, int baseIdx, int nargs, int nresults)
    {
        var status = lua_pcall(L, nargs, nresults, baseIdx + 1);
        if (status != LUA_OK)
        {
            var message = TextAt(L, -1);
            lua_settop(L, baseIdx);
            throw new LuaCallException(message);
        }

        lua_remove(L, baseIdx + 1);
    }

    private sealed class LuaCallException(string message) : Exception(message);
}
