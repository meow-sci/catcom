using System.Diagnostics;
using System.Text;
using Catcom.Core.Hosting;
using Catcom.Core.Logging;
using Catcom.Core.Runtime;
using Catcom.Core.Values;
using Catcom.Runtime.Lua.Interop;
using static Catcom.Runtime.Lua.Native.LuaNative;

namespace Catcom.Runtime.Lua;

// Load / Invoke / Advance: every script entry runs inside a coroutine started with lua_resume.
public sealed unsafe partial class LuaRuntime
{
    private enum ThreadOutcome
    {
        Finished,
        Yielded,
        Failed,
    }

    private readonly record struct ResumeResult(ThreadOutcome Outcome, HostWait? Wait, ScriptRuntimeException? Error);

    // ------------------------------------------------------------------ Load

    /// <inheritdoc />
    public ScriptModule Load(ScriptSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_hostBound)
            throw new InvalidOperationException("The Lua runtime has not been initialized.");
        if (!Ready)
            throw new ScriptLoadException(_unavailableReason ?? "The Lua runtime is unavailable.", source.Path);

        DrainPendingUnrefs();

        var host = Host;
        var chunkName = "scripts/" + ChunkRelativePath(source);
        _chunkPaths[chunkName] = source.Path;

        var module = new ModuleState(NextToken(), source.Name, source.Path);
        var L = _L;
        using var scope = host.EnterScript(source.Name);

