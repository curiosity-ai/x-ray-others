using System.Numerics;

namespace XRay.ThreeD.Formats.Step;

/// <summary>Double-precision 3-vector (STEP files are metric-heavy and float32 would lose digits in the intermediate maths).</summary>
internal readonly record struct V3(double X, double Y, double Z)
{
    public static V3 Zero => new(0, 0, 0);

    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);

    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static V3 operator *(double s, V3 a) => a * s;

    public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;

    public V3 Cross(V3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

    public double Length => Math.Sqrt(Dot(this));

    public V3 Normalized()
    {
        var l = Length;
        return l > 1e-300 ? this * (1 / l) : this;
    }

    public Vector3 ToFloat() => new((float)X, (float)Y, (float)Z);
}

/// <summary>An orthonormal frame (AXIS2_PLACEMENT_3D): origin and the x, y, z directions.</summary>
internal sealed class Frame
{
    public V3 O;
    public V3 X = new(1, 0, 0);
    public V3 Y = new(0, 1, 0);
    public V3 Z = new(0, 0, 1);

    public static Frame From(V3 origin, V3? axis, V3? reference)
    {
        var z = (axis ?? new V3(0, 0, 1)).Normalized();
        if (z.Length < 0.5)
        {
            z = new V3(0, 0, 1);
        }

        V3 x;
        if (reference is { } r && r.Length > 1e-12)
        {
            x = (r - z * r.Dot(z));
            if (x.Length < 1e-9)
            {
                x = Perpendicular(z);
            }
        }
        else
        {
            x = Perpendicular(z);
        }

        x = x.Normalized();
        return new Frame { O = origin, X = x, Y = z.Cross(x).Normalized(), Z = z };
    }

    public static V3 Perpendicular(V3 z) => Math.Abs(z.X) < 0.9 ? new V3(1, 0, 0) - z * z.X : new V3(0, 1, 0) - z * z.Y;

    public V3 ToWorld(double x, double y, double z) => O + X * x + Y * y + Z * z;

    public (double X, double Y, double Z) ToLocal(V3 p)
    {
        var d = p - O;
        return (d.Dot(X), d.Dot(Y), d.Dot(Z));
    }

    /// <summary>The frame as a System.Numerics matrix (row vectors): local coordinates to world.</summary>
    public Matrix4x4 ToMatrix() => new(
        (float)X.X, (float)X.Y, (float)X.Z, 0,
        (float)Y.X, (float)Y.Y, (float)Y.Z, 0,
        (float)Z.X, (float)Z.Y, (float)Z.Z, 0,
        (float)O.X, (float)O.Y, (float)O.Z, 1);
}

// ---- B-splines ---------------------------------------------------------------------------------------------------

internal static class BSpline
{
    /// <summary>Knot vector from (value, multiplicity) pairs.</summary>
    public static double[] Expand(IReadOnlyList<double> knots, IReadOnlyList<int> multiplicities)
    {
        var result = new List<double>();
        for (var i = 0; i < knots.Count; i++)
        {
            for (var m = 0; m < (i < multiplicities.Count ? multiplicities[i] : 1); m++)
            {
                result.Add(knots[i]);
            }
        }

        return result.ToArray();
    }

    /// <summary>A clamped uniform knot vector for the "quasi-uniform"/Bezier forms that carry no knots.</summary>
    public static double[] Clamped(int controlPoints, int degree)
    {
        var knots = new List<double>();
        for (var i = 0; i <= degree; i++)
        {
            knots.Add(0);
        }

        var inner = controlPoints - degree - 1;
        for (var i = 1; i <= inner; i++)
        {
            knots.Add(i);
        }

        for (var i = 0; i <= degree; i++)
        {
            knots.Add(inner + 1);
        }

        return knots.ToArray();
    }

    public static int FindSpan(int n, int degree, double u, double[] knots)
    {
        if (u >= knots[n + 1])
        {
            return n;
        }

        if (u <= knots[degree])
        {
            return degree;
        }

        int low = degree, high = n + 1, mid = (low + high) / 2;
        while (u < knots[mid] || u >= knots[mid + 1])
        {
            if (u < knots[mid])
            {
                high = mid;
            }
            else
            {
                low = mid;
            }

            mid = (low + high) / 2;
        }

        return mid;
    }

