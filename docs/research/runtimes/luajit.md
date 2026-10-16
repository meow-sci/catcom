---
type: Runtime Evaluation
title: LuaJIT 2.1 via direct P/Invoke
description: Evaluation of embedding LuaJIT in the KSA mod - 2026 status (rolling v2.1, no official binaries, build it yourself per RID), the Lua 5.1 API and enabled extensions, GC64, FFI removal, JIT-versus-hooks behaviour, the hard rule that Lua errors cannot unwind through .NET frames (and the two workarounds), embedding patterns, measured call overheads, native loading, and platform notes including a macOS JIT SIGKILL observed with the Homebrew bottle.
resource: https://luajit.org
tags: [runtime, lua, luajit, pinvoke, native]
sources:
  - id: report
    resource: /references/lua-runtimes-report-2026-10-16.md
    title: Lua/LuaJIT embedding research report (2026-10-16)
  - id: status
    resource: https://luajit.org/status.html
    title: LuaJIT status
  - id: download
    resource: https://luajit.org/download.html
    title: LuaJIT download (no binaries policy)
  - id: install
    resource: https://luajit.org/install.html
    title: LuaJIT install
  - id: extensions
    resource: https://luajit.org/extensions.html
    title: LuaJIT extensions (5.2/5.3 features, C++ exception interoperability, resumable VM)
  - id: ffi
    resource: https://luajit.org/ext_ffi_semantics.html
    title: LuaJIT FFI semantics (not safe for untrusted code)
  - id: hooks
    resource: https://luajit.freelists.narkive.com/lexU9r0S/debug-hooks-and-jit
    title: Mike Pall on debug hooks and the JIT
  - id: eh
    resource: https://learn.microsoft.com/en-us/dotnet/standard/native-interop/exceptions-interoperability
    title: .NET exceptions interoperability (no longjmp across managed frames)
  - id: pr111259
    resource: https://github.com/dotnet/runtime/pull/111259
    title: dotnet/runtime PR #111259 (longjmp over managed frames is unsupported)
  - id: keralua
    resource: https://github.com/NLua/KeraLua/blob/main/src/NativeMethods.cs
    title: KeraLua NativeMethods (Lua 5.4 imports)
  - id: spike
    resource: /spikes/luajit-pinvoke-spike.md
    title: LuaJIT P/Invoke spike
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T22:00:00Z }
status: stable
---

# Status (2026-10-16)

- LuaJIT is "actively developed and maintained": the **v2.1 branch is the production branch** with rolling releases; commits by Mike Pall landed on 2026-10-13/14; MIT. **No release tarballs or binaries exist**, and the maintainer tells you not to use third-party binaries: build from git per RID.[^status][^download]
- Artifacts: Windows `msvcbuild` → `lua51.dll` (keep the name); Linux `libluajit-5.1.so.2` (links only `-lm -ldl`); macOS `libluajit-5.1.2.dylib` (needs `MACOSX_DEPLOYMENT_TARGET`). Library size ≈ 0.5–0.7 MB.[^install][^report]
- **GC64** is the default on all 64-bit ports (47-bit, 128 TB); `lua_newstate` with a custom allocator exists under GC64 but Mike Pall calls custom allocators debug-only and plans to ignore them in v3.0; prefer `lua_gc(L, LUA_GCCOUNT, 0)` polling for memory budgets.[^report]
- API: Lua 5.1 (`LUA_GLOBALSINDEX = -10002`, `lua_resume(L, narg)`, `lua_yield(L, n)`) plus 5.2/5.3 extensions (`goto`, `load` with mode `"t"` to reject bytecode, `xpcall` extra args, `table.move`, `\u{}`), and C-API extras `luaL_traceback`, `lua_tointegerx`, `luaL_loadbufferx`, `luaJIT_setmode`. **The VM is fully resumable**: coroutines can yield across `pcall`, iterators and metamethods, which makes "await from a host function" possible.[^extensions]
- **FFI** "is not safe for use by untrusted Lua code"; build with `-DLUAJIT_DISABLE_FFI` for a sandbox (also removes boxed 64-bit integers).[^ffi]

# The two hard rules

