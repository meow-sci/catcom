# Plan 00b — purrTTY side: cross-mod terminal launcher contract, in-world part addressing, small window/shell touch-ups

Status: proposed · Repo: `/Users/asherwin/repos/meow-sci/purrtty`, branch `feature/scripting` (currently identical to `main` at `790a82b`) · Depends on: nothing (parallel with Plan 00; consumed from Plan 00 Phase E/G) · Research: [purrTTY launcher gap](../docs/research/existing-mods/purrtty-terminal-launcher-gap.md), [virtual-pty bridge](../docs/research/existing-mods/purrtty-virtual-pty-bridge.md), [in-world terminals](../docs/research/existing-mods/purrtty-in-world-terminals.md), [custom shell contract](../docs/research/existing-mods/purrtty-custom-shell-contract.md), raw report [references/purrtty-virtual-pty-report](../docs/research/references/purrtty-virtual-pty-report-2026-10-16.md) §6

## 1. Goal

Let another mod (ksa-scripting) open and manage purrTTY terminals from code — a 2D window or tab running either a registered custom shell (the script's virtual terminal) or a real process (the shipped nvim), and an in-world terminal on a vehicle part addressed by `Part.InstanceId` — with handles, lifecycle events and main-thread marshalling hidden inside purrTTY. Keep the ABI in the already-exported `purrTTY.CustomShellContract`; export nothing new.

## 2. Work breakdown

### A. Contract additions (`purrTTY.CustomShellContract`, namespace `purrTTY.Core.Terminal`, new files, additive only) — ~200 lines

```csharp
public enum TerminalPlacementKind { Window, Tab, InWorldPart, InWorldBillboard }
public sealed class TerminalProgramSpec { string? CustomShellId; string? Program; List<string> Arguments; string? WorkingDirectory; Dictionary<string,string> Environment; string? StartupCommand; Dictionary<string,object> Configuration; }
public sealed class TerminalWindowOptions { string? Name, Title, ThemeName; float? PosX, PosY, Width, Height; int? Cols, Rows; bool? LockMode, HideChrome, NoResize; bool Focus = true; bool ShowTerminal = true; }
public sealed class TerminalInWorldOptions { string? Name, ThemeName; int Cols = 40, Rows = 12; string VehicleId = ""; string PartId = ""; uint? PartInstanceId; string SubPartId = ""; float OffsetX, OffsetY, OffsetZ = 0.05f, RotationX, RotationY, RotationZ, WidthMeters = 0.6f, HeightMeters = 0.2f; float? BackgroundOpacity, ForegroundOpacity, CellBackgroundOpacity; bool Billboard; float BillboardDistance = 5f; bool BillboardAlwaysOnTop = true, BillboardClickToFocus = true; bool Focus; }
public sealed class TerminalLaunchRequest { TerminalPlacementKind Placement; TerminalProgramSpec Program; TerminalWindowOptions? Window; TerminalInWorldOptions? InWorld; }
public enum TerminalLifecycleEvent { Created, Started, Focused, Unfocused, Resized, Exited, Closed }
public interface ITerminalHandle { Guid Id; string Name; TerminalPlacementKind Placement; bool IsOpen; bool IsRunning; int? ExitCode; int Cols; int Rows; event Action<ITerminalHandle, TerminalLifecycleEvent>? Lifecycle; Task FocusAsync(); Task CloseAsync(); Task RenameAsync(string); Task<bool> ResizeAsync(int cols, int rows); Task SetPlacementAsync(TerminalInWorldOptions); Task ApplyThemeAsync(string themeName); void WriteInput(ReadOnlyMemory<byte> bytes); }
public interface ITerminalLauncher { bool IsAvailable; Task<ITerminalHandle> LaunchAsync(TerminalLaunchRequest request, CancellationToken ct = default); IReadOnlyList<ITerminalHandle> Terminals; ITerminalHandle? Find(string name); event Action<ITerminalHandle, TerminalLifecycleEvent>? Lifecycle; }
public static class TerminalLauncherRegistry { public static ITerminalLauncher? Current { get; set; } public static event Action<ITerminalLauncher?>? Changed; }
```

POCOs and interfaces only (no `ProcessLaunchOptions`, ImGui or KSA types) so the contract keeps its dependency set. Bump the contract assembly's minor version; existing consumers keep binding.

### B. Launcher implementation (`purrTTY.GameMod/Launcher/TerminalLauncher.cs`, internal) — ~350–450 lines

- Built in `TerminalMod.InitializeTerminal` after the controller (rebound to the in-world manager in `TryInitializeInWorld`); published as `TerminalLauncherRegistry.Current`; nulled in `DisposeResources` (raise `Changed`).
- **Main-thread pump:** `ConcurrentQueue<Action>` drained at the top of `TerminalMod.OnAfterUi` (before `_controller.Render()` and `_inWorld.OnAfterGui(dt)`) so queued work runs with an ImGui frame active (precondition for `InWorldTerminalManager.Create`, `TerminalWindow` construction, `TerminalTargetRegistry`). `LaunchAsync` and every handle method enqueue and complete a `TaskCompletionSource` (`RunContinuationsAsynchronously`); inline execution when already pumping (thread-static flag). `Lifecycle` events raised from the pump (main thread).
- **Mapping:** `CustomShellId` → `ProcessLaunchOptions.CreateCustomGame(id)` (+ env/cwd/StartupCommand); `Program` → `CreateCustom(program, args)`; both null → `Configuration.CreateLaunchOptions()`; `Environment` merged.
- **Window/Tab:** `Tab` → `controller.OpenTab(opts, title)` (subscribe `SessionManager.SessionCreated` before the call to capture the session); `Window` → `controller.CreateConfiguredWindow(new WindowLayoutRecord { Name, Position, Size, ThemeName, Launch }, theme)`; apply `LockMode/HideChrome/NoResize`, `RequestFocus()` when `Focus`, then `controller.IsVisible = true` when `ShowTerminal` (**after** the window exists). `Cols/Rows` → pixels via `InWorldTerminalRenderer.MeasureCell(config, catalog, themeName)` + `ImGui.GetFrameHeight()`.
- **In-world:** build an `InWorldTerminalRecord` from `TerminalInWorldOptions`, `_inWorld.Create(record)`, `SetOpacities(...)`, `Focus` if requested; `SetPlacementAsync` mutates `Record.*` live on the main thread; `ResizeAsync` → `TrySetGridSize`; `ApplyThemeAsync` → `ApplyTheme(catalog.Find(name))`.
- **Close/focus/rename/write:** `controller.CloseWindow` / `_inWorld.Remove`, `RequestFocus` / `_inWorld.Focus`, `TryRename`, `session.SendInput(bytes)` (enqueued for ordering with user input).
- **Lifecycle:** `window.FocusChanged`, new `TerminalWindow.Closed` event, `SessionManager.SessionCreated/SessionClosed`, `TerminalSession.ProcessExited` (re-enqueued to the pump before raising `Exited`), in-world focus transitions (new `FocusChanged` on `InWorldTerminalInstance` or polled), `Surface.Cols/Rows` change → `Resized`.

### C. Display/Terminal touch-ups — ~30 lines

- `TerminalWindowSettings.NoResize` + OR the ImGui flag in `TerminalWindow.Render`.
- `public event Action<TerminalWindow>? Closed` raised in `TerminalWindow.Close()`/`Dispose`.
- Forward `TerminalProgramSpec.Configuration` into `CustomShellStartOptions.Configuration` in `CustomShellPtyBridge.StartAsync` (one line) — or rely on env vars (ksa-scripting uses `KSA_SCRIPT` in env regardless).
- `InWorldTerminalRecord.TargetPartInstanceId` (`uint?`) + a branch in `InWorldQuad.ResolveTopLevelPart/ResolvePart` matching `p.InstanceId` (preferred over `TargetPartId` when set); persist it in `LayoutTomlFormat` as `part_instance_id`.
- Optional: `public event Action<InWorldTerminalInstance, bool>? FocusChanged`.

### D. Tests and docs

- Pure mapping/state-machine class `TerminalLaunchMapper` in `purrTTY.Display` covered by `purrTTY.Display.Tests` (request → `ProcessLaunchOptions`/`InWorldTerminalRecord`, handle state transitions).
- Existing `CustomShellPtyBridgeTests` extended for `Configuration` forwarding.
- Docs: `docs/how-to.md` "Launch a terminal from another mod", `docs/code-navigation.md` (Launcher/), `docs/gotchas.md` (launcher main-thread pump; `IsVisible` ordering; in-world click-also-press), `AGENTS.md` contract list; fix the two stale notes found in research (`gotchas.md` §18 inbox size 24 MiB; `InWorldTerminalRenderer` class doc about input).
- Refresh the vendored contract DLLs in gatOS and ksa-scripting (`vendor/purrTTY/`) after merge.

## 3. Acceptance

From a test mod calling the contract: open a 2D window running a custom shell with cols×rows and a title; open a tab running `nvim` (real process) and observe `Exited` with the real exit code; create an in-world terminal on the controlled vessel by `PartInstanceId` running the custom shell, click it, see SGR mouse bytes arrive in the shell, move it live via `SetPlacementAsync`, resize via `ResizeAsync`, close it; lifecycle events arrive on the main thread; purrTTY's own menus and layouts keep working; `dotnet test purrtty.slnx` green (quiet, no fixed sleeps).

## 4. Risks

| Risk | Mitigation |
|---|---|
| Contract version drift between purrTTY and consumers | additive-only types; minor version bump; consumers treat `TerminalLauncherRegistry.Current == null` as "feature unavailable" |
| Launch requests before purrTTY initialised | `IsAvailable` + `Changed` event; ksa-scripting retries placements on `Changed` |
| `IsVisible` ordering spawning a default shell | create the window first (documented gotcha) |