    public static void Basis(int span, double u, int degree, double[] knots, Span<double> result)
    {
        Span<double> left = degree < 32 ? stackalloc double[degree + 1] : new double[degree + 1];
        Span<double> right = degree < 32 ? stackalloc double[degree + 1] : new double[degree + 1];
        result[0] = 1;
        for (var j = 1; j <= degree; j++)
        {
            left[j] = u - knots[span + 1 - j];
            right[j] = knots[span + j] - u;
            var saved = 0.0;
            for (var r = 0; r < j; r++)
            {
                var denominator = right[r + 1] + left[j - r];
                var temp = Math.Abs(denominator) < 1e-300 ? 0 : result[r] / denominator;
                result[r] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }

            result[j] = saved;
        }
    }
}

// ---- curves -------------------------------------------------------------------------------------------------------

internal abstract class Curve
{
    public abstract V3 At(double t);

    /// <summary>The parameter of the point on the curve nearest to <paramref name="p"/>, near <paramref name="hint"/> when given.</summary>
    public abstract double Project(V3 p, double? hint = null);

    /// <summary>Period of a closed analytic curve (2 pi for circles), or 0.</summary>
    public virtual double Period => 0;

    /// <summary>The parameter range for curves with a natural one (B-splines).</summary>
    public virtual (double Min, double Max)? Range => null;

    /// <summary>Points from parameter t0 to t1 (either direction) with the given deflections.</summary>
    public abstract List<V3> Sample(double t0, double t1, double linearTolerance, double angularTolerance);
}

internal sealed class LineCurve : Curve
{
    public V3 P;
    public V3 D; // unit

    public override V3 At(double t) => P + D * t;

    public override double Project(V3 p, double? hint = null) => (p - P).Dot(D);

    public override List<V3> Sample(double t0, double t1, double linearTolerance, double angularTolerance) => [At(t0), At(t1)];
}

internal sealed class EllipseCurve : Curve
{
    public Frame F = new();
    public double A;
    public double B;

    public override double Period => 2 * Math.PI;

    public override V3 At(double t) => F.O + F.X * (A * Math.Cos(t)) + F.Y * (B * Math.Sin(t));

    public override double Project(V3 p, double? hint = null)
    {
        var (x, y, _) = F.ToLocal(p);
        return Math.Atan2(y / Math.Max(B, 1e-300), x / Math.Max(A, 1e-300));
    }

    public override List<V3> Sample(double t0, double t1, double linearTolerance, double angularTolerance)
    {
        var sweep = Math.Abs(t1 - t0);
        var radius = Math.Max(A, B);
        var step = Math.Min(angularTolerance, radius > linearTolerance ? 2 * Math.Acos(Math.Clamp(1 - linearTolerance / radius, -1, 1)) : angularTolerance);
        var segments = Math.Max(2, (int)Math.Ceiling(sweep / Math.Max(step, 1e-3)));
        segments = Math.Min(segments, 2048);
        var points = new List<V3>(segments + 1);
        for (var i = 0; i <= segments; i++)
        {
            points.Add(At(t0 + (t1 - t0) * i / segments));
        }

        return points;
    }
}

internal sealed class SplineCurve : Curve
{
    public int Degree;
    public V3[] Control = [];
    public double[] Weights = [];
    public double[] Knots = [];

    public override (double Min, double Max)? Range => (Knots[Degree], Knots[Control.Length]);

    public override V3 At(double t)
    {
        var n = Control.Length - 1;
        var span = BSpline.FindSpan(n, Degree, t, Knots);
        Span<double> basis = Degree < 32 ? stackalloc double[Degree + 1] : new double[Degree + 1];
        BSpline.Basis(span, t, Degree, Knots, basis);
        double x = 0, y = 0, z = 0, w = 0;
        for (var j = 0; j <= Degree; j++)
        {
            var index = span - Degree + j;
            var weight = basis[j] * (Weights.Length > index ? Weights[index] : 1);
            x += weight * Control[index].X;
            y += weight * Control[index].Y;
            z += weight * Control[index].Z;
            w += weight;
        }

        return Math.Abs(w) > 1e-300 ? new V3(x / w, y / w, z / w) : Control[span];
    }

