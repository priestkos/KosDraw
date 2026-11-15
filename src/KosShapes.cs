#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace KosDraw;

// Pure: shapes in world space, projection and near-plane clipping. No Dalamud, ImGui or Direct3D types, so the offline
// tests compile this file on its own.

/// <summary>One filled world-space triangle. Colours are packed ImGui-style (0xAABBGGRR).</summary>
internal readonly record struct KosFill(Vector3 A, Vector3 B, Vector3 C, uint Color);

/// <summary>A world-space polyline, <see cref="Thickness"/> in screen pixels.</summary>
internal sealed record KosStroke(Vector3[] Points, bool Closed, uint Color, float Thickness);

/// <summary>A screen-space marker at a world point: a dot (<see cref="Text"/> null) or a centred label. Never occluded.</summary>
internal readonly record struct KosMark(Vector3 At, float Radius, string? Text, uint Color);

/// <summary>
/// One frame's shapes. Angles follow the game's object rotation: 0 faces +Z (south), π/2 faces +X (east).
/// <c>thickness</c> 0 fills a shape; above 0 outlines it that many pixels wide.
/// </summary>
internal sealed class KosShapes
{
    /// <summary>Segments in a full circle (5° each: a 30 m circle stays within 4 cm of round).</summary>
    public const int CircleSegments = 72;

    public readonly List<KosFill> Fills = [];
    public readonly List<KosStroke> Strokes = [];
    public readonly List<KosMark> Marks = [];

    public bool IsEmpty => Fills.Count == 0 && Strokes.Count == 0 && Marks.Count == 0;

    public void Clear()
    {
        Fills.Clear();
        Strokes.Clear();
        Marks.Clear();
    }

    public void Circle(Vector3 centre, float radius, Vector4 color, float thickness = 0) =>
        Fan(centre, 0, radius, 0, MathF.Tau, color, thickness);

    public void Ring(Vector3 centre, float inner, float outer, Vector4 color, float thickness = 0) =>
        Fan(centre, inner, outer, 0, MathF.Tau, color, thickness);

    /// <summary>A cone (pie slice) from <paramref name="origin"/>, <paramref name="halfAngle"/> either side of <paramref name="facing"/>.</summary>
    public void Cone(Vector3 origin, float radius, float facing, float halfAngle, Vector4 color, float thickness = 0) =>
        Fan(origin, 0, radius, facing - halfAngle, facing + halfAngle, color, thickness);

    /// <summary>An arc line from angle <paramref name="from"/> to <paramref name="to"/>.</summary>
    public void Arc(Vector3 centre, float radius, float from, float to, Vector4 color, float thickness = 2)
    {
        if (radius <= 0 || thickness <= 0) return;
        Strokes.Add(new KosStroke(ArcPoints(centre, radius, from, to), false, Pack(color), thickness));
    }

    /// <summary>The general ring sector every round shape is made of. <paramref name="inner"/> 0 makes a pie slice.</summary>
    public void Fan(Vector3 centre, float inner, float outer, float from, float to, Vector4 color, float thickness = 0)
    {
        inner = MathF.Max(inner, 0);
        if (outer <= inner) return;
        var c = Pack(color);
        var sweep = MathF.Min(MathF.Abs(to - from), MathF.Tau);
        var full = sweep >= MathF.Tau - 1e-4f;
        if (to < from) (from, to) = (to, from);
        to = from + sweep;
        var o = ArcPoints(centre, outer, from, to);
        var i = inner > 0 ? ArcPoints(centre, inner, from, to) : null;

        if (thickness > 0)
        {
            if (full)
            {
                Strokes.Add(new KosStroke(o[..^1], true, c, thickness)); // the last point repeats the first
                if (i is not null) Strokes.Add(new KosStroke(i[..^1], true, c, thickness));
                return;
            }
            var loop = new List<Vector3>(o);
            if (i is null) loop.Add(centre);
            else for (var k = i.Length - 1; k >= 0; k--) loop.Add(i[k]);
            Strokes.Add(new KosStroke(loop.ToArray(), true, c, thickness));
            return;
        }

        for (var k = 0; k < o.Length - 1; k++)
        {
            if (i is null)
            {
                Fills.Add(new KosFill(centre, o[k], o[k + 1], c));
                continue;
            }
            Fills.Add(new KosFill(i[k], o[k], o[k + 1], c));
            Fills.Add(new KosFill(i[k], o[k + 1], i[k + 1], c));
        }
    }

