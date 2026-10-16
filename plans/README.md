# ksa-scripting implementation plans

These plans turn the research bundle in [`docs/research`](../docs/research/index.md) into buildable work. Read the [recommendation](../docs/research/decisions/recommendation.md) first; it explains the ordering below.

| # | Plan | Depends on | Status |
|---|---|---|---|
| 00 | [Shared foundation: host, facade, hooks, scheduler, Script Manager UI, terminal scripts, editor bridge](00-shared-foundation-plan.md) | research bundle | proposed |
| 00b | [purrTTY side: launcher contract, virtual-pty shells, in-world placement](00b-purrtty-scripting-extensions-plan.md) | 00 (parallel) | proposed |
| 01 | [LuaJIT runtime (direct P/Invoke)](01-luajit-runtime-plan.md) | 00 | proposed |
| 02 | [V8 via ClearScript with transparent TypeScript](02-v8-clearscript-typescript-runtime-plan.md) | 00 | proposed |
| — | Jint fallback | dropped by decision on 2026-10-16 (research kept under [runtimes/jint](../docs/research/runtimes/jint.md)) | dropped |
| — | Node.js via libnode | rejected for now (see [nodejs-libnode](../docs/research/runtimes/nodejs-libnode.md)); no plan until a LibNode ≥ 22.18 exists | deferred |

Conventions shared by every plan:

- Target `net10.0` / C# 13, warnings as errors, nullable, the gatOS `Directory.Build.props` path resolution (`KSA_DLL_DIR` or the sibling `ksa-game-assemblies` checkout).
- Only `KsaScripting.GameMod` and `KsaScripting.Ksa` reference KSA/Brutal/StarMap assemblies; everything else is game-free and unit-tested.
- Every KSA touch carries a `[KsaAnchor]` attribute; every Harmony patch body is try/catch with log-once and self-disable; nothing on the sim path may throw.
- Spikes under [`/spikes`](../spikes) are the executable evidence; extend `HookCompileCheck` with one stub per new Harmony target before implementing it.
- Plans are written so that a future agentic session can execute them without re-reading the whole bundle; each task names the concept pages it relies on.
