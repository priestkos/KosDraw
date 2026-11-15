using System.Numerics;
using KosDraw;
using Xunit;

namespace KosDraw.Tests;

public class ShapeTests
{
    private static readonly Vector4 Red = new(1, 0, 0, 1);

    /// <summary>A left-handed camera at the origin looking down +Z, infinite reverse-Z like the game's.</summary>
    internal static Matrix4x4 Camera(float near = 0.1f, float fovY = MathF.PI / 2)
    {
        var f = 1 / MathF.Tan(fovY / 2);
        return new Matrix4x4(f, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, 1, 0, 0, near, 0);
    }

    private static float Area(Vector3 a, Vector3 b, Vector3 c) => Vector3.Cross(b - a, c - a).Length() / 2;

    [Fact]
    public void ArcPointsAreAtMostFiveDegreesApartWithBothEnds()
    {
        var p = KosShapes.ArcPoints(Vector3.Zero, 2, 0, MathF.PI / 2);
        Assert.Equal(19, p.Length); // 90° in 5° steps
        Assert.True(Vector3.Distance(p[0], new Vector3(0, 0, 2)) < 1e-5f, "0 rad faces +Z");
        Assert.True(Vector3.Distance(p[^1], new Vector3(2, 0, 0)) < 1e-5f, "π/2 faces +X");
        Assert.Equal(KosShapes.CircleSegments + 1, KosShapes.ArcPoints(Vector3.Zero, 1, 0, MathF.Tau).Length);
        Assert.Equal(2, KosShapes.ArcPoints(Vector3.Zero, 1, 0, 0.01f).Length);
    }

    [Fact]
    public void FilledCircleAndRingHaveTheRightArea()
    {
        var s = new KosShapes();
        s.Circle(new Vector3(5, 1, 5), 10, Red);
        Assert.Equal(KosShapes.CircleSegments, s.Fills.Count);
        var area = s.Fills.Sum(f => Area(f.A, f.B, f.C));
        Assert.InRange(area / (MathF.PI * 100), 0.995f, 1f); // a polygon inside the circle
        Assert.All(s.Fills, f => Assert.Equal(1f, f.A.Y));

        s.Clear();
        s.Ring(Vector3.Zero, 4, 6, Red);
        Assert.Equal(2 * KosShapes.CircleSegments, s.Fills.Count);
        Assert.InRange(s.Fills.Sum(f => Area(f.A, f.B, f.C)) / (MathF.PI * 20), 0.995f, 1f);
    }

    [Fact]
    public void OutlinesAreClosedLoops()
    {
        var s = new KosShapes();
        s.Ring(Vector3.Zero, 4, 6, Red, 2);
        Assert.Equal(2, s.Strokes.Count);
        Assert.All(s.Strokes, k => Assert.True(k.Closed && k.Points.Length == KosShapes.CircleSegments));

        s.Clear();
        s.Cone(Vector3.Zero, 10, MathF.PI / 2, MathF.PI / 4, Red, 2); // facing east
        var loop = Assert.Single(s.Strokes);
        Assert.True(loop.Closed);
        Assert.Equal(Vector3.Zero, loop.Points[^1]); // back to the apex
        Assert.True(loop.Points.Average(p => p.X) > 4, "a cone facing π/2 points along +X");
        Assert.All(loop.Points[..^1], p => Assert.InRange(p.Length(), 9.999f, 10.001f));
    }

    [Fact]
    public void ConeSpansTwiceItsHalfAngle()
    {
        var s = new KosShapes();
        s.Cone(Vector3.Zero, 5, 0, MathF.PI / 6, Red);
        Assert.Equal(12, s.Fills.Count); // 60° in 5° slices
        var edge = s.Fills[0].B;
        Assert.InRange(MathF.Atan2(edge.X, edge.Z), -MathF.PI / 6 - 1e-4f, -MathF.PI / 6 + 1e-4f);
    }

    [Fact]
    public void ArrowHeadLiesFlatAndEndsAtTheTip()
    {
        var s = new KosShapes();
        s.Arrow(new Vector3(0, 2, 0), new Vector3(0, 2, 10), Red, 3);
        var shaft = Assert.Single(s.Strokes);
        var head = Assert.Single(s.Fills);
        Assert.Equal(new Vector3(0, 2, 10), head.A);
        Assert.Equal(shaft.Points[1].Z, head.B.Z, 4);
        Assert.All(new[] { head.A, head.B, head.C }, p => Assert.Equal(2f, p.Y));
        s.Arrow(Vector3.Zero, Vector3.UnitY * 3, Red); // straight up must not divide by zero
        Assert.All(s.Fills.Skip(1), f => Assert.False(float.IsNaN(f.B.X)));
    }

