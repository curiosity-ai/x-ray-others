// Ports of the parts of an imaging library's drawing and resampling code that the XPS reader
// used to rasterize masks: filled polygons, wide lines with round joints, pie slices, and
// separable BOX/BICUBIC/LANCZOS resampling with 22-bit fixed-point coefficients. The arithmetic
// (integer vertices, float angles, rounding macros) follows the C sources so coverage masks come
// out identical.

namespace XRay.PS.Imaging;

/// <summary>An 8-bit single-band image ("L").</summary>
internal sealed class GrayImage(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = new byte[checked(width * height)];

    public byte this[int x, int y] => Pixels[y * Width + x];
}

/// <summary>Drawing primitives over <see cref="GrayImage"/>.</summary>
internal static class PillowDraw
{
    private struct Edge
    {
        public int D;
        public int X0, Y0;
        public int XMin, YMin, XMax, YMax;
        public float Dx;
    }

    private static int RoundUp(double f) => (int)(f >= 0.0 ? Math.Floor(f + 0.5f) : -Math.Floor(Math.Abs(f) + 0.5f));

    private static int RoundDown(double f) => (int)(f >= 0.0 ? Math.Ceiling(f - 0.5f) : -Math.Ceiling(Math.Abs(f) - 0.5f));

    // C's (int) conversion of a double: truncation (out-of-range values clamp here).
    private static int Trunc(double v) => double.IsNaN(v) ? int.MinValue : (int)Math.Clamp(Math.Truncate(v), int.MinValue, int.MaxValue);

    private static void HLine(GrayImage im, int x0, int y0, int x1, int ink)
    {
        if (y0 < 0 || y0 >= im.Height)
            return;
        if (x0 < 0)
            x0 = 0;
        else if (x0 >= im.Width)
            return;
        if (x1 < 0)
            return;
        if (x1 >= im.Width)
            x1 = im.Width - 1;
        if (x0 <= x1)
            Array.Fill(im.Pixels, (byte)ink, y0 * im.Width + x0, x1 - x0 + 1);
    }

    private static void Point(GrayImage im, int x, int y, int ink)
    {
        if (x >= 0 && x < im.Width && y >= 0 && y < im.Height)
            im.Pixels[y * im.Width + x] = (byte)ink;
    }

    private static void AddEdge(ref Edge e, int x0, int y0, int x1, int y1)
    {
        if (x0 <= x1)
        {
            e.XMin = x0;
            e.XMax = x1;
        }
        else
        {
            e.XMin = x1;
            e.XMax = x0;
        }
        if (y0 <= y1)
        {
            e.YMin = y0;
            e.YMax = y1;
        }
        else
        {
            e.YMin = y1;
            e.YMax = y0;
        }
        if (y0 == y1)
        {
            e.D = 0;
            e.Dx = 0.0f;
        }
        else
        {
            e.Dx = (float)(x1 - x0) / (y1 - y0);
            e.D = y0 == e.YMin ? 1 : -1;
        }
        e.X0 = x0;
        e.Y0 = y0;
    }

