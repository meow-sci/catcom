# Manager A progress (WP1, WP2, WP3, WP4, WP8)

Owner paths: `catcom.slnx`, `Directory.Build.props`, `.gitignore`, `src/Catcom.Core/**`, `src/Catcom.Ksa/**`, `src/Catcom.GameMod/**`, `tests/Catcom.Core.Tests/**`, `tests/Catcom.Ksa.CompileCheck/**`, `docs/progress/manager-a.md`, `docs/VALIDATION.md`, `README.md`, `.github/workflows/**`.

## Contract notes for Manager B (read these)

- Guide §3.7 (commit `f1ef35c`) pins the runtime ↔ prelude ↔ host conventions: `IHostFacade.EnterScript/ReportError`, public `HostWait`, the `__host` / `ksa_host` surfaces and return conventions (JS: `wait` → Promise, Solver `act` → Promise; Lua: `(ok, value)` pairs, wait tables yielded by the prelude), context maps with `"$ctx": true` passed through `ksa.makeContext` / `ksa.make_context`, option-map keys, wait kinds, handle/read paths and targeted events.
- `src/Catcom.Core/Hooks/EventCatalog.cs` is the canonical v1 event list (names, payload fields, producers).
- Preludes (WP3) will live at `src/Catcom.Core/Api/Preludes/ksa.js` and `ksa.lua`.
- WP3 naming corrections to guide §5 (JS getter/method collisions, Lua reserved word): value-setting methods use a `set` prefix (`setThrottle`, `setAttitudeMode`, `setLights`, …); Lua uses `wait_until` / `wait_event` / `next_step` / `next_frame` and snake_case members with colon method calls.

## WP1 — scaffold + Core contracts

