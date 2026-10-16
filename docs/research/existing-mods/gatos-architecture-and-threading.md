---
type: Component Analysis
title: gatOS game-side architecture and the three game-thread write lanes
description: How gatOS gets onto the game thread, the Frame / Solver / post-solver lanes it uses to mutate KSA state safely, its CommandQueue contract and result vocabulary, and the F2-proof per-frame driver trick; the model an in-process script host must replicate.
resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.GameMod/Game/Mod.Game.cs
tags: [gatos, threading, game-thread, solver-phase, command-queue, harmony]
sources:
  - id: report
    resource: /references/gatos-internals-report-2026-10-16.md
    title: gatOS internals research report (2026-10-16)
  - id: modgame
    resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.GameMod/Game/Mod.Game.cs
    title: Mod.Game.cs
  - id: cmdqueue
    resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.SimFs/Commands/CommandQueue.cs
    title: CommandQueue.cs
  - id: simcommand
    resource: /Users/asherwin/repos/meow-sci/gatOS/gatOS.SimFs/Commands/SimCommand.cs
    title: SimCommand.cs
  - id: agents
    resource: /Users/asherwin/repos/meow-sci/gatOS/AGENTS.md
    title: gatOS AGENTS.md
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:00:00Z }
status: stable
---

# The data path in one picture

```
                 ┌──────────── game thread ─────────────────────────────────────────────┐
KSA objects      │ TelemetrySampler.Tick(dt) → VesselReader/PartsReader/BodyReader       │
                 │   → new SimSnapshot(...) → SnapshotStore.Publish (volatile swap)       │
                 │ CommandQueue.Drain(Frame,  KsaCatalog, 64)  ← per frame                │
                 │ CommandQueue.Drain(Solver, KsaCatalog, 64)  ← Harmony prefix on         │
                 │                                                Universe.ExecuteNextVehicleSolvers
                 └────────────────────────────────────────────────────────────────────────┘
                        ▲ SimCommand (immutable)                 │ SimSnapshot (immutable)
   transport threads: 9P / HTTP / MQTT / MCP / serial → ICommandSink.SubmitAsync → await (2 s timeout)
```

Only `gatOS.GameMod` references KSA; every game touch lives under `Game/Ksa/**`. Two immutable records are the firewall: `SimSnapshot` (reads) and `SimCommand` (writes).[^report][^agents]

For an in-process script host the marshalling problem **inverts**: scripts ticked from the same per-frame slot are already on the game thread and may read live KSA objects directly. What must still be copied exactly is (a) the Solver-phase lane, (b) the post-solver slot, (c) per-vehicle try/catch plus health latching, and (d) the action vocabulary so scripts and `/sim` agree.[^report]

# StarMap hooks gatOS uses

| Hook | What runs there |
|---|---|
| `[StarMapAllModsLoaded] OnFullyLoaded()` | builds stores/queue/tree, starts servers, `InstallSolverHook()`, `InstallCameraHook()`, `InstallDisplayHook()`, `InstallMenuFallback()` |
| `[StarMapBeforeGui] OnBeforeUi(dt)` | `DrivePerFrame(dt)`: sample → tick schedules → drain Frame commands → audio/paint drivers. StarMap implements this as a prefix on `Program.OnDrawUiFrame`, which is inside `if (DrawUI)`, so **F2 (hide UI) stops it** |
| `[StarMapAfterGui] OnAfterUi(dt)` | `DrivePostSolver(dt)` (welds, IVA physics) + `DrawGameUi()`; also F2-gated |
| `[StarMapAfterOnFrame] OnAfterFrame(t, dt)` | postfix on `Program.OnFrame`, always fires; re-runs the drivers **only** on frames where the GUI hooks were skipped (a boolean latch), making the pipeline F2-proof |
| `[StarMapUnload] Unload()` | tear down cheats → remove camera hook → remove solver hook → remove menu fallback → dispose display capture |

