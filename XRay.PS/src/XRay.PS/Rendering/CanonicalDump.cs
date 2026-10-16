using System.Security.Cryptography;
using System.Text;
using XRay.PS.Internal;

namespace XRay.PS.Rendering;

/// <summary>
/// A canonical text dump of a render document, used to compare render models line by line with
/// the Python library this package was ported from. The format is defined by
/// <c>tools/parity/canonical.py</c>; keep the two in step.
/// </summary>
public static class CanonicalDump
{
    public static string Dump(RenderDocument doc)
    {
        var lines = new List<string>();
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            var page = doc.Pages[i];
            lines.Add($"page {i + 1} {Num(page.Width)} {Num(page.Height)} n={page.Commands.Count}");
            foreach (var cmd in page.Commands)
                lines.Add(Command(cmd));
        }
        var res = doc.Resources;
        foreach (var key in res.ColorSpaces.Keys.OrderBy(k => k, StringComparer.Ordinal))
            lines.Add($"cs {key} {Val(res.ColorSpaces[key])}");
        foreach (var key in res.Functions.Keys.OrderBy(k => k, StringComparer.Ordinal))
            lines.Add($"fn {key} {Val(res.Functions[key])}");
        foreach (var key in res.Patterns.Keys.OrderBy(k => k, StringComparer.Ordinal))
            lines.Add($"pat {key} {Val(res.Patterns[key])}");
        foreach (var key in res.Images.Keys.OrderBy(k => k, StringComparer.Ordinal))
            lines.Add($"img {key} {ImageResource(res.Images[key])}");
        return string.Join("\n", lines);
    }

    public static string Num(double value)
    {
        string text = Py.FormatF(value, 3);
        return text == "-0.000" ? "0.000" : text;
    }

    public static string Text(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char unit in value)
        {
            if (unit == '"')
                sb.Append("\\\"");
            else if (unit == '\\')
                sb.Append("\\\\");
            else if (unit >= 0x20 && unit <= 0x7E)
                sb.Append(unit);
            else
                sb.Append("\\u").Append(((int)unit).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string Blob(byte[] value) =>
        "b" + value.Length + ":" + Convert.ToHexStringLower(SHA1.HashData(value))[..12];

    private static string List(IEnumerable<string> items) => "[" + string.Join(",", items) + "]";

    private static string Dataclass(string name, params (string Field, string Value)[] fields) =>
        name + "(" + string.Join(",", fields.Select(f => f.Field + "=" + f.Value)) + ")";

    public static string Val(object? value)
    {
        switch (value)
        {
            case null:
                return "~";
            case bool b:
                return b ? "T" : "F";
            case double d:
                return Num(d);
            case int i:
                return Num(i);
            case long l:
                return Num(l);
            case string s:
                return Text(s);
            case byte[] bytes:
                return Blob(bytes);
            case double[] arr:
                return List(arr.Select(Num));
            case int[] ints:
                return List(ints.Select(v => Num(v)));
            case IEnumerable<string> strings:
                return List(strings.Select(Text));
            case Matrix m:
                return List(m.ToArray().Select(Num));
            case (double t0, double t1):
                return List([Num(t0), Num(t1)]);
            case (bool e0, bool e1):
                return List([e0 ? "T" : "F", e1 ? "T" : "F"]);
            case RenderCommand cmd:
                return "<" + Command(cmd) + ">";
            case RenderPath path:
                return PathText(path);
            case IReadOnlyDictionary<string, object?> dict:
                return "{" + string.Join(",", dict.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => Text(k) + ":" + Val(dict[k]))) + "}";
            case System.Collections.IList list:
                return List(list.Cast<object?>().Select(Val));
            case DeviceColorSpace dcs:
                return Dataclass("DeviceColorSpace", ("name", Text(dcs.Name)));
            case IndexedColorSpace ics:
                return Dataclass("IndexedColorSpace", ("base", Val(ics.Base)), ("hival", Num(ics.HiVal)), ("lookup", Blob(ics.Lookup)));
            case SeparationColorSpace scs:
                return Dataclass("SeparationColorSpace", ("name", Text(scs.Name)), ("alternate", Val(scs.Alternate)), ("tint", Val(scs.Tint)));
            case DeviceNColorSpace dn:
                return Dataclass("DeviceNColorSpace", ("names", List(dn.Names.Select(Text))), ("alternate", Val(dn.Alternate)), ("tint", Val(dn.Tint)));
            case CieBasedColorSpace cie:
                return Dataclass("CieBasedColorSpace", ("icc_profile", Blob(cie.IccProfile)), ("components", Num(cie.Components)), ("ranges", Val(cie.Ranges)));
            case PatternColorSpace pcs:
                return Dataclass("PatternColorSpace", ("base", Val(pcs.Base)));
            case SampledFunction sf:
                return Dataclass("SampledFunction", ("domain", Val(sf.Domain)), ("range", Val(sf.Range)), ("size", Val(sf.Size)),
                    ("bits_per_sample", Num(sf.BitsPerSample)), ("order", Num(sf.Order)), ("encode", Val(sf.Encode)), ("decode", Val(sf.Decode)),
                    ("samples", Blob(sf.Samples)));
            case ExponentialFunction ef:
                return Dataclass("ExponentialFunction", ("domain", Val(ef.Domain)), ("range", Val(ef.Range)), ("c0", Val(ef.C0)), ("c1", Val(ef.C1)), ("n", Num(ef.N)));
            case StitchingFunction stf:
                return Dataclass("StitchingFunction", ("domain", Val(stf.Domain)), ("range", Val(stf.Range)), ("functions", List(stf.Functions.Select(Val))),
                    ("bounds", Val(stf.Bounds)), ("encode", Val(stf.Encode)));
            case AxialShading ax:
                return Dataclass("AxialShading", ("color_space", Val(ax.ColorSpace)), ("coords", Val(ax.Coords)), ("domain", ax.Domain is { } d1 ? Val(d1) : "~"),
                    ("function", Val(ax.Function)), ("extend", Val(ax.Extend)));
            case RadialShading rad:
                return Dataclass("RadialShading", ("color_space", Val(rad.ColorSpace)), ("coords", Val(rad.Coords)), ("domain", rad.Domain is { } d2 ? Val(d2) : "~"),
                    ("function", Val(rad.Function)), ("extend", Val(rad.Extend)));
            case TilingPattern tp:
                return Dataclass("TilingPattern", ("paint_type", Num(tp.PaintType)), ("tiling_type", Num(tp.TilingType)), ("bbox", Val(tp.BBox)),
                    ("x_step", Num(tp.XStep)), ("y_step", Num(tp.YStep)), ("matrix", Val(tp.Matrix)), ("commands", List(tp.Commands.Select(Val))));
            case ShadingPattern sp:
                return Dataclass("ShadingPattern", ("shading", Val(sp.Shading)), ("matrix", Val(sp.Matrix)));
            case ColorSpacePaint csp:
                return Dataclass("ColorSpacePaint", ("space_id", Text(csp.SpaceId)), ("components", Val(csp.Components)));
            case PatternPaint pp:
                return Dataclass("PatternPaint", ("pattern_id", Text(pp.PatternId)), ("base_space_id", Val(pp.BaseSpaceId)), ("base_components", Val(pp.BaseComponents)));
        }
        return "?" + value.GetType().Name;
    }

    public static string PathText(RenderPath path)
    {
        var parts = new List<string>(path.Segments.Count);
        foreach (var seg in path.Segments)
        {
            string letter = seg.Kind switch
            {
                PathSegmentKind.Move => "m",
                PathSegmentKind.Line => "l",
                PathSegmentKind.Curve => "c",
                _ => "h",
            };
            if (seg.Points.Length == 0)
            {
                parts.Add(letter);
                continue;
            }
            parts.Add(letter + " " + string.Join(" ", seg.Points.Select(p => Num(p.X) + " " + Num(p.Y))));
        }
        return string.Join("|", parts);
    }

    private static string MatrixText(Matrix m) => string.Join(" ", m.ToArray().Select(Num));

    private static string Stroke(StrokeStyle? s) => s is null
        ? "~"
        : $"lw={Num(s.LineWidth)} cap={s.LineCap} join={s.LineJoin} ml={Num(s.MiterLimit)} dash={Val(s.Dash)} ph={Num(s.DashPhase)}";

    public static string PaintText(Paint? p) => p is null ? "~" : $"{p.Kind}:{Val(p.Value)}";

    public static string Command(RenderCommand cmd) => cmd switch
    {
        PathCommand pc => $"P fr={pc.FillRule} op={(pc.Overprint ? "T" : "F")} fo={Num(pc.FillOpacity)} so={Num(pc.StrokeOpacity)} fill={PaintText(pc.Fill)} stroke={{{Stroke(pc.Stroke)}}} sp={PaintText(pc.StrokePaint)} : {PathText(pc.Path)}",
        TextCommand tc => $"T {Text(tc.Text)} font={Text(tc.FontRef)} size={Num(tc.FontSize)} m=[{MatrixText(tc.Matrix)}] fill={PaintText(tc.Fill)} fo={Num(tc.FillOpacity)} gid={(tc.GlyphId is null ? "~" : tc.GlyphId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))} adj={Val(tc.PdfTextAdjustments)}",
        ImageCommand ic => $"I {ic.ImageId} {ic.Width} {ic.Height} m=[{MatrixText(ic.Matrix)}] mask={(ic.Mask ? "T" : "F")} mp={PaintText(ic.MaskPaint)} op={Num(ic.Opacity)}",
        ClipCommand cc => $"C fr={cc.FillRule} : {PathText(cc.Path)}",
        StateSaveCommand => "S",
        StateRestoreCommand => "R",
        _ => "?" + cmd.GetType().Name,
    };

    private static string ImageResource(RenderImageResource res) =>
        $"data={Blob(res.Data)} w={res.Width} h={res.Height} cs={Val(res.ColorSpace)} bpc={res.BitsPerComponent} filter={Val(res.Filter)} params={Val(res.FilterParams)} decode={Val(res.Decode)} mask={(res.Mask ? "T" : "F")} pol={(res.MaskPolarity ? "T" : "F")} smask={(res.SoftMask is null ? "~" : Blob(res.SoftMask))}";
}
