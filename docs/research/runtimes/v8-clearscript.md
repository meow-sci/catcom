---
type: Runtime Evaluation
title: V8 via Microsoft ClearScript
description: Evaluation of embedding Google V8 in the KSA mod through Microsoft.ClearScript.V8 7.5.1.1 - packaging, native-library location, ES modules and custom loaders, host interop, Task/Promise interop, resource limits, threading, debugging, and the process-global constraints that matter for a mod loader.
resource: https://github.com/microsoft/ClearScript
tags: [runtime, javascript, v8, clearscript, typescript]
sources:
  - id: report
    resource: /references/js-runtimes-report-2026-10-16.md
    title: JS/TS runtime embedding research report (2026-10-16)
  - id: nuget
    resource: https://www.nuget.org/packages/Microsoft.ClearScript.V8
    title: Microsoft.ClearScript.V8 on NuGet
  - id: releases
    resource: https://github.com/microsoft/ClearScript/releases
    title: ClearScript releases
  - id: v8proxy
    resource: https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8Proxy.cs
    title: V8Proxy.cs (native library probe)
  - id: docsettings
    resource: https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/DocumentSettings.cs
    title: DocumentSettings.cs
  - id: constraints
    resource: https://raw.githubusercontent.com/microsoft/ClearScript/master/ClearScript/V8/V8RuntimeConstraints.cs
    title: V8RuntimeConstraints.cs
  - id: spike
    resource: /spikes/v8-typescript-spike.md
    title: V8 TypeScript spike result
  - id: jses51
    resource: https://github.com/Taritsyn/JavaScriptEngineSwitcher/issues/51
    title: Two ClearScript copies in one process (JavaScriptEngineSwitcher #51)
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:00:00Z }
status: stable
---

# Status (2026-10-16)

- **Microsoft.ClearScript.V8 7.5.1.1**, published 2026-09-04, bundles **V8 14.7.173.23**; MIT; last commit 2026-09-04; cadence 1–2 releases/year, single primary maintainer.[^nuget][^releases]
- 7.5.1 changed the strong-name key (all hosts recompile); 7.5.0 requires glibc ≥ 2.27 on Linux.[^releases]
- Packages: `Microsoft.ClearScript.Core`, `.V8` (depends on `.V8.ICUData` 4.5 MB and `Newtonsoft.Json ≥ 13.0.3`), and `Microsoft.ClearScript.V8.Native.<rid>` for win-x86/x64/arm64, linux-x64/arm/arm64, osx-x64/arm64 (win-x64 nupkg 10.5 MB). Managed files to ship: `ClearScript.Core.dll`, `ClearScript.V8.dll`, `ClearScript.V8.ICUData.dll`, `Newtonsoft.Json.dll`, plus `ClearScriptV8.<rid>.<dll|so|dylib>`.[^report]

# Native library location (fits the mod folder)

`V8Proxy` computes the file name `ClearScriptV8.{win|linux|osx}-{x86|x64|arm|arm64}.{dll|so|dylib}` and probes, in order: `<assemblyDir>/runtimes/<rid>/native`, `<assemblyDir>`, `AppDomain.BaseDirectory`, `RelativeSearchPath`, then every entry of **`HostSettings.AuxiliarySearchPath`**; it loads with **`NativeLibrary.Load(absolutePath)`**. StarMap's ALC not overriding `LoadUnmanagedDll` is therefore irrelevant: place the native beside `ClearScript.V8.dll` in the mod folder, or set `AuxiliarySearchPath` before the first engine is created. After load it verifies the native version string against the managed version and initializes ICU from an embedded resource.[^v8proxy][^report]

Process-global constraints:

- **One V8 platform per process.** Two mods each loading their own `ClearScript.V8.dll` copy would both try to own the V8 platform; a related failure is documented when two apps load ClearScriptV8 in one process.[^jses51] If another mod ever needs V8, share the assembly through StarMap `ExportedAssemblies`/`ImportedAssemblies` (as purrTTY does for its contract) so there is one managed copy.
- **No unload.** V8 cannot be freed by ALC unload; design the host as load-once.

# ES modules and transparent TypeScript

