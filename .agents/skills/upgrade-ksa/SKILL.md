---
name: upgrade-ksa
description: Upgrade catcom to a new Kitten Space Agency (KSA) game build. Use when ksa-game-assemblies gets a new build (or catcom CI goes red after one), to find and apply every change catcom needs - renamed/split Brutal assemblies in the csproj references, Harmony patch targets (Step/Solver slots, the 24 event hooks, UI patches, force-render), the CompileCheck stubs, reflection-reached private fields, [KsaAnchor] reader/actuator bindings, semantic drift (units, frames, game-loop order, input chain, console API), and the version stamps. Drives a baseline-vs-new diff of the KSA assemblies and decomp, applies fixes in a fixed order, and re-verifies with build, tests, dist and the in-game checklist.
---

# upgrade-ksa: move catcom to a new KSA build

catcom compiles against the KSA reference assemblies, patches about 30 game methods with Harmony,
reaches a few private fields by reflection, and reads and writes live vessel state through about 120
`[KsaAnchor]`-tagged bindings in `src/Catcom.Ksa` and `src/Catcom.GameMod`. A new game build can break
any of these. Some breaks show up at compile time. The dangerous ones break silently at runtime: an
event hook goes dark, an accessor latches degraded, or a value comes back in different units.

This skill tells you where to look, how to find the changes cheaply, and the order to apply them in.

## 0. Inputs and where they live

