using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Tests;

public class TriangulatorTests
{
    private static double Area((double X, double Y)[] p, int[] t)
    {
        double total = 0;
        for (var i = 0; i < t.Length; i += 3)
        {
            var (a, b, c) = (p[t[i]], p[t[i + 1]], p[t[i + 2]]);
            var signed = ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
            Assert.True(signed > -1e-9, "a triangle is clockwise");
            total += signed;
        }

        return total;
    }

    private static double PolygonArea(IEnumerable<(double X, double Y)> loop)
    {
        var l = loop.ToArray();
        double s = 0;
        for (var i = 0; i < l.Length; i++)
        {
            var (a, b) = (l[i], l[(i + 1) % l.Length]);
            s += a.X * b.Y - b.X * a.Y;
        }

        return s / 2;
    }

    private static bool Inside((double X, double Y) pt, IEnumerable<(double X, double Y)[]> loops)
    {
        var inside = false;
        foreach (var l in loops)
        {
            for (int i = 0, j = l.Length - 1; i < l.Length; j = i++)
            {
                if ((l[i].Y > pt.Y) != (l[j].Y > pt.Y) && pt.X < (l[j].X - l[i].X) * (pt.Y - l[i].Y) / (l[j].Y - l[i].Y) + l[i].X)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    [Fact]
    public void Square_gives_two_triangles()
    {
        (double, double)[] p = [(0, 0), (1, 0), (1, 1), (0, 1)];

        var t = Triangulator.Triangulate(p, [[0, 1, 2, 3]]);

        Assert.Equal(6, t.Length);
        Assert.Equal(1.0, Area(p, t), 9);
    }

    [Fact]
    public void Square_with_a_hole_excludes_the_hole()
    {
        var pts = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (0, 10), (3, 3), (3, 7), (7, 7), (7, 3) };

        var t = Triangulator.Triangulate(pts, [[0, 1, 2, 3], [4, 5, 6, 7]]);

        Assert.Equal(100 - 16, Area(pts.ToArray(), t), 6);
    }

    [Fact]
    public void Concave_polygon_keeps_its_notch()
    {
        // An L shape: area 3.
        var pts = new (double X, double Y)[] { (0, 0), (2, 0), (2, 1), (1, 1), (1, 2), (0, 2) };

        var t = Triangulator.Triangulate(pts, [[0, 1, 2, 3, 4, 5]]);

        Assert.Equal(3.0, Area(pts, t), 9);
        for (var i = 0; i < t.Length; i += 3)
        {
            var c = ((pts[t[i]].X + pts[t[i + 1]].X + pts[t[i + 2]].X) / 3, (pts[t[i]].Y + pts[t[i + 1]].Y + pts[t[i + 2]].Y) / 3);
            Assert.True(Inside(c, [pts]));
        }
    }

    [Fact]
    public void Circle_with_interior_grid_points_covers_the_disc()
    {
        var pts = new List<(double X, double Y)>();
        const int n = 64;
        for (var i = 0; i < n; i++)
        {
            pts.Add((Math.Cos(2 * Math.PI * i / n), Math.Sin(2 * Math.PI * i / n)));
        }

        for (var gx = -9; gx <= 9; gx++)
        {
            for (var gy = -9; gy <= 9; gy++)
            {
                var (x, y) = (gx / 10.0, gy / 10.0);
                if (x * x + y * y < 0.8)
                {
                    pts.Add((x, y));
                }
            }
        }

        var t = Triangulator.Triangulate(pts, [Enumerable.Range(0, n).ToArray()]);

        Assert.Equal(PolygonArea(pts.Take(n)), Area(pts.ToArray(), t), 6);
        Assert.True(t.Length / 3 > 100);
    }

    [Fact]
    public void Random_star_polygons_with_holes_have_the_exact_area()
    {
        var random = new Random(3);
        for (var round = 0; round < 40; round++)
        {
            var pts = new List<(double X, double Y)>();
            var loops = new List<int[]>();
            var outer = new List<(double X, double Y)>();
            var count = 8 + random.Next(40);
            for (var i = 0; i < count; i++)
            {
                var a = 2 * Math.PI * i / count;
                var r = 8 + random.NextDouble() * 4;
                outer.Add((r * Math.Cos(a), r * Math.Sin(a)));
            }

            loops.Add(Enumerable.Range(0, count).ToArray());
            pts.AddRange(outer);
            var polygonArea = PolygonArea(outer);
            var holeLoops = new List<(double X, double Y)[]>();
            for (var h = 0; h < 2; h++)
            {
                var hole = new List<(double X, double Y)>();
                var m = 5 + random.Next(6);
                var (cx, cy) = (h == 0 ? -3.0 : 3.0, 0.0);
                for (var i = 0; i < m; i++)
                {
                    var a = 2 * Math.PI * i / m;
                    var r = 1 + random.NextDouble();
                    hole.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
                }

                loops.Add(Enumerable.Range(pts.Count, m).ToArray());
                pts.AddRange(hole);
                holeLoops.Add(hole.ToArray());
                polygonArea -= PolygonArea(hole);
            }

            var t = Triangulator.Triangulate(pts, loops);

            Assert.Equal(polygonArea, Area(pts.ToArray(), t), 6);
            for (var i = 0; i < t.Length; i += 3)
            {
                var c = ((pts[t[i]].X + pts[t[i + 1]].X + pts[t[i + 2]].X) / 3, (pts[t[i]].Y + pts[t[i + 1]].Y + pts[t[i + 2]].Y) / 3);
                Assert.True(Inside(c, new[] { outer.ToArray() }.Concat(holeLoops)), $"round {round}: triangle outside the region");
            }
        }
    }

    [Fact]
    public void Thin_strip_with_many_boundary_points_is_triangulated()
    {
        // A cylinder side in parameter space: a long strip with a dense top and bottom edge.
        var pts = new List<(double X, double Y)>();
        const int n = 200;
        for (var i = 0; i <= n; i++)
        {
            pts.Add((i * 0.0314, 0));
        }

        for (var i = n; i >= 0; i--)
        {
            pts.Add((i * 0.0314, 5));
        }

        var t = Triangulator.Triangulate(pts, [Enumerable.Range(0, pts.Count).ToArray()]);

        Assert.Equal(n * 0.0314 * 5, Area(pts.ToArray(), t), 6);
    }

    [Fact]
    public void Grid_points_that_are_cocircular_do_not_break_it()
    {
        var pts = new List<(double X, double Y)> { (0, 0), (20, 0), (20, 20), (0, 20) };
        for (var x = 1; x < 20; x++)
        {
            for (var y = 1; y < 20; y++)
            {
                pts.Add((x, y));
            }
        }

        var t = Triangulator.Triangulate(pts, [[0, 1, 2, 3]]);

        Assert.Equal(400.0, Area(pts.ToArray(), t), 6);
        Assert.Equal(2 * pts.Count - 2 - 4, t.Length / 3); // Euler: a triangulated disc with V points and 4 on the boundary
    }
}
