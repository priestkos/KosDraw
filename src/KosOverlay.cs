#nullable enable
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace KosDraw;

/// <summary>
/// Draws shapes in the game world, behind every ImGui window. The host creates one in its constructor and disposes it
/// in its own Dispose, then calls <see cref="Draw"/> from <c>UiBuilder.Draw</c> each frame:
/// <code>overlay.Draw(s => s.Circle(player.Position, 5, color));</code>
/// The ImGui path is the default. <see cref="UseGpu"/> (off by default) turns on the Direct3D path, which hides shapes
/// behind scenery and keeps them off the game's UI; any part of it that faults switches off and falls back to ImGui.
/// Game memory is only read (camera matrix, depth texture, back buffer), every frame afresh; nothing is hooked.
/// </summary>
internal sealed unsafe class KosOverlay : IDisposable
{
    private readonly IUiBuilder ui;
    private readonly IPluginLog log;
    private readonly KosShapes shapes = new();
    private KosGpu? gpu;
    private bool gpuBroken;
    private int drawThread;
    private bool wrongThreadLogged;

    public KosOverlay(IUiBuilder ui, IPluginLog log) => (this.ui, this.log) = (ui, log);

    /// <summary>Opt-in Direct3D path with depth occlusion. Off by default.</summary>
    public bool UseGpu { get; set; }

    /// <summary>GPU path: alpha kept by the part of a shape behind scenery (0 hides it).</summary>
    public float OccludedAlpha { get; set; } = 0.25f;

    /// <summary>GPU path: metres a shape may sit behind the scene and still count as in front (shapes on uneven ground).</summary>
    public float Tolerance { get; set; } = 0.3f;

    /// <summary>GPU path: keep shapes off the game's own UI (hotbars, chat).</summary>
    public bool MaskGameUi { get; set; } = true;

    /// <summary>GPU features still on (None while the GPU path is off or not started).</summary>
    public KosGpuFeature GpuFeatures => gpu?.Enabled ?? KosGpuFeature.None;

    /// <summary>The last GPU fault, for a status line.</summary>
    public string? LastFault { get; private set; }

    /// <summary>For testing the fault breakers from a debug UI: the next GPU frame fails inside these features.</summary>
    public void InjectFault(KosGpuFeature features)
    {
        if (gpu is not null) gpu.InjectFault |= features;
    }

    /// <summary>Collects this frame's shapes from <paramref name="build"/> and draws them. Call only from <c>UiBuilder.Draw</c>.</summary>
    public void Draw(Action<KosShapes> build)
    {
        if (!OnDrawThread()) return;
        var control = Control.Instance();
        if (control == null) return;
        var viewProj = control->ViewProjectionMatrix;
        var viewport = ImGui.GetMainViewport();
        var size = viewport.Size;
        if (size.X < 1 || size.Y < 1) return;

        shapes.Clear();
        try
        {
            build(shapes);
            if (shapes.IsEmpty) return;
            var draw = ImGui.GetBackgroundDrawList();
            var srv = UseGpu ? RenderGpu(viewProj, size) : 0;
            if (srv != 0) draw.AddImage(new ImTextureID(srv), viewport.Pos, viewport.Pos + size);
            var onGpu = srv != 0 ? gpu!.Enabled : KosGpuFeature.None;
            DrawImGui(draw, viewProj, viewport.Pos, size, !onGpu.HasFlag(KosGpuFeature.Fills), !onGpu.HasFlag(KosGpuFeature.Strokes));
        }
        finally
        {
            shapes.Clear();
        }
    }

    private bool OnDrawThread()
    {
        var id = Environment.CurrentManagedThreadId;
        if (drawThread == 0) drawThread = id;
        if (id == drawThread) return true;
        if (!wrongThreadLogged) log.Error("[KosDraw] Draw called off the draw thread; ignored. Call it from UiBuilder.Draw.");
        wrongThreadLogged = true;
        return false;
    }

