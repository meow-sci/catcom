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
