# Plan 00 — Shared foundation: host, game API facade, hooks, scheduler, Script Manager UI, terminal scripts, editor bridge

Status: proposed (revised 2026-10-16 for the Script Manager UI, UI-created bindings, terminal scripts and nvim-over-TCP) · Owner: meow sci · Depends on: [research bundle](../docs/research/index.md) · Parallel: [Plan 00b (purrTTY side)](00b-purrtty-scripting-extensions-plan.md) · Feeds: plans 01 and 02

## 1. Goal

Build the runtime-agnostic part of the ksa-scripting mod so that each scripting runtime (LuaJIT, V8) is a thin adapter behind one interface. When this plan is complete, a player can — entirely from an ImGui window — create a script, edit it in the shipped Neovim inside a purrTTY terminal, run it once, run it forever, attach its exported functions to game events or ticks, give it a virtual terminal rendered in a 2D window or on an in-world part, and see its output and errors; and a "null runtime" (C# lambdas behind `IScriptRuntime`) proves every one of those paths before any engine exists.

Design sources: [architecture](../docs/research/architecture/in-band-scripting-architecture.md), [hook model](../docs/research/architecture/hook-model-sync-async.md), [script API surface](../docs/research/architecture/script-api-surface.md), [exports and bindings](../docs/research/architecture/script-exports-and-bindings.md), [Script Manager UI](../docs/research/architecture/script-manager-ui.md), [terminal scripts](../docs/research/architecture/terminal-scripts-and-virtual-terminals.md), [sandboxing](../docs/research/architecture/sandboxing-limits-and-safety.md), [neovim TCP launch](../docs/research/editor/neovim-tcp-launch.md).

## 2. Repository and project layout

```
ksa-scripting/
  ksa-scripting.slnx
  Directory.Build.props                 # copy of gatOS's (KSAFolder resolution, dist dir, zero-warning policy)
  src/
    KsaScripting.Core/                  # game-free: DTOs, ApiSchema, hooks, bindings, scheduler, catalog, terminals (host side), persistence, stub generators, nvim RPC client
    KsaScripting.Ksa/                   # KSA-facing: readers, actuators, ActionExecutor, event patches, slots, KsaHealth, KsaAnchor
    KsaScripting.GameMod/               # StarMap entry: Mod.cs, Patcher.cs, Script Manager window, menu, REPL shells, terminal shells, editor launcher, mod.toml
    KsaScripting.Runtime.Lua/           # plan 01
    KsaScripting.Runtime.V8/            # plan 02
  tests/
    KsaScripting.Core.Tests/            # NUnit, game-free
    KsaScripting.Ksa.CompileCheck/      # grows from spikes/HookCompileCheck: one stub per Harmony target
  editor/                               # shipped nvim config (init.lua), noshell stub, luarc.json, tsconfig template; binaries fetched by tools/
  scripts-lib/                          # shipped script libraries copied to <scripts>/lib (tui.lua, tui.ts, ...)
  tools/                                # fetch-nvim.*, fetch-lsp.*, build-luajit.*, fetch-ts.*, check-dist.py
  spikes/                               # existing
  docs/research/                        # existing OKF bundle
```

`mod.toml`:

```toml
name = "ksa-scripting"
description = "In-band Lua (LuaJIT) and TypeScript scripting for KSA: game-loop hooks, terminal scripts on purrTTY surfaces, and a shipped Neovim editor."
version = "0.1.0"
author = "meow sci"

[StarMap]
EntryAssembly = "KsaScripting.GameMod"

[[StarMap.ModDependencies]]
ModId = "purrTTY"
Optional = true
ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

Packaging follows [runtime and packaging](../docs/research/ksa-host/runtime-and-packaging.md): deny-list DLL glob, `deps.json`, natives under `runtimes/<rid>/native/`, editor blobs outside the wipe step with `SkipUnchangedFiles`, licenses. User data under `<Documents>/My Games/Kitten Space Agency/mods/ksa-scripting/`: `scripts/` (user files), `scripts/lib/` (shipped helpers, overwritten on update), `scripts/stubs/` (generated), `bindings.toml`, `terminals.toml`, `editor/{data,state,cache}/`, `ksa-scripting.toml`.

## 3. Work breakdown

### Phase A — Scaffold (1–2 days)

1. Solution, projects, `Directory.Build.props`, `.github/workflows/build.yml` (build + tests on ubuntu/windows/macos; game DLLs via `KSA_DLL_DIR` or compile-removal of game code like gatOS).
2. Vendor purrTTY contract DLLs (`vendor/purrTTY/`, `Private=true`) exactly as gatOS does; bump when Plan 00b lands the extended contract.
3. Port `KsaAnchorAttribute`, `KsaHealth`, `AtomicFile`, Tomlyn config loader from gatOS. Config keys: `scripts_dir`, `frame_budget_ms` (2), `sync_hook_budget_ms` (5), `call_budget_ms` (50), `control_all_vessels` (true), `debug_namespace` (true), `autostart` (list), `editor_port_range` (e.g. 48600–48699), `v8_debug_port`, `lua_jit_enabled`, `terminal_output_buffer_kib` (256).
4. `CopyCustomContent` target and `tools/check-dist.py`.

### Phase B — Execution slots and lifecycle (2–3 days)

Source: [game loop and threading](../docs/research/ksa-host/game-loop-and-threading.md), [gatOS threading](../docs/research/existing-mods/gatos-architecture-and-threading.md).

1. `Mod.cs` with `[StarMapImmediateLoad]`, `[StarMapAllModsLoaded]` (install patches, create host, register shells/launcher), `[StarMapBeforeGui]`/`[StarMapAfterGui]` (UI, post-solver drivers), `[StarMapAfterOnFrame]` (F2-proof latch), `[StarMapUnload]` (reverse teardown: close terminals, stop nvim sessions, unregister commands, unpatch).
2. `SlotPatches`: postfix `Universe.ApplyVehicleSolvers()` → `Host.StepSlot()`; `Priority.First` prefix `Universe.ExecuteNextVehicleSolvers(double, SimStep)` → `Host.SolverSlot()`; try/catch, `PerfStat`, self-disable.
3. `GameThreadQueue` (gatOS `CommandQueue` shape) for off-thread callers: terminal input from purrTTY's tick thread, nvim RPC thread, file watcher.
4. `Watchdog` thread: deadline per runtime entry; `IScriptRuntime.Interrupt()` on expiry.
5. Minimal status overlay (per-slot timings) to validate budgets before the full window exists.

Acceptance: empty mod loads, slots fire each frame, F2 does not stop timers, unload leaves no patches.

### Phase C — Game API facade (`KsaScripting.Ksa`) (5–8 days)

Source: [gatOS snapshot model](../docs/research/existing-mods/gatos-snapshot-and-reader-model.md), [gatOS actuators](../docs/research/existing-mods/gatos-actuator-catalog.md), [gatOS /sim surface](../docs/research/existing-mods/gatos-sim-api-surface.md), [state surface](../docs/research/ksa-host/state-read-write-surface.md), [event hook points](../docs/research/ksa-host/event-hook-points.md).

1. Port DTO records, readers, actuators, `KsaCatalog` → `ActionExecutor` with the gatOS action vocabulary, `PhaseFor`, authority gate, health latches, reflection accessors.
2. `IHostFacade` (coarse, ~15 methods: `Read`, `Snapshot`, `List`, `Act`, `Wait`, `Subscribe/Unsubscribe`, `Log`, `State*`, `Term*`) implemented over the facade; this is the only object the runtimes bind.
3. Events: `EventDiffer` port + `EventPatches` (one per catalog row) producing `ScriptEvent`s; sync-hook dispatch inside the patch under the sync budget.
4. Persistence: save sidecar via `UncompressedSave.Write` postfix / `Program.OnGameLoaded` postfix.
5. Extend `KsaScripting.Ksa.CompileCheck` with a stub per patched member (CI "build as alarm").

Acceptance (in game, null runtime): read telemetry each step; ignite, stage, attitude target under warp, teleport; receive `engine.activate`, `stage`, `dock`, `situation.change`.

### Phase D — Core: ApiSchema, hooks, bindings, scheduler, catalog, stubs (4–6 days)

1. `ApiSchema`: declarative description of every namespace/member/parameter/doc/action key; generators for `host.d.ts`, `ksa.lua` (LuaCATS), the JS and Lua preludes, `tsconfig.json`, `.luarc.json`; snapshot tests.
2. `HookRegistry` + `BindingStore` (`bindings.toml`): code-declared and UI-created bindings; atomic swap per script on reload; context object construction (`EventContext`, `TickContext`, `ServiceContext`, `TerminalContext`).
3. `Scheduler`: Step-slot driver with per-frame budget, fairness, clocks (`ut`, `wall`, `render`), awaitables (`Sleep`, `NextStep`, `NextFrame`, `Until`, `WaitEvent`, `TermRead`, pending `CommandResult`), service cancellation signals, `IScriptRuntime.Advance(budget)`.
4. `ScriptCatalog`: scan `scripts/`, header metadata (`-- @ksa …` / `// @ksa …`), versions, `Run`/`Start`/`Stop`/`Reload`/`Unload`/`List`, export discovery via the runtime, debounced `FileSystemWatcher` (content-hash check), state badges.
5. `IScriptRuntime`, `ScriptValue`, `ScriptModule`, `ScriptCallable`, `ScriptLoadException(file, line, col)`, `RuntimeStats`; `NullRuntime` for tests.
6. Unit tests for all of the above (no KSA references).

### Phase E — Terminal scripts (host side) (3–5 days)

Source: [terminal scripts and virtual terminals](../docs/research/architecture/terminal-scripts-and-virtual-terminals.md), [purrTTY custom shell contract](../docs/research/existing-mods/purrtty-custom-shell-contract.md), [purrTTY VT capabilities](../docs/research/existing-mods/purrtty-vt-capabilities-for-tuis.md).

1. `ScriptTerminal` (Core): per-script virtual terminal state — output ring buffer (`terminal_output_buffer_kib`), input queue, size (cols/rows), attached-surface count, mode flags the host tracks (alt screen, mouse, kitty keyboard) by scanning output for the DEC private mode sequences (for diagnostics only; purrTTY interprets them).
2. `ScriptTerminalShell : BaseChannelOutputShell` (GameMod): the purrTTY "mock pty" (facts in [purrTTY virtual-pty bridge](../docs/research/existing-mods/purrtty-virtual-pty-bridge.md)). One shell id `ksa-script` is registered; the target script is passed through `CustomShellStartOptions.EnvironmentVariables["KSA_SCRIPT"]` (the factory takes no arguments). `OnStartingAsync` binds to the `ScriptTerminal`, replays the ring buffer so a (re)attached surface shows the current screen, treats `InitialWidth/Height` as provisional (the real grid arrives via `NotifyTerminalResize` within a frame or two). `WriteInputAsync` (called synchronously on the game thread by purrTTY) enqueues the raw bytes — keys, SGR mouse reports, engine query replies — to the terminal input queue and returns `Task.CompletedTask`; `NotifyTerminalResize` → `resize` event; Ctrl+C arrives as `0x03` (purrTTY never calls `RequestCancellation`) and is surfaced as a `cancel` event the script may handle; `Terminated` when the script stops (print a final `[script exited]` line first, since a single-session window shows no exit marker). **Teardown lives in `Dispose`** (purrTTY closes sessions with `Dispose()` on a pool thread and never calls `StopAsync`), and output emitted with no bridge attached is dropped by `BaseChannelOutputShell`, which is why the host keeps its own ring buffer.
3. Script-facing `term` API (via `IHostFacade.Term*`): `write(bytes|string)`, `read() → Promise<bytes>` / yielding read, `on('data'|'resize'|'attach'|'detach'|'cancel', fn)`, `size`, `attached`, `clear()`, `flush()`; delivery in the Step slot; output flushed once per frame to the shell (purrTTY drains per frame anyway).
4. Shipped `scripts/lib/tui.ts` and `tui.lua`: escape-code helpers (alt screen, cursor, SGR colors, clear, box drawing), input parser (keys incl. kitty encoding, SGR mouse `\x1b[<b;x;yM/m`), tiny widget set (label, button with hit-test, toggle, progress bar), redraw-on-resize loop helper. Documented in the stubs.
5. Surface management (needs Plan 00b): `TerminalPlacer` requests a 2D window or an in-world placement for a script terminal through the purrTTY launcher contract (`TerminalLaunchRequest` with `CustomShellId = "ksa-script"`, `Environment["KSA_SCRIPT"]`, `InWorld { Cols, Rows, VehicleId, PartInstanceId, Offset*, Rotation*, WidthMeters, HeightMeters, opacities }`); stores placements in `terminals.toml` and re-creates them when the script starts and the vehicle exists; detaches on script stop or vehicle removal. Default panel: 40×12 cells at the theme's 32 px font ≈ 772×456 px texture; choose `WidthMeters/HeightMeters` with the same aspect (≈1.69) to avoid stretching. The click that focuses a quad is also forwarded as a mouse press, so the shipped `tui` helpers act on **release**.
6. Lifecycle rules: the script owns the terminal; surfaces attach/detach freely; output while detached is buffered; a script stop terminates the shell (tab shows exit) but a service restart re-attaches configured placements.

Acceptance (null runtime): a C# "door panel" terminal script draws a button with box characters, enables SGR mouse, toggles a light on click, redraws on resize, in a 2D window and on an in-world part quad; keyboard `q` exits.

### Phase F — Script Manager window and menu (3–4 days)

Source: [Script Manager UI](../docs/research/architecture/script-manager-ui.md).

1. Window skeleton (scripts pane, toolbar, tabs, global tabs), hotkey suppression via the `GameSettings.OnKeyAll` prefix + mod-scoped focus flag, menu entry (ModMenu.Attributes or `DrawProgramMenusHook` fallback), configurable toggle key (not F12).
2. Actions: New (templates per language and kind), Edit (editor launcher), Run, Start/Stop, Reload, Delete (modal), Open folder (path only).
3. Bindings tab with event catalog picker (names, descriptions, payload fields, live counters), export picker, options; writes `bindings.toml`.
4. Terminal tab: open 2D window, place on part (vehicle/part pickers; defaults to the controlled vessel; part tree by `DisplayName`/`InstanceId`), list/focus/close attachments, mini output preview.
5. Output tab (per-script log ring), Code tab (read-only), Events/Runtimes/Settings tabs.
6. All UI actions post to the Step slot; nothing touches runtimes in the UI phase.

### Phase G — Editor bridge (4–6 days)

Source: [neovim shipping](../docs/research/editor/neovim-shipping.md), [neovim TCP launch](../docs/research/editor/neovim-tcp-launch.md), [save detection](../docs/research/editor/save-detection-and-reload.md), [Plan 00b](00b-purrtty-scripting-extensions-plan.md).

1. `tools/fetch-nvim.*` (Neovim 0.12.6 per RID, drop `xxd.exe`/`win32yank.exe`, keep `LICENSE.txt`, `vcruntime140.dll` beside `nvim.exe` on Windows), `tools/fetch-lsp.*` (LuaLS 3.19.2, TypeScript 7.0.2 native `tsc`), TypeScript tree-sitter parser build + queries.
2. Shipped `editor/config/ksa-editor/init.lua` from the research page plus the no-shell settings (`shell` → `editor/noshell` stub, `shellcmdflag=''`), `backupcopy=yes`, `mouse=a`, LSP configs, completion, `BufWritePost` → `vim.rpcnotify(0, 'ksa_saved', path)`, `<C-z>` no-op, no F12 binding.
3. `EditorLauncher`: pick a free loopback port, build env/args (`--clean -u init.lua --listen 127.0.0.1:<port> <file>`, cwd = scripts dir), launch **nvim directly as the PTY child** through the purrTTY launcher contract (no shell), fall back to printing instructions when purrTTY lacks the contract.
4. `NvimRpcClient` (MessagePack-CSharp 3.1.12 over `TcpClient`): connect with retries, `nvim_set_client_info`, `nvim_get_api_info`, handle `ksa_saved` → reload via the game-thread queue; push diagnostics (`vim.diagnostic.set`) and jump-to-line on load errors; close on `Exited`.
5. One editor session per script file; "Edit" on an open script focuses the existing terminal.

Acceptance: "Edit" opens nvim in a purrTTY window on the file with LSP completion from the generated stubs; `:w` reloads within one frame; a syntax error appears as an nvim diagnostic and in the Output tab; `:q` closes the session; `:!` and `:terminal` cannot open a shell.

### Phase H — Console commands and REPL (1–2 days)

`script` commands (`run`, `start`, `stop`, `reload`, `list`, `edit`, `stubs`) registered with `[TerminalAction]` under an owner token; `ScriptReplShell : BaseLineBufferedShell` per runtime registered in `CustomShellRegistry`; output mirrored to `ConsoleWindow.Print`.

### Phase I — Hardening and release (2–3 days)

Stress tests (50 scripts with step hooks under 1000× warp; runaway loop; unbounded allocation; save/load with state; vehicle destruction mid-hook; terminal script with surface attached while the vehicle is destroyed), upgrade playbook, `docs/VALIDATION.md`, release workflow (one zip: payload + natives + editor).

## 4. Key interfaces (Core)

```csharp
public interface IScriptRuntime : IDisposable
{
    string Id { get; }
    ScriptModule Load(ScriptSource source);                       // throws ScriptLoadException
    IReadOnlyList<string> Exports(ScriptModule module);           // exported function names
    ScriptValue Invoke(ScriptCallable fn, ReadOnlySpan<ScriptValue> args);
    bool Advance(TimeSpan budget);                                // drain continuations; true if more work remains
    void Interrupt();                                             // any thread
    RuntimeStats Stats { get; }
}
public interface IHostFacade
{
    ScriptValue Read(string handle, string path);  ScriptValue Snapshot(string handle);  ScriptValue List(string kind);
    CommandResult Act(string action, string? vesselId, int ordinal, double value, double[]? values, string? token, string? aux, out PendingResult? pending);
    WaitHandle Wait(string kind, ScriptValue arg);                 // sleep/nextStep/nextFrame/until/waitEvent/termRead
    Guid Subscribe(string kind, string key, ScriptCallable fn, BindingOptions opts);  void Unsubscribe(Guid id);
    void Log(LogLevel level, string message);
    void TermWrite(int termId, ReadOnlySpan<byte> bytes);  TerminalInfo TermInfo(int termId);
    ScriptValue StateGet(string key);  void StateSet(string key, ScriptValue value);
}
public sealed record ScriptEvent(double Ut, string Type, string? VesselId, IReadOnlyDictionary<string, ScriptValue> Payload);
public enum BindingKind { Event, Tick, Service, Terminal, Command }
public sealed record Binding(Guid Id, string Script, string Export, BindingKind Kind, string Key, BindingOptions Options, bool FromCode);
```

## 5. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Step-slot work delays the solver kick | strict budgets, per-script accounting, status window |
| Harmony target churn on game updates | compile-check project, probe tool, anchors, feature-dark fallback |
| purrTTY contract work (Plan 00b) slips | null-runtime milestones do not need it until Phase E/G; layout-based fallback documented |
| Terminal input arrives on purrTTY's tick thread | always via the game-thread queue; never touch runtimes off-thread |
| Editor payload size | optional download on first use (gatOS guest-image model) |

## 6. Definition of done

All acceptance criteria pass on Windows x64 and Linux x64 with the null runtime; unit tests green; compile check green against the current `ksa-game-assemblies`; bundle `log.md` and this plan's status updated.
