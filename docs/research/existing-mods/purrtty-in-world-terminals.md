---
type: Component Analysis
title: purrTTY in-world terminals and 2D windows (creation, input, rendering, canvas)
description: Verified facts about placing a purrTTY terminal on a vehicle part or as a billboard - the InWorldTerminalRecord, the main-thread creation API, custom-shell support, focus and mouse-to-cell mapping, texture/quad sizing and per-frame cost, persistence - plus the 2D window facts (fixed grids, lock mode, titles, exit behaviour) and the fonts/glyphs available for drawing panels.
resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.GameMod/InWorld
tags: [purrtty, in-world, quad, mouse, raycast, rendering, fonts, 2d-window]
sources:
  - id: report
    resource: /references/purrtty-virtual-pty-report-2026-10-16.md
    title: purrTTY virtual-pty research report (2026-10-16)
  - id: record
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.GameMod/InWorld/Settings/InWorldTerminalRecord.cs
    title: InWorldTerminalRecord.cs
  - id: manager
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs
    title: InWorldTerminalManager.cs
  - id: quad
    resource: /Users/asherwin/repos/meow-sci/purrtty/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs
    title: InWorldQuad.cs
generated: { by: claude-fable-5.1/ksa-scripting-research, at: 2026-10-17T00:00:00Z }
status: stable
---

# The record

```csharp
public sealed class InWorldTerminalRecord
{
    public string Name = "In-World";  public int Cols = 100;  public int Rows = 30;     // fixed grid → texture extent
    public ProcessLaunchOptions? Launch;      // null = configured default shell; CreateCustomGame("<id>") runs a registered ICustomShell
    public string? ThemeName;                 // colours + font family/size; opacity lives in the theme / SetOpacities
    public string Mode = "part";              // "part" | "billboard"
    public string TargetVehicleId = "";       // "" = controlled vehicle; compared with Vehicle.Id
    public string TargetPartId = "";          // compared with Part.Id (instance name or TEMPLATE id → collisions pick the first match)
    public string TargetSubPartId = "";
    public float PartOffsetX, PartOffsetY, PartOffsetZ = 2f;   // metres, part-local frame; +Z faces the viewer
    public float PartRotationX, PartRotationY, PartRotationZ;  // degrees, X·Y·Z order
    public float PartWidthMeters = 2f, PartHeightMeters = 2f;
    public float BillboardDistance = 5f, BillboardOffsetX, BillboardOffsetY, BillboardWidthMeters = 3f, BillboardHeightMeters = 2f;
    public float BillboardRotationX, BillboardRotationY, BillboardRotationZ;
    public bool BillboardAlwaysOnTop = true, BillboardClickToFocus;
}
```

`Part.InstanceId` is **not** used by purrTTY today; addressing a part by instance id needs a new `uint? TargetPartInstanceId` field and a ~15-line branch in `InWorldQuad.ResolveTopLevelPart/ResolvePart`. Once resolved, the `Part` object is followed by reference across decouple/dock.[^record][^quad][^report]

# Creation API (main thread, inside an ImGui frame)

```csharp
public sealed class InWorldTerminalManager
{
    public static InWorldTerminalManager? Instance { get; }  public static bool IsInputFocused { get; }
    public InWorldTerminalInstance? Create(InWorldTerminalRecord record);   // MAIN THREAD with an ImGui frame active (CalcTextSize, Vulkan, registry); may suffix Name
    public void Remove(InWorldTerminalInstance instance);                  // GPU free deferred 2 frames
    public void Focus(InWorldTerminalInstance? instance);
    public InWorldTerminalInstance? Recreate(InWorldTerminalInstance old, InWorldTerminalRecord newRecord);  // needed to change the shell
}
public sealed class InWorldTerminalInstance : INamedTerminal
{
    public InWorldTerminalRecord Record { get; }   // placement fields are read LIVE every frame: mutate on the main thread to move the quad
    public bool TrySetGridSize(int cols, int rows);  // clamps 8..400 × 4..200; deferred 3 frames; shell gets NotifyTerminalResize
    public void SetOpacities(float background, float foreground, float cellBackground);
    public void ApplyTheme(ThemeDefinition theme);  public bool TryRename(string newName);
    public bool TryRaycast(Ray ray, out double t, out float2 uv);
}
```

`LayoutManager.Apply` is the existing example of programmatic creation and is pumped from `TerminalMod.OnAfterUi` for exactly this reason. **A registered custom shell runs in-world with zero changes** (`Launch = ProcessLaunchOptions.CreateCustomGame(id)`); the in-world manager UI already lists registered shells, and layouts persist `custom_shell_id`.[^manager][^report]

