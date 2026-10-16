// LuaJitSpike: direct P/Invoke binding shape for LuaJIT 2.1 (Lua 5.1 C API) from .NET 10.
// Validates: resolver-based native loading, state creation, running code, host functions via
// UnmanagedCallersOnly, error handling with traceback, coroutines driven from C#, sandboxing, hooks, timing.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

unsafe class Program
{
    const string Lib = "luajit";
    const int LUA_GLOBALSINDEX = -10002;
    const int LUA_MULTRET = -1;
    const int LUA_YIELD = 1;
    const int LUA_MASKCOUNT = 8;

    [DllImport(Lib)] static extern nint luaL_newstate();
    [DllImport(Lib)] static extern void luaL_openlibs(nint L);
    [DllImport(Lib)] static extern void lua_close(nint L);
    [DllImport(Lib)] static extern int luaL_loadbuffer(nint L, byte* buff, nuint sz, byte* name);
    [DllImport(Lib)] static extern int lua_pcall(nint L, int nargs, int nresults, int errfunc);
    [DllImport(Lib)] static extern byte* lua_tolstring(nint L, int idx, nuint* len);
    [DllImport(Lib)] static extern double lua_tonumber(nint L, int idx);
    [DllImport(Lib)] static extern void lua_pushnumber(nint L, double n);
    [DllImport(Lib)] static extern void lua_pushnil(nint L);
    [DllImport(Lib)] static extern void lua_pushcclosure(nint L, delegate* unmanaged[Cdecl]<nint, int> f, int n);
    [DllImport(Lib)] static extern void lua_setfield(nint L, int idx, byte* k);
    [DllImport(Lib)] static extern void lua_getfield(nint L, int idx, byte* k);
    [DllImport(Lib)] static extern int lua_gettop(nint L);
    [DllImport(Lib)] static extern void lua_settop(nint L, int idx);
    [DllImport(Lib)] static extern int lua_type(nint L, int idx);
    [DllImport(Lib)] static extern nint lua_newthread(nint L);
    [DllImport(Lib)] static extern int lua_resume(nint L, int narg);
    [DllImport(Lib)] static extern int lua_yield(nint L, int nresults);
    [DllImport(Lib)] static extern int lua_status(nint L);
    [DllImport(Lib)] static extern void lua_sethook(nint L, delegate* unmanaged[Cdecl]<nint, nint, void> f, int mask, int count);
    [DllImport(Lib)] static extern int lua_error(nint L);
    [DllImport(Lib)] static extern void lua_pushstring(nint L, byte* s);
    [DllImport(Lib)] static extern int lua_gc(nint L, int what, int data);

    static byte* Z(string s) { var b = Encoding.UTF8.GetBytes(s + "\0"); var p = (byte*)NativeMemory.Alloc((nuint)b.Length); b.AsSpan().CopyTo(new Span<byte>(p, b.Length)); return p; }
    static string Str(nint L, int idx) { nuint len; var p = lua_tolstring(L, idx, &len); return p == null ? "<nil>" : Encoding.UTF8.GetString(p, (int)len); }
    static int Load(nint L, string code, string name) { var b = Encoding.UTF8.GetBytes(code); fixed (byte* p = b) return luaL_loadbuffer(L, p, (nuint)b.Length, Z(name)); }
    static int DoString(nint L, string code, string name = "chunk")
    {
        var top = lua_gettop(L);
        lua_getfield(L, LUA_GLOBALSINDEX, Z("__traceback")); // error handler (debug.traceback)
        var st = Load(L, code, name);
        if (st != 0) { Console.WriteLine($"load error: {Str(L, -1)}"); lua_settop(L, top); return st; }
        st = lua_pcall(L, 0, LUA_MULTRET, top + 1);
        if (st != 0) { Console.WriteLine($"runtime error ({st}): {Str(L, -1)}"); }
        return st;
    }

    static long hostCalls;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int Host_Add(nint L) { var a = lua_tonumber(L, 1); var b = lua_tonumber(L, 2); hostCalls++; lua_pushnumber(L, a + b); return 1; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int Host_Print(nint L) { Console.WriteLine("[lua] " + Str(L, 1)); return 0; }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int Host_Await(nint L) { return lua_yield(L, lua_gettop(L)); } // yield everything to the host

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int Host_ShouldInterrupt(nint L) { hookTicks++; lua_pushnumber(L, interrupt ? 1 : 0); return 1; }

    static long hookTicks; static volatile bool interrupt; static bool logged;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void CountHook(nint L, nint ar) { hookTicks++; if (interrupt) { lua_pushstring(L, Z("script interrupted by host")); lua_error(L); } }

    static int Main()
    {
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return nint.Zero;
            foreach (var cand in new[] { "/opt/homebrew/lib/libluajit-5.1.dylib", "/opt/homebrew/lib/libluajit.dylib", "libluajit-5.1.so.2", "lua51.dll" })
                if (NativeLibrary.TryLoad(cand, out var h)) { if (!logged) { Console.WriteLine($"loaded {cand}"); logged = true; } return h; }
            return nint.Zero;
        });
        var sw = Stopwatch.StartNew();
        var L = luaL_newstate();
        luaL_openlibs(L);
        Console.WriteLine($"state+openlibs: {sw.Elapsed.TotalMilliseconds:F2} ms");
        DoString(L, "__traceback = debug.traceback; print(jit.version, jit.arch, jit.status())", "init");
        lua_pushcclosure(L, &Host_Add, 0); lua_setfield(L, LUA_GLOBALSINDEX, Z("host_add"));
        lua_pushcclosure(L, &Host_Print, 0); lua_setfield(L, LUA_GLOBALSINDEX, Z("host_print"));
        lua_pushcclosure(L, &Host_Await, 0); lua_setfield(L, LUA_GLOBALSINDEX, Z("host_await"));