1. **Lua errors must never unwind through a managed frame.** LuaJIT on x64/arm64 unwinds with the platform C++ unwinder / SEH; .NET does not support `longjmp`/SEH crossing managed frames (the runtime's own fix is Windows-only and best-effort). The spike reproduced the panic. Consequences: from C# only call `lua_pcall`/`lua_resume`; inside `[UnmanagedCallersOnly]` host functions call only non-raising API functions (`lua_type`, `lua_tonumberx`, `lua_tolstring` on strings, `lua_raw*`, `lua_push*`, `lua_checkstack`; never `luaL_check*`, `luaL_error`, `lua_error`, `lua_call`, `lua_getfield` on values with metamethods); wrap every callback body in try/catch and return `(false, msg)`; raise from **Lua-side wrappers** (`local ok, a = raw(...) if not ok then error(a, 2) end`) or from a **~30-line native C shim compiled into the LuaJIT build** (`host_timeout_hook` + `host_raise`). NuLua ships exactly such protected shims (`nulua_pgetfield`, …).[^eh][^pr111259][^spike][^report]
2. **Hooks are not called from compiled traces** unless the build has `-DLUAJIT_ENABLE_CHECKHOOK` (speed penalty) or the JIT is off; `lua_sethook` is the only API that may be called from another thread (the watchdog). A `while true do end` inside a hot trace is otherwise unkillable. **Build with CHECKHOOK, or run untrusted scripts with `jit.off()`** (the interpreter is still ≈3× slower than JIT on arithmetic and equal on host-call-heavy code).[^hooks][^report]

# Bindings verdict

| Library | Lua flavour | LuaJIT-usable? |
|---|---|---|
| KeraLua 1.4.10 / NLua 1.7.9 | 5.4; 24 of 137 imports are 5.2+/5.4-only (`lua_pcallk`, `lua_rotate`, 4-arg `lua_resume`, …) | **no** (EntryPointNotFound on first use)[^keralua] |
| NuLua.LuaJit 0.1.0 (2026-06) | 5.1–5.5, LuaJIT, Luau; csbindgen + C shims; per-RID runtime packages | preview; borrow its ideas |
| Lua.NET 6.0.1 (2025-06) / JAJ fork 1.1.0 | includes LuaJIT; delegate-based; opaque binaries (header says 2.1.0-beta3) | stale; signature reference only |
| Lua-CSharp 0.5.7, MoonSharp 2.0 / 3.0-beta.1, NeoLua, Laylua (LGPL), WattleScript | pure C# interpreters (Lua 5.2/5.3) | not LuaJIT; see [Lua alternatives](/runtimes/lua-alternatives.md) |

**Recommendation: hand-written `[LibraryImport]` bindings (~60 Lua 5.1 functions) + `delegate* unmanaged[Cdecl]` / `[UnmanagedCallersOnly]` host functions against our own LuaJIT build** (GC64, `-DLUAJIT_DISABLE_FFI`, `-DLUAJIT_ENABLE_CHECKHOOK`, static CRT on Windows, amalgamation with the C shim). The spike used exactly this shape.[^report][^spike]

# Embedding patterns

- One `lua_State` on the game thread (`luaL_newstate` + `luaL_openlibs`; ≈22 µs, 36 KB heap), never touched from other threads; full-state recreation is the simplest reload/reset.
- Host handles as full userdata holding a host-registry id (never a managed address) with a metatable (`__index` methods, `__gc` to release); hot telemetry as tables refreshed per tick; Lua callbacks held via `luaL_ref` and `luaL_unref`'d on unload.
- Calls into Lua: `lua_pcall` with a traceback message handler (`luaL_traceback` from a shim, or `debug.traceback` captured before `debug` is removed); cache registry refs instead of `lua_getfield` by name in hot paths (string marshalling ≈12 ns per string).
- Async: per-script coroutine thread (`lua_newthread`, registry ref); a host function returns `lua_yield(L, n)` (regular yield path does not unwind, so it is safe from managed code when reached via `lua_resume`); the scheduler `lua_resume`s with results later. Verified in the spike.
- Sandbox (5.1 `setfenv`): fresh env table with safe builtins, `math`, `string` minus `dump`, `table`, `bit`, `coroutine`, `os.clock/date/time` only; no `io`, `package`, `require` (custom scripts-folder loader with `[%w_%.]+` names and mode `"t"`), `debug`, `jit` (maybe status), `load*`, `setfenv/getfenv`, `newproxy`; `print` replaced by the host logger; `env._G = env`; `lua_setfenv` on each loaded chunk.
- Timeout: watchdog thread arms `lua_sethook(L, shim_hook, LUA_MASKCOUNT, 1)` after the budget; the shim raises "script timed out"; `lua_pcall` restores hook state on error.
- Memory: poll `lua_gc(LUA_GCCOUNT)` per frame / in the hook; on breach raise and recreate the state.[^report]

# Measured

| Operation | .NET 10 host (M4 Pro) |
|---|---|
| `[LibraryImport]` call into LuaJIT (`lua_gettop`) | 0.8–1.1 ns (same as C) |
| Lua→C# `[UnmanagedCallersOnly]` callback | 5–7 ns (JIT on), 5 ns (off) |
| `lua_getfield(GLOBALS,"f") + lua_pcall` | 38–41 ns (15 ns from C; string marshalling) |
| Pure Lua loop per iteration, JIT on / off | 0.9 / 2.5 ns |
| State create + openlibs + close | 21–25 µs |
| Spike: 1M Lua→C# calls / 1M C#→Lua pcalls / 50M-iteration loop | 28 ms / 72 ms / 45 ms |

Against Lua-CSharp's own ≈320 ns per C# call, LuaJIT is roughly 40–50× cheaper per host call.[^report][^spike]

# Platform notes

- **Windows:** the default `msvcbuild` is `/MD` (imports `VCRUNTIME140.dll`); switch `LJDYNBUILD` to `/MT` for a static CRT so the DLL depends only on `KERNEL32` (verify with `dumpbin /dependents`). Pass `-DLUAJIT_DISABLE_FFI -DLUAJIT_ENABLE_CHECKHOOK` by editing `LJCOMPILE` in the batch file.
- **Linux:** build on the oldest glibc you support (manylinux-style container); keep the soname file name, load by absolute path.
- **macOS arm64:** the research agent observed the **Homebrew bottle SIGKILL the .NET host when the first trace compiled** (3/3 runs), while the same commit built locally ran JIT-on (0/9 failures). The LuaJIT spike in this repo, on the same machine, ran a 50M-iteration JIT-compiled loop with the Homebrew bottle without incident, so the trigger is not understood (entitlements, SDK version, or harness differences are candidates). The actionable rule is the same either way: **build LuaJIT yourself for every RID, test JIT-on inside the real host process, keep a `jit.off()` kill switch, and log `jit.status()` at startup** (the JIT also silently disables itself if the first mcode allocation fails). `-DLUAJIT_ENABLE_OSX_HRT` (MAP_JIT for hardened-runtime hosts) does not compile at tip without a one-line fix in `lj_mcode.c`.[^report][^spike]
- **Loading:** `[ModuleInitializer]` + `SetDllImportResolver` (bind-time matters for `SuppressGCTransition` imports), `NativeLibrary.Load(absolutePath)` from `<mod>/runtimes/<rid>/native/`, with an os-arch fallback when the RID is distro-specific; never `NativeLibrary.Free`; `lua_close` all states before any unload.

# Tooling for authors

LuaLS 3.19.2 with `runtime.version = "LuaJIT"`, a generated LuaCATS definition file (`---@class`, `---@field`, `---@param`) for the host API, and a `.luarc.json` in the scripts folder; see [neovim shipping](/editor/neovim-shipping.md). Typed-Lua alternatives (Luau, Teal) were noted but not pursued: Luau is a different VM (no LuaJIT), Teal compiles to Lua and would add a build step.

[^report]: Lua/LuaJIT embedding research report (2026-10-16)
[^status]: LuaJIT status
[^download]: LuaJIT download (no binaries policy)
[^install]: LuaJIT install
[^extensions]: LuaJIT extensions (5.2/5.3 features, C++ exception interoperability, resumable VM)
[^ffi]: LuaJIT FFI semantics (not safe for untrusted code)
[^hooks]: Mike Pall on debug hooks and the JIT
[^eh]: .NET exceptions interoperability (no longjmp across managed frames)
[^pr111259]: dotnet/runtime PR #111259 (longjmp over managed frames is unsupported)
[^keralua]: KeraLua NativeMethods (Lua 5.4 imports)
[^spike]: LuaJIT P/Invoke spike
