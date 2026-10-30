using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Step;

/// <summary>A boundary of a face: its points in 3D, and optionally the surface parameters of each point (when the file gives the boundary in parameter space).</summary>
internal sealed class FaceLoop
{
    public FaceLoop(List<V3> points, List<(double U, double V)>? uv = null)
    {
        Points = points;
        Uv = uv;
    }

    public List<V3> Points { get; }

    public List<(double U, double V)>? Uv { get; }
}

/// <summary>
/// Triangulates one trimmed surface patch: boundaries are mapped into the parameter space of the surface, triangulated there with a
/// constrained Delaunay triangulation (with interior sample points for surfaces that curve in both directions) and mapped back, with
/// analytic normals. Shared by the STEP and IGES readers, which differ only in how they find the surface and its boundaries.
/// </summary>
internal sealed class SurfaceFaceTessellator
{
    private readonly double _angular;
    private readonly CancellationToken _cancel;

    public SurfaceFaceTessellator(double angular, CancellationToken cancel, List<string> warnings)
    {
        _angular = angular;
        _cancel = cancel;
        Warnings = warnings;
    }

    public List<string> Warnings { get; }

    /// <summary>
    /// Triangles of the patch of <paramref name="surface"/> inside <paramref name="loops"/> (the outer loop first), counter-clockwise seen from
    /// outside when <paramref name="sameSense"/> (the face agrees with the surface normal). Without a surface, flat boundaries are meshed as a plane.
    /// </summary>
    public FaceMesh? Tessellate(Surface? surface, List<FaceLoop> loops, bool sameSense, string label, bool approximatedWarning = false)
    {
        if (surface is null)
        {
            surface = FitPlane(loops);
            if (surface is null)
            {
                Warnings.Add($"{label} has no usable surface and was skipped.");
                return null;
            }

            if (approximatedWarning)
            {
                Warnings.Add($"{label} uses an unsupported surface type and was approximated by a plane.");
            }
        }

        try
        {
            var mesh = surface.IsPlane ? Planar((PlaneSurface)surface, loops, sameSense) : Curved(surface, loops, sameSense);
            return mesh is { Indices.Count: > 0 } ? mesh : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Warnings.Add($"{label} could not be tessellated ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>A plane through the outer loop, by Newell's method, when the loop is flat; otherwise null.</summary>
    private static PlaneSurface? FitPlane(List<FaceLoop> loops)
    {
        var outer = loops[0].Points;
        double nx = 0, ny = 0, nz = 0;
        for (var i = 0; i < outer.Count; i++)
        {
            var a = outer[i];
            var b = outer[(i + 1) % outer.Count];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }

        var normal = new V3(nx, ny, nz);
        if (normal.Length < 1e-14)
        {
            return null;
        }

        var centroid = V3.Zero;
        foreach (var p in outer)
        {
            centroid += p;
        }

        centroid *= 1.0 / outer.Count;
        return new PlaneSurface { F = Frame.From(centroid, normal.Normalized(), null) };
    }

    private FaceMesh Planar(PlaneSurface plane, List<FaceLoop> loops, bool sameSense)
    {
        var mesh = new FaceMesh();
        var uv = new List<(double X, double Y)>();
        var rings = new List<int[]>();
        var positions = new List<V3>();
        foreach (var loop in loops)
        {
            var start = uv.Count;
            foreach (var p in loop.Points)
            {
                var (u, v) = plane.Project(p);
                uv.Add((u, v));
                positions.Add(p);
            }

            rings.Add(Enumerable.Range(start, loop.Points.Count).ToArray());
        }

        var triangles = Triangulator.TriangulateCancellable(uv, rings, _cancel);
        var normal = sameSense ? plane.F.Z : -plane.F.Z;
        var flip = !sameSense;
        for (var i = 0; i < positions.Count; i++)
        {
            mesh.Positions.Add(positions[i]);
            mesh.Normals.Add(normal);
        }

        for (var i = 0; i < triangles.Length; i += 3)
        {
            if (flip)
            {
                mesh.Indices.AddRange([triangles[i], triangles[i + 2], triangles[i + 1]]);
            }
            else
            {
                mesh.Indices.AddRange([triangles[i], triangles[i + 1], triangles[i + 2]]);
            }
        }

        return mesh;
    }

    private const double TwoPi = 2 * Math.PI;

    private sealed class UvLoop
    {
        public List<V3> Points = new();
        public List<(double U, double V)> Uv = new();
        public int WindingU;
        public int WindingV;
    }

    private FaceMesh? Curved(Surface surface, List<FaceLoop> loops, bool sameSense)
    {
        // 1. Parameter-space loops, unwrapped along each loop.
        var uvLoops = new List<UvLoop>();
        foreach (var loop in loops)
        {
            var result = new UvLoop { Points = loop.Points };
            (double U, double V)? previous = null;
            for (var k = 0; k < loop.Points.Count; k++)
            {
                // Parameter-space boundaries (IGES) arrive with their parameters; 3D boundaries are projected onto the surface.
                var uv = loop.Uv is { } given && k < given.Count ? given[k] : surface.Project(loop.Points[k], previous);
                if (previous is { } prev)
                {
                    if (surface.PeriodicU)
                    {
                        uv.U = prev.U + Wrap(uv.U - prev.U);
                    }

                    if (surface.PeriodicV)
                    {
                        uv.V = prev.V + Wrap(uv.V - prev.V);
                    }
                }

                result.Uv.Add(uv);
                previous = uv;
            }

            var first = result.Uv[0];
            var last = result.Uv[^1];
            if (surface.PeriodicU)
            {
                result.WindingU = (int)Math.Round((last.U + Wrap(first.U - last.U) - first.U) / TwoPi);
            }

            if (surface.PeriodicV)
            {
                result.WindingV = (int)Math.Round((last.V + Wrap(first.V - last.V) - first.V) / TwoPi);
            }

            uvLoops.Add(result);
        }

        if (uvLoops.Any(l => l.WindingV != 0))
        {
            Warnings.Add("A face winds around the tube of a torus-like surface and was skipped.");
            return null;
        }

        var polygons = BuildPolygons(surface, uvLoops, sameSense);
        if (polygons is null)
        {
            return null;
        }

        // 2. Points: the boundary (with exact 3D positions) and interior samples.
        var uvPoints = new List<(double X, double Y)>();
        var positions = new List<V3?>();
        var rings = new List<int[]>();
        foreach (var polygon in polygons)
        {
            var start = uvPoints.Count;
            foreach (var (u, v, p) in polygon)
            {
                uvPoints.Add((u, v));
                positions.Add(p);
            }

            rings.Add(Enumerable.Range(start, polygon.Count).ToArray());
        }

        var interior = InteriorPoints(surface, uvPoints, rings);
        foreach (var (u, v) in interior)
        {
            uvPoints.Add((u, v));
            positions.Add(null);
        }

        var triangles = Triangulator.TriangulateCancellable(uvPoints, rings, _cancel);
        var mesh = new FaceMesh();
        for (var i = 0; i < uvPoints.Count; i++)
        {
            var (u, v) = uvPoints[i];
            var p = positions[i] ?? surface.At(u, v);
            var n = surface.Normal(u, v);
            mesh.Positions.Add(p);
            mesh.Normals.Add(sameSense ? n : -n);
        }

        for (var i = 0; i < triangles.Length; i += 3)
        {
            if (sameSense)
            {
                mesh.Indices.AddRange([triangles[i], triangles[i + 1], triangles[i + 2]]);
            }
            else
            {
                mesh.Indices.AddRange([triangles[i], triangles[i + 2], triangles[i + 1]]);
            }
        }

        return mesh;
    }

    private static double Wrap(double delta)
    {
        delta %= TwoPi;
        if (delta > Math.PI)
        {
            delta -= TwoPi;
        }
        else if (delta < -Math.PI)
        {
            delta += TwoPi;
        }

        return delta;
    }

    /// <summary>
    /// The parameter-space polygons of the face: loops that do not wind around the axis stand alone (shifted by whole periods so that
    /// they sit next to each other); two loops winding in opposite senses are joined into one polygon across the seam; a single winding
    /// loop is closed through the pole. Each vertex carries its exact 3D position.
    /// </summary>
    private List<List<(double U, double V, V3? P)>>? BuildPolygons(Surface surface, List<UvLoop> loops, bool sameSense)
    {
        var winding = loops.Where(l => l.WindingU != 0).ToList();
        var plain = loops.Where(l => l.WindingU == 0).ToList();
        var polygons = new List<List<(double, double, V3?)>>();

        List<(double, double, V3?)> ToPolygon(UvLoop l, double shift = 0) =>
            l.Uv.Select((uv, i) => (uv.U + shift, uv.V, (V3?)l.Points[i])).ToList();

        // Centre of the first stand-alone (or winding) loop in u, for aligning the others.
        double reference;
        if (winding.Count == 0)
        {
            reference = plain.Count > 0 ? plain[0].Uv.Average(p => p.U) : 0;
        }
        else
        {
            reference = winding[0].Uv.Min(p => p.U) + Math.PI;
        }

        if (winding.Count == 0)
        {
            foreach (var l in plain)
            {
                var centre = l.Uv.Average(p => p.U);
                var shift = surface.PeriodicU ? TwoPi * Math.Round((reference - centre) / TwoPi) : 0;
                polygons.Add(ToPolygon(l, shift));
            }

            return polygons;
        }

        if (winding.Count == 2 && winding[0].WindingU == -winding[1].WindingU)
        {
            var a = winding[0].WindingU > 0 ? winding[0] : winding[1]; // increasing u
            var b = winding[0].WindingU > 0 ? winding[1] : winding[0]; // decreasing u
            var aPoints = Rotate(a, 0);
            var u0 = aPoints[0].U;
            var aSeam = aPoints[0].U + TwoPi;
            // Start b where its u (mod 2 pi) is closest to the seam, unwrapped to run from u0 + 2 pi downwards.
            var best = 0;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < b.Uv.Count; i++)
            {
                var d = Math.Abs(Wrap(b.Uv[i].U - u0));
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }

            var bPoints = Rotate(b, best);
            var shift = TwoPi * Math.Round((aSeam - bPoints[0].U) / TwoPi);
            for (var i = 0; i < bPoints.Count; i++)
            {
                bPoints[i] = (bPoints[i].U + shift, bPoints[i].V, bPoints[i].P);
            }

            var joined = new List<(double, double, V3?)>(aPoints.Select(p => (p.U, p.V, p.P)));
            joined.Add((aPoints[0].U + TwoPi, aPoints[0].V, aPoints[0].P));
            joined.AddRange(bPoints.Select(p => (p.U, p.V, p.P)));
            joined.Add((bPoints[0].U - TwoPi, bPoints[0].V, bPoints[0].P));
            // The joined loop closes by the edge from the last point back to the first: both lie on the u0 side of the strip.
            polygons.Add(joined);
            foreach (var l in plain)
            {
                var centre = l.Uv.Average(p => p.U);
                var holeShift = TwoPi * Math.Round((u0 + Math.PI - centre) / TwoPi);
                polygons.Add(ToPolygon(l, holeShift));
            }

            return polygons;
        }

        if (winding.Count == 1 && surface.PoleV.Length > 0)
        {
            var l = winding[0];
            var points = Rotate(l, 0);
            var meanV = l.Uv.Average(p => p.V);
            // The interior is on the left of travel when the face agrees with the surface, otherwise on the right; moving along +u,
            // left is the +v side.
            var towardsPositive = (l.WindingU > 0) == sameSense;
            var pole = surface.PoleV.Where(v => towardsPositive ? v > meanV : v < meanV).OrderBy(v => Math.Abs(v - meanV)).Cast<double?>().FirstOrDefault();
            if (pole is { } pv)
            {
                var poleP = surface.At(points[0].U, pv);
                var loop = new List<(double, double, V3?)>(points.Select(p => (p.U, p.V, p.P)))
                {
                    (points[0].U + TwoPi, points[0].V, points[0].P),
                    (points[0].U + TwoPi, pv, poleP),
                    (points[0].U, pv, poleP),
                };
                polygons.Add(loop);
                foreach (var h in plain)
                {
                    var centre = h.Uv.Average(p => p.U);
                    polygons.Add(ToPolygon(h, TwoPi * Math.Round((points[0].U + Math.PI - centre) / TwoPi)));
                }

                return polygons;
            }
        }

        Warnings.Add("A face has boundaries winding around its surface in a combination that is not handled and was skipped.");
        return null;
    }

    /// <summary>The loop starting at <paramref name="start"/>, with u unwrapped monotonically from there.</summary>
    private static List<(double U, double V, V3? P)> Rotate(UvLoop loop, int start)
    {
        var n = loop.Uv.Count;
        var result = new List<(double U, double V, V3? P)>(n);
        var offset = 0.0;
        var previous = loop.Uv[start].U;
        for (var k = 0; k < n; k++)
        {
            var i = (start + k) % n;
            var u = loop.Uv[i].U;
            var cur = u + offset;
            // Within a rotated copy the unwrapped sequence may jump by the loop's total winding where the original closed.
            while (cur - previous > Math.PI)
            {
                offset -= TwoPi;
                cur -= TwoPi;
            }

            while (previous - cur > Math.PI)
            {
                offset += TwoPi;
                cur += TwoPi;
            }

            result.Add((cur, loop.Uv[i].V, loop.Points[i]));
            previous = cur;
        }

        return result;
    }

    private List<(double U, double V)> InteriorPoints(Surface surface, List<(double X, double Y)> boundary, List<int[]> rings)
    {
        var points = new List<(double, double)>();
        var (stepU, stepV) = surface.InteriorStep(_angular);
        if (stepU <= 0 && stepV <= 0)
        {
            return points;
        }

        if (stepU <= 0)
        {
            stepU = double.PositiveInfinity;
        }

        if (stepV <= 0)
        {
            stepV = double.PositiveInfinity;
        }

        // Refine the angular step for chord error on small radii.
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var (x, y) in boundary)
        {
            minU = Math.Min(minU, x);
            maxU = Math.Max(maxU, x);
            minV = Math.Min(minV, y);
            maxV = Math.Max(maxV, y);
        }

        var cols = double.IsInfinity(stepU) ? 1 : Math.Max(1, (int)Math.Ceiling((maxU - minU) / stepU));
        var rowsCount = double.IsInfinity(stepV) ? 1 : Math.Max(1, (int)Math.Ceiling((maxV - minV) / stepV));
        if ((long)cols * rowsCount > 40_000)
        {
            var scale = Math.Sqrt(40_000.0 / ((long)cols * rowsCount));
            cols = Math.Max(1, (int)(cols * scale));
            rowsCount = Math.Max(1, (int)(rowsCount * scale));
        }

        var du = (maxU - minU) / cols;
        var dv = (maxV - minV) / rowsCount;
        var margin = 0.35 * Math.Min(du > 0 ? du : double.MaxValue, dv > 0 ? dv : double.MaxValue);
        var segments = new List<(double X1, double Y1, double X2, double Y2)>();
        foreach (var ring in rings)
        {
            for (var k = 0; k < ring.Length; k++)
            {
                var a = boundary[ring[k]];
                var b = boundary[ring[(k + 1) % ring.Length]];
                segments.Add((a.X, a.Y, b.X, b.Y));
            }
        }

        for (var i = 1; i < cols || (cols == 1 && i == 1 && false); i++)
        {
            for (var j = 1; j < rowsCount; j++)
            {
                var u = minU + du * i;
                var v = minV + dv * j;
                if (Inside(u, v, segments) && DistanceToBoundary(u, v, segments) > margin)
                {
                    points.Add((u, v));
                }
            }
        }

        // A surface that curves in one direction only still needs rows or columns of samples across the other.
        return points;
    }

    private static bool Inside(double x, double y, List<(double X1, double Y1, double X2, double Y2)> segments)
    {
        var inside = false;
        foreach (var (x1, y1, x2, y2) in segments)
        {
            if ((y1 > y) != (y2 > y) && x < (x2 - x1) * (y - y1) / (y2 - y1) + x1)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double DistanceToBoundary(double x, double y, List<(double X1, double Y1, double X2, double Y2)> segments)
    {
        var best = double.MaxValue;
        foreach (var (x1, y1, x2, y2) in segments)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var l2 = dx * dx + dy * dy;
            var t = l2 > 0 ? Math.Clamp(((x - x1) * dx + (y - y1) * dy) / l2, 0, 1) : 0;
            var px = x1 + t * dx - x;
            var py = y1 + t * dy - y;
            best = Math.Min(best, px * px + py * py);
        }

        return Math.Sqrt(best);
    }
}
