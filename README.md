# ksa-scripting

In-band scripting for Kitten Space Agency: LuaJIT and TypeScript scripts that run inside the game process with gatOS-style read/write APIs, game-loop event hooks, and a shipped Neovim editor inside purrTTY.

This repository currently holds research, plans, and spikes; no mod code yet.

| Folder | Contents |
|---|---|
| [`docs/research/`](docs/research/index.md) | OKF v0.2 knowledge bundle: KSA host facts (loader, game loop, hooks, console, API surface), gatOS and purrTTY analyses, runtime evaluations (LuaJIT, ClearScript V8, Jint, libnode, others), editor research (Neovim), architecture and recommendation, spike results, and the raw research reports. |
| [`plans/`](plans/README.md) | Implementation plans: shared foundation (host, Script Manager UI, bindings, terminal scripts, nvim over TCP), purrTTY-side extensions, LuaJIT runtime, V8/ClearScript TypeScript runtime. |
| [`spikes/`](spikes) | Runnable evidence: `V8TsSpike`, `JintTsSpike`, `LuaJitSpike`, `KsaProbe` (metadata probe of KSA.dll), `HookCompileCheck` (Harmony/StarMap compile check against the game DLLs). |
| [`REFERENCES.md`](REFERENCES.md) | The original brief and reference material locations. |

Build the spikes with `dotnet build spikes/<name>`; they resolve the KSA reference assemblies from `KSA_DLL_DIR` or the sibling `ksa-game-assemblies/current/dll/` checkout, like gatOS and purrTTY.
