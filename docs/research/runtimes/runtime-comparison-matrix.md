---
type: Comparison
title: Scripting runtime comparison matrix
description: Side-by-side comparison of every runtime evaluated for catcom (LuaJIT direct, Lua-CSharp, ClearScript V8, Jint, node-api-dotnet/libnode, PuerTS, QuickJS, Roslyn C# scripting) across language, maintenance, native footprint, loader fit, performance, limits, async model, debugging, editor tooling and risk, with measured numbers from the spikes.
tags: [comparison, matrix, runtimes, decision]
sources:
  - id: luajit
    resource: /runtimes/luajit.md
    title: LuaJIT evaluation
  - id: v8
    resource: /runtimes/v8-clearscript.md
    title: V8 via ClearScript
  - id: jint
    resource: /runtimes/jint.md
    title: Jint
  - id: node
    resource: /runtimes/nodejs-libnode.md
    title: Node.js via libnode
  - id: others
    resource: /runtimes/other-js-engines.md
    title: Other JS engines
  - id: luaalt
    resource: /runtimes/lua-alternatives.md
    title: Lua alternatives
  - id: roslyn
    resource: https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp.Scripting
    title: Microsoft.CodeAnalysis.CSharp.Scripting on NuGet
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T22:00:00Z }
status: stable
---

| Criterion | **LuaJIT 2.1 (direct P/Invoke)** | Lua-CSharp 0.5.7 | **ClearScript V8 7.5.1.1** | **Jint 4.17.0** | node-api-dotnet + LibNode 20 | PuerTS 3.0.3 | QuickJS-NG | Roslyn C# scripting 5.9 |
|---|---|---|---|---|---|---|---|---|
| Language | Lua 5.1 + ext | Lua 5.2 | JS ES2025 (+TS via in-isolate transpile) | JS ES2025 (+TS via sucrase/typescript.js) | JS (+TS only with self-built Node ≥ 22.18) | JS (+TS via transpile) | JS | C# |
| Maintained (2026-10) | yes, rolling (Mike Pall, 2026-10-14) | yes (2026-09-26) | yes (2026-09-04) | yes (2026-10-07) | library yes; **libnode stale (2025-04, Node 20 EOL)** | yes (2026-09-05) | engine yes; no .NET binding | yes |
| License | MIT | MIT | MIT | BSD-2 | MIT | BSD-3 | MIT | MIT |
| Native footprint per RID | 0.5–0.7 MB, **self-built** (no official binaries) | none | ~10.5 MB nupkg (+4.5 MB ICU) ≈ 15 MB | none (6.7 MB nupkg) | ~28 MB | V8-sized | ~1 MB | none (Roslyn ~10 MB managed) |
| RIDs | any we build (win-x64, linux-x64, osx-arm64 tested) | any | win/linux/osx × x86/x64/arm/arm64 | any | win/linux/osx x64/arm64 | win-x64, linux-x64, osx-universal | — | any |
| Loader fit (StarMap ALC) | resolver + absolute path (purrTTY pattern) | trivial | own absolute-path loader + `AuxiliarySearchPath`; **one V8 per process** | trivial | explicit path; one platform per process | unverified | — | trivial |
| Script speed (measured) | 50M-iteration loop 45 ms; host call 5–7 ns | ≈320 ns/host call (vendor) | 5M loop ≈ 10 ms class; `transpileModule` 0.8 ms | 5M loop 425 ms; transpile 34 ms (typescript.js) / 2.4 ms (sucrase) | V8 | V8 | interpreter | native |
| Startup | 22 µs per state | ms | 32 ms platform + ~0 per engine; TS compiler 48–79 ms | 246 ms first engine; sucrase 39 ms | Node boot | — | — | first compile 100s of ms |
| Sandbox default | remove libs + `-DLUAJIT_DISABLE_FFI` | `LuaPlatform` | bare V8, `DocumentAccessFlags.None` | hardened profile | **full Node fs/net** | bare V8 | bare | full CLR (no sandbox) |
| Heap limit | `lua_gc` polling (allocator hook deprecated) | `LimitMemory`-like? | `V8RuntimeConstraints` + soft limit | `LimitMemory` (cooperative) | V8 flags | unverified | C API | none |
| CPU interrupt | count hook via C shim; **needs CHECKHOOK build or jit.off()** | hook (cannot interrupt host) | `Interrupt()` any thread (verified) | timeout/statements (cooperative) | thread model | unverified | C API | none |
| Async model | coroutines (`lua_yield` from host fn, resumable VM) | async/await host fns | Promises ↔ Task (`EnableTaskPromiseConversion`, verified) | `TaskInterop` (experimental) | Node loop on its own thread | — | — | Task |
| Debugger | none (traceback only) | none | V8 inspector port | `Jint.DevTools` CDP | Node inspector | V8 | none | VS |
| Editor tooling | LuaLS 3.19.2 (20–23 MB), LuaCATS stubs | same | `tsc --lsp` (TS 7, 27 MB, no Node) + `.d.ts` | same | same | same | same | OmniSharp/Roslyn LSP (heavy) |
| Critical gotcha | errors must not unwind through managed frames (C shim / Lua-side raise) | dialect ≠ LuaJIT | pin `typescript@6`; V8 is process-global | 40× slower than V8 on loops | wrong Node version; experimental embedding | Unity-centric docs | no binding | no sandbox; Roslyn in a game process |
| Verdict | **primary Lua** | not pursued | **primary TS/JS** | not pursued (decision 2026-10-16; research kept) | rejected for now | third option | rejected | out of scope (users asked for Lua/TS; note only) |

Roslyn C# scripting is listed for completeness: it would give mod-author power with zero marshalling, but it offers no sandbox, heavy compile latency, and the user brief asks for Lua and TypeScript.[^roslyn]

[^luajit]: LuaJIT evaluation
[^v8]: V8 via ClearScript
[^jint]: Jint
[^node]: Node.js via libnode
[^others]: Other JS engines
[^luaalt]: Lua alternatives
[^roslyn]: Microsoft.CodeAnalysis.CSharp.Scripting on NuGet
