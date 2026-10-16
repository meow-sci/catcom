# Plan 01 — LuaJIT runtime (direct P/Invoke to a self-built LuaJIT 2.1)

Status: proposed · Depends on: [Plan 00](00-shared-foundation-plan.md) · Research: [LuaJIT](../docs/research/runtimes/luajit.md), [Lua alternatives](../docs/research/runtimes/lua-alternatives.md), [LuaJIT spike](../docs/research/spikes/luajit-pinvoke-spike.md), [purrTTY native loading pattern](../docs/research/existing-mods/purrtty-native-library-loading.md), [sandboxing](../docs/research/architecture/sandboxing-limits-and-safety.md)

## 1. Goal

`KsaScripting.Runtime.Lua` implements `IScriptRuntime` on LuaJIT 2.1 with: a sandboxed per-script environment, host API bound from `ApiSchema`, coroutine-based awaits, a watchdog that can stop runaway scripts, memory accounting, tracebacks mapped to `scripts/<file>.lua:<line>`, and a Lua REPL. Users get LuaLS completion from the generated LuaCATS stub.

## 2. Native build pipeline (`tools/build-luajit.*` + CI)

LuaJIT ships no binaries; build per RID from the `v2.1` branch at a pinned commit (record it in `tools/luajit.pin`).

| RID | Build | Flags | Output |
|---|---|---|---|
| win-x64 | VS developer prompt, `msvcbuild.bat` with `LJCOMPILE` extended | `/DLUAJIT_DISABLE_FFI /DLUAJIT_ENABLE_CHECKHOOK`, `LJDYNBUILD` changed from `/MD` to `/MT` (static CRT) | `lua51.dll` (depends only on KERNEL32; verify with `dumpbin /dependents`) |
| linux-x64 | `make amalg` in a manylinux-style container (oldest supported glibc) | `XCFLAGS="-DLUAJIT_DISABLE_FFI -DLUAJIT_ENABLE_CHECKHOOK"` | `libluajit-5.1.so.2` (deps: libc, libm, libdl) |
| osx-arm64 (nice to have) | `make amalg` with `MACOSX_DEPLOYMENT_TARGET=11.0` | same + `-DLUAJIT_ENABLE_OSX_HRT` after the one-line `lj_mcode.c` fix | `libluajit-5.1.2.dylib` |

Add `host_shim.c` to the amalgamation (one native file per RID, no secondary DLL search problems):

```c
#include "lua.h"
#include "lauxlib.h"
/* Count hook armed by the watchdog thread via lua_sethook(L, host_timeout_hook, LUA_MASKCOUNT, 1). */
void host_timeout_hook(lua_State *L, lua_Debug *ar) { (void)ar; lua_sethook(L, NULL, 0, 0); luaL_error(L, "script timed out"); }
/* Raise the value on top of the stack as an error from a native frame (managed code must never call lua_error). */
int host_raise(lua_State *L) { return lua_error(L); }
/* Traceback message handler for lua_pcall. */
int host_traceback(lua_State *L) { const char *m = lua_tostring(L, 1); luaL_traceback(L, L, m, 1); return 1; }
```

Export these three symbols from the library (`__declspec(dllexport)` on Windows via `LUA_API`). CI builds all RIDs, runs `luajit -e "print(jit.status())"` and the binding tests, and uploads artifacts consumed by the mod build (`runtimes/<rid>/native/`).

**Verification gate:** run the spike's JIT-on loop inside the real game process on Windows and Linux before enabling the JIT by default (a macOS SIGKILL was observed once with a third-party bottle; see the research page). Config `lua_jit_enabled` defaults to true on win/linux after that gate, false until then; `jit.status()` is logged at start.

## 3. Binding assembly (`LuaNative`)

- `[LibraryImport("lua51")]` declarations for the ~60 Lua 5.1 functions used (`luaL_newstate`, `luaL_openlibs`, `lua_close`, `luaL_loadbufferx`, `lua_pcall`, `lua_resume`, `lua_yield`, `lua_status`, `lua_newthread`, `lua_gettop/settop/pushvalue/remove/insert`, `lua_type/typename`, `lua_tolstring/tonumberx/tointegerx/toboolean/touserdata`, `lua_pushnil/number/boolean/lstring/cclosure/lightuserdata`, `lua_newuserdata`, `luaL_newmetatable/luaL_setmetatable/luaL_testudata`, `lua_getmetatable/setmetatable`, `lua_rawget/rawset/rawgeti/rawseti`, `lua_createtable/next/rawequal`, `lua_setfenv/getfenv`, `luaL_ref/unref`, `lua_sethook`, `lua_gc`, `luaJIT_setmode`, plus the three shim exports). `StringMarshalling.Utf8` for convenience overloads; span-based hot paths.
- `[ModuleInitializer]` registering `NativeLibrary.SetDllImportResolver` that loads `<modDir>/runtimes/<rid>/native/<file>` by absolute path with an os-arch fallback (recipe in the research page); never `NativeLibrary.Free`.
- Host functions: `static int F(nint L)` with `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]`, pushed via `delegate* unmanaged[Cdecl]<nint,int>`. **Rules enforced by code review and an analyzer-style test:** body wrapped in try/catch; only non-raising API calls; errors returned as `(false, message)`; raising done by the Lua-side wrapper or `host_raise`.
- `LuaStateHandle : SafeHandle` whose release enqueues `lua_close` on the game thread.

## 4. Runtime adapter (`LuaRuntime : IScriptRuntime`)