# Focus, keyboard, mouse

- Focus: on left click, `Cursor.GetEgoRay(Program.MainViewport)` is ray-tested against every pickable instance (Möller–Trumbore on the quad); nearest hit focuses; a click on nothing (and not on ImGui) clears focus; Escape does not release; 2D terminal focus and in-world focus are mutually exclusive. Billboards are pickable only with `BillboardClickToFocus`.
- Keyboard: while focused, the same `TerminalInputEncoder.ProcessKeyboard` as 2D; game keys are suppressed by purrTTY's Harmony patches (`Program.OnKey`, `GameSettings.OnKeyAll`, mouse-binding gate). The purrTTY toggle key is skipped while an in-world terminal is focused, so F12 reaches the script there.
- Mouse: each frame the raycast UV (0→1 left→right, top→bottom) maps to `col = clamp((int)(u·texW / cellW), 0, cols-1)` and the engine receives the cell centre; left/middle/right press+release, drag motion on cell change, wheel per notch — **only when the shell enabled mouse tracking**; otherwise a click only focuses. No text selection, no Shift bypass, no context menu on quads. **The click that focuses the quad is also forwarded as a press in the same frame**, so panel scripts should act on release.[^report]

# Rendering and cost

- Texture `clamp(ceil(Cols·cellW), 256, 4096) × clamp(ceil(Rows·cellH), 256, 4096)`, `R8G8B8A8UNorm`, own offscreen target, own secondary ImGui context sharing the font atlas, linear clamped sampler without mipmaps (a small quad viewed from far away shimmers; prefer fewer, larger cells).
- Quad: unit quad scaled by `(PartWidthMeters, PartHeightMeters)`, rotated, translated by the part-local offset (part scale excluded), depth-tested and depth-written. **Flat quad only**: no mesh-conforming or socket attachment; multiple quads per vehicle are fine.
- Draw injected by a postfix on `SuperMeshRenderSystem.RenderTranslucencyPass`; one `DrawIndexed` per quad.
- Per frame per instance: inbox drain + frame build always (~0.1–0.5 ms for a small quiet grid, dirty-tracked); the ImGui build + offscreen pass + queue submit only when the frame changed, a kitty image arrived, focus changed, or 1 s elapsed. A static panel costs ~1 render/s.
- Teardown deferred 2 frames with `Device.WaitIdle()`.[^report]

# Persistence

In-world terminals are session-only; a user-saved layout (`<Documents>/My Games/Kitten Space Agency/.purrTTY/layouts/<name>.toml`) captures record fields and the shell spec; apply is user-initiated only. A sibling mod must keep its own placement records and re-create terminals through the launcher.[^report]

# 2D windows

- **Fixed cols×rows is not possible** (`TrySetGridSize` returns false): the grid derives from the window's pixel size; compute pixels from cols×rows and the theme cell size, call `RequestPlacement`, and (new, one line) set `NoResize`.
- The tab bar is hidden with a single session; the title strip shows the session title (settable by the script via OSC 2); registry `Name` is separate.
- `LockMode` makes an unfocused window click-through with a hot-zone refocus affordance; `BorderOnFocus/Hover` are per-window settings.
- `CreateConfiguredWindow(WindowLayoutRecord, ThemeDefinition?)` is the right factory for a launcher; set `controller.IsVisible = true` **after** the window exists or a default shell window spawns.
- Shell exit shows nothing on a single-session window; the window stays until closed.[^report]

# Fonts and canvas

Bundled Nerd Fonts (Hack default, JetBrains Mono, Space Mono, ProFont, Proggy Clean, ShureTech Mono) load on demand under ImGui 1.92's dynamic atlas, so box drawing U+2500–257F, geometric shapes, arrows, Powerline and the Nerd Font PUA icons all render; Block Elements U+2580–259F and Braille U+2800–28FF are drawn as geometry regardless of font; no CJK or colour emoji. Cell size is `CalcTextSize("M")` at the theme `FontSize` (default 32 px; range 4–72): at 32 px a 40×12 panel is ≈ 772×456 px (aspect 1.69, e.g. a 0.6 m × 0.355 m quad); at 16 px the 256 px floor makes the shell see 40×13.[^report]

[^report]: purrTTY virtual-pty research report (2026-10-16)
[^record]: InWorldTerminalRecord.cs
[^manager]: InWorldTerminalManager.cs
[^quad]: InWorldQuad.cs
