---
type: Design
title: Script exports, UI-created bindings, context objects, and the native binding bridge
description: How a script exposes callable functions, how the mod attaches them to events/ticks/services/terminals from the ImGui window (persisted bindings) or from code, what context object each invocation receives, and how every read, write and function call travels from Lua/TypeScript through the mod's bridge into KSA.
tags: [design, bindings, exports, context, bridge, api-schema]
sources:
  - id: api
    resource: /architecture/script-api-surface.md
    title: Script API surface
  - id: arch
    resource: /architecture/in-band-scripting-architecture.md
    title: In-band scripting architecture
  - id: hooks
    resource: /architecture/hook-model-sync-async.md
    title: Hook model
  - id: v8
    resource: /runtimes/v8-clearscript.md
    title: V8 via ClearScript
  - id: lua
    resource: /runtimes/luajit.md
    title: LuaJIT
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T23:30:00Z }
status: draft
---

# Exports

A script is a module. Its **exports** are the functions the mod can call.

| Language | How to export | How the mod discovers them |
|---|---|---|
| TypeScript | ES module `export function onIgnite(ctx: ksa.EventContext) {…}`; `export default` or `export function main(ctx)` for the entry | after import, enumerate the module namespace: `Object.entries(ns).filter(([,v]) => typeof v === 'function')` |
| Lua | the chunk returns a table `return { on_ignite = function(ctx) … end, main = function(ctx) … end }`; or calls `ksa.export("on_ignite", fn)` | iterate the returned table / the export registry |

The entry conventions: `main(ctx)` runs for **ad-hoc** (returns when done) and **service** scripts (loops until `ctx.signal.cancelled`); for **terminal** scripts `main(ctx)` receives `ctx.term`. Other exports are attachable to events and ticks.

# Bindings

A binding attaches one export to one trigger. Two sources, one registry:

- **Declared in code** (`ksa.on("engine.activate", fn, opts)`, `ksa.every("step", fn)`, `ksa.service(fn)`): created when the script loads, removed when it unloads; shown read-only in the UI.
- **Created in the UI** (Bindings tab): persisted in `<data>/bindings.toml` and applied on every load of that script:

```toml
[[binding]]
script = "ascent.lua"
export = "on_ignite"
kind = "event"            # event | tick | service | terminal
key = "engine.activate"   # event name, or "frame" | "step" | "every"
sync = false
vessel = "active"         # optional filter: active | <vessel id> | any
enabled = true

[[binding]]
script = "telemetry.ts"
export = "sample"
kind = "tick"
key = "every"
interval_seconds = 1.0
clock = "ut"
```

Reload semantics: the registry swaps all bindings of a script atomically when a new version loads; a failed load keeps the old version and its bindings alive and reports the error.

# Context objects (what the function receives)

```ts
interface BaseContext { ksa: typeof ksa; script: { name: string; path: string; state: Record<string, unknown> }; log: Logger }
interface EventContext extends BaseContext { event: { ut: number; type: string; vesselId?: string; payload: Record<string, unknown> }; vessel?: VesselRef; sync: boolean }
interface TickContext  extends BaseContext { ut: number; dt: number; stepIndex: number; frame: number }
interface ServiceContext extends BaseContext { signal: { cancelled: boolean; onCancel(fn: () => void): void } }
interface TerminalContext extends ServiceContext { term: Terminal }   // see terminal scripts
```

Lua receives the same shape as a table (`ctx.event.vessel_id`, `ctx.signal.cancelled`). The context is created per invocation (cheap: a small table/object with handles), and `ksa` is the same API object the module already imported.

# The bridge: how a call reaches KSA

```
script code                     runtime boundary                     mod (game thread)                         KSA
─────────────                   ──────────────────                   ─────────────────────────                ─────────
v.throttle(0.5)   ──TS prelude──▶ __host.act("vessel.throttle",     ActionExecutor.Execute(SimCommand)  ──▶ reflection write of
                                   id, -1, 0.5, …)                     │ validate · authority · PhaseFor         Vehicle._manualControlInputs
                   ──Lua prelude─▶ [UnmanagedCallersOnly] host_act    │ Frame: inline in Step slot                .EngineThrottle
                                                                       │ Solver: queue → ExecuteNextVehicleSolvers prefix

v.position.cci    ──prelude──▶   __host.read(id, "position.cci")     VesselReader field read (live Vehicle) ──▶ vehicle.GetPositionCci()
ksa.vessels()     ──prelude──▶   __host.list("vessels")               Universe.CurrentSystem.All.OfType<Vehicle>()
await ksa.sleep(2)──prelude──▶   __host.wait("sleep", 2) → Task/yield  Scheduler registers a UT deadline; resumes in a later Step slot
ksa.on(ev, fn)    ──prelude──▶   __host.subscribe(ev, fnRef, opts)    HookRegistry + Harmony/differ event producers
term.write(bytes) ──prelude──▶   __host.termWrite(termId, bytes)      ScriptTerminalShell.QueueOutput → purrTTY surface
```

- **One schema, three artefacts.** `ApiSchema` (C#) declares every namespace, member, parameter, doc string and action key. From it the mod generates: the TypeScript prelude + `host.d.ts`, the Lua prelude + `ksa.lua` (LuaCATS), and the C# dispatch tables. Adding an API member is a one-place change.
- **Host facade is small and coarse** (`read`, `snapshot`, `list`, `act`, `wait`, `subscribe`, `unsubscribe`, `log`, `term*`, `state*`): ~15 methods marshalled once per runtime (ClearScript `AddHostObject` with `DirectAccess`; LuaJIT `[UnmanagedCallersOnly]` functions in a table). The ergonomic object model (`VesselRef` with properties and methods) lives in the generated preludes, written in the script language itself, so per-member interop binding cost and `.d.ts` drift are avoided.
- **Values crossing the boundary** are primitives, strings, arrays, string-keyed maps, and opaque handle ids (`"vessel:Kitty-1"`, `"part:Kitty-1/1234"`, `"term:3"`). Snapshots cross as one JSON document per request (V8) or one table build (Lua); hot reads cross as scalars.
- **Results** are `CommandResult { ok, outcome, message }`; Solver-phase actions return a pending result (`Promise` / coroutine yield) resolved after the Solver slot.
- **Everything executes on the game thread** inside the Step slot or inside a Harmony patch (sync hooks). Off-thread entry points (terminal input from purrTTY's tick thread, nvim RPC, the file watcher) enqueue to the game-thread queue.

# Native bindings per runtime (what "provide bindings" means concretely)

| | V8 (ClearScript) | LuaJIT |
|---|---|---|
| Host object | `engine.AddHostObject("__host", HostItemFlags.DirectAccess, facade)`; Task-returning methods become Promises | `ksa_host` table of `lua_pushcclosure`'d `[UnmanagedCallersOnly]` functions; async methods `return lua_yield` |
| Prelude | system document `ksa` (generated JS) building classes over `__host` | generated Lua module `ksa` (loaded into the sandbox env) building metatables over `ksa_host` |
| Stubs | `scripts/stubs/host.d.ts` | `scripts/stubs/ksa.lua` + `.luarc.json` |
| Handles | strings; the prelude caches `VesselRef` objects per id | strings; prelude caches userdata-free tables per id |
| Errors | host exceptions become JS errors with messages | host returns `(false, msg)`; prelude raises with `error(msg, 2)` |

[^api]: Script API surface
[^arch]: In-band scripting architecture
[^hooks]: Hook model
[^v8]: V8 via ClearScript
[^lua]: LuaJIT
