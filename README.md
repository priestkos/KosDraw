# KosDraw

A small world-overlay library for the PriestKos Dalamud plugins: circles, rings, cones, fans, arcs, lines, arrows,
polygons, pillars of light, dots and labels drawn **in the game world**, on the ground round the player, a target or any map point.

`source-included` · Dalamud API 15 · .NET 10 · AGPL-3.0

> **No support is offered.** KosDraw exists for the PriestKos plugin family; it is public so the plugins' release builds can check it out.

## What it does

- **`KosOverlay.Draw(s => ...)`**: called from `UiBuilder.Draw`, it collects the frame's shapes and draws them behind
  every ImGui window. Shapes are in world coordinates; angles follow the game's object rotation (0 faces south/+Z,
  π/2 faces east/+X), so `player.Rotation` can be passed straight in.
- **Shapes** (`KosShapes`): `Circle`, `Ring`, `Cone`, `Fan` (ring sector), `Arc`, `Line`, `Arrow`, `Polygon`, `Pillar`,
  `Dot`, `Text`. `thickness` 0 fills a shape; above 0 outlines it that many pixels wide. Round shapes are cut into 5° steps.
  `Pillar` is a see-through upright tube (24 sides) that fades out towards its top: a beacon over a spot.
- **Two ways to draw**, chosen per overlay:
  - **ImGui path** (default): shapes are projected and drawn on ImGui's background draw list. No GPU code. Shapes show
    through walls and terrain.
  - **GPU path** (`overlay.UseGpu = true`, off by default): shapes are drawn with Direct3D 11 into KosDraw's own
    texture, each pixel tested against the game's depth buffer so scenery in front hides them (or fades them, with
    `OccludedAlpha`), kept off the game's own UI (`MaskGameUi`), and shown as one image.
- **Fault breakers**: each part of the GPU path (fills, strokes, depth, UI mask, composite) that fails switches off on
  its own, is logged once, and its shapes go back to the ImGui path. `overlay.GpuFeatures` and `overlay.LastFault`
  feed a status line.

### What it is not meant to do

- Hook or call game functions. KosDraw uses no signatures and no hooks, and never draws through the game's own VFX
  or omen system. It only **reads** game memory: the camera's view-projection matrix (`Control`), the scene depth
  texture and the back buffer (`RenderTargetManager`), each re-read every frame.
- Write to anything the game owns. The depth texture and back buffer are read through KosDraw's own views and copies.
- Register commands or settings. The host plugin owns its UI and decides when the GPU path is offered.

## How it works

Each frame `KosOverlay` reads `Control.Instance()->ViewProjectionMatrix`. The game's projection is **infinite
reverse-Z**: clip-space z is the constant near distance, so depth = near / distance and the near distance is the
matrix's `M43`.

- **ImGui path**: every triangle and line is clipped against the near plane in world space before projection (a point
  behind the camera would otherwise flip across the screen), then drawn with anti-aliased fill turned off so the
  triangles of one shape don't show seams.