        BeginCall();
        var outermost = _depth == 1;
        var previousModule = _currentModule;
        _currentModule = module;
        var top = lua_gettop(L);
        var succeeded = false;
        try
        {
            LoadCore(source, module, chunkName);
            _modules[module.Token] = module;
            succeeded = true;
            return new ScriptModule(Id, source.Name, module.Token);
        }
        finally
        {
            _currentModule = previousModule;
            lua_settop(L, top);
            if (!succeeded)
                ReleaseModuleResources(module);
            if (outermost)
                PruneUnclaimedWaits();
            EndCall();
        }
    }

    private void LoadCore(ScriptSource source, ModuleState module, string chunkName)
    {
        var L = _L;

        // 1. The sandbox environment.
        try
        {
            var baseIdx = PrepareCall(L, _refNewEnv);
            LuaValues.PushUtf8(L, source.Name);
            LuaValues.PushUtf8(L, chunkName);
            FinishCall(L, baseIdx, 2, 1);
            module.EnvRef = luaL_ref(L, LUA_REGISTRYINDEX);
        }
        catch (LuaCallException ex)
        {
            throw new ScriptLoadException($"failed to create the script environment: {ex.Message}", source.Path);
        }

        // 2. The prelude (builds env.ksa).
        try
        {
            var baseIdx = PrepareCall(L, _refRunPrelude);
            lua_rawgeti(L, LUA_REGISTRYINDEX, module.EnvRef);
            LuaValues.PushBytes(L, _preludeBytes);
            FinishCall(L, baseIdx, 2, 1);
            lua_settop(L, baseIdx);
        }
        catch (LuaCallException ex)
        {
            throw new ScriptLoadException($"the ksa prelude failed to load: {ex.Message}", source.Path);
        }

        // 3. Compile (text chunks only: binary chunks are refused by mode "t").
        var code = Encoding.UTF8.GetBytes(source.Code);
        var status = LoadBuffer(L, code, "@" + chunkName);
        if (status != LUA_OK)
        {
            var message = TextAt(L, -1);
            lua_settop(L, -2);
            var info = LuaErrorInfo.Parse(message, ScriptsDir, _chunkPaths);
            var line = info?.Line ?? 0;
            throw new ScriptLoadException(message, source.Path, line, 0, SnippetFor(source.Code, line));
        }

        lua_rawgeti(L, LUA_REGISTRYINDEX, module.EnvRef);
        lua_setfenv(L, -2);

        // 4. Run the top-level code through the same coroutine + R.invoke path as Invoke.
        var thread = NewThread(L, out var threadRef);
        try
        {
            lua_checkstack(thread, 8);
            lua_rawgeti(thread, LUA_REGISTRYINDEX, _refInvoke);
            lua_xmove(L, thread, 1); // the compiled chunk becomes the argument of R.invoke

            var result = ResumeThread(thread, 1);
            switch (result.Outcome)
            {
                case ThreadOutcome.Yielded:
                    result.Wait?.Cancel();
                    throw new ScriptLoadException("top-level code cannot wait; move waits into main(ctx) or a hook", source.Path, 0);

                case ThreadOutcome.Failed:
                {
                    var error = result.Error!;
                    var file = error.File ?? source.Path;
                    var line = error.Line ?? 0;
                    var snippet = string.Equals(file, source.Path, StringComparison.Ordinal) ? SnippetFor(source.Code, line) : string.Empty;
                    throw new ScriptLoadException(error.Message, file, line, 0, snippet, error);
                }
            }

            CollectExports(L, thread, module);
        }
        finally
        {
            luaL_unref(L, LUA_REGISTRYINDEX, threadRef);
        }
    }

    // R.collect_exports(env, returned): fills module.ExportRefs from the resulting name -> function table.
    private void CollectExports(nint L, nint thread, ModuleState module)
    {
        try
        {
            var baseIdx = PrepareCall(L, _refCollectExports);
            lua_rawgeti(L, LUA_REGISTRYINDEX, module.EnvRef);
            if (lua_gettop(thread) >= 2)
            {
                lua_pushvalue(thread, 2);
                lua_xmove(thread, L, 1);
            }
            else
            {
                lua_pushnil(L);
            }

            FinishCall(L, baseIdx, 2, 1);

            var table = lua_gettop(L);
            lua_pushnil(L);
            while (lua_next(L, table) != 0)
            {
                // key at -2 (a string produced by collect_exports), value at -1 (a function)
                var name = LuaValues.ReadString(L, lua_gettop(L) - 1);
                lua_pushvalue(L, -1);
                module.ExportRefs[name] = luaL_ref(L, LUA_REGISTRYINDEX);
                lua_settop(L, -2);
            }

            lua_settop(L, baseIdx);
        }
        catch (LuaCallException ex)
        {
            throw new ScriptLoadException($"failed to collect exports: {ex.Message}", module.Path);
        }
    }

    private string ChunkRelativePath(ScriptSource source)
    {
        var dir = _options?.ScriptsDir;
        if (!string.IsNullOrEmpty(source.Path))
        {
            try
            {
                var full = Path.GetFullPath(source.Path);
                if (!string.IsNullOrEmpty(dir))
                {
                    var relative = Path.GetRelativePath(Path.GetFullPath(dir), full);
                    var outside = relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                        || Path.IsPathRooted(relative);
                    if (!outside)
                        return relative.Replace('\\', '/');
                }

                return Path.GetFileName(full);
            }
            catch (ArgumentException)
            {
                // fall through to the name-based chunk name
            }
        }

        return source.Name.Replace('\\', '/') + ".lua";
    }

    private static string SnippetFor(string code, int line)
    {
        if (line <= 0)
            return string.Empty;

        var current = 1;
        var start = 0;
        while (current < line)
        {
            var next = code.IndexOf('\n', start);
            if (next < 0)
                return string.Empty;
            start = next + 1;
            current++;
        }

        var end = code.IndexOf('\n', start);
        var text = end < 0 ? code[start..] : code[start..end];
        return text.Trim();
    }

    // ------------------------------------------------------------------ Invoke

    /// <inheritdoc />
    public ScriptValue Invoke(ScriptCallable fn, ReadOnlySpan<ScriptValue> args)
    {
        ArgumentNullException.ThrowIfNull(fn);
        if (!Ready)
        {
            throw new ScriptRuntimeException(
                !_hostBound ? "The Lua runtime has not been initialized."
                : Volatile.Read(ref _disposed) != 0 ? "The Lua runtime has been disposed."
                : _unavailableReason ?? "The Lua runtime is unavailable.");
        }

        DrainPendingUnrefs();
        var entry = ValidateCallable(fn);
        _modules.TryGetValue(entry.OwnerToken, out var module);

        var L = _L;
        var started = Stopwatch.GetTimestamp();
        BeginCall();
        var outermost = _depth == 1;
        var previousModule = _currentModule;
        _currentModule = module;
        var top = lua_gettop(L);
        var result = ScriptValue.Null;
        ScriptRuntimeException? failure = null;
        var threadRef = LUA_NOREF;
        try
        {
            var thread = NewThread(L, out threadRef);
            var nargs = PushInvocation(thread, entry.Ref, args);
            var resumed = ResumeThread(thread, nargs);
            switch (resumed.Outcome)
            {
                case ThreadOutcome.Finished:
                    result = lua_gettop(thread) >= 2 ? Values.ToScriptValue(thread, 2) : ScriptValue.Null;
                    break;
                case ThreadOutcome.Yielded:
                    // A module unloaded while its own call ran (re-entrantly) leaves nothing to resume later.
                    if (IsLive(module))
                    {
                        _parked.Add(new ParkedCoroutine(thread, threadRef, resumed.Wait!, module, module?.Name ?? string.Empty));
                        threadRef = LUA_NOREF; // now owned by the parked entry
                    }

                    break;
                default:
                    failure = resumed.Error;
                    break;
            }
        }
        finally
        {
            if (threadRef != LUA_NOREF)
                luaL_unref(L, LUA_REGISTRYINDEX, threadRef);
            _currentModule = previousModule;
            lua_settop(L, top);
            if (outermost)
                PruneUnclaimedWaits();
            EndCall();
        }

        string? memoryError = null;
        if (outermost)
        {
            Volatile.Write(ref _lastCallMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Interlocked.Increment(ref _callsTotal);
            memoryError = CheckMemory();
        }

        if (failure is not null)
            throw failure;
        if (memoryError is not null)
            throw new ScriptRuntimeException(memoryError);
        return result;
    }

    // A coroutine without an owner module can always be parked; one whose module was unloaded cannot.
    private bool IsLive(ModuleState? owner) => owner is null || _modules.ContainsKey(owner.Token);

    private CallableEntry ValidateCallable(ScriptCallable fn)
    {
        if (!string.Equals(fn.RuntimeId, Id, StringComparison.Ordinal))
            throw new ScriptRuntimeException($"function {fn} does not belong to the Lua runtime");
        if (fn.IsDisposed)
            throw new ScriptRuntimeException($"function {fn} has been disposed");
        if (!_callables.TryGetValue(fn.Token, out var entry))
            throw new ScriptRuntimeException($"function {fn} is no longer registered (its module was unloaded)");
        return entry;
    }

    // Pushes [R.invoke, fn, args...] onto a fresh coroutine and returns the lua_resume argument count.
    private int PushInvocation(nint thread, int fnRef, ReadOnlySpan<ScriptValue> args)
    {
        if (lua_checkstack(thread, args.Length + 8) == 0)
            throw new ScriptRuntimeException("too many arguments");

        lua_rawgeti(thread, LUA_REGISTRYINDEX, _refInvoke);
        lua_rawgeti(thread, LUA_REGISTRYINDEX, fnRef);
        for (var i = 0; i < args.Length; i++)
            Values.Push(thread, args[i]);
        return 1 + args.Length;
    }

    private static nint NewThread(nint L, out int threadRef)
    {
        var thread = lua_newthread(L);
        threadRef = luaL_ref(L, LUA_REGISTRYINDEX); // pops the thread from L and anchors it
        return thread;
    }

    // After the outermost call: collect when over the soft heap limit; returns the error text if still over.
    private string? CheckMemory()
    {
        var limit = _options?.HeapLimitBytes ?? 0;
        if (limit <= 0 || !Ready)
            return null;

        var L = _L;
        if (HeapBytes(L) <= limit)
            return null;

        lua_gc(L, LUA_GCCOLLECT, 0);
        var heap = HeapBytes(L);
        return heap <= limit ? null : $"memory limit exceeded ({heap / 1024} KiB > {limit / 1024} KiB)";
    }

    // ------------------------------------------------------------------ Advance

    /// <inheritdoc />
    public bool Advance(TimeSpan budget)
    {
        if (!Ready || _host is null)
            return false;

        DrainPendingUnrefs();
        if (!_parked.Exists(p => p.Wait.IsCompleted))
            return false;

        var started = Stopwatch.GetTimestamp();
        BeginCall();
        try
        {
            // Each parked coroutine is resumed at most once per Advance, in park (FIFO) order; coroutines
            // that park again wait for the next call. At least one completed continuation always runs.
            var candidates = _parked.ToArray();
            var ran = false;
            foreach (var parked in candidates)
            {
                if (!parked.Wait.IsCompleted || !_parked.Contains(parked))
                    continue;
                if (ran && (Stopwatch.GetElapsedTime(started) >= budget || IsInterruptRequested))
                    break;

                ResumeParked(parked);
                ran = true;
            }
        }
        finally
        {
            PruneUnclaimedWaits();
            EndCall();
        }

        var memoryError = CheckMemory();
        if (memoryError is not null)
            ReportAsyncError(new ScriptRuntimeException(memoryError));

        return _parked.Exists(p => p.Wait.IsCompleted);
    }

    private void ResumeParked(ParkedCoroutine parked)
    {
        _parked.Remove(parked);

        var L = _L;
        var thread = parked.Thread;
        var keepReference = false;
        using var scope = Host.EnterScript(parked.ScriptName);
        var previousModule = _currentModule;
        _currentModule = parked.Owner;
        try
        {
            lua_settop(thread, 0); // drop the yielded wait table
            lua_checkstack(thread, 8);

            int nargs;
            var task = parked.Wait.Task;
            if (task.IsCompletedSuccessfully)
            {
                Values.Push(thread, task.Result);
                nargs = 1;
            }
            else
            {
                lua_pushnil(thread);
                LuaValues.PushUtf8(thread, WaitFailureMessage(task));
                nargs = 2;
            }

            var resumed = ResumeThread(thread, nargs);
            switch (resumed.Outcome)
            {
                case ThreadOutcome.Yielded:
                    if (IsLive(parked.Owner))
                    {
                        _parked.Add(new ParkedCoroutine(thread, parked.ThreadRef, resumed.Wait!, parked.Owner, parked.ScriptName));
                        keepReference = true;
                    }

                    break;
                case ThreadOutcome.Failed:
                    ReportAsyncError(resumed.Error!);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ReportAsyncError(new ScriptRuntimeException($"internal error while resuming a coroutine: {ex.Message}", string.Empty, null, null, ex));
        }
        finally
        {
            if (!keepReference)
                luaL_unref(L, LUA_REGISTRYINDEX, parked.ThreadRef);
            _currentModule = previousModule;
        }
    }

    private static string WaitFailureMessage(Task task)
    {
        if (task.IsCanceled)
            return "wait cancelled";
        return task.Exception?.GetBaseException().Message ?? "wait failed";
    }

    private void ReportAsyncError(ScriptRuntimeException error)
    {
        try
        {
            Host.ReportError(error);
        }
        catch (Exception ex)
        {
            Log.Error($"lua: the host failed to record a script error ({error.Message})", ex);
        }
    }

    // ------------------------------------------------------------------ resume machinery

    // Resumes a coroutine and classifies the outcome. On Finished the coroutine's stack holds (true, results...).
    private ResumeResult ResumeThread(nint thread, int nargs)
    {
        var status = lua_resume(thread, nargs);
        switch (status)
        {
            case LUA_OK:
            {
                if (lua_gettop(thread) >= 1 && lua_type(thread, 1) == LUA_TBOOLEAN && lua_toboolean(thread, 1) != 0)
                    return new ResumeResult(ThreadOutcome.Finished, null, null);

                var text = lua_gettop(thread) >= 2 ? TextAt(thread, 2) : "script failed";
                return Failed(MakeRuntimeException(text));
            }

            case LUA_YIELD:
            {
                if (TryTakeYieldedWait(thread, out var wait))
                    return new ResumeResult(ThreadOutcome.Yielded, wait, null);

                return Failed(new ScriptRuntimeException(
                    "coroutine.yield is only allowed for ksa waits (use ksa.sleep / ksa.next_frame …)"));
            }

            default:
                // LUA_ERRRUN / ERRMEM / ERRERR outside the xpcall in R.invoke (e.g. the timeout hook firing there).
                return Failed(MakeRuntimeException(TextAt(thread, -1)));
        }
    }

    private ResumeResult Failed(ScriptRuntimeException error)
    {
        if (IsInterruptRequested || error.Message.Contains("script timed out", StringComparison.Ordinal))
            error = new ScriptRuntimeException("interrupted");
        return new ResumeResult(ThreadOutcome.Failed, null, error);
    }

    private ScriptRuntimeException MakeRuntimeException(string text)
    {
        var message = LuaErrorInfo.MessageOf(text);
        var info = LuaErrorInfo.Parse(message, ScriptsDir, _chunkPaths) ?? LuaErrorInfo.Parse(text, ScriptsDir, _chunkPaths);
        return new ScriptRuntimeException(message, text, info?.File, info?.Line);
    }

    // The coroutine yielded values: the first must be a table carrying a numeric "$wait" naming a registered HostWait.
    private bool TryTakeYieldedWait(nint thread, out HostWait? wait)
    {
        wait = null;
        if (lua_gettop(thread) < 1 || lua_type(thread, 1) != LUA_TTABLE)
            return false;

        lua_checkstack(thread, 4);
        LuaValues.PushUtf8(thread, "$wait");
        lua_rawget(thread, 1);
        var isNumber = lua_type(thread, -1) == LUA_TNUMBER;
        var id = isNumber ? (long)lua_tonumber(thread, -1) : 0;
        lua_settop(thread, -2);
        return isNumber && _waits.Remove(id, out wait);
    }
}
