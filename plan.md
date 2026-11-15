# KosDraw: plan

A small world-overlay drawing library for the PriestKos plugin family: rings, circles, cones, fans, lines, arrows,
polygons and labels drawn **in the game world** (on the ground round the player, round a target, at a map position),
source-included the same way as KosLib.

Status (2026-11-15): **M0, M1 and M3 built and tested offline; not yet seen in game.** M2 waits for a first adopter (none chosen). Decisions in §7.

Order of priorities, the same as KosLib: **correctness → safety → clarity → flair**. KosDraw is the riskiest code the
family would carry: it reads game memory for the camera and, in the GPU tier, issues Direct3D 11 calls inside the
game's process. The tiers below exist so the risky part comes last, behind a setting, and with a fallback.

---

## 1. How a world overlay works (the approach we take)

Studied from two renderers in the wild (one read for understanding only, never copied; the other, Pictomancy, is
public under AGPL-3.0). Both follow the same pipeline:

1. **Each frame, from `UiBuilder.Draw`:** the plugin opens a draw list (`using var dl = KosDraw.Begin();`), adds
   shapes in **world coordinates**, and the list is drawn when it is disposed.
2. **Camera:** the view-projection matrix comes from the game's camera control (`Control.Instance()->ViewProjectionMatrix`
   in FFXIVClientStructs, read-only), the viewport size from the graphics device. Read fresh every frame, never cached.
3. **Projection and clipping:** world points are projected to the screen. A shape that crosses the camera's near plane
   is clipped in 3-D before projection; otherwise a point behind the camera flips across the screen.
4. **Two ways to put pixels on screen:**
   - **ImGui path:** the projected, tessellated shapes go onto the draw list of a full-screen, transparent,
     input-less ImGui window drawn behind the plugin windows. No GPU code at all. Shapes are not hidden by
     walls, terrain or characters.
   - **GPU path:** a private D3D11 **deferred context** on the game's device records draw calls into the library's own
     render target, using small HLSL shaders for fills, fans and strokes. Each pixel is tested against the game's own
     **depth buffer**, so a ring behind a pillar is hidden by the pillar. A composite pass then produces one texture,
     which is shown as a single image in the same full-screen ImGui window. The recorded command list runs on the
     immediate context once per frame. An optional mask reads the back buffer's alpha so the overlay never covers
     the game's own UI.
5. **Fault breakers:** every GPU feature (fills, strokes, depth, composite, UI mask) is wrapped so that one failure
   disables that feature, logs once, and falls back (ultimately to the ImGui path), instead of throwing every frame.

## 2. Safety envelope (non-negotiable)

