---
type: Runtime Evaluation
title: Other JavaScript engines considered and dismissed
description: Short verdicts on QuickJS/QuickJS-NG, PuerTS, Deno, Bun, Hermes, Boa, YantraJS, ChakraCore, Edge.js and the AssemblyScript/WASM route, with the reason each is not the primary choice for ksa-scripting.
tags: [runtime, javascript, quickjs, puerts, deno, bun, hermes, rejected]
sources:
  - id: report
    resource: /references/js-runtimes-report-2026-10-16.md
    title: JS/TS runtime embedding research report (2026-10-16)
  - id: puerts
    resource: https://www.nuget.org/packages/Puerts.V8.Complete
    title: Puerts.V8.Complete on NuGet
  - id: quickjsng
    resource: https://github.com/quickjs-ng/quickjs
    title: quickjs-ng
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:00:00Z }
status: stable
---

| Engine | Status (2026-10) | Why not primary |
|---|---|---|
| **QuickJS / QuickJS-NG** | engine active (MIT, ~1 MB, interpreter, latest ES) | **no maintained standalone .NET binding** (`QuickJS.NET` last 0.0.3 in 2021; `QjsIpc` is out-of-process); only reachable through PuerTS's debugger-less backend[^quickjsng][^report] |
| **PuerTS** (Tencent) | `Puerts.V8.Complete` 3.0.3 (2026-09-05), BSD-3, V8/QuickJS/Node backends, net8–10, win-x64/linux-x64/osx-universal, `.d.ts` generation | Unity/Unreal-centric and partly Chinese-first docs, no AOT, still needs an in-engine transpiler; a credible third option, less idiomatic than ClearScript[^puerts] |
| **Deno** (`deno_core`) | Rust crate, no C ABI | would require a self-maintained Rust cdylib shim; heavy binary |
| **Bun** | JavaScriptCore statically linked | no embedding API |
| **Hermes / Static Hermes** | Hermes V1 default in React Native 0.84 (2026-02); typed AOT not shipped | mobile-oriented, no .NET binding |
| **Boa** | experimental Rust interpreter 0.21 | not production, Rust-only |
| **YantraJS** | C#, Apache-2.0, ES5/ES6 partial, unstable preview forks | immature, "not a security sandbox" |
| **ChakraCore** | support ended 2021, downloads removed 2024 | dead |
| **Edge.js** | Node hosting only on Windows desktop CLR | not cross-platform |
| **AssemblyScript / TS→WASM** | compiles a TypeScript-like subset to static WASM | not TypeScript (no `any`, unions, dynamic objects); loses host-object interop |

Ranking from the research: (1) ClearScript V8 + in-isolate transpiler, (2) Jint + sucrase/typescript.js, (3) PuerTS, (4) node-api-dotnet only with a self-built Node ≥ 24 libnode, (5) QuickJS, (6) the rest not viable.[^report]

[^report]: JS/TS runtime embedding research report (2026-10-16)
[^puerts]: Puerts.V8.Complete on NuGet
[^quickjsng]: quickjs-ng
