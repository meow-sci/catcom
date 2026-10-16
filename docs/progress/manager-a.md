# Manager A progress (WP1, WP2, WP3, WP4, WP8)

Owner paths: `ksa-scripting.slnx`, `Directory.Build.props`, `.gitignore`, `src/KsaScripting.Core/**`, `src/KsaScripting.Ksa/**`, `src/KsaScripting.GameMod/**`, `tests/KsaScripting.Core.Tests/**`, `tests/KsaScripting.Ksa.CompileCheck/**`, `docs/progress/manager-a.md`, `docs/VALIDATION.md`, `README.md`, `.github/workflows/**`.

## Contract notes for Manager B (read these)

- Guide §3.7 (commit `f1ef35c`) pins the runtime ↔ prelude ↔ host conventions: `IHostFacade.EnterScript/ReportError`, public `HostWait`, the `__host` / `ksa_host` surfaces and return conventions (JS: `wait` → Promise, Solver `act` → Promise; Lua: `(ok, value)` pairs, wait tables yielded by the prelude), context maps with `"$ctx": true` passed through `ksa.makeContext` / `ksa.make_context`, option-map keys, wait kinds, handle/read paths and targeted events.
- `src/KsaScripting.Core/Hooks/EventCatalog.cs` is the canonical v1 event list (names, payload fields, producers).
- Preludes (WP3) will live at `src/KsaScripting.Core/Api/Preludes/ksa.js` and `ksa.lua`.
- WP3 naming corrections to guide §5 (JS getter/method collisions, Lua reserved word): value-setting methods use a `set` prefix (`setThrottle`, `setAttitudeMode`, `setLights`, …); Lua uses `wait_until` / `wait_event` / `next_step` / `next_frame` and snake_case members with colon method calls.

## WP1 — scaffold + Core contracts

- Landed: solution (`ksa-scripting.slnx`, 9 projects), `Directory.Build.props` (KSAFolder tiers, `KsaAssembliesPresent`, `KsaScriptingDistDir` = `$KSA_SCRIPTING_DIST_DIR` else `$HOME/repos/meow-sci/mods/ksa-scripting/`), csproj stubs for Runtime.Lua / Runtime.V8 / their tests (handed to Manager B), CompileCheck stub, GameMod csproj + `mod.toml` + `CopyCustomContent` dist target, placeholder `vendor/purrTTY/` DLLs (from gatOS's pin; WP7 replaces them).
- Core: `Values` (ScriptValue, ScriptCallable, SnapshotPath), `Runtime` (IScriptRuntime and friends, IRuntimeSet/RuntimeSet, ScriptLanguages), `Hosting` (IHostFacade incl. EnterScript/ReportError, IGameBridge, HostWait, ActResult, BindingOptions, EventDescriptor, CommandResultExtensions), `Logging`, `Config` (ScriptingConfig with the §9 keys, AtomicFile), ported gatOS `Commands` and `Snapshots` (records + Sanitize/EnumText/EventDiffer/SampleClock + a minimal `Formats`), `ScriptViews`/`TimeView`, `Scheduling.GameThreadQueue` (+ PostCommand/PostWork/DrainWork), `Hooks.EventCatalog`.
- Deviations: telemetry helpers live in `KsaScripting.Core.Snapshots` (not Commands); `GameThreadQueue.Drain` also guards observer callbacks; dist target skips `runtimes/**/.gitignore`.
- Tests: Core.Tests 240 passing; Runtime.Lua.Tests / Runtime.V8.Tests 1 placeholder each.
- Commits: `f1ef35c` (guide §3.7), `ce6ee19` (WP1).

## WP3 — API schema, generators, preludes

- Landed (`8d11cc4`): `Core.Api` (ApiSchema with reflected `*Info` classes, refs, contexts, terminal, events), `StubGenerators` (host.d.ts verified with tsc 5.9.3; LuaCATS verified with `luajit -bl`), `StubWriter.WriteAll(scriptsDir)`, preludes `src/KsaScripting.Core/Api/Preludes/ksa.js` (ES module, needs `globalThis.__host` installed BEFORE it is evaluated; sets `globalThis.ksa`; `ksa.makeContext`) and `ksa.lua` (needs `ksa_host` in the env; returns `ksa`, sets `_G.ksa`; `ksa.make_context`; `ksa.__exports`; waits via `coroutine.yield({["$wait"]=id})`).
- Notes for runtimes (Manager B): JS `act` passes `values` as a JS array of numbers — convert to `double[]`; JS option maps may contain extra keys (e.g. `description` for `command`) — ignore unknown keys; Lua waiting calls outside a coroutine raise before any host call.
- Guide §5 naming corrections added (set-prefix rule, Lua `wait_until`/`wait_event`/`next_step`/`next_frame`, colon methods).
- Golden files embed `EventCatalog` text: after editing `EventCatalog.cs` regenerate with `KSA_UPDATE_GOLDEN=1 dotnet test tests/KsaScripting.Core.Tests --filter FullyQualifiedName~Golden` and review the diff.
- Tests: 46 (Api).

## WP4 — Ksa facade

- Landed: `9c085f5` (verbatim gatOS port: readers, actuators, anchors, VesselForceRender), `16e4790` (ActionExecutor, VesselCache, KsaBridge, SlotHost, 24 event patch classes, differ aliasing, KsaSaveHooks, IEventSink/IStepSink + EventBusSink/SchedulerStepSink, CompileCheck stubs).
- Every patch target verified against decomp 5554; patches applied in a scratch harness against the real KSA.dll on macOS (PE machine field patched); no Ksa unit tests (game-coupled, compile-removed in CI).
- Wiring for GameMod: `ActionExecutor(new KsaHealth(), controlAllVessels, debugNamespace)` → `VesselCache()` → `KsaBridge(cache, executor, queue)` → `SlotHost(new SchedulerStepSink(scheduler), new EventBusSink(bus), queue, executor, cache, bridge).Install()`; `EventPatches.Install(new EventBusSink(bus))` on the game thread; `[StarMapAfterOnFrame]` → `slots.OnAfterFrame(dtPlayer)`; `KsaSaveHooks.SaveWritten(dir)` / `GameLoaded(dir?)` for the state sidecar; unload: `EventPatches.Uninstall(); slots.Uninstall(); ActionExecutor.Teardown()`.