- **No hooks, no signatures, no calls into game functions.** That rules out drawing through the game's own VFX/omen
  system (Pictomancy's `VfxDraw/VfxFunctions.cs` signature-scans game functions such as `CreateVfx` and calls them
  through `Marshal.GetDelegateForFunctionPointer`, read 2026-11-15; KosDraw won't).
- **The device comes from Dalamud** (`IUiBuilder.DeviceHandle`, documented as the game's active `ID3D11Device`, or 0
  when not ready), not from game memory. GPU objects are created lazily inside the draw callback, never in the plugin
  constructor, because the device may not exist yet.
- **Game pointers** (camera, viewport, depth texture) are read inside the frame, null-checked, and re-read the next
  frame. Nothing native is cached across frames except the library's own COM objects, which hold their own references.
- **The game's depth texture is only ever read** (a shader resource view). KosDraw never writes to a game resource
  and never binds anything on the immediate context except through its own executed command list.
- **One thread:** drawing only from `UiBuilder.Draw`; a call from another thread is refused and logged once.
- **Resolution change, device loss, unload:** targets are recreated on size change; `Dispose` releases every COM
  reference in reverse order of creation; a hot reload leaves nothing behind.
- **Quiet when it should be:** nothing drawn during cutscenes, GPose, loading screens or while the UI is hidden
  (Dalamud conditions/`UiBuilder` flags), unless the host asks.
- **Off-game proof first:** every piece of maths (projection, near-plane clipping, arc/fan/cone tessellation, stroke
  expansion with joins and caps, dash patterns, gradients) is pure C# and unit-tested without the game.

## 3. Packaging

Source-included git submodule at `<Plugin>/KosDraw/`, like KosLib: csproj `<Compile Remove="KosDraw/**" />` plus
`<Compile Include="KosDraw/src/**/*.cs" />`, `submodules: true` on CI checkouts, all types `internal`, no global
commands. Dalamud ships `FFXIVClientStructs` (referenced by the SDK) and `TerraFX.Interop.Windows` 10.0.26100.5 (not
referenced by the SDK: consumers add `<Reference Include="TerraFX.Interop.Windows" Private="false" />`, found through
the SDK's `DalamudLibPath` search path), so no NuGet package ships. Shaders: HLSL embedded as a string and compiled
once at run time with `DirectX.D3DCompile` (present in Dalamud's TerraFX, imports the system `d3dcompiler_47`).

A dev-only demo plugin (`KosDrawDemo`, inside the KosDraw repo, never released) gives the in-game check a host: a
window with toggles that draws every primitive round the player.

## 4. Milestones

| # | Deliverable | Done when |
|---|---|---|
| M0 | Repo, licence, README, plan, CI running the tests | Decisions answered; empty `src/` builds |
| M1 | **ImGui path**: `Circle`, `Ring`, `Arc`, `Fan`, `Cone`, `Line`, `Arrow`, `Polygon`, `Dot`, `Text`, filled and outlined; near-plane clipping; xUnit tests for all geometry; `KosDrawDemo` | Tests green; demo builds 0 warnings; **stop for the user's in-game check** |
| M2 | First adopter uses it behind its own setting | Adopter Release 0 warnings, its tests green, in-game check |
| M3 | **GPU path**: deferred context, own target, fill/fan/stroke shaders, depth occlusion, composite, UI mask; fault breakers falling back to M1. Opt-in setting, off by default | Demo shows occlusion in game; disabling each feature by fault-injection falls back cleanly; unload/reload ×3 leaves no errors in `/xllog` |
| M4 | Extras on request: dashes, gradients, glow, world-space text scale, KosLib accent colours for overlays | Each on request only |

## 5. Explicitly out of scope

Hooks, signatures, game VFX/omens, ground raycasts that need game functions, IPC to Splatoon/Pictomancy, a minimap,
anything that runs outside `UiBuilder.Draw`.

## 6. What's verified and what isn't

- Verified 2026-11-15: Dalamud's `Dalamud.xml` documents `IUiBuilder.DeviceHandle`; the dev distrib holds
  `FFXIVClientStructs.dll` and `TerraFX.Interop.Windows.dll`; Pictomancy is AGPL-3.0 (GitHub licence field) with a
  `DXDraw` / `ImGuiDraw` / `VfxDraw` split; no family plugin draws in the world today (one call in KeyboundAssist,
  the rest inside the huntalerts upstream fork).
- Verified 2026-11-15 from the installed assemblies: TerraFX exposes `D3DCompile`; ClientStructs has
  `Control.ViewProjectionMatrix`, `RenderTargetManager.DepthStencil` (documented "unscaled scene reverse-Z depth
  stencil", a `Texture*` with `D3D11Texture2D`, `ActualWidth/Height` and `AllocatedWidth/Height`) and
  `RenderTargetManager.SwapChainBackBuffer`. Pictomancy's source (read with the user's permission, commit `70611d1`)
  confirms the approach: a typed `R24_UNORM_X8_TYPELESS` view of the depth texture, near = the matrix's `_m32`,
  depth compared in metres, UV scaled by actual/allocated size, UI masked by the back buffer's alpha.
- Verified offline: 22 tests, including the whole GPU path on WARP against a fake depth buffer and back buffer
  (occlusion, fade, line width, UI mask, fault breakers, resize, no leaked COM references). Each GPU check was
  confirmed to fail when its feature was deliberately broken.
- Not verified: anything in a live client: the depth texture's actual format, the scale under dynamic resolution,
  whether the back buffer's alpha marks UI, the ImGui path's look, and unload/reload.

## 7. Decisions (user, 2026-11-15)

1. **Licence:** AGPL-3.0.
2. **Reference source:** Pictomancy may be read as a reference; the code is our own.
3. **First build:** M1 through M3 in one go.
4. **First adopter:** none yet; the dev-only demo plugin is the host.
5. **Repo:** public `priestkos/KosDraw` on GitHub plus a Tower Forgejo mirror, like KosLib.
