// Ported from the glyph helpers of image/raster_renderer.py (_contours_to_path and friends),
// which text operators and both rasterizers share.

using XRay.PS.Rendering;

namespace XRay.PS.Fonts;

/// <summary>Converts TrueType contours into render-model paths.</summary>
public static class GlyphOutlines
{
    /// <summary>Contours simplified below this many device pixels (PS_TEXT_SIMPLIFY_PX).</summary>
    public const double SimplifyPx = 0.25;

    /// <summary>Segment cap per glyph path (PS_TEXT_SEGMENT_CAP).</summary>
    public const int SegmentCap = 2000;

    /// <summary>Builds a path from glyph contours scaled by <paramref name="sizeScale"/> and
    /// transformed by (a, b, c, d, e, f).</summary>
    public static RenderPath ContoursToPath(List<List<GlyphPoint>> contours, double sizeScale, double a, double b, double c, double d, double e, double f)
    {
        var segments = new List<PathSegment>();
        foreach (var source in contours)
        {
            if (source.Count == 0)
                continue;
            var contour = source;
            if (SimplifyPx > 0)
            {
                double simplifyUnits = SimplifyPx / Math.Max(sizeScale, 1e-6);
                contour = SimplifyContour(contour, simplifyUnits);
            }
            var expanded = Expand(contour, 2.0);
            if (expanded.Count == 0)
                continue;
            int startIndex = 0;
            for (int i = 0; i < expanded.Count; i++)
            {
                if (expanded[i].OnCurve)
                {
                    startIndex = i;
                    break;
                }
            }
            var current = expanded[startIndex];
            segments.Add(new PathSegment(PathSegmentKind.Move, [TransformPoint(current, sizeScale, a, b, c, d, e, f)]));
            int idx = (startIndex + 1) % expanded.Count;
            while (idx != startIndex)
            {
                Internal.Guard.Check();
                if (SegmentCap > 0 && segments.Count >= SegmentCap)
                    break;
                var pt = expanded[idx];
                if (pt.OnCurve)
                {
                    segments.Add(new PathSegment(PathSegmentKind.Line, [TransformPoint(pt, sizeScale, a, b, c, d, e, f)]));
                    current = pt;
                }
                else
                {
                    var nxt = expanded[(idx + 1) % expanded.Count];
                    if (!nxt.OnCurve)
                    {
                        idx = (idx + 1) % expanded.Count;
                        continue;
                    }
                    var (c1, c2, end) = QuadraticToCubic(current, pt, nxt, sizeScale, a, b, c, d, e, f);
                    segments.Add(new PathSegment(PathSegmentKind.Curve, [c1, c2, end]));
                    current = nxt;
                    idx = (idx + 1) % expanded.Count;
                    // Deviation from the original: a curve that ends on the contour's first point
                    // closes it. The original stepped past that point and went round the contour
                    // again until the segment cap, which dropped every later contour of the glyph
                    // (a P, B or D lost its bowl or counter).
                    if (idx == startIndex)
                        break;
                }
                idx = (idx + 1) % expanded.Count;
            }
            segments.Add(new PathSegment(PathSegmentKind.Close, []));
        }
        return new RenderPath(segments);
    }

    /// <summary>Inserts the implied on-curve midpoints between consecutive off-curve points.</summary>
    private static List<GlyphPoint> Expand(List<GlyphPoint> contour, double divisor)
    {
        var expanded = new List<GlyphPoint>(contour.Count * 2);
        int count = contour.Count;
        for (int i = 0; i < count; i++)
        {
            var pt = contour[i];
            expanded.Add(pt);
            var nxt = contour[(i + 1) % count];
            if (!pt.OnCurve && !nxt.OnCurve)
            {
                expanded.Add(divisor == 2.0
                    ? new GlyphPoint((pt.X + nxt.X) / 2.0, (pt.Y + nxt.Y) / 2.0, true)
                    : new GlyphPoint((pt.X + nxt.X) * 0.5, (pt.Y + nxt.Y) * 0.5, true));
            }
        }
        return expanded;
    }

    /// <summary>True when every non-degenerate contour winds the same way, which closes counters
    /// under the nonzero rule; such glyphs are filled even-odd.</summary>
    public static bool NeedEvenOdd(List<List<GlyphPoint>> contours)
    {
        if (contours.Count < 2)
            return false;
        var signs = new List<int>();
        foreach (var contour in contours)
        {
            int sign = ExpandedContourWindingSign(contour);
            if (sign != 0)
                signs.Add(sign);
        }
        if (signs.Count < 2)
            return false;
        int first = signs[0];
        return signs.Skip(1).All(s => s == first);
    }

