---
type: Research Report
title: Lua/LuaJIT embedding research report (2026-10-16)
description: Verbatim research report produced by a delegated research pass on 2026-10-16; the primary source the distilled concepts in this bundle cite. Claims inside are tagged verified/inference/measured by the author of the report.
tags: [reference, report, raw]
generated: { by: claude-fable-5.1/ksa-scripting-research-subagent, at: 2026-10-16T20:00:00Z }
status: stable
---

> Mirrored from the session scratchpad into the bundle so the cited source outlives the session. Scratchpad paths mentioned inside (e.g. harness locations) no longer exist.

# Embedding LuaJIT in a .NET 10 game mod (custom AssemblyLoadContext) — research report

*Date: 2026-10-16. Targets: Windows x64, Linux x64 (macOS arm64 nice-to-have). Host: a game mod loaded into a custom `AssemblyLoadContext`, .NET 10 / C# 13–14.*

**Legend used throughout**

- **[V]** = verified from the cited source/docs (quoted or paraphrased closely).
- **[I]** = my inference / recommendation; not stated by any source.
- **[M]** = my own measurement on this machine (Apple M4 Pro, macOS 27.0.1, Apple clang 21, .NET 10.0.0, LuaJIT v2.1 commit `2a8e017` + Homebrew bottle 2.1.1788856981). Harness and raw logs are listed in Appendix A. Treat as order-of-magnitude, single-machine Stopwatch numbers, not BenchmarkDotNet results.

Note on "C# 13": .NET 10 shipped 2025-11-11 as an LTS release together with **C# 14** ([Microsoft .NET blog](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/)), so a net10.0 project defaults to C# 14; everything below also works with `LangVersion` 13 **[V]/[I]**.

---

## 0. Executive summary and recommendation