- **GPU path**: the device comes from Dalamud (`IUiBuilder.DeviceHandle`), and objects are created lazily on the first
  GPU frame. Shapes are recorded on a private **deferred context**: fills as triangles, lines as quads widened in
  screen space in the vertex shader. Each pixel turns both its own depth and the scene's (a typed view of the game's
  typeless depth texture, scaled for dynamic resolution) back into metres and compares them with a tolerance. A
  composite pass masks out game UI (where the back buffer's alpha marks it) and turns premultiplied alpha into the
  straight alpha ImGui's image blend expects. The recorded list runs on the immediate context with its state restored,
  so neither the game's nor ImGui's state changes. Shaders are compiled once from HLSL with the system's
  `d3dcompiler_47`.

## Using it in a plugin

```bash
git submodule add https://github.com/priestkos/KosDraw.git KosDraw
```

In the plugin csproj:

```xml
<ItemGroup>
  <Compile Remove="KosDraw/**" />
  <Compile Include="KosDraw/src/**/*.cs" />
  <!-- Dalamud ships TerraFX; the SDK doesn't reference it. Private=false keeps Dalamud's copy the one loaded. -->
  <Reference Include="TerraFX.Interop.Windows" Private="false" />
</ItemGroup>
```

```csharp
overlay = new KosOverlay(pluginInterface.UiBuilder, log);   // constructor
overlay.UseGpu = config.DepthAwareOverlay;                   // a setting, off by default

private void OnDraw()                                        // UiBuilder.Draw
{
    if (objects.LocalPlayer is { } me)
        overlay.Draw(s => s.Cone(me.Position, 8, me.Rotation, MathF.PI / 6, new Vector4(0.9f, 0.2f, 0.3f, 0.4f)));
}

overlay.Dispose();                                           // Dispose
```

Every `actions/checkout` that builds the plugin needs `with: submodules: true`. Every type is `internal`.

## Demo

`demo/KosDrawDemo` is a dev-only plugin (never released) for the in-game check. Build it, add
`demo/KosDrawDemo/bin/Release/KosDrawDemo.dll` as a dev plugin location, enable it, and use `/kosdraw`. It draws
every shape round your character (and a cone on your target), with switches for fills or outlines, the GPU path, the
UI mask, the occluded alpha and depth tolerance, and one button per GPU feature to inject a fault.

## Tests

```bash
dotnet test tests/KosDraw.Tests/KosDraw.Tests.csproj -c Release
```

22 xUnit checks, run without the game:

- **Geometry** (`src/KosShapes.cs`): arc spacing and angle convention, filled circle and ring areas, closed outlines,
  cone span, arrow head, polygon fan, pillar shape and fade, colour packing, projection against a reverse-Z camera, and near-plane clipping
  of segments, triangles and polylines (including a cut loop re-joined through its first corner).
- **GPU path** (`src/KosGpu.cs`) on WARP, Windows' software Direct3D 11, with a fake depth buffer and back buffer:
  every shader compiles; a fill in front of the scene comes out in straight alpha; behind it, it is hidden or faded,
  and shows again with depth off; a 4 px line is 4 px wide; the UI mask clears shapes over UI; an injected fault in
  depth, fills or composite switches off only that part; targets follow the viewport size; and `Dispose` returns the
  device's reference count to where it started.

`src/KosOverlay.cs` (the Dalamud glue and the ImGui path) can't run outside the game; it is checked by building the demo.

## Limits

- Not verified in game yet: everything above ran offline only.
- Lines use square caps that overlap at corners rather than true joins; at large widths a corner shows the overlap.
- `Polygon` fills convex polygons only (outlines can be any shape).
- Shapes are drawn at the height given. On uneven ground, part of a flat shape can sit under the terrain; on the GPU
  path `Tolerance` (default 0.3 m) decides how much of that still counts as visible.
- The UI mask depends on the game marking its UI in the back buffer's alpha, as observed by other overlay renderers;
  it is skipped when the back buffer and the overlay differ in size.
- Dots and labels always draw on top (ImGui), never hidden by scenery.

## Layout

| Path | What |
|---|---|
| `src/KosShapes.cs` | Shapes, projection and near-plane clipping (pure) |
| `src/KosGpu.cs` | Direct3D 11 path: shaders, targets, depth and UI-mask views, fault breakers (no Dalamud types) |
| `src/KosOverlay.cs` | Dalamud glue: camera, depth and back buffer from the game, the ImGui path, GPU/ImGui routing |
| `demo/KosDrawDemo/` | Dev-only plugin for the in-game check |
| `tests/KosDraw.Tests/` | xUnit tests for `KosShapes.cs` and `KosGpu.cs` |
| `plan.md` | Design plan and milestones |

## Licence

AGPL-3.0, see [LICENSE](LICENSE). The approach was studied from two overlay renderers, one of them
[Pictomancy](https://github.com/sourpuh/ffxiv_pictomancy) (AGPL-3.0); the code here is KosDraw's own.
