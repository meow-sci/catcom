# Plan 02 — V8 via ClearScript with transparent TypeScript

Status: proposed · Depends on: [Plan 00](00-shared-foundation-plan.md) · Research: [V8 via ClearScript](../docs/research/runtimes/v8-clearscript.md), [TypeScript execution options](../docs/research/runtimes/typescript-execution-options.md), [V8 spike](../docs/research/spikes/v8-typescript-spike.md), [sandboxing](../docs/research/architecture/sandboxing-limits-and-safety.md), [neovim shipping](../docs/research/editor/neovim-shipping.md)

## 1. Goal

`KsaScripting.Runtime.V8` implements `IScriptRuntime` on **Microsoft.ClearScript.V8 7.5.1.1** so that users write `.ts` files that run with no build step: the mod transpiles with `typescript@6` inside V8 on load and on every save, resolves `import "./x.ts"` through a custom `DocumentLoader`, exposes the `ksa` API as host objects, maps `async` handlers to the scheduler via Promise ↔ Task conversion, enforces heap and watchdog limits, and offers a V8 inspector port for debugging. Users get completion from the generated `host.d.ts` via TypeScript 7's native `tsc --lsp`.

## 2. Packages and files

- NuGet: `Microsoft.ClearScript.V8 7.5.1.1` (+ `Core`, `V8.ICUData`, `Newtonsoft.Json`), `Microsoft.ClearScript.V8.Native.win-x64`, `.linux-x64`, optionally `.osx-arm64` (all pinned together; 7.5.1 changed the strong-name key, so bumping requires a recompile).
- npm (vendored at build time by `tools/fetch-ts.*`, pinned): `typescript@6.0.x` → ship only `lib/typescript.js` (8.9 MiB) plus its LICENSE; optional `sucrase` IIFE bundle (203 KiB, built with `bun build --format=iife --minify`) as a fast path for syntax-only transforms.
- Dist layout: managed DLLs flat; natives under `runtimes/<rid>/native/ClearScriptV8.<rid>.<ext>` (ClearScript's own loader probes `<assemblyDir>/runtimes/<rid>/native` first); set `HostSettings.AuxiliarySearchPath = modDir` before the first engine as belt and braces; call `NativeLibrary.Load(absPath)` at startup for a clear error if the file or glibc (≥ 2.27) is missing.
- **One ClearScript copy per process:** if another mod ever needs V8, share this assembly via StarMap `ExportedAssemblies`.

## 3. Engine topology

```
V8Runtime (one per process, constraints) ──┬── "compiler" V8ScriptEngine: typescript.js (+sucrase) loaded once, code-cached
                                            ├── script engine A  (user script A: its module graph, host objects)
                                            ├── script engine B
                                            └── REPL engine
```

- **Compiler engine:** loads `typescript.js` with `Compile(..., V8CacheKind.Code, out cache)`; persists the cache under the user data dir keyed by ClearScript/TS version (48 ms warm vs 79 ms cold measured); exposes `transpile(src, fileName) → { out, map, diagnostics }` using `ts.transpileModule` with `{ module: ESNext, target: ES2022, isolatedModules: true, inlineSourceMap: true, inlineSources: true, reportDiagnostics: true }`. Steady-state 0.8 ms per file.
- **Script engines:** one `V8ScriptEngine` per script (creation ≈ 0 ms after platform init) with flags `EnableTaskPromiseConversion | UseSynchronizationContexts | EnableDynamicModuleImports | EnableStringifyEnhancements` (+ `EnableDebugging | EnableRemoteDebugging` and `debugPort` when configured). Per-engine `DocumentSettings.AccessFlags = DocumentAccessFlags.None` with a custom `TsDocumentLoader` (only paths under `scripts/` are loadable; `.ts` → compiler engine; `.js` passthrough; `ksa` bare specifier → `AddSystemDocument`). Hot reload = dispose the engine and create a new one (module records are per engine), which also drops leaked closures.
- **Runtime constraints:** `V8RuntimeConstraints { MaxOldSpaceSize = 256, MaxNewSpaceSize = 16, MaxArrayBufferAllocation = 64 MiB }`; per engine `MaxRuntimeHeapSize` soft limit (64 MiB default, configurable), `RuntimeHeapSizeSampleInterval = 50 ms`, `RuntimeHeapSizeViolationPolicy.Interrupt`, `MaxRuntimeStackUsage`.

## 4. Host API exposure

- Generate from `ApiSchema` a JS prelude (`ksa` module as a system document) that wraps a small set of host objects: `engine.AddHostObject("__host", HostItemFlags.DirectAccess, hostFacade)` where `hostFacade` exposes coarse methods (`read(handleId, fieldPath)`, `snapshot(handleId)`, `act(actionKey, vesselId, ordinal, value, values, token, aux) → CommandResult`, `on/every/service/command`, `sleep/nextStep/until/waitEvent` returning Tasks). Keeping the host surface small and typed avoids per-member reflection binding cost and keeps the `.d.ts` the single source of truth; the prelude builds ergonomic `VesselRef` classes on top.
- Values: DTO records marshalled to plain JS objects via `JSON` round-trip for snapshots (cheap at 10 Hz) and direct scalar returns for hot reads; `Vec3`/`Quat` as `{x,y,z}`/`{x,y,z,w}`.
- Async: host methods return `Task<T>`; with `EnableTaskPromiseConversion` scripts `await` them. Install a game-thread `SynchronizationContext` whose `Post` enqueues to the Step slot so continuations after host Tasks re-enter the engine on the game thread; `UseSynchronizationContexts` makes ClearScript honour it. `IScriptRuntime.Advance` runs pending continuations (drain the context queue) within the budget.
- Sync hooks: `Invoke(handler)` directly inside the patch; a handler that returns a Promise is rejected with a logged error (sync hooks cannot await).
- Console: `console.log/warn/error` → host logger → KSA console + REPL.
- **Terminal scripts:** `ctx.term` is a prelude class over `__host.termWrite/termInfo/wait("termRead")`: `term.write(string | Uint8Array)`, `await term.read(): Promise<Uint8Array>`, `term.on('data'|'resize'|'attach'|'detach'|'cancel', fn)`, `term.size`, `term.attached`; the shipped `lib/tui.ts` adds escape-code helpers, an input parser (keys incl. kitty encoding, SGR mouse) and small widgets. Output is buffered per frame by the host.
- **Exports and context:** `IScriptRuntime.Exports` enumerates the module namespace for functions; each invocation gets a `ctx` object (`EventContext`/`TickContext`/`ServiceContext`/`TerminalContext` from the `.d.ts`) built by the prelude from a host map; `ServiceContext.signal.cancelled` flips when the user presses Stop.

## 5. Limits, errors, debugging

- Watchdog → `engine.Interrupt()` (verified ~100 ms stop); `ScriptInterruptedException` marks the script faulted; `CancelInterrupt()` before reuse.
- Errors: `ScriptEngineException.ErrorDetails` includes the JS stack; decode the inline source map (small VLQ decoder in Core) to report `.ts` lines; `ScriptLoadException` from `transpile` diagnostics (`ts.flattenDiagnosticMessageText`) with file/line/col for nvim diagnostics.
- Debugging: config `v8_debug_port` (default off); when on, `AwaitDebuggerAndPauseOnStart` optional; document VS Code attach and the Chrome `devtools://…?ws=` URL.
- No Node APIs exist in the engine (no `require`, `fs`, `process`); document the "browser-less, Node-less" environment in the stub header; provide `setTimeout`/`setInterval` on the sim clock and a minimal `performance.now()`.

## 6. TypeScript user experience

- `scripts/stubs/host.d.ts` generated from `ApiSchema` (JSDoc from schema docs); `tsconfig.json` with `module: esnext`, `target: es2022`, `moduleResolution: bundler`, `strict`, `types: ["./stubs/host.d.ts"]`, `allowImportingTsExtensions`, `noEmit`.
- Type checking is editor-side only (`tsc --lsp`); the runtime accepts anything `transpileModule` can emit. A `script check <file>` console command can run a full `ts.createProgram` type-check in the compiler engine on demand (slower, optional).
- Full language supported: enums, namespaces, parameter properties, decorators (`experimentalDecorators` off by default; ES decorators per TS 6 defaults).

## 7. Tests

- Unit tests (no game): compiler engine load + transpile fixtures; `TsDocumentLoader` path containment (`../` rejected); Promise/Task round trips on a fake sync context; interrupt; heap limit; source-map line mapping; stub generator snapshot test.
- Script-level tests with `NullHost`: hooks, ticks, awaits, reload semantics, error reporting.
- In-game checklist: same as Plan 01 plus an inspector attach session.

## 8. Risks

| Risk | Mitigation |
|---|---|
| ClearScript key/version bumps | pin exact versions; recompile on bump; CI matrix |
| V8 process-global ownership | single copy rule; export assembly if needed |
| Per-engine typescript.js cost on reload | compiler engine separation (users' engines never load TS) |
| Heap growth from many engines | per-engine soft limit; REPL engine recycled; status window shows `GetRuntimeHeapInfo()` |
| TS 7 removal of the JS API | pin `typescript@6`; sucrase as a permanent syntax-only fallback |
| Linux glibc < 2.27 | startup check with a clear message; TypeScript runtime reported unavailable (Lua still works) |

## 9. Definition of done

Tests green on win-x64 and linux-x64; in-game checklist passes; a `.ts` script with `import` of a sibling `.ts` module, an `async` event hook, an enum, and a Solver-phase action works end to end; nvim completion via `tsc --lsp`; inspector attach documented and verified once.
