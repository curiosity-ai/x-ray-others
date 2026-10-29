using System.Globalization;

namespace XRay.ThreeD.Formats.Step;

/// <summary>Turns STEP geometry entities (points, directions, placements, curves, surfaces) into the objects of <c>StepGeometry</c>. Results are cached by entity id.</summary>
internal sealed class StepEntities
{
    private readonly StepFile _file;
    private readonly Dictionary<int, V3> _points = new();
    private readonly Dictionary<int, Frame> _frames = new();
    private readonly Dictionary<int, Curve?> _curves = new();
    private readonly Dictionary<int, Surface?> _surfaces = new();
    private readonly HashSet<int> _building = new();

    public StepEntities(StepFile file)
    {
        _file = file;
    }

    public V3 Point(int id)
    {
        if (_points.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var e = _file.Get(id);
        V3 result = V3.Zero;
        if (e is not null && e.Args.Count >= 2)
        {
            // CARTESIAN_POINT(name, (x, y, z)); a VERTEX_POINT stands for the point it refers to.
            if (e.Type == "VERTEX_POINT")
            {
                result = Point(e.Arg(1).ToRef);
            }
            else if (e.Arg(1).Type == StepArg.Kind.List)
            {
                var c = e.Arg(1).Items;
                result = new V3(c.Count > 0 ? c[0].Num : 0, c.Count > 1 ? c[1].Num : 0, c.Count > 2 ? c[2].Num : 0);
            }
        }

        _points[id] = result;
        return result;
    }

    /// <summary>DIRECTION or VECTOR (orientation scaled by magnitude is returned as the direction only).</summary>
    public V3 Direction(int id)
    {
        var e = _file.Get(id);
        if (e is null)
        {
            return new V3(0, 0, 1);
        }

        if (e.Type == "VECTOR")
        {
            return Direction(e.Arg(1).ToRef);
        }

        var c = e.Arg(1).Items;
        return new V3(c.Count > 0 ? c[0].Num : 0, c.Count > 1 ? c[1].Num : 0, c.Count > 2 ? c[2].Num : 0).Normalized();
    }

    public double VectorMagnitude(int id) => _file.Get(id) is { Type: "VECTOR" } v ? v.Arg(2).Num : 1;

    public Frame Axis2(int id)
    {
        if (_frames.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var e = _file.Get(id);
        Frame frame;
        if (e is null || e.Type is not ("AXIS2_PLACEMENT_3D" or "AXIS1_PLACEMENT" or "AXIS2_PLACEMENT_2D"))
        {
            frame = new Frame();
        }
        else
        {
            var location = Point(e.Arg(1).ToRef);
            V3? axis = e.Arg(2).IsRef ? Direction(e.Arg(2).ToRef) : null;
            V3? reference = e.Type == "AXIS2_PLACEMENT_3D" && e.Arg(3).IsRef ? Direction(e.Arg(3).ToRef) : e.Type == "AXIS2_PLACEMENT_2D" && e.Arg(2).IsRef ? Direction(e.Arg(2).ToRef) : null;
            frame = e.Type == "AXIS2_PLACEMENT_2D" ? Frame.From(location, null, reference) : Frame.From(location, axis, reference);
        }

        _frames[id] = frame;
        return frame;
    }

    // ---- curves ----------------------------------------------------------------------------------------------------

    public Curve? GetCurve(int id)
    {
        if (_curves.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (!_building.Add(id))
        {
            return null;
        }

        Curve? curve = null;
        try
        {
            curve = BuildCurve(_file.Get(id));
        }
        finally
        {
            _building.Remove(id);
        }

        _curves[id] = curve;
        return curve;
    }

    private Curve? BuildCurve(StepEntity? e)
    {
        if (e is null)
        {
            return null;
        }

        if (e.IsComplex && e.Is("B_SPLINE_CURVE"))
        {
            return Spline(e);
        }

        switch (e.Type)
        {
            case "LINE":
                {
                    var p = Point(e.Arg(1).ToRef);
                    var v = e.Arg(2).ToRef;
                    return new LineCurve { P = p, D = Direction(v) };
                }

            case "CIRCLE":
                return new EllipseCurve { F = Axis2(e.Arg(1).ToRef), A = e.Arg(2).Num, B = e.Arg(2).Num };
            case "ELLIPSE":
                return new EllipseCurve { F = Axis2(e.Arg(1).ToRef), A = e.Arg(2).Num, B = e.Arg(3).Num };
            case "B_SPLINE_CURVE_WITH_KNOTS":
            case "QUASI_UNIFORM_CURVE":
            case "UNIFORM_CURVE":
            case "BEZIER_CURVE":
                return Spline(e);
            case "POLYLINE":
                return new PolylineCurve { Points = e.Arg(1).Refs.Select(Point).ToList() };
            case "SURFACE_CURVE":
            case "SEAM_CURVE":
            case "INTERSECTION_CURVE":
            case "BOUNDED_SURFACE_CURVE":
                return GetCurve(e.Arg(1).ToRef);
            case "TRIMMED_CURVE":
                return GetCurve(e.Arg(1).ToRef);
            case "COMPOSITE_CURVE":
                return null;
            default:
                return null;
        }
    }

    private SplineCurve Spline(StepEntity e)
    {
        // Simple entity: (name, degree, control points, form, closed, self_intersect, [multiplicities, knots, knot_spec]).
        // Complex entity: B_SPLINE_CURVE(degree, points, form, closed, self), B_SPLINE_CURVE_WITH_KNOTS(mult, knots, spec), RATIONAL_B_SPLINE_CURVE(weights).
        IReadOnlyList<StepArg> head = e.IsComplex ? e.ArgsOf("B_SPLINE_CURVE") : e.Args;
        var offset = e.IsComplex ? -1 : 0; // the complex part has no name
        var degree = (int)head[1 + offset].Num;
        var control = head[2 + offset].Items.Select(i => Point(i.ToRef)).ToArray();
        IReadOnlyList<StepArg> knotArgs = e.IsComplex ? e.ArgsOf("B_SPLINE_CURVE_WITH_KNOTS") : e.Args;
        var knotOffset = e.IsComplex ? -6 : 0;
        double[] knots;
        if (e.Is("B_SPLINE_CURVE_WITH_KNOTS") && knotArgs.Count >= 8 + knotOffset)
        {
            var multiplicities = knotArgs[6 + knotOffset].Items.Select(i => (int)i.Num).ToList();
            var values = knotArgs[7 + knotOffset].Items.Select(i => i.Num).ToList();
            knots = BSpline.Expand(values, multiplicities);
        }
        else
        {
            knots = BSpline.Clamped(control.Length, degree);
        }

        double[] weights = [];
        if (e.IsComplex && e.Is("RATIONAL_B_SPLINE_CURVE"))
        {
            weights = e.ArgsOf("RATIONAL_B_SPLINE_CURVE").FirstOrDefault()?.Items.Select(i => i.Num).ToArray() ?? [];
        }

        if (knots.Length != control.Length + degree + 1)
        {
            knots = BSpline.Clamped(control.Length, degree);
        }

        return new SplineCurve { Degree = degree, Control = control, Knots = knots, Weights = weights };
    }

    // ---- surfaces --------------------------------------------------------------------------------------------------

    public Surface? GetSurface(int id)
    {
        if (_surfaces.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (!_building.Add(-id))
        {
            return null;
        }

        Surface? surface = null;
        try
        {
            surface = BuildSurface(_file.Get(id));
        }
        finally
        {
            _building.Remove(-id);
        }

        _surfaces[id] = surface;
        return surface;
    }

    private Surface? BuildSurface(StepEntity? e)
    {
        if (e is null)
        {
            return null;
        }

        if (e.IsComplex && e.Is("B_SPLINE_SURFACE"))
        {
            return SplineSurface(e);
        }

        switch (e.Type)
        {
            case "PLANE":
                return new PlaneSurface { F = Axis2(e.Arg(1).ToRef) };
            case "CYLINDRICAL_SURFACE":
                return new CylinderSurface { F = Axis2(e.Arg(1).ToRef), R = e.Arg(2).Num };
            case "CONICAL_SURFACE":
                return new ConeSurface { F = Axis2(e.Arg(1).ToRef), R = e.Arg(2).Num, Angle = e.Arg(3).Num };
            case "SPHERICAL_SURFACE":
                return new SphereSurface { F = Axis2(e.Arg(1).ToRef), R = e.Arg(2).Num };
            case "TOROIDAL_SURFACE":
                return new TorusSurface { F = Axis2(e.Arg(1).ToRef), Major = e.Arg(2).Num, Minor = e.Arg(3).Num };
            case "B_SPLINE_SURFACE_WITH_KNOTS":
            case "UNIFORM_SURFACE":
            case "QUASI_UNIFORM_SURFACE":
            case "BEZIER_SURFACE":
                return SplineSurface(e);
            case "SURFACE_OF_REVOLUTION":
                {
                    var curve = GetCurve(e.Arg(1).ToRef);
                    var axis = Axis2(e.Arg(2).ToRef);
                    return curve is null ? null : new RevolutionSurface { Profile = curve, AxisOrigin = axis.O, AxisDirection = axis.Z };
                }

            case "SURFACE_OF_LINEAR_EXTRUSION":
                {
                    var curve = GetCurve(e.Arg(1).ToRef);
                    return curve is null ? null : new ExtrusionSurface { Profile = curve, Direction = Direction(e.Arg(2).ToRef) };
                }

            case "RECTANGULAR_TRIMMED_SURFACE":
            case "OFFSET_SURFACE":
                return GetSurface(e.Arg(1).ToRef);
            default:
                return null;
        }
    }

    private SplineSurface? SplineSurface(StepEntity e)
    {
        // B_SPLINE_SURFACE(name, u_degree, v_degree, ((control points)), form, u_closed, v_closed, self_intersect), then
        // B_SPLINE_SURFACE_WITH_KNOTS(u_mult, v_mult, u_knots, v_knots, spec); the complex form drops the name from each part.
        IReadOnlyList<StepArg> head = e.IsComplex ? e.ArgsOf("B_SPLINE_SURFACE") : e.Args;
        var offset = e.IsComplex ? -1 : 0;
        var degreeU = (int)head[1 + offset].Num;
        var degreeV = (int)head[2 + offset].Num;
        var rows = head[3 + offset].Items;
        var nu = rows.Count;
        var nv = nu > 0 ? rows[0].Items.Count : 0;
        if (nu == 0 || nv == 0)
        {
            return null;
        }

        var control = new V3[nu, nv];
        for (var i = 0; i < nu; i++)
        {
            for (var j = 0; j < nv && j < rows[i].Items.Count; j++)
            {
                control[i, j] = Point(rows[i].Items[j].ToRef);
            }
        }

        double[] knotsU, knotsV;
        if (e.Is("B_SPLINE_SURFACE_WITH_KNOTS"))
        {
            IReadOnlyList<StepArg> k = e.IsComplex ? e.ArgsOf("B_SPLINE_SURFACE_WITH_KNOTS") : e.Args;
            var ko = e.IsComplex ? -8 : 0;
            knotsU = BSpline.Expand(k[10 + ko].Items.Select(i => i.Num).ToList(), k[8 + ko].Items.Select(i => (int)i.Num).ToList());
            knotsV = BSpline.Expand(k[11 + ko].Items.Select(i => i.Num).ToList(), k[9 + ko].Items.Select(i => (int)i.Num).ToList());
        }
        else
        {
            knotsU = BSpline.Clamped(nu, degreeU);
            knotsV = BSpline.Clamped(nv, degreeV);
        }

        if (knotsU.Length != nu + degreeU + 1)
        {
            knotsU = BSpline.Clamped(nu, degreeU);
        }

        if (knotsV.Length != nv + degreeV + 1)
        {
            knotsV = BSpline.Clamped(nv, degreeV);
        }

        double[,]? weights = null;
        if (e.IsComplex && e.Is("RATIONAL_B_SPLINE_SURFACE"))
        {
            var w = e.ArgsOf("RATIONAL_B_SPLINE_SURFACE").FirstOrDefault()?.Items;
            if (w is not null && w.Count == nu)
            {
                weights = new double[nu, nv];
                for (var i = 0; i < nu; i++)
                {
                    for (var j = 0; j < nv && j < w[i].Items.Count; j++)
                    {
                        weights[i, j] = w[i].Items[j].Num;
                    }
                }
            }
        }

        return new SplineSurface { DegreeU = degreeU, DegreeV = degreeV, Control = control, KnotsU = knotsU, KnotsV = knotsV, Weights = weights };
    }
}
