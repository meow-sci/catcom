---
type: Research Report
title: JS/TS runtime embedding research report (2026-10-16)
description: Verbatim research report produced by a delegated research pass on 2026-10-16; the primary source the distilled concepts in this bundle cite. Claims inside are tagged verified/inference/measured by the author of the report.
tags: [reference, report, raw]
generated: { by: claude-fable-5.1/ksa-scripting-research-subagent, at: 2026-10-16T20:00:00Z }
status: stable
---

> Mirrored from the session scratchpad into the bundle so the cited source outlives the session. Scratchpad paths mentioned inside (e.g. harness locations) no longer exist.

# Embedding a JavaScript/TypeScript runtime in a .NET 10 game mod (custom AssemblyLoadContext)

Research date: 2026-10-16. Target: .NET 10 / C# 13 mod loaded into a custom `AssemblyLoadContext`, Windows x64 + Linux x64 (macOS arm64 nice-to-have). End users write **TypeScript**; the mod must make TS run with no visible build step.

Legend used throughout:

- **[verified]** = stated by the cited primary source (docs, source code, NuGet/npm registry, GitHub).
- **[inference]** = my reasoning / extrapolation; not directly stated by a source.
- **[reported]** = stated by a user/maintainer in an issue or blog, not official docs.

---

## 0. Executive summary