    private nint RenderGpu(in Matrix4x4 viewProj, Vector2 size)
    {
        if (gpuBroken) return 0;
        try
        {
            if (gpu is null)
            {
                var device = ui.DeviceHandle; // 0 until the game's device exists: try again next frame
                if (device == 0) return 0;
                gpu = new KosGpu(device);
            }

            nint depth = 0, back = 0;
            var depthScale = Vector2.One;
            var targets = RenderTargetManager.Instance();
            if (targets != null)
            {
                var d = targets->DepthStencil; // the scene's reverse-Z depth, at render resolution
                if (d != null && d->D3D11Texture2D != null && d->ActualWidth > 0 && d->ActualHeight > 0)
                {
                    depth = (nint)d->D3D11Texture2D;
                    depthScale = new Vector2(d->ActualWidth / size.X, d->ActualHeight / size.Y);
                }
                var b = targets->SwapChainBackBuffer;
                if (MaskGameUi && b != null && b->D3D11Texture2D != null) back = (nint)b->D3D11Texture2D;
            }

            var before = gpu.LastFault;
            var srv = gpu.Render(shapes, new KosGpuFrame(viewProj, size, depth, depthScale, back, OccludedAlpha, Tolerance));
            if (gpu.LastFault != before)
            {
                LastFault = gpu.LastFault;
                log.Warning($"[KosDraw] {LastFault}");
            }
            return srv;
        }
        catch (Exception e) // device or pipeline creation: the GPU path stays off until the plugin reloads
        {
            gpuBroken = true;
            LastFault = $"GPU path off: {e.Message}";
            log.Error(e, "[KosDraw] GPU path off; drawing with ImGui.");
            gpu?.Dispose();
            gpu = null;
            return 0;
        }
    }

    private void DrawImGui(ImDrawListPtr draw, in Matrix4x4 m, Vector2 origin, Vector2 size, bool fills, bool strokes)
    {
        if (fills && shapes.Fills.Count > 0)
        {
            var flags = draw.Flags;
            draw.Flags &= ~ImDrawListFlags.AntiAliasedFill; // anti-aliased edges would show as seams between triangles
            try
            {
                Span<Vector3> poly = stackalloc Vector3[4];
                Span<Vector2> px = stackalloc Vector2[4];
                foreach (var f in shapes.Fills)
                {
                    var n = KosProjection.ClipTriangle(m, f.A, f.B, f.C, poly);
                    for (var k = 0; k < n; k++) px[k] = origin + KosProjection.Project(m, poly[k], size);
                    for (var k = 1; k < n - 1; k++) draw.AddTriangleFilled(px[0], px[k], px[k + 1], f.Color);
                }
            }
            finally
            {
                draw.Flags = flags;
            }
        }

        if (strokes)
            foreach (var s in shapes.Strokes)
            foreach (var run in KosProjection.ClipPolyline(m, s.Points, s.Closed))
            {
                var loop = s.Closed && run.Count == s.Points.Length + 1; // wholly visible: draw closed for a proper corner at the start
                var count = loop ? run.Count - 1 : run.Count;
                var px = new Vector2[count];
                for (var k = 0; k < count; k++) px[k] = origin + KosProjection.Project(m, run[k], size);
                draw.AddPolyline(ref px[0], count, s.Color, loop ? ImDrawFlags.Closed : ImDrawFlags.None, s.Thickness);
            }

        foreach (var mark in shapes.Marks)
        {
            if (!KosProjection.ToScreen(m, mark.At, size, out var at)) continue;
            at += origin;
            if (mark.Text is null) draw.AddCircleFilled(at, mark.Radius, mark.Color);
            else draw.AddText(at - ImGui.CalcTextSize(mark.Text) / 2, mark.Color, mark.Text);
        }
    }

    /// <summary>Releases the GPU objects. The host calls it from its own Dispose.</summary>
    public void Dispose()
    {
        gpu?.Dispose();
        gpu = null;
    }
}