`DocumentSettings` provides `Loader`, `AccessFlags` (default `None` = no file or web loading), `SearchPath`, `ContextCallback`, `AddSystemDocument(...)`. Subclass `DocumentLoader` and override `LoadDocumentAsync(settings, sourceInfo, specifier, category, contextCallback)` to transpile `.ts` on load and return a `StringDocument` in `ModuleCategory.Standard`; `EnableDynamicModuleImports` enables `import()`; `import.meta.setResult` returns module results.[^docsettings] The spike proved this end to end with a two-file `.ts` graph importing in 4 ms.[^spike] Hot reload: `DiscardCachedDocuments()` plus a fresh engine (module records are cached per specifier in V8) or cache-busting specifiers.

# Host interop

`AddHostObject`, `AddHostType(s)`, `HostFunctions`, `Script` (dynamic globals), `Invoke`, `Execute/Evaluate(DocumentInfo, code)`, `Compile(..., V8CacheKind.Code, out byte[] cache)` and `Compile(..., cacheBytes, out bool accepted)`. 7.5.1 made `HostItemFlags.DirectAccess` the default for fully public classes and added script-side disposal of host objects. `FastProxy` (7.5.0) is the low-overhead path for hot host objects.[^report]

Task ↔ Promise: `EnableTaskPromiseConversion` (async script exports return `Task<object>` to C#), `EnableValueTaskPromiseConversion`, `UseSynchronizationContexts` (so continuations re-enter on a game-thread `SynchronizationContext`), `ToPromise()/ToTask()` extensions, `PromiseHook`, `PromiseRejectionCallback` (7.5.1.1).[^report]

# Limits and interruption

`V8RuntimeConstraints { MaxNewSpaceSize, MaxOldSpaceSize, HeapExpansionMultiplier, MaxArrayBufferAllocation }`; soft `MaxRuntimeHeapSize` + `RuntimeHeapSizeSampleInterval` + `RuntimeHeapSizeViolationPolicy` (`Interrupt` default, or `Exception`); `MaxRuntimeStackUsage`; `GetRuntimeHeapInfo()`; `Interrupt()` callable from any thread. There is **no CPU-time primitive**: a watchdog thread calling `Interrupt()` is the mechanism, and the spike confirmed it halts `for(;;){}` with `ScriptInterruptedException`. V8 aborts the process on hard OOM, so a soft heap limit is mandatory.[^constraints][^spike]

# Threading

Engines have no thread affinity but use an internal lock; only one thread at a time may use an isolate. For the game: call the engine from the game thread for hooks, marshal async completions via `UseSynchronizationContexts`, and use a watchdog thread for `Interrupt()`.[^report]

# Debugging

Constructor `debugPort` + `EnableDebugging | EnableRemoteDebugging` + `AwaitDebuggerAndPauseOnStart` expose the V8 inspector protocol over WebSocket (commonly port 9222). VS Code attach is the maintainer-recommended client; Chrome DevTools works via an explicit `devtools://…?ws=` URL or extension, not `chrome://inspect` auto-discovery.[^report]

# Measured on this workstation (osx-arm64)

First engine 32 ms (platform init), later engines ~0 ms; typescript.js 6.0.3 (8.9 MiB) executes in 75–79 ms cold / 48 ms with a 2.4 MiB code cache; `ts.transpileModule` 0.7–0.9 ms per small file; heap 29 MiB with the compiler resident.[^spike]

# Verdict

The strongest V8 option: well-maintained, MIT, sandbox-by-default, hard resource limits, custom module loader for transparent TS, inspector debugging, and a native loader that already fits a plugin folder. Costs: roughly 15 MB per RID on disk, a recompile on ClearScript key changes, process-global V8 ownership, and the need to pin TypeScript 6 if the full compiler is embedded (TypeScript 7 has no JS API).

[^report]: JS/TS runtime embedding research report (2026-10-16)
[^nuget]: Microsoft.ClearScript.V8 on NuGet
[^releases]: ClearScript releases
[^v8proxy]: V8Proxy.cs (native library probe)
[^docsettings]: DocumentSettings.cs
[^constraints]: V8RuntimeConstraints.cs
[^spike]: V8 TypeScript spike result
[^jses51]: Two ClearScript copies in one process (JavaScriptEngineSwitcher #51)