The normal sample→schedule→drain path is actually a Harmony prefix on `GameViewport.OnFrame(double)` for `Program.MainViewport`, which runs **after simulation advance and before KSA rebuilds camera matrices**; a `_mainViewportPreparedThisFrame` latch stops the GUI path from double-stepping.[^modgame]

# The three write lanes

| Lane | Where | What drains there | Why |
|---|---|---|---|
| **Frame** | `queue.Drain(CommandPhase.Frame, catalog, 64)` from `DrivePerFrame` | every action not in `SimCommand.SolverActions` | ordinary mutations |
| **Solver** | `Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)` installed via `AccessTools.Method` | `debug.refill_fuel`, `debug.refill_battery`, `debug.teleport`, `debug.impulse`, `vessel.attitude_mode`, `vessel.attitude_frame`, `vessel.attitude_target`, `vessel.burn`, `vessel.rcs_mode` | resource state must be solver-visible the same tick; `Vehicle.Teleport` detaches from a `PhysicsBubble` owned by the solver worker, and the prefix runs after `VehicleSolver.Wait()` and before the job is re-queued; KSA's async solver **snapshots and restores `FlightComputer` every frame (`FlightComputer.CopyFrom`)**, so a Frame-phase flight-computer write "flashes on, then reverts to manual" |
| **Post-solver** | `DriveWelds`/`DriveIvaPhysics` in `[StarMapAfterGui]` | per-frame re-posing | both call `JobSystems.VehicleSolver.Wait()` first so kinematics are settled |

Phase is derived from the action key: `PhaseFor(action) => SolverActions.Contains(action) ? Solver : Frame`, never chosen at a call site.[^simcommand]

# CommandQueue contract (verbatim shape)

```csharp
public interface IPostObserver { void OnCommandResult(int token, CommandResult result); }   // inline on the game thread; trivial
public sealed class CommandQueue : ICommandSink
{
    public CommandQueue(bool controlEnabled, bool debugEnabled, TimeSpan timeout);            // timeout ≤ 0 → 2 s
    public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct);
    public Task<CommandResult> SubmitBatchAsync(IReadOnlyList<SimCommand> commands, CancellationToken ct); // one phase, same tick
    public void Post(SimCommand command, IPostObserver? observer = null, int token = 0);      // fire-and-forget
    public int Drain(CommandPhase phase, ICommandExecutor executor, int maxCommands);         // GAME THREAD ONLY
}
```

Mechanics to copy: two `ConcurrentQueue<Pending>`; `TaskCompletionSource` created with `RunContinuationsAsynchronously` so an awaiting transport thread never resumes inline inside `Drain`; 2 s timeout marks the pending `Abandoned` and returns `TimedOut`; `Drain` dequeues up to 64 per frame, skips abandoned, executes whole batches atomically, converts executor throws into `Fault`.[^cmdqueue]

Result vocabulary: `CommandResult(CommandOutcome Outcome, string? Message)` with `Ok, Invalid(EINVAL/400), NotFound(ENOENT/404), Denied(EACCES/403), Busy(EBUSY/409), Fault(EIO/500), TimedOut(ETIMEDOUT/504), Unsupported(EOPNOTSUPP/501)`. The executor contract is "never throw".[^report]

# What ksa-scripting takes from this

- Tick scripts where gatOS drains: a Frame slot (viewport prefix or `[StarMapBeforeGui]` with the `[StarMapAfterOnFrame]` F2-proof latch), a Solver slot (`Priority.First` prefix on `Universe.ExecuteNextVehicleSolvers`), and a post-solver slot (`JobSystems.VehicleSolver.Wait()` first).
- Keep the Solver action set as the phase truth table so flight-computer setpoints written by scripts stick.
- In-process callers already on the game thread may skip the queue but must keep batch and phase semantics; off-thread callers (REPL output threads, file watchers, nvim exit callbacks) use the queue.

[^report]: gatOS internals research report (2026-10-16)
[^modgame]: Mod.Game.cs
[^cmdqueue]: CommandQueue.cs
[^simcommand]: SimCommand.cs
[^agents]: gatOS AGENTS.md
