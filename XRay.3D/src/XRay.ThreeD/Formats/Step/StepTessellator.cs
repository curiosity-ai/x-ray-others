using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Step;

/// <summary>The triangles of one face: vertex arrays (not shared with other faces) and counter-clockwise triangles seen from outside.</summary>
internal sealed class FaceMesh
{
    public readonly List<V3> Positions = new();
    public readonly List<V3> Normals = new();
    public readonly List<int> Indices = new();
}

/// <summary>
/// Tessellates B-rep faces: edges are sampled once and shared by the faces that use them (so adjacent faces meet exactly), each face is
/// mapped into the parameter space of its surface, triangulated there with a constrained Delaunay triangulation (with interior sample
/// points for surfaces that curve in both directions) and mapped back, with analytic normals.
/// </summary>
internal sealed class StepTessellator
{
    private readonly StepFile _file;
    private readonly StepEntities _geo;
    private readonly double _linear;
    private readonly double _angular;
    private readonly Dictionary<int, List<V3>> _edges = new();

    public List<string> Warnings { get; } = new();


    public StepTessellator(StepFile file, StepEntities geo, double linearTolerance, double angularTolerance)
    {
        _file = file;
        _geo = geo;
        _linear = linearTolerance;
        _angular = angularTolerance;
    }

    // ---- edges and loops -----------------------------------------------------------------------------------------

    /// <summary>The sample points of an EDGE_CURVE from its start vertex to its end vertex.</summary>
    private List<V3> EdgePoints(int edgeId)
    {
        if (_edges.TryGetValue(edgeId, out var cached))
        {
            return cached;
        }

        var e = _file.Get(edgeId);
        var points = new List<V3>();
        if (e is { Type: "EDGE_CURVE" })
        {
            var p0 = _geo.Point(e.Arg(1).ToRef);
            var p1 = _geo.Point(e.Arg(2).ToRef);
            var same = e.Arg(4).Bool;
            var curve = _geo.GetCurve(e.Arg(3).ToRef);
            if (curve is null || (p0 - p1).Length < 1e-12 && curve.Period == 0 && curve is not SplineCurve)
            {
                points = [p0, p1];
            }
            else
            {
                var t0 = curve.Project(p0);
                var t1 = curve.Project(p1);
                var period = curve.Period;
                var closed = (p0 - p1).Length < 1e-9 * Math.Max(1, p0.Length);
                if (period > 0)
                {
                    if (same)
                    {
                        while (t1 <= t0 + 1e-9)
                        {
                            t1 += period;
                        }

                        if (closed && t1 - t0 < period / 2)
                        {
                            t1 = t0 + period;
                        }
                    }
                    else
                    {
                        while (t1 >= t0 - 1e-9)
                        {
                            t1 -= period;
                        }

                        if (closed && t0 - t1 < period / 2)
                        {
                            t1 = t0 - period;
                        }
                    }
                }

                points = curve.Sample(t0, t1, _linear, _angular);
                if (points.Count >= 2)
                {
                    points[0] = p0;
                    points[^1] = p1;
                }
            }
        }

        _edges[edgeId] = points;
        return points;
    }

    private List<V3> LoopPoints(int loopId, bool reverse)
    {
        var loop = _file.Get(loopId);
        var points = new List<V3>();
        if (loop is null)
        {
            return points;
        }

        switch (loop.Type)
        {
            case "EDGE_LOOP":
                foreach (var oriented in loop.Arg(1).Refs)
                {
                    var oe = _file.Get(oriented);
                    if (oe is null)
                    {
                        continue;
                    }

                    // ORIENTED_EDGE(name, *, *, edge_element, orientation); the edge element may itself be an EDGE_CURVE.
                    var edgeId = oe.Arg(3).ToRef;
                    var forward = oe.Arg(4).Bool;
                    var sample = EdgePoints(edgeId);
                    if (sample.Count == 0)
                    {
                        continue;
                    }

                    IEnumerable<V3> ordered = forward ? sample : Enumerable.Reverse(sample);
                    var first = true;
                    foreach (var p in ordered)
                    {
                        if (first && points.Count > 0 && (points[^1] - p).Length < 1e-9)
                        {
                            first = false;
                            continue;
                        }

                        first = false;
                        points.Add(p);
                    }
                }

                break;
            case "POLY_LOOP":
                points.AddRange(loop.Arg(1).Refs.Select(_geo.Point));
                break;
            case "VERTEX_LOOP":
                break;
        }

        if (points.Count > 1 && (points[0] - points[^1]).Length < 1e-9)
        {
            points.RemoveAt(points.Count - 1);
        }

        if (reverse)
        {
            points.Reverse();
        }

        return points;
    }

    /// <summary>The loops of a face's bounds as 3D polylines; the outer bound (if marked) is first.</summary>
    private List<List<V3>> FaceLoops(StepEntity face, out bool outerFirst)
    {
        var loops = new List<List<V3>>();
        outerFirst = false;
        var bounds = face.Arg(1).Refs.ToList();
        foreach (var id in bounds)
        {
            var bound = _file.Get(id);
            if (bound is null || bound.Type is not ("FACE_BOUND" or "FACE_OUTER_BOUND"))
            {
                continue;
            }

            var points = LoopPoints(bound.Arg(1).ToRef, !bound.Arg(2).Bool);
            if (points.Count < 3)
            {
                continue;
            }

            if (bound.Type == "FACE_OUTER_BOUND" && loops.Count > 0)
            {
                loops.Insert(0, points);
                outerFirst = true;
            }
            else
            {
                loops.Add(points);
                outerFirst = outerFirst || bound.Type == "FACE_OUTER_BOUND";
            }
        }

        return loops;
    }

    // ---- faces -----------------------------------------------------------------------------------------------------

    /// <summary>Cancellation of the load this tessellator works for; checked once per face and inside the triangulation.</summary>
    public CancellationToken Cancel { get; init; }

    private SurfaceFaceTessellator Faces => _faces ??= new SurfaceFaceTessellator(_angular, Cancel, Warnings);

    private SurfaceFaceTessellator? _faces;

    /// <summary>Tessellates an ADVANCED_FACE, FACE_SURFACE or FACE. Returns null (with a warning) when the face cannot be handled.</summary>
    public FaceMesh? TessellateFace(StepEntity face)
    {
        Cancel.ThrowIfCancellationRequested();
        var loops = FaceLoops(face, out _);
        if (loops.Count == 0)
        {
            return null;
        }

        var sameSense = face.Type == "FACE" || face.Arg(3).IsNull ? true : face.Arg(3).Bool;
        Surface? surface = null;
        var advanced = face.Type is "ADVANCED_FACE" or "FACE_SURFACE";
        if (advanced)
        {
            surface = _geo.GetSurface(face.Arg(2).ToRef);
            sameSense = face.Arg(3).Bool;
        }

        return Faces.Tessellate(surface, loops.Select(l => new FaceLoop(l)).ToList(), sameSense, $"Face #{face.Id}", approximatedWarning: advanced);
    }
}
