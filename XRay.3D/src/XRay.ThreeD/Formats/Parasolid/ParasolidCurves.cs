using XRay.ThreeD.Formats.Step;

namespace XRay.ThreeD.Formats.Parasolid;

/// <summary>A curve given by a two dimensional B-spline in the parameter space of a surface (a Parasolid SP_CURVE): its points are the surface evaluated along it.</summary>
internal sealed class SurfaceParamCurve : Curve
{
    public required Surface Surface { get; init; }

    /// <summary>The curve in the (u, v) plane of the surface: the x and y of its points are u and v.</summary>
    public required SplineCurve Uv { get; init; }

    public override (double Min, double Max)? Range => Uv.Range;

    public override V3 At(double t)
    {
        var q = Uv.At(t);
        return Surface.At(q.X, q.Y);
    }

    public override double Project(V3 p, double? hint = null)
    {
        var (min, max) = Uv.Range!.Value;
        var samples = Math.Max(64, Uv.Control.Length * 8);
        var best = min;
        var bestDistance = double.MaxValue;
        for (var i = 0; i <= samples; i++)
        {
            var t = min + (max - min) * i / samples;
            var d = (At(t) - p).Length;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = t;
            }
        }

        var lo = Math.Max(min, best - (max - min) / samples);
        var hi = Math.Min(max, best + (max - min) / samples);
        for (var i = 0; i < 40; i++)
        {
            var m1 = lo + (hi - lo) / 3;
            var m2 = hi - (hi - lo) / 3;
            if ((At(m1) - p).Length < (At(m2) - p).Length)
            {
                hi = m2;
            }
            else
            {
                lo = m1;
            }
        }

        return (lo + hi) / 2;
    }

    public override List<V3> Sample(double t0, double t1, double linearTolerance, double angularTolerance)
    {
        var (min, max) = Uv.Range!.Value;
        var reverse = t1 < t0;
        var (a, b) = reverse ? (t1, t0) : (t0, t1);
        a = Math.Clamp(a, min, max);
        b = Math.Clamp(b, min, max);
        var spans = Math.Max(1, Uv.Control.Length - Uv.Degree);
        var segments = Math.Clamp(spans * Math.Max(Uv.Degree, 2) * 4, 8, 800);
        segments = Math.Max(2, (int)Math.Ceiling(segments * (b - a) / Math.Max(max - min, 1e-300)));
        var points = new List<V3>(segments + 1);
        for (var i = 0; i <= segments; i++)
        {
            points.Add(At(a + (b - a) * i / segments));
        }

        if (reverse)
        {
            points.Reverse();
        }

        return points;
    }
}