1. **LuaJIT is alive and current.** The `v2.1` branch received commits on 2026-10-13/14 (Mike Pall) and the project copyright reads 2005-2026 ([GitHub commits](https://github.com/LuaJIT/LuaJIT/commits/v2.1), [repo](https://github.com/LuaJIT/LuaJIT)). It is "actively developed and maintained", uses rolling releases, ships **no binaries or tarballs**, and the maintainer explicitly tells you not to use third-party binaries ([status](https://luajit.org/status.html), [download](https://luajit.org/download.html)) **[V]**. You will build `lua51.dll` / `libluajit-5.1.so.2` / `libluajit-5.1.2.dylib` yourself (Section 1.10).
2. **No existing .NET binding is a good fit for LuaJIT.** KeraLua/NLua bind Lua 5.4 and import ~24 functions that do not exist in a Lua-5.1-ABI library (`lua_pcallk`, `lua_rotate`, 4-arg `lua_resume`, …), so they cannot simply be pointed at `lua51.dll` ([KeraLua NativeMethods.cs](https://github.com/NLua/KeraLua/blob/main/src/NativeMethods.cs)) **[V]**. The LuaJIT-capable packages (Lua.NET 6.0.1, JAJ.Packages.LuaNET 1.1.0, NuLua.LuaJit 0.1.0-preview) are small, DllImport+delegate based, and either stale or explicitly preview (Section 2) **[V]**. Pure-C# interpreters (Lua-CSharp 0.5.7, MoonSharp 2.0/3.0-beta, NeoLua) are Lua 5.2/5.3 and 10–100× slower than LuaJIT on Lua code (Section 6).
3. **Recommendation [I]:** hand-written `[LibraryImport]` bindings to the ~60 Lua 5.1 C-API functions you actually need, `delegate* unmanaged[Cdecl]` + `[UnmanagedCallersOnly]` for `lua_CFunction`, `NativeLibrary.SetDllImportResolver` (or fully manual `NativeLibrary.Load` + `GetExport`) to load the per-RID library from your mod folder, **plus one tiny native shim (≈30 lines of C, compiled into your LuaJIT build)** for the two operations that must unwind the Lua stack: the timeout hook and raising errors from host functions. The shim is needed because .NET does not support `longjmp`/SEH unwinding across managed frames ([exceptions-interoperability](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/exceptions-interoperability), [dotnet/runtime PR #111259](https://github.com/dotnet/runtime/pull/111259)) **[V]**.
4. **Measured on this machine [M]:** a `[LibraryImport]` call into LuaJIT costs ≈0.9 ns (identical to C), a Lua→C# `[UnmanagedCallersOnly]` callback ≈5–7 ns, creating a state with all libraries ≈21–25 µs, baseline heap ≈36 KB, library ≈0.5–0.6 MB. The expensive thing is UTF-8 string marshalling (`lua_getfield(L, idx, "name")` + `lua_pcall` ≈ 40 ns from .NET vs 15 ns from C), so cache registry refs instead of looking things up by name in hot paths.
5. **macOS arm64 warning [M]:** the Homebrew LuaJIT bottle (2.1.1788856981, built against SDK 26.5) **SIGKILLed the .NET 10 host process** the moment the JIT compiled its first trace; the *same commit built from source* on this machine ran with `jit.status()==true` inside .NET. Root cause not determined; the actionable lesson is: build LuaJIT yourself for every RID and test JIT-on inside the real host process, and keep a `jit.off()` kill switch. Also, `-DLUAJIT_ENABLE_OSX_HRT` (MAP_JIT support for hardened-runtime hosts) does **not compile** at commit `2a8e017` (`return 0;` in a `void` function, `lj_mcode.c:156`), a one-line fix.

---

## 1. LuaJIT itself (status as of 2026-10-16)

### 1.1 Maintenance and release model **[V]**

- "LuaJIT is actively developed and maintained." Branches: **v2.0** = bug fixes only; **v2.1** = "production branch", maintained with limited new features; a development branch is "(TBA)"; the old `master` is pinned to v2.0 and no longer maintained. "Any commit may break API, ABI and bytecode compatibility" applies only to development branches. ([luajit.org/status.html](https://luajit.org/status.html))
- Rolling releases: "there are no release tarballs or binaries"; the version's patch component is the POSIX timestamp of the latest commit ([status](https://luajit.org/status.html)). `luajit_rolling.h` defines `LUAJIT_VERSION "LuaJIT 2.1.ROLLING"`, `LUAJIT_VERSION_NUM 20199` (deprecated), and `LUAJIT_VERSION_SYM luaJIT_version_2_1_ROLLING` ("Enforce (dynamic) linker error for version mismatches") ([luajit_rolling.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/luajit_rolling.h)).
- Most recent commits on `v2.1`: 2026-10-14 "Windows: Use 64 bit conversion for tonumber() with base", and nine commits on 2026-10-13 (x64/x86/ARM64 fixes, FFI string-buffer fix, "v3.0 backport") — all by Mike Pall ([commits](https://github.com/LuaJIT/LuaJIT/commits/v2.1)). The Homebrew formula currently pins commit `2a8e017…` = version 2.1.1791988261 ([luajit.rb](https://github.com/Homebrew/homebrew-core/blob/master/Formula/l/luajit.rb)).
- Mike Pall's comments in issue #1393 (Oct 2025) mention a planned **v3.0** in which "custom allocators will be ignored" and "an API for memory constraints is planned" ([issue #1393](https://github.com/LuaJIT/LuaJIT/issues/1393)).
- A reputable alternative branch: **OpenResty's luajit2** ("OpenResty's maintained branch of LuaJIT", "not to be considered a fork, since we still regularly synchronize changes from the upstream"), adding `thread.exdata`, `lua_resetthread`, `table.isempty/isarray/nkeys/clone`, `jit.prngstate`, more aggressive JIT defaults ([openresty/luajit2](https://github.com/openresty/luajit2)).

### 1.2 Platforms and library names **[V]**

| Item | Fact | Source |
|---|---|---|
| Windows | Windows 7+, x86 / x64 / ARM64 (from v2.0) | [status](https://luajit.org/status.html) |
| Linux / *BSD / POSIX | supported from v2.0 (needs mmap + dlopen) | [status](https://luajit.org/status.html) |
| macOS | 10.4+ from v2.1; **ARM64 from v2.1**; iOS 3.0+ | [status](https://luajit.org/status.html) |
| CPUs | x86 (SSE2 required in v2.1), x64, ARM, ARM64, MIPS32/64; RISC-V 64 "TBA" | [status](https://luajit.org/status.html) |
| Windows artifact | `msvcbuild.bat` → `lua51.dll` + `luajit.exe`; "link to the lua51.dll … and don't rename it" | [install](https://luajit.org/install.html), [msvcbuild.bat](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/msvcbuild.bat) |
| Linux artifact | `TARGET_SONAME = libluajit-5.1.so.2` (MAJVER 2, MINVER 1, ABIVER 5.1); extra libs only `-lm -ldl` | [Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile) |
| macOS artifact | `libluajit-5.1.2.dylib`, built with `-dynamiclib -undefined dynamic_lookup`; `MACOSX_DEPLOYMENT_TARGET` **must** be exported or make errors out | [Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile), [install](https://luajit.org/install.html) |
| Windows ARM64 cross-build | `vcvarsall.bat x64_arm64` then `msvcbuild` | [install](https://luajit.org/install.html) |

The Windows ABI note from Mike Pall: all C symbols are bound to the DLL name, so a LuaJIT DLL must stay `lua51.dll` if any C module expects it ([lua-l 2009](https://lua-users.org/lists/lua-l/2009-11/msg00767.html)). For a sandbox with no C modules this is irrelevant, but the install page says: "Static linking is allowed on Windows only if you won't load Lua/C modules at runtime" ([install](https://luajit.org/install.html)) **[V]**.

### 1.3 GC64 mode, memory limits, and the `lua_newstate` restriction **[V]**

- "All 64 bit ports use 64 bit GC objects by default (LJ_GC64)"; x64 can be switched to the old 32-on-64 mode with `XCFLAGS=-DLUAJIT_DISABLE_GC64`; bytecode differs between the modes ([install](https://luajit.org/install.html)). `msvcbuild.bat nogc64` does the same on Windows ([msvcbuild.bat](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/msvcbuild.bat)).
- Limits: `LJ_MAX_MEM32 0x7fffff00` ("Max. 32 bit memory allocation"), `LJ_MAX_MEM64 ((uint64_t)1<<47)`, `LJ_MAX_MEM (LJ_GC64 ? LJ_MAX_MEM64 : LJ_MAX_MEM32)` ([lj_def.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_def.h)). GC64 references: "The lowest 47 bits either hold a pointer, a zero-extended 32 bit integer or all bits set to 1 for primitive types" ([lj_obj.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_obj.h)). OpenResty's write-up: classic x64 mode confined GC memory to "the low 31 bit address space … 2 GB"; GC64 extends to "the low 47 bit space, which is 128 TB"; "pioneered by Peter Cawley and consolidated by Mike Pall" ([OpenResty blog, 2019](https://blog.openresty.com/en/luajit-gc64-mode/)).
- `lua_newstate`: the install page says use `luaL_newstate`, "avoid using lua_newstate, since this uses the (slower) default memory allocator from your system (no support for this on 64 bit architectures)" ([install](https://luajit.org/install.html)). In source the restriction is `#if LJ_64 && !LJ_GC64` → stub printing "Must use luaL_newstate() for 64 bit target" and returning NULL ([lib_aux.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lib_aux.c)); under GC64 the real `lua_newstate(lua_Alloc, void*)` exists in `lj_state.c`, and the first allocation is validated with `checkptrGC(GG)` ([lj_state.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_state.c)). **[M]** `nm` on the GC64 arm64 dylib shows `_lua_newstate` exported.
- Issue #1393 (opened 2025-10-13, fixed 2025-10-16): a custom allocator that returned addresses outside the accepted range made `lua_newstate` return NULL and leak; Mike Pall: "Custom allocators should only be used for very specific debugging purposes", "custom allocators will be ignored in v3.0", and an API for memory limits is planned ([issue #1393](https://github.com/LuaJIT/LuaJIT/issues/1393)). `lua_setallocf` exists and simply sets `g->allocf/allocd` ([lj_api.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_api.c)).
- **[I]** Consequence for a memory budget: a counting allocator via `lua_newstate` *works today on GC64* if your allocator returns 47-bit addresses (any normal `malloc`/`NativeMemory.Alloc` does on Windows/Linux/macOS user space), but Mike considers it a debugging feature and will drop it in v3.0. Prefer polling `lua_gc(L, LUA_GCCOUNT, 0)` (KB in use; implemented as `g->gc.total >> 10` in [lj_api.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_api.c)) once per frame / inside the timeout hook and killing the script when it exceeds the budget.

### 1.4 Lua 5.1 API, enabled 5.2/5.3 extensions, C-API extensions **[V]**

([luajit.org/extensions.html](https://luajit.org/extensions.html) unless noted)

- "LuaJIT is fully upwards-compatible with Lua 5.1" and ABI-compatible at the linker level; `lua.h` says `LUA_VERSION "Lua 5.1"`, `LUA_VERSION_NUM 501`, `LUA_GLOBALSINDEX (-10002)`, `LUA_REGISTRYINDEX (-10000)`, `LUA_ENVIRONINDEX (-10001)`, `lua_resume(L, narg)`, `lua_yield(L, nresults)` ([lua.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lua.h)). `luaconf.h`: `LUA_NUMBER double`, `LUA_INTEGER ptrdiff_t` ("changing the following defines breaks the Lua 5.1 ABI"), `LUAI_MAXSTACK 65500`, `LUAI_MAXCSTACK 8000` ("Max. # of stack slots for a C func") ([luaconf.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/luaconf.h)).
- **Unconditionally enabled 5.2 features:** `goto`/labels, `\x`/`\z` escapes, `load()` with string/reader (and `loadstring` alias), `loadfile(mode, env)`, `math.log(x, base)`, `string.rep(s,n,sep)`, `string.format` `%q` reversible / `%s` honours `__tostring` / `%a`, `%g` pattern class, `io.read("*L")`, `os.exit(status|true|false, close)`, `package.searchpath`, `debug.getinfo` `nparams/isvararg`, `debug.upvalueid/upvaluejoin`, `xpcall(f, err, ...)` extra args.
- **Only with `-DLUAJIT_ENABLE_LUA52COMPAT`:** `goto` reserved, `__lt/__le` mixed types, `__len` on tables + `rawlen`, `__pairs/__ipairs`, `coroutine.running()` two results, `table.pack/unpack`, `io.write` returns file, `package.searchers`, etc. The Makefile warns this "is not full 5.2 compatibility" ([Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile)).
- **5.3 extensions:** `\u{XXXX}` escapes, `table.move`, `coroutine.isyieldable`/`lua_isyieldable`, `assert()` with any error object, `io.read` formats without `*`.
- Other: `load*()` accept a mode string `"t"`/`"b"`/`"bt"` (use `"t"` to reject bytecode); `string.dump` can strip; `table.new`/`table.clear` via `require`; bit ops `bit.*` built in; unary `~`, `&`, `|`, `<<`, `>>` backported from 2.1 syntax; a safe-navigation operator `??` appears in the 2026-10-13 commit log and in LuaLS's 3.19.2 notes.
- **C-API extensions present in `lua51`:** `lua_version`, `lua_upvalueid/join`, `lua_loadx`, `lua_copy`, `lua_tonumberx`, `lua_tointegerx`, `luaL_loadfilex`, `luaL_loadbufferx`, `luaL_traceback`, `luaL_setfuncs`, `luaL_newlib`, `luaL_testudata`, `luaL_setmetatable` ([extensions](https://luajit.org/extensions.html), [lauxlib.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lauxlib.h)); plus `luaJIT_setmode` and `luaJIT_profile_start/stop` ([luajit_rolling.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/luajit_rolling.h), [ext_c_api](https://luajit.org/ext_c_api.html)). **[M]** `nm` confirms `_luaL_traceback`, `_lua_tointegerx`, `_lua_isyieldable`, `_lua_loadx`, `_luaL_loadbufferx`, `_luaJIT_setmode` are exported; `lua_pcallk`, `lua_rotate`, `lua_geti`, `luaL_tolstring` are **not**.
- "The LuaJIT VM is fully resumable" — coroutines can yield across `pcall()`/`xpcall()`, iterators and metamethods, which stock 5.1 cannot ([extensions](https://luajit.org/extensions.html)).
- Numbers: doubles only (`LUA_NUMBER double`); 64-bit integers exist only as FFI `cdata` ("The result is a boxed 64 bit cdata object", literal syntax `0x…ULL`) ([ext_ffi_semantics](https://luajit.org/ext_ffi_semantics.html)) — not available once FFI is removed (next section) **[V]**.

### 1.5 The FFI and why you disable it in a sandbox **[V]**

- The FFI "allows calling external C functions and using C data structures from pure Lua code", "is tightly integrated into LuaJIT (it's not available as a separate module)" and is loaded with `require("ffi")` ([ext_ffi](https://luajit.org/ext_ffi.html)).
- Safety statements: the FFI "provides no memory safety, unlike regular Lua code"; "the FFI library is not safe for use by untrusted Lua code"; on sandboxing: "you definitely don't want to give this code access to the FFI library" ([ext_ffi_semantics](https://luajit.org/ext_ffi_semantics.html)).
- Build-time removal: `-DLUAJIT_DISABLE_FFI` "removes the FFI extension to shrink the executable" ([Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile)). **[I]** For a mod sandbox, compile it out: it also removes the `ffi` entry from `package.preload`, so no runtime scrubbing can be forgotten. Cost: no boxed 64-bit integers and no `string.buffer` FFI-backed APIs for scripts; the host still gets the same 5.1 C API.

### 1.6 JIT on/off, and when hooks and the JIT fight **[V]**

- `jit.on()`/`jit.off()`/`jit.flush()`/`jit.status()`; JIT and optimisation are on by default; `jit.off(true, true)` disables for the current module recursively; `jit.on/off` "only set a flag that is checked when a function is about to be compiled" ([ext_jit](https://luajit.org/ext_jit.html)). From C: `luaJIT_setmode(L, 0, LUAJIT_MODE_ENGINE|LUAJIT_MODE_OFF)` for the whole engine, `LUAJIT_MODE_FUNC/ALLFUNC/ALLSUBFUNC` per function, `LUAJIT_MODE_TRACE|FLUSH` per trace ([ext_c_api](https://luajit.org/ext_c_api.html)). `-DLUAJIT_DISABLE_JIT` "turns LuaJIT into a pure interpreter" ([Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile)).
- **Hooks are not called from compiled traces.** Mike Pall (2012): "You need to turn it off at the start if you plan to receive reliable hook calls at any later point in time"; code compiled before the hook was set "will not trigger any hook calls"; "The LuaJIT interpreter modifies the whole dispatch table"; "per-coroutine hooks are a misfeature" ([Debug hooks and JIT thread, narkive mirror](https://luajit.freelists.narkive.com/lexU9r0S/debug-hooks-and-jit); original list is on freelists.org). Mike Pall (2011): "JIT-compiled code does not call hooks by default. You must either disable the JIT compiler or build with `-DLUAJIT_ENABLE_CHECKHOOK`", which "comes with a speed penalty"; "For LuaJIT you only need to set it once and it applies to all coroutines"; "The line hook is very, very inefficient"; recommended approach: a count hook with a high count, or `count=1` set from a timer/other thread and disabled once it fires — `lua_sethook()` is "the only cross-thread-safe and signal-safe Lua API function" ([coroutine.yield from debug hook thread](https://lua-l.lua.narkive.com/T9ORJwOv/coroutine-yield-from-debug-hook-in-luajit-2)).
- Source confirms both: `LUAJIT_ENABLE_CHECKHOOK` makes traces "Regularly check for instruction/line hooks from compiled code and exit to the interpreter if the hooks are set … This is a compile-time option and disabled by default, since the hook checks may be quite expensive in tight loops" ([lj_record.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_record.c)); `lua_sethook` writes `g->hookf/hookmask/hookcount` on the *global* state, is commented "This function can be called asynchronously (e.g. during a signal)", and calls `lj_trace_abort(g)` ("Abort recording on any hook change") ([lj_dispatch.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_dispatch.c)). Issue #531: `lua_sethook(L, NULL, 0, 0)` clears mask/count but keeps the function pointer (labelled invalid) ([issue #531](https://github.com/LuaJIT/LuaJIT/issues/531)).
- Yielding from hooks: "You can yield from a C hook (lua_sethook), but not from a Lua hook (debug.sethook)" (Mike Pall, 2011, same thread). In `lj_api.c`, `lua_yield` has a separate "Yield from hook: add a pseudo-frame" path ([lj_api.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_api.c)) — **[I]** that path unwinds, so it is *not* usable from a managed hook (see 1.7).
- Where the JIT silently turns itself off: if the **first** machine-code allocation fails, `trace_abort` does `J->flags &= ~JIT_F_ON` ("Disable JIT compiler if first mcode alloc fails") with no Lua error ([lj_trace.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_trace.c)). On macOS, MAP_JIT allocation is only compiled in with `-DLUAJIT_ENABLE_OSX_HRT` ("Check for macOS hardened runtime", `pthread_jit_write_protect_np`) ([lj_mcode.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_mcode.c)). **[I]** Always log `jit.status()` from inside the host at startup so you notice a silently interpreted LuaJIT.

### 1.7 Error handling: longjmp/SEH vs C++ exceptions vs .NET frames **[V]**

- Lua errors (`lua_error`, `luaL_error`, runtime errors) unwind to the nearest `lua_pcall`/`lua_resume`. LuaJIT's exception interoperability table: GCC/Clang/MSVC with external frame unwinding = "Full" (C++ exceptions can be caught by `pcall()`, Lua errors by `catch(...)`, destructors run); "Windows 64 bit, non-MSVC" and GCC internal unwinding = "Limited"; others = "None" ("not safe to throw C++ exceptions across Lua frames"). Foreign exceptions become the Lua error string "C++ exception" unless the C-call wrapper (`LUAJIT_MODE_WRAPCFUNC`, "a single global wrapper function", "not without overhead") is used ([extensions](https://luajit.org/extensions.html), [ext_c_api](https://luajit.org/ext_c_api.html)). The Darwin build adds `-DLUAJIT_UNWIND_EXTERNAL` ([Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile)).
- **.NET:** "Interop with `setjmp` and `longjmp` C functions is not supported. You can't use `longjmp` to skip over managed frames"; on non-Windows "exceptions can end up resulting in unpredictable behaviors and crashes" ([exceptions-interoperability](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/exceptions-interoperability)). The Lua case specifically: Lua's `longjmp` crossing managed frames surfaced as an unhandled `SEHException` with the new EH; the maintainers' fix is "Windows-only", "best-effort", finally blocks in skipped frames "won't be called", and "skipping over managed frames using setjmp/longjmp is unsupported" ([dotnet/runtime PR #111259](https://github.com/dotnet/runtime/pull/111259)). `UnmanagedCallersOnly` methods must be static, blittable, non-generic, never called from managed code ([UnmanagedCallersOnlyAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute?view=net-10.0)); exceptions must never escape them (unhandled on Unix terminates the process — see search results around `exceptionhandling.cpp` in PR #111259 discussion).
- **Rules that follow [I]:**
  1. From C#, only ever call `lua_pcall`/`lua_resume` (never `lua_call`/`lua_callk`), with a message handler for tracebacks (`luaL_traceback` exists in LuaJIT).
  2. Inside an `[UnmanagedCallersOnly]` host function, call only non-raising API functions (`lua_type`, `lua_tonumberx`, `lua_tolstring` on strings, `lua_raw*`, `lua_pushX`, `lua_checkstack`); never `luaL_check*`, `luaL_error`, `lua_error`, `lua_gettable`/`lua_getfield` on values that may have erroring metamethods. NuLua ships exactly such protected shims (`nulua_pgettable`, `nulua_pgetfield`, `nulua_psetfield`, `nulua_pcompare`, `nulua_pnext`, …) ([NuLua NativeMethods.g.cs](https://github.com/nuskey8/NuLua/blob/main/src/NuLua.Interop.LuaJit/Generated/NativeMethods.g.cs)) — evidence that others hit the same constraint.
  3. Wrap every managed callback body in `try/catch`; convert exceptions to a return value (e.g. `nil, errmsg` or a sentinel) and let **Lua** raise: generate Lua-side wrappers `host.f = function(...) local ok, a, b = raw_f(...) if ok then return a, b end error(a, 2) end` — the `error()` executes in Lua frames only.
  4. Anything that *must* unwind from native code (timeout hook that aborts the script; a C function that calls `luaL_error`) must be real native code — a tiny C shim compiled into your `lua51` build (Section 3.6).

### 1.8 C stack and other limits **[V]**

- Error strings: "stack overflow", "attempt to yield across C-call boundary", "cannot resume non-suspended coroutine", "error in error handling", "C++ exception", "not enough memory" ([lj_errmsg.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_errmsg.h)). `LUAI_MAXSTACK 65500` slots per thread, `LUAI_MAXCSTACK 8000` slots a C function may use ([luaconf.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/luaconf.h)); `lj_state_growstack` raises "stack overflow" and `LUA_ERRERR` if the handler overflows again ([lj_state.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_state.c)). **[I]** LuaJIT has no C-call depth counter like PUC's `LUAI_MAXCCALLS`; deep C↔Lua recursion (host→pcall→Lua→host→pcall…) is bounded by the OS thread stack. Keep host callbacks shallow and never re-enter `lua_pcall` from inside a callback more than a few levels.
- `lua_checkstack` must be called before pushing many values; `LUA_MINSTACK` (20) is the guaranteed free space ([Lua 5.1 manual §3.2](https://www.lua.org/manual/5.1/manual.html)).

### 1.9 License **[V]**

MIT: "LuaJIT is free software, released under the MIT license. See full Copyright Notice in the COPYRIGHT file or in luajit.h"; "Copyright (C) 2005-2026 Mike Pall" ([README](https://github.com/LuaJIT/LuaJIT)). OpenResty luajit2 is MIT as well ([luajit2](https://github.com/openresty/luajit2)).

### 1.10 Binaries: none official — how to build, and how big it is

- "LuaJIT is only made available as source code from a git repository"; do not use "pseudo-releases or tarballs created by third parties" or third-party binaries; distro packages are acceptable but "often ship old versions"; distro maintainers should snapshot a branch ([download](https://luajit.org/download.html)) **[V]**. The GitHub repo is a mirror + issue tracker; the canonical remote is `https://luajit.org/git/luajit.git` ([download](https://luajit.org/download.html)) **[V]**.
- **Windows (MSVC) [V]:** open a VS developer prompt, `cd src`, `msvcbuild` → `lua51.dll`, `lua51.lib`, `luajit.exe` ([install](https://luajit.org/install.html)). Options: `nogc64`, `lua52compat`, `debug`, `amalg`, `static` (static lib; no `/MD`), `mixed` ("static library for linking into your own DLL"). Dynamic builds use `/DLUA_BUILD_AS_DLL /MD` (debug `/MDd`) ([msvcbuild.bat](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/msvcbuild.bat)). To pass `-DLUAJIT_DISABLE_FFI`/`-DLUAJIT_ENABLE_CHECKHOOK` on Windows you edit `LJCOMPILE` in the batch file (no XCFLAGS variable) **[I]**.
- **Linux/macOS [V]:** `make` (default `BUILDMODE=mixed` → static + shared libs and a statically linked `luajit`); `make XCFLAGS="-DLUAJIT_DISABLE_FFI -DLUAJIT_ENABLE_CHECKHOOK"`; `make amalg` is the single-translation-unit build ("requires lots of memory"); macOS requires `MACOSX_DEPLOYMENT_TARGET=XX.YY`; the `.so` is stripped by default (`TARGET_STRIP`), `.a` is not ([install](https://luajit.org/install.html), [Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile)).
- **Sizes [M]:** Homebrew arm64 `libluajit-5.1.2.dylib` 527 KB; my own `v2.1` build 626 KB (dylib, stripped) and 889 KB (`libluajit.a`); the only dynamic dependency on macOS is `libSystem.B.dylib` (`otool -L`). The Linux `.so` links only `-lm -ldl` ([Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile)) so it never depends on a system `liblua` **[V]**; expect the same ~0.5–0.7 MB on Linux x64 and Windows x64 **[I]** (no citation found for an official Windows size; third-party DLL sites are not trustworthy).
- **Reputable prebuilt sources, if you must [I]:** distro packages (Debian/Ubuntu `libluajit-5.1-2`, Fedora, Arch), Homebrew (`luajit` formula pins a commit, builds `make amalg` with no XCFLAGS — [luajit.rb](https://github.com/Homebrew/homebrew-core/blob/master/Formula/l/luajit.rb)), OpenResty's bundled luajit2, and the LuaJIT binaries inside NuGet packages NuLua.Runtime.LuaJit.* / Lua.NET (Section 2). None are built with `-DLUAJIT_DISABLE_FFI`, so for a sandbox you build anyway.

### 1.11 macOS arm64 specifics (nice-to-have target) **[M]**

Observed with the harness in Appendix A, `.NET 10.0.0 RID=osx-arm64`:

| LuaJIT library | Result inside the `dotnet` host (hardened runtime, entitlements include `com.apple.security.cs.allow-jit`, `disable-library-validation`) |
|---|---|
| Homebrew bottle 2.1.1788856981 (minos 26.0 / SDK 26.5) | `jit.status()==true`, then **SIGKILL (exit 137)** as soon as the first hot loop was compiled; with `luaJIT_setmode(…OFF)` first, everything runs. |
| Same commit (c6ffc14 / 1788856981) built locally with `make amalg`, SDK 27.0 | works; pure Lua loop 0.91 ns/iter with JIT, `jit.status()==true` |
| Tip `2a8e017` (1791988261) built locally, default flags | works (0.90 ns/iter) |
| Tip built with `-DLUAJIT_ENABLE_OSX_HRT` | **does not compile** at `lj_mcode.c:156` ("void function 'mcode_setprot' should not return a value", Apple clang 21); after changing `return 0;` to `return;` it builds and works (0.91 ns/iter) |

Takeaways **[I]**: (1) never ship someone else's bottle; (2) test JIT-on inside the *actual* host binary (KSA's entitlements may differ from `dotnet`'s); (3) if the host is a hardened-runtime app, build with `LUAJIT_ENABLE_OSX_HRT` (after the one-line fix) so LuaJIT uses `MAP_JIT` + `pthread_jit_write_protect_np` ([lj_mcode.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_mcode.c)); (4) because `pthread_jit_write_protect_np` is per-thread and process-wide for all MAP_JIT regions, run LuaJIT only on threads where .NET's own JIT state is not mid-write (practically: one game thread) — this is a conjecture, not something I found documented.

---

## 2. .NET bindings for LuaJIT / Lua 5.1 ABI

### 2.1 Summary table

| Library | Version (date) | Lua flavour | Model | Native packaging | License | LuaJIT-usable? |
|---|---|---|---|---|---|---|
| **KeraLua** | NuGet 1.4.10 (2026-09-12); 1.4.9 (2026-01-19) | **Lua 5.4** (5.4.8 submodule) | `[DllImport("lua54")]`, Cdecl, `CharSet.Ansi`; callbacks are `IntPtr` | `runtimes/{win-x64,win-x86,win-arm64,linux-x64,linux-arm64,android-*}/native/{lua54.dll,liblua54.so}`, `runtimes/osx/native/liblua54.dylib`, iOS/tvOS/MacCatalyst frameworks | MIT | **No** (5.4-only imports) |
| **NLua** | NuGet 1.7.9 (2026-05-01), dep KeraLua ≥ 1.4.9 | 5.4 via KeraLua | reflection-based CLR bridge (`import`, `LoadCLRPackage`) | via KeraLua | MIT | **No** |
| **MoonSharp** | NuGet 2.0.0 (2016-10-14); 3.0.0-beta.1 prerelease (NuGet 2026-07-04; GitHub "v3.0.0 Beta #1" 03 Mar, published by github-actions) | "99% compatible with Lua 5.2" (no weak tables) | pure C# interpreter | n/a | BSD-3 | n/a (not LuaJIT) |
| **Lua-CSharp** (nuskey8) | NuGet `LuaCSharp` 0.5.7 (2026-09-26) | **Lua 5.2** interpreter (README) — not 5.4 | pure C#, async/await, `CancellationToken`, debug hooks | n/a | MIT | n/a |
| **NuLua** (nuskey8) | NuGet `NuLua.LuaJit` 0.1.0 (2026-06-22), "provided as a preview" | Lua 5.1–5.5, **LuaJIT**, Luau | csbindgen `[DllImport("luajit")]`, Cdecl, delegates for `lua_CFunction`; Rust/Cargo-built natives + C shims | `NuLua.Runtime.LuaJit.{win-x64,win-arm64,osx-x64,osx-arm64,linux-x64,linux-arm64}` → `runtimes/<rid>/native/` | MIT | Yes (preview) |
| **Lua.NET** (tilkinsc) | NuGet 6.0.1 (2025-06-08) | 5.0.3/5.1.5/5.2.4/5.3.6/5.4.7 + **LuaJIT** | `[DllImport("lua51")]`, Cdecl; `lua_CFunction` is a managed delegate via `GetFunctionPointerForDelegate`; header constant still says "LuaJIT 2.1.0-beta3" | bundled `runtimes/win-x64/native/lua51.dll`, `runtimes/linux-x64/native/lua51.so` (no macOS) | MIT | Yes (stale) |
| **JAJ.Packages.LuaNET** | NuGet 1.1.0 (2025-01-14) | LuaJIT-only fork of Lua.NET | as Lua.NET + C#-style API | ? | MIT | Yes (stale) |
| **NeoLua** | NuGet 1.3.19 (2025-12-04), 1.3.20-beta.1 (2026-01-15) | ~Lua 5.3 on the DLR, "not 100% compatible" | compiles Lua to IL | n/a | Apache-2.0 | n/a |
| **Laylua** (Quahu) | NuGet 1.0.0 (2025-01-10), 442 downloads | Lua 5.4 | wrapper + sandbox ("limit memory, instructions, and what scripts can access") | `Laylua.Natives` | **LGPL-3.0** | No |
| **WattleScript** | NuGet 1.0.0 (2022-10-10) | MoonSharp-derived, Lua 5.2 | pure C# | n/a | BSD-3 | n/a |
| "LuaJIT.NET" / "luajit-net" | — | — | no maintained package under these names found (searches on nuget.org/GitHub) **[I]** | — | — | — |

Sources: KeraLua [NuGet](https://www.nuget.org/packages/KeraLua), [repo](https://github.com/NLua/KeraLua), [NativeMethods.cs](https://github.com/NLua/KeraLua/blob/main/src/NativeMethods.cs), [nuspec](https://github.com/NLua/KeraLua/blob/main/KeraLua.nuspec), [releases](https://github.com/NLua/KeraLua/releases); NLua [NuGet](https://www.nuget.org/packages/NLua), [repo](https://github.com/NLua/NLua); MoonSharp [NuGet](https://www.nuget.org/packages/MoonSharp), [3.0.0-beta.1](https://www.nuget.org/packages/MoonSharp/3.0.0-beta.1), [repo](https://github.com/moonsharp-devs/moonsharp), [releases](https://github.com/moonsharp-devs/moonsharp/releases); Lua-CSharp [repo](https://github.com/nuskey8/Lua-CSharp), [NuGet](https://www.nuget.org/packages/LuaCSharp); NuLua [repo](https://github.com/nuskey8/NuLua), [NuGet NuLua.LuaJit](https://www.nuget.org/packages/NuLua.LuaJit), [NuLua.Runtime.LuaJit](https://www.nuget.org/packages/NuLua.Runtime.LuaJit), [generated bindings](https://github.com/nuskey8/NuLua/blob/main/src/NuLua.Interop.LuaJit/Generated/NativeMethods.g.cs); Lua.NET [NuGet](https://www.nuget.org/packages/Lua.NET), [repo](https://github.com/tilkinsc/Lua.NET), [LuaJIT.cs](https://github.com/tilkinsc/Lua.NET/blob/main/src/LuaJIT.cs); [JAJ.Packages.LuaNET](https://www.nuget.org/packages/JAJ.Packages.LuaNET); NeoLua [NuGet](https://www.nuget.org/packages/NeoLua), [repo](https://github.com/neolithos/neolua); Laylua [NuGet](https://www.nuget.org/packages/Laylua), [repo](https://github.com/Quahu/Laylua); WattleScript [NuGet](https://www.nuget.org/packages/WattleScript.Interpreter), [repo](https://github.com/WattleScript/wattlescript). All **[V]** unless marked.

### 2.2 Notes per library

- **KeraLua / NLua.** Native name is `"lua54"` by default (`liblua54.so` Android, `liblua54.dylib` macOS, `@rpath/liblua54.framework/liblua54` iOS) ([NativeMethods.cs](https://github.com/NLua/KeraLua/blob/main/src/NativeMethods.cs)). You *can* install `NativeLibrary.SetDllImportResolver(typeof(KeraLua.Lua).Assembly, …)` to redirect `"lua54"` to `lua51.dll` — the resolver is "the first attempt to resolve native library loads initiated by this assembly" ([SetDllImportResolver](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.setdllimportresolver?view=net-10.0)) — but 24 of its 137 imports are 5.2+/5.4-only (`lua_pcallk`, `lua_callk`, `lua_yieldk`, `lua_rotate`, `lua_rawlen`, `lua_isinteger`, `lua_geti/seti`, `lua_arith`, `lua_len`, `lua_absindex`, `luaL_tolstring`, `luaL_len`, 4-argument `lua_resume`, `lua_newuserdatauv`, `lua_setwarnf`, `lua_toclose`, `luaL_requiref`, `lua_getiuservalue`, …) and KeraLua's high-level wrapper calls them (`PCall` → `lua_pcallk`), so it fails with `EntryPointNotFoundException` on first use **[V]+[I]**. NLua's README says "I strongly recommend you to use plain Lua to do your sandbox" and defaults string encoding to ASCII ([NLua README](https://github.com/NLua/NLua)).
- **Lua-CSharp.** README: "Lua 5.2 interpreter implemented in C#", ".NET Standard 2.1 or higher", strings are UTF-16, `collectgarbage()` maps to .NET GC, "`LuaState` is not thread-safe", `LuaPlatform` abstraction "for sandboxing" (`require`, `print`, `dofile`, `os`), debug hooks exist but "A timeout checked by a hook therefore cannot interrupt a blocking C# function", host functions receive a `CancellationToken` ([README](https://github.com/nuskey8/Lua-CSharp)). Its own benchmark chart (`docs/Benchmark.png`, "Calling C# function 25,000 times"): NLua 36.45 ms / MoonSharp 19.87 ms / **Lua-CSharp 8.05 ms**; allocation 12,306.5 KB / 13,887 KB / **31.3 KB** ([chart](https://github.com/nuskey8/Lua-CSharp/blob/main/docs/Benchmark.png)). A Unity user reported the opposite for 0.4.2 (NLua 4× and MoonSharp ~8× faster) until an unofficial 0.5 build "outperforms both" ([issue #156](https://github.com/nuskey8/Lua-CSharp/issues/156)).
- **NuLua.** Same author as Lua-CSharp; "Unified Lua5.x/LuaJIT/Luau bindings"; "This library is currently provided as a preview"; `ILuaState` "is not thread-safe"; "Lua script execution itself always completes synchronously, but C# functions passed to Lua can be asynchronous" (scripts calling them must run via `DoStringAsync`); userdata structs "must be unmanaged" ([README](https://github.com/nuskey8/NuLua)). The generated bindings use `[DllImport("luajit", …)]` with `[UnmanagedFunctionPointer(Cdecl)]` delegates, and the native side is built by Cargo with a C shim ([tree](https://github.com/nuskey8/NuLua)). **[I]** Promising design (protected shims, per-RID runtime packages incl. osx-arm64), but 0.1.0-preview with no Unity support yet; I would borrow its ideas rather than depend on it for a shipped mod.
- **Lua.NET / JAJ fork.** `DllImport("lua51")`, delegates marshalled with `Marshal.GetFunctionPointerForDelegate` (you must keep delegates alive — "You must manually keep the delegate from being collected"; Microsoft now says "It is recommended to use function pointers and UnmanagedCallersOnlyAttribute instead" ([GetFunctionPointerForDelegate](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getfunctionpointerfordelegate?view=net-10.0))); README itself shows `[UnmanagedCallersOnly]` static lambdas and `delegate*unmanaged` in `AsLuaLReg` ([Lua.NET](https://github.com/tilkinsc/Lua.NET)); bundled binaries are opaque (version/build flags not stated; constant says 2.1.0-beta3) **[V]**. Usable as a reference for signatures, not as a dependency **[I]**.

### 2.3 Recommendation **[I]**

**Direct P/Invoke to your own LuaJIT build.** Reasons: (a) you need a specific build (GC64, `-DLUAJIT_DISABLE_FFI`, optionally `-DLUAJIT_ENABLE_CHECKHOOK`, static CRT on Windows) that no package ships; (b) the 5.1 C API surface you need is ~60 functions — a few hundred lines of `[LibraryImport]`; (c) you control loading from the mod folder (Section 7) and unloading semantics; (d) `[UnmanagedCallersOnly]` + `delegate* unmanaged[Cdecl]<nint,int>` gives allocation-free callbacks with no delegate lifetime problems; (e) measured call overhead is at the noise floor (Section 6). KeraLua-adapted is a dead end (ABI mismatch); pure-C# interpreters remove native-loading problems but cost 10–100× on script execution and give you Lua 5.2 semantics, UTF-16 strings and a different hook model — acceptable only if scripts are tiny glue code.

---

## 3. Embedding patterns (Lua 5.1 API as implemented by LuaJIT)

### 3.1 State creation

- `luaL_newstate()` (not `lua_newstate`), then `luaL_openlibs(L)`; "load the jit library, or the JIT compiler won't activate" ([install](https://luajit.org/install.html)) **[V]**. Create it on the game thread and only ever touch it there (Section 4) **[I]**.
- **[M]** `luaL_newstate + luaL_openlibs + lua_close` ≈ 21–25 µs; heap after `openlibs` ≈ 36 KB (`collectgarbage("count")`). Full-state recreation is cheap enough to be the default reload/reset strategy.

### 3.2 Exposing host objects

Options and trade-offs **[I]**, using 5.1 API facts **[V]**:

| Approach | How | Pros | Cons |
|---|---|---|---|
| **Full userdata + metatable** (recommended for handles) | `lua_newuserdata(L, sizeof(int))` holding a host-side handle id (or `GCHandle.ToIntPtr`), `luaL_newmetatable`/`luaL_setmetatable` with `__index` (methods), `__newindex`, `__gc`, `__tostring`, `__eq`; validate with `luaL_testudata` (exists in LuaJIT) | identity + lifetime: `__gc` fires on collection so you can `GCHandle.Free` / release host refs; metatable protects fields; type-checkable | each access is a C# callback (~5–7 ns + marshalling) |
| **Light userdata** | `lua_pushlightuserdata(L, ptr)`; "A light userdata represents a pointer" and "is equal to any light userdata with the same C address" ([manual](https://www.lua.org/manual/5.1/manual.html)) | zero allocation | no per-value metatable in 5.1 (one shared metatable via `debug.setmetatable`), no `__gc` → no lifetime tracking; pointer must be a stable handle, never a managed object address |
| **Plain tables** (snapshots) | push a table of fields per event / per frame | fastest reads for scripts (JIT-compiled table access), no callbacks | copy cost; writes don't propagate unless you read them back; stale data |
| **Registry references** | `luaL_ref(L, LUA_REGISTRYINDEX)` returns an int ref ("creates and returns a reference … and pops the object"; `luaL_unref` so "the referred object can be collected") ([manual](https://www.lua.org/manual/5.1/manual.html)) | the standard way for C# to hold Lua functions/tables (event callbacks) | you must `luaL_unref` when the script is unloaded or you leak |

**[I]** For a game API: expose *handles* as full userdata (vehicle, part, …) whose methods are host functions; expose hot read-only telemetry as a table you refresh per tick (or as userdata with `__index` reading fields on demand). Never store managed object addresses in Lua; store a `GCHandle` (normal or weak) or an index into a host-side registry and `__gc`-release it.

### 3.3 Calling Lua from C#

- Push the function (from a registry ref), args, then `lua_pcall(L, nargs, nresults, errfunc)`; with `errfunc = 0` "the error message returned on the stack is exactly the original error message"; otherwise the handler "will be called with the error message and its return value will be the message returned on the stack" ([manual](https://www.lua.org/manual/5.1/manual.html)) **[V]**. Push a C traceback handler once (a function that calls `luaL_traceback(L, L, msg, 1)`, available in LuaJIT) and pass its index as `errfunc`, or install `debug.traceback` from the sandbox env **[I]**. Note: `debug.traceback` is a Lua C function in the `debug` library — if you remove `debug` from the sandbox, keep a private reference to `traceback` in the registry first **[I]**.
- `lua_pcall` restores hook state on error (`hook_restore(g, oldh)` in [lj_api.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_api.c)) **[V]**, so a timeout error does not leave a stale hook.
- **[M]** `lua_getfield(L, LUA_GLOBALSINDEX, "noop") + lua_pcall` ≈ 38–41 ns from .NET (15 ns from C); the difference is UTF-8 string marshalling of the name. Resolve names to registry refs once (`luaL_ref`) and call `lua_rawgeti(L, LUA_REGISTRYINDEX, ref)` in hot paths.

### 3.4 Calling C# from Lua

```csharp
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
static int Vehicle_GetAltitude(nint L) {            // lua_CFunction
    try {
        var v = HostRegistry.Resolve(Lua.lua_touserdata(L, 1));   // userdata → handle id
        Lua.lua_pushnumber(L, v.Altitude);
        return 1;
    } catch (Exception e) { Lua.lua_pushnil(L); Lua.lua_pushstring(L, e.Message); return 2; } // never let it escape
}
// registration:
Lua.lua_pushcclosure(L, &Vehicle_GetAltitude, 0);   // delegate* unmanaged[Cdecl]<nint,int>
```

- `&Method` on an `[UnmanagedCallersOnly]` static method yields a `delegate* unmanaged` ([UnmanagedCallersOnlyAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute?view=net-10.0), [function pointers](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code)); no delegate object, nothing to keep alive, "supported in all environments" ([GetFunctionPointerForDelegate remarks](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getfunctionpointerfordelegate?view=net-10.0)) **[V]**.
- Per-instance state: use closures with upvalues (`lua_pushcclosure(L, fn, n)` + `lua_upvalueindex(i)` = `LUA_GLOBALSINDEX - i` in 5.1) holding a light userdata handle, or an integer id **[V]/[I]**.
- Error protocol: see 1.7 rule 3 — return `(false, msg)` and raise from a Lua wrapper; or route through the native shim (3.6).
- Strings: LuaJIT copies the bytes on `lua_pushlstring`, so pass `ReadOnlySpan<byte>` pinned only for the call; use `StringMarshalling.Utf8` on `[LibraryImport]` for convenience APIs ([P/Invoke source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation)) and `Marshal.PtrToStringUTF8(ptr, len)` from `lua_tolstring` **[I]**. Lua strings are byte strings; LuaJIT's `load*()` accept UTF-8 source and skip a BOM ([extensions](https://luajit.org/extensions.html)) **[V]**.
- Numbers: `double` only; integers beyond 2^53 lose precision; no FFI → no boxed 64-bit ints. Pass 64-bit ids as two doubles or as a userdata/string **[I]**.

### 3.5 Coroutines and "await host promise"

- Per script: `lua_newthread(L)` (keep a registry ref to it so it is not collected — "There is no explicit function to close or to destroy a thread" ([manual](https://www.lua.org/manual/5.1/manual.html))), load the chunk into that thread, run with `lua_resume(L1, nargs)`; it "returns LUA_YIELD if the coroutine yields, 0 if the coroutine finishes" ([manual](https://www.lua.org/manual/5.1/manual.html)) **[V]**.
- An async host function yields by `return lua_yield(L, nresults)` — "This function should only be called as the return expression of a C function" ([manual](https://www.lua.org/manual/5.1/manual.html)) **[V]**; the regular-yield path in `lj_api.c` just moves results and returns to the VM ("Regular yield: move results down if needed") — no unwinding, so it is safe from an `[UnmanagedCallersOnly]` function **[V]+[I]**. It only works when the function was reached through `lua_resume` (else "attempt to yield across C-call boundary"); thanks to LuaJIT's fully resumable VM it works even if the script called you from inside `pcall` or a metamethod ([extensions](https://luajit.org/extensions.html)) **[V]**.
- Pattern **[I]**: the host function stores the pending `Task`/callback keyed by the coroutine (`lua_State*` of the thread), returns `lua_yield(L, 0)`; when the task completes, the game thread pushes the results onto that thread and calls `lua_resume(L1, nres)`. Expose it to scripts as `local result = host.fetch(...)` (looks synchronous) or as `await(promise)` where `await` is `coroutine.yield` under the hood. Events from the host: `lua_resume` a fresh coroutine per handler invocation (sync handlers) or keep long-running handler coroutines you resume with event payloads.
- Timeouts for coroutines: the count hook (3.7) is global to the state and fires in whichever coroutine is running ("you only need to set it once and it applies to all coroutines" — Mike Pall, [thread](https://lua-l.lua.narkive.com/T9ORJwOv/coroutine-yield-from-debug-hook-in-luajit-2)) **[V]**.

### 3.6 Sandboxing (Lua 5.1 / LuaJIT)

Facts **[V]**:
- 5.1 uses function environments: "To get the environment table of a Lua function, you call getfenv. To replace it, you call setfenv" ([manual §2.9](https://www.lua.org/manual/5.1/manual.html)); from C, `lua_setfenv(L, idx)` on the loaded chunk. The lua-users sandbox page marks `setfenv` as "UNSAFE – can modify environments of functions that are up the call-chain and outside the sandbox" and also flags `setmetatable`, `rawget`, `rawset`, `string.dump`, bytecode loading ([lua-users SandBoxes](http://lua-users.org/wiki/SandBoxes)); an exploit write-up notes `setfenv()` alone "isn't enough for proper protection" ([LuaRocks CVE post](https://vhyrro.neorg.org/posts/critical-luarocks-exploit-cve/)). Escape vectors listed by HackTricks: `package.loadlib`, `require` of native modules, LuaJIT's `ffi`, the `debug` library ([HackTricks](https://hacktricks.wiki/en/generic-methodologies-and-resources/lua/bypass-lua-sandboxes/index.html)).
- `load*` mode `"t"` rejects bytecode ([extensions](https://luajit.org/extensions.html)); `os.exit` exists in LuaJIT's `os` ([extensions](https://luajit.org/extensions.html)); `require` consults `package.loaded[modname]` first and then the searchers in `package.loaders`/`package.preload` ([manual](https://www.lua.org/manual/5.1/manual.html)).

Recipe **[I]** (all on the game thread, at state creation):
1. Build LuaJIT with `-DLUAJIT_DISABLE_FFI`. Call `luaL_openlibs`, then build a **fresh environment table** `env` for scripts containing copies of: `assert, error, ipairs, pairs, next, pcall, xpcall, select, tonumber, tostring, type, unpack, rawequal` (optionally `rawget/rawset` if you have no proxies to protect), `math.*`, `string.*` minus `dump`, `table.*`, `bit.*`, `coroutine.*`, `os.clock/date/time/difftime` only, and your `host` API. **Do not** include `io`, `os.*` (rest), `package`, `require` (replace), `debug`, `jit` (maybe `jit.off/on/status` only), `load/loadstring/loadfile/dofile`, `setfenv/getfenv`, `newproxy`, `collectgarbage` (or restrict to `"count"`), `print` (replace with host logger), `module`.
2. Protect the shared string metatable: `string.dump` was deleted from the shared `string` table, so `("x"):dump` is gone too; otherwise `getmetatable("").__index` reaches the original `string` table.
3. Load each script with `luaL_loadbufferx(L, src, len, "@scripts/foo.lua", "t")` then `lua_setfenv(L, -2)` with `env` (or a per-script table whose `__index` is `env`, so scripts cannot see each other's globals).
4. Custom `require(name)`: normalise `name` (`[%w_%.]+` only, no `..`), map `a.b` → `<scripts>/a/b.lua`, load with mode `"t"`, set env, cache in a per-sandbox `loaded` table; return the cached value on repeat; clear the cache on reload.
5. Hide the `_G` escape hatch: `env._G = env`.
6. Keep `pcall/xpcall/error` — scripts need them, and error objects are plain values.
7. Resource limits: timeout (3.7), memory (1.3), and a cap on `lua_newthread` count / registry refs per script.

### 3.7 Instruction-count / wall-clock interrupts

What the sources establish **[V]**: hooks are not called from JIT-compiled code unless the build has `-DLUAJIT_ENABLE_CHECKHOOK` (speed penalty) or the JIT is off; `lua_sethook` may be called asynchronously from another thread (the only such API); a count hook with `count=1` set from a timer and removed on first fire is Mike Pall's recommended approach (1.6). Kong's sandbox notes "quotas don't work in LuaJIT since debug.sethook works differently" and disables the JIT for quota'd code ([kong-lua-sandbox](https://github.com/kong/kong-lua-sandbox/blob/master/kong-lua-sandbox.lua)).

Design **[I]**:
- **Watchdog thread** (a .NET timer) arms `lua_sethook(L, timeout_hook, LUA_MASKCOUNT, 1)` when a script has run longer than its budget (e.g. 5 ms of a frame, 2 s for a one-shot). The game thread sets a `volatile` "script running since" timestamp before each `lua_pcall`/`lua_resume` and clears it after.
- The hook must abort the script by raising a Lua error. **The hook cannot be C# code** (raising = unwinding across the managed frame; 1.7). Two zero-risk options: (a) a ~10-line C shim compiled into your `lua51` (e.g. add `host_shim.c` to the amalgamation): `void host_timeout_hook(lua_State *L, lua_Debug *ar){ lua_sethook(L, NULL, 0, 0); luaL_error(L, "script timed out"); }` plus `int host_raise(lua_State *L)` (= `lua_error`) for host-side errors; (b) type-pun LuaJIT's own exported `lua_error` as the hook function (`int lua_error(lua_State*)` ignores the extra `lua_Debug*` argument on all three target ABIs): it throws whatever value is on top of the stack, so the error object is garbage but your host flag tells you it was a timeout — works, but it is UB in C terms; use (a).
- **Build choice:** with `-DLUAJIT_ENABLE_CHECKHOOK` the hook also stops compiled loops (traces "exit to the interpreter if the hooks are set", [lj_record.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_record.c)). Without it, either `luaJIT_setmode(L,0,ENGINE|OFF)` for untrusted scripts (my measurement: pure-Lua loop 0.9 → 2.5 ns/iter, i.e. ≈2.8× slower on arithmetic loops, ~same speed for host-call-heavy code) or accept that a `while true do end` in a hot trace is only killable by `lua_close`-ing the state from the game thread (which you can still do, since the game thread is the one blocked… so actually you cannot — the watchdog can only set the hook). Therefore: **CHECKHOOK build, or JIT off, for any script you cannot trust to terminate.**
- Cooperative alternative: run scripts as coroutines and give each a per-frame budget; a script that never yields is still only stoppable via the hook. Lua-CSharp's note applies to every model: "A timeout checked by a hook therefore cannot interrupt a blocking C# function" ([Lua-CSharp README](https://github.com/nuskey8/Lua-CSharp)) — keep host functions short or make them async (3.5).

### 3.8 Memory limits

See 1.3: counting allocator through `lua_newstate` works on GC64 today but is discouraged and scheduled to be ignored in v3.0 ([issue #1393](https://github.com/LuaJIT/LuaJIT/issues/1393)); `lua_gc(L, LUA_GCCOUNT, 0)` gives KB in use ([lua.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lua.h), [lj_api.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_api.c)) **[V]**. **[I]** Poll it per frame and from the count hook; on breach raise the timeout error path and recreate the state.

### 3.9 Error propagation with stack traces

`luaL_traceback(L, L1, msg, level)` is in LuaJIT's `lauxlib.h` ([lauxlib.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lauxlib.h)) **[V]**; use it in the message handler (shim or Lua `debug.traceback` captured before `debug` is removed). `lua_pcall` returns `LUA_ERRRUN/LUA_ERRMEM/LUA_ERRERR`; the message (with traceback) is on the stack; copy it to a C# `LuaException { Status, Message, Traceback, ScriptPath }` and pop **[I]**. Chunk names of the form `@scripts/foo.lua` make tracebacks show `scripts/foo.lua:12:` — handy for jumping to the file in neovim **[I]**.

### 3.10 Hot reload

**[V]** `require` returns `package.loaded[modname]` when present; setting it to `nil` forces a reload ([manual](https://www.lua.org/manual/5.1/manual.html)). **[I]** Two tiers: (1) *module reload*: clear your sandbox `loaded[name]`, re-run `require`, swap the exported table in place (`for k in pairs(old) do old[k]=nil end; for k,v in pairs(new) do old[k]=v end`) so holders keep working — but host-side registry refs to the old handlers must be `luaL_unref`'d and re-registered; (2) *full reset* (default): `lua_close(L)`, new state, re-run every script (21–25 µs + script time) — simplest and leak-proof. Watch files with `FileSystemWatcher`, debounce ~100 ms, marshal onto the game thread, and keep the previous state alive until the new one loaded without errors so a syntax error doesn't kill a running mod.

---

## 4. Threading and the .NET GC

- **Lua is single-threaded per global state.** `lua_lock`/`lua_unlock` exist for OS-thread embedding but "In the default distribution these two functions do nothing" ([lua-users ThreadsTutorial](http://lua-users.org/wiki/ThreadsTutorial)) **[V]**; LuaJIT exposes no locking at all and its docs/bindings all say the same thing (Lua-CSharp: "LuaState is not thread-safe"; NuLua: "ILuaState is not thread-safe") **[V]**. The one exception is `lua_sethook` from another thread (1.6) **[V]**.
- Options **[I]**: (a) one state on the game thread, scripts as coroutines (recommended: all host objects are game-thread objects anyway); (b) one *separate* state per worker thread, exchanging only plain data through the host (no Lua values cross states); (c) never run two threads in one state, including coroutines of that state.
- **.NET GC interplement [V]+[I]:**
  - `lua_State*` is unmanaged memory; nothing to pin. Wrap it in a `SafeHandle` whose `ReleaseHandle` calls `lua_close` for deterministic disposal; SafeHandle gives critical finalization and P/Invoke ref-counting ([SafeHandle](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.safehandle?view=net-10.0)). But "The thread on which the finalizer runs is unspecified" ([Object.Finalize](https://learn.microsoft.com/en-us/dotnet/api/system.object.finalize?view=net-10.0)), so a finalizer must never `lua_close` a state the game thread may still be using: make `ReleaseHandle` enqueue the close onto the game thread (or log and leak) and always `Dispose()` explicitly.
  - Managed objects referenced from Lua: store `GCHandle` values (as light userdata or an id) — never raw addresses (objects move). Strong handles keep objects (and a collectible ALC) alive: "Strong garbage collector (GC) handles … from both inside and outside of the AssemblyLoadContext" block unloading ([unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)). Free them in `__gc` and in bulk on state close.
  - Delegates are unnecessary with `[UnmanagedCallersOnly]`; if you do use delegates, keep them rooted ("If native code stores the function pointer beyond the duration of the call, root the delegate for its entire lifetime" — [GetFunctionPointerForDelegate](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getfunctionpointerfordelegate?view=net-10.0)).
  - Function pointers into a **collectible ALC** die with it: before `AssemblyLoadContext.Unload()`, `lua_close` every state, remove every hook, and drop every `lua_pushcclosure`'d pointer; the `Unloading` event is the place ([unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)) **[V]+[I]**.
  - Buffers passed to `lua_pushlstring`/`luaL_loadbufferx` are copied by Lua, so `fixed` for the duration of the call is enough **[I]**.

---

## 5. Tooling for script authors (neovim + LuaLS)

### 5.1 Facts **[V]**

- LuaLS settings: `runtime.version` is a string with options `"Lua 5.1"`, `"Lua 5.2"`, `"Lua 5.3"`, `"Lua 5.4"`, `"LuaJIT"` (default `"Lua 5.4"`); `runtime.builtin` entries default to "enabled if it is a part of the current runtime.version" — `ffi`, `bit`, `jit` are in that list; `workspace.library` "Used to add library implementation code and definition files to the workspace scope"; `diagnostics.globals`; `runtime.path`/`runtime.pathStrict` for `require` resolution ([settings](https://luals.github.io/wiki/settings/)).
- Definition files: "A definition file is simply a Lua file with annotations in it and a `@meta` annotation at the top"; recommended name `*.d.lua`; "provide the path to the file (or parent directory) to `workspace.library`"; community definitions live in the LuaCATS organisation ([definition files](https://luals.github.io/wiki/definition-files/)).
- Annotations: `---@meta [name]`, `---@class [(exact)] Name[: Parent]`, `---@field [scope] name[?] type [desc]`, `---@param name[?] type`, `---@return type [name]`, `---@type`, `---@alias`, `---@generic`, `---@async` (enables `await` hints), `---@overload fun(...)`, `---@enum`, `---@nodiscard` ([annotations](https://luals.github.io/wiki/annotations/)).
- `.luarc.json` in the workspace; priority: `--configpath` file > `.luarc.json` > client settings; keys do **not** take the `Lua.` prefix; add `"$schema": "https://raw.githubusercontent.com/LuaLS/vscode-lua/master/setting/schema.json"` ([configuration](https://luals.github.io/wiki/configuration/)).
- Neovim: nvim-lspconfig's `lua_ls` uses `root_markers` `.luarc.json`, `.luarc.jsonc`, `.emmyrc.json` (then stylua/selene files, `.git`), and its sample `on_init` sets `runtime.version = 'LuaJIT'` and `workspace.library` ([nvim-lspconfig lua_ls.lua](https://github.com/neovim/nvim-lspconfig/blob/master/lsp/lua_ls.lua)); configure via `vim.lsp.config('lua_ls', {...})`.
- LuaLS latest release 3.19.2 (13 Oct; 3.19.0 on 07 Aug) — year not shown on the page, 2026 by sequence; the 3.19.2 notes explicitly fix "The LuaJIT `??` operator" typing ([releases](https://github.com/LuaLS/lua-language-server/releases), [3.19.2](https://github.com/LuaLS/lua-language-server/releases/tag/3.19.2)).

### 5.2 Shipping an API stub **[I]**

Generate `api/host.d.lua` from your C# API (reflection over the registration table or source generator) and put a `.luarc.json` at the root of the user's scripts folder — because `.luarc.json` is a root marker, opening any script in neovim makes that folder the workspace:

```jsonc
// <scripts>/.luarc.json
{
  "$schema": "https://raw.githubusercontent.com/LuaLS/vscode-lua/master/setting/schema.json",
  "runtime.version": "LuaJIT",
  "runtime.path": ["?.lua", "?/init.lua"],
  "runtime.pathStrict": true,
  "workspace.library": ["./api"],
  "workspace.checkThirdParty": "Disable",
  "diagnostics.globals": ["host"],
  "runtime.builtin": { "io": "disable", "os": "disable", "package": "disable", "debug": "disable", "ffi": "disable" }
}
```

```lua
-- api/host.d.lua (generated)
---@meta host

---@class Vehicle
---@field name string
---@field altitude number  # metres above the reference surface
local Vehicle = {}
---@param throttle number  # 0..1
function Vehicle:set_throttle(throttle) end

---@class host
host = {}
---@return Vehicle
function host.active_vehicle() end
---@async
---@param seconds number
function host.sleep(seconds) end      -- yields the coroutine; resumes after `seconds`
---@param name string
---@param fn fun(ev: table)
function host.on(name, fn) end
```

`runtime.builtin` entries set to `"disable"` make LuaLS flag `io.open` etc. as undefined — matching the sandbox. Keep the stub and the runtime API in lockstep by generating both from the same C# registration metadata; add a CI test that loads the stub with `lua-language-server --check`.

### 5.3 Typed-Lua alternatives (brief) **[V]**

- **Luau** (Roblox): "a small, fast, and embeddable programming language based on Lua with a gradual type system", "aims to be backwards-compatible with Lua 5.1", MIT, C++ (C++11 runtime, C++17 compiler), embedding via `luau_compile` + `luau_load`, `__gc` unsupported (`lua_newuserdatadtor`), sandboxing via `luaL_sandbox`/`luaL_sandboxthread`; the sandbox removes `io`, `package`, most of `os` and `debug`, `dofile/loadfile`, bytecode loading, and makes library tables and the globals table read-only; optional JIT on x64/arm64; weekly releases (0.742 on 10 Oct) ([luau.org](https://luau.org/), [sandbox](https://luau.org/sandbox), [repo](https://github.com/luau-lang/luau), [releases](https://github.com/luau-lang/luau/releases)). NuLua ships a `NuLua.Luau` binding ([NuLua](https://github.com/nuskey8/NuLua)). **[I]** Best built-in sandbox story of any Lua, but it is not LuaJIT (interpreter + optional JIT, different C API details, C++ build).
- **Teal**: "a typed dialect of Lua" that compiles to plain Lua; "Teal works with Lua 5.1-5.4, including LuaJIT"; `tl check`, `tl gen module.tl → module.lua`; MIT; single-file compiler; v0.24.8 (13 Oct) ([teal-language/tl](https://github.com/teal-language/tl), [releases](https://github.com/teal-language/tl/releases)). **[I]** Keeps LuaJIT as the runtime; you could offer `.tl` support later by running `tl gen` at load time (the compiler is itself Lua and can run inside the sandboxed state).

---

## 6. Numbers

### 6.1 From published sources **[V]**

| Metric | Value | Source |
|---|---|---|
| Lua-CSharp vs MoonSharp vs NLua, "Calling C# function 25,000 times" | 8.05 ms / 19.87 ms / 36.45 ms (≈0.32 / 0.79 / 1.46 µs per call); allocations 31.3 KB / 13,887 KB / 12,306.5 KB | [Benchmark.png](https://github.com/nuskey8/Lua-CSharp/blob/main/docs/Benchmark.png) |
| Unity, empty Lua function ×5,000 per frame | LuaCSharp 0.4.2: 40 fps; unofficial 0.5: 395 fps; NLua 170 fps; MoonSharp 315 fps | [Lua-CSharp issue #156](https://github.com/nuskey8/Lua-CSharp/issues/156) |
| NLua vs MoonSharp, `fib(35)` (undated gist) | ≈1.09 s vs 13.2 s | [gist](https://gist.github.com/CannibalVox/aa63029545ef7fa0e8f36a43536df490) |
| P/Invoke with vs without GC transition (BenchmarkDotNet, 2019, x64) | 15–17 ns vs 9–11 ns | [dotnet/coreclr PR #26458](https://github.com/dotnet/coreclr/pull/26458) |
| `SuppressGCTransition` on a trivial native call | "sped up by 4 times"; risks: no GC during call, no reverse P/Invoke, no exceptions | [Kevin Gosse, 2023](https://minidump.net/suppressgctransition-b9a8a774edbd/) |
| Old x64 LuaJIT heap limit vs GC64 | 2 GB (31-bit) vs 128 TB (47-bit) | [OpenResty](https://blog.openresty.com/en/luajit-gc64-mode/) |

I found no published .NET 8/9/10 benchmark of LuaJIT-via-P/Invoke, nor a Lua-CSharp vs LuaJIT comparison; hence the measurements below.

### 6.2 My measurements **[M]** (Apple M4 Pro, macOS 27.0.1, .NET 10.0.0 osx-arm64, LuaJIT 2.1 GC64 arm64; Stopwatch/clock_gettime loops, 20–200 M iterations, stable to ±5% across runs)

| Operation | C host | .NET 10 host |
|---|---|---|
| `luaL_newstate + luaL_openlibs + lua_close` | 21.6–25.6 µs | 20.5–24.6 µs (one outlier run 70–138 µs) |
| Lua heap after `openlibs` | 35.9 KB | 35.8 KB |
| `luajit` CLI process start (fork+exec+empty chunk), max RSS | 2.84 ms median; 2.03 MB | — |
| `lua_gettop` call | 0.90–0.94 ns | `[LibraryImport]` 0.80–1.07 ns; `[DllImport]` 0.91–1.01 ns; `[SuppressGCTransition]` 0.67 ns |
| `lua_pushnumber + lua_tonumber + lua_settop` (3 calls) | 3.30 ns | 3.56 ns |
| `lua_getfield(GLOBALS,"noop") + lua_pcall` | 13.9–15.5 ns | 38–41 ns (UTF-8 marshalling of the name) |
| Lua→host `noop()` callback, JIT on / off | 5.27 / 3.66 ns | 6.8–7.0 / 4.8–5.0 ns (`[UnmanagedCallersOnly]`) |
| Lua→host `add1(x)` (read 1 number, push 1), JIT on / off | 10.5 / 9.4 ns | 13.5–14.9 / 12.0–12.7 ns |
| Pure Lua loop `x = x + i % 7`, JIT on / off | 0.99 / 2.65 ns per iter | 0.88–0.91 / 2.54 ns per iter |
| Library size | dylib 527 KB (Homebrew) / 626 KB (own build); `.a` 889 KB | — |

Reading **[I]**: raw managed↔native transitions on this platform/runtime are at the noise floor (≲1–2 ns), reverse P/Invoke adds ≈1.5 ns, string marshalling adds ≈12 ns per string; calling a host function from a JIT trace is *slower* than from the interpreter (same in C — C calls from traces are not inlined). Against Lua-CSharp's own figure of ≈320 ns per C# call, LuaJIT + `[UnmanagedCallersOnly]` is roughly 40–50× cheaper per call before argument marshalling, and pure-Lua code is interpreted ~3× slower than LuaJIT's JIT even in LuaJIT's own interpreter — pure-C# interpreters sit well behind that. Reproduce on Windows x64/Linux x64 with BenchmarkDotNet before quoting these numbers anywhere.

---

## 7. Native-library loading from a mod folder in a custom AssemblyLoadContext

### 7.1 What the runtime does **[V]**

- P/Invoke load order for an assembly: the *active* ALC is "the one with the assembly that defines the PInvoke"; it tries (1) its cache, (2) the `DllImportResolver` set by `NativeLibrary.SetDllImportResolver`, (3) `AssemblyLoadContext.LoadUnmanagedDll` on that ALC, (4) the AppDomain cache and default probing, (5) the `AssemblyLoadContext.ResolvingUnmanagedDll` event ([loading-unmanaged](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/loading-unmanaged)).
- Default probing: `NATIVE_DLL_SEARCH_DIRECTORIES` (populated from the **app's** `*.deps.json`, including its `runtimes/<rid>/native` assets), then the calling assembly's directory if `DllImportSearchPath.AssemblyDirectory` applies, then the bare name ([default-probing](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/default-probing)). Name variations: Windows tries `name`, `name.dll`; Linux/macOS try `name.so|.dylib`, `libname.so|.dylib`, `name`, `libname`; a Linux name containing `.so.` (e.g. `luajit-5.1.so.2`) is tried as-is, with `lib` prefix, then with `.so` appended; absolute paths are used as-is ([native-library-loading](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading)).
- `NativeLibrary.SetDllImportResolver(assembly, resolver)`: "the first attempt to resolve native library loads initiated by this assembly"; "register the resolver for their own assemblies only"; "Only one resolver can be registered per assembly" (second registration throws) ([SetDllImportResolver](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.setdllimportresolver?view=net-10.0)). `NativeLibrary.Load(string)` "wraps the OS loader and uses default flags" (no probing); `Load(name, assembly, DllImportSearchPath?)` applies probing, "does not invoke the resolver registered using SetDllImportResolver", and since .NET 5 does invoke `AssemblyLoadContext.LoadUnmanagedDll` and `ResolvingUnmanagedDll` ([Load](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.load?view=net-10.0)). `TryGetExport(handle, name, out addr)` / `GetExport` resolve symbols; `Free(handle)` "Frees a loaded library with the specified OS handle" ([NativeLibrary](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary?view=net-10.0), [Free](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.free?view=net-10.0)). `LoadUnmanagedDll` default returns `IntPtr.Zero` → runtime default policy; override it "to customize the unmanaged library search algorithm" ([LoadUnmanagedDll](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext.loadunmanageddll?view=net-10.0)).
- `DllImportSearchPath` flags: `AssemblyDirectory` (2) "include the directory that contains the assembly itself, and search that directory first", `ApplicationDirectory` (512), `SafeDirectories` (4096), `LegacyBehavior` (0) — "Operating systems that do not support the DefaultDllImportSearchPathsAttribute attribute use this value" ([DllImportSearchPath](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.dllimportsearchpath?view=net-10.0)).
- `RuntimeInformation.RuntimeIdentifier` returns e.g. `win-x64`, `linux-x64`; distro-built runtimes "may return more specific RIDs like `ubuntu.24.10-x64`"; "it's not recommended to parse the string" ([RuntimeIdentifier](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.runtimeinformation.runtimeidentifier?view=net-10.0)).
- NuGet convention `runtimes/{rid}/native/` is for packages; the SDK copies and "flattens any directory structure under runtimes/{rid}/native/" into the output, and RID selection comes from deps.json ([native files in packages](https://learn.microsoft.com/en-us/nuget/create-packages/native-files-in-net-packages)).
- `[LibraryImport]` (.NET 7+) generates marshalling at compile time, "removing the need for the generation of an IL stub at runtime"; `StringMarshalling.Utf8` is first-class; project needs `AllowUnsafeBlocks` ([P/Invoke source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation)).
- Windows CRT: `msvcbuild` dynamic builds use `/MD` ([msvcbuild.bat](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/msvcbuild.bat)) = "DLL-specific version of the runtime library" (`MSVCRT.lib` import), `/MT` = "multithread, static version" ([/MD, /MT](https://learn.microsoft.com/en-us/cpp/build/reference/md-mt-ld-use-run-time-library)). Modern .NET on Windows 10/11 lists no VC++ redistributable prerequisite; only Windows 7/8.1/Server 2012 need "Microsoft Visual C++ 2015-2019 Redistributable", and a missing `api-ms-win-crt-runtime-l1-1-0.dll` is the symptom of the UCRT being absent ([Install .NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)).
- Collectible ALC unload is cooperative: it finishes only when no thread has frames from those assemblies and nothing (including strong/pinned GC handles) references them; "Threads running code from an assembly loaded into the collectible AssemblyLoadContext" and "Pending RegisteredWaitHandle instances" block it; use the `Unloading` event to "stop threads or clean up strong GC handles" ([unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)).

### 7.2 Layout and loading recipe **[I]**

```
<GameMods>/MyMod/
  MyMod.dll
  runtimes/win-x64/native/lua51.dll
  runtimes/linux-x64/native/libluajit-5.1.so.2
  runtimes/osx-arm64/native/libluajit-5.1.2.dylib
  scripts/.luarc.json, scripts/api/host.d.lua, scripts/*.lua
```

```csharp
internal static partial class LuaNative
{
    static nint _lib;
    [ModuleInitializer]                         // runs before any P/Invoke in this assembly is bound
    internal static void Init() =>
        NativeLibrary.SetDllImportResolver(typeof(LuaNative).Assembly, (name, asm, path) =>
            name == "lua51" ? (_lib != 0 ? _lib : _lib = NativeLibrary.Load(Locate())) : IntPtr.Zero);

    static string Locate()
    {
        string dir = Path.GetDirectoryName(typeof(LuaNative).Assembly.Location)!;
        string rid = RuntimeInformation.RuntimeIdentifier;                   // "win-x64", "linux-x64", "osx-arm64", or distro-specific
        string file = OperatingSystem.IsWindows() ? "lua51.dll"
                    : OperatingSystem.IsMacOS()   ? "libluajit-5.1.2.dylib" : "libluajit-5.1.so.2";
        string p = Path.Combine(dir, "runtimes", rid, "native", file);
        if (!File.Exists(p)) {                                                // fall back to os-arch if the RID is distro-specific
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            string arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();   // x64 / arm64
            p = Path.Combine(dir, "runtimes", $"{os}-{arch}", "native", file);
        }
        return p;                                                            // absolute path: no name variations, no probing
    }

    [LibraryImport("lua51")] internal static partial nint luaL_newstate();
    [LibraryImport("lua51", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int luaL_loadbufferx(nint L, ReadOnlySpan<byte> buff, nuint sz, string name, string mode);
    // ...
}
```

Why this and not the defaults:
- The mod folder is not in the app's deps.json, so `runtimes/<rid>/native` probing does not apply to it; `DllImportSearchPath.AssemblyDirectory` would find a file *next to* `MyMod.dll`, but you need different file names per OS anyway, so a resolver (or fully manual `NativeLibrary.Load` + `GetExport` into `delegate* unmanaged` fields, which avoids `DllImport` entirely and makes the lifetime explicit) is cleaner.
- Register the resolver in a `[ModuleInitializer]`: any `[SuppressGCTransition]` P/Invoke is bound when its *caller* is JIT-compiled ("can cause the P/Invoke export to be bound earlier as a side-effect of the calling method being JIT compiled" — [SuppressGCTransitionAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.suppressgctransitionattribute?view=net-10.0)); I hit exactly this (`DllNotFoundException` before `Main` ran) **[M]**.
- Each mod reload creates a new `Assembly` object in a new ALC, so "one resolver per assembly" does not collide across reloads. In the runtime source the registry is `private static ConditionalWeakTable<Assembly, DllImportResolver>? s_nativeDllResolveMap;` and a duplicate registration throws `InvalidOperationException` (the API docs list `ArgumentException`; the code throws `InvalidOperationException`) ([NativeLibrary.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/NativeLibrary.cs)) **[V]** — a `ConditionalWeakTable` keyed by the assembly does not root it, so registering a resolver does not by itself block ALC unload **[I]**.
- If the host's mod loader lets you subclass the ALC, overriding `LoadUnmanagedDll` is equivalent and also covers `NativeLibrary.Load(name, assembly, searchPath)` callers ([Load](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.load?view=net-10.0)).

### 7.3 Platform notes **[V]+[I]**

- **Linux:** the `.so` depends only on `libc`, `libm`, `libdl` ([Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile)) — no system `liblua` involved; build on the oldest glibc you intend to support (or in a manylinux-style container) because glibc symbol versions are the only portability risk; keep the file name `libluajit-5.1.so.2` (soname) but load it by absolute path. Deployment target of the game's own runtime does not matter to `dlopen`.
- **Windows:** the stock `lua51.dll` (`/MD`) imports `VCRUNTIME140.dll` and the UCRT `api-ms-win-crt-*` forwarders; UCRT is part of Windows 10+, `VCRUNTIME140.dll` is not guaranteed. Either ship `vcruntime140.dll` beside it (allowed by the VC redist terms), or build with a static CRT: change `LJDYNBUILD=/DLUA_BUILD_AS_DLL /MD` to `/MT` in `msvcbuild.bat` (and `/MTd` for debug) — `msvcbuild static`/`mixed` already omit `/MD` and so use the static CRT, but they produce a `.lib`, not a DLL, which is only useful if you build your own shim DLL that links it (a good place for the `host_shim.c` functions from 3.7). Verify with `dumpbin /dependents lua51.dll` — a static-CRT build lists only `KERNEL32.dll`. Also: a hand-rolled native shim DLL that *links* `lua51.lib` must then be loaded with `DllImportSearchPath.UseDllDirectoryForDependencies`/`AssemblyDirectory` or `LoadLibraryEx(LOAD_WITH_ALTERED_SEARCH_PATH)` semantics so it finds `lua51.dll` in the same folder; simplest is to put the shim functions *inside* the LuaJIT build (`amalg`) so there is exactly one native file per RID.
- **macOS:** the dylib's install name is `$(PREFIX)/lib/libluajit-5.1.2.dylib` (`/usr/local/lib/...` by default, [Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile)); irrelevant when loaded by absolute path, but anything that *links* against it needs `install_name_tool -id @rpath/...`. Unsigned dylibs load into `dotnet` because it has `disable-library-validation`; a notarised game may need the dylib ad-hoc signed. See 1.11 for the JIT/hardened-runtime observations.

### 7.4 Unloading **[V]+[I]**

- `NativeLibrary.Free(handle)` is a no-op for `IntPtr.Zero` and otherwise calls the runtime's `FreeLib` ([NativeLibrary.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/NativeLibrary.cs)) **[V]**, i.e. `FreeLibrary`/`dlclose` reference counting **[I]**; nothing in the docs says the runtime frees libraries loaded through a resolver or `LoadUnmanagedDll` when a collectible ALC unloads, and I found no runtime issue documenting it either way — assume it does **not**. Reloading the same path returns the same OS handle (refcount++), so a mod reload that simply calls `NativeLibrary.Load` again is harmless.
- Risks of actually freeing: live `lua_State`s, installed hooks, and any `[UnmanagedCallersOnly]` function pointers held by native code become dangling → crash. LuaJIT keeps per-state data only, so there is no benefit to unloading between reloads. **Recommendation:** load once per process, never `Free`; on ALC unload, `lua_close` all states first (so no native code can call back into the dying ALC), and keep the native handle in a static of the mod's *own* assembly (not the default ALC) only if the game's loader does not itself keep mods pinned — if the loader never truly unloads (common), none of this matters in practice.

---

## Appendix A — measurement details **[M]**

- Machine: Apple M4 Pro, macOS 27.0.1 (Darwin 27.0.0), Apple clang 21.0.0, .NET SDK 10.0.100 / runtime 10.0.0 (`osx-arm64`).
- LuaJIT builds: Homebrew bottle `2.1.1788856981` (`/opt/homebrew/lib/libluajit-5.1.2.dylib`, minos 26.0/SDK 26.5, GC64, `jit.status()` true, `ffi.abi("gc64")` true); local builds of `v2.1` commit `2a8e017` (2026-10-14) and `c6ffc14` (= 1788856981), `make amalg` and plain `make`, `MACOSX_DEPLOYMENT_TARGET` 14.0 and 27.0, plus `XCFLAGS=-DLUAJIT_ENABLE_OSX_HRT` after fixing `lj_mcode.c:156` (`return 0;` → `return;`).
- Harnesses: C baseline `/private/tmp/claude-501/-Users-asherwin-repos-meow-sci-ksa-scripting/045e475f-e542-4b8d-95ac-266a2876d608/scratchpad/ljbench-c/bench.c` and `bench2.c`; .NET harness `/private/tmp/claude-501/-Users-asherwin-repos-meow-sci-ksa-scripting/045e475f-e542-4b8d-95ac-266a2876d608/scratchpad/ljbench-cs/Program.cs` (`[LibraryImport]`, `[DllImport]`, `[UnmanagedCallersOnly]`, `luaJIT_setmode`) and `/private/tmp/claude-501/-Users-asherwin-repos-meow-sci-ksa-scripting/045e475f-e542-4b8d-95ac-266a2876d608/scratchpad/ljbench-cs2/Program.cs` (`SuppressGCTransition`, scaling check 20 M vs 200 M iterations). Built libraries are under `.../scratchpad/dl/lj-default`, `lj-hrt`, `lj-osver`, `lj-old`; LuaJIT sources under `.../scratchpad/dl/luajit-src` and `luajit-old`.
- Method: `Stopwatch`/`clock_gettime(CLOCK_MONOTONIC)` around 2–200 M iteration loops after a 0.1–1 M warm-up; numbers varied ≤10% across 6 runs; the SIGKILL reproduced 3/3 times with the Homebrew bottle and 0/9 with locally built libraries.

## Appendix B — source index

LuaJIT: [status](https://luajit.org/status.html) · [download](https://luajit.org/download.html) · [install](https://luajit.org/install.html) · [extensions](https://luajit.org/extensions.html) · [ext_jit](https://luajit.org/ext_jit.html) · [ext_c_api](https://luajit.org/ext_c_api.html) · [ext_ffi](https://luajit.org/ext_ffi.html) · [ext_ffi_semantics](https://luajit.org/ext_ffi_semantics.html) · [running](https://luajit.org/running.html) · [GitHub](https://github.com/LuaJIT/LuaJIT) · [commits v2.1](https://github.com/LuaJIT/LuaJIT/commits/v2.1) · [luajit_rolling.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/luajit_rolling.h) · [lua.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lua.h) · [lauxlib.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lauxlib.h) · [luaconf.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/luaconf.h) · [Makefile](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/Makefile) · [msvcbuild.bat](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/msvcbuild.bat) · [lib_aux.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lib_aux.c) · [lj_state.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_state.c) · [lj_api.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_api.c) · [lj_dispatch.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_dispatch.c) · [lj_record.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_record.c) · [lj_trace.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_trace.c) · [lj_mcode.c](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_mcode.c) · [lj_def.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_def.h) · [lj_obj.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_obj.h) · [lj_errmsg.h](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_errmsg.h) · [issue #531](https://github.com/LuaJIT/LuaJIT/issues/531) · [issue #1393](https://github.com/LuaJIT/LuaJIT/issues/1393) · [hooks vs JIT (2012)](https://luajit.freelists.narkive.com/lexU9r0S/debug-hooks-and-jit) · [yield from hook (2011)](https://lua-l.lua.narkive.com/T9ORJwOv/coroutine-yield-from-debug-hook-in-luajit-2) · [OpenResty GC64](https://blog.openresty.com/en/luajit-gc64-mode/) · [openresty/luajit2](https://github.com/openresty/luajit2) · [Homebrew luajit.rb](https://github.com/Homebrew/homebrew-core/blob/master/Formula/l/luajit.rb) · [Lua 5.1 manual](https://www.lua.org/manual/5.1/manual.html) · [lua-users ThreadsTutorial](http://lua-users.org/wiki/ThreadsTutorial) · [lua-users SandBoxes](http://lua-users.org/wiki/SandBoxes) · [kong-lua-sandbox](https://github.com/kong/kong-lua-sandbox/blob/master/kong-lua-sandbox.lua) · [HackTricks Lua sandbox](https://hacktricks.wiki/en/generic-methodologies-and-resources/lua/bypass-lua-sandboxes/index.html).

.NET: [NativeLibrary](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary?view=net-10.0) · [SetDllImportResolver](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.setdllimportresolver?view=net-10.0) · [Load](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.load?view=net-10.0) · [Free](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.free?view=net-10.0) · [DllImportSearchPath](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.dllimportsearchpath?view=net-10.0) · [loading-unmanaged](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/loading-unmanaged) · [default-probing](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/default-probing) · [native-library-loading](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading) · [LoadUnmanagedDll](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext.loadunmanageddll?view=net-10.0) · [RuntimeIdentifier](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.runtimeinformation.runtimeidentifier?view=net-10.0) · [unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability) · [exceptions-interoperability](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/exceptions-interoperability) · [PR #111259](https://github.com/dotnet/runtime/pull/111259) · [UnmanagedCallersOnlyAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute?view=net-10.0) · [GetFunctionPointerForDelegate](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getfunctionpointerfordelegate?view=net-10.0) · [P/Invoke source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation) · [unsafe code / function pointers](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code) · [SuppressGCTransitionAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.suppressgctransitionattribute?view=net-10.0) · [Object.Finalize](https://learn.microsoft.com/en-us/dotnet/api/system.object.finalize?view=net-10.0) · [SafeHandle](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.safehandle?view=net-10.0) · [Install .NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows) · [/MD, /MT](https://learn.microsoft.com/en-us/cpp/build/reference/md-mt-ld-use-run-time-library) · [native files in packages](https://learn.microsoft.com/en-us/nuget/create-packages/native-files-in-net-packages) · [announcing .NET 10](https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/) · [coreclr PR #26458](https://github.com/dotnet/coreclr/pull/26458) · [SuppressGCTransition deep dive](https://minidump.net/suppressgctransition-b9a8a774edbd/).

Bindings: see Section 2.1 source list. Tooling: [LuaLS settings](https://luals.github.io/wiki/settings/) · [annotations](https://luals.github.io/wiki/annotations/) · [definition files](https://luals.github.io/wiki/definition-files/) · [configuration](https://luals.github.io/wiki/configuration/) · [LuaLS releases](https://github.com/LuaLS/lua-language-server/releases) · [nvim-lspconfig lua_ls](https://github.com/neovim/nvim-lspconfig/blob/master/lsp/lua_ls.lua) · [luau.org](https://luau.org/) · [Luau sandbox](https://luau.org/sandbox) · [luau repo](https://github.com/luau-lang/luau) · [Teal](https://github.com/teal-language/tl).
