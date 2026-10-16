using System.Reflection;
using System.Runtime.InteropServices;
using KsaScripting.Runtime.Lua.Native;

namespace KsaScripting.Runtime.Lua.Tests;

[TestFixture]
public sealed class NativeLoaderTests
{
    [Test]
    public void TryEnsureLoaded_IsIdempotent_AndFindsTheShim()
    {
        Assert.That(LuaNativeLoader.TryEnsureLoaded(out var reason), Is.True, reason);
        Assert.That(reason, Is.Null);
        Assert.That(LuaNativeLoader.TryEnsureLoaded(out reason), Is.True);
        Assert.That(LuaNativeLoader.LoadedPath, Is.Not.Null.And.Matches<string>(File.Exists));

        Assert.That(LuaNativeLoader.GetShimExport("host_traceback"), Is.Not.EqualTo(nint.Zero));
        Assert.That(LuaNativeLoader.GetShimExport("host_raise"), Is.Not.EqualTo(nint.Zero));
        Assert.That(LuaNativeLoader.GetShimExport("host_timeout_hook"), Is.Not.EqualTo(nint.Zero));
    }

    [Test]
    public void TryEnsureLoaded_IsThreadSafe()
    {
        var results = Enumerable.Range(0, 8).Select(_ => Task.Run(() => LuaNativeLoader.TryEnsureLoaded(out string? _))).ToArray();

        Assert.That(Task.WhenAll(results).Wait(TimeSpan.FromSeconds(10)), Is.True);
        Assert.That(results.All(t => t.Result), Is.True);
    }

    [Test]
    public void NativeLibrary_IsLoadedFromTheRuntimesFolderNextToTheAssembly()
    {
        LuaNativeLoader.TryEnsureLoaded(out _);
        var path = LuaNativeLoader.LoadedPath!;
        var assemblyDir = Path.GetDirectoryName(typeof(LuaRuntime).Assembly.Location)!;

        Assert.That(path, Does.StartWith(assemblyDir).Or.EqualTo(Environment.GetEnvironmentVariable(LuaNativeLoader.OverrideEnvironmentVariable)));
        Assert.That(path, Does.Contain(Path.Combine("runtimes", "")));
    }

    [Test]
    public void Candidates_ListTheRidFolder_TheOsArchFolder_AndTheAssemblyFolder()
    {
        var candidates = LuaNativeLoader.Candidates().ToArray();
        var assemblyDir = Path.GetDirectoryName(typeof(LuaRuntime).Assembly.Location)!;

        Assert.That(candidates, Has.Length.GreaterThanOrEqualTo(2));
        Assert.That(candidates.All(Path.IsPathRooted), Is.True, "libraries are loaded by absolute path");
        Assert.That(candidates.Last(), Is.EqualTo(Path.Combine(assemblyDir, Path.GetFileName(candidates.Last()))));
        Assert.That(candidates.Any(c => c.Contains(Path.Combine("runtimes", "osx-arm64", "native")) || c.Contains(Path.Combine("runtimes", "linux-x64", "native")) || c.Contains(Path.Combine("runtimes", "win-x64", "native")) || c.Contains(Path.Combine("runtimes", RuntimeInformation.RuntimeIdentifier, "native"))), Is.True);
    }

    [Test]
    public void TryLoadFrom_ReportsTheProbedPaths_WhenNothingExists()
    {
        var result = LuaNativeLoader.TryLoadFrom([Path.Combine(Path.GetTempPath(), "no-such-dir", "lua51.dll"), "/also/missing/libluajit.so"]);

        Assert.That(result.Handle, Is.EqualTo(nint.Zero));
        Assert.That(result.Reason, Does.StartWith("LuaJIT native library not found. Probed: "));
        Assert.That(result.Reason, Does.Contain("no-such-dir").And.Contain("/also/missing/libluajit.so"));
    }

    [Test]
    public void TryLoadFrom_ListsASingleMissingPath_AndLoadsNothing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ksa-no-such-dir", Guid.NewGuid().ToString("N"), "libluajit-5.1.2.dylib");

        var result = LuaNativeLoader.TryLoadFrom([missing]);