    public override double Project(V3 p, double? hint = null)
    {
        var (min, max) = Range!.Value;
        var best = min;
        var bestDistance = double.MaxValue;
        var samples = Math.Max(32, Control.Length * 4);
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

        // Golden-section refinement around the best sample.
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
        var spans = Math.Max(1, Control.Length - Degree);
        var segments = Math.Clamp(spans * Math.Max(Degree, 2) * 3, 8, 400);
        var reverse = t1 < t0;
        var (a, b) = reverse ? (t1, t0) : (t0, t1);
        var range = Range!.Value;
        a = Math.Clamp(a, range.Min, range.Max);
        b = Math.Clamp(b, range.Min, range.Max);
        var fraction = (b - a) / Math.Max(range.Max - range.Min, 1e-300);
        segments = Math.Max(2, (int)Math.Ceiling(segments * fraction));
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

internal sealed class PolylineCurve : Curve
{
    public List<V3> Points = [];

    public override (double Min, double Max)? Range => Points.Count > 1 ? (0, Points.Count - 1) : null;

    public override V3 At(double t)
    {
        var i = Math.Clamp((int)Math.Floor(t), 0, Points.Count - 2);
        var f = Math.Clamp(t - i, 0, 1);
        return Points[i] + (Points[i + 1] - Points[i]) * f;
    }

    public override double Project(V3 p, double? hint = null)
    {
        var best = 0.0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i + 1 < Points.Count; i++)
        {
            var d = Points[i + 1] - Points[i];
            var l2 = Math.Max(d.Dot(d), 1e-300);
            var f = Math.Clamp((p - Points[i]).Dot(d) / l2, 0, 1);
            var distance = (Points[i] + d * f - p).Length;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i + f;
            }
        }

        return best;
    }

    public override List<V3> Sample(double t0, double t1, double linearTolerance, double angularTolerance)
    {
        var result = new List<V3> { At(t0) };
        var forward = t1 >= t0;
        var from = Math.Min(t0, t1);
        var to = Math.Max(t0, t1);
        var inner = new List<V3>();
        for (var i = (int)Math.Floor(from) + 1; i < to; i++)
        {
            inner.Add(Points[Math.Clamp(i, 0, Points.Count - 1)]);
        }

        if (!forward)
        {
            inner.Reverse();
        }

        result.AddRange(inner);
        result.Add(At(t1));
        return result;
    }
}

// ---- surfaces -----------------------------------------------------------------------------------------------------

internal abstract class Surface
{
    public abstract V3 At(double u, double v);

    /// <summary>The surface normal in the direction of dP/du x dP/dv.</summary>
    public virtual V3 Normal(double u, double v)
    {
        const double h = 1e-5;
        var du = (At(u + h, v) - At(u - h, v)) * (1 / (2 * h));
        var dv = (At(u, v + h) - At(u, v - h)) * (1 / (2 * h));
        var n = du.Cross(dv);
        return n.Length > 1e-300 ? n.Normalized() : new V3(0, 0, 1);
    }

    public abstract (double U, double V) Project(V3 p, (double U, double V)? hint = null);

    public virtual bool PeriodicU => false;

    public virtual bool PeriodicV => false;

    public virtual bool IsPlane => false;

    /// <summary>The v where u degenerates to a point (sphere poles, cone apex): used to close loops that wind around the axis.</summary>
    public virtual double[] PoleV => [];

    /// <summary>The parameter spacing of interior sample points; 0 when the surface needs none (planes, cylinders, cones are straight in v).</summary>
    public virtual (double U, double V) InteriorStep(double angular) => (0, 0);
}

internal sealed class PlaneSurface : Surface
{
    public Frame F = new();

    public override bool IsPlane => true;

    public override V3 At(double u, double v) => F.O + F.X * u + F.Y * v;

    public override V3 Normal(double u, double v) => F.Z;

    public override (double U, double V) Project(V3 p, (double U, double V)? hint = null)
    {
        var (x, y, _) = F.ToLocal(p);
        return (x, y);
    }
}

internal sealed class CylinderSurface : Surface
{
    public Frame F = new();
    public double R;

    public override bool PeriodicU => true;

    public override V3 At(double u, double v) => F.O + F.X * (R * Math.Cos(u)) + F.Y * (R * Math.Sin(u)) + F.Z * v;

    public override V3 Normal(double u, double v) => F.X * Math.Cos(u) + F.Y * Math.Sin(u);

    public override (double U, double V) Project(V3 p, (double U, double V)? hint = null)
    {
        var (x, y, z) = F.ToLocal(p);
        return (Math.Atan2(y, x), z);
    }
}

internal sealed class ConeSurface : Surface
{
    public Frame F = new();
    public double R;
    public double Angle; // semi-angle

    public override bool PeriodicU => true;