- **One `lua_State` per runtime** (game thread only). Per script: an environment table (`__index` → shared sandbox env), a coroutine thread (`lua_newthread`, registry ref) for top-level code, and per-handler coroutines for async hooks.
- **Sandbox at state creation:** `luaL_openlibs`; capture `debug.traceback` into the registry; build `env` with the allowed builtins (`assert, error, ipairs, pairs, next, pcall, xpcall, select, tonumber, tostring, type, unpack, rawequal`, `math`, `string` minus `dump`, `table`, `bit`, `coroutine`, `os.clock/date/time/difftime`), `print` → host logger, `require` → scripts-folder loader (`[%w_%.]+`, mode `"t"`, per-sandbox `loaded`), `_G = env`; remove `io`, `os.*`, `package`, `debug`, `jit` (expose `jit.status` read-only), `load*`, `setfenv/getfenv`, `newproxy`, `collectgarbage` (allow `"count"`). FFI is compiled out.
- **Loading:** `luaL_loadbufferx(L, src, len, "@scripts/<file>.lua", "t")` → `lua_setfenv` → run as a coroutine under `lua_resume`; `ScriptLoadException` from syntax errors with file/line parsed from the message.
- **Host API binding from `ApiSchema`:** `ksa` table whose namespaces are tables of closures; handles (`VesselRef`, `EngineRef`, …) as full userdata holding a host-registry id with a shared metatable (`__index` to a methods table, `__gc` releasing the registry entry, `__tostring`, `__eq`). Hot reads (`v.position.cci`) return small tables; the per-step `snapshot()` returns a cached table refreshed once per step. Generated Lua-side wrappers convert `(false, msg)` into `error(msg, 2)`.
- **Async:** `ksa.sleep`, `ksa.next_step`, `ksa.until_`, `ksa.wait_event` and Solver-phase actions are host functions that register a wait with the scheduler and `return lua_yield(L, 0)`; the scheduler `lua_resume`s the thread with results when due. Top-level script code and all hooks run inside coroutines so yields always have a resume point. Sync hooks run on the main thread via `lua_pcall` of the handler in a fresh coroutine with yields disallowed (a yield attempt returns an error).
- **Watchdog:** on expiry the watchdog thread calls `lua_sethook(L, &host_timeout_hook, LUA_MASKCOUNT, 1)` (the only cross-thread-safe API); CHECKHOOK build makes it fire inside traces; `lua_pcall` restores hook state.
- **Memory:** poll `lua_gc(L, LUA_GCCOUNT, 0)` each Step slot; over budget → mark the script faulted, `collectgarbage`, and if still over, recreate the state (22 µs) and reload the other scripts.
- **Errors:** `lua_pcall`/`lua_resume` with `host_traceback`; message + traceback captured into `ScriptError(file, line, message, traceback)`; chunk names `@scripts/x.lua` make `scripts/x.lua:12:` jump-able in nvim.
- **Reload:** default full-state recreation (cheap and leak-proof); scripts' `state` tables round-trip through the sidecar.
- **Terminal scripts:** `ctx.term` is a table over `ksa_host.term_*`: `term.write(s)` pushes bytes to the host ring buffer; `term.read()` yields the coroutine until input bytes arrive (delivered as a Lua string); `term.on("resize"|"attach"|"detach"|"cancel", fn)`; `term.size` returns `{cols, rows}`; the shipped `lib/tui.lua` wraps these with escape-code helpers, an input parser (keys + SGR mouse) and small widgets. Reads and writes are game-thread only and go through the Step slot like every other host call.
- **Context objects:** every invocation receives a `ctx` table (`ctx.ksa`, `ctx.script`, `ctx.event`/`ctx.dt`/`ctx.signal`/`ctx.term`) built by the prelude from a host-provided map; exports are discovered from the chunk's returned table or `ksa.export()` calls and reported through `IScriptRuntime.Exports`.
- **REPL:** `LuaReplShell` evaluates `return <line>` first then `<line>`, in a scratch env inheriting the sandbox, printing results with a small pretty-printer.

## 5. Tooling

- `ksa.lua` LuaCATS stub generated from `ApiSchema` (`---@class ksa.Vessel`, `---@field position ksa.Position`, `---@param`/`---@return` on functions) and `.luarc.json` with `runtime.version = "LuaJIT"`, `workspace.library = ["stubs"]`, `diagnostics.globals = ["ksa"]`.
- nvim `lua_ls` config from the shared `init.lua` (Plan 00 Phase F).

## 6. Tests

- Binding tests (NUnit, run on CI runners with the built library): state lifecycle, pcall error → exception, host function error protocol, coroutine yield/resume, watchdog interrupt (JIT on and off), memory polling, sandbox escapes (`io`, `os.execute`, `require "ffi"`, `debug`, `setfenv`, bytecode load all fail).
- Script-level tests with the `NullHost` (fake `ApiSchema` implementation) covering event delivery, ticks, awaits, reload.
- In-game checklist: ignite/stage/attitude/burn/teleport from Lua; 10k host calls per step under budget; `while true do end` is stopped within the call budget.

## 7. Risks

| Risk | Mitigation |
|---|---|
| Error unwinding through managed frames (crash) | rules in §3; shim for all raises; review checklist; a test that deliberately violates the rule is **not** written (it would crash the test host) — document instead |
| JIT instability inside the game process on some platform | per-RID self-build, verification gate, `jit.off()` switch, startup log |
| Windows CRT dependency | static CRT build; `dumpbin` check in CI |
| Lua 5.1 dialect expectations (no integers, no 5.4 features) | document in the stub header; LuaLS runtime set to LuaJIT |
| Future LuaJIT v3 ignoring custom allocators | memory via `lua_gc` polling only |

## 8. Definition of done

All tests green on win-x64 and linux-x64 CI; in-game checklist passes; `jit.status()` true in game on both; REPL and nvim editing with LuaLS completion work; bundle `log.md` updated with the pinned LuaJIT commit.