        // sandbox: remove dangerous libs
        DoString(L, "os = nil; io = nil; package = nil; require = nil; loadfile = nil; dofile = nil; ffi = nil; print = host_print", "sandbox");
        DoString(L, "host_print('hello from LuaJIT; ffi is ' .. tostring(ffi) .. ', io is ' .. tostring(io))", "t1");
        DoString(L, "local x = nil; return x.y", "err-test"); // runtime error with traceback

        // Lua -> host calls throughput
        sw.Restart();
        DoString(L, "local s = 0; for i = 1, 1000000 do s = s + host_add(i, 1) end; return s", "bench-host");
        Console.WriteLine($"1M Lua->C# calls: {sw.ElapsedMilliseconds} ms (hostCalls={hostCalls})");
        // host -> Lua calls throughput
        DoString(L, "function f(a) return a * 2 end", "deff");
        sw.Restart();
        for (int i = 0; i < 1_000_000; i++)
        {
            lua_getfield(L, LUA_GLOBALSINDEX, Z("f")); lua_pushnumber(L, i); lua_pcall(L, 1, 1, 0); lua_settop(L, 0);
        }
        Console.WriteLine($"1M C#->Lua pcalls: {sw.ElapsedMilliseconds} ms");
        // pure Lua loop (JIT)
        sw.Restart();
        DoString(L, "local s=0; for i=1,50000000 do s = s + (i*2 % 7) end; return s", "loop");
        Console.WriteLine($"50M-iteration loop (JIT): {sw.ElapsedMilliseconds} ms");

        // coroutine driven from C#: script 'awaits' host by yielding
        var co = lua_newthread(L);
        Load(co, "local a = host_await('need-altitude'); host_print('got ' .. tostring(a)); local b = host_await('need-speed'); host_print('got ' .. tostring(b)); return a + b", "co");
        int st = lua_resume(co, 0);
        while (st == LUA_YIELD)
        {
            Console.WriteLine($"script yielded request: {Str(co, 1)}"); lua_settop(co, 0);
            lua_pushnumber(co, 42); st = lua_resume(co, 1); // host supplies the awaited value
        }
        Console.WriteLine($"coroutine finished status={st} result={lua_tonumber(co, -1)}");

        // Interrupt variant B: the hook is a LUA function (no managed frame on the unwind path); the host
        // only answers a non-throwing predicate. Expected to work where variant A (lua_error from inside an
        // UnmanagedCallersOnly hook) panics, because LuaJIT 2.1 on x64/arm64 unwinds errors with the
        // platform C++ unwinder (LJ_UNWIND_EXT) / SEH, which cannot cross .NET managed frames.
        lua_pushcclosure(L, &Host_ShouldInterrupt, 0); lua_setfield(L, LUA_GLOBALSINDEX, Z("host_should_interrupt"));
        DoString(L, "jit.off(); debug.sethook(function() if host_should_interrupt() == 1 then error('script interrupted by host', 2) end end, '', 1000)", "hook");
        interrupt = false;
        var timer = new Thread(() => { Thread.Sleep(100); interrupt = true; }); timer.Start();
        sw.Restart();
        DoString(L, "while true do end", "infinite");
        Console.WriteLine($"infinite loop interrupted after {sw.ElapsedMilliseconds} ms, predicate calls={hookTicks}");
        DoString(L, "debug.sethook()", "unhook");
        // JIT ON + count hook: does the hook still fire inside compiled traces?
        interrupt = false; hookTicks = 0;
        DoString(L, "jit.on(); debug.sethook(function() if host_should_interrupt() == 1 then error('script interrupted by host', 2) end end, '', 1000)", "hook2");
        var timer2 = new Thread(() => { Thread.Sleep(100); interrupt = true; }); timer2.Start();
        sw.Restart();
        var t2 = new Thread(() => DoString(L, "local i = 0; while true do i = i + 1 end", "infinite-jit")); t2.Start();
        if (!t2.Join(3000)) Console.WriteLine("JIT ON: hook did NOT fire within 3 s inside a compiled trace (expected LuaJIT behavior) - leaving thread running");
        else Console.WriteLine($"JIT ON: interrupted after {sw.ElapsedMilliseconds} ms, predicate calls={hookTicks}");
        Console.WriteLine($"lua memory: {lua_gc(L, 3, 0)} KiB"); // LUA_GCCOUNT=3
        Environment.Exit(0);
        return 0;
    }
}
