// Ported from ps/clipper.py: the polygon-offset part of the Clipper port (offset_paths and
// PolyOffsetBuilder), which is all the interpreter uses. Points are reference objects, as in
// the original, and coordinates are exact integers.

namespace XRay.PS.PostScript;

internal enum ClipperJoinType
{
    Square = 0,
    Round = 1,
    Miter = 2,
}

internal enum ClipperEndType
{
    Closed = 0,
    Butt = 1,
    Square = 2,
    Round = 3,
}

internal sealed class IntPoint(long x, long y) : IEquatable<IntPoint>
{
    public long X = x;
    public long Y = y;

    public bool Equals(IntPoint? other) => other is not null && other.X == X && other.Y == Y;

    public override bool Equals(object? obj) => Equals(obj as IntPoint);

    public override int GetHashCode() => HashCode.Combine(X, Y);
}

internal sealed class DoublePoint(double x = 0.0, double y = 0.0)
{
    public double X = x;
    public double Y = y;
}

internal static class ClipperOffset
{
    private const double Tolerance = 1.0e-20;

    public static long RoundInt(double value) => value < 0 ? Internal.Py.Int(value - 0.5) : Internal.Py.Int(value + 0.5);

    public static DoublePoint GetUnitNormal(IntPoint pt1, IntPoint pt2)
    {
        double dx = pt2.X - pt1.X;
        double dy = pt2.Y - pt1.Y;
        if (dx == 0 && dy == 0)
            return new DoublePoint();
        // int arithmetic in the original: dx*dx + dy*dy is exact before the square root.
        Int128 sq = (Int128)(pt2.X - pt1.X) * (pt2.X - pt1.X) + (Int128)(pt2.Y - pt1.Y) * (pt2.Y - pt1.Y);
        double f = 1.0 / Math.Sqrt((double)sq);
        dx *= f;
        dy *= f;
        return new DoublePoint(dy, -dx);
    }

    public static double Area(List<IntPoint> poly)
    {
        int high = poly.Count - 1;
        if (high < 2)
            return 0.0;
        Int128 area = (Int128)(poly[high].X + poly[0].X) * (poly[0].Y - poly[high].Y);
        for (int i = 1; i <= high; i++)
            area += (Int128)(poly[i - 1].X + poly[i].X) * (poly[i].Y - poly[i - 1].Y);
        return (double)area / 2.0;
    }

    public static bool Orientation(List<IntPoint> poly) => Area(poly) >= 0;

    private static (List<IntPoint> Path, IntPoint Bot)? StripDupsAndGetBotPt(List<IntPoint> inPath, bool closed)
    {
        int count = inPath.Count;
        if (closed)
        {
            while (count > 0 && inPath[0].Equals(inPath[count - 1]))
                count--;
        }
        if (count == 0)
            return null;
        var outPath = new List<IntPoint> { inPath[0] };
        var botPt = inPath[0];
        for (int i = 1; i < count; i++)
        {
            var pt = inPath[i];
            if (pt.Equals(outPath[^1]))
                continue;
            outPath.Add(pt);
            if (pt.Y > botPt.Y || (pt.Y == botPt.Y && pt.X < botPt.X))
                botPt = pt;
        }
        if (outPath.Count < 2 || (closed && outPath.Count == 2))
            return null;
        return (outPath, botPt);
    }

    public static List<List<IntPoint>> OffsetPaths(List<List<IntPoint>> polys, double delta, ClipperJoinType joinType, ClipperEndType endType, double miterLimit)
    {
        var outPolys = new List<List<IntPoint>>();
        int botIdx = -1;
        var botPt = new IntPoint(0, 0);
        foreach (var poly in polys)
        {
            var stripped = StripDupsAndGetBotPt(poly, endType == ClipperEndType.Closed);
            var outPath = new List<IntPoint>();
            if (stripped is not null)
            {
                (outPath, var pt) = stripped.Value;
                if (botIdx < 0 || pt.Y > botPt.Y || (pt.Y == botPt.Y && pt.X < botPt.X))
                {
                    botPt = pt;
                    botIdx = outPolys.Count;
                }
            }
            outPolys.Add(outPath);
        }
        if (endType == ClipperEndType.Closed && botIdx >= 0 && !Orientation(outPolys[botIdx]))
        {
            foreach (var p in outPolys)
                p.Reverse();
        }
        return new PolyOffsetBuilder(outPolys, delta, joinType, endType, miterLimit).Solution;
    }