    private static void PolygonGeneric(GrayImage im, Edge[] e, int n, int ink)
    {
        if (n <= 0)
            return;
        var edgeTable = new int[n];
        int edgeCount = 0;
        int ymin = im.Height - 1;
        int ymax = 0;
        for (int i = 0; i < n; i++)
        {
            if (ymin > e[i].YMin)
                ymin = e[i].YMin;
            if (ymax < e[i].YMax)
                ymax = e[i].YMax;
            if (e[i].YMin == e[i].YMax)
            {
                HLine(im, e[i].XMin, e[i].YMin, e[i].XMax, ink);
                continue;
            }
            edgeTable[edgeCount++] = i;
        }
        if (ymin < 0)
            ymin = 0;
        if (ymax > im.Height)
            ymax = im.Height;
        var xx = new float[edgeCount * 2];
        for (; ymin <= ymax; ymin++)
        {
            int j = 0;
            for (int i = 0; i < edgeCount; i++)
            {
                ref Edge current = ref e[edgeTable[i]];
                if (ymin < current.YMin || ymin > current.YMax)
                    continue;
                xx[j++] = (ymin - current.Y0) * current.Dx + current.X0;
                if (ymin == current.YMax && ymin < ymax)
                {
                    // Needed to draw consistent polygons.
                    xx[j] = xx[j - 1];
                    j++;
                }
                else if ((ymin == current.YMin || ymin == current.YMax) && current.Dx != 0)
                {
                    // Connect discontiguous corners.
                    for (int k = 0; k < i; k++)
                    {
                        ref Edge other = ref e[edgeTable[k]];
                        if ((ymin != other.YMin && ymin != other.YMax) || other.Dx == 0)
                            continue;
                        if (MathF.Round(xx[j - 1], MidpointRounding.AwayFromZero) != MathF.Round((ymin - other.Y0) * other.Dx + other.X0, MidpointRounding.AwayFromZero))
                            continue;
                        int offset = ymin == current.YMax ? -1 : 1;
                        float adjacentLineX = (ymin + offset - current.Y0) * current.Dx + current.X0;
                        if (ymin + offset >= other.YMin && ymin + offset <= other.YMax)
                        {
                            float adjacentOther = (ymin + offset - other.Y0) * other.Dx + other.X0;
                            if (xx[j - 1] > adjacentLineX + 1 && xx[j - 1] > adjacentOther + 1)
                                xx[j - 1] = (float)(Math.Round(Math.Max(adjacentLineX, (double)adjacentOther), MidpointRounding.AwayFromZero) + 1);
                            else if (xx[j - 1] < adjacentLineX - 1 && xx[j - 1] < adjacentOther - 1)
                                xx[j - 1] = (float)(Math.Round(Math.Min(adjacentLineX, (double)adjacentOther), MidpointRounding.AwayFromZero) - 1);
                            break;
                        }
                    }
                }
            }
            Array.Sort(xx, 0, j);
            for (int i = 1; i < j; i += 2)
                HLine(im, RoundUp(xx[i - 1]), ymin, RoundDown(xx[i]), ink);
        }
    }

    /// <summary>A filled polygon (<c>draw.polygon(xy, fill=ink)</c>); vertices truncate to integers.</summary>
    public static void FillPolygon(GrayImage im, IReadOnlyList<(double X, double Y)> points, int ink)
    {
        int count = points.Count;
        if (count < 2)
            throw new ArgumentException("coordinate list must contain at least 2 coordinates");
        var xy = new int[count * 2];
        for (int i = 0; i < count; i++)
        {
            xy[i * 2] = Trunc(points[i].X);
            xy[i * 2 + 1] = Trunc(points[i].Y);
        }
        var e = new Edge[count];
        int n = 0;
        int idx;
        for (idx = 0; idx < count - 1; idx++)
        {
            int x0 = xy[idx * 2], y0 = xy[idx * 2 + 1], x1 = xy[idx * 2 + 2], y1 = xy[idx * 2 + 3];
            if (y0 == y1 && idx != 0 && y0 == xy[idx * 2 - 1])
            {
                // A horizontal line immediately following another horizontal line.
                ref Edge last = ref e[n - 1];
                if (x1 > x0 && x0 > xy[idx * 2 - 2])
                {
                    last.XMax = x1;
                    continue;
                }
                if (x1 < x0 && x0 < xy[idx * 2 - 2])
                {
                    last.XMin = x1;
                    continue;
                }
            }
            AddEdge(ref e[n++], x0, y0, x1, y1);
        }
        if (xy[idx * 2] != xy[0] || xy[idx * 2 + 1] != xy[1])
            AddEdge(ref e[n++], xy[idx * 2], xy[idx * 2 + 1], xy[0], xy[1]);
        PolygonGeneric(im, e, n, ink);
    }