    private static int ExpandedContourWindingSign(List<GlyphPoint> contour)
    {
        if (contour.Count < 3)
            return 0;
        var expanded = Expand(contour, 0.5);
        if (expanded.Count < 3)
            return 0;
        double area2 = 0.0;
        for (int i = 0; i < expanded.Count; i++)
        {
            var p0 = expanded[i];
            var p1 = expanded[(i + 1) % expanded.Count];
            area2 += (p0.X * p1.Y) - (p1.X * p0.Y);
        }
        if (Math.Abs(area2) < 1e-9)
            return 0;
        return area2 > 0 ? 1 : -1;
    }

    public static List<GlyphPoint> SimplifyContour(List<GlyphPoint> contour, double epsilon)
    {
        if (epsilon <= 0 || contour.Count <= 2)
            return contour;
        var keep = RdpIndices(contour, epsilon);
        if (keep.Count <= 2)
            return contour;
        return keep.Select(i => contour[i]).ToList();
    }

    private static List<int> RdpIndices(List<GlyphPoint> points, double epsilon)
    {
        int n = points.Count;
        if (n <= 2)
            return Enumerable.Range(0, n).ToList();
        double epsSq = epsilon * epsilon;
        var keep = new HashSet<int> { 0, n - 1 };
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            if (end <= start + 1)
                continue;
            double maxDist = -1.0;
            int maxIdx = -1;
            double x1 = points[start].X, y1 = points[start].Y;
            double x2 = points[end].X, y2 = points[end].Y;
            double dx = x2 - x1;
            double dy = y2 - y1;
            double denom = dx * dx + dy * dy;
            for (int i = start + 1; i < end; i++)
            {
                double px = points[i].X, py = points[i].Y;
                double distSq;
                if (denom == 0)
                {
                    distSq = Math.Pow(px - x1, 2) + Math.Pow(py - y1, 2);
                }
                else
                {
                    double t = ((px - x1) * dx + (py - y1) * dy) / denom;
                    double cx, cy;
                    if (t < 0)
                        (cx, cy) = (x1, y1);
                    else if (t > 1)
                        (cx, cy) = (x2, y2);
                    else
                        (cx, cy) = (x1 + t * dx, y1 + t * dy);
                    distSq = Math.Pow(px - cx, 2) + Math.Pow(py - cy, 2);
                }
                if (distSq > maxDist)
                {
                    maxDist = distSq;
                    maxIdx = i;
                }
            }
            if (maxDist > epsSq && maxIdx != -1)
            {
                keep.Add(maxIdx);
                stack.Push((start, maxIdx));
                stack.Push((maxIdx, end));
            }
        }
        var sorted = keep.ToList();
        sorted.Sort();
        return sorted;
    }

    public static Point TransformPoint(GlyphPoint pt, double sizeScale, double a, double b, double c, double d, double e, double f)
    {
        double x = pt.X * sizeScale;
        double y = pt.Y * sizeScale;
        return new Point(a * x + c * y + e, b * x + d * y + f);
    }

    public static (Point C1, Point C2, Point End) QuadraticToCubic(GlyphPoint p0, GlyphPoint p1, GlyphPoint p2, double sizeScale,
        double a, double b, double c, double d, double e, double f)
    {
        double x0 = p0.X * sizeScale, y0 = p0.Y * sizeScale;
        double x1 = p1.X * sizeScale, y1 = p1.Y * sizeScale;
        double x2 = p2.X * sizeScale, y2 = p2.Y * sizeScale;
        double c1x = x0 + (2.0 / 3.0) * (x1 - x0);
        double c1y = y0 + (2.0 / 3.0) * (y1 - y0);
        double c2x = x2 + (2.0 / 3.0) * (x1 - x2);
        double c2y = y2 + (2.0 / 3.0) * (y1 - y2);
        return (
            new Point(a * c1x + c * c1y + e, b * c1x + d * c1y + f),
            new Point(a * c2x + c * c2y + e, b * c2x + d * c2y + f),
            new Point(a * x2 + c * y2 + e, b * x2 + d * y2 + f));
    }
}
