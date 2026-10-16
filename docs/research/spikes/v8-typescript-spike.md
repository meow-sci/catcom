---
type: Spike Result
title: V8 (ClearScript) TypeScript-in-engine spike
description: Measured results of running the TypeScript compiler inside V8 via Microsoft.ClearScript to transpile user .ts files transparently, plus module loading, async hook await, interrupt and heap limits.
resource: ../../../spikes/V8TsSpike/Program.cs
tags: [spike, v8, clearscript, typescript, measurement]
sources:
  - id: spike
    resource: ../../../spikes/V8TsSpike/Program.cs
    title: V8TsSpike source
  - id: clearscript-nuget
    resource: https://www.nuget.org/packages/Microsoft.ClearScript.V8
    title: Microsoft.ClearScript.V8 on NuGet
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:30:00Z }
status: stable
---

# What was tested

`spikes/V8TsSpike` is a .NET 10 console app using **Microsoft.ClearScript.V8 7.5.1.1** (published 2026-09-04)[^clearscript-nuget] with the `osx-arm64`, `win-x64` and `linux-x64` native packages referenced. It was run on this macOS arm64 workstation; numbers are from one run and are indicative, not benchmarks.[^spike]

The TypeScript compiler used is `typescript.js` **6.0.3** (8.9 MiB) taken from the `ksa-game-assemblies` checkout's `node_modules`.

# Results

| Step | Measured |
|---|---|
| First `V8ScriptEngine` (includes V8 platform init) | 32 ms |
| Every further `V8ScriptEngine` | ~0 ms |
| `engine.Execute(typescript.js)` cold | 75–79 ms |
| `engine.Compile(..., V8CacheKind.Code, out cache)` | 86–88 ms, cache = 2.4 MiB |
| Fresh engine, compile **with** code cache + execute | 48 ms (cache accepted) |
| First `ts.transpileModule` call (JIT warm-up) | 21 ms |
| Steady-state `ts.transpileModule` of a ~15-line hook file (avg of 200) | 0.7–0.9 ms |
| ES-module import graph of two `.ts` files through a custom `DocumentLoader` that transpiles on load | 4 ms |
| `engine.Interrupt()` from another thread against `for(;;){}` | returns after ~100 ms (the sleep before interrupt) with `ScriptInterruptedException` |
| Heap with typescript.js resident | 29 MiB used |
| **sucrase 3.35.1** bundled as a 203 KiB IIFE (`bun build --format=iife --minify`): execute cold | 5 ms |
| sucrase first transpile / steady-state (avg of 200), `transforms: ['typescript']` | 6 ms / 0.09 ms |

# What this proves

- **Transparent TS is cheap in V8.** Loading the full TypeScript compiler once per engine costs well under 100 ms and each transpile is sub-millisecond. The end user never runs a build step: the mod transpiles on file load (and on every save).
- **sucrase is the lightweight alternative**: 203 KiB, 5 ms load, 0.09 ms per file, and it also emits enums (as a compact IIFE), but it does not downlevel or report type-syntax diagnostics the way `ts.transpileModule` does.
- **`ts.transpileModule` handles non-erasable syntax** (enums emitted as IIFEs, as shown in the output), unlike pure type-stripping. The spike enabled `inlineSourceMap` so stack traces can map back to `.ts` lines.
- **Custom `DocumentLoader` works for `import './x.ts'`.** Override `LoadDocumentAsync`, read the file, transpile when the path ends in `.ts`, return a `StringDocument` with `ModuleCategory.Standard` and the provided `ContextCallback`. Relative specifiers resolve against `sourceInfo.Uri`.
- **Promise ↔ Task interop is automatic** with `V8ScriptEngineFlags.EnableTaskPromiseConversion`: calling an `async` TS export from C# returns a `Task<object>` directly (there is no `ToTask()` member on the dynamic result; cast the result).
- **Interrupt and heap limits work:** `engine.Interrupt()`, `MaxRuntimeHeapSize`, `RuntimeHeapSizeSampleInterval` and `GetRuntimeHeapInfo()` behave as documented.

# Gotchas found

- A module evaluation (`ModuleCategory.Standard`) returns `Undefined`, not a `ScriptObject`; export values must be pulled out via `globalThis` assignments or `engine.Script` after import.
- `engine.Script.<fn>` returns `dynamic`; cast `Task<object>` results explicitly.
- The code cache must be regenerated when the ClearScript/V8 version changes (V8 rejects a cache built by a different V8 build; `accepted` reports this).

# How to re-run

```bash
cd spikes/V8TsSpike && dotnet run -- /path/to/node_modules/typescript/lib/typescript.js
```

The default path points at the sibling `ksa-game-assemblies/node_modules/typescript/lib/typescript.js`.

[^spike]: V8TsSpike source
[^clearscript-nuget]: Microsoft.ClearScript.V8 on NuGet
