# Manager B progress — WP7, WP9-tools, WP5, WP6

Owner paths: `src/KsaScripting.Runtime.Lua/**`, `src/KsaScripting.Runtime.V8/**`, `tests/KsaScripting.Runtime.Lua.Tests/**`, `tests/KsaScripting.Runtime.V8.Tests/**`, `tools/**`, `editor/**`, `scripts-lib/**`, `vendor/**`, `THIRD-PARTY-NOTICES.md`, `third-party-licenses/**`, this file, and the purrTTY repo (`feature/scripting`).

| WP | Status | Commits |
|---|---|---|
| WP7 purrTTY launcher contract + vendoring | **done** | purrtty `16c6d91` (feature/scripting); ksa-scripting `d8b4af2` |
| WP9-tools (fetch/build scripts, editor config, tui libs, check-dist, notices) | **done** | `a72ee8f`, `047747d`, `39bacc3`, `0d6e392`, `847a760` |
| WP5 LuaJIT runtime | **done** | `644147c`, `353d97c` (613 tests) |
| WP6 V8 runtime | **done** | `614f646`, `ba78ecc`, `64d06c1` (542 tests) |

## Log

- 2026-10-16: started WP7 and WP9-tools in parallel; pinned LuaJIT v2.1 at `2a8e017ef1ece6a95b983ffca073d30609dce7c4` (2026-10-14).
- 2026-10-16: WP9-tools scripts committed: fetch-nvim/fetch-lsp (`a72ee8f`), check-dist + notices (`047747d`), editor config + fetch-ts + build-ts-parser (`39bacc3`), LuaJIT pipeline + shim (`0d6e392`). osx-arm64 and linux-x64 LuaJIT natives built here (linux via docker); win-x64 script written, not runnable here.
- 2026-10-16: WP7 done. purrtty `feature/scripting` @ `16c6d91`: contract 1.1.0.0 (`ITerminalLauncher` etc.), GameMod launcher with main-thread pump, Display mapper + handle state machine, NoResize/Closed/Configuration forwarding/TargetPartInstanceId/FocusChanged, docs. purrtty build 0 warnings; tests 214 + 38 + 120 + 126 (2 platform skips) green. Vendored into `vendor/purrTTY/` with README pin + `LAUNCHER_CONTRACT_READY` (`d8b4af2`).
- 2026-10-16: scripts-lib tui.ts / tui.lua / README committed (`847a760`); 380 shared parser vectors pass in bun, bare V8 (node vm) and LuaJIT (sandboxed env); the vectors are ported into the runtime test projects in WP5/WP6.
- 2026-10-16: WP5 core committed (`644147c`): LuaRuntime with LibraryImport bindings + resolver, §3.7 `ksa_host`, bootstrap sandbox, coroutine waits, rate-limited cross-thread interrupt hook, memory accounting; 182 tests green. Finding: hammering `lua_sethook` from another thread at an unbounded rate with the JIT on SIGSEGVs LuaJIT; one interrupt per call (what Core's Watchdog does) is fine and the runtime rate-limits re-arming to 50 Hz (regression test). Keep the plan's in-game JIT verification gate before trusting `lua_jit_enabled = true` on Windows/Linux. Follow-up (tui.lua vectors, real-prelude coverage, waits inside user coroutines) in progress.
- Guide vs prelude: guide §5 shows `VesselRef.throttle(f)`; the Core preludes (WP3) use `v:set_throttle(f)` (Lua) — the runtime tests follow the preludes.
- 2026-10-16: WP6 core committed (`614f646`): V8ScriptRuntime (ClearScript 7.5.1.1), compiler in its own V8Runtime with persisted code cache + sucrase fallback, contained TsDocumentLoader, `__host` facade with game-thread settled promises, source-map error lines; 91 tests green. Notes for GameMod (Manager A): the dist must copy `$(TargetDir)assets/**` → `assets/` (typescript.js, sucrase.bundle.js, licences) — guide §1's CopyCustomContent list does not mention it; the runtime csproj sets `CopyLocalLockFileAssemblies` so ClearScript DLLs and `runtimes/<rid>/native/ClearScriptV8.*` land in output; the soft heap limit was initially shared by all script engines (one isolate) — superseded by `64d06c1` (one isolate per script). Follow-up (tui.ts vectors, real-prelude coverage, Core end-to-end, unobserved rejections) in progress.
- 2026-10-16: WP5 follow-up committed (`353d97c`): 380 tui.lua vectors through the runtime, real-prelude coverage, waits inside script-created coroutines, bounded host→Lua pushes; 613 tests green. For the prelude owner (WP3): `await` in `ksa.lua` raises with `error(err, 3)` but is reached through tail calls, so a failed wait inside a script helper is attributed to the helper's caller; level 2 would point at the waiting line (a Lua test pins the current behaviour).
- 2026-10-16: WP6 follow-up committed (`ba78ecc`): 380 tui.ts vectors through the runtime, real-prelude and Core end-to-end coverage, unhandled-rejection reporting via `PromiseRejectionCallback`, top-level-await errors surfaced at load, `HideHostExceptions`, canonical-path loader containment (verified on a case-sensitive APFS image too).
- 2026-10-16: `64d06c1`: one V8 isolate per script (per-script soft heap limit = `v8_heap_limit_mb`, as guide §9 says); with `v8_debug_port` set all scripts share one debuggable isolate. ~0.45 MiB V8 heap / ~2 MiB RSS per isolate, no load-time penalty. For Core (Manager A): the scheduler resets the consecutive-fault counter on every successful synchronous invoke, so errors that arrive asynchronously (async hook failures, unhandled rejections via ReportError) never reach `fault_limit` for hooks that run every slot.

## Verification (2026-10-16, macOS arm64)

- `dotnet build ksa-scripting.slnx -nologo -v quiet`: 0 warnings, 0 errors. `dotnet test ksa-scripting.slnx --nologo -v quiet`: Core 816, Runtime.Lua 613, Runtime.V8 542 — all green, quiet.
- purrtty `feature/scripting` @ `16c6d91`: `dotnet build purrtty.slnx` 0 warnings; `dotnet test purrtty.slnx` 214 + 38 + 120 + 126 (2 platform skips) green.
- Scripts executed here: `tools/fetch-nvim.sh` and `tools/fetch-lsp.sh` (osx-arm64, win-x64, linux-x64), `tools/fetch-ts.sh`, `tools/build-ts-parser.sh` (all three RIDs via cc / zig cc) and `--queries`, `tools/build-luajit.sh` (osx-arm64 native, linux-x64 in docker), `tools/check-dist.py --self-test` and against the deployed dist (0 errors; warnings: win-x64 lua51.dll absent, purrTTY/ModMenu DLL copies).
- Not verifiable here: the PowerShell scripts (`build-luajit.ps1`, `fetch-*.ps1`; no pwsh on this Mac), the win-x64 LuaJIT build, loading the linux-x64/win-x64 natives in a .NET process, nvim.exe on Windows (needs VCRUNTIME140.dll, not in the zip), and everything in-game (purrTTY launcher windows/tabs/in-world quads, JIT-on interrupt inside the game process).

## Notes for other work packages

- Dist: `assets/` (typescript.js, sucrase.bundle.js + licences) must be in the dist next to `KsaScripting.Runtime.V8.dll` — done by Manager A in `2b44a04`.
- `editor/luarc.json` and `editor/tsconfig.template.json` are reference templates matching what the stub generators write into the scripts folder; they are not part of the §1 dist copy list.
- Editor env: `KSA_LSP_DIR=<ModDir>/editor/<rid>/lsp`, `KSA_NOSHELL=<ModDir>/editor/noshell[.cmd]` (init.lua falls back to the stub next to `editor/config/`), optional `KSA_SERVER_FILE`.
- Linux natives (LuaJIT built on ubuntu:22.04) need glibc ≥ 2.34 — the same floor as the shipped Neovim.
- Guide §5 names (`VesselRef.throttle(f)`) differ from the WP3 preludes (`setThrottle` / `set_throttle`); runtime tests follow the preludes.