1. **V8 does not execute TypeScript.** Node's "native TS" is whitespace type-stripping done by **amaro** (`@swc/wasm-typescript`) before V8 sees the code. **[verified]** (§1)
2. **ClearScript 7.5.1.1 (2026-09-04, V8 14.7.173.23)** is the best-supported V8-in-.NET option: MIT, actively maintained (last commit 2026-09-04), has heap/stack/interrupt limits, ES modules with a custom loader hook, Task↔Promise conversion, and a V8 inspector debug port. Its native library is located by a documented probe list that includes `HostSettings.AuxiliarySearchPath`, and it loads via `NativeLibrary.Load(absolutePath)`, which sidesteps most custom-ALC native-resolution pitfalls. **[verified]** (§2, §7)
3. **Transparent TS→JS inside the engine** is practical: run **sucrase** (~1.1 MB JS, MIT) or **`@swc/wasm-typescript`** (~3.8 MB WASM, Apache-2.0) inside the same V8 isolate, or the full `typescript@6` `transpileModule` (23.6 MB package; note **TypeScript 7 has no JS API at all**, so you must pin TS 6 for in-engine use). **[verified]** (§3)
4. **node-api-dotnet / Microsoft.JavaScript.LibNode is NOT the "TS just works" answer today**: the only published LibNode is **Node 20.18.0** (last published 2025-04-02), Node 20 is EOL since 2026-04-30 and predates type stripping (added in Node 22.6.0). The upstream C embedding API PR (nodejs/node#54660) was closed unmerged, and the project's embedding doc calls itself experimental. **[verified]** (§4.2)
5. **Jint 4.17.0 (2026-10-07)** is the strongest pure-managed fallback (no native libs, BSD-2, ES2025, cooperative limits, Chrome DevTools Protocol package), at the cost of interpreter speed on heavy loops. **[verified]** (§4.1)
6. Recommendation ranking (details §9): **(1) ClearScript V8 + in-isolate sucrase/swc-wasm**, **(2) Jint + sucrase**, **(3) PuerTS**, **(4) node-api-dotnet with a self-built Node 26 libnode**, (5) QuickJS, (6) everything else not viable.

---

## 1. Premise: does V8 run TypeScript natively? — No.

### 1.1 Node.js type stripping (the thing people call "native TS") **[verified]**

Source: https://nodejs.org/api/typescript.html

- Stability: **2 – Stable**.
- History box: added in **v22.6.0**; `--experimental-transform-types` added **v22.7.0**; type stripping **enabled by default in v23.6.0 / v22.18.0**; experimental warning removed **v24.3.0 / v22.18.0**; marked **stable in v25.2.0 / v24.12.0**; **v26.0.0 removed the `--experimental-transform-types` flag**.
- Mechanism: "Node.js will replace TypeScript syntax with whitespace, and no type checking is performed." Disable with `--no-strip-types`.
- Requires WebAssembly: "The built-in TypeScript parser requires WebAssembly. If WebAssembly is unavailable, parsing throws `ERR_WEBASSEMBLY_NOT_SUPPORTED`."
- **Not supported (errors with `ERR_UNSUPPORTED_TYPESCRIPT_SYNTAX`)**: "`Enum` declarations, `namespace` with runtime code, parameter properties, import aliases". Decorators "are not transformed and will result in a parser error". `tsconfig` `paths` error. `.tsx` is unsupported. TS files under `node_modules` are refused. File extensions are mandatory in `import`. Type-only imports must use `import type` / `type` modifier.
- Source maps: "Node.js does not generate source maps"; because types become whitespace, "line numbers in stack traces are already correct."
- Recommended tsconfig: `noEmit`, `target: esnext`, `module: nodenext`, `rewriteRelativeImportExtensions`, `erasableSyntaxOnly`, `verbatimModuleSyntax`; "TypeScript 5.8 or newer is recommended."

Node's loader implementation (https://github.com/nodejs/node/blob/main/lib/internal/modules/typescript.js) does `const amaro = require('internal/deps/amaro/dist/index'); return amaro.transformSync;` and only ever passes `mode: 'strip-only'`; errors with code `UnsupportedSyntax` → `ERR_UNSUPPORTED_TYPESCRIPT_SYNTAX`, `InvalidSyntax` → `ERR_INVALID_TYPESCRIPT_SYNTAX`. **[verified]**

Amaro (https://github.com/nodejs/amaro): "a wrapper around `@swc/wasm-typescript`, a WebAssembly port of the SWC TypeScript parser"; `transformSync` with `mode: "strip-only"` replaces types with whitespace to preserve stack traces; "supported TypeScript version is 5.8"; MIT. **[verified]**

**Inference [inference]:** the `v26.0.0` removal of `--experimental-transform-types` plus the docs still listing enums/namespaces/parameter properties as unsupported means Node 26 ships strip-only; if your users want enums you need your own transformer regardless of runtime.

### 1.2 TypeScript `--erasableSyntaxOnly` (TS 5.8) **[verified]**

- Release post dated **February 28, 2025**: https://devblogs.microsoft.com/typescript/announcing-typescript-5-8/
- Handbook: https://www.typescriptlang.org/docs/handbook/release-notes/typescript-5-8.html — errors on `enum`, `namespace`/`module` with runtime code, parameter properties, `import x = require(...)`, `import Bar = container.Bar`, `export =`. Explicitly ties to Node 23.6 type stripping and says ts-blank-space and Amaro share the same limitations. Recommended with `--verbatimModuleSyntax`.

### 1.3 TC39 "Type Annotations" proposal **[verified]**

- https://github.com/tc39/proposal-type-annotations — **Stage 1**; champions Daniel Rosenwasser (Microsoft), Romulo Cintra (Igalia), Rob Palmer (Bloomberg); "At runtime, a JavaScript engine ignores them, treating the types as comments." Page warns it "has not been updated regularly".
- https://github.com/tc39/proposals/blob/main/stage-1-proposals.md — still listed at Stage 1; last presented **2023-09**.
- **[inference]** No engine (V8 included) will parse type syntax for the foreseeable future; every "run TS" story is a transpile step somewhere.

### 1.4 Deno / Bun **[verified where cited]**

- deno_core (https://docs.rs/deno_core/latest/deno_core/) is "A modern JavaScript/TypeScript runtime built with V8, Rust, and Tokio" — a Rust crate; the TS handling is in Rust, not V8. Bun is "built on Apple's JavaScriptCore" and statically links it (https://bun.com/blog/how-bun-supports-v8-apis-without-using-v8-part-1). **[inference]** Both transpile TS internally before the engine sees it (Deno via its Rust transpiler stack, Bun via its own Zig transpiler); I did not fetch a primary doc stating the transpiler name, so treat the specific transpiler identity as inference.

---

## 2. Microsoft ClearScript (V8 in .NET)

### 2.1 Versions, dates, V8 **[verified]**

Sources: https://github.com/microsoft/ClearScript/releases ; https://www.nuget.org/packages/Microsoft.ClearScript.V8 ; https://www.nuget.org/packages/Microsoft.ClearScript.V8.Native.win-x64

| Version | NuGet date | V8 | Notable |
|---|---|---|---|
| **7.5.1.1** | **2026-09-04** | **14.7.173.23** | Patched `v8::Isolate::ResetStackLimit` (#718); added `V8Settings.DefaultStackSize` (#726); added `V8ScriptEngine.PromiseHook` and `PromiseRejectionCallback` (#715) |
| 7.5.1 | 2026-05-10 | 14.7.173.23 | **Breaking: strong-name keypair changed — all hosts must recompile**; JS explicit resource management + script-side host object disposal (#533); `JavaScriptObjectFlags` Iterable/AsyncIterable/Disposable/AsyncDisposable; `IJavaScriptObject.Update`; `HostItemFlags.DirectAccess` now default for fully public classes (#626); `HostSettings.DisableInteropAssemblyConstruction`; enum marshaling options; `V8FastArgs` uses ArrayPool; init-script minimization removed to ease debugging |
| 7.5.0 | 2025-03-07 | 13.3.415.23 | **Breaking: .NET Framework < 4.6.2 dropped; Linux requires glibc ≥ 2.27**; `FastProxy` API; Span support (#632); pre-settled promise/task interop (#641); `EnableArrayConversion`; `import.meta.setResult`; **fixed `MaxHeapSize`/`MaxRuntimeHeapSize` behavior (#571)**; assembly table moved to XML (BinaryFormatter removed) |
| 7.4.5 | 2025-03-25 | 12.3.219.12 | `AddPerformanceObject`, `SetTimerResolution`; improved execution/termination transitions (#560); Clang + C++20 on all platforms |
| 7.4.4 | 2023-10-17 | 11.8.172.15 | `UseSynchronizationContexts` flag (#509) |

Maintenance: last commit **2026-09-04** by `ClearScriptLib` ("Version 7.5.1.1 ...") (https://api.github.com/repos/microsoft/ClearScript/commits?per_page=5); ~2.0k stars, 14 open issues (https://github.com/microsoft/ClearScript). License **MIT** (repo sidebar). NuGet total downloads 23.1M, ~13.7K/day. **[verified]**

**[inference]** Cadence is ~1–2 releases/year with V8 bumps; single primary maintainer. Stable, not fast-moving.

### 2.2 NuGet package layout **[verified]**

From README (https://github.com/microsoft/ClearScript) and NuGet pages:

- Component packages: `Microsoft.ClearScript.Core`, `Microsoft.ClearScript.V8` (1.03 MB nupkg; depends on `Core`, `V8.ICUData`, `Newtonsoft.Json ≥ 13.0.3`), `Microsoft.ClearScript.V8.ICUData` (4.47 MB nupkg), `Microsoft.ClearScript.Windows[.Core]` (JScript/VBScript, Windows only).
- Native packages: `Microsoft.ClearScript.V8.Native.<RID>` for **win-x86, win-x64, win-arm64, linux-x64, linux-arm, linux-arm64, osx-x64, osx-arm64**. `...Native.win-x64` 7.5.1.1 nupkg = **10.52 MB**.
- Composite: `Microsoft.ClearScript` (Windows), `Microsoft.ClearScript.linux-x64`, `.osx-arm64`, etc.; all-in-one `Microsoft.ClearScript.Complete`.
- Supported: .NET 5.0+, .NET Framework 4.6.2+, .NET Core 3.1, .NET Standard 2.1; Windows x86/x64/arm64, Linux x64/arm/arm64, macOS x64/arm64.
- Deployment file list (https://clearscript.clearfoundry.net/Details/Build.html): Linux/macOS/arm64 need `ClearScript.Core.dll`, `ClearScript.V8.dll`, `ClearScript.V8.ICUData.dll`, `ClearScriptV8.<rid>.so|.dylib|.dll`.

**[inference]** For a mod folder you ship: `ClearScript.Core.dll`, `ClearScript.V8.dll`, `ClearScript.V8.ICUData.dll`, `Newtonsoft.Json.dll`, plus `ClearScriptV8.win-x64.dll` and `ClearScriptV8.linux-x64.so` (and `ClearScriptV8.osx-arm64.dylib`). Roughly 10–11 MB compressed per native RID; expect ~30–40 MB on disk per RID uncompressed (the nupkg size is compressed; I did not measure the extracted size — treat as estimate).

### 2.3 How the native library is located at runtime **[verified from source]**

Source: https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8Proxy.cs and `.../V8Proxy.NetCore.cs`, `.../HostSettings.cs`.

- File name: `$"{baseName}.{platform}-{architecture}.{extension}"` with `baseName = "ClearScriptV8"`; platform via `RuntimeInformation.IsOSPlatform` → `win`/`dll`, `linux`/`so`, `osx`/`dylib`; arch via `RuntimeInformation.ProcessArchitecture` → `x86|x64|arm|arm64`. Example: `ClearScriptV8.linux-x64.so`.
- Probe order (`GetDirPaths`):
  1. if `typeof(V8Proxy).Assembly.Location` is non-blank: `<assemblyDir>/runtimes/{platform}-{arch}/native`, then `<assemblyDir>`;
  2. `AppDomain.CurrentDomain.BaseDirectory`;
  3. each entry of `AppDomain.CurrentDomain.RelativeSearchPath`;
  4. each entry of **`HostSettings.AuxiliarySearchPath`** (semicolon-delimited).
  Distinct full paths tried in order.
- `HostSettings.AuxiliarySearchPath` doc: "Gets or sets a semicolon-delimited list of directory paths to search for auxiliary files. … allows the host to augment ClearScript's algorithm for locating unmanaged resources such as native assemblies and related data files."
- Loader (`V8Proxy.NetCore.cs`): `LoadLibrary(path)` → **`NativeLibrary.Load(path)`** in try/catch; the exception message is stored in a `[ThreadStatic]` field; `FreeLibrary` → `NativeLibrary.Free`.
- Failure: `TypeLoadException` "Cannot load ClearScript V8 library. Load failure information for {file}:" followed by one line per path tried with the error.
- After load: native version must equal `ClearScriptVersion.Informational` or it throws "V8 native assembly: loaded version … does not match required version …"; then `InitializeICU()` reads the ICU blob **from an embedded resource in the managed assembly** (that is what `ClearScript.V8.ICUData.dll` carries) and passes it to `V8Environment_InitializeICU`. Load is attempted once (`triedToLoadNativeAssembly`).

**Implications for a plugin folder [inference]:**
- Because ClearScript calls `NativeLibrary.Load` with an **absolute path**, the custom ALC's `LoadUnmanagedDll` / `DllImportResolver` logic is irrelevant to finding `ClearScriptV8.*`; only the probe list matters.
- If you load `ClearScript.V8.dll` via `LoadFromAssemblyPath` (Location populated) and put the native file in `<modDir>/runtimes/<rid>/native/` or next to the DLL, probe #1 finds it with no configuration.
- If you load assemblies from a byte stream (Location blank), probe #1 is skipped and you must set `HostSettings.AuxiliarySearchPath = modDir` **before the first `V8ScriptEngine`/`V8Runtime` is constructed** (load happens once at first use).
- Evidence this matters: a LINQPad user hit "Cannot load ClearScript V8 library" because the loader assumed an MSBuild layout; the suggested fix was to pre-load via `NativeLibrary.Load` (https://forum.linqpad.net/discussion/comment/6849) **[reported]**. Pre-loading the same absolute path yourself first is harmless because the OS loader returns the same module handle **[inference]**.
- 7.5.0 Linux builds require glibc ≥ 2.27 (release notes) — fine for any current distro **[verified]**. macOS: ClearScript issue #483 reports a crash fixed by a developer-signature workaround (https://github.com/microsoft/ClearScript/issues/483) **[reported]**.

### 2.4 ES module support **[verified from source]**

- `DocumentSettings` (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/DocumentSettings.cs): `Loader` (falls back to `DocumentLoader.Default`), `AccessFlags`, `SearchPath` (semicolon-separated dirs/URLs), `FileNameExtensions`, `ContextCallback` (exposes host data to a module via `import.meta` without globals), `LoadCallback`, `AsyncLoadCallback`, and `AddSystemDocument(identifier, category, contents[, contextCallback])` — "system documents take precedence over loaded documents".
- `DocumentAccessFlags` (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/DocumentAccessFlags.cs): `None` (default), `EnableFileLoading`, `EnableWebLoading`, `EnableAllLoading`, `EnforceRelativePrefix`, `AllowCategoryMismatch`, `UseAsyncLoadCallback`. **Default is `None` → no file/web loading unless the host opts in** (good for sandboxing).
- `DocumentLoader` (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/DocumentLoader.cs): subclass and override `abstract Task<Document> LoadDocumentAsync(settings, sourceInfo, specifier, category, contextCallback)`; `MaxCacheSize` (default loader 1024), `DiscardCachedDocuments()`.
- `V8ScriptEngineFlags.EnableDynamicModuleImports` — "Enables dynamic module imports … experimental and may be removed" (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8ScriptEngineFlags.cs).
- Maintainer guidance in discussion #322: use `AddSystemDocument` with `ModuleCategory.Standard` to inject host-provided modules instead of a custom loader (https://github.com/ClearFoundry/ClearScript/discussions/322) **[reported]**. 7.5.0 added `import.meta.setResult` for reliable module evaluation results (releases page).

**[inference]** The transparent-TS design: a custom `DocumentLoader` that, for `.ts` specifiers, reads the file, runs the in-isolate transpiler, and returns a `StringDocument` with `DocumentCategory`/`ModuleCategory.Standard`. Hot reload = `DiscardCachedDocuments()` + re-import under a new specifier (ES module graphs are immutable per specifier; V8 caches module records — you will need cache-busting query strings or a fresh engine).

### 2.5 Host object / type exposure **[verified]**

`IScriptEngine` docs (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/IScriptEngine.cs): `AddHostObject` ("Exposes a host object to script code with the specified options"), `AddHostType`, `AddHostTypes`, `Execute`/`Evaluate`/`ExecuteDocument`/`EvaluateDocument`/`ExecuteCommand`, `Invoke` ("Invokes a global function or procedure"), `Script` (dynamic access to globals), `Global` (`ScriptObject`), `AccessContext`, `DefaultAccess`, `AllowReflection`, `EnableAutoHostVariables`, `ExposeHostObjectStaticMembers`, `DisableTypeRestriction`, `HostData`, `Interrupt` ("can be called safely from any thread"), `CollectGarbage`. `HostFunctions` ("Provides optional script-callable utility functions"; expose via `AddHostObject`; `newObj`, `newArr<T>`, `func<T>`, `del<T>`, `proc`, `cast<T>`, `isType<T>`, `typeOf<T>`, `flags<T>`, `toInt32` …) (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/HostFunctions.cs). 7.5.1 made `HostItemFlags.DirectAccess` the default for fully public classes and added script-side disposal of host objects (releases page).

### 2.6 Task ↔ Promise interop **[verified]**

- Flags: `EnableTaskPromiseConversion` ("Converts between .NET Task objects and JavaScript promises … lossy; a Task from a promise has an object result type"), `EnableValueTaskPromiseConversion` (one-way), `UseSynchronizationContexts` ("Enables synchronization context support for task-promise interop"). 7.5.0 added support for pre-settled promises / pre-completed tasks (#641). 7.5.1.1 added `PromiseHook` and `PromiseRejectionCallback`.
- Extension methods (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/JavaScript/JavaScriptExtensions.cs): `ToPromise(this Task|Task<T>|ValueTask|ValueTask<T> [, ScriptEngine engine])` — overloads without `engine` bind to `ScriptEngine.Current` (script currently running on the calling thread); `Task<object> ToTask(this object promise)`.

**[inference]** For a game loop, use `UseSynchronizationContexts` with your own game-thread `SynchronizationContext` so continuations after `await`ing a .NET Task re-enter the engine on the game thread; otherwise promise resolution may run on a thread-pool thread and contend for the engine lock.

### 2.7 Flags (full list) **[verified]**

https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8ScriptEngineFlags.cs: `None`, `EnableDebugging`, `DisableGlobalMembers` (speeds global access), `EnableRemoteDebugging` (ignored unless `EnableDebugging`), `AwaitDebuggerAndPauseOnStart`, `EnableDateTimeConversion`, `EnableDynamicModuleImports` (experimental), `MarshalUnsafeInt64AsBigInt`, `MarshalAllInt64AsBigInt`, `EnableTaskPromiseConversion`, `EnableValueTaskPromiseConversion`, `EnableArrayConversion` (lossy; perf caution), `UseCaseInsensitiveMemberBinding`, `EnableStringifyEnhancements`, `HideHostExceptions`, `UseSynchronizationContexts`, `AddPerformanceObject`, `SetTimerResolution`.

`V8GlobalFlags` (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8GlobalFlags.cs): `DisableJITCompilation`, `DisableBackgroundWork`, `DisableExplicitResourceManagement`; set via `V8Settings.GlobalFlags` before first instantiation. `V8Settings.DefaultStackSize` (7.5.1.1).

### 2.8 Resource limits **[verified]**

- `V8RuntimeConstraints` (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8RuntimeConstraints.cs): `MaxNewSpaceSize` (MiB), `MaxOldSpaceSize` (MiB; values > 1 TiB treated as bytes for back-compat), `HeapExpansionMultiplier` (>1 enables on-demand growth; docs warn a malicious script can still exhaust memory and recommend pairing with heap monitoring), `MaxArrayBufferAllocation` (bytes, default unlimited), obsolete `MaxExecutableSize`/`MaxYoungSpaceSize`.
- `V8Runtime.MaxHeapSize` — "a soft limit for the size of the V8 runtime's heap"; "enabling heap size monitoring results in slower script execution"; `HeapSizeSampleInterval`; `HeapSizeViolationPolicy`; `MaxStackUsage` ("does not monitor stack usage while a host call is in progress") (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8Runtime.cs). The engine-level mirrors: `V8ScriptEngine.MaxRuntimeHeapSize`, `RuntimeHeapSizeSampleInterval`, `RuntimeHeapSizeViolationPolicy`, `MaxRuntimeStackUsage`, `GetRuntimeHeapInfo()`, `Interrupt()` (any thread), `CancelInterrupt()`, `EnableRuntimeInterruptPropagation` (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8ScriptEngine.cs).
- `V8RuntimeViolationPolicy` (https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8RuntimeViolationPolicy.cs): `Interrupt` (default; halts script, throws managed exception, stays blocked until host raises the limit — "strongest safeguard against process termination") vs `Exception` (throws a script exception; better for promise-based error handling).
- Maintainer note: V8's default heap cap is ~1.5 GB in a 64-bit process and V8 **aborts the process** on allocation failure, so the soft limit/monitoring exists to prevent that (https://github.com/microsoft/ClearScript/issues/84) **[reported]**.
- There is **no CPU-time limit primitive**; `Interrupt()` from a watchdog thread/timer is the documented mechanism **[verified: Interrupt doc; inference: watchdog pattern]**.

### 2.9 Threading **[verified]**

- `V8ScriptEngine` class remarks: "V8ScriptEngine instances do not have thread affinity … The underlying script engine is not thread-safe, however, so this class uses internal locks". `Interrupt`, `CancelInterrupt`, `CancelAwaitDebugger` are safe from any thread. FAQtorial Q2: V8 instances "have no thread affinity and are suitable for server-side asynchronous scripting" (https://clearscript.clearfoundry.net/Tutorial/FAQtorial.html).
- V8 itself: "Multiple threads in V8 are allowed, but only one thread at a time is allowed to use any given V8 isolate" (https://raw.githubusercontent.com/v8/v8/main/include/v8-locker.h).
- `V8Runtime.CreateScriptEngine` engines share one runtime; "V8 supports one script debugger per runtime".
- **[inference]** For a game: call the engine only from the game thread for event callbacks; marshal async completions back via `UseSynchronizationContexts`; use `Interrupt()` from a watchdog thread.

### 2.10 Debugging **[verified + reported]**

- Constructor overloads accept `debugPort` ("A TCP port on which to listen for a debugger connection"); flags `EnableDebugging`, `EnableRemoteDebugging`, `AwaitDebuggerAndPauseOnStart`; `CancelAwaitDebugger()` **[verified]**.
- FAQtorial Q26 "How do I debug script code?": "your best option is Visual Studio Code" (attach VS Code and Visual Studio simultaneously for script+managed+native) **[verified]**.
- Chrome DevTools: maintainer stated (2017) "ClearScript instances don't show up on chrome://inspect" and suggested the "Node.js V8 Inspector" Chrome extension (https://github.com/microsoft/ClearScript/issues/24); a later regression report used `EnableDebugging | EnableRemoteDebugging` on a custom port with Chrome DevTools over WebSocket (https://github.com/microsoft/clearscript/issues/42); third-party ClearScript.Manager documents 9222 as the default port (https://github.com/eswann/ClearScript.Manager) **[reported]**.
- **[inference]** ClearScript speaks the V8 Inspector protocol over WebSocket on `debugPort` (default 9222); DevTools attaches via a `devtools://devtools/bundled/inspector.html?ws=localhost:9222/...` style URL or an extension, not auto-discovery. I could not fetch the debug agent source to confirm the exact `/json/list` endpoint shape — verify empirically.

### 2.11 Performance / memory **[reported + inference]**

- Jint's README benchmark (vendor-run) says V8/ClearScript wins on long compute loops, but "a minimal script runs in about 1.1 µs with a prepared Jint script, versus about 380 µs for the compiled V8 lane" and script→host interop is "3.4x to 11.2x faster" in Jint (https://github.com/sebastienros/jint) **[reported, vendor]**.
- User report: `new V8ScriptEngine()` ≈ 300 ms, host-type setup ≈ 50 ms (https://github.com/microsoft/ClearScript/discussions/567) **[reported]**. Mitigations in docs: share a `V8Runtime`, `V8Runtime.Compile(...)` with cache data "usable in other V8 runtimes", `DisableGlobalMembers`, `FastProxy` **[verified]**.
- Memory: no official per-isolate figure. **[inference]** Expect tens of MB for the native library mapped + a few MB per isolate baseline; the V8 default heap ceiling (~1.5 GB) is the thing to constrain via `V8RuntimeConstraints`.

---

## 3. Running the TypeScript transpiler inside the engine

### 3.1 The TypeScript compiler itself **[verified]**

- `typescript@5.9.3`: unpacked **23,625,066 bytes**, `main: ./lib/typescript.js`, Apache-2.0 (https://registry.npmjs.org/typescript/5.9.3).
- **TypeScript 6.0 (2026-03-23)** is "the last release based on the current JavaScript codebase"; defaults changed (`module: esnext`, `target: es2025`); `target: es5`, `--moduleResolution node`, AMD/UMD/System removed or deprecated (https://devblogs.microsoft.com/typescript/announcing-typescript-6-0/).
- **TypeScript 7.0 (2026-07-08)**: Go native port; 8–12x faster builds; **"ships without a programmatic API"**; "a new, different API in 7.1"; `@typescript/typescript6` compatibility package provides `tsc6` and re-exports the 6.0 API (https://devblogs.microsoft.com/typescript/announcing-typescript-7-0/). `typescript@7.0.2` npm is now a 2.5 MB wrapper with per-platform `@typescript/typescript-<os>-<arch>` optional deps (https://registry.npmjs.org/typescript/latest); `@typescript/typescript-linux-x64@7.0.2` unpacked **27,945,990 bytes**, Apache-2.0 (https://registry.npmjs.org/@typescript/typescript-linux-x64/latest). `@typescript/typescript6@6.0.2` is a 10.5 KB shim with `main: ./lib/typescript.js` (https://registry.npmjs.org/@typescript/typescript6/latest).
- Compiler API wiki: `ts.transpileModule(source, { compilerOptions })` is the single-file transform; the wiki "currently describes TypeScript 6.0 and earlier" and "TypeScript 7.1 will have a completely different API" (https://github.com/microsoft/TypeScript/wiki/Using-the-Compiler-API).
- typescript-go repo: archived; permanent archive September 2026; Apache-2.0; "API: not ready" (https://github.com/microsoft/typescript-go).

**[inference]** If you want full-fidelity TS (enums, namespaces, parameter properties, decorators) inside the engine, embed `typescript@6.x` `lib/typescript.js` (~9 MB JS of the 23.6 MB package) and call `ts.transpileModule` with `{ module: ESNext, target: ES2022+, isolatedModules: true, inlineSourceMap: true }`. Cost: parsing/compiling a ~9 MB script in V8 at startup (hundreds of ms; mitigate with `V8Runtime.Compile` + cache data), and per-file transpiles typically in the low-ms range for small files **(estimate, not measured)**. Pin TS 6; TS 7 cannot be embedded.

- Historical data point: TypeScript 1.6 ran inside **Jint** in 2016 — "not as slow as I anticipated", "the initial transpile hit is rough"; TS 2.6.2 later failed to parse in Jint with an Esprima exception (https://github.com/sebastienros/jint/issues/274) **[reported, old]**.
- TypeScript issue "Do not check in the transpileModule API" (https://github.com/microsoft/TypeScript/issues/29651) documents that `transpileModule` still does more work than pure emit in some paths **[reported]**.

### 3.2 Lighter transpilers that run inside V8 **[verified]**

| Tool | Version / size | License | Notes |
|---|---|---|---|
| **sucrase** | 3.35.1, unpacked **1,137,073 B** (https://registry.npmjs.org/sucrase/latest) | MIT | Pure JS; "about 20x faster than Babel"; benchmark table (Jest codebase, 2022): sucrase 0.57 s, swc 1.19 s, esbuild 1.45 s, TypeScript 8.98 s; `typescript` transform strips types **and handles enums**; `imports` transform for CJS; no downleveling; unsupported syntax passes through; const enums not inlined cross-file (https://github.com/alangpierce/sucrase) |
| **@swc/wasm-typescript** | 1.16.18, unpacked **3,787,717 B** (https://registry.npmjs.org/@swc/wasm-typescript/latest) | Apache-2.0 | The WASM strip/transform used by amaro; V8 runs WASM natively |
| **amaro** | 1.2.1, unpacked **7,567,272 B** (https://registry.npmjs.org/amaro/latest) | MIT | Node's wrapper; `transformSync`, `mode: "strip-only"` preserves line/col; `amaro/transform` for enums etc. requires source maps |
| **esbuild-wasm** | 0.28.2, unpacked **14,532,821 B** (https://registry.npmjs.org/esbuild-wasm/latest) | MIT | "much, much slower than the native version … an order of magnitude (i.e. 10x) slower" (https://esbuild.github.io/getting-started/#wasm) |
| oxc transformer (wasm) | not evaluated | — | Not fetched in this research; treat as unverified |

**[inference]** sucrase is the sweet spot for "transparent TS": ~1 MB of plain JS, enum support, no WASM, no Node APIs, loads in a fraction of the time of typescript.js, and emits whitespace-faithful output. swc-wasm gives you exactly Node's semantics (strip-only) if you want parity with `node file.ts`.

### 3.3 Shipping a native transpiler binary and spawning it **[verified + inference]**

- esbuild: "The API can be accessed in one of three languages: on the command line, in JavaScript, and in Go" (https://esbuild.github.io/api/); "The native version can only be used via the command-line interface" apart from JS/Go (https://esbuild.github.io/getting-started/); `@esbuild/win32-x64@0.28.2` unpacked **11,695,109 B**, MIT (https://registry.npmjs.org/@esbuild/win32-x64/latest). Historically its JS `transformSync` "spawn[ed] a new child process on every call" until reworked (https://newreleases.io/project/npm/esbuild/release/0.12.11). **No C API / FFI is documented** — [inference: absence confirmed by docs listing only CLI/JS/Go].
- TypeScript 7 `tsc` native binary: ~28 MB per platform, Apache-2.0, no API; CLI only. `tsc --lsp --stdio` is its LSP mode (nvim-lspconfig `tsc.lua`: https://raw.githubusercontent.com/neovim/nvim-lspconfig/master/lsp/tsc.lua).
- **[inference]** Spawning `esbuild --loader=ts` per save costs a process start (tens of ms on Windows, less on Linux) plus sub-ms transform; acceptable for hot reload but adds 10–12 MB per RID, a process-spawn surface area, and an external dependency on disk — worse than in-isolate sucrase. Spawning `tsc` per file is wrong-shaped (full program build, no per-file transform mode).

---

## 4. Alternatives to V8

### 4.1 Jint (pure C#) **[verified]**

- NuGet `Jint` **4.17.0, 2026-10-07**; BSD-2-Clause; assemblies for net462/netstandard2.0/2.1/net8.0/**net10.0**; dependency `Acornima ≥ 1.7.0`; nupkg 6.67 MB (https://www.nuget.org/packages/Jint). Releases: 4.16.x Aug–Sep 2026; 4.x is the maintenance line while `main` is Jint 5 (unreleased) (https://github.com/sebastienros/jint/releases, https://github.com/sebastienros/jint).
- ES support: README (4.x branch) lists ES2015 → **ECMAScript 2025** plus proposals; main README reports test262 **99.9% (102,585/102,692)** (https://raw.githubusercontent.com/sebastienros/jint/4.x/README.md, https://github.com/sebastienros/jint).
- Constraints: `TimeoutInterval`, `MaxStatements`, `LimitMemory`, `LimitRecursion(n)`, `CancellationToken`, custom `Constraint` subclasses, `OperationDeadlineConstraint`; constraints reset per `Execute/Evaluate/Invoke/Call` (4.x README). Hardened profile `UntrustedCodeLimits` / `ForUntrustedCode(limits)` with `BeginOperation(engine, cancellationToken)`, restricting CLR/reflection, module loading, string compilation, `Atomics.wait`. **"Constraints are cooperative and cannot preempt arbitrary host callbacks"**; for hostile code they recommend an external deadline and a disposable least-privileged process (main README).
- Modules: `options.EnableModules(path)`, `engine.Modules.Add(name, source)`, `engine.Modules.Import(...)`, `IModuleLoader` / `IAsyncModuleLoader` (4.16.0), `Modules.ImportAsync`.
- Async/Task: `ExperimentalFeature.TaskInterop` makes .NET `Task`/`ValueTask` awaitable Promises; `UnwrapIfPromiseAsync()`, `EvaluateAsync/ExecuteAsync/InvokeAsync`; `Options.Constraints.PromiseTimeout`.
- Debugging: "Chrome DevTools Protocol integration" via `Jint.DevTools` package (CDP server for debugging/profiling) (main README; the NuGet page for Jint.DevTools returned 404 at fetch time — version unverified).
- Threading: "Engine instances are not thread-safe"; `Prepared<Script>`/`Prepared<Module>` are thread-safe and cacheable.
- Perf (vendor): fastest managed engine on 10/12 scripts; "Native V8 remains faster on long, compute-heavy loops."
- 4.17.0: `DiscardInterpreterCaches()` for pooled engines, fixes for scripts that could crash the host process, opt-in `StackOverflowGuard` (4.16.0).
- TS inside Jint: see §3.1 (TS 1.6 worked in 2016; later TS versions hit parser issues then — modern Jint uses Acornima and ES2025, so **[inference]** `typescript@6` very likely parses now, but expect multi-second load of a 9 MB script in an interpreter; **sucrase (1 MB) is the realistic choice for Jint**.

### 4.2 Microsoft.JavaScript.NodeApi (node-api-dotnet) + Microsoft.JavaScript.LibNode **[verified]**

- `Microsoft.JavaScript.NodeApi` **0.9.27 (2026-09-03)**, MIT, "Public Preview"; net8.0/net9.0/net10.0/netstandard2.0/net472; 939 KB (https://www.nuget.org/packages/Microsoft.JavaScript.NodeApi). Repo active: commits 2026-08-07 … 2026-09-03 (vmoroz, GalaxiasKyklos) (https://api.github.com/repos/microsoft/node-api-dotnet/commits?per_page=5). README: "there may still be minor breaking API changes" (https://github.com/microsoft/node-api-dotnet). No GitHub releases are published (https://github.com/microsoft/node-api-dotnet/releases).
- `Microsoft.JavaScript.LibNode` **20.1800.215, 2025-04-02** (versions 20.1800.201–215, Feb–Apr 2025 only); meta-package depends on `LibNode.win-x86|win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64`; MIT; "libNode is a Node.js shared library … using the C-based Node-API and the Node.js embedding API" (https://www.nuget.org/packages/Microsoft.JavaScript.LibNode). `LibNode.win-x64` nupkg **27.59 MB** (https://www.nuget.org/packages/Microsoft.JavaScript.LibNode.win-x64). Repo pins `20.1800.203` (https://raw.githubusercontent.com/microsoft/node-api-dotnet/main/Directory.Packages.props). Issues #481/#464 identify 20.1800.215 as **Node 20.18.0** (https://api.github.com/search/issues?q=repo:microsoft/node-api-dotnet+libnode+is:issue).
- Embedding API (https://raw.githubusercontent.com/microsoft/node-api-dotnet/main/docs/scenarios/dotnet-js.md): `new NodeEmbeddingPlatform(libnodePath, null)`; `platform.CreateThreadRuntime(baseDir, new NodeEmbeddingRuntimeSettings { MainScript = ... })`; **"Only one NodeEmbeddingPlatform may exist per process"**; `nodejsRuntime.RunAsync(() => JSValue.RunScript("..."))`; `Import(module, property)`; **all JS must run on the environment thread** via `Run/RunAsync/Post/PostAsync`; `JSValue`s invalid off-thread, keep `JSReference`; `Uri StartInspector(int? port, string? host, bool? wait)` returns a URL VS Code / Chrome can attach to. The doc says: "The functionality is **experimental**", "It depends on an open Node.js pull request that adds the ABI-stable embedding APIs", "libnode has breaking changes between versions", prebuilt binaries only Windows/macOS/Ubuntu.
- Source (https://raw.githubusercontent.com/microsoft/node-api-dotnet/main/src/NodeApi/Runtime/NodeEmbeddingThreadRuntime.cs): "A Node.js runtime environment with a dedicated main execution thread"; constructor spawns a `Thread`, runs `runtime.RunEventLoop()` until disposal; `SynchronizationContext` "enables switching to the Node.js environment's thread and returning to the thread after an await". `NodeEmbeddingPlatformSettings` has `LibNodePath`, `Args` (default `["node"]`), `PlatformFlags`, `ConfigurePlatform` (https://raw.githubusercontent.com/microsoft/node-api-dotnet/main/src/NodeApi/Runtime/NodeEmbeddingPlatformSettings.cs).
- Upstream status: PR **nodejs/node#54660 "node-api: use c-based api for libnode embedding"** (vmoroz, opened 2024-08-30) was **closed unmerged** (draft); addaleax requested changes 2025-10-11 suggesting wrapping the existing C++ embedder API instead (https://github.com/nodejs/node/pull/54660, https://api.github.com/search/issues?q=repo:nodejs/node+author:vmoroz+is:pr+embedding). Node's official embedding doc only covers the C++ API and warns breaking changes "may occur in any semver-major release without prior warning" (https://nodejs.org/api/embedding.html).
- Issue **#472 "Add support for the current version of Node" (2026-02-13, open)**: stock Node 25.6.1 `libnode.dll` lacks `node_embedding_platform_create()`; Oct 2026 comments: patched libnode builds "are stuck on Node 20, which is out of support"; a contributor built **alethic/libnode-dotnet** (unpatched Node 26.10.0 + a C shim over `node.h`, packages `Alethic.LibNode.runtime.<rid>` on a GitHub Packages feed, MIT, 9 commits/0 stars, author "would rather not maintain this long-term") (https://github.com/microsoft/node-api-dotnet/issues/472, https://github.com/alethic/libnode-dotnet).
- Node release schedule: **Node 20 EOL 2026-04-30**; 22 Maintenance (EOL 2027-04-30); 24 Active LTS (Maintenance 2026-10-20, EOL 2028-04-30); 26 Current (LTS 2026-10-28, EOL 2029-04-30) (https://github.com/nodejs/Release).

**Does Node's type stripping work in embedded libnode?** Type stripping was added in **v22.6.0** (§1.1), so **the only published LibNode (20.18.0) cannot strip types at all** **[verified by combining the two docs]**. **[inference]** A self-built libnode ≥ 22.18/24/26 would include `lib/internal/modules/typescript.js` and the bundled amaro WASM, so `import './user.ts'` from the embedded environment should work exactly as in the CLI (the loader is in `lib/`, not in the CLI binary) — but nobody publishes such a package on NuGet today; you would own a ~1 h/RID Node build pipeline (alethic README) and an unstable C-shim ABI.

**Other constraints [verified]:** the Node event loop runs on its own dedicated thread; every JS touch must be marshaled onto it (`Run/Post`), so game-thread callbacks become cross-thread hops; one platform per process (a second mod using it would conflict); full Node API surface (fs/net) is present — sandboxing would need Node's own permission model, which I did not verify in embedded mode **[inference]**. Package weight ~28 MB nupkg per RID.

### 4.3 QuickJS / QuickJS-NG **[verified where cited]**

- quickjs-ng: "a small and embeddable JavaScript engine", fork of Bellard/Gordon QuickJS, MIT, "aims to support the latest ECMAScript specification"; no language bindings listed on the repo page (https://github.com/quickjs-ng/quickjs).
- .NET bindings: searches found **no actively maintained standalone .NET binding** for QuickJS/QuickJS-NG (`QjsIpc` 1.0.0-preview.7 wraps original QuickJS out-of-process; `vmas/QuickJS.NET` returned 404) (https://www.nuget.org/packages/QjsIpc, https://github.com/vmas/QuickJS.NET). The maintained path is **PuerTS's QuickJS backend** ("smallest binary … no debugging") (https://www.nuget.org/packages/Puerts.QuickJS.Complete).
- **[inference]** No JIT (interpreter), ~1 MB native; TS via the same in-engine transpile approach; no inspector. Not recommended given binding availability.

### 4.4 PuerTS (Tencent) **[verified]**

- "multi-language scripting solution for Unity/Unreal/DotNet"; backends V8 (default; ECMAScript only, debugging supported), QuickJS (small, no debugging), Node.js (largest, Node APIs); BSD-3-Clause (https://github.com/Tencent/puerts). `Puerts.V8.Complete` **3.0.3 (2026-09-05)**; netstandard2.1/net8.0/net9.0/net10.0; Windows x64, Linux x64, macOS universal; "AOT compilation is not supported"; "Type-safe scripting with full TypeScript declaration generation" (https://www.nuget.org/packages/Puerts.V8.Complete). **[inference]** PuerTS expects TS to be compiled by the user's toolchain (it generates `.d.ts`; it does not ship a transpiler), so you'd still add in-engine sucrase. Documentation is Unity/Unreal-centric and partly Chinese-first; a solid but less .NET-idiomatic alternative to ClearScript.

### 4.5 Deno, Bun, Hermes, Boa, YantraJS, Edge.js, ChakraCore **[verified where cited]**

- **Deno**: `deno_core` 0.412.0 is a Rust crate (`JsRuntime`, ops, extensions, snapshots); no C ABI documented (https://docs.rs/deno_core/latest/deno_core/). **[inference]** Embedding from .NET requires writing and maintaining your own Rust cdylib shim; high complexity, large binary.
- **Bun**: built on JavaScriptCore, statically linked; no embedding API found in docs or issues (https://bun.com/blog/how-bun-supports-v8-apis-without-using-v8-part-1). **[inference]** Not embeddable.
- **Hermes**: Hermes V1 is the default engine in React Native 0.84 (2026-02-11) (https://reactnative.dev/blog/2026/02/11/react-native-0.84); the "Static Hermes" codename was retired into V1 (https://www.callstack.com/podcasts/from-static-hermes-to-hermes-v1-the-road-to-default); typed/native-AOT compilation is not part of the shipped V1 per Dec-2025 reporting (https://swmansion.com/blog/welcoming-the-next-generation-of-hermes-67ab5679e184) **[reported]**. No .NET binding; mobile-oriented. Not viable.
- **Boa**: "an experimental JavaScript lexer, parser and interpreter written in Rust", 0.21.x, MIT/Unlicense, Rust-only (https://github.com/boa-dev/boa). Not viable.
- **YantraJS**: C#, Apache-2.0, "Many ES5 + ES6 features", no releases visible (https://github.com/yantrajs/yantra); the Broiler.JS fork is "0.1.0-preview", unstable, "not a security sandbox" (https://www.nuget.org/packages/Broiler.JavaScript.Runtime/). Not viable.
- **Edge.js**: ".NET hosting Node only works on Windows using desktop CLR" (https://github.com/agracio/edge-js). Not viable.
- **ChakraCore**: Microsoft support ended 2021-03-09; roadmap marked obsolete 2024-04-30; release downloads removed after 2024-05-31 (https://github.com/chakra-core/ChakraCore, https://github.com/microsoft/ChakraCore-wiki/blob/master/Roadmap.md). Not viable.
- **WASM route (AssemblyScript)**: "a variant of TypeScript … not all of TypeScript can be supported … compiles to a static WebAssembly binary" (https://www.assemblyscript.org/introduction.html). **[inference]** Users' idiomatic TS (any, unions, dynamic objects, host object graphs) won't compile; you'd also lose the host-object/dynamic interop story. Dismissed.

---

## 5. Editor tooling for end users (neovim)

- **typescript-language-server**: "a thin LSP interface" over tsserver; install `npm install -g typescript-language-server typescript@6`; run `--stdio`; notes that TS 7 "will include its own LSP implementation" (https://github.com/typescript-language-server/typescript-language-server). Requires Node **[verified by install method]**.
- **TypeScript 7 native LSP**: multi-threaded LSP built in; VS Code extension; "Other LSP-capable editors should work" (https://devblogs.microsoft.com/typescript/announcing-typescript-7-0/). nvim-lspconfig's `tsc` config runs `tsc --lsp --stdio` (root markers: lockfiles) and `tsgo.lua` is "Deprecated in favor of tsc" (https://raw.githubusercontent.com/neovim/nvim-lspconfig/master/lsp/tsc.lua, https://raw.githubusercontent.com/neovim/nvim-lspconfig/master/lsp/tsgo.lua). **[inference]** The native `tsc` binary (~28 MB) needs no Node at runtime, but `npm install typescript` is still the distribution channel; you can also ship the platform binary from `@typescript/typescript-<os>-<arch>` yourself.
- **Deno LSP**: built into the `deno` CLI, provides "a Deno view of code" (https://docs.deno.com/runtime/reference/lsp_integration/). **[inference]** Workable only if your scripts are organized as a Deno project; it will surface Deno globals/resolution that don't match a V8-in-.NET host. Not recommended.
- **Ship a `.d.ts` bundle + tsconfig**: Node's recommended tsconfig for erasable TS (`noEmit`, `target: esnext`, `module: nodenext`, `rewriteRelativeImportExtensions`, `erasableSyntaxOnly`, `verbatimModuleSyntax`) (https://nodejs.org/api/typescript.html). **[inference]** Generate `host.d.ts` from your exposed C# API (PuerTS and node-api-dotnet both have generators; for ClearScript write a small reflection-based generator) and drop a `tsconfig.json` in the user script folder with `"types": ["./host.d.ts"]`; any TS LSP then gives autocomplete without Node at runtime for the game.
- **Source maps**: Node's `--enable-source-maps` remaps stack traces for transpiled code and warns about `Error.prepareStackTrace` overrides and `Error.stack` latency (https://nodejs.org/api/cli.html). **[inference]** Bare V8 (ClearScript) does not apply source maps; with strip-only transpile (amaro/sucrase whitespace-preserving modes) you don't need them — lines/columns match the `.ts`. If you use `ts.transpileModule` with `inlineSourceMap`, remap in C# (e.g. a small VLQ decoder) when formatting errors.

---

## 6. Sandboxing & limits comparison

| | ClearScript V8 | node-api-dotnet (libnode) | Jint | QuickJS (via PuerTS) |
|---|---|---|---|---|
| fs/net by default | **None** — bare V8; `DocumentAccessFlags.None` default; host decides what to expose **[verified]** | **Full Node API** (fs, net, child_process) present **[verified: "includes Node.js APIs"]**; Node permission model not verified in embedded mode **[inference]** | None; hardened `ForUntrustedCode` profile restricts CLR/reflection/modules/`eval` **[verified]** | None (ES only) **[verified: "ECMAScript spec only"]** |
| Heap limit | `V8RuntimeConstraints.MaxOldSpaceSize/MaxNewSpaceSize`, `MaxArrayBufferAllocation`; soft `MaxRuntimeHeapSize` + `Interrupt`/`Exception` policy **[verified]** | V8 flags via `Args` (e.g. `--max-old-space-size`) **[inference]** | `LimitMemory` (cooperative) **[verified]** | QuickJS C API has memory limits; exposure through PuerTS unverified **[inference]** |
| CPU/timeout | `Interrupt()` from any thread; `MaxRuntimeStackUsage`; no built-in timer — host watchdog **[verified]** | `node::Stop`-style termination from any thread per Node docs; .NET API exposure unverified **[inference]** | `TimeoutInterval`, `MaxStatements`, `CancellationToken`, `LimitRecursion`; **cannot preempt host callbacks** **[verified]** | interrupt handler exists in C API; PuerTS exposure unverified **[inference]** |
| Threading | No affinity; internal lock; one thread at a time per isolate **[verified]** | Dedicated Node thread per runtime; all JS via `Run/Post`; one platform per process **[verified]** | Engine not thread-safe; `Prepared<T>` thread-safe **[verified]** | single-threaded runtime **[inference]** |
| Debugger | V8 inspector on `debugPort` (VS Code recommended; Chrome via URL/extension) **[verified/reported]** | `StartInspector()` → standard Node inspector URL; chrome://inspect works for Node **[verified]** | `Jint.DevTools` CDP server **[verified]** | none (PuerTS QuickJS) **[verified]** |
| Process-kill risk | V8 aborts on hard OOM if no soft limit **[reported #84]** | same V8 behavior **[inference]** | 4.17.0 fixed scripts that "could end the host process" **[verified]** | — |

---

## 7. Native library loading in a custom AssemblyLoadContext (.NET 10)

**[verified]** From https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/loading-unmanaged — P/Invoke resolution order for the *active* ALC (the one containing the assembly declaring the `DllImport`): (1) ALC cache; (2) `DllImportResolver` set via `NativeLibrary.SetDllImportResolver`; (3) `AssemblyLoadContext.LoadUnmanagedDll` override; (4) AppDomain cache + default probing (deps.json `runtimes/<rid>/native`, app paths); (5) `AssemblyLoadContext.ResolvingUnmanagedDll` event.

- `NativeLibrary.SetDllImportResolver(Assembly, DllImportResolver)`: "This per-assembly resolver is the first attempt to resolve native library loads initiated by this assembly"; "Only one resolver can be registered per assembly" (https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.setdllimportresolver).
- Plugin tutorial pattern: custom ALC with `AssemblyDependencyResolver` → `Load` via `ResolveAssemblyToPath`, `LoadUnmanagedDll` via `ResolveUnmanagedDllToPath` + `LoadUnmanagedDllFromPath`; `<EnableDynamicLoading>true</EnableDynamicLoading>` copies dependencies and `.deps.json`; "plugins should target a runtime, such as .NET 10, instead of a version of .NET Standard"; "Untrusted code cannot be safely loaded into a trusted .NET process" (https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support).
- dotnet/runtime #13472 "There's no way to call native library resolution on a specific AssemblyLoadContext" — closed 2024-12-17 without a fix: a child ALC cannot fall back to the parent's native resolution (https://github.com/dotnet/runtime/issues/13472).
- `NativeLibrary.Load` "starting with .NET 5 … does invoke the AssemblyLoadContext.LoadUnmanagedDll method and the AssemblyLoadContext.ResolvingUnmanagedDll event" when given a name (https://learn.microsoft.com/bg-bg/dotnet/api/system.runtime.interopservices.nativelibrary.load) — **[inference]** with an absolute path, the OS loader is used directly and ALC hooks are moot.

**How this applies to ClearScript [verified §2.3 + inference]:**
1. ClearScript never relies on `DllImport` name resolution for `ClearScriptV8.*`; it computes candidate absolute paths and calls `NativeLibrary.Load(path)`. Your custom ALC's `LoadUnmanagedDll` is not consulted and need not be overridden.
2. If the mod loader loads `ClearScript.V8.dll` from a file path, place `ClearScriptV8.<rid>.<ext>` either next to it or under `runtimes/<rid>/native/`. If it loads from bytes/streams, set `HostSettings.AuxiliarySearchPath` first.
3. Mod-side safety: pre-call `NativeLibrary.Load(Path.Combine(modDir, "ClearScriptV8.win-x64.dll"))` before touching ClearScript to get a clear exception if the file is missing or has a dependency problem (e.g. glibc < 2.27 on Linux).
4. A second copy of ClearScript loaded by another mod in a different ALC would try to load the same native file again; the OS returns the same module, but two managed `ClearScript.V8.dll` copies would both call `V8Environment_InitializeICU` and attempt to own the V8 platform — **[inference]** ClearScript is not designed for two managed instances in one process; JavaScriptEngineSwitcher issue #51 shows a related failure when two apps load ClearScriptV8 in one process (https://github.com/Taritsyn/JavaScriptEngineSwitcher/issues/51) **[reported]**. Coordinate with other mods (e.g. load ClearScript into the default ALC once).
5. ALC **unloading**: V8 is a process-global native runtime; collectible-ALC unload cannot free it. Design the scripting host as non-collectible / loaded once.

**How this applies to node-api-dotnet [verified]:** you pass `libnodePath` explicitly (`new NodeEmbeddingPlatform(libnodePath, …)`), so file placement is under your control; the P/Invoke surface inside `Microsoft.JavaScript.NodeApi` binds to that handle. The one-platform-per-process rule is the bigger constraint.

---

## 8. Comparison table

| Criterion | ClearScript V8 7.5.1.1 | Jint 4.17.0 | node-api-dotnet 0.9.27 + LibNode 20.18 | PuerTS 3.0.3 (V8) | QuickJS-NG | Deno/Bun/Hermes/Boa/Yantra/Chakra/Edge |
|---|---|---|---|---|---|---|
| Runs TS natively | No (needs transpiler) | No | **No** (Node 20 predates stripping); yes only with self-built Node ≥22.18 | No | No | Deno/Bun yes but not embeddable |
| Engine / perf | V8 14.7 JIT | managed interpreter | V8 (Node 20) | V8 | interpreter | — |
| Native deps / size | ~10.5 MB nupkg per RID + ICU 4.5 MB | **none** (6.7 MB nupkg) | ~28 MB nupkg per RID | V8 native (size n/a) | ~1 MB | — |
| RIDs | win/linux/osx × x86/x64/arm/arm64 | any .NET | win-x86/x64/arm64, linux-x64/arm64, osx-x64/arm64 | win-x64, linux-x64, osx-universal | any (if bound) | — |
| Host object/type exposure | rich (`AddHostObject/Type`, `HostFunctions`, dynamic `Script`) | rich (`SetValue`, CLR types, `TypeReference`) | `JSValue`, `JSExport`, generators | yes + `.d.ts` gen | via PuerTS | — |
| Task↔Promise | flags + `ToPromise/ToTask` | `TaskInterop` (experimental) | yes (on Node thread) | yes (unverified detail) | — | — |
| Sandbox default | bare V8, no fs/net | none; hardened profile | **full Node APIs** | bare V8 | bare | — |
| Limits | heap constraints, soft heap, stack, `Interrupt()` | timeout, statements, memory, recursion, token (cooperative) | V8 flags via args; thread model | unverified | unverified | — |
| Hot reload | custom `DocumentLoader` + cache discard / new engine | `Modules.Add` + new engine | re-import on Node thread | re-run | — | — |
| Debugger | V8 inspector port (VS Code; Chrome via URL/ext) | `Jint.DevTools` CDP | Node inspector (chrome://inspect) | yes (V8) | no | — |
| Threading model | any thread, one at a time | single-threaded engine | **dedicated Node thread**, marshal everything | V8 | single | — |
| Custom-ALC friendliness | good (absolute-path `NativeLibrary.Load`, `AuxiliarySearchPath`) | trivial | explicit path; one platform/process | unverified | — | — |
| License | MIT | BSD-2 | MIT | BSD-3 | MIT | — |
| Maintenance (2026) | active; last release 2026-09-04 | very active; 2026-10-07 | library active; **libnode stale (2025-04, Node 20 EOL)** | active; 2026-09-05 | engine active; no .NET binding | archived / not embeddable |

---

## 9. Recommendation ranking

1. **ClearScript V8 + in-isolate transpiler (sucrase first; `@swc/wasm-typescript` for Node-parity strip-only; `typescript@6` only if you need the full language).** Best fit for every requirement: host objects (§2.5), sync/async callbacks with Task↔Promise (§2.6), sandbox-by-default (`DocumentAccessFlags.None`, nothing exposed unless added), heap/stack/interrupt limits (§2.8), hot reload via a custom `DocumentLoader` (§2.4), inspector port (§2.10), and a native loader that cooperates with a plugin folder (§2.3, §7). Costs: ~15 MB nupkg per RID, a strong-name recompile whenever ClearScript bumps keys (7.5.1), and the need to pin TS 6 if you embed `typescript.js`. **[inference on ranking]**
2. **Jint 4.x + sucrase.** Zero native code, trivially plugin-safe, cooperative limits, CDP debugging, ES2025. Choose this if deployment simplicity and "cannot crash the game process" matter more than peak script throughput, or as a fallback engine behind the same `IScriptHost` abstraction. Avoid embedding full `typescript.js` here. **[inference]**
3. **PuerTS (V8 backend).** Game-native heritage and `.d.ts` generation, actively released in 2026, but Unity/Unreal-centric docs, no AOT, and you still need in-engine sucrase. Reasonable if you want a "batteries for games" layer and accept a less idiomatic .NET API. **[inference]**
4. **node-api-dotnet + self-built libnode ≥ 24/26.** The only route where `.ts` runs with Node's own stripping and full Node APIs — but it is blocked today by a Node-20-only NuGet, a closed upstream PR, an experimental embedding API, a mandatory dedicated Node thread, one-platform-per-process, and a 28 MB+ per-RID payload with full fs/net exposed. Revisit if Microsoft publishes a LibNode ≥ 22.18 or the alethic shim gains traction. **[verified facts, inferred ranking]**
5. **QuickJS-NG.** Small and sandboxable, but no maintained standalone .NET binding in 2026; only via PuerTS's debugger-less backend. **[inference]**
6. **Deno / Bun / Hermes / Boa / YantraJS / ChakraCore / Edge.js / AssemblyScript.** Not embeddable from .NET, archived, immature, or not actually TypeScript (§4.5).

### Concrete starting design for option 1 **[inference]**

- Load `ClearScript.V8.dll` from the mod folder via your ALC; before first engine use set `HostSettings.AuxiliarySearchPath = modDir` and `NativeLibrary.Load` the RID-specific file to fail fast.
- One shared `V8Runtime` with `V8RuntimeConstraints { MaxOldSpaceSize = 256, MaxNewSpaceSize = 16 }`, `MaxHeapSize` soft limit with `Interrupt` policy, `MaxStackUsage` set; one `V8ScriptEngine` per user script bundle (or per mod user) created with `EnableTaskPromiseConversion | UseSynchronizationContexts | EnableDynamicModuleImports | EnableDebugging | EnableRemoteDebugging` and a configurable `debugPort`.
- Load sucrase once via `V8Runtime.Compile(..., V8CacheKind.Code, out cache)` and persist the cache; expose `transform(code, {transforms:["typescript"]})` to a C# `DocumentLoader` subclass that handles `.ts` specifiers and returns `StringDocument`s in `ModuleCategory.Standard`.
- Watch the script directory; on change call `DocumentSettings.Loader.DiscardCachedDocuments()`, dispose and recreate the engine (cheapest reliable way to drop stale module records), re-register host objects, re-import the entry module.
- Game-thread `SynchronizationContext` installed when invoking scripts; watchdog thread calling `engine.Interrupt()` after N ms per event tick.
- Ship `host.d.ts` + `tsconfig.json` (erasableSyntaxOnly off if you use sucrase, since enums are supported there) so neovim users get completions from `tsc --lsp --stdio` (TS 7) or `typescript-language-server` (TS 6).

---

## 10. Source index

Node/TS/TC39: https://nodejs.org/api/typescript.html · https://github.com/nodejs/node/blob/main/lib/internal/modules/typescript.js · https://github.com/nodejs/amaro · https://devblogs.microsoft.com/typescript/announcing-typescript-5-8/ · https://www.typescriptlang.org/docs/handbook/release-notes/typescript-5-8.html · https://devblogs.microsoft.com/typescript/announcing-typescript-6-0/ · https://devblogs.microsoft.com/typescript/announcing-typescript-7-0/ · https://github.com/microsoft/typescript-go · https://github.com/microsoft/TypeScript/wiki/Using-the-Compiler-API · https://github.com/tc39/proposal-type-annotations · https://github.com/tc39/proposals/blob/main/stage-1-proposals.md · https://github.com/nodejs/Release · https://nodejs.org/api/embedding.html · https://nodejs.org/api/cli.html · https://nodejs.org/en/learn/getting-started/debugging

ClearScript: https://github.com/microsoft/ClearScript · https://github.com/microsoft/ClearScript/releases · https://www.nuget.org/packages/Microsoft.ClearScript.V8 · https://www.nuget.org/packages/Microsoft.ClearScript.V8.Native.win-x64 · https://www.nuget.org/packages/Microsoft.ClearScript.V8.ICUData · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8Proxy.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8Proxy.NetCore.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/HostSettings.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8RuntimeConstraints.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8ScriptEngineFlags.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8ScriptEngine.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8Runtime.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8RuntimeViolationPolicy.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8Settings.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8GlobalFlags.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/DocumentSettings.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/DocumentAccessFlags.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/DocumentLoader.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/IScriptEngine.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/HostFunctions.cs · https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/JavaScript/JavaScriptExtensions.cs · https://clearscript.clearfoundry.net/Tutorial/FAQtorial.html · https://clearscript.clearfoundry.net/Details/Build.html · https://github.com/microsoft/ClearScript/issues/24 · https://github.com/microsoft/clearscript/issues/42 · https://github.com/microsoft/ClearScript/issues/84 · https://github.com/microsoft/ClearScript/issues/483 · https://github.com/microsoft/ClearScript/discussions/567 · https://github.com/ClearFoundry/ClearScript/discussions/322 · https://forum.linqpad.net/discussion/comment/6849 · https://github.com/Taritsyn/JavaScriptEngineSwitcher/issues/51 · https://github.com/eswann/ClearScript.Manager · https://raw.githubusercontent.com/v8/v8/main/include/v8-locker.h · https://v8.dev/docs/embed

Transpilers: https://registry.npmjs.org/typescript/5.9.3 · https://registry.npmjs.org/typescript/latest · https://registry.npmjs.org/@typescript/typescript6/latest · https://registry.npmjs.org/@typescript/typescript-linux-x64/latest · https://registry.npmjs.org/sucrase/latest · https://github.com/alangpierce/sucrase · https://registry.npmjs.org/@swc/wasm-typescript/latest · https://registry.npmjs.org/amaro/latest · https://registry.npmjs.org/esbuild-wasm/latest · https://registry.npmjs.org/@esbuild/win32-x64/latest · https://esbuild.github.io/getting-started/ · https://esbuild.github.io/api/ · https://newreleases.io/project/npm/esbuild/release/0.12.11 · https://github.com/microsoft/TypeScript/issues/29651 · https://github.com/sebastienros/jint/issues/274

Jint: https://github.com/sebastienros/jint · https://raw.githubusercontent.com/sebastienros/jint/4.x/README.md · https://github.com/sebastienros/jint/releases · https://www.nuget.org/packages/Jint

node-api-dotnet: https://github.com/microsoft/node-api-dotnet · https://www.nuget.org/packages/Microsoft.JavaScript.NodeApi · https://www.nuget.org/packages/Microsoft.JavaScript.LibNode · https://www.nuget.org/packages/Microsoft.JavaScript.LibNode.win-x64 · https://raw.githubusercontent.com/microsoft/node-api-dotnet/main/docs/scenarios/dotnet-js.md · https://raw.githubusercontent.com/microsoft/node-api-dotnet/main/src/NodeApi/Runtime/NodeEmbeddingThreadRuntime.cs · https://raw.githubusercontent.com/microsoft/node-api-dotnet/main/src/NodeApi/Runtime/NodeEmbeddingPlatform.cs · https://raw.githubusercontent.com/microsoft/node-api-dotnet/main/src/NodeApi/Runtime/NodeEmbeddingPlatformSettings.cs · https://raw.githubusercontent.com/microsoft/node-api-dotnet/main/Directory.Packages.props · https://github.com/microsoft/node-api-dotnet/issues/472 · https://github.com/nodejs/node/pull/54660 · https://github.com/alethic/libnode-dotnet · https://api.github.com/repos/microsoft/node-api-dotnet/commits?per_page=5

Others: https://github.com/quickjs-ng/quickjs · https://www.nuget.org/packages/QjsIpc · https://github.com/Tencent/puerts · https://www.nuget.org/packages/Puerts.V8.Complete · https://www.nuget.org/packages/Puerts.QuickJS.Complete · https://docs.rs/deno_core/latest/deno_core/ · https://docs.deno.com/runtime/reference/lsp_integration/ · https://bun.com/blog/how-bun-supports-v8-apis-without-using-v8-part-1 · https://reactnative.dev/blog/2026/02/11/react-native-0.84 · https://www.callstack.com/podcasts/from-static-hermes-to-hermes-v1-the-road-to-default · https://swmansion.com/blog/welcoming-the-next-generation-of-hermes-67ab5679e184 · https://github.com/boa-dev/boa · https://github.com/yantrajs/yantra · https://www.nuget.org/packages/Broiler.JavaScript.Runtime/ · https://github.com/agracio/edge-js · https://github.com/chakra-core/ChakraCore · https://github.com/microsoft/ChakraCore-wiki/blob/master/Roadmap.md · https://www.assemblyscript.org/introduction.html

Tooling/.NET: https://github.com/typescript-language-server/typescript-language-server · https://raw.githubusercontent.com/neovim/nvim-lspconfig/master/lsp/tsc.lua · https://raw.githubusercontent.com/neovim/nvim-lspconfig/master/lsp/tsgo.lua · https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/loading-unmanaged · https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.setdllimportresolver · https://learn.microsoft.com/bg-bg/dotnet/api/system.runtime.interopservices.nativelibrary.load · https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support · https://github.com/dotnet/runtime/issues/13472
