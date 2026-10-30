namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// The control points of a spline that a DWG stores by its fit points only: the cubic B-spline through the points, parametrised by chord length, with a knot at
/// each interior fit point and the end conditions AutoCAD uses (the given end tangent as a unit-speed derivative, or a zero second derivative when the
/// tangent is not set). That gives n + 2 control points and n + 6 knots for n fit points, as AutoCAD reports them (checked against the property dump of its own
/// spline in the sample set: knots 0, 0, 0, 0, d(P0, P1), d(P0, P1) + d(P1, P2), ...).
/// </summary>
internal static class DwgSplineFit
{
    private const int MaxFitPoints = 400;

    public static bool TryCompute(IReadOnlyList<(double X, double Y, double Z)> fit, (double X, double Y, double Z) startTangent, (double X, double Y, double Z) endTangent, out List<(double, double, double)> control, out List<double> knots)
    {
        control = [];
        knots = [];
        var n = fit.Count;
        if (n < 3 || n > MaxFitPoints)
        {
            return false;
        }

        var u = new double[n];
        for (var i = 1; i < n; i++)
        {
            var dx = fit[i].X - fit[i - 1].X;
            var dy = fit[i].Y - fit[i - 1].Y;
            var dz = fit[i].Z - fit[i - 1].Z;
            u[i] = u[i - 1] + Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!(u[i] > u[i - 1]))
            {
                return false; // a repeated point: the chord parametrisation degenerates
            }
        }

        const int p = 3;
        var m = n + 2;
        var k = new double[m + p + 1];
        for (var i = 0; i <= p; i++)
        {
            k[i] = u[0];
            k[m + i] = u[n - 1];
        }

        for (var i = 1; i <= n - 2; i++)
        {
            k[p + i] = u[i];
        }

        // Rows: n interpolation conditions, then one condition per end.
        var a = new double[m, m + 3];
        var rows = 0;
        var basis = new double[p + 1];
        var d1 = new double[p + 1];
        var d2 = new double[p + 1];
        for (var i = 0; i < n; i++)
        {
            var span = Span(k, m, p, u[i]);
            Derivatives(k, span, p, u[i], basis, d1, d2);
            for (var j = 0; j <= p; j++)
            {
                a[rows, span - p + j] = basis[j];
            }

            a[rows, m] = fit[i].X;
            a[rows, m + 1] = fit[i].Y;
            a[rows, m + 2] = fit[i].Z;
            rows++;
        }

        for (var end = 0; end < 2; end++)
        {
            var at = end == 0 ? u[0] : u[n - 1];
            var tangent = end == 0 ? startTangent : endTangent;
            var span = Span(k, m, p, at);
            Derivatives(k, span, p, at, basis, d1, d2);
            var length = Math.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y + tangent.Z * tangent.Z);
            var hasTangent = length > 1e-12;
            for (var j = 0; j <= p; j++)
            {
                a[rows, span - p + j] = hasTangent ? d1[j] : d2[j];
            }

            a[rows, m] = hasTangent ? tangent.X / length : 0;
            a[rows, m + 1] = hasTangent ? tangent.Y / length : 0;
            a[rows, m + 2] = hasTangent ? tangent.Z / length : 0;
            rows++;
        }

        // Gaussian elimination with partial pivoting.
        for (var col = 0; col < m; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < m; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(a[pivot, col]) < 1e-14)
            {
                return false;
            }

            if (pivot != col)
            {
                for (var c = 0; c < m + 3; c++)
                {
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                }
            }

            for (var r = col + 1; r < m; r++)
            {
                var f = a[r, col] / a[col, col];
                if (f == 0)
                {
                    continue;
                }

                for (var c = col; c < m + 3; c++)
                {
                    a[r, c] -= f * a[col, c];
                }
            }
        }

        var solution = new double[m, 3];
        for (var r = m - 1; r >= 0; r--)
        {
            for (var d = 0; d < 3; d++)
            {
                var sum = a[r, m + d];
                for (var c = r + 1; c < m; c++)
                {
                    sum -= a[r, c] * solution[c, d];
                }

                solution[r, d] = sum / a[r, r];
            }
        }

        for (var r = 0; r < m; r++)
        {
            if (!double.IsFinite(solution[r, 0] + solution[r, 1] + solution[r, 2]))
            {
                return false;
            }

            control.Add((solution[r, 0], solution[r, 1], solution[r, 2]));
        }

        knots = [.. k];
        return true;
    }

    private static int Span(double[] knots, int count, int degree, double t)
    {
        if (t >= knots[count])
        {
            return count - 1;
        }

        var span = degree;
        while (span < count - 1 && t >= knots[span + 1])
        {
            span++;
        }

        return span;
    }

    /// <summary>The non-zero basis functions of a span and their first and second derivatives (Piegl and Tiller, algorithms A2.2 and A2.3, order two).</summary>
    private static void Derivatives(double[] u, int span, int p, double t, double[] n0, double[] n1, double[] n2)
    {
        var ndu = new double[p + 1, p + 1];
        var left = new double[p + 1];
        var right = new double[p + 1];
        ndu[0, 0] = 1;
        for (var j = 1; j <= p; j++)
        {
            left[j] = t - u[span + 1 - j];
            right[j] = u[span + j] - t;
            var saved = 0.0;
            for (var r = 0; r < j; r++)
            {
                ndu[j, r] = right[r + 1] + left[j - r];
                var temp = ndu[r, j - 1] / ndu[j, r];
                ndu[r, j] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }

            ndu[j, j] = saved;
        }

        var ders = new double[3, p + 1];
        var a = new double[2, p + 1];
        for (var j = 0; j <= p; j++)
        {
            ders[0, j] = ndu[j, p];
        }

        for (var r = 0; r <= p; r++)
        {
            var s1 = 0;
            var s2 = 1;
            a[0, 0] = 1;
            for (var kk = 1; kk <= 2; kk++)
            {
                var d = 0.0;
                var rk = r - kk;
                var pk = p - kk;
                if (r >= kk)
                {
                    a[s2, 0] = a[s1, 0] / ndu[pk + 1, rk];
                    d = a[s2, 0] * ndu[rk, pk];
                }

                var j1 = rk >= -1 ? 1 : -rk;
                var j2 = r - 1 <= pk ? kk - 1 : p - r;
                for (var j = j1; j <= j2; j++)
                {
                    a[s2, j] = (a[s1, j] - a[s1, j - 1]) / ndu[pk + 1, rk + j];
                    d += a[s2, j] * ndu[rk + j, pk];
                }

                if (r <= pk)
                {
                    a[s2, kk] = -a[s1, kk - 1] / ndu[pk + 1, r];
                    d += a[s2, kk] * ndu[r, pk];
                }

                ders[kk, r] = d;
                (s1, s2) = (s2, s1);
            }
        }

        var factor = (double)p;
        for (var kk = 1; kk <= 2; kk++)
        {
            for (var j = 0; j <= p; j++)
            {
                ders[kk, j] *= factor;
            }

            factor *= p - kk;
        }

        for (var j = 0; j <= p; j++)
        {
            n0[j] = ders[0, j];
            n1[j] = ders[1, j];
            n2[j] = ders[2, j];
        }
    }
}