        Assert.That(result.Handle, Is.EqualTo(nint.Zero));
        Assert.That(result.Path, Is.Null);
        Assert.That(result.Reason, Is.EqualTo($"LuaJIT native library not found. Probed: {missing}"));
    }

    [Test]
    public void TryLoadFrom_WithNoCandidates_ReportsNothingWasProbed()
    {
        var result = LuaNativeLoader.TryLoadFrom([]);

        Assert.That(result.Handle, Is.EqualTo(nint.Zero));
        Assert.That(result.Reason, Is.EqualTo("LuaJIT native library not found. Probed: "));
    }

    // A genuine LuaJIT that was built without tools/host_shim.c: it loads fine, but it lacks the ksa exports.
    private const string HomebrewLuaJit = "/opt/homebrew/lib/libluajit-5.1.dylib";

    [Test]
    public void TryLoadFrom_RejectsARealLuaJit_ThatLacksTheHostShim()
    {
        Assume.That(File.Exists(HomebrewLuaJit), $"{HomebrewLuaJit} is not installed (stock LuaJIT without the ksa shim)");

        var result = LuaNativeLoader.TryLoadFrom([HomebrewLuaJit]);

        Assert.That(result.Handle, Is.EqualTo(nint.Zero), "a library without the shim is never handed out");
        Assert.That(result.Path, Is.EqualTo(HomebrewLuaJit));
        Assert.That(result.Reason, Does.Contain("missing the ksa host shim").And.Contain("tools/build-luajit"));
    }

    [Test]
    public void TryLoadFrom_SkipsMissingCandidates_ThenRejectsTheFirstExistingOneWithoutTheShim()
    {
        Assume.That(File.Exists(HomebrewLuaJit), $"{HomebrewLuaJit} is not installed (stock LuaJIT without the ksa shim)");
        LuaNativeLoader.TryEnsureLoaded(out _);
        var missing = Path.Combine(Path.GetTempPath(), "ksa-no-such-dir", "lua51.dll");

        // First existing candidate wins: the shipped library behind it is not consulted.
        var result = LuaNativeLoader.TryLoadFrom([missing, HomebrewLuaJit, LuaNativeLoader.LoadedPath!]);

        Assert.That(result.Handle, Is.EqualTo(nint.Zero));
        Assert.That(result.Path, Is.EqualTo(HomebrewLuaJit));
        Assert.That(result.Reason, Does.Contain("missing the ksa host shim"));
    }

    [Test]
    public void TheRuntime_ReportsTheProbeFailure_WhenNoLibraryIsUsable()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ksa-no-such-dir", "lua51.dll");
        var reason = LuaNativeLoader.TryLoadFrom([missing]).Reason;
        using var runtime = new LuaRuntime(() => reason);

        Assert.That(runtime.IsAvailable, Is.False);
        Assert.That(runtime.UnavailableReason, Does.Contain(missing));
    }

    [Test]
    public void TryLoadFrom_ReportsFilesThatCannotBeLoaded()
    {
        var notNative = typeof(LuaRuntime).Assembly.Location; // a managed assembly is not a native library

        var result = LuaNativeLoader.TryLoadFrom([notNative]);

        Assert.That(result.Handle, Is.EqualTo(nint.Zero));
        Assert.That(result.Reason, Does.Contain("could not be loaded").And.Contain(notNative));
    }

    [Test]
    public void TryLoadFrom_AcceptsTheShippedLibrary()
    {
        LuaNativeLoader.TryEnsureLoaded(out _);

        var result = LuaNativeLoader.TryLoadFrom([LuaNativeLoader.LoadedPath!]);

        Assert.That(result.Reason, Is.Null);
        Assert.That(result.Handle, Is.Not.EqualTo(nint.Zero));
    }

    [Test]
    public void EveryImportedFunction_IsExportedByTheLibrary()
    {
        LuaNativeLoader.TryEnsureLoaded(out _);
        var library = NativeLibrary.Load(LuaNativeLoader.LoadedPath!);
        var names = typeof(LuaNative)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => n.StartsWith("lua", StringComparison.Ordinal) || n.StartsWith("host_", StringComparison.Ordinal))
            .Where(n => n != nameof(LuaNative.lua_upvalueindex))
            .Distinct()
            .ToArray();

        Assert.That(names, Has.Length.GreaterThan(55));
        foreach (var name in names)
            Assert.That(NativeLibrary.TryGetExport(library, name, out _), Is.True, name);
    }

    [Test]
    public unsafe void RarelyUsedBindings_WorkOnAPlainState()
    {
        LuaNativeLoader.TryEnsureLoaded(out _);
        var L = LuaNative.luaL_newstate();
        Assert.That(L, Is.Not.EqualTo(nint.Zero));
        try
        {
            LuaNative.luaL_openlibs(L);

            // stack manipulation: insert / replace / remove / pushvalue / xmove / rawequal
            LuaNative.lua_pushinteger(L, 1);
            LuaNative.lua_pushinteger(L, 2);
            LuaNative.lua_pushinteger(L, 3);
            LuaNative.lua_insert(L, 1);                  // 3 1 2
            Assert.That(LuaNative.lua_tointeger(L, 1), Is.EqualTo((nint)3));
            LuaNative.lua_pushnumber(L, 9.5);
            LuaNative.lua_replace(L, 2);                 // 3 9.5 2
            Assert.That(LuaNative.lua_tonumber(L, 2), Is.EqualTo(9.5));
            LuaNative.lua_remove(L, 1);                  // 9.5 2
            Assert.That(LuaNative.lua_gettop(L), Is.EqualTo(2));
            LuaNative.lua_pushvalue(L, 1);
            Assert.That(LuaNative.lua_rawequal(L, 1, 3), Is.EqualTo(1));
            Assert.That(LuaNative.lua_checkstack(L, 100), Is.Not.EqualTo(0));
            LuaNative.lua_settop(L, 0);

            // threads: newthread / xmove / status / tothread / type / typename
            var thread = LuaNative.lua_newthread(L);
            Assert.That(LuaNative.lua_type(L, -1), Is.EqualTo(LuaNative.LUA_TTHREAD));
            Assert.That(LuaNative.lua_tothread(L, -1), Is.EqualTo(thread));
            Assert.That(Marshal.PtrToStringUTF8((nint)LuaNative.lua_typename(L, LuaNative.LUA_TTHREAD)), Is.EqualTo("thread"));
            Assert.That(LuaNative.lua_status(thread), Is.EqualTo(LuaNative.LUA_OK));
            LuaNative.lua_pushboolean(L, 1);
            LuaNative.lua_xmove(L, thread, 1);
            Assert.That(LuaNative.lua_toboolean(thread, -1), Is.EqualTo(1));
            LuaNative.lua_settop(L, 0);

            // tables, metatables, userdata, environments, refs
            LuaNative.lua_createtable(L, 0, 0);
            fixed (byte* name = "ksa.test\0"u8)
                Assert.That(LuaNative.luaL_newmetatable(L, name), Is.EqualTo(1));
            Assert.That(LuaNative.lua_setmetatable(L, 1), Is.Not.EqualTo(0));
            Assert.That(LuaNative.lua_getmetatable(L, 1), Is.EqualTo(1));
            LuaNative.lua_settop(L, 0);

            var p = LuaNative.lua_newuserdata(L, 16);
            Assert.That(p == null, Is.False);
            Assert.That(LuaNative.lua_type(L, -1), Is.EqualTo(LuaNative.LUA_TUSERDATA));
            Assert.That(LuaNative.lua_objlen(L, -1), Is.EqualTo((nuint)16));
            LuaNative.lua_settop(L, 0);

            LuaNative.lua_pushstring(L, "value");
            var reference = LuaNative.luaL_ref(L, LuaNative.LUA_REGISTRYINDEX);
            LuaNative.lua_rawgeti(L, LuaNative.LUA_REGISTRYINDEX, reference);
            Assert.That(LuaValuesText(L, -1), Is.EqualTo("value"));
            LuaNative.luaL_unref(L, LuaNative.LUA_REGISTRYINDEX, reference);
            LuaNative.lua_settop(L, 0);

            LuaNative.lua_pushcclosure(L, LuaNative.HostRaiseAddress, 0);
            LuaNative.lua_getfenv(L, -1);
            Assert.That(LuaNative.lua_type(L, -1), Is.EqualTo(LuaNative.LUA_TTABLE));
            LuaNative.lua_settop(L, 0);

            // host_traceback as a message handler, host_raise as a protected raiser, luaL_traceback
            LuaNative.lua_pushcclosure(L, LuaNative.HostTracebackAddress, 0);
            LuaNative.lua_pushcclosure(L, LuaNative.HostRaiseAddress, 0);
            LuaNative.lua_pushstring(L, "boom");
            Assert.That(LuaNative.lua_pcall(L, 1, 0, 1), Is.EqualTo(LuaNative.LUA_ERRRUN));
            Assert.That(LuaValuesText(L, -1), Does.StartWith("boom").And.Contain("stack traceback"));
            LuaNative.lua_settop(L, 0);

            // GC accounting, JIT mode switch
            Assert.That(LuaNative.lua_gc(L, LuaNative.LUA_GCCOUNT, 0), Is.GreaterThan(0));
            Assert.That(LuaNative.lua_gc(L, LuaNative.LUA_GCCOLLECT, 0), Is.EqualTo(0));
            Assert.That(LuaNative.luaJIT_setmode(L, LuaNative.LUAJIT_MODE_ENGINE, LuaNative.LUAJIT_MODE_ENGINE | LuaNative.LUAJIT_MODE_OFF), Is.Not.EqualTo(-1));
            Assert.That(LuaNative.luaJIT_setmode(L, LuaNative.LUAJIT_MODE_ENGINE, LuaNative.LUAJIT_MODE_ENGINE | LuaNative.LUAJIT_MODE_ON), Is.Not.EqualTo(-1));
        }
        finally
        {
            LuaNative.lua_close(L);
        }
    }

    private static unsafe string LuaValuesText(nint L, int index)
    {
        nuint len;
        var p = LuaNative.lua_tolstring(L, index, &len);
        return System.Text.Encoding.UTF8.GetString(p, (int)len);
    }
}
