# Research bundle update log

## 2026-10-17
* **Update**: Scope revision from the maintainer: Jint fallback dropped (research kept, plan removed); nvim RPC moves to loopback TCP with a stubbed shell ([neovim TCP launch](editor/neovim-tcp-launch.md), verified locally); new designs for the ImGui [Script Manager](architecture/script-manager-ui.md), [exports and bindings](architecture/script-exports-and-bindings.md) and [terminal scripts](architecture/terminal-scripts-and-virtual-terminals.md); new purrTTY concepts [virtual-pty bridge](existing-mods/purrtty-virtual-pty-bridge.md) and [in-world terminals](existing-mods/purrtty-in-world-terminals.md) from a second purrTTY research pass (mirrored under references/). Plans 00/01/02 revised; plan 00b (purrTTY side) added.

## 2026-10-16
* **Create**: Initial bundle. Six parallel research passes (KSA decompiled hooks, purrTTY internals, gatOS internals, LuaJIT embedding, JS/TS runtimes, Neovim shipping) distilled into 36 concepts across ksa-host/, existing-mods/, runtimes/, editor/, architecture/, decisions/ and spikes/. Five spikes added under /spikes (V8+TS, Jint+TS, LuaJIT P/Invoke, KsaProbe, HookCompileCheck) with measured results recorded in spikes/*.md. Verified against KSA build 2026.10.10.5554; doc drift against the ksa skill recorded in [game loop and threading](ksa-host/game-loop-and-threading.md).