    public void Line(Vector3 a, Vector3 b, Vector4 color, float thickness = 2)
    {
        if (thickness <= 0) return;
        Strokes.Add(new KosStroke([a, b], false, Pack(color), thickness));
    }

    /// <summary>A line with a filled head at <paramref name="to"/>, the head lying flat (in the ground plane when level).</summary>
    public void Arrow(Vector3 from, Vector3 to, Vector4 color, float thickness = 2)
    {
        var d = to - from;
        var len = d.Length();
        if (len < 1e-4f || thickness <= 0) return;
        var dir = d / len;
        var head = MathF.Min(1f, len * 0.35f);
        var side = Vector3.Cross(dir, Vector3.UnitY);
        side = side.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(side); // straight up or down
        var b = to - dir * head;
        Line(from, b, color, thickness);
        Fills.Add(new KosFill(to, b + side * head * 0.5f, b - side * head * 0.5f, Pack(color)));
    }

    /// <summary>A polygon from its corners in order. shortcut: fills convex polygons only (fan from the first corner); add ear clipping if a concave one is needed.</summary>
    public void Polygon(IReadOnlyList<Vector3> corners, Vector4 color, float thickness = 0)
    {
        if (corners.Count < 3) return;
        var c = Pack(color);
        if (thickness > 0)
        {
            var pts = new Vector3[corners.Count];
            for (var k = 0; k < pts.Length; k++) pts[k] = corners[k];
            Strokes.Add(new KosStroke(pts, true, c, thickness));
            return;
        }
        for (var k = 1; k < corners.Count - 1; k++) Fills.Add(new KosFill(corners[0], corners[k], corners[k + 1], c));
    }

    /// <summary>A screen-space dot <paramref name="radius"/> pixels across, at a world point.</summary>
    public void Dot(Vector3 at, float radius, Vector4 color)
    {
        if (radius > 0) Marks.Add(new KosMark(at, radius, null, Pack(color)));
    }

    /// <summary>A label centred on a world point.</summary>
    public void Text(Vector3 at, string text, Vector4 color)
    {
        if (!string.IsNullOrEmpty(text)) Marks.Add(new KosMark(at, 0, text, Pack(color)));
    }

    /// <summary>Points on an arc, both ends included, at most 5° apart.</summary>
    public static Vector3[] ArcPoints(Vector3 centre, float radius, float from, float to)
    {
        var sweep = to - from;
        var n = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweep) / MathF.Tau * CircleSegments - 1e-3f));
        var pts = new Vector3[n + 1];
        for (var k = 0; k <= n; k++)
        {
            var a = from + sweep * k / n;
            pts[k] = centre + new Vector3(MathF.Sin(a), 0, MathF.Cos(a)) * radius;
        }
        return pts;
    }

    /// <summary>A colour packed the way ImGui's <c>GetColorU32</c> does (0xAABBGGRR), without the style alpha.</summary>
    public static uint Pack(Vector4 c)
    {
        static uint B(float v) => (uint)MathF.Round(Math.Clamp(v, 0, 1) * 255);
        return B(c.X) | B(c.Y) << 8 | B(c.Z) << 16 | B(c.W) << 24;
    }
}

/// <summary>World to screen with the game's view-projection matrix (System.Numerics row vectors: clip = (p, 1) × M).</summary>
internal static class KosProjection
{
    /// <summary>Clip-space w of a point: its distance in front of the camera.</summary>
    public static float W(in Matrix4x4 m, Vector3 p) => p.X * m.M14 + p.Y * m.M24 + p.Z * m.M34 + m.M44;

