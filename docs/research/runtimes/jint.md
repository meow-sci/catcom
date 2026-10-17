---
type: Runtime Evaluation
title: Jint (pure C# JavaScript engine)
description: Evaluation of Jint 4.17.0 as a zero-native-dependency JavaScript runtime for catcom, with its constraint model, module and Task interop support, debugging, measured TypeScript-transpile cost, and the trade-off against V8.
resource: https://github.com/sebastienros/jint
tags: [runtime, javascript, jint, managed, fallback]
sources:
  - id: report
    resource: /references/js-runtimes-report-2026-10-16.md
    title: JS/TS runtime embedding research report (2026-10-16)
  - id: nuget
    resource: https://www.nuget.org/packages/Jint
    title: Jint on NuGet
  - id: readme
    resource: https://raw.githubusercontent.com/sebastienros/jint/4.x/README.md
    title: Jint 4.x README
  - id: spike
    resource: /spikes/jint-typescript-spike.md
    title: Jint TypeScript spike result
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:00:00Z }
status: stable
---

> **Decision (2026-10-16):** Jint is not pursued for catcom; this page remains as reference evidence. See the [recommendation](/decisions/recommendation.md).

# Status (2026-10-16)

**Jint 4.17.0**, published 2026-10-07, BSD-2-Clause, targets net462/netstandard2.0/2.1/net8.0/**net10.0**, depends on `Acornima`; 4.x is the maintenance line while `main` is Jint 5 (unreleased). ES2015 → **ES2025** with test262 at 99.9%.[^nuget][^readme]

# Capabilities

- **Constraints:** `TimeoutInterval`, `MaxStatements`, `LimitMemory`, `LimitRecursion(n)`, `CancellationToken`, custom `Constraint` subclasses, `OperationDeadlineConstraint`; a hardened `ForUntrustedCode(limits)` profile restricts CLR/reflection, module loading, string compilation and `Atomics.wait`. **Constraints are cooperative and cannot pre-empt host callbacks.**[^readme]
- **Modules:** `EnableModules(path)`, `engine.Modules.Add(name, source)`, `Modules.Import(...)`, `IModuleLoader` / `IAsyncModuleLoader`.
- **Async:** `ExperimentalFeature.TaskInterop` makes .NET `Task`/`ValueTask` awaitable; `EvaluateAsync/ExecuteAsync/InvokeAsync`; `UnwrapIfPromiseAsync()`.
- **Debugging:** `Jint.DevTools` (Chrome DevTools Protocol server).
- **Threading:** engine instances are not thread-safe; `Prepared<Script>`/`Prepared<Module>` are thread-safe and cacheable. 4.17.0 added `DiscardInterpreterCaches()` and fixed scripts that could crash the host; 4.16 added an opt-in `StackOverflowGuard`.[^report]
- **Host interop:** `SetValue`, CLR types via `TypeReference`, delegates as functions.

# Measured on this workstation

`new Engine()` 246 ms (first use, includes JIT of Jint itself); `typescript.js` 6.0.3 executes in 475 ms; first `transpileModule` 166 ms, steady 33.8 ms per file; a 5M-iteration arithmetic loop 425 ms (V8 ≈ 10 ms; LuaJIT 50M iterations 45 ms).[^spike] A bundled sucrase build is the lighter transpiler for Jint (measurement in the spike page when present).

# Trade-offs

| For | Against |
|---|---|
| Zero native code: no RID folders, no DllImport resolvers, no process-global V8 ownership, no "two mods both load V8" conflict | Roughly 40× slower than V8 on compute-heavy loops |
| Cooperative but comprehensive limits; cannot abort the game process on OOM like V8 can | Limits cannot pre-empt a host callback that blocks |
| Same ES2025 language surface and the same transparent-TS design (custom module loader + in-engine transpiler) | No WASM, so `@swc/wasm-typescript` is unavailable; use sucrase or typescript.js |
| CDP debugging | Smaller ecosystem of docs than V8 |

# Verdict

The right **fallback engine** behind a shared `IScriptHost` abstraction, and a credible primary choice if deployment simplicity and crash-safety outrank peak script throughput. Event hooks that do a few hundred operations per event are unaffected by interpreter speed; per-frame numerical hooks are where V8 matters.

[^report]: JS/TS runtime embedding research report (2026-10-16)
[^nuget]: Jint on NuGet
[^readme]: Jint 4.x README
[^spike]: Jint TypeScript spike result