| Thing | Where | Notes |
|---|---|---|
| Snapshot repo | `$KSA_REPO`, default the sibling checkout `../ksa-game-assemblies` (the same tier-2 default `Directory.Build.props` uses) | Private repo `meow-sci/ksa-game-assemblies`. **One commit per game build, and the commit subject is the build string** (`2026.10.19.5638`). Its layout is `current/{dll,decomp,Content,version.json}`. |
| **BASE** build | The build catcom was last verified on: `VerifiedKsaVersion` in `src/Catcom.GameMod/UI/ScriptManagerWindow.Settings.cs` (it also appears in the README's "Built against KSA" line) | Diff from BASE, never from "the previous commit" in the snapshot repo. Builds that nobody reviewed may sit between them. |
| **NEW** build | Usually `HEAD` of `$KSA_REPO` (`cat $KSA_REPO/current/version.json`) | Ask the user if it is unclear which build is the target. |
| Brutal source | Sibling `../BRUTAL` (github.com/ahwootech/BRUTAL; Brutal went open source around 5638) | This is real source with comments, easier to read than the decomp. It has no tags, so its HEAD may not match the Brutal that KSA ships. **The DLLs in `current/dll` are authoritative.** |

Find the snapshot commits and get the BASE DLLs. Use a worktree in your scratchpad and never modify
the snapshot repo itself. All commands below run from the catcom root, with `$SCRATCH` set to your
scratchpad directory:

```bash
KSA_REPO=../ksa-game-assemblies
BASE=$(git -C $KSA_REPO log --format=%h --grep='\.5554$' -1)   # ← BASE build number
NEW=$(git -C $KSA_REPO log --format=%h -1)
git -C $KSA_REPO worktree list    # an existing worktree already at $BASE (e.g. ../ksa-game-assemblies_prev) can be read instead
git -C $KSA_REPO worktree add --detach "$SCRATCH/ksa-base" $BASE   # BASE dlls: $SCRATCH/ksa-base/current/dll
# … when done:  git -C $KSA_REPO worktree remove --force "$SCRATCH/ksa-base"
```

**Decomp layout:** the decomp has one folder per **namespace** (`current/decomp/KSA/Universe.cs`,
`current/decomp/Brutal.Numerics/…`, `current/decomp/RenderCore.Input/…`) and one `.csproj` per
**assembly** at the top level (`current/decomp/Brutal.Numerics.csproj`). The `SourceFile =` paths in
`[KsaAnchor]` are relative to `current/decomp/`.

**CI is unpinned.** `.github/workflows/build.yml` checks out ksa-game-assemblies at `ref: main`.
As soon as a new build is pushed there, every catcom CI run builds against it, and **every branch push
that would publish a release fails** until catcom is upgraded. If CI went red with no catcom change,
check whether ksa-game-assemblies `main` moved before you debug anything else.

**Check sibling repos first. It is cheap and often saves the most time.** gatOS (the source of most of
`Catcom.Ksa`'s readers and actuators), unscience (the source of `VesselForceRender`) and purrtty upgrade
to the same builds. They have their own `upgrade-ksa` skills and commits named
`upgrade(ksa): … <build>`:

```bash
for r in gatOS unscience purrtty; do git -C ../$r log --oneline -50 | grep -E '<NEW build or rev>'; done
```

When one of them already handled a member catcom also binds (same reader, same patch target), reuse
its finding. Still verify it against catcom's code, because the ports have diverged.

## 1. How catcom fails, by coupling kind

| Coupling | Where | Breaks at | What you see |
|---|---|---|---|
| **Assembly set** (a `Brutal.*` DLL renamed, split or removed) | `<Reference Include=… Condition="Exists(…)">` in 3 csprojs + 1 spike | **compile, misleadingly** | The reference silently drops out because its `Exists()` condition is false. You get **no** missing-file error, only `CS0234: The type or namespace name 'Numerics' does not exist in the namespace 'Brutal'` / `CS0246: 'double3' could not be found`. |
| Direct member use | `Catcom.Ksa`, `Catcom.GameMod` | compile | CS errors at `[KsaAnchor]` sites, which become the work list. `TreatWarningsAsErrors` is on, so a member KSA newly marks `[Obsolete]` (CS0618) also fails the build. |
| **Harmony target**, attribute-declared and stubbed | `SlotHost`, `EventPatches` rows, `VesselForceRender` | compile of `tests/Catcom.Ksa.CompileCheck` | That project mirrors each patch's target attributes and Prefix/Postfix parameter lists, and calls the target with **named** arguments, so renames and re-signs fail its build. |
| Harmony target, **not stubbed** | `UiPatches` (`HotkeyGuardPatch`, `MenuFallbackPatch`) | runtime, at load | `Log.Warn("UI patch '…' failed to apply …")`. GameMod's `nameof()` only proves the member still exists. A changed return type or a different meaning slips through. |
| Harmony **semantics** (the target still exists but means something else) | every patch | **silent** | The event fires at the wrong time or with the wrong payload, or a slot runs in the wrong phase. Only reading the decomp diff catches this. |
| **Reflection** by string | §4 list | runtime, silent | The field resolves to null, and the actuator returns `CommandResult.Fault` or latches degraded. |
| Reader/actuator **semantics** (units, frames, gating) | `Readers/`, `Actuators/`, `VesselCache` | silent wrong values | Nothing detects it. Diff the anchored members and the Content XML. |
| StarMap / Harmony / purrTTY | `mod.toml`, `Mod.cs`, `vendor/purrTTY` | load | These are not KSA, but they often ship alongside a game build (see §6). |

Runtime failures stay contained by design. An event patch that fails to install darkens one event
(`could not patch the '<row>' event hook (that event is dark)`), a slot patch that fails darkens one
slot, and a reader that throws latches its accessor degraded through `KsaHealth` and logs once. All
of these show up in-game under **Script Manager → Runtimes → "Game slots and event patches" /
accessor health**. That containment is why a green build proves so little.

## 2. Procedure

Run the steps in order. Each one is cheap relative to the next and narrows what the next step has to
look at.

### Step 1: changelog scan

Read `$KSA_REPO/current/version.json`: `build`, `fromRevision`..`toRevision`, `commits[].lines`. Note
the subsystems that moved (vehicles, staging, docking, flight computer, input, console, save/load,
time warp, part modules).

`commits[]` **has gaps.** `fromRevision` is whatever the snapshot tool last saw, not BASE. When BASE
is several builds back, read each intermediate commit's `version.json`
(`git -C $KSA_REPO show <sha>:current/version.json`). Never conclude "nothing relevant moved" from the
changelog alone. The diff in Step 4 is the complete record.

### Step 2: assembly-set diff

```bash
diff <(ls "$SCRATCH/ksa-base/current/dll") <(ls $KSA_REPO/current/dll)
git -C $KSA_REPO diff -M --stat $BASE $NEW -- current/dll | grep '=>'    # renames, as git detects them
```

Then list every DLL that catcom's projects reference but the NEW folder lacks:

```bash
KSA_DLL_DIR=$KSA_REPO/current/dll
grep -hoE 'KSAFolder\)[A-Za-z0-9.]+\.dll' src/*/*.csproj tests/*/*.csproj spikes/*/*.csproj \
  | sed 's/KSAFolder)//' | sort -u | while read f; do [ -f "$KSA_DLL_DIR/$f" ] || echo "missing: $f"; done
```

Fix each missing DLL in **all** of these files. They carry the same reference block and must stay in
sync:

- `src/Catcom.Ksa/Catcom.Ksa.csproj`
- `src/Catcom.GameMod/Catcom.GameMod.csproj`
- `tests/Catcom.Ksa.CompileCheck/Catcom.Ksa.CompileCheck.csproj`
- `spikes/HookCompileCheck/HookCompileCheck.csproj` (not in the solution, and its references are
  **unconditioned**, so it errors loudly instead)

Change both the `Include=` name and the `HintPath`/`Exists()` file name. Keep `Private=false`. Check
whether namespaces moved too: `grep -rhoE '^namespace [A-Za-z.]+' $KSA_REPO/current/decomp/<Ns>`.
If they did not, there are no `using` changes to make. `Catcom.Dist.csproj` excludes `Brutal.*.dll`
by glob, so it needs no change.

**Known rename: Brutal open-source release (BASE 5554 → 5638).** The assemblies were renamed and the
namespaces stayed the same:

| Old DLL (≤ 5554) | New DLL (≥ 5638) | Referenced by catcom |
|---|---|---|
| `Brutal.Core.Common` | `Brutal.Core` | Ksa, GameMod, CompileCheck, spike |
| `Brutal.Core.Numerics` | `Brutal.Numerics` | Ksa, GameMod, CompileCheck, spike |
| `Brutal.Core.Logging` | `Brutal.Logging` | Ksa, GameMod, CompileCheck |
| `Brutal.Core.Strings` | `Brutal.Strings` | Ksa, GameMod, CompileCheck |
| `Brutal.Core.Memory` | `Brutal.Memory` | Ksa, CompileCheck |
| `Brutal.Core.Collections` | `Brutal.Collections` | no |
| `Brutal.Core.Maths` | `Brutal.Maths` | no |
| `Brutal.Core.Package` | removed | no |

Unchanged: `Brutal.Concurrency`, `Brutal.ImGui`, `Brutal.ImGui.Abstractions`, `Brutal.Glfw`,
`Brutal.Vulkan`, `Brutal.Vulkan.Abstractions`, `Planet.Render.Core`, `KSA`. After this rename, the
NEW Brutal decomp is also checkable against `../BRUTAL/<Assembly>/` source.

### Step 3: build against NEW (the alarm)

Build in this order, because each error set is a different work list. Point the dist at scratch so a
build never overwrites the developer's mod folder:

```bash
export KSA_DLL_DIR=$KSA_REPO/current/dll CATCOM_DIST_DIR="$SCRATCH/dist/catcom"
dotnet build tests/Catcom.Ksa.CompileCheck -nologo -v quiet   # Catcom.Ksa errors, then the patch-target stubs
dotnet build src/Catcom.PurrTty -nologo -v quiet              # pulls in Catcom.GameMod
dotnet build catcom.slnx -nologo -v quiet                     # everything (Catcom.Dist needs tools/fetch-all.sh run once)
dotnet test  catcom.slnx --nologo -v quiet                    # game-free suites; must print nothing
```

- Errors in **`Catcom.Ksa`** are at `[KsaAnchor]` sites. The anchor's `Notes` usually record what
  this member did in earlier builds, which helps you choose the fix.
- Errors in **`HookStubs.cs`** mean a patch target was renamed or re-signed, or one of its parameter
  names changed (Harmony injects arguments **by name**, so the real patch is broken too).
- The build stops at the first failing project, so repeat until it is clean.
- The tests do not touch KSA at all. A pass only proves you did not break Core or the runtimes.

**A green build clears only the compile-visible rows of §1.** Steps 4 to 6 are the real review.

### Step 4: find which bound members changed, cheaply

The decomp diff between builds is large (the 5554→5638 diff touched 762 files in `KSA/`). Don't read
it top to bottom. Intersect it with what catcom actually binds:

```bash
git -C $KSA_REPO diff --name-only $BASE $NEW -- current/decomp/KSA | sed 's#current/decomp/##' > "$SCRATCH/changed.txt"
git grep -ohE 'SourceFile = "[^"]*"' -- src | grep -oE 'KSA/[A-Za-z0-9_.]+\.cs' | sort -u \
  | grep -xFf "$SCRATCH/changed.txt"                     # anchored source files that changed
```

For each hit, diff **only the bound members**. Take their names from the anchor strings
(`git grep -n 'KsaAnchor("' -- src`):

```bash
git -C $KSA_REPO diff -w $BASE $NEW -- current/decomp/KSA/DockingPort.cs | grep -nE 'Dock\(|Undock\(|Docked|PushoffImpulse'
```

For **exact signatures** (visibility, parameter names and types, static or instance), the compiled
DLL beats the decomp. `spikes/KsaProbe` reads both builds through MetadataLoadContext:

```bash
q=(GameSettings::OnKeyAll Universe::ExecuteNextVehicleSolvers DockingPort::Undock Program::DrawProgramMenusHook)
probe() { dotnet run --project spikes/KsaProbe -v quiet -- "$1" "${q[@]}" | tail -n +2 | sed 's/Version=[^,]*,//'; }
diff <(probe "$SCRATCH/ksa-base/current/dll") <(probe $KSA_REPO/current/dll)   # empty = signatures unchanged
```

KsaProbe queries types in `KSA.dll` only (`Type`, `Type::Member`, `grep:<substr>`, `events`). For
Brutal types, diff `current/decomp/Brutal.*` or read `../BRUTAL`.

Work in priority order: **Harmony targets (§3) → reflection (§4) → `ChurnRisk.High` anchors →
`Medium` → `Low`**. Also re-check any anchor whose `GameVersion` is older than BASE. Those were ported
from gatOS and never re-verified in catcom.

### Step 5: semantic review of changed members

For each changed member, decide one of three outcomes: **no-op**, **mechanical fix**, or **behavioral
change**. Questions to ask, catcom-specific:

- **Slot timing.** Does `Program.PrepareFrame` still call `JobSystems.VehicleSolver.Wait()` →
  `Universe.ApplyVehicleSolvers()` once per frame (also while paused)? Is
  `ExecuteNextVehicleSolvers` still the point where solvers sit idle with inputs applied? Does
  `FlightComputer.CopyFrom` still clobber flight-computer writes made outside the Solver slot? If
  this ordering moves, every script tick and every direct write is affected.
- **Event semantics.** Does the patched method still fire exactly when the event name says, once,
  with the same `__result` or `out` meaning? Examples: `Decoupler.Decouple` returns the separated
  vehicle, `DockingPort.Undock(out PoseChange)`, `SequenceList.ActiveSequence` after staging, and
  `UncompressedSave.Load` paired with `Program.OnGameLoaded`. Also check for new callers: a new path
  that bypasses the patched method darkens the event without any error.
- **Units and ranges.** Power has changed J↔W before (rev 4681), and masses, throttle (0..1) or
  angles (deg/rad) can change the same way. Diff the Content XML for the attributes behind module
  fields too (`git -C $KSA_REPO diff $BASE $NEW --stat -- current/Content`).
- **Frames and numerics.** CCI/CCE/ECL/ego conventions, `Brutal.Numerics` `double3`/`doubleQuat`
  operator semantics, and row-vector order. A convention change silently corrupts orbit, position
  and attitude readouts and the teleport/impulse actuators.
- **Gating.** Watch for new preconditions on a read or write. Examples:
  `Vehicle.IsControllable => … Parts.Controls.NumModules > 0` (rev 4699), and the
  `Vehicle.IsDebris` class split (5402).
- **Shape changes.** Single-instance modules becoming multi-instance (`Decoupler`, 5348), templates
  that are now cloned or shared (`LightActuator` clones the template per instance), and
  constructors that dropped side effects (`ThrusterController`, 5348).

**Decomp can lag the binary.** If a member looks fine in the decomp but behaves differently, trust
KsaProbe and the DLL.

### Step 6: StarMap, Harmony, purrTTY

These are not KSA, but check them on every game upgrade, since they often move at the same time:

- `StarMap.API` 0.3.6 and `Lib.Harmony` 2.4.2 are **compile-only** (StarMap supplies them at
  runtime; see `Catcom.GameMod.csproj`). If the user's StarMap is newer, bump both in GameMod, Ksa and
  CompileCheck together. The `Mod` skeleton in `HookStubs.cs` checks the lifecycle attribute
  signatures (`[StarMapImmediateLoad]`, `AllModsLoaded`, `BeforeGui`, `AfterGui`, `AfterOnFrame`,
  `Unload`).
- `vendor/purrTTY/*.dll` are the pinned contract assemblies. A purrTTY upgrade for the same game build
  is purrtty's own job (it has its own `upgrade-ksa`). Re-vendor only if its contract changed (see
  `vendor/purrTTY/README.md`).

## 3. Harmony patch catalog

Every patch body is wrapped in try/catch and logs once. Event patches publish canonical names from
`Catcom.Core/Hooks/EventCatalog.cs`. "Stub" means `tests/Catcom.Ksa.CompileCheck/HookStubs.cs`
mirrors the patch.

| Patch class (file) | Target | Stub | Event / role |
|---|---|---|---|
| `StepSlotPatch` (`Slots/SlotHost.cs`) | `Universe.ApplyVehicleSolvers()` static postfix | ✓ | **Step slot.** Drains queues, runs the differ, ticks the scheduler. The most critical patch. |
| `SolverSlotPatch` (`Slots/SlotHost.cs`) | `Universe.ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)` static prefix, `Priority.First` | ✓ | **Solver slot.** The only sim-safe direct-write window. |
| `EngineActivatePatch` (`Events/ControlPatches.cs`) | `EngineController.SetIsActive(Vehicle?, bool activationState)` postfix | ✓ | `engine.activate` / `engine.deactivate` |
| `RcsActivatePatch` | `ThrusterController.SetIsActive(Vehicle?, bool)` postfix | ✓ | `rcs.activate` |
| `StagePatch` | `SequenceList.ActivateNextSequence(Vehicle)` postfix; reads `ActiveSequence` | ✓ | `stage` |
| `DecouplePatch` | `Decoupler.Decouple(Vehicle oldVehicle) → Vehicle?` postfix (`__result`) | ✓ | `decouple` |
| `SplitPatch` (`Events/VesselPatches.cs`) | `Vehicle.Split(Part.Connection, Part.Connection.IConnector, …)` | ✓ | `split` |
| `DockPatch` / `UndockPatch` | `DockingPort.Dock(Vehicle, Vehicle, DockingPort, …)` / `DockingPort.Undock(Vehicle, out PoseChange) → Vehicle?` | ✓ | `dock` / `undock` |
| `VesselSpawnPatch` / `VesselRemovePatch` | `CelestialSystem.Register(Astronomical)` / `Deregister(Astronomical)` postfix | ✓ | `vessel.spawn` / `vessel.remove` |
| `VesselLaunchPatch` | `Vehicle.MarkLaunched()` postfix; `HasLaunched` | ✓ | `vessel.launch` |
| `ImpactGroundPatch` / `ImpactWaterPatch` | `Celestial.TrySpawnGroundImpact(GroundImpactEvent)` / `TrySpawnWaterSplash(WaterSplashEvent)` prefix | ✓ | `impact` |
| `DestroyFromEventPatch` / `DestroyRemovedPatch` | `Universe.DestroyVehicleFromEvent(Vehicle, VehicleDestructionEvent)` / `Universe.DestroyVehicle(Vehicle, CrewDisposition = EndMission)` static prefix | ✓ | `vessel.destroy` (cause / removed) |
| `PartFailurePatch` | `PartFailureEvent.Apply(Vehicle)` prefix; `FailedParts`, `DestroyWholeVehicle` | ✓ | `part.failure` |
| `ControlChangePatch` (`Events/SessionPatches.cs`) | `Program.ControlledVehicle` **setter** static postfix | ✓ | `control.change` |
| `EditorEnterPatch` / `EditorExitPatch` | `VehicleEditor..ctor(Vehicle?, bool)` postfix / `VehicleEditor.Dispose()` prefix | ✓ | `editor.enter` / `editor.exit` |
| `SaveWritePatch` | `UncompressedSave.Write() → bool` postfix; `Directory` | ✓ | `save.write` |
| `SaveLoadDirectoryPatch` + `SaveLoadPatch` | `UncompressedSave.Load()` prefix and finalizer, then `Program.OnGameLoaded()` static postfix | ✓ | `save.load` (directory captured, then fired) |
| `SystemLoadPatch` | `Universe.LoadSystem(string id)` static postfix | ✓ | `system.load` |
| `WarpChangePatch` | `Universe.SetSimulationSpeed(double, bool alert = true)` static prefix | ✓ | `warp.change` |
| `BurnAlertsPatch` | `FlightComputer.RaisePendingAlerts(Vehicle)` prefix | ✓ | `burn.complete` / `burn.fuel_out` |
| `HotkeyGuardPatch` (`GameMod/UI/UiPatches.cs`) | `GameSettings.OnKeyAll(GlfwKeyEvent)` prefix, `ref bool __result` | **✗** | **Required.** Blocks game hotkeys while a Script Manager text field has focus. Relies on `OnKeyAll` heading the input chain (`GameSettings.OnKeyAll → Popup.OnKeyAll → ConsoleWindow.OnKey → … → Program.OnKey`) and on `Program.ConsoleWindow.IsOpen`. |
| `MenuFallbackPatch` (`GameMod/UI/UiPatches.cs`) | `Program.DrawProgramMenusHook()` postfix; `((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true)` | **✗** | Optional. Draws the "Scripting" menu when ModMenu is absent. |
| `VesselForceRender` (`Render/VesselForceRender.cs`), patched manually through `AccessTools` | `Vehicle.GetWorldMatrix(Camera)` + `Vehicle.UpdateRenderData(IViewport, int)` prefixes (installed only while a vessel is marked `always_render`) | ✓ | Throws `MissingMethodException` when a vessel is marked. `UpdateRenderData` is virtual, so overrides such as `KittenEva` are not covered. |

The UI patches have no stubs. After every build, re-read `GameSettings.OnKeyAll`, the key-dispatch
order in `Program.cs`, and `DrawProgramMenusHook`/`DrawMenuBar` by hand. Adding stubs for them to
`HookStubs.cs` is a welcome refactor. CompileCheck does not reference GameMod, so each stub has to
name the KSA types directly; it may also need the `Brutal.Glfw` reference that GameMod has.

## 4. Reflection and other string-bound couplings (the compiler can't see these)

Grep each name in BASE **and** NEW to prove whether a miss is new:

```bash
grep -rn '_manualControlInputs\|EngineThrottle\|ThrusterCommandFlags' $KSA_REPO/current/decomp/KSA/Vehicle.cs | head
grep -n '_renderable\|_characterAvatar' $KSA_REPO/current/decomp/KSA/{KittenEva,KittenRenderable}.cs
```

| Name | Code | If it moves |
|---|---|---|
| `Vehicle._manualControlInputs` (private) `.EngineThrottle`, `.ThrusterCommandFlags` | `Actuators/{Throttle,Translate,Rotate}Actuator.cs` | Throttle, translate and rotate commands return faults. |
| `Vehicle._renderable` → `KittenRenderable._characterAvatar` → `.Core` → `Scale` (field **or** property) | `Actuators/ScaleActuator.cs` | Kitten EVA scaling stops working. Part scaling is a typed binding and keeps working. |
| `LightModule` template: field-by-field `ShallowClone` over the whole hierarchy | `Actuators/LightActuator.cs` | A new field holding shared mutable state gets aliased between instances. Review any field added to `LightModule` or its bases. |
| `ModLibrary.Find(string)` (reached by reflection because the `Find(KeyHash)` overload lives in unreferenced `Planet.Render.Core`) | `GameMod/KsaMods.cs` | It falls back to an assembly-name scan, which is mostly harmless. |
| `Program.TerminalInterface.RegisterCommand` / `UnregisterObject` / `Execute`, `[TerminalAction(…, ArgParseMode.Unparsed)]` | `GameMod/Commands/ScriptCommands.cs`, `Bridge/KsaBridge.cs` | The `script` console verb or `ksa.console(...)` disappears. |
| `ConsoleWindow.Print(ReadOnlySpan<char>, ImColor8)`, `ConsoleWindow.{Error,Warning,Debug}Color` | `GameMod/ModLogSink.cs` | In-game log output is lost (stdout still works). |
| `KSA.VersionInfo.Current.VersionString`, `Constants.DocumentsFolderPath`, `FontManager.ConsoleFont` | Settings About tab, `ModPaths.cs`, `ImGuiWidgets.cs` | The version check shows "unknown", the mod data path is wrong, or the font falls back. |

## 5. Applying the changes (do it in this order)

1. **csproj references** (Step 2). Make one mechanical change across all four project files.
2. **Compile fixes in `Catcom.Ksa`**, then in `Catcom.GameMod`. Keep every KSA call behind its
   existing `[KsaAnchor]` member, and keep KSA, Brutal, Harmony and ImGui types out of
   `Catcom.Core` and the runtimes (`docs/IMPLEMENTATION_GUIDE.md` §2). If a KSA restructure tempts you
   to spread game types outward, add or reshape a reader/actuator method instead.
3. **Patch targets.** Change the real patch **and** its `HookStubs.cs` stub in the same change. The
   stub must keep the same target attributes, the same Prefix/Postfix parameter list, and a `Sig`
   that calls the target with named arguments. For a new event hook, add a row to the patch class's
   `PatchTable`, the payload in `EventCatalog`, a stub, and an item in `docs/VALIDATION.md` §6.
4. **Re-stamp anchors, but only the ones you re-verified.** Set `Verified = "<today>"` and
   `GameVersion = "<NEW build>"`, fix `SourceFile` and line hints, and **append** to `Notes` as
   `"<build>: <what changed, or 'byte-identical'>"`. Keep the history already there; the existing
   notes show the style. Leave unreviewed anchors on their old stamp: a stale stamp is an honest
   signal that nobody checked them.
5. **Snapshot or API changes.** If a reader change adds, renames or re-units a field on a
   `*Snapshot` record, the script API stubs regenerate by reflection
   (`Catcom.Core/Api/ReflectedClasses.cs`) and the golden tests fail. Update the curated docs in
   `ReflectedDocs.cs` (and the preludes `Api/Preludes/ksa.{js,lua}` if they name the field), then run
   `KSA_UPDATE_GOLDEN=1 dotnet test tests/Catcom.Core.Tests --filter FullyQualifiedName~Golden` and
   review the diff of `tests/Catcom.Core.Tests/Golden/`. A unit change is a script-visible breaking
   change, so call it out.
6. **Version stamps.** Update `VerifiedKsaVersion` in `src/Catcom.GameMod/UI/ScriptManagerWindow.Settings.cs`
   (it drives the in-game "matches / differs" line), the README's "Built against KSA …" line, the
   `docs/VALIDATION.md` intro, and the `docs/IMPLEMENTATION_GUIDE.md` §0 table and §2 anchor example.
   Do **not** rewrite `docs/research/**` or `docs/progress/**`, which are dated records.
7. **Verify.** Run the four Step 3 commands with zero warnings, then deploy to scratch and check the
   dist:
   `dotnet build src/Catcom.Dist -c Release` with `CATCOM_DIST_DIR="$SCRATCH/dist/catcom"`, then
   `python3 tools/check-dist.py "$SCRATCH/dist/catcom"`. For the in-game pass, which only the user can
   run on Windows or Linux, point them at `docs/VALIDATION.md` §1 (load) and §6 (events), plus these
   checks: Runtimes tab shows **2/2 slot patches and 24/24 event patches installed** with no degraded
   accessors, Settings → About says **matches**, and the log has no `UI patch … failed to apply`.
8. **Commits** (only when the user asks). Use conventional commits and keep the mechanical change
   apart from the behavioral ones: `build(ksa): reference the renamed Brutal assemblies (KSA <build>)`,
   then `fix(ksa): …` per behavioral change, then `docs: verified against KSA <build>`. Siblings use
   `upgrade(ksa): …` too. End each message with the attribution trailers.
9. **Clean up** the BASE worktree (`git -C $KSA_REPO worktree remove --force "$SCRATCH/ksa-base"`).

## 6. Deliverable

If the user asked only for a review, don't edit. Report each touchpoint that changed:

```
### <touchpoint> — <CRITICAL|HIGH|MEDIUM|LOW>
- KSA delta: <BASE → NEW, decomp file:line or version.json rev>
- catcom: <file:symbol>   coupling: <assembly-ref | compile | harmony | reflection | semantic | lifecycle>
- Impact: <what breaks and who notices>
- Change: <none | mechanical: … | behavioral: …>
```

- **Critical:** a Step or Solver slot patch, `HotkeyGuardPatch`, or the input chain. These break
  every script or leak keys for every user.
- **High:** an event-hook target or meaning, a frame or unit change in a reader, or an actuator
  write path.
- **Medium:** one event, one reflection actuator, `VesselForceRender`, or `MenuFallbackPatch`.
- **Low:** a mechanical reference rename, or anything already null-tolerant.

End with a summary table and a verdict: *no changes*, *mechanical only*, or *behavioral work
required*. State plainly which checks you could not run, such as the in-game pass or a missing BASE
build.

## 7. Keeping this skill true

When catcom's coupling changes (a new patch, a new reflection read, a new anchored binding, or a
renamed DLL), update §3 or §4 in the same change. To re-derive the surface:

```bash
git grep -n 'HarmonyPatch\|AccessTools\.' -- src
git grep -nE 'GetField\("|GetProperty\("|BindingFlags' -- src/Catcom.Ksa src/Catcom.GameMod
git grep -c 'KsaAnchor("' -- src
grep -n 'HintPath>$(KSAFolder)' src/*/*.csproj tests/*/*.csproj spikes/*/*.csproj
```

Add each finished upgrade's notable lesson (like the Brutal rename table) to the section it belongs
in, so the next upgrade starts from it.
