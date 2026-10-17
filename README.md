# catcom

In-band scripting for Kitten Space Agency: Lua (LuaJIT) and TypeScript (V8 via ClearScript) scripts that run inside the game process on the game thread, with a gatOS-style read/write API (`ksa.vessel(id).setThrottle(0.5)`), game-loop event hooks (`ksa.on("engine.activate", …)`, `ksa.every("step", …)`), awaitable waits on the sim clock, script-owned virtual terminals rendered by purrTTY in 2D windows or on vehicle parts, and a shipped Neovim editor inside purrTTY with LSP completion from generated stubs.

Status: v0.1.0 implemented and verified by builds, unit tests and off-game harnesses on macOS; in-game validation is pending (see [docs/VALIDATION.md](docs/VALIDATION.md)). Built against KSA 2026.10.10.5554.

## Layout

| Path | Contents |
|---|---|
| `src/Catcom.Core` | Game-free core: `ScriptValue`, runtime abstraction (`IScriptRuntime`), host facade, hooks/bindings, Step-slot scheduler and watchdog, script catalog and file watcher, script terminals, state store, API schema + stub generators, the `ksa.js` / `ksa.lua` preludes, the Neovim msgpack-RPC client, ported gatOS snapshot and command models. |
| `src/Catcom.Ksa` | Everything that touches KSA: readers and actuators ported from gatOS, `ActionExecutor`, `KsaBridge`, the Step/Solver slot patches and the event patches. |
| `src/Catcom.Runtime.Lua` | LuaJIT runtime (sandboxed `lua_State`, coroutine waits, count-hook interrupt). |
| `src/Catcom.Runtime.V8` | V8 runtime (TypeScript transpile, ES modules, promises, interrupt, heap limits, source maps). |
| `src/Catcom.GameMod` | StarMap entry: lifecycle, composition root, Script Manager window, console `script` verbs, REPL and terminal shells, terminal placement, editor launcher, dist target. |
| `tests/` | NUnit suites for Core and both runtimes; `Catcom.Ksa.CompileCheck` (one Harmony stub per patched member — build-as-alarm on game updates). |
| `editor/`, `scripts-lib/`, `tools/` | Shipped nvim config, the `tui.ts` / `tui.lua` terminal helpers, the fetch scripts (`fetch-all.*` and the per-component ones), the zig cross-build of the natives (`build-natives.sh`), `check-dist.py`, and the pins (`*.pin`, `assets.lock`). |
| `src/Catcom.PurrTty` | The only assembly that touches purrTTY's contract (shells, launcher, editor window); loaded by the mod at runtime only when purrTTY is installed. |
| `src/Catcom.Dist` | Deploy project: builds everything and copies the mod folder (the dist target). |
| `vendor/purrTTY/` | Pinned purrTTY contract DLLs, **compile-time only** (never shipped: StarMap resolves them from purrTTY's load context). |
| `docs/IMPLEMENTATION_GUIDE.md` | The build book: contracts, conventions, API, work packages. |
| `docs/research/`, `plans/`, `spikes/` | OKF research bundle, plans, runnable spikes. |

## Build and test

```bash
tools/fetch-all.sh                       # once per clone (and after a pin under tools/ changes): EVERY shipped binary for ALL
                                         # three platforms (win-x64, linux-x64, osx-arm64), from any host. Windows: tools/fetch-all.ps1
dotnet build catcom.slnx -nologo -v quiet
dotnet test  catcom.slnx --nologo -v quiet
```

`tools/fetch-all.*` downloads, never compiles: the TypeScript assets, Neovim and the language servers from their upstream releases, and the LuaJIT library plus the tree-sitter TypeScript parser from this repo's `natives-v*` GitHub release (cross-built by the Linux CI job with zig, `tools/build-natives.sh`; the repo is private, so that step needs `gh auth login` or a `GH_TOKEN`). Everything lands in gitignored folders (`editor/<rid>/`, `src/*/runtimes/`, `src/*/assets/`, `tools/.cache/`); nothing downloaded is ever committed. The game-coupled projects resolve the KSA reference assemblies from `KSA_DLL_DIR` or a sibling `ksa-game-assemblies/current/dll/` checkout; without them their sources are compile-removed, no dist is deployed, and everything else still builds and tests.

## Deploy

```bash
dotnet build src/Catcom.Dist -c Release               # deploys to $CATCOM_DIST_DIR, else ~/repos/meow-sci/mods/catcom (macOS/Linux)
                                                       # or %USERPROFILE%\Documents\My Games\Kitten Space Agency\mods\catcom (Windows)
python3 tools/check-dist.py "$HOME/repos/meow-sci/mods/catcom"
```

The dist always contains every binary for all three platforms; the build **fails** (before touching the old dist) if any fetched binary is missing, so a mod folder is never produced incomplete. CI (`.github/workflows/build.yml`, one Linux job) does the same end to end and uploads the folder as the `catcom-dist` artifact.

Install purrTTY (with the launcher contract) for terminals, REPL shells and the editor (catcom never ships purrTTY's DLLs; it imports them from purrTTY through StarMap); ModMenu is optional (a fallback "Scripting" menu is injected without it). User scripts live in `<Documents>/My Games/Kitten Space Agency/mods/catcom/scripts/`; open the Script Manager with F11 (configurable).
