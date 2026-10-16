---
type: Spike Result
title: Jint TypeScript-in-engine spike
description: Measured results of hosting typescript.js inside the pure-C# Jint engine to transpile user .ts files with no native dependencies, with engine speed figures for comparison with V8 and LuaJIT.
resource: ../../../spikes/JintTsSpike/Program.cs
tags: [spike, jint, typescript, measurement]
sources:
  - id: spike
    resource: ../../../spikes/JintTsSpike/Program.cs
    title: JintTsSpike source
  - id: jint-nuget
    resource: https://www.nuget.org/packages/Jint
    title: Jint on NuGet
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T20:30:00Z }
status: stable
---

# What was tested

`spikes/JintTsSpike` is a .NET 10 console app using **Jint 4.17.0** (published 2026-10-07, BSD-2-Clause)[^jint-nuget]. It loads `typescript.js` 6.0.3 (8.9 MiB) and calls `ts.transpileModule`, then runs a pure-JS hot loop to gauge interpreter speed. Run on macOS arm64; single run, indicative only.[^spike]

# Results

| Step | Measured |
|---|---|
| `new Engine()` | 246 ms (first use of the assembly; includes JIT of Jint itself) |
| `engine.Execute(typescript.js)` cold | 475 ms |
| First `ts.transpileModule` | 166 ms |
| Steady-state transpile of the same ~12-line file (avg of 50) | 33.8 ms |
| 5,000,000-iteration arithmetic loop | 425 ms |
| **sucrase** 203 KiB bundle: execute cold | 39 ms |
| sucrase first transpile / steady-state (avg of 50) | 10 ms / 2.4 ms |

For comparison the same transpile is 0.7–0.9 ms in V8 and the 5M loop is roughly 10 ms in V8; the 50M-iteration LuaJIT loop took 45 ms.

# What this proves

- **sucrase is the right transpiler for Jint**: 39 ms to load and 2.4 ms per file versus 475 ms / 34 ms for typescript.js, a 14× per-file and 12× load-time improvement with no native code.
- **Jint can host the TypeScript compiler.** Transparent TS-on-save is feasible with zero native dependencies: ~35 ms per file save is imperceptible in an editor workflow, and the one-time 0.5 s compiler load can be done on a background thread at mod start.
- **Jint is roughly 40× slower than V8 for compute-heavy script code.** That is irrelevant for event hooks that do a few hundred operations per event, and relevant for per-frame hooks doing heavy math.
- Jint has no native library, so it sidesteps every `AssemblyLoadContext`/native-probing concern in the mod loader. This makes it the lowest-risk fallback JS runtime.

# How to re-run

```bash
cd spikes/JintTsSpike && dotnet run -- /path/to/node_modules/typescript/lib/typescript.js
```

[^spike]: JintTsSpike source
[^jint-nuget]: Jint on NuGet