    private static void Line8(GrayImage im, int x0, int y0, int x1, int y1, int ink)
    {
        int dx = x1 - x0, xs = 1;
        if (dx < 0)
        {
            dx = -dx;
            xs = -1;
        }
        int dy = y1 - y0, ys = 1;
        if (dy < 0)
        {
            dy = -dy;
            ys = -1;
        }
        if (dx == 0)
        {
            for (int i = 0; i < dy; i++)
            {
                Point(im, x0, y0, ink);
                y0 += ys;
            }
        }
        else if (dy == 0)
        {
            for (int i = 0; i < dx; i++)
            {
                Point(im, x0, y0, ink);
                x0 += xs;
            }
        }
        else if (dx > dy)
        {
            int n = dx;
            dy += dy;
            int e = dy - dx;
            dx += dx;
            for (int i = 0; i < n; i++)
            {
                Point(im, x0, y0, ink);
                if (e >= 0)
                {
                    y0 += ys;
                    e -= dx;
                }
                e += dy;
                x0 += xs;
            }
        }
        else
        {
            int n = dy;
            dx += dx;
            int e = dx - dy;
            dy += dy;
            for (int i = 0; i < n; i++)
            {
                Point(im, x0, y0, ink);
                if (e >= 0)
                {
                    x0 += xs;
                    e -= dy;
                }
                e += dx;
                y0 += ys;
            }
        }
    }

    private static void WideLine(GrayImage im, int x0, int y0, int x1, int y1, int ink, int width)
    {
        int dx = x1 - x0;
        int dy = y1 - y0;
        if (dx == 0 && dy == 0)
        {
            Point(im, x0, y0, ink);
            return;
        }
        double bigHypotenuse = double.Hypot(dx, dy);
        double smallHypotenuse = (width - 1) / 2.0;
        double ratioMax = RoundUp(smallHypotenuse) / bigHypotenuse;
        double ratioMin = RoundDown(smallHypotenuse) / bigHypotenuse;
        int dxmin = RoundDown(ratioMin * dy);
        int dxmax = RoundDown(ratioMax * dy);
        int dymin = RoundDown(ratioMin * dx);
        int dymax = RoundDown(ratioMax * dx);
        int v0x = x0 - dxmin, v0y = y0 + dymax;
        int v1x = x1 - dxmin, v1y = y1 + dymax;
        int v2x = x1 + dxmax, v2y = y1 - dymin;
        int v3x = x0 + dxmax, v3y = y0 - dymin;
        var e = new Edge[4];
        AddEdge(ref e[0], v0x, v0y, v1x, v1y);
        AddEdge(ref e[1], v1x, v1y, v2x, v2y);
        AddEdge(ref e[2], v2x, v2y, v3x, v3y);
        AddEdge(ref e[3], v3x, v3y, v0x, v0y);
        PolygonGeneric(im, e, 4, ink);
    }

