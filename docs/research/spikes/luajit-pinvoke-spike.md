---
type: Spike Result
title: LuaJIT direct P/Invoke spike
description: Validates the hand-written P/Invoke binding shape for LuaJIT 2.1 from .NET 10, including host callbacks, coroutine-driven await, sandboxing, error tracebacks, interrupt hooks, call-overhead timings, and the critical finding that Lua errors must never unwind through a managed frame.
resource: ../../../spikes/LuaJitSpike/Program.cs
tags: [spike, luajit, lua, pinvoke, measurement]
sources:
  - id: spike
    resource: ../../../spikes/LuaJitSpike/Program.cs
    title: LuaJitSpike source
  - id: luajit-ext
    resource: https://luajit.org/extensions.html
    title: LuaJIT extensions and C++ exception interoperability
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:30:00Z }
status: stable
---

# What was tested

`spikes/LuaJitSpike` binds **LuaJIT 2.1 (Homebrew build 2.1.1788856981, arm64)** directly with `[DllImport("luajit")]` declarations and a `NativeLibrary.SetDllImportResolver` that maps the logical name to an explicit library path. No binding library (KeraLua/NLua) is used; the Lua 5.1 C API is called as-is. Host functions are `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` static methods pushed with `lua_pushcclosure` via function pointers.[^spike]

# Results

| Step | Measured |
|---|---|
| `luaL_newstate` + `luaL_openlibs` | < 1 ms |
| 1,000,000 Lua → C# calls (`host_add(i, 1)` in a loop) | 28 ms |
| 1,000,000 C# → Lua `lua_pcall` round trips | 72 ms |
| 50,000,000-iteration arithmetic loop with JIT on | 45 ms |
| Coroutine "await": script calls a host function that `lua_yield`s; host resumes with a value | works; two yields, final result correct |
| Sandbox: `os`, `io`, `package`, `require`, `loadfile`, `dofile`, `ffi` set to nil | works |
| Runtime error with `debug.traceback` as the `lua_pcall` error handler | full traceback returned to C# |
| Interrupt **variant A**: C `lua_sethook` count hook implemented in C# calling `lua_error` | **PANIC: unprotected error in call to Lua API** |
| Interrupt **variant B**: hook installed from Lua (`debug.sethook(function() if host_should_interrupt()==1 then error(...) end end, '', 1000)`) with `jit.off()` | interrupted after 101 ms, 139,958 predicate calls |
| Variant B with `jit.on()` | hook never fires inside the compiled trace (thread still running after 3 s) |
| Lua heap after the run | 52 KiB |

# The critical finding: never raise Lua errors across managed frames

Variant A panics because LuaJIT 2.1 on 64-bit targets unwinds Lua errors with the platform's C++ unwinder (`_Unwind_RaiseException` on Linux/macOS, SEH on Windows) rather than `longjmp`.[^luajit-ext] A .NET managed frame (the `UnmanagedCallersOnly` hook or any host function) has no unwind tables the native unwinder can traverse, so the throw cannot find the enclosing `lua_pcall` frame and LuaJIT reports an unprotected error. On Windows the SEH path would attempt to unwind through the CLR's frames with equally undefined results.

Consequences for the binding design:

1. **Host functions must never call `lua_error`, `luaL_error`, `luaL_check*`, `luaL_argerror`, `lua_call`, `lua_concat` on bad types, `lua_getfield` on non-tables, or any other API that can throw.** They return `(nil, message)` and a tiny Lua-side shim converts that into `error()` from a Lua frame.
2. **Interrupts/watchdogs are Lua-side hooks** that poll a non-throwing host predicate (variant B). This works, costs about 140k predicate calls per 100 ms at count=1000, and the count can be raised to cut overhead.
3. **Count hooks do not fire inside JIT-compiled traces.** A watchdog therefore needs either `jit.off()` for untrusted scripts (the interpreter is still fast: the 1M host-call loop is interpreter-bound anyway) or a cooperative design where only the host-call boundary checks the deadline. The plan should default per-frame hooks to JIT on with a deadline check at every host call, and ad-hoc/long-running scripts to a coroutine model where the host regains control at each yield.
4. **Re-entrancy is fine as long as the inner call is protected:** C# → `lua_pcall` → Lua → C# host function → `lua_pcall` (inner) → Lua error is caught by the inner `lua_pcall` before reaching the managed frame.

# Other observations

- The `DllImportResolver` is invoked once per imported entry point, so it must be cheap and idempotent; cache the handle.
- `lua_tolstring` returns UTF-8; strings are marshalled manually with `Encoding.UTF8` to avoid the default ANSI marshalling.
- `LUA_GLOBALSINDEX = -10002` (Lua 5.1 pseudo-index) is used for globals; LuaJIT keeps the 5.1 API (`lua_resume(L, narg)`, no `lua_resume` `from` parameter).

# How to re-run

```bash
brew install luajit   # macOS; the resolver also probes libluajit-5.1.so.2 and lua51.dll
cd spikes/LuaJitSpike && dotnet run
```

[^spike]: LuaJitSpike source
[^luajit-ext]: LuaJIT extensions and C++ exception interoperability
