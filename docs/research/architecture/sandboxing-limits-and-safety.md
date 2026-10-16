---
type: Design
title: Sandboxing, resource limits, and crash safety for user scripts
description: The threat model (user-authored scripts in a single-player game process), what each runtime can and cannot enforce (filesystem/network isolation, heap limits, CPU interrupts), the host-side rules that make a script bug never crash the game, and the per-runtime mechanisms with their verified caveats.
tags: [design, sandbox, limits, watchdog, safety]
sources:
  - id: v8
    resource: /runtimes/v8-clearscript.md
    title: V8 via ClearScript
  - id: jint
    resource: /runtimes/jint.md
    title: Jint
  - id: luaspike
    resource: /spikes/luajit-pinvoke-spike.md
    title: LuaJIT spike
  - id: gameloop
    resource: /ksa-host/game-loop-and-threading.md
    title: KSA frame loop and threading
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:30:00Z }
status: draft
---

# Threat model

Scripts are written by the player (or copied from the community) and run inside a single-player game. The goal is **robustness, not security against a hostile author**: a buggy or runaway script must not crash, hang or corrupt the game, and must not accidentally touch the user's files or network. Full isolation of malicious code is out of scope (Jint's own README says cooperative limits cannot stop hostile code; a disposable process would be needed).[^jint]

# Host-side rules (apply to every runtime)

1. **Every entry into a runtime is wrapped**: exceptions become script errors (console, REPL, nvim diagnostic), never propagate into Harmony patch bodies or the frame loop. A throw on the sim path would crash the game with a heap dump.[^gameloop]
2. **Budgets**: per-frame total (2 ms default), per-call watchdog (5 ms sync hooks, 50 ms others). A watchdog thread arms a deadline before each call and interrupts the runtime on expiry; the offending script is disabled until reloaded.
3. **Fault latching**: a script that faults N times in a row (default 3) is disabled; a hook target that cannot be patched goes dark (gatOS `KsaHealth` model).
4. **No direct KSA objects in scripts**: handles + DTOs only, so a script cannot hold a disposed `Vehicle` or race the solver.
5. **Writes only through the action executor**, which validates, gates authority (`control_all_vessels` default on for scripts, configurable) and routes phases.
6. **Memory**: per-runtime heap ceilings; a runtime that exceeds its soft limit is interrupted (V8) or its allocator refuses (Lua `lua_setallocf` accounting) and the script is disabled.
7. **Reload safety**: unregister hooks before disposing the old scope; never free a native library.

# Per-runtime mechanisms

| Concern | V8 (ClearScript) | Jint | LuaJIT |
|---|---|---|---|
| Filesystem / network | none by default (`DocumentAccessFlags.None`; only host objects exposed) | none by default; `ForUntrustedCode` profile also blocks CLR reflection and `eval` | remove `os`, `io`, `package`, `require` (replace with a scripts-folder-only loader), `loadfile`, `dofile`, `debug` (keep `traceback`), and **`ffi`** |
| Heap | `V8RuntimeConstraints { MaxOldSpaceSize, MaxNewSpaceSize }` + `MaxRuntimeHeapSize` soft limit with `Interrupt` policy; V8 aborts the process on hard OOM, so the soft limit is mandatory | `LimitMemory` (cooperative) | custom allocator via `lua_setallocf` with a byte budget; `lua_gc(LUA_GCCOUNT)` for stats |
| CPU / runaway loop | `engine.Interrupt()` from the watchdog thread (verified: ~100 ms to stop `for(;;){}`) | `TimeoutInterval`, `MaxStatements`, `CancellationToken` (cooperative, cannot pre-empt a blocking host callback) | Lua-side `debug.sethook(fn, "", count)` polling a non-throwing host predicate; **does not fire inside JIT-compiled traces**, so untrusted/long scripts run with `jit.off()` or rely on host-call-boundary checks; verified in the spike |
| Error propagation | `ScriptEngineException` with JS stack; source maps decoded in C# for `.ts` lines | `JavaScriptException` with location | `lua_pcall` with `debug.traceback` handler; **host functions never call `lua_error`** (errors unwinding through managed frames panic) |
| Reentrancy | engine lock; host callbacks may call back in | not thread-safe; single thread | fine when inner calls are `lua_pcall`-protected |
| Debugging | V8 inspector port (VS Code; Chrome via URL) | `Jint.DevTools` CDP | none built in; `print`/traceback; LuaLS static analysis in nvim |

# Verified caveats

- LuaJIT 2.1 on x64/arm64 unwinds errors with the platform C++ unwinder, which cannot traverse .NET frames: variant A of the spike (`lua_error` from a managed hook) panicked, variant B (Lua-side hook + host predicate) worked.[^luaspike]
- LuaJIT count hooks never fire inside compiled traces; the spike's JIT-on infinite loop was not interrupted within 3 s.[^luaspike]
- ClearScript: two managed copies of ClearScript in one process conflict over the V8 platform; share one copy across mods if another mod ever needs V8.[^v8]

[^v8]: V8 via ClearScript
[^jint]: Jint
[^luaspike]: LuaJIT spike
[^gameloop]: KSA frame loop and threading