    /// <summary><c>draw.line(points, fill=ink, width=width, joint=joint)</c>.</summary>
    public static void Line(GrayImage im, IReadOnlyList<(double X, double Y)> points, int ink, int width, string? joint)
    {
        if (width == 0)
            return;
        int n = points.Count;
        if (width == 1)
        {
            (double, double)? last = null;
            for (int i = 0; i < n - 1; i++)
            {
                Line8(im, Trunc(points[i].X), Trunc(points[i].Y), Trunc(points[i + 1].X), Trunc(points[i + 1].Y), ink);
                last = points[i + 1];
            }
            if (last is var (lx, ly))
                Point(im, Trunc(lx), Trunc(ly), ink);
        }
        else
        {
            for (int i = 0; i < n - 1; i++)
                WideLine(im, Trunc(points[i].X), Trunc(points[i].Y), Trunc(points[i + 1].X), Trunc(points[i + 1].Y), ink, width);
        }
        if (joint != "curve" || width <= 4)
            return;
        for (int i = 1; i < n - 1; i++)
        {
            var point = points[i];
            double angle0 = Internal.Py.Mod(Internal.Py.Degrees(Math.Atan2(point.X - points[i - 1].X, points[i - 1].Y - point.Y)), 360);
            double angle1 = Internal.Py.Mod(Internal.Py.Degrees(Math.Atan2(points[i + 1].X - point.X, point.Y - points[i + 1].Y)), 360);
            if (angle0 == angle1)
                continue; // a straight line needs no joint
            bool flipped = (angle1 > angle0 && angle1 - 180 > angle0) || (angle1 < angle0 && angle1 + 180 > angle0);
            double cx0 = point.X - width / 2.0 + 1, cy0 = point.Y - width / 2.0 + 1;
            double cx1 = point.X + width / 2.0 - 1, cy1 = point.Y + width / 2.0 - 1;
            double start, end;
            if (flipped)
                (start, end) = (angle1 + 90, angle0 + 90);
            else
                (start, end) = (angle0 - 90, angle1 - 90);
            Pieslice(im, cx0, cy0, cx1, cy1, start - 90, end - 90, ink);
            if (width > 8)
            {
                // Cover potential gaps between the line and the joint.
                (double, double) CoordAtAngle((double X, double Y) coord, double angle)
                {
                    angle -= 90;
                    double distance = width / 2.0 - 1;
                    double pdx = distance * Math.Cos(Internal.Py.Radians(angle));
                    double pdy = distance * Math.Sin(Internal.Py.Radians(angle));
                    return (coord.X + (pdx > 0 ? Math.Floor(pdx) : Math.Ceiling(pdx)), coord.Y + (pdy > 0 ? Math.Floor(pdy) : Math.Ceiling(pdy)));
                }
                var gap = flipped
                    ? new[] { CoordAtAngle(point, angle0 + 90), point, CoordAtAngle(point, angle1 + 90) }
                    : new[] { CoordAtAngle(point, angle0 - 90), point, CoordAtAngle(point, angle1 - 90) };
                Line(im, gap, ink, 3, null);
            }
        }
    }

    /// <summary><c>draw.pieslice([(x0, y0), (x1, y1)], start, end, fill=ink)</c>.</summary>
    public static void Pieslice(GrayImage im, double x0, double y0, double x1, double y1, double start, double end, int ink)
    {
        if (x1 < x0)
            throw new ArgumentException("x1 must be greater than or equal to x0");
        if (y1 < y0)
            throw new ArgumentException("y1 must be greater than or equal to y0");
        int ix0 = Trunc(x0), iy0 = Trunc(y0), ix1 = Trunc(x1), iy1 = Trunc(y1);
        float al = (float)start, ar = (float)end;
        NormalizeAngles(ref al, ref ar);
        if (al + 360 == ar)
        {
            EllipseFill(im, ix0, iy0, ix1, iy1, ink);
            return;
        }
        if (al == ar)
            return;
        int a = ix1 - ix0;
        int b = iy1 - iy0;
        if (a < 0 || b < 0)
            return;
        var state = new ClipEllipse();
        state.PieInit(a, b, ix1 + iy1 - ix0 - iy0, al, ar);
        while (state.Next(out int sx0, out int sy, out int sx1))
            HLine(im, ix0 + (sx0 + a) / 2, iy0 + (sy + b) / 2, ix0 + (sx1 + a) / 2, ink);
    }

    private static void EllipseFill(GrayImage im, int x0, int y0, int x1, int y1, int ink)
    {
        int a = x1 - x0;
        int b = y1 - y0;
        if (a < 0 || b < 0)
            return;
        var st = new EllipseState(a, b, a + b);
        while (st.Next(out int sx0, out int sy, out int sx1))
            HLine(im, x0 + (sx0 + a) / 2, y0 + (sy + b) / 2, x0 + (sx1 + a) / 2, ink);
    }