    private sealed class PolyOffsetBuilder
    {
        private readonly List<List<IntPoint>> _p;
        private List<IntPoint> _currentPoly = [];
        private List<DoublePoint> _normals = [];
        private readonly double _delta;
        private double _sinA;
        private readonly double _sin;
        private readonly double _cos;
        private readonly double _steps360;
        private readonly double _miterLim;
        private readonly ClipperJoinType _jointype;
        private readonly ClipperEndType _endtype;

        public List<List<IntPoint>> Solution { get; private set; } = [];

        public PolyOffsetBuilder(List<List<IntPoint>> pts, double delta, ClipperJoinType jointype, ClipperEndType endtype, double limit)
        {
            _p = pts;
            _delta = delta;
            _jointype = jointype;
            _endtype = endtype;
            if (-Tolerance < delta && delta < Tolerance)
            {
                Solution = pts;
                return;
            }
            if (endtype != ClipperEndType.Closed && delta < 0)
            {
                delta = -delta;
                _delta = delta;
            }
            if (jointype == ClipperJoinType.Miter)
            {
                _miterLim = limit > 2 ? 2 / (limit * limit) : 0.5;
                if (endtype == ClipperEndType.Round)
                    limit = 0.25;
            }
            else
            {
                _miterLim = 0.0;
            }
            if (jointype == ClipperJoinType.Round || endtype == ClipperEndType.Round)
            {
                if (limit <= 0)
                    limit = 0.25;
                else if (limit > Math.Abs(delta) * 0.25)
                    limit = Math.Abs(delta) * 0.25;
                _steps360 = Math.PI / Math.Acos(1 - limit / Math.Abs(delta));
                _sin = Math.Sin(2 * Math.PI / _steps360);
                _cos = Math.Cos(2 * Math.PI / _steps360);
                _steps360 /= Math.PI * 2;
                if (delta < 0)
                    _sin = -_sin;
            }
            Build();
        }

        private void AddPoint(IntPoint pt) => _currentPoly.Add(pt);

        private void DoSquare(int k, int j, int m)
        {
            double dx = Math.Tan(Math.Atan2(_sinA, _normals[k].X * _normals[j].X + _normals[k].Y * _normals[j].Y) / 4);
            var p = _p[m][j];
            AddPoint(new IntPoint(RoundInt(p.X + _delta * (_normals[k].X - _normals[k].Y * dx)), RoundInt(p.Y + _delta * (_normals[k].Y + _normals[k].X * dx))));
            AddPoint(new IntPoint(RoundInt(p.X + _delta * (_normals[j].X + _normals[j].Y * dx)), RoundInt(p.Y + _delta * (_normals[j].Y - _normals[j].X * dx))));
        }

        private void DoMiter(double r, int k, int j, int m)
        {
            double q = _delta / r;
            var p = _p[m][j];
            AddPoint(new IntPoint(RoundInt(p.X + (_normals[k].X + _normals[j].X) * q), RoundInt(p.Y + (_normals[k].Y + _normals[j].Y) * q)));
        }

        private void DoRound(int k, int j, int m)
        {
            double a = Math.Atan2(_sinA, _normals[k].X * _normals[j].X + _normals[k].Y * _normals[j].Y);
            long steps = RoundInt(_steps360 * Math.Abs(a));
            double x = _normals[k].X;
            double y = _normals[k].Y;
            var p = _p[m][j];
            for (long s = 0; s < steps; s++)
            {
                AddPoint(new IntPoint(RoundInt(p.X + x * _delta), RoundInt(p.Y + y * _delta)));
                double x2 = x;
                x = x * _cos - _sin * y;
                y = x2 * _sin + y * _cos;
            }
            AddPoint(new IntPoint(RoundInt(p.X + _normals[j].X * _delta), RoundInt(p.Y + _normals[j].Y * _delta)));
        }

        private int OffsetPoint(int k, int j, int m)
        {
            _sinA = _normals[k].X * _normals[j].Y - _normals[j].X * _normals[k].Y;
            if (_sinA > 1.0)
                _sinA = 1.0;
            else if (_sinA < -1.0)
                _sinA = -1.0;
            var p = _p[m][j];
            if (_sinA * _delta < 0)
            {
                AddPoint(new IntPoint(RoundInt(p.X + _normals[k].X * _delta), RoundInt(p.Y + _normals[k].Y * _delta)));
                AddPoint(new IntPoint(p.X, p.Y));
                AddPoint(new IntPoint(RoundInt(p.X + _normals[j].X * _delta), RoundInt(p.Y + _normals[j].Y * _delta)));
            }
            else if (_jointype == ClipperJoinType.Miter)
            {
                double r = 1 + (_normals[j].X * _normals[k].X + _normals[j].Y * _normals[k].Y);
                if (r >= _miterLim)
                    DoMiter(r, k, j, m);
                else
                    DoSquare(k, j, m);
            }
            else if (_jointype == ClipperJoinType.Square)
            {
                DoSquare(k, j, m);
            }
            else
            {
                DoRound(k, j, m);
            }
            return j;
        }

