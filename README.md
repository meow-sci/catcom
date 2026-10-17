# ksa-scripting

In-band scripting for Kitten Space Agency: Lua (LuaJIT) and TypeScript (V8 via ClearScript) scripts that run inside the game process on the game thread, with a gatOS-style read/write API (`ksa.vessel(id).setThrottle(0.5)`), game-loop event hooks (`ksa.on("engine.activate", …)`, `ksa.every("step", …)`), awaitable waits on the sim clock, script-owned virtual terminals rendered by purrTTY in 2D windows or on vehicle parts, and a shipped Neovim editor inside purrTTY with LSP completion from generated stubs.

Status: v0.1.0 implemented and verified by builds, unit tests and off-game harnesses on macOS; in-game validation is pending (see [docs/VALIDATION.md](docs/VALIDATION.md)). Built against KSA 2026.10.10.5554.

## Layout

| Path | Contents |
|---|---|
| `src/KsaScripting.Core` | Game-free core: `ScriptValue`, runtime abstraction (`IScriptRuntime`), host facade, hooks/bindings, Step-slot scheduler and watchdog, script catalog and file watcher, script terminals, state store, API schema + stub generators, the `ksa.js` / `ksa.lua` preludes, the Neovim msgpack-RPC client, ported gatOS snapshot and command models. |
| `src/KsaScripting.Ksa` | Everything that touches KSA: readers and actuators ported from gatOS, `ActionExecutor`, `KsaBridge`, the Step/Solver slot patches and the event patches. |
| `src/KsaScripting.Runtime.Lua` | LuaJIT runtime (sandboxed `lua_State`, coroutine waits, count-hook interrupt). |
| `src/KsaScripting.Runtime.V8` | V8 runtime (TypeScript transpile, ES modules, promises, interrupt, heap limits, source maps). |
| `src/KsaScripting.GameMod` | StarMap entry: lifecycle, composition root, Script Manager window, console `script` verbs, REPL and terminal shells, terminal placement, editor launcher, dist target. |
| `tests/` | NUnit suites for Core and both runtimes; `KsaScripting.Ksa.CompileCheck` (one Harmony stub per patched member — build-as-alarm on game updates). |
| `editor/`, `scripts-lib/`, `tools/` | Shipped nvim config, the `tui.ts` / `tui.lua` terminal helpers, native build and asset fetch scripts, `check-dist.py`. |
| `src/KsaScripting.PurrTty` | The only assembly that touches purrTTY's contract (shells, launcher, editor window); loaded by the mod at runtime only when purrTTY is installed. |
| `src/KsaScripting.Dist` | Deploy project: builds everything and copies the mod folder (the dist target). |
| `vendor/purrTTY/` | Pinned purrTTY contract DLLs, **compile-time only** (never shipped: StarMap resolves them from purrTTY's load context). |
| `docs/IMPLEMENTATION_GUIDE.md` | The build book: contracts, conventions, API, work packages. |
| `docs/research/`, `plans/`, `spikes/` | OKF research bundle, plans, runnable spikes. |

## Build and test

```bash
tools/build-luajit.sh osx-arm64          # once per RID: the LuaJIT native (linux-x64 on Linux; tools/build-luajit.ps1 on Windows)
tools/fetch-ts.sh                        # once: typescript.js + sucrase for the V8 runtime
dotnet build ksa-scripting.slnx -nologo -v quiet
dotnet test  ksa-scripting.slnx --nologo -v quiet
```

The game-coupled projects resolve the KSA reference assemblies from `KSA_DLL_DIR` or a sibling `ksa-game-assemblies/current/dll/` checkout; without them their sources are compile-removed and everything else still builds and tests.

## Deploy

```bash
dotnet build src/KsaScripting.Dist -c Release         # deploys to $KSA_SCRIPTING_DIST_DIR, else ~/repos/meow-sci/mods/ksa-scripting (macOS/Linux)
                                                       # or %USERPROFILE%\Documents\My Games\Kitten Space Agency\mods\ksa-scripting (Windows)
tools/fetch-nvim.sh; tools/fetch-lsp.sh; tools/build-ts-parser.sh   # optional editor payload (per RID)
python3 tools/check-dist.py "$HOME/repos/meow-sci/mods/ksa-scripting"
```

Install purrTTY (with the launcher contract) for terminals, REPL shells and the editor (ksa-scripting never ships purrTTY's DLLs; it imports them from purrTTY through StarMap); ModMenu is optional (a fallback "Scripting" menu is injected without it). User scripts live in `<Documents>/My Games/Kitten Space Agency/mods/ksa-scripting/scripts/`; open the Script Manager with F11 (configurable).
