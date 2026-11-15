using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using KosDraw;

namespace KosDrawDemo;

/// <summary>Dev-only host: /kosdraw opens a window that draws every shape round the player.</summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/kosdraw";

    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly WindowSystem windows = new("KosDrawDemo");
    private readonly DemoWindow window;
    private readonly KosOverlay overlay;

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IObjectTable objects, ITargetManager targets, IPluginLog log)
    {
        (this.pi, this.commands, this.objects, this.targets) = (pi, commands, objects, targets);
        overlay = new KosOverlay(pi.UiBuilder, log);
        window = new DemoWindow(overlay) { IsOpen = true };
        windows.AddWindow(window);
        commands.AddHandler(Command, new CommandInfo((_, _) => window.Toggle()) { HelpMessage = "Open the KosDraw demo window." });
        pi.UiBuilder.Draw += OnDraw;
        pi.UiBuilder.OpenMainUi += window.Toggle;
    }

    private void OnDraw()
    {
        windows.Draw();
        if (!window.Show || objects.LocalPlayer is not { } me) return;
        var target = targets.Target;
        overlay.Draw(s => window.Shapes(s, me.Position, me.Rotation, target?.Position, target?.Rotation));
    }

    public void Dispose()
    {
        pi.UiBuilder.Draw -= OnDraw;
        pi.UiBuilder.OpenMainUi -= window.Toggle;
        commands.RemoveHandler(Command);
        windows.RemoveAllWindows();
        overlay.Dispose();
    }
}

internal sealed class DemoWindow(KosOverlay overlay) : Window("KosDraw demo")
{
    private static readonly Vector4 Teal = new(0.2f, 0.85f, 0.8f, 0.45f), Gold = new(1f, 0.8f, 0.3f, 0.9f), Crimson = new(0.9f, 0.2f, 0.3f, 0.4f);

    public bool Show = true;
    private bool circle = true, ring = true, cone = true, fan, arc = true, lines = true, polygon, marks = true, outlines;
    private float thickness = 3;

    public void Shapes(KosShapes s, Vector3 me, float facing, Vector3? target, float? targetFacing)
    {
        var t = outlines ? thickness : 0;
        if (circle) s.Circle(me, 1.5f, Teal, t);
        if (ring) s.Ring(me, 4, 5, Teal, t);
        if (cone) s.Cone(me, 8, facing, MathF.PI / 6, Crimson, t);
        if (fan) s.Fan(me, 6, 10, facing + MathF.PI * 0.5f, facing + MathF.PI * 1.5f, Crimson, t);
        if (arc) s.Arc(me, 12, facing - MathF.PI / 4, facing + MathF.PI / 4, Gold, thickness);
        if (lines)
        {
            s.Line(me + new Vector3(-6, 0, 6), me + new Vector3(6, 0, 6), Gold, thickness);
            s.Arrow(me, me + new Vector3(MathF.Sin(facing), 0, MathF.Cos(facing)) * 6, Gold, thickness);
        }
        if (polygon) s.Polygon([me + new Vector3(8, 0, -2), me + new Vector3(12, 0, -2), me + new Vector3(12, 0, 2), me + new Vector3(8, 0, 2)], Teal, t);
        if (marks)
        {
            s.Dot(me + new Vector3(0, 2.2f, 0), 4, Gold);
            s.Text(me + new Vector3(0, 2.6f, 0), "KosDraw", Gold);
        }
        if (target is { } at && targetFacing is { } tf) s.Cone(at, 6, tf, MathF.PI / 4, Crimson, t);
    }

    public override void Draw()
    {
        ImGui.Checkbox("Draw round me", ref Show);
        ImGui.Checkbox("Circle", ref circle);
        ImGui.SameLine();
        ImGui.Checkbox("Ring", ref ring);
        ImGui.SameLine();
        ImGui.Checkbox("Cone", ref cone);
        ImGui.SameLine();
        ImGui.Checkbox("Fan", ref fan);
        ImGui.Checkbox("Arc", ref arc);
        ImGui.SameLine();
        ImGui.Checkbox("Line + arrow", ref lines);
        ImGui.SameLine();
        ImGui.Checkbox("Polygon", ref polygon);
        ImGui.SameLine();
        ImGui.Checkbox("Dot + text", ref marks);
        ImGui.Checkbox("Outlines instead of fills", ref outlines);
        ImGui.SetNextItemWidth(160);
        ImGui.SliderFloat("Line width (px)", ref thickness, 1, 8);

        ImGui.Separator();
        var gpu = overlay.UseGpu;
        if (ImGui.Checkbox("GPU path (hides shapes behind scenery)", ref gpu)) overlay.UseGpu = gpu;
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled(!gpu))
        {
            var mask = overlay.MaskGameUi;
            if (ImGui.Checkbox("Keep off the game's UI", ref mask)) overlay.MaskGameUi = mask;
            var occluded = overlay.OccludedAlpha;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("Alpha behind scenery", ref occluded, 0, 1)) overlay.OccludedAlpha = occluded;
            var tolerance = overlay.Tolerance;
            ImGui.SetNextItemWidth(160);
            if (ImGui.SliderFloat("Depth tolerance (m)", ref tolerance, 0, 2)) overlay.Tolerance = tolerance;
            ImGui.TextUnformatted($"GPU features on: {overlay.GpuFeatures}");
            if (overlay.LastFault is { } fault) ImGui.TextWrapped($"Last fault: {fault}");
            ImGui.TextUnformatted("Inject a fault (that part switches off, the rest carries on):");
            foreach (var f in new[] { KosGpuFeature.Fills, KosGpuFeature.Strokes, KosGpuFeature.Depth, KosGpuFeature.UiMask, KosGpuFeature.Composite })
            {
                if (ImGui.Button(f.ToString())) overlay.InjectFault(f);
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }
    }
}