        private void Build()
        {
            for (int m = 0; m < _p.Count; m++)
            {
                var poly = _p[m];
                int length = poly.Count;
                if (length == 0 || (length < 3 && _delta <= 0))
                    continue;
                if (length == 1)
                {
                    _currentPoly = [];
                    if (_jointype == ClipperJoinType.Round)
                    {
                        double x = 1.0, y = 0.0;
                        long count = RoundInt(_steps360 * 2 * Math.PI);
                        for (long s = 1; s <= count; s++)
                        {
                            AddPoint(new IntPoint(RoundInt(poly[0].X + x * _delta), RoundInt(poly[0].Y + y * _delta)));
                            double x2 = x;
                            x = x * _cos - _sin * y;
                            y = x2 * _sin + y * _cos;
                        }
                    }
                    else
                    {
                        double x = -1.0, y = -1.0;
                        for (int s = 0; s < 4; s++)
                        {
                            AddPoint(new IntPoint(RoundInt(poly[0].X + x * _delta), RoundInt(poly[0].Y + y * _delta)));
                            if (x < 0)
                                x = 1.0;
                            else if (y < 0)
                                y = 1.0;
                            else
                                x = -1.0;
                        }
                    }
                    Solution.Add(_currentPoly);
                    continue;
                }
                _normals = [];
                for (int j = 0; j < length - 1; j++)
                    _normals.Add(GetUnitNormal(poly[j], poly[j + 1]));
                if (_endtype == ClipperEndType.Closed)
                    _normals.Add(GetUnitNormal(poly[length - 1], poly[0]));
                else
                    _normals.Add(new DoublePoint(_normals[length - 2].X, _normals[length - 2].Y));
                _currentPoly = [];
                if (_endtype == ClipperEndType.Closed)
                {
                    int k = length - 1;
                    for (int j = 0; j < length; j++)
                        k = OffsetPoint(k, j, m);
                    Solution.Add(_currentPoly);
                }
                else
                {
                    int k = 0;
                    for (int j = 1; j < length - 1; j++)
                        k = OffsetPoint(k, j, m);
                    int jj = length - 1;
                    if (_endtype == ClipperEndType.Butt)
                    {
                        AddPoint(new IntPoint(RoundInt(poly[jj].X + _normals[jj].X * _delta), RoundInt(poly[jj].Y + _normals[jj].Y * _delta)));
                        AddPoint(new IntPoint(RoundInt(poly[jj].X - _normals[jj].X * _delta), RoundInt(poly[jj].Y - _normals[jj].Y * _delta)));
                    }
                    else
                    {
                        k = length - 2;
                        _sinA = 0.0;
                        _normals[jj].X = -_normals[jj].X;
                        _normals[jj].Y = -_normals[jj].Y;
                        if (_endtype == ClipperEndType.Square)
                            DoSquare(k, jj, m);
                        else
                            DoRound(k, jj, m);
                    }
                    for (int j = length - 1; j > 0; j--)
                    {
                        _normals[j].X = -_normals[j - 1].X;
                        _normals[j].Y = -_normals[j - 1].Y;
                    }
                    _normals[0].X = -_normals[1].X;
                    _normals[0].Y = -_normals[1].Y;
                    k = length - 1;
                    for (int j = k - 1; j > 0; j--)
                        k = OffsetPoint(k, j, m);
                    if (_endtype == ClipperEndType.Butt)
                    {
                        AddPoint(new IntPoint(RoundInt(poly[0].X - _normals[0].X * _delta), RoundInt(poly[0].Y - _normals[0].Y * _delta)));
                        AddPoint(new IntPoint(RoundInt(poly[0].X + _normals[0].X * _delta), RoundInt(poly[0].Y + _normals[0].Y * _delta)));
                    }
                    else
                    {
                        k = 1;
                        _sinA = 0.0;
                        if (_endtype == ClipperEndType.Square)
                            DoSquare(k, 0, m);
                        else
                            DoRound(k, 0, m);
                    }
                    Solution.Add(_currentPoly);
                }
            }
        }
    }
}