    private static void NormalizeAngles(ref float al, ref float ar)
    {
        if (ar - al >= 360)
        {
            al = 0;
            ar = 360;
        }
        else
        {
            al = (float)CFmod(al < 0 ? 360 - CFmod(-al, 360) : al, 360);
            ar = (float)(al + CFmod(ar < al ? 360 - CFmod(al - ar, 360) : ar - al, 360));
        }
    }

    // C's fmod: the remainder takes the sign of the dividend, as C#'s % does.
    private static double CFmod(double x, double y) => x % y;

    private sealed class QuarterState
    {
        private int _cx, _cy, _ex, _ey;
        private long _a2, _b2, _a2b2;
        private bool _finished;

        public QuarterState(int a, int b)
        {
            if (a < 0 || b < 0)
            {
                _finished = true;
                return;
            }
            _cx = a;
            _cy = b % 2;
            _ex = a % 2;
            _ey = b;
            _a2 = (long)a * a;
            _b2 = (long)b * b;
            _a2b2 = _a2 * _b2;
        }

        private long Delta(long x, long y) => Math.Abs(_a2 * y * y + _b2 * x * x - _a2b2);

        public bool Next(out int x, out int y)
        {
            x = y = 0;
            if (_finished)
                return false;
            x = _cx;
            y = _cy;
            if (_cx == _ex && _cy == _ey)
            {
                _finished = true;
            }
            else
            {
                int nx = _cx;
                int ny = _cy + 2;
                long ndelta = Delta(nx, ny);
                if (nx > 1)
                {
                    long newdelta = Delta(_cx - 2, _cy + 2);
                    if (ndelta > newdelta)
                    {
                        nx = _cx - 2;
                        ny = _cy + 2;
                        ndelta = newdelta;
                    }
                    newdelta = Delta(_cx - 2, _cy);
                    if (ndelta > newdelta)
                    {
                        nx = _cx - 2;
                        ny = _cy;
                    }
                }
                _cx = nx;
                _cy = ny;
            }
            return true;
        }
    }

    private sealed class EllipseState
    {
        private readonly QuarterState _outer;
        private QuarterState? _inner;
        private int _py, _pl, _pr;
        private readonly int[] _cy = new int[4], _cl = new int[4], _cr = new int[4];
        private int _bufcnt;
        private bool _finished;
        private readonly int _leftmost;

        public EllipseState(int a, int b, int w)
        {
            _leftmost = a % 2;
            _outer = new QuarterState(a, b);
            if (w < 1 || !_outer.Next(out _pr, out _py))
            {
                _finished = true;
            }
            else
            {
                _inner = new QuarterState(a - 2 * (w - 1), b - 2 * (w - 1));
                _pl = _leftmost;
            }
        }

        public bool Next(out int x0, out int y, out int x1)
        {
            if (_bufcnt == 0)
            {
                if (_finished)
                {
                    x0 = y = x1 = 0;
                    return false;
                }
                int yy = _py;
                int l = _pl;
                int r = _pr;
                int cx = 0, cy = 0;
                bool more;
                while ((more = _outer.Next(out cx, out cy)) && cy <= yy)
                {
                }
                if (!more)
                {
                    _finished = true;
                }
                else
                {
                    _pr = cx;
                    _py = cy;
                }
                while ((more = _inner!.Next(out cx, out cy)) && cy <= yy)
                    l = cx;
                _pl = !more ? _leftmost : cx;
                if ((l > 0 || l < r) && yy > 0)
                {
                    _cl[_bufcnt] = l == 0 ? 2 : l;
                    _cy[_bufcnt] = yy;
                    _cr[_bufcnt] = r;
                    _bufcnt++;
                }
                if (yy > 0)
                {
                    _cl[_bufcnt] = -r;
                    _cy[_bufcnt] = yy;
                    _cr[_bufcnt] = -l;
                    _bufcnt++;
                }
                if (l > 0 || l < r)
                {
                    _cl[_bufcnt] = l == 0 ? 2 : l;
                    _cy[_bufcnt] = -yy;
                    _cr[_bufcnt] = r;
                    _bufcnt++;
                }
                _cl[_bufcnt] = -r;
                _cy[_bufcnt] = -yy;
                _cr[_bufcnt] = -l;
                _bufcnt++;
            }
            _bufcnt--;
            x0 = _cl[_bufcnt];
            y = _cy[_bufcnt];
            x1 = _cr[_bufcnt];
            return true;
        }
    }

