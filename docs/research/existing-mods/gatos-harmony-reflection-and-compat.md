---
type: Reference
title: gatOS Harmony patches, reflection anchors, and KSA-compatibility discipline
description: The complete list of Harmony patches gatOS applies, the private KSA members it reaches by reflection, the KsaAnchor / KsaHealth / upgrade-ksa patterns that keep it alive across game builds, and the finding that no prior in-process scripting design exists in gatOS.
resource: /Users/asherwin/repos/meow-sci/gatOS/scope/ksa-runtime-coupling.md
tags: [gatos, harmony, reflection, compatibility, ksa-anchor, upgrade-ksa]
sources:
  - id: report
    resource: /references/gatos-internals-report-2026-10-16.md
    title: gatOS internals research report (2026-10-16)
  - id: upgrade
    resource: /Users/asherwin/repos/meow-sci/gatOS/.claude/skills/upgrade-ksa/SKILL.md
    title: upgrade-ksa skill
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-16T21:00:00Z }
status: stable
---

# Harmony patches (complete)

| Harmony id | Target | Kind | Purpose |
|---|---|---|---|
| StarMap | `Program.OnDrawUiFrame` / `OnDrawUiViewports` / `OnFrame` | prefix / postfix / postfix | the lifecycle hooks |
| `gatos.solver` | `Universe.ExecuteNextVehicleSolvers(double, SimStep)` | prefix, `Priority.First` | Solver-phase command drain |
| `gatos.menu` | `Program.DrawProgramMenusHook()` | postfix | fallback menu entry when ModMenu is absent |
| `gatos.display` | `Program.RenderGame` (private) | **transpiler** (inserts a call before the final command-buffer `End`) | in-band screen capture |
| `gatos.display` | `GameSettings.UiPixelCulling()` | prefix (`ref bool __result`) | suppress UI-coverage depth stamp |
| `gatos.camera` | `GameViewport.OnFrame(double)` | prefix + postfix gated on `Program.MainViewport` | same-frame sample/schedule/drain + camera |
| `gatos.iva` | `PartModel..ctor(PartModelModule.Template)` | postfix, dynamic | force-render IVA |
| `gatos.always_render` | `Vehicle.GetWorldMatrix(Camera)`, `Vehicle.UpdateRenderData(IViewport,int)` | prefixes, dynamic | render-distance override |
| `gatos.thug_life` | `SuperMeshRenderSystem.RenderMainPass(IViewport, CommandBuffer)` | postfix, dynamic | textured quad draws |
| `gatos.stickers` | `KSA.Rendering.RenderTarget.ResolveAttachments(CommandBuffer, bool)` | postfix, dynamic | decal pass |
| PaintManager | `ShaderModuleUtils.FromFile(...)`, `PartTreeRenderData.WriteState/WriteDynamicState` (private) | prefix / postfixes, dynamic | paint injection |

Conventions: one `Harmony` instance per feature with a string id; every patch body is try/catch with log-once and self-disable; dynamic patches install on first use and `UnpatchAll(id)` on last use; targets resolved via `AccessTools` so a rename fails at install time (feature goes dark) rather than at compile time.[^report]

# Reflection / private access

| KSA member | How reached | Used by |
|---|---|---|
| `Vehicle._manualControlInputs` → `.EngineThrottle`, `.ThrusterCommandFlags` | `GetField(..., Instance|NonPublic)`, box, set, write back | throttle / translate / rotate |
| `LightModule.Template` + `Intensity/ColorRgb/OuterAngle/InnerAngle` | `RuntimeHelpers.GetUninitializedObject` + per-field copy | light actuator unshare |
| `KittenEva._renderable` → `_characterAvatar` → `Core.Scale` | field chain + struct write-back | scale, EVA paint |
| `GameViewport.FixedController`, `ViewportBase.Mode` (protected setters) | `GetProperty(...).GetSetMethod(true).Invoke` | camera seam |
| `Program.Instance._volumetricTrailRenderer`, `_planetTransparenciesRenderer`, `VolumetricTrailRenderer._plumeTrailSegmentsManager._settings`, `CloudRenderer._renderer/...`, `PlanetRenderer._renderUboMap/_meshUboMap`, `VolumetricExhaustTemplate.References` | private fields | FX editors |
| `PartTreeRenderData` nested `Batch`/`DynamicBatch` + `StateBitFlags` | `GetNestedType`, `AccessTools.FieldRefAccess` | paint |
| `Program.RenderGame`, `PartModel..ctor(...)` | `AccessTools.Method/Constructor` | display, IVA |

A scripting bridge that wants throttle/translate/rotate must copy the first row; everything else gatOS touches is public (179-anchor census in the report).[^report]

# Compatibility discipline to copy

- **`[KsaAnchor(string member) { SourceFile, Verified, GameVersion, Risk, Notes }`** on every method touching a KSA type, so "the churn playbook is a grep".
- **`KsaHealth`**: `IsDegraded(accessor)`, `Fault(accessor, ut, error)` (latch + log once), `Clear(accessor)`, `Snapshot()` → `/sim/status/accessors`. A miss degrades to `EOPNOTSUPP`, never a crash.
- Graceful-degradation ladder: per-vehicle try/catch in the sampler; executor never throws; session kill-switches per feature; null-tolerant reflection accessors returning a reason; best-effort hook install; `Sanitize.Finite` on every published double.
- Build scaffolding: `Game/**/*.cs` compile-removed when `KSA.dll` is absent; `partial` mod class split on the game-assembly boundary with `[MethodImpl(NoInlining)]` seams; game-typed statics in a nested class so the mod type-loads without Brutal.
- The **upgrade-ksa playbook**: diff `version.json` changelogs, build against the new DLLs as the alarm, decomp-diff for silent semantic drift, re-verify reflection accessors and Harmony targets by hand, bump anchors.[^upgrade]

# Prior art on in-process scripting

A search of gatOS plans, ideas and notes found **no prior design for in-process Lua/JS/Roslyn scripting**. The nearest analogues are out-of-process: the TypeScript-on-Bun SDK, the game-free timed scheduler (`/sim/ctl/timed_batch`), the camera-track JSON DSL, and the MCP server. The founding note (`KSA_GAME_INTEGRATION_IDEA.md`) stresses that "KSA is in constant flux … special care should be taken to abstract our interface for both reading KSA data and changing KSA game state … documented in detail for each data point", which is the origin of `[KsaAnchor]`/`KsaCatalog` and applies verbatim to a scripting bridge.[^report]

[^report]: gatOS internals research report (2026-10-16)
[^upgrade]: upgrade-ksa skill
