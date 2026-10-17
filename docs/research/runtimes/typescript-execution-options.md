---
type: Analysis
title: How TypeScript can run with no user-visible build step
description: Settles that no engine executes TypeScript natively (Node's "native TS" is type stripping by amaro before V8), then compares the in-process transpile options (typescript.js 6, sucrase, swc-wasm, esbuild) with measured numbers from the spikes, and sets the recommended transpile policy for catcom.
tags: [typescript, transpile, type-stripping, sucrase, swc, esbuild, node]
sources:
  - id: report
    resource: /references/js-runtimes-report-2026-10-16.md
    title: JS/TS runtime embedding research report (2026-10-16)
  - id: nodets
    resource: https://nodejs.org/api/typescript.html
    title: "Node.js Modules: TypeScript documentation"
  - id: amaro
    resource: https://github.com/nodejs/amaro
    title: nodejs/amaro
  - id: ts58
    resource: https://www.typescriptlang.org/docs/handbook/release-notes/typescript-5-8.html
    title: TypeScript 5.8 release notes (erasableSyntaxOnly)
  - id: ts70
    resource: https://devblogs.microsoft.com/typescript/announcing-typescript-7-0/
    title: Announcing TypeScript 7.0
  - id: tc39
    resource: https://github.com/tc39/proposal-type-annotations
    title: TC39 Type Annotations proposal
  - id: sucrase
    resource: https://github.com/alangpierce/sucrase
    title: sucrase
  - id: esbuild
    resource: https://esbuild.github.io/api/
    title: esbuild API
  - id: v8spike
    resource: /spikes/v8-typescript-spike.md
    title: V8 TypeScript spike result
  - id: jintspike
    resource: /spikes/jint-typescript-spike.md
    title: Jint TypeScript spike result
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:00:00Z }
status: stable
---

# The premise, corrected

**V8 does not execute TypeScript.** Node's "run .ts natively" feature replaces type syntax with whitespace using amaro (a wrapper over `@swc/wasm-typescript`) before V8 sees the code: added in v22.6.0, on by default from v23.6.0 / v22.18.0, stable in v25.2.0 / v24.12.0; v26 removed `--experimental-transform-types`, so Node ships strip-only. Unsupported there: `enum`, `namespace` with runtime code, parameter properties, `import x = require`, decorators, `tsconfig` paths, `.tsx`.[^nodets][^amaro] TypeScript 5.8's `--erasableSyntaxOnly` mirrors exactly that subset.[^ts58] The TC39 Type Annotations proposal is still Stage 1 (last presented 2023-09), so no engine will parse types in the foreseeable future.[^tc39] Deno and Bun also transpile internally. **Every "run TS" story is a transpile step somewhere; the only question is who runs it.** For catcom the answer is: the mod, in-process, at load and on save.

# In-process transpiler options

| Option | Size | License | Handles enums / namespaces / param props | Measured in V8 (this workstation) | Measured in Jint |
|---|---|---|---|---|---|
| `typescript@6` `lib/typescript.js` + `ts.transpileModule` | 8.9 MiB JS (package 23.6 MB) | Apache-2.0 | **yes** (full language) | load 75–79 ms (48 ms with code cache); 0.7–0.9 ms per file | load 475 ms; ~34 ms per file |
| **sucrase** 3.35.1 (`transforms: ['typescript']`) | ~1.1 MB JS (bundled IIFE) | MIT | enums yes; no downleveling; unsupported syntax passes through | see the sucrase section below | see below |
| `@swc/wasm-typescript` 1.16.18 (what Node uses) | 3.8 MB WASM | Apache-2.0 | strip-only (Node parity), whitespace-preserving | not measured; V8 runs WASM natively | Jint cannot run WASM |
| `amaro` 1.2.1 | 7.6 MB | MIT | strip-only + `amaro/transform` | not measured | no |
| `esbuild-wasm` 0.28.2 | 14.5 MB | MIT | yes | "10× slower than native" | no |
| esbuild native binary spawned per save | ~11.7 MB per RID | MIT | yes | process spawn per save; **no C API** exists | n/a |
| TypeScript 7 (`tsc`, Go) | ~28 MB per RID | Apache-2.0 | n/a: **no programmatic API**, CLI/LSP only | per-file transform is wrong-shaped | n/a |

TypeScript 6.0 (2026-03-23) is the last JS-based compiler; 7.0 (2026-07-08) ships without a JS API and `@typescript/typescript6` is the compatibility shim. **Pin `typescript@6` for in-engine use.**[^ts70][^report]

# Recommended policy

1. **Default: `typescript@6` `transpileModule` inside the engine** with `{ module: ESNext, target: ES2022, isolatedModules: true, inlineSourceMap: true, inlineSources: true }`. It costs under 100 ms once (cacheable), under 1 ms per file in V8, supports the whole language (enums, namespaces, parameter properties, decorators) so users never hit "unsupported syntax", and `reportDiagnostics` returns syntax errors with positions for the in-game error display. The spike shows the output.[^v8spike]
2. **Jint fallback: sucrase**, not typescript.js, because the interpreter makes the 8.9 MiB compiler cost ~0.5 s to load and ~34 ms per file; sucrase is 8× smaller and faster in an interpreter. The Jint spike measured typescript.js as still workable, so this is an optimization, not a blocker.[^jintspike]
3. **Do not spawn external binaries** (esbuild, tsc) from the game process: process-spawn latency on Windows, an extra 10–28 MB per RID, and an attack/complexity surface, for no functional gain over in-isolate transpilation.
4. **Type checking is an editor concern**, not a runtime one: ship `host.d.ts` + `tsconfig.json`, and let nvim's LSP (`tsc --lsp --stdio` from TypeScript 7, or `typescript-language-server` with TS 6) report type errors. The runtime only needs syntax-valid TS.
5. **Source maps:** with `inlineSourceMap`, decode the VLQ map in C# when formatting stack traces, or use strip-only mode (whitespace-preserving) where line numbers already match. The simplest robust choice is `transpileModule` + a small VLQ decoder, since V8 does not apply source maps itself.

# Tooling note for the editor

`typescript-language-server` needs Node; TypeScript 7's native `tsc --lsp --stdio` does not (nvim-lspconfig ships a `tsc` config). Either way the LSP reads the same `host.d.ts`/`tsconfig.json` the mod generates. See [neovim shipping](/editor/neovim-shipping.md).

[^report]: JS/TS runtime embedding research report (2026-10-16)
[^nodets]: Node.js "Modules: TypeScript" documentation
[^amaro]: nodejs/amaro
[^ts58]: TypeScript 5.8 release notes (erasableSyntaxOnly)
[^ts70]: Announcing TypeScript 7.0
[^tc39]: TC39 Type Annotations proposal
[^sucrase]: sucrase
[^esbuild]: esbuild API
[^v8spike]: V8 TypeScript spike result
[^jintspike]: Jint TypeScript spike result