    private enum ClipType
    {
        And,
        Or,
        Clip,
    }

    private sealed class ClipNode
    {
        public ClipType Type;
        public double A, B, C;
        public ClipNode? L, R;
    }

    private sealed class ClipEllipse
    {
        private EllipseState? _st;
        private ClipNode? _root;
        private readonly Queue<(int X, int Type)> _head = new();
        private int _y;

        public void PieInit(int a, int b, int w, float al, float ar)
        {
            _st = new EllipseState(a, b, w);
            double xl = a * Math.Cos(al * Math.PI / 180.0), xr = a * Math.Cos(ar * Math.PI / 180.0);
            double yl = b * Math.Sin(al * Math.PI / 180.0), yr = b * Math.Sin(ar * Math.PI / 180.0);
            var lc = new ClipNode { Type = ClipType.Clip, A = -yl, B = xl, C = 0 };
            var rc = new ClipNode { Type = ClipType.Clip, A = yr, B = -xr, C = 0 };
            _root = new ClipNode { L = lc, R = rc, Type = ar - al < 180 ? ClipType.And : ClipType.Or };
            if (ar - al < 90)
            {
                // One more half-plane avoids spikes.
                var spike = new ClipNode { Type = ClipType.Clip, A = (xl + xr) / 2.0, B = (yl + yr) / 2.0, C = 0 };
                _root = new ClipNode { L = _root, R = spike, Type = ClipType.And };
            }
        }

        public bool Next(out int x0, out int y, out int x1)
        {
            while (_head.Count == 0 && _st!.Next(out int sx0, out int sy, out int sx1))
            {
                foreach (var ev in DoClip(_root, sx0, sy, sx1))
                    _head.Enqueue(ev);
                _y = sy;
            }
            if (_head.Count > 0)
            {
                y = _y;
                x0 = _head.Dequeue().X;
                x1 = _head.Dequeue().X;
                return true;
            }
            x0 = y = x1 = 0;
            return false;
        }

        private static List<(int X, int Type)> DoClip(ClipNode? root, int x0, int y, int x1)
        {
            if (root is null)
                return [(x0, 1), (x1, -1)];
            if (root.Type == ClipType.Clip)
            {
                const double eps = 1e-9;
                double a = root.A, b = root.B, c = root.C;
                if (Math.Abs(a) < eps)
                {
                    if (b * y + c < -eps)
                    {
                        x0 = 1;
                        x1 = 0;
                    }
                }
                else
                {
                    double ix = -(b * y + c) / a;
                    if (a * x0 + b * y + c < eps)
                        x0 = (int)Math.Round(Math.Max(x0, ix), MidpointRounding.AwayFromZero);
                    if (a * x1 + b * y + c < eps)
                        x1 = (int)Math.Round(Math.Min(x1, ix), MidpointRounding.AwayFromZero);
                }
                return x0 <= x1 ? [(x0, 1), (x1, -1)] : [];
            }
            var l1 = DoClip(root.L, x0, y, x1);
            var l2 = DoClip(root.R, x0, y, x1);
            var result = new List<(int X, int Type)>();
            int i1 = 0, i2 = 0, k1 = 0, k2 = 0;
            while (i1 < l1.Count || i2 < l2.Count)
            {
                (int X, int Type) t;
                if (i2 >= l2.Count || (i1 < l1.Count && (l1[i1].X < l2[i2].X || (l1[i1].X == l2[i2].X && l1[i1].Type > l2[i2].Type))))
                {
                    t = l1[i1++];
                    k1 += t.Type;
                }
                else
                {
                    t = l2[i2++];
                    k2 += t.Type;
                }
                bool tailIsEnd = result.Count == 0 || result[^1].Type == -1;
                bool keep = root.Type == ClipType.Or
                    ? (t.Type == 1 && tailIsEnd) || (t.Type == -1 && k1 == 0 && k2 == 0)
                    : (t.Type == 1 && tailIsEnd && k1 > 0 && k2 > 0) || (t.Type == -1 && result.Count > 0 && result[^1].Type == 1 && (k1 == 0 || k2 == 0));
                if (keep)
                    result.Add(t);
            }
            return result;
        }
    }
}