- Landed: solution (`catcom.slnx`, 9 projects), `Directory.Build.props` (KSAFolder tiers, `KsaAssembliesPresent`, `CatcomDistDir` = `$CATCOM_DIST_DIR` else `$HOME/repos/meow-sci/mods/catcom/`), csproj stubs for Runtime.Lua / Runtime.V8 / their tests (handed to Manager B), CompileCheck stub, GameMod csproj + `mod.toml` + `CopyCustomContent` dist target, placeholder `vendor/purrTTY/` DLLs (from gatOS's pin; WP7 replaces them).
- Core: `Values` (ScriptValue, ScriptCallable, SnapshotPath), `Runtime` (IScriptRuntime and friends, IRuntimeSet/RuntimeSet, ScriptLanguages), `Hosting` (IHostFacade incl. EnterScript/ReportError, IGameBridge, HostWait, ActResult, BindingOptions, EventDescriptor, CommandResultExtensions), `Logging`, `Config` (ScriptingConfig with the §9 keys, AtomicFile), ported gatOS `Commands` and `Snapshots` (records + Sanitize/EnumText/EventDiffer/SampleClock + a minimal `Formats`), `ScriptViews`/`TimeView`, `Scheduling.GameThreadQueue` (+ PostCommand/PostWork/DrainWork), `Hooks.EventCatalog`.
- Deviations: telemetry helpers live in `Catcom.Core.Snapshots` (not Commands); `GameThreadQueue.Drain` also guards observer callbacks; dist target skips `runtimes/**/.gitignore`.
- Tests: Core.Tests 240 passing; Runtime.Lua.Tests / Runtime.V8.Tests 1 placeholder each.
- Commits: `f1ef35c` (guide §3.7), `ce6ee19` (WP1).

## WP3 — API schema, generators, preludes

- Landed (`8d11cc4`): `Core.Api` (ApiSchema with reflected `*Info` classes, refs, contexts, terminal, events), `StubGenerators` (host.d.ts verified with tsc 5.9.3; LuaCATS verified with `luajit -bl`), `StubWriter.WriteAll(scriptsDir)`, preludes `src/Catcom.Core/Api/Preludes/ksa.js` (ES module, needs `globalThis.__host` installed BEFORE it is evaluated; sets `globalThis.ksa`; `ksa.makeContext`) and `ksa.lua` (needs `ksa_host` in the env; returns `ksa`, sets `_G.ksa`; `ksa.make_context`; `ksa.__exports`; waits via `coroutine.yield({["$wait"]=id})`).
- Notes for runtimes (Manager B): JS `act` passes `values` as a JS array of numbers — convert to `double[]`; JS option maps may contain extra keys (e.g. `description` for `command`) — ignore unknown keys; Lua waiting calls outside a coroutine raise before any host call.
- Guide §5 naming corrections added (set-prefix rule, Lua `wait_until`/`wait_event`/`next_step`/`next_frame`, colon methods).
- Golden files embed `EventCatalog` text: after editing `EventCatalog.cs` regenerate with `KSA_UPDATE_GOLDEN=1 dotnet test tests/Catcom.Core.Tests --filter FullyQualifiedName~Golden` and review the diff.
- Tests: 46 (Api).

## WP4 — Ksa facade

- Landed: `9c085f5` (verbatim gatOS port: readers, actuators, anchors, VesselForceRender), `16e4790` (ActionExecutor, VesselCache, KsaBridge, SlotHost, 24 event patch classes, differ aliasing, KsaSaveHooks, IEventSink/IStepSink + EventBusSink/SchedulerStepSink, CompileCheck stubs).
- Every patch target verified against decomp 5554; patches applied in a scratch harness against the real KSA.dll on macOS (PE machine field patched); no Ksa unit tests (game-coupled, compile-removed in CI).
- Wiring for GameMod: `ActionExecutor(new KsaHealth(), controlAllVessels, debugNamespace)` → `VesselCache()` → `KsaBridge(cache, executor, queue)` → `SlotHost(new SchedulerStepSink(scheduler), new EventBusSink(bus), queue, executor, cache, bridge).Install()`; `EventPatches.Install(new EventBusSink(bus))` on the game thread; `[StarMapAfterOnFrame]` → `slots.OnAfterFrame(dtPlayer)`; `KsaSaveHooks.SaveWritten(dir)` / `GameLoaded(dir?)` for the state sidecar; unload: `EventPatches.Uninstall(); slots.Uninstall(); ActionExecutor.Teardown()`.

## WP2 — Core hooks / scheduler / catalog / terminals / persistence / facade

- Landed (`88e619e`): HookRegistry, BindingStore (`[[binding]]` TOML), EventBus (sync dispatch, deferral while an Invoke/Advance is running — deferred sync subscribers get the event in the next slot with `sync = false`; `$script` targeting hidden from payloads), Scheduler (StepSlot order per guide §3.4, all §3.7 wait kinds, budgets, fault limit, watchdog disable), Watchdog (nestable), ScriptCatalog (+ ScriptHeader, ScriptWatcher), ScriptTerminal/TerminalRegistry (`CopyRingBuffer()`), ScriptStateStore, HostFacade (+ `HostFacadeOptions.FromConfig`, per-script output ring, `RecordError`), NullRuntime.
- GameMod must wire the Scheduler's `onScriptError` to `facade.RecordError`.
- Tests: Core.Tests 626 total at this point (3 consecutive green runs).

## WP8 — GameMod

- Landed: `f00c8e1` (Core.Editor: MsgPackRpc codec, NvimRpcClient, LoopbackPorts; 190 tests incl. a real headless-nvim smoke test that self-skips without nvim), `2b44a04` (dist: V8 `assets/` shipped, game DLLs denied in the glob, Version 0.1.0), `0229d11` (Mod lifecycle, ScriptingHost composition root, ModLogSink, RuntimeLoader (reflection: `LuaRuntime`, `V8ScriptRuntime`/`V8Runtime`), console `script run|start|stop|reload|list|edit|stubs|events|call`, ScriptTerminalShell `ksa-script`, REPL shells `ksa-lua-repl`/`ksa-ts-repl`, StateSidecar `<save>/catcom-state.toml`, TerminalPlacer + `terminals.toml` over the purrTTY launcher, EditorLauncher, Script Manager window with all tabs/pickers/templates, hotkey guard, menu fallback).
- Verified off-game: builds Debug/Release/game-free with zero warnings; scratch harnesses ran the real ScriptingHost + Lua/V8 runtimes against PE-patched KSA DLLs with a fake launcher, the window on native Dear ImGui (23 scenarios), templates type-checked (tsc 5.9.3) and luajit-checked, EditorLauncher end-to-end against the real nvim.
- Dist check (`python3 tools/check-dist.py ~/repos/meow-sci/mods/catcom --rids linux-x64,osx-arm64`): 1 error — no `LICENSE` file in the repo (project licence is the user's decision); WARNs for ModMenu.Attributes / purrTTY contract DLLs are by design (fallback when those mods are absent). The win-x64 `lua51.dll` is built on Windows only.
- For Manager B: the V8 runtime writes its compiler cache under `scripts/cache`; `ModPaths.CacheDir` (`<data>/cache`) is the intended place (RuntimeOptions has no cache dir field — derive it from `ScriptsDir`'s parent or add a field).

## Docs / CI

- `34650cd`: README, docs/VALIDATION.md (in-game checklist), .github/workflows/build.yml (ubuntu/windows/macos-14 matrix).

## Totals (2026-10-16, end of Manager A's WPs)

- `dotnet build catcom.slnx`: 0 warnings, 0 errors. `dotnet test`: Core.Tests 816, Runtime.Lua.Tests 613, Runtime.V8.Tests 498 — all green.