    public override double[] PoleV => Math.Abs(Math.Tan(Angle)) > 1e-12 ? [-R / Math.Tan(Angle)] : [];

    public override V3 At(double u, double v) => F.O + (F.X * Math.Cos(u) + F.Y * Math.Sin(u)) * (R + v * Math.Tan(Angle)) + F.Z * v;

    public override V3 Normal(double u, double v)
    {
        var radial = F.X * Math.Cos(u) + F.Y * Math.Sin(u);
        var tangent = F.X * -Math.Sin(u) + F.Y * Math.Cos(u);
        var dv = radial * Math.Tan(Angle) + F.Z;
        var rho = R + v * Math.Tan(Angle);
        var n = tangent.Cross(dv);
        return (rho < 0 ? -n : n).Normalized();
    }

    public override (double U, double V) Project(V3 p, (double U, double V)? hint = null)
    {
        var (x, y, z) = F.ToLocal(p);
        var u = Math.Sqrt(x * x + y * y) < 1e-9 && hint is { } h ? h.U : Math.Atan2(y, x);
        return (u, z);
    }
}

internal sealed class SphereSurface : Surface
{
    public Frame F = new();
    public double R;

    public override bool PeriodicU => true;

    public override double[] PoleV => [Math.PI / 2, -Math.PI / 2];

    public override V3 At(double u, double v) => F.O + F.X * (R * Math.Cos(v) * Math.Cos(u)) + F.Y * (R * Math.Cos(v) * Math.Sin(u)) + F.Z * (R * Math.Sin(v));

    public override V3 Normal(double u, double v) => (F.X * (Math.Cos(v) * Math.Cos(u)) + F.Y * (Math.Cos(v) * Math.Sin(u)) + F.Z * Math.Sin(v)).Normalized();

    public override (double U, double V) Project(V3 p, (double U, double V)? hint = null)
    {
        var (x, y, z) = F.ToLocal(p);
        var rho = Math.Sqrt(x * x + y * y);
        var u = rho < 1e-9 * Math.Max(R, 1e-9) && hint is { } h ? h.U : Math.Atan2(y, x);
        return (u, Math.Atan2(z, rho));
    }

    public override (double U, double V) InteriorStep(double angular) => (angular, angular);
}

internal sealed class TorusSurface : Surface
{
    public Frame F = new();
    public double Major;
    public double Minor;

    public override bool PeriodicU => true;

    public override bool PeriodicV => true;

    public override V3 At(double u, double v) => F.O + (F.X * Math.Cos(u) + F.Y * Math.Sin(u)) * (Major + Minor * Math.Cos(v)) + F.Z * (Minor * Math.Sin(v));

    public override V3 Normal(double u, double v) => ((F.X * Math.Cos(u) + F.Y * Math.Sin(u)) * Math.Cos(v) + F.Z * Math.Sin(v)).Normalized();

    public override (double U, double V) Project(V3 p, (double U, double V)? hint = null)
    {
        var (x, y, z) = F.ToLocal(p);
        var rho = Math.Sqrt(x * x + y * y);
        var u = rho < 1e-9 && hint is { } h ? h.U : Math.Atan2(y, x);
        return (u, Math.Atan2(z, rho - Major));
    }

    public override (double U, double V) InteriorStep(double angular) => (angular, angular);
}

internal sealed class SplineSurface : Surface
{
    public int DegreeU;
    public int DegreeV;
    public V3[,] Control = new V3[0, 0];
    public double[,]? Weights;
    public double[] KnotsU = [];
    public double[] KnotsV = [];

    public (double Min, double Max) RangeU => (KnotsU[DegreeU], KnotsU[Control.GetLength(0)]);

    public (double Min, double Max) RangeV => (KnotsV[DegreeV], KnotsV[Control.GetLength(1)]);