/// <summary>Resampling filters.</summary>
internal enum ResampleFilter
{
    Box,
    Bicubic,
    Lanczos,
}

/// <summary>Separable resampling with the fixed-point coefficients of the imaging library.</summary>
internal static class PillowResample
{
    private const int PrecisionBits = 32 - 8 - 2;

    private static double Filter(ResampleFilter filter, double x)
    {
        switch (filter)
        {
            case ResampleFilter.Box:
                return x > -0.5 && x <= 0.5 ? 1.0 : 0.0;
            case ResampleFilter.Bicubic:
            {
                const double a = -0.5;
                if (x < 0.0)
                    x = -x;
                if (x < 1.0)
                    return ((a + 2.0) * x - (a + 3.0)) * x * x + 1;
                if (x < 2.0)
                    return (((x - 5) * x + 8) * x - 4) * a;
                return 0.0;
            }
            default:
                if (-3.0 <= x && x < 3.0)
                    return Sinc(x) * Sinc(x / 3);
                return 0.0;
        }
    }

    private static double Sinc(double x)
    {
        if (x == 0.0)
            return 1.0;
        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    private static double Support(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Box => 0.5,
        ResampleFilter.Bicubic => 2.0,
        _ => 3.0,
    };

    private static (int KSize, int[] Bounds, int[] Coeffs) PrecomputeCoeffs(int inSize, float in0, float in1, int outSize, ResampleFilter filter)
    {
        double scale = (double)(in1 - in0) / outSize;
        double filterscale = scale < 1.0 ? 1.0 : scale;
        double support = Support(filter) * filterscale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;
        var kk = new double[checked(outSize * ksize)];
        var bounds = new int[outSize * 2];
        double invFilterscale = 1.0 / filterscale;
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = in0 + (xx + 0.5) * scale;
            double ww = 0.0;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0)
                xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize)
                xmax = inSize;
            xmax -= xmin;
            int k = xx * ksize;
            int x;
            for (x = 0; x < xmax; x++)
            {
                double w = Filter(filter, (x + xmin - center + 0.5) * invFilterscale);
                kk[k + x] = w;
                ww += w;
            }
            if (ww != 0.0)
            {
                for (x = 0; x < xmax; x++)
                    kk[k + x] /= ww;
            }
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        var coeffs = new int[kk.Length];
        for (int i = 0; i < kk.Length; i++)
            coeffs[i] = kk[i] < 0 ? (int)(-0.5 + kk[i] * (1 << PrecisionBits)) : (int)(0.5 + kk[i] * (1 << PrecisionBits));
        return (ksize, bounds, coeffs);
    }

    private static byte Clip8(int v)
    {
        int index = v >> PrecisionBits;
        return (byte)Math.Clamp(index, 0, 255);
    }

    /// <summary><c>image.resize((width, height), filter)</c> for an 8-bit single-band image.</summary>
    public static GrayImage Resize(GrayImage image, int width, int height, ResampleFilter filter)
    {
        if (width < 1 || height < 1)
            throw new ArgumentException("height and width must be > 0");
        if (image.Width == width && image.Height == height)
            return Copy(image);
        if (image.Height > image.Width * 100 && height < image.Height)
        {
            var step = ResizeBox(image, image.Width, height, filter, 0, 0, image.Width, image.Height);
            return ResizeBox(step, width, height, filter, 0, 0, image.Width, height);
        }
        return ResizeBox(image, width, height, filter, 0, 0, image.Width, image.Height);
    }

    private static GrayImage Copy(GrayImage image)
    {
        var copy = new GrayImage(image.Width, image.Height);
        image.Pixels.CopyTo(copy.Pixels, 0);
        return copy;
    }

    private static GrayImage ResizeBox(GrayImage imIn, int xsize, int ysize, ResampleFilter filter, float b0, float b1, float b2, float b3)
    {
        if (b0 - (int)b0 == 0 && b2 - b0 == xsize && b1 - (int)b1 == 0 && b3 - b1 == ysize)
        {
            var crop = new GrayImage(xsize, ysize);
            for (int y = 0; y < ysize; y++)
            {
                for (int x = 0; x < xsize; x++)
                {
                    int sx = x + (int)b0, sy = y + (int)b1;
                    crop.Pixels[y * xsize + x] = sx < imIn.Width && sy < imIn.Height ? imIn[sx, sy] : (byte)0;
                }
            }
            return crop;
        }
        bool needHorizontal = xsize != imIn.Width || b0 != 0 || b2 != xsize;
        bool needVertical = ysize != imIn.Height || b1 != 0 || b3 != ysize;
        var (ksizeV, boundsV, kkV) = PrecomputeCoeffs(imIn.Height, b1, b3, ysize, filter);
        int yboxFirst = boundsV[0];
        int yboxLast = boundsV[ysize * 2 - 2] + boundsV[ysize * 2 - 1];
        GrayImage current = imIn;
        GrayImage? output = null;
        if (needHorizontal)
        {
            var (ksizeH, boundsH, kkH) = PrecomputeCoeffs(imIn.Width, b0, b2, xsize, filter);
            for (int i = 0; i < ysize; i++)
                boundsV[i * 2] -= yboxFirst;
            var temp = new GrayImage(xsize, yboxLast - yboxFirst);
            for (int yy = 0; yy < temp.Height; yy++)
            {
                int inRow = (yy + yboxFirst) * imIn.Width;
                for (int xx = 0; xx < xsize; xx++)
                {
                    int xmin = boundsH[xx * 2];
                    int xmax = boundsH[xx * 2 + 1];
                    int k = xx * ksizeH;
                    int ss = 1 << (PrecisionBits - 1);
                    for (int x = 0; x < xmax; x++)
                        ss += imIn.Pixels[inRow + x + xmin] * kkH[k + x];
                    temp.Pixels[yy * xsize + xx] = Clip8(ss);
                }
            }
            current = temp;
            output = temp;
        }
        if (needVertical)
        {
            var result = new GrayImage(current.Width, ysize);
            for (int yy = 0; yy < ysize; yy++)
            {
                int k = yy * ksizeV;
                int ymin = boundsV[yy * 2];
                int ymax = boundsV[yy * 2 + 1];
                for (int xx = 0; xx < current.Width; xx++)
                {
                    int ss = 1 << (PrecisionBits - 1);
                    for (int y = 0; y < ymax; y++)
                        ss += current.Pixels[(y + ymin) * current.Width + xx] * kkV[k + y];
                    result.Pixels[yy * current.Width + xx] = Clip8(ss);
                }
            }
            output = result;
        }
        return output ?? Copy(current);
    }

    /// <summary>Resizes interleaved 8-bit RGB (each band resampled independently).</summary>
    public static byte[] ResizeRgb(byte[] rgb, int width, int height, int newWidth, int newHeight, ResampleFilter filter)
    {
        var result = new byte[checked(newWidth * newHeight * 3)];
        for (int band = 0; band < 3; band++)
        {
            var plane = new GrayImage(width, height);
            for (int i = 0; i < width * height; i++)
                plane.Pixels[i] = rgb[i * 3 + band];
            var resized = Resize(plane, newWidth, newHeight, filter);
            for (int i = 0; i < newWidth * newHeight; i++)
                result[i * 3 + band] = resized.Pixels[i];
        }
        return result;
    }
}