    [Fact]
    public void PolygonFillsAsAFanAndOutlinesClosed()
    {
        var s = new KosShapes();
        Vector3[] square = [new(0, 0, 0), new(2, 0, 0), new(2, 0, 2), new(0, 0, 2)];
        s.Polygon(square, Red);
        Assert.Equal(2, s.Fills.Count);
        Assert.Equal(4f, s.Fills.Sum(f => Area(f.A, f.B, f.C)), 4);
        s.Polygon(square, Red, 1);
        Assert.True(Assert.Single(s.Strokes).Closed);
        s.Polygon(square[..2], Red);
        Assert.Equal(2, s.Fills.Count);
    }

    [Fact]
    public void PacksLikeImGui()
    {
        Assert.Equal(0xFF0000FFu, KosShapes.Pack(new Vector4(1, 0, 0, 1)));
        Assert.Equal(0x80FF0000u, KosShapes.Pack(new Vector4(0, 0, 1, 0.5f)));
        Assert.Equal(0xFFFFFFFFu, KosShapes.Pack(new Vector4(2, 2, 2, 2)));
    }

    [Fact]
    public void ProjectsLikeTheCameraAndRefusesPointsBehind()
    {
        var m = Camera();
        var size = new Vector2(200, 100);
        Assert.True(KosProjection.ToScreen(m, new Vector3(0, 0, 10), size, out var centre));
        Assert.Equal(new Vector2(100, 50), centre);
        Assert.True(KosProjection.ToScreen(m, new Vector3(10, 10, 10), size, out var corner)); // 45° = edge of a 90° view
        Assert.Equal(200, corner.X, 3);
        Assert.Equal(0, corner.Y, 3);
        Assert.False(KosProjection.ToScreen(m, new Vector3(0, 0, -1), size, out _));
        Assert.False(KosProjection.ToScreen(m, new Vector3(0, 0, 0.05f), size, out _)); // inside the near plane

        Assert.Equal(0.1f, KosProjection.Near(m));
        var c = Vector4.Transform(new Vector4(0, 0, 10, 1), m);
        Assert.Equal(0.01f, c.Z / c.W, 6); // reverse-Z: depth = near / distance
    }

    [Fact]
    public void ClipsSegmentsAtTheNearPlane()
    {
        var m = Camera();
        Assert.True(KosProjection.ClipSegment(m, new Vector3(0, 0, -5), new Vector3(0, 0, 5), out var a, out var b));
        Assert.Equal(0.1f, KosProjection.W(m, a), 5);
        Assert.Equal(new Vector3(0, 0, 5), b);
        Assert.True(KosProjection.ClipSegment(m, new Vector3(1, 0, 5), new Vector3(1, 0, -5), out a, out b));
        Assert.Equal(new Vector3(1, 0, 5), a);
        Assert.Equal(0.1f, KosProjection.W(m, b), 5);
        Assert.False(KosProjection.ClipSegment(m, new Vector3(0, 0, -5), new Vector3(3, 0, -1), out _, out _));
    }

    [Theory]
    [InlineData(5, 5, 5, 3)]
    [InlineData(-5, 5, 5, 4)]
    [InlineData(-5, -5, 5, 3)]
    [InlineData(-5, -5, -5, 0)]
    public void ClipsTrianglesToAConvexPolygonInFront(float za, float zb, float zc, int corners)
    {
        var m = Camera();
        Span<Vector3> poly = stackalloc Vector3[4];
        var n = KosProjection.ClipTriangle(m, new Vector3(-1, 0, za), new Vector3(1, 0, zb), new Vector3(0, 1, zc), poly);
        Assert.Equal(corners, n);
        for (var k = 0; k < n; k++) Assert.True(KosProjection.W(m, poly[k]) >= 0.1f - 1e-5f);
    }

    [Fact]
    public void ClipsPolylinesIntoRunsAndRejoinsACutLoop()
    {
        var m = Camera();
        Vector3[] square = [new(-1, 0, 5), new(1, 0, 5), new(1, 0, 15), new(-1, 0, 15)];
        var whole = Assert.Single(KosProjection.ClipPolyline(m, square, true));
        Assert.Equal(5, whole.Count);
        Assert.Equal(whole[0], whole[^1]);

        // The third corner behind the camera: one run, re-joined through the first corner.
        Vector3[] cut = [new(-1, 0, 5), new(1, 0, 5), new(1, 0, -5), new(-1, 0, 15)];
        var run = Assert.Single(KosProjection.ClipPolyline(m, cut, true));
        Assert.Equal(5, run.Count);
        Assert.Equal(cut[3], run[1]);
        Assert.Equal(cut[0], run[2]);
        Assert.Equal(0.1f, KosProjection.W(m, run[0]), 5);
        Assert.Equal(0.1f, KosProjection.W(m, run[^1]), 5);

        // Open line dipping behind the camera in the middle: two runs.
        Vector3[] dip = [new(-2, 0, 5), new(0, 0, -5), new(2, 0, 5)];
        Assert.Equal(2, KosProjection.ClipPolyline(m, dip, false).Count);
        Assert.Empty(KosProjection.ClipPolyline(m, [new(0, 0, -1), new(1, 0, -2)], false));
    }
}
