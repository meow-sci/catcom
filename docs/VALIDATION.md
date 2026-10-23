# catcom in-game validation checklist

This checklist is the in-game pass that CI and the development Mac cannot run. Work through it on Windows x64 and on Linux x64 with KSA build 2026.10.19.5638 and purrTTY (with the launcher contract) installed. Each item is an action followed by its expected result. Run the sections in order, because later sections assume the earlier ones pass. All features listed here are implemented as of 2026-10-16 and proven by builds, unit tests and off-game harnesses on macOS; none of them has run inside the game yet, so record every deviation precisely (what you did, what happened, the log lines).

## Setup

### Install

- Build the dist from this repo on the test machine with `dotnet build src/Catcom.Dist -c Release` (the deploy project; it builds the mod and the purrTTY bridge assembly and copies the mod folder). The game DLLs come from `KSA_DLL_DIR`, or from a sibling `ksa-game-assemblies` checkout. On Windows the default game folder is `C:\Program Files\Kitten Space Agency\`.
- On Windows the build deploys directly to `%USERPROFILE%\Documents\My Games\Kitten Space Agency\mods\catcom\`. On Linux the default deploy folder is `$HOME/repos/meow-sci/mods/catcom/`, so set `CATCOM_DIST_DIR` to `<Documents>/My Games/Kitten Space Agency/mods/catcom/` before building. Use the Documents folder that KSA itself uses on Linux.
- The result must be the folder `mods/catcom/` directly under the KSA mods folder, with `mod.toml` at its top level.
- Install purrTTY with the launcher contract, as its own mod. catcom ships none of purrTTY's DLLs: the dist must not contain `purrTTY.CustomShellContract.dll` or `purrTTY.Logging.dll` (StarMap resolves them from purrTTY's load context via `ImportedAssemblies`). The log line `purrTTY bridge loaded` must name purrTTY's mod folder as the contract location; `purrTTY is not installed` or `purrTTY contract could not be loaded` means the bridge was not loaded and terminals, REPL shells and the editor are unavailable.
- Run `tools/fetch-all.sh` (macOS/Linux) or `tools/fetch-all.ps1` (Windows) once before building: it downloads every shipped binary for all three platforms (TypeScript assets, Neovim, language servers, and the cross-built LuaJIT + TypeScript parser from the public `natives-v*` release of meow-sci/catcom-native; no credentials needed). The dist build fails if anything is missing, so a dist that built at all is complete.

### Where data lives

- The mod folder (install dir) holds the DLLs, `mod.toml`, `runtimes/`, `assets/` (TypeScript compiler), `scripts-lib/`, and `editor/`. The user data always lives in `<Documents>/My Games/Kitten Space Agency/mods/catcom/`: `catcom.toml`, `scripts/`, `bindings.toml`, `terminals.toml`, `cache/`, and `editor/{data,state,cache}`. On Windows that is normally the same folder as the install dir.
- On each start the mod regenerates `scripts/stubs/` (`host.d.ts`, `ksa.lua`, and the tsconfig and LuaLS config files) and copies `scripts-lib/` to `scripts/lib/`.
- The script state for a save is kept in the sidecar `catcom-state.toml`, written into the save's own folder (the `directory` in the `save.write` payload).

### Logs

- Standard output: start KSA from a terminal or console so that stdout is visible. Mod lines have the form `[catcom] <level>: <message>`.
- In-game console: the same lines appear in KSA's console window. `ksa.console.log` output goes to both places and to the Output tab of the Script Manager.
- Record here where the file log is written, if the build writes one: ____

### Configuration

`catcom.toml` (in the mod data folder) holds the keys this checklist uses. The defaults are shown in brackets: `debug_namespace` (true), `control_all_vessels` (true), `fault_limit` (3), `frame_budget_ms` (2.0), `sync_hook_budget_ms` (5.0), `call_budget_ms` (50.0), `lua_jit_enabled` (true), `lua_memory_limit_kb` (65536), `v8_heap_limit_mb` (64), `manager_hotkey` ("F11"). Restart KSA after editing it.

### Conventions used below

- Lua methods on objects use colon syntax (`v:set_throttle(0.5)`), and module functions use dot syntax (`ksa.sleep(2)`). Lua names are snake_case, and TypeScript names are camelCase (`v.setThrottle(0.5)`).
- TypeScript arrays are 0-based (`v.engines[0]`). Lua lists are 1-based (`v.engines[1]`).
- Event payload fields are camelCase in TypeScript and snake_case in Lua (`newVesselId` and `new_vessel_id`).
- Every write returns a result with `ok` and `outcome`. The outcomes are `Ok`, `Invalid`, `NotFound`, `Denied`, `Busy`, `Fault`, `TimedOut`, and `Unsupported`.
- The three test scripts are in the appendix at the end of this file.

### Failure notes

For each failure, record the date, the KSA build, the OS, the section and item number, the script name, and the exact log line.

## 1. Load and lifecycle

- [ ] Start KSA with the mod installed and load a save or a new game. Expected: the game reaches the world without a crash dialog, and stdout contains a line with `catcom started`.
- [ ] Read the runtime status in the Runtimes tab or in the startup log. Expected: the Lua runtime (LuaJIT) and the TypeScript runtime (V8) both report available. Record the `jit.status` value from the log. Expected `true` on Windows and Linux (section 13 covers the JIT caveat).
- [ ] Create a Lua file `scripts/timer.lua` containing `ksa.every(1, function(ctx) ksa.console.log("tick", ctx.ut) end)`. It needs no Run, because the hook is declared in code. Expected: `tick` lines appear about once per simulated second at 1x.
- [ ] Press F2 to hide the game UI, wait 5 seconds, then press F2 again. Expected: the UI hides and returns, and `tick` lines keep appearing while the UI is hidden.
- [ ] Unload the mod if the game's mod list allows it (disable catcom and restart). Expected: the unload completes without an exception in the log, and no `[catcom]` line appears afterwards. Record "not testable" if the game cannot disable a mod without deleting its folder.

## 2. Script Manager window

- [ ] Press F11. Expected: the Script Manager window opens. Press F11 again. Expected: it closes. Press purrTTY's own toggle (F12) and confirm that purrTTY still responds.
- [ ] With ModMenu installed, open the top-level `Scripting` menu. Expected: it contains the Script Manager toggle and opens the window.
- [ ] Remove ModMenu (or disable it) and restart. Open the game's program menus. Expected: a `Scripting` entry appears in the fallback menu and opens the window.
- [ ] Click the filter box and type a word containing letters that are game hotkeys (for example the warp and camera keys). Expected: the text appears in the filter, and no game hotkey fires, so time warp and the camera do not change.
- [ ] Close the window, open the in-game console with its normal key, and run an existing console command (for example `help`). Expected: the command runs and its output appears, and the Script Manager does not open or change.
- [ ] Open the window with no scripts in `scripts/`. Expected: the empty state shows the three-step hint (New, Edit, Run).
- [ ] Put one Lua script and one TypeScript script in `scripts/`, plus one file in `scripts/lib/` and one in `scripts/stubs/`. Expected: the two scripts are listed with a state badge, and the `lib/` and `stubs/` files are not listed.

## 3. New, Edit, Run, Start, Stop, Reload, Delete

- [ ] For each template (ad-hoc, event handler, service loop, terminal app) and each language (Lua, TypeScript), choose New, give a name, and create the script. Expected: `scripts/<name>.lua` or `scripts/<name>.ts` is written from that template and appears in the list. That is eight combinations.
- [ ] Expected for each template: ad-hoc prints the active vessel's telemetry and returns. Event handler exports a handler (`onEvent` in TypeScript). Service loop loops on `ctx.signal.cancelled` and sleeps one second per pass. Terminal app draws a button, toggles a light on click, redraws on resize, and exits on `q`.
- [ ] Edit: choose Edit on a script. Expected: nvim opens in a purrTTY window. Section 10 covers the details.
- [ ] Run the ad-hoc Lua template, then the ad-hoc TypeScript template. Expected: each prints the telemetry in the Output tab and the badge returns to idle.
- [ ] Start the Lua service loop and then Stop it. Repeat with the TypeScript service loop. Expected: the badge shows the service state while it runs. After Stop the loop exits and the badge returns to idle.
- [ ] Edit a log string in a script file outside the game and press Reload. Expected: the new text is used on the next call or tick, and the Output tab shows the reload.
- [ ] Choose Delete on a script and then Cancel. Expected: a confirmation modal appears and the file stays. Choose Delete again and confirm. Expected: the file is removed from `scripts/` and the script stops. Record whether any `bindings.toml` row for it remains.
- [ ] Choose Open folder. Expected: the folder path is shown, and no OS file browser or shell opens.

## 4. Reads

- [ ] Run test script 1 (`telemetry.lua`, in the appendix) with a vessel on the pad or in flight. Expected: it prints the vessel name and situation, the barometric and radar altitude, the total mass, the CCI position, one line per engine, and the battery fraction (`nil` when the vessel has no battery).
- [ ] In a TypeScript ad-hoc script, log `ksa.active?.altitude.barometric`, `ksa.active?.mass.total`, and `ksa.active?.position.cci.x`. Expected: the values agree with test script 1 taken in the same second.
- [ ] On the launch pad, `altitude.radar` is near zero. After a short climb it increases. Expected: it roughly matches the game's own altitude readout.
- [ ] After about 10 seconds of ignition at full throttle (section 5), read `mass.propellant` and `mass.total` again. Expected: both fall, and their change is the same.
- [ ] After `ignite()`, the first engine reads `active` as true (`v.engines[1].active` in Lua, `v.engines[0].active` in TypeScript). After `shutdown()` it reads false.
- [ ] Read `ksa.time.ut` and `ksa.time.warp`. Set warp to the largest entry in `ksa.time.warp_speeds` (section 5). Expected: `warp` reports that factor, and `ut` advances about that many times faster than real time.
- [ ] Call `ksa.vessel("does-not-exist")` in either language. Expected: nil in Lua or undefined in TypeScript, with no error.
- [ ] Call `v:snapshot()` in Lua or `v.snapshot()` in TypeScript. Expected: the full record, with more fields than the typed getters.

## 5. Writes

- [ ] With engines on, call `v:set_throttle(0.5)` (or `v.setThrottle(0.5)`), then `set_throttle(0)`. Expected: the result has `ok` true and `outcome` `Ok`, `throttle_cmd` reads 0.5 and then 0, and the engine responds.
- [ ] Call `v:ignite()` on a vehicle with propellant. Expected: `Ok`, `engine_on` true, and the engine lights. `engine.activate` and `engine.ignite` fire (section 6).
- [ ] Call `v:stage()`. Expected: `Ok`, the next stage activates in the game, and the `stage` event fires.
- [ ] Call `v:set_lights(true)`, then `v.lights[1]:set_on(false)` and `v.lights[1]:set_color(1, 0, 0)`. Expected: the lights switch as set, and the colour changes to red.
- [ ] Call `v:set_rcs(true)`, then `v:rotate(0, 1, 0)`, then `v:rotate(0, 0, 0)`. Expected: `rcs_on` is true. The vehicle pitches while the input is held and stops when it is set to zero. `translate` behaves the same way on its own axes.
- [ ] Attitude mode under warp (Solver lane). Call `v:set_attitude_mode("Prograde")` from a script coroutine in Lua, or `await v.setAttitudeMode("Prograde")` in TypeScript. Then set warp to the largest entry in `warp_speeds` and wait 10 seconds of real time. Expected: the call returns `Ok` after the solver slot runs, `attitude_mode` still reads `Prograde` after the warp change, and `attitude.quat` changes as the vehicle turns. The Solver lane must not stall under warp.
- [ ] Burn: with a flying vessel that has propellant, call `v:burn(ksa.time.ut + 30, {x = 0, y = 0, z = 5})`, or `v.burn(ksa.time.ut + 30, {x: 0, y: 0, z: 5})` in TypeScript. Expected: `Ok`, and `burn.complete` fires when the delta-v is delivered. If the result is `Invalid` or `Busy`, record the reason.
- [ ] Debug namespace on (the default). Call `v.debug:refill_fuel()` on a vehicle with empty tanks. Expected: `Ok`, and the tank fractions become 1. Call `v.debug:refill_battery()` on a vehicle with a battery. Expected: `Ok`.
- [ ] With the debug namespace on, call `v.debug:teleport(pos, vel)` with `pos` 10 metres above the current `position.cci`. Expected: `Ok`, and the vessel moves by about that offset.
- [ ] With the debug namespace on, call `v.debug:impulse({x = 0, y = 1, z = 0}, "body", "dv")`. Expected: `Ok`, and the velocity changes by about 1 m/s along the body's Y axis.
- [ ] Set `debug_namespace = false` in `catcom.toml`, restart, and repeat the refill and teleport calls. Expected: each returns `Denied` with `ok` false, and nothing changes. Restore the setting afterwards.
- [ ] Optional: set `control_all_vessels = false`, restart, and call `v.debug:control()` on another vessel. Expected: `Denied`. Record the actual outcome if it differs. Restore the setting.

## 6. Events

Events come from Harmony hooks (intent and transitions, delivered at the call) and from the snapshot differ (about 10 times a second). For each item, run the trigger with a handler on that name. Log `ctx.type`, `ctx.vessel_id`, and the payload field named in the item.

### Harmony events

- [ ] `engine.activate`: call `v:ignite()` (or use the in-game engine toggle). Expected: the handler runs with `ctx.type == "engine.activate"`, and `payload.engine` is set. The log line appears at the call, before the engine physically ignites.
- [ ] `engine.deactivate`: call `v:shutdown()`. Expected: the handler fires.
- [ ] `rcs.activate`: call `v:set_rcs(true)`, then `v:set_rcs(false)`. Expected: events fire with `payload.active` true and then false.
- [ ] `stage`: call `v:stage()` or use the in-game staging control. Expected: the handler fires with `payload.sequence` set.
- [ ] `decouple`: fire a decoupler between two stages with `v.decouplers[1]:fire()`. Expected: `decouple` fires with `payload.new_vessel_id`, and that id appears in `ksa.vessels()`.
- [ ] `split`: use the same trigger as `decouple`. Expected: `split` fires with the new vessel id.
- [ ] `dock`: dock one vessel's docking port to another vessel's port. Expected: `dock` fires once with `payload.other_vessel_id`.
- [ ] `undock`: call `v.docking[1]:undock()` on a docked port. Expected: `undock` fires with `payload.new_vessel_id`.
- [ ] `vessel.spawn`: launch a vehicle from the launch site, or spawn a kitten (EVA). Expected: `vessel.spawn` fires with `payload.name`.
- [ ] `vessel.remove`: remove a vessel from the system, for example a crashed or recovered stage. Expected: `vessel.remove` fires with `payload.name`.
- [ ] `vessel.launch`: lift off from the launch pad. Expected: it fires once, at liftoff.
- [ ] `impact`: crash a vehicle into ground or water at speed. Expected: `impact` fires with `payload.kind` and `payload.body`.
- [ ] `vessel.destroy`: crash the vehicle at high speed. Expected: `vessel.destroy` fires before the vessel is removed, with `payload.cause` set (or "removed"), and `payload.peak_g` and `payload.peak_q` set.
- [ ] `part.failure`: overstress a part with a hard landing or a high-G crash. Expected: it fires if a part fails. If no part fails in the test, record "not observed" and do not count it as a failure.
- [ ] `control.change`: switch control to another vessel, with the game's vessel switch or `v.debug:control()` on the other vessel. Expected: `control.change` fires with `payload.from` and `payload.to`.
- [ ] `editor.enter` and `editor.exit`: open the vehicle editor, then close it. Expected: one event each. `editor.enter` carries `payload.new_vehicle`.
- [ ] `save.write`: save the game. Expected: `save.write` fires with `payload.directory` set to the save folder, and the sidecar appears there (section 12).
- [ ] `save.load`: load that save. Expected: `save.load` fires with the same directory.
- [ ] `system.load`: start or load a game in another planetary system. Expected: `system.load` fires with `payload.system`.
- [ ] `warp.change`: change time warp with the game's control or `ksa.time:set_warp(f)`. Expected: `warp.change` fires with `payload.from` and `payload.to`.
- [ ] `burn.complete`: run a burn (section 5). Expected: `burn.complete` fires once, when the burn finishes.
- [ ] `burn.fuel_out`: plan a burn far larger than the propellant on board, for example 5000 m/s. Expected: `burn.fuel_out` fires when the flight computer stops for lack of propellant, and `burn.complete` does not fire.

### Snapshot differ events

- [ ] `engine.ignite` and `engine.shutdown`: call `v:ignite()` and then `v:shutdown()`. Expected: each fires a few steps after the call, when the engine physically changes state, which is later than `engine.activate` and `engine.deactivate`. `payload.engine` names the engine.
- [ ] `situation.change`: land a vehicle (`Landed`) and then lift off again. Expected: `situation.change` fires at landing and at liftoff, with `payload.from` and `payload.to`. The `situation` getter agrees.
- [ ] `soi.change`: fly across a sphere-of-influence boundary, or use `debug.teleport` to move the vessel into a moon's sphere. Expected: `soi.change` fires with `from` and `to` bodies.
- [ ] `flameout`: run an engine until its propellant runs out, or drain its tank. Expected: `flameout` fires with `payload.engine`.
- [ ] `battery.depleted`: with the lights on and no power source, wait until the battery empties (use warp). Expected: it fires once, when the battery reaches empty. Record the time taken.
- [ ] `battery.charged`: call `v.debug:refill_battery()` on a vehicle with an empty battery. Expected: it fires once, when the battery is full.
- [ ] `animation.complete`: call `v.animations[1]:set_goal(1)` and wait for the animation. Expected: it fires with `payload.animation` when the goal is reached.
- [ ] gatOS alias names: `docked`, `undocked`, `decoupled`, `active-changed`, `warp-changed`, `vessel-appeared`, `vessel-removed`, `situation-change`, `soi-changed`, `engine-state`, `animation-complete`, `battery-depleted`, and `battery-charged`. Repeat the matching trigger above with a handler on the alias name. Expected: each alias fires on its trigger. Record any that does not.

### Sync and async handlers

- [ ] Sync hook: `ksa.on("engine.activate", function(ctx) ksa.console.log("sync", ctx.sync) end, {sync = true})`, then call `v:ignite()`. Expected: `ctx.sync` is true, and the line appears inside the `ignite()` call, before the engine state changes.
- [ ] Async hook (the default): `ksa.on("engine.activate", fn)`, where `fn` calls `ksa.sleep(2)` before it logs. Expected: `ctx.sync` is false, the line appears about 2 simulated seconds after the trigger, and there is no error.
- [ ] Waiting in a sync hook: register `ksa.on("stage", fn, {sync = true})`, where `fn` calls `ksa.sleep(1)` in Lua, or is `async` and awaits in TypeScript. Then stage the vehicle. Expected: a logged error says that a sync hook cannot wait. The hook is rejected or demoted to async (record which). The stage still happens, and the game does not freeze.

### Vessel filter

- [ ] `ksa.on("stage", fn, {vessel = "active"})`, then stage the controlled vessel. Expected: it fires. Switch control to another vessel and stage that one with `ksa.vessel(id):stage()`. Expected: the handler does not fire, because the filter is checked at delivery time.
- [ ] `ksa.on("stage", fn, {vessel = v})` with a VesselRef `v`. Expected: it fires only when the event's vessel is `v`.

## 7. Ticks and waits

- [ ] `ksa.every("frame", fn)`, with a counter that logs every 300 calls. Expected: the counter rises about once per rendered frame, close to the display refresh rate.
- [ ] `ksa.every("step", fn)`. Expected: `ctx.step_index` increases by one per call. While the game is paused the calls stop, because paused frames are not steps.
- [ ] `ksa.every(1, fn)` (the ut clock by default). Expected: one call per simulated second. At 1x that is about one per real second, and at the largest warp factor it is about that factor per real second.
- [ ] `ksa.every(1, fn, {clock = "wall"})`. Expected: one call per real second at 1x and at the largest warp factor.
- [ ] `ksa.every(1, fn, {clock = "render"})`. Expected: one call per real second at 1x and at the largest warp factor. Pause the game and record whether the render clock differs from the wall clock.
- [ ] At the largest warp factor, `ksa.sleep(10)` (ut clock). Expected: it returns after 10 simulated seconds, which is a fraction of a real second.
- [ ] At the largest warp factor, `ksa.sleep(10, "wall")`. Expected: it returns after about 10 real seconds.
- [ ] `ksa.wait_until(function() return false end, {timeout_seconds = 3})` in Lua, or `await ksa.until(() => false, { timeoutSeconds: 3 })` in TypeScript. Expected: it resolves `false` after about 3 simulated seconds.
- [ ] `ksa.wait_until(function() return ksa.time.ut > 0 end)` in Lua, or `await ksa.until(() => ksa.time.ut > 0)` in TypeScript. Expected: it resolves `true` at the next step slot.
- [ ] `ksa.wait_event("burn.complete", {timeout_seconds = 5})` in Lua, or `await ksa.waitEvent("burn.complete", { timeoutSeconds: 5 })` in TypeScript, with no burn running. Expected: it resolves nil (Lua) or null (TypeScript) after about 5 simulated seconds.
- [ ] `await ksa.waitEvent("stage", { vessel: "active" })`, then stage the vehicle. Expected: it resolves with an event whose type is `stage` and whose vessel id is the active vessel's id.
- [ ] Loop `await ksa.nextStep()` and `await ksa.nextFrame()` for 5 seconds, logging `step_index` and `frame`. Expected: `step_index` and `frame` both increase. While paused, `nextStep` does not resolve.

## 8. Bindings tab

- [ ] Open a script that exports a handler (the event handler template exports `onEvent`). In the Bindings tab, choose Add binding, then event `stage`, export `onEvent`, vessel `active`, and sync off. Expected: a row appears, and `bindings.toml` gains a `[[binding]]` table with `script`, `export`, `kind = "event"`, `key = "stage"`, `sync = false`, `vessel = "active"`, and `enabled = true`.
- [ ] Stage the active vessel. Expected: the exported function runs, and its log line appears.
- [ ] Reload the script, then stage again. Expected: the row is still present and the function still fires once per stage.
- [ ] Quit KSA completely, start it again, and load the same script. Expected: the row and its firing are restored from `bindings.toml`.
- [ ] Untick `enabled`. Expected: the file shows `enabled = false`, and staging no longer runs the function. Tick it again and confirm that it runs.
- [ ] Remove the row. Expected: its `[[binding]]` table is gone from the file, and staging no longer runs the function.
- [ ] Look at a hook declared with `ksa.on` in the script. Expected: it is shown greyed as "declared in code" and cannot be removed in the UI.
- [ ] Add a tick binding (kind `tick`, key `every`, interval 1.0, clock `ut`). Expected: the file row has `interval_seconds = 1.0` and `clock = "ut"`, and the export runs about once per simulated second.

## 9. Terminal scripts

- [ ] Create a terminal app script in TypeScript. In the Terminal tab, choose Open 2D window. Expected: a purrTTY window opens showing the panel, its box, title, and button. No OS shell appears.
- [ ] Click the button with a press and then a release. Expected: the light toggles on the release, not on the press. A second click toggles it back, and the panel redraws to show the new state.
- [ ] Resize the purrTTY window. Expected: the panel redraws to fill the new grid, and nothing is left over from the old size.
- [ ] Press `q`. Expected: the script stops, and the window shows `[script exited]` as its last line. A single-session 2D window hides the tab bar, so this line is the only exit indication. The Script Manager badge returns to idle.
- [ ] Close the window, then reopen it from the Terminal tab (a detach, then an attach). Expected: the screen repaints from the output buffer, showing the panel and the light state, with no leftover garbage.
- [ ] Choose Place on part, then pick the controlled vessel and a part near a light. Expected: a quad appears on the part, showing the panel.
- [ ] Click the button on the quad with a press and then a release. Expected: the light toggles on the release. The click that focuses the quad does not toggle it.
- [ ] Attach a 2D window and an in-world quad with different grids (for example 80x24 and 40x12). Expected: `ctx.term.size` reads the smaller grid (40x12), and the panel draws to that size. Close the smaller surface. Expected: `term.resize` fires and the size becomes the remaining surface's grid.
- [ ] Press Ctrl+C in the window. Expected: a `cancel` event reaches the script if it subscribes with `ctx.term.on("cancel", fn)`. Ctrl+C alone does not kill the terminal.
- [ ] Stop the script from the Script Manager. Expected: `[script exited]` is printed in the window, and the placement detaches. Start the script again. Expected: the placement recorded in `terminals.toml` is re-created.
- [ ] Destroy the vehicle that carries a quad while the script runs. Expected: the quad is removed, the script receives `term.detach`, and there is no crash.

## 10. Editor

- [ ] Choose Edit on a Lua script. Expected: nvim opens in a purrTTY window with the file loaded and the shipped banner shown. No OS shell appears.
- [ ] Lua completion: in nvim, start a new line with `ksa.` and trigger completion with the key configured in `init.lua` (record the key). Expected: the list includes `sleep`, `every`, `on`, `wait_until`, `vessel`, `active`, and `console`, from the generated `ksa.lua` stub.
- [ ] Lua method completion: type `local v = ksa.active; v:` and trigger completion. Expected: colon-call methods with the `set_` prefix (`set_throttle`, `set_lights`) and the verbs (`ignite`, `stage`).
- [ ] TypeScript completion: in a `.ts` script, type `ksa.active?.` and trigger completion. Expected: `setThrottle`, `setLights`, `ignite`, and `stage` from `host.d.ts`, served by `tsc --lsp`.
- [ ] Change a string in the file and save with `:w`. Expected: the script reloads, the next tick or log line uses the new text within one frame, and the Output tab shows the reload.
- [ ] Write `local x = = 1` and save with `:w`. Expected: nvim shows a diagnostic on that line, and the Output tab shows the error with `scripts/<file>.lua:<line>`. The game keeps running. Repeat with a TypeScript syntax error. Expected: the same two places show it.
- [ ] Type `:!ls` in nvim. Expected: it fails with a "shell failed to start" style error, and no OS shell opens.
- [ ] Type `:terminal` in nvim. Expected: it fails to open a terminal buffer, and no OS shell opens.
- [ ] Type `:q`. Expected: nvim exits, the purrTTY window closes, and the RPC port is released. A second Edit starts a fresh session.
- [ ] With the script already open, choose Edit again. Expected: the existing terminal is focused, and no second nvim starts.
- [ ] Press `Ctrl+Z` in normal mode. Expected: nothing happens, and nvim is not suspended.

## 11. Console commands and REPL shells

- [ ] `script list`. Expected: each script is listed with its state.
- [ ] `script run <file>`. Expected: the ad-hoc script runs, and its output appears in the console.
- [ ] `script start <file>`, then `script stop <file>`. Expected: the service starts and stops.
- [ ] Edit a file, then `script reload <file>`. Expected: the new code is used.
- [ ] `script edit <file>`. Expected: the same result as Edit in the Script Manager.
- [ ] `script stubs`. Expected: the generated stub files are rewritten (`scripts/stubs/host.d.ts`, `ksa.lua`, and the tsconfig and LuaLS config), and their modification times change.
- [ ] `script events`. Expected: the event names from section 6 are listed.
- [ ] `script call <script> <export>`. Expected: the named exported function is called. The design documents do not fix the argument form, so record the usage text that the command prints.
- [ ] `script` with an unknown subcommand. Expected: usage text is printed, and nothing throws.
- [ ] Open the Lua REPL (`ksa-lua-repl`, from purrTTY's shell choice). Type `ksa.time.ut`, then `return ksa.active and ksa.active.name`. Expected: each prints its value. Type `ksa.console.log("hi")`. Expected: `hi` prints in the REPL and in the console.
- [ ] Open the TypeScript REPL (`ksa-ts-repl`). Type `ksa.time.ut`, then `ksa.active?.name`. Expected: each prints its value. Type `console.log("hi")`. Expected: `hi` prints.
- [ ] In each REPL, type an erroring line (for example `ksa.nope()` in Lua). Expected: an error line prints, and the REPL keeps working.

## 12. Persistence

- [ ] In a Lua tick, run `ksa.state.set("count", (ksa.state.get("count") or 0) + 1)` once per second. Expected: the count rises.
- [ ] Save the game. Expected: `save.write` fires, and `catcom-state.toml` appears in the save folder named in the payload, holding the script's state.
- [ ] Let the count rise further, then load that save. Expected: `save.load` fires, and the count restarts from the saved value, not from the later one.
- [ ] In a TypeScript script, use `ksa.state.counter = 1`, then save and reload. Expected: the same result as Lua.
- [ ] Store a function value: `ksa.state.set("f", function() end)` in Lua, or `ksa.state.f = () => 1` in TypeScript. Expected: the write is rejected with an error in the Output tab, and the other keys are unchanged.
- [ ] Load a save with no sidecar (for example an older save). Expected: it loads without an error, and `ksa.state.get("count")` returns nil in Lua or null in TypeScript.

## 13. Safety and limits

- [ ] Lua runaway loop: a service with `function main(ctx) while true do end end`. Expected: the watchdog stops it within about `call_budget_ms` (50 ms). The Output tab and log show a timeout error ("script timed out"), the script is marked faulted, and the game keeps running at normal frame rate. Record whether the script shows `disabled` afterwards; it is disabled after `fault_limit` consecutive faults.
- [ ] JIT caveat for the item above: with `lua_jit_enabled = true`, a compiled loop can run without returning to the interpreter. The count hook is checked inside traces only in the build's CHECKHOOK configuration. If the game freezes, does not stop, or crashes, record it, set `lua_jit_enabled = false` in `catcom.toml`, restart, and repeat. Record the result with the JIT on and again with it off.
- [ ] TypeScript runaway loop: an ad-hoc `export function main(ctx) { for (;;) {} }`. Expected: it is interrupted within about 100 ms, the error is reported as interrupted or timed out, the script is faulted, and the game stays responsive.
- [ ] Sync hook budget: `ksa.on("stage", fn, {sync = true})`, where `fn` runs `while true do end`, then stage a vehicle. Expected: the sync budget (`sync_hook_budget_ms`, 5 ms) interrupts the hook, the hook is demoted to async with a logged error, and the stage completes without a freeze.
- [ ] Unbounded allocation (Lua): `local t = {} while true do t[#t + 1] = string.rep("x", 1048576) end` in an ad-hoc run. Expected: the script is faulted at the memory limit (`lua_memory_limit_kb`, 64 MiB), an error is logged, and the game stays responsive.
- [ ] Unbounded allocation (TypeScript): `const a: string[] = []; for (;;) a.push("x".repeat(1 << 20));` in an ad-hoc run. Expected: the engine heap limit (`v8_heap_limit_mb`, 64 MiB) stops the script, an error is logged, and the game stays responsive.
- [ ] Fault limit: `ksa.every("step", function(ctx) error("boom") end)`. Expected: one error line per fault. After `fault_limit` (3) consecutive faults the script is disabled, its badge shows disabled, and the other scripts keep running. Record the exact log lines.
- [ ] Script errors never crash the game: load one script with a syntax error and one whose event handler raises at runtime. Expected: both show in the Output tab with file and line, and the game keeps running.
- [ ] Vehicle destroyed mid-hook: a service that waits with `ksa.wait_until(function() return ksa.active ~= nil end)` and then sets the throttle every second. Destroy the vehicle while the loop runs. Expected: reads return nil or empty, writes return `NotFound`, `vessel.destroy` fires, and there is no crash.
- [ ] Vehicle destroyed mid-hook with a terminal quad attached (section 9). Expected: the quad is removed, the script receives `term.detach`, and there is no crash.
- [ ] 50 scripts with step hooks under warp. Create 50 files, each containing `ksa.every("step", function(ctx) end)`. Windows PowerShell: `1..50 | ForEach-Object { 'ksa.every("step", function(ctx) end)' | Set-Content "$env:USERPROFILE\Documents\My Games\Kitten Space Agency\mods\catcom\scripts\load-$_.lua" }`. Linux bash: `for i in $(seq 1 50); do echo 'ksa.every("step", function(ctx) end)' > "$HOME/Documents/My Games/Kitten Space Agency/mods/catcom/scripts/load-$i.lua"; done`. Set warp to 1000x if `warp_speeds` contains it, otherwise to the largest entry, and wait 60 seconds of real time. Expected: the frame rate stays close to its 1x value, no script is faulted, and the Runtimes tab's per-slot budget stays within `frame_budget_ms` (2.0 ms). Record the frame rate and the budget figures, then delete the 50 files.

## 14. Packaging

- [ ] List the installed mod folder. Expected: `mod.toml`, `Catcom.GameMod.deps.json`, the `Catcom.*.dll` assemblies, `ClearScript.Core.dll`, `ClearScript.V8.dll`, `ClearScript.V8.ICUData.dll`, `Newtonsoft.Json.dll`, `Tomlyn.dll`, `MessagePack.dll`, `runtimes/<rid>/native/`, `scripts-lib/` (`tui.ts`, `tui.lua`, `README.md`), `editor/config/ksa-editor/init.lua`, `editor/noshell`, `editor/noshell.cmd`, `assets/typescript.js`, `assets/sucrase.bundle.js`, `LICENSE`, `THIRD-PARTY-NOTICES.md`, and `third-party-licenses/`.
- [ ] On Windows run `python3 tools/check-dist.py "<dist>" --rids win-x64` (use `py -3` if `python3` is absent). On Linux run `python3 tools/check-dist.py "<dist>" --rids linux-x64`. Expected: no `ERROR` line and 0 errors in the summary. The default `--rids` requires both RIDs, and each machine has only its own natives, so pass the local RID. A `WARN` for missing editor blobs is acceptable only when section 10 is recorded as blocked.
- [ ] Natives: `runtimes/win-x64/native/` holds `lua51.dll` and `ClearScriptV8.win-x64.dll`, and `runtimes/linux-x64/native/` holds `libluajit-5.1.so.2` and `ClearScriptV8.linux-x64.so`. Expected: both are present, and `check-dist` reports the expected architecture for each.
- [ ] `0Harmony.dll` and `StarMap.API.dll` are not in the dist folder. Expected: neither is present. `check-dist` reports an error if either is.
- [ ] Start the game from the installed folder, not the build folder. Expected: the `catcom started` line appears (as in section 1).

## 15. Known limitations to observe

Record anything odd. The items below are expected limitations; confirm each one and note what you see.

- [ ] Lua `vim.system()` and `jobstart()` inside the editor can still start processes. The `shell` stub blocks only the shell UI (`:!` and `:terminal`, section 10). Expected: `:!ls` and `:terminal` are blocked. Record whether `:lua vim.system({"ls"})` runs.
- [ ] TypeScript does not check event names. `ksa.on("engine.activat", fn)` is accepted with no error and never fires. Check the spelling against `script events`.
- [ ] A single-session 2D window hides its tab bar, so the `[script exited]` line is the only exit indication.
- [ ] purrTTY consumes its own toggle key (F12) before nvim sees it.
- [ ] `part.failure` may not fire in practice (section 6).
- [ ] The JIT behaviour under a runaway loop (section 13).
- [ ] Solver-phase calls (`set_attitude_mode`, `burn`, and the debug actions) wait for the solver slot, so their results arrive later than Frame writes. Record the delay observed under warp.
- [ ] Anything else odd: record it with the exact log line.

## Appendix: test scripts

Copy each script into `scripts/` (for example with New, then paste the contents over the template, or write the file directly). The scripts were type-checked against the golden `host.d.ts`, and the Lua file was compiled with LuaJIT.

Test script 1, Lua, ad-hoc read only (`telemetry.lua`):

```lua
-- Test 1 (Lua, ad-hoc, read only). Run: script run telemetry.lua
local function main(ctx)
  local v = ksa.active
  if v == nil then
    ksa.console.log("no active vessel")
    return
  end
  local p = v.position.cci
  ksa.console.log(string.format("%s situation=%s alt=%.1f m radar=%.1f m mass=%.1f kg",
    v.name, v.situation, v.altitude.barometric, v.altitude.radar, v.mass.total))
  ksa.console.log(string.format("ut=%.1f warp=%s cci=(%.0f, %.0f, %.0f)",
    ksa.time.ut, tostring(ksa.time.warp), p.x, p.y, p.z))
  for _, e in ipairs(v.engines) do
    ksa.console.log(string.format("engine %d active=%s vac_thrust_n=%.0f",
      e.index, tostring(e.active), e.vac_thrust_n))
  end
  ksa.console.log("battery fraction=" .. tostring(v.battery.fraction))
end

return { main = main }
```

Test script 2, TypeScript, ad-hoc read and write (`throttle-pulse.ts`):

```ts
// Test 2 (TypeScript, ad-hoc, read and write). Run: script run throttle-pulse.ts
import { ksa } from "ksa";

export async function main(ctx: ksa.MainContext): Promise<void> {
  const v = ksa.active;
  if (v === undefined) {
    ctx.log.warn("no active vessel");
    return;
  }
  ctx.log.info(`${v.name}: situation=${v.situation} alt=${v.altitude.barometric.toFixed(1)} m mass=${v.mass.total.toFixed(1)} kg`);
  const on = v.setThrottle(0.5);
  ctx.log.info("setThrottle(0.5):", on.outcome, on.message ?? "");
  await ksa.sleep(2);
  const off = v.setThrottle(0);
  ctx.log.info("setThrottle(0):", off.outcome);
}
```

Test script 3, TypeScript, event logger (`event-logger.ts`). The hooks are declared in code, so the script is active as soon as it loads:

```ts
// Test 3 (TypeScript, event logger). Declared in code, so it is active as soon as the script loads.
import { ksa } from "ksa";

// Async (default): runs in the next step slot and may await.
ksa.on("situation.change", (ctx) => {
  ctx.log.info("situation.change", ctx.ut, ctx.vesselId ?? "-", JSON.stringify(ctx.payload));
});

// Filtered to the controlled vessel by the string token "active".
ksa.on("stage", (ctx) => {
  ctx.log.info("stage (active vessel)", ctx.vesselId ?? "-", JSON.stringify(ctx.payload));
}, { vessel: "active" });

// Sync: runs inline inside the game call that raised it; it must not await.
ksa.on("engine.activate", (ctx) => {
  ctx.log.info("engine.activate sync=", ctx.sync, JSON.stringify(ctx.payload));
}, { sync: true });
```