    public override V3 At(double u, double v)
    {
        var nu = Control.GetLength(0) - 1;
        var nv = Control.GetLength(1) - 1;
        u = Math.Clamp(u, RangeU.Min, RangeU.Max);
        v = Math.Clamp(v, RangeV.Min, RangeV.Max);
        var su = BSpline.FindSpan(nu, DegreeU, u, KnotsU);
        var sv = BSpline.FindSpan(nv, DegreeV, v, KnotsV);
        Span<double> bu = DegreeU < 32 ? stackalloc double[DegreeU + 1] : new double[DegreeU + 1];
        Span<double> bv = DegreeV < 32 ? stackalloc double[DegreeV + 1] : new double[DegreeV + 1];
        BSpline.Basis(su, u, DegreeU, KnotsU, bu);
        BSpline.Basis(sv, v, DegreeV, KnotsV, bv);
        double x = 0, y = 0, z = 0, w = 0;
        for (var i = 0; i <= DegreeU; i++)
        {
            for (var j = 0; j <= DegreeV; j++)
            {
                var iu = su - DegreeU + i;
                var jv = sv - DegreeV + j;
                var weight = bu[i] * bv[j] * (Weights is null ? 1 : Weights[iu, jv]);
                var c = Control[iu, jv];
                x += weight * c.X;
                y += weight * c.Y;
                z += weight * c.Z;
                w += weight;
            }
        }

        return Math.Abs(w) > 1e-300 ? new V3(x / w, y / w, z / w) : Control[su, sv];
    }

    public override V3 Normal(double u, double v)
    {
        var (u0, u1) = RangeU;
        var (v0, v1) = RangeV;
        var hu = (u1 - u0) * 1e-5;
        var hv = (v1 - v0) * 1e-5;
        var a = Math.Clamp(u - hu, u0, u1);
        var b = Math.Clamp(u + hu, u0, u1);
        var c = Math.Clamp(v - hv, v0, v1);
        var d = Math.Clamp(v + hv, v0, v1);
        var du = At(b, v) - At(a, v);
        var dv = At(u, d) - At(u, c);
        var n = du.Cross(dv);
        if (n.Length < 1e-300)
        {
            // Degenerate edge (a collapsed side): look slightly inside.
            var iu = u0 + (u1 - u0) * 0.5 > u ? u + (u1 - u0) * 1e-3 : u - (u1 - u0) * 1e-3;
            var iv = v0 + (v1 - v0) * 0.5 > v ? v + (v1 - v0) * 1e-3 : v - (v1 - v0) * 1e-3;
            n = (At(Math.Clamp(iu + hu, u0, u1), iv) - At(Math.Clamp(iu - hu, u0, u1), iv)).Cross(At(iu, Math.Clamp(iv + hv, v0, v1)) - At(iu, Math.Clamp(iv - hv, v0, v1)));
        }

        return n.Length > 1e-300 ? n.Normalized() : new V3(0, 0, 1);
    }

    public override (double U, double V) Project(V3 p, (double U, double V)? hint = null)
    {
        var (u0, u1) = RangeU;
        var (v0, v1) = RangeV;
        double u, v;
        if (hint is { } h)
        {
            (u, v) = (Math.Clamp(h.U, u0, u1), Math.Clamp(h.V, v0, v1));
            var local = Newton(p, u, v);
            if ((At(local.U, local.V) - p).Length < 1e-5 * Math.Max(1, p.Length))
            {
                return local;
            }
        }

        // Coarse grid search for the nearest sample, then Newton.
        var nu = Math.Clamp(Control.GetLength(0) * 3, 12, 60);
        var nv = Math.Clamp(Control.GetLength(1) * 3, 12, 60);
        var bestU = u0;
        var bestV = v0;
        var best = double.MaxValue;
        for (var i = 0; i <= nu; i++)
        {
            for (var j = 0; j <= nv; j++)
            {
                var cu = u0 + (u1 - u0) * i / nu;
                var cv = v0 + (v1 - v0) * j / nv;
                var d = (At(cu, cv) - p).Length;
                if (d < best)
                {
                    best = d;
                    bestU = cu;
                    bestV = cv;
                }
            }
        }

        return Newton(p, bestU, bestV);
    }

    private (double U, double V) Newton(V3 p, double u, double v)
    {
        var (u0, u1) = RangeU;
        var (v0, v1) = RangeV;
        var hu = (u1 - u0) * 1e-6;
        var hv = (v1 - v0) * 1e-6;
        for (var iteration = 0; iteration < 25; iteration++)
        {
            var s = At(u, v);
            var su = (At(Math.Min(u + hu, u1), v) - At(Math.Max(u - hu, u0), v)) * (1 / (Math.Min(u + hu, u1) - Math.Max(u - hu, u0)));
            var sv = (At(u, Math.Min(v + hv, v1)) - At(u, Math.Max(v - hv, v0))) * (1 / (Math.Min(v + hv, v1) - Math.Max(v - hv, v0)));
            var r = s - p;
            double a = su.Dot(su), b = su.Dot(sv), c = sv.Dot(sv);
            double e = -r.Dot(su), f = -r.Dot(sv);
            var det = a * c - b * b;
            if (Math.Abs(det) < 1e-24)
            {
                break;
            }

            var du = (e * c - b * f) / det;
            var dv = (a * f - b * e) / det;
            var nu = Math.Clamp(u + du, u0, u1);
            var nv = Math.Clamp(v + dv, v0, v1);
            if (Math.Abs(nu - u) < (u1 - u0) * 1e-12 && Math.Abs(nv - v) < (v1 - v0) * 1e-12)
            {
                u = nu;
                v = nv;
                break;
            }

            u = nu;
            v = nv;
        }

        return (u, v);
    }