    /// <summary>
    /// The near distance. The game's projection is infinite reverse-Z, so clip z is the constant near distance and
    /// depth = near / w; the GPU drops anything with w below it, and the CPU path clips at the same plane.
    /// </summary>
    public static float Near(in Matrix4x4 m) => MathF.Max(m.M43, 1e-3f);

    /// <summary>Pixel position in a viewport of <paramref name="size"/>; false when the point is behind the near plane.</summary>
    public static bool ToScreen(in Matrix4x4 m, Vector3 p, Vector2 size, out Vector2 screen)
    {
        if (W(m, p) < Near(m))
        {
            screen = default;
            return false;
        }
        screen = Project(m, p, size);
        return true;
    }

    /// <summary>Pixel position of a point already clipped to the near plane (no check, so points exactly on it pass).</summary>
    public static Vector2 Project(in Matrix4x4 m, Vector3 p, Vector2 size)
    {
        var c = Vector4.Transform(new Vector4(p, 1), m);
        return new Vector2((c.X / c.W + 1) * 0.5f * size.X, (1 - c.Y / c.W) * 0.5f * size.Y);
    }

    /// <summary>The part of segment a-b in front of the near plane; false when none is.</summary>
    public static bool ClipSegment(in Matrix4x4 m, Vector3 a, Vector3 b, out Vector3 ca, out Vector3 cb)
    {
        var near = Near(m);
        float da = W(m, a) - near, db = W(m, b) - near;
        (ca, cb) = (a, b);
        if (da < 0 && db < 0) return false;
        if (da < 0) ca = a + (b - a) * (da / (da - db));
        else if (db < 0) cb = b + (a - b) * (db / (db - da));
        return true;
    }

    /// <summary>
    /// A triangle cut at the near plane: writes 0, 3 or 4 corners of a convex polygon to <paramref name="poly"/>
    /// (length 4 or more) and returns the count.
    /// </summary>
    public static int ClipTriangle(in Matrix4x4 m, Vector3 a, Vector3 b, Vector3 c, Span<Vector3> poly)
    {
        var near = Near(m);
        Span<Vector3> v = [a, b, c];
        Span<float> d = [W(m, a) - near, W(m, b) - near, W(m, c) - near];
        var n = 0;
        for (var k = 0; k < 3; k++)
        {
            var j = (k + 1) % 3;
            if (d[k] >= 0) poly[n++] = v[k];
            if ((d[k] >= 0) != (d[j] >= 0)) poly[n++] = v[k] + (v[j] - v[k]) * (d[k] / (d[k] - d[j]));
        }
        return n;
    }

    /// <summary>
    /// The runs of a polyline in front of the near plane. A closed loop that is wholly visible comes back as one run
    /// whose last point repeats its first; a loop cut by the plane comes back as open runs.
    /// </summary>
    public static List<List<Vector3>> ClipPolyline(in Matrix4x4 m, IReadOnlyList<Vector3> pts, bool closed)
    {
        var runs = new List<List<Vector3>>();
        var n = pts.Count;
        if (n < 2) return runs;
        List<Vector3>? run = null;
        var segments = closed ? n : n - 1;
        for (var s = 0; s < segments; s++)
        {
            Vector3 a = pts[s], b = pts[(s + 1) % n];
            if (!ClipSegment(m, a, b, out var ca, out var cb))
            {
                run = null;
                continue;
            }
            if (run is null || ca != a)
            {
                run = [ca];
                runs.Add(run);
            }
            run.Add(cb);
            if (cb != b) run = null;
        }
        // A loop cut once comes back as [... -> p0] and [p0 -> ...]: join them so p0 is a corner, not two line ends.
        if (closed && runs.Count > 1 && runs[^1][^1] == pts[0] && runs[0][0] == pts[0])
        {
            runs[^1].AddRange(runs[0].GetRange(1, runs[0].Count - 1));
            runs.RemoveAt(0);
        }
        return runs;
    }
}