    public override (double U, double V) InteriorStep(double angular)
    {
        var spansU = Math.Max(1, Control.GetLength(0) - DegreeU);
        var spansV = Math.Max(1, Control.GetLength(1) - DegreeV);
        var cu = Math.Clamp(spansU * Math.Max(DegreeU, 2) * 2, 4, 48);
        var cv = Math.Clamp(spansV * Math.Max(DegreeV, 2) * 2, 4, 48);
        return ((RangeU.Max - RangeU.Min) / cu, (RangeV.Max - RangeV.Min) / cv);
    }
}

internal sealed class RevolutionSurface : Surface
{
    public Curve Profile = new LineCurve();
    public V3 AxisOrigin;
    public V3 AxisDirection = new(0, 0, 1);
    private Frame? _frame;

    public override bool PeriodicU => true;

    private Frame F => _frame ??= Frame.From(AxisOrigin, AxisDirection, null);

    /// <summary>Where the profile ends on the axis (a sphere's poles, a cone's apex).</summary>
    public override double[] PoleV
    {
        get
        {
            if (Profile.Range is not { } range)
            {
                return [];
            }

            var poles = new List<double>(2);
            foreach (var t in new[] { range.Min, range.Max })
            {
                var p = Profile.At(t);
                var (x, y, _) = F.ToLocal(p);
                if (Math.Sqrt(x * x + y * y) < 1e-7 * (1 + p.Length))
                {
                    poles.Add(t);
                }
            }

            return poles.ToArray();
        }
    }

    public override V3 At(double u, double v)
    {
        var p = Profile.At(v);
        var (x, y, z) = F.ToLocal(p);
        var rho = Math.Sqrt(x * x + y * y);
        var phase = Math.Atan2(y, x);
        return F.ToWorld(rho * Math.Cos(phase + u), rho * Math.Sin(phase + u), z);
    }

    public override (double U, double V) Project(V3 p, (double U, double V)? hint = null)
    {
        // The profile parameter: nearest profile point among those at the same radius and height (the profile lies in a half plane).
        var (x, y, z) = F.ToLocal(p);
        var rho = Math.Sqrt(x * x + y * y);
        var angle = Math.Atan2(y, x);
        var range = Profile.Range ?? (-1000, 1000);
        var best = range.Min;
        var bestDistance = double.MaxValue;
        var samples = 200;
        for (var i = 0; i <= samples; i++)
        {
            var t = range.Min + (range.Max - range.Min) * i / samples;
            var (px, py, pz) = F.ToLocal(Profile.At(t));
            var d = Math.Abs(Math.Sqrt(px * px + py * py) - rho) + Math.Abs(pz - z);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = t;
            }
        }

        var (qx, qy, _) = F.ToLocal(Profile.At(best));
        return (angle - Math.Atan2(qy, qx), best);
    }

    public override (double U, double V) InteriorStep(double angular) => (angular, (Profile.Range is { } r ? (r.Max - r.Min) / 12 : angular));
}

internal sealed class ExtrusionSurface : Surface
{
    public Curve Profile = new LineCurve();
    public V3 Direction = new(0, 0, 1);

    public override V3 At(double u, double v) => Profile.At(u) + Direction * v;

    public override (double U, double V) Project(V3 p, (double U, double V)? hint = null)
    {
        // The profile parameter is found on the plane through p perpendicular to the extrusion; v is the offset along it.
        var t = Profile.Project(p - Direction * ((p - Profile.At(hint?.U ?? 0)).Dot(Direction)), hint?.U);
        return (t, (p - Profile.At(t)).Dot(Direction));
    }

    public override (double U, double V) InteriorStep(double angular) => (Profile.Range is { } r ? (r.Max - r.Min) / 12 : 0, 0);
}
