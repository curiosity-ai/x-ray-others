using System.Globalization;
using System.Text;

namespace XRay.PS.Rendering;

/// <summary>Value equality over render-model values, mirroring Python's <c>==</c> on the frozen
/// dataclasses of the original model (numbers compare by value, sequences element-wise).</summary>
internal static class ValueEquality
{
    public static bool ObjectEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;
        if (a is double da && b is double db)
            return da == db;
        if (a is double[] aa && b is double[] ab)
            return aa.AsSpan().SequenceEqual(ab);
        return a.Equals(b);
    }
}

/// <summary>Builds a canonical string for a resource so that structurally equal resources share
/// an id, as the original registered them with <c>==</c>.</summary>
internal static class StructuralKey
{
    public static string Of(object? value)
    {
        var sb = new StringBuilder();
        Append(sb, value);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append('~');
                break;
            case bool b:
                sb.Append(b ? 'T' : 'F');
                break;
            case double d:
                sb.Append(d == 0 ? "0" : d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case string s:
                sb.Append('"').Append(s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                break;
            case byte[] bytes:
                sb.Append("b").Append(bytes.Length).Append(':').Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
                break;
            case double[] arr:
                sb.Append('[');
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    Append(sb, arr[i]);
                }
                sb.Append(']');
                break;
            case int[] ints:
                sb.Append('[').Append(string.Join(",", ints)).Append(']');
                break;
            case Matrix m:
                sb.Append("M(");
                Append(sb, m.ToArray());
                sb.Append(')');
                break;
            case Point p:
                Append(sb, p.X);
                sb.Append(' ');
                Append(sb, p.Y);
                break;
            case RenderPath path:
                sb.Append("path(");
                foreach (var seg in path.Segments)
                {
                    sb.Append((int)seg.Kind).Append(':');
                    foreach (var pt in seg.Points)
                    {
                        Append(sb, pt);
                        sb.Append(';');
                    }
                    sb.Append('|');
                }
                sb.Append(')');
                break;
            case StrokeStyle st:
                sb.Append("stroke(");
                Append(sb, st.LineWidth); sb.Append(',');
                sb.Append(st.LineCap).Append(',').Append(st.LineJoin).Append(',');
                Append(sb, st.MiterLimit); sb.Append(',');
                Append(sb, st.Dash); sb.Append(',');
                Append(sb, st.DashPhase);
                sb.Append(')');
                break;
            case Paint paint:
                sb.Append("paint(").Append(paint.Kind).Append(',');
                Append(sb, paint.Value);
                sb.Append(')');
                break;
            case ColorSpacePaint csp:
                sb.Append("csp(").Append(csp.SpaceId).Append(',');
                Append(sb, csp.Components);
                sb.Append(')');
                break;
            case PatternPaint pp:
                sb.Append("pp(").Append(pp.PatternId).Append(',');
                Append(sb, pp.BaseSpaceId);
                sb.Append(',');
                Append(sb, pp.BaseComponents);
                sb.Append(')');
                break;
            case PathCommand pc:
                sb.Append("P(");
                Append(sb, pc.Path); sb.Append(',');
                Append(sb, pc.Stroke); sb.Append(',');
                Append(sb, pc.Fill); sb.Append(',');
                sb.Append(pc.FillRule).Append(',');
                Append(sb, pc.StrokePaint); sb.Append(',');
                Append(sb, pc.Overprint); sb.Append(',');
                Append(sb, pc.FillOpacity); sb.Append(',');
                Append(sb, pc.StrokeOpacity);
                sb.Append(')');
                break;
            case TextCommand tc:
                sb.Append("T(");
                Append(sb, tc.Text); sb.Append(',');
                Append(sb, tc.FontRef); sb.Append(',');
                Append(sb, tc.FontSize); sb.Append(',');
                Append(sb, tc.Matrix); sb.Append(',');
                Append(sb, tc.Fill); sb.Append(',');
                Append(sb, tc.FillOpacity); sb.Append(',');
                Append(sb, tc.GlyphId is null ? null : (long)tc.GlyphId.Value); sb.Append(',');
                Append(sb, tc.PdfTextAdjustments);
                sb.Append(')');
                break;
            case ImageCommand ic:
                sb.Append("I(");
                Append(sb, ic.ImageId); sb.Append(',');
                sb.Append(ic.Width).Append(',').Append(ic.Height).Append(',');
                Append(sb, ic.Matrix); sb.Append(',');
                Append(sb, ic.Mask); sb.Append(',');
                Append(sb, ic.MaskPaint); sb.Append(',');
                Append(sb, ic.Opacity);
                sb.Append(')');
                break;
            case ClipCommand cc:
                sb.Append("C(");
                Append(sb, cc.Path); sb.Append(',').Append(cc.FillRule).Append(')');
                break;
            case StateSaveCommand:
                sb.Append("S()");
                break;
            case StateRestoreCommand:
                sb.Append("R()");
                break;
            case DeviceColorSpace dcs:
                sb.Append("dcs(").Append(dcs.Name).Append(')');
                break;
            case IndexedColorSpace ics:
                sb.Append("ics(");
                Append(sb, ics.Base); sb.Append(',').Append(ics.HiVal).Append(',');
                Append(sb, ics.Lookup);
                sb.Append(')');
                break;
            case SeparationColorSpace scs:
                sb.Append("scs(");
                Append(sb, scs.Name); sb.Append(',');
                Append(sb, scs.Alternate); sb.Append(',');
                Append(sb, scs.Tint);
                sb.Append(')');
                break;
            case DeviceNColorSpace dn:
                sb.Append("dn(");
                foreach (var n in dn.Names)
                {
                    Append(sb, n);
                    sb.Append(',');
                }
                Append(sb, dn.Alternate); sb.Append(',');
                Append(sb, dn.Tint);
                sb.Append(')');
                break;
            case CieBasedColorSpace cie:
                sb.Append("cie(");
                Append(sb, cie.IccProfile); sb.Append(',').Append(cie.Components).Append(',');
                Append(sb, cie.Ranges);
                sb.Append(')');
                break;
            case PatternColorSpace pcs:
                sb.Append("pcs(");
                Append(sb, pcs.Base);
                sb.Append(')');
                break;
            case SampledFunction sf:
                sb.Append("sf(");
                Append(sb, sf.Domain); Append(sb, sf.Range); Append(sb, sf.Size);
                sb.Append(sf.BitsPerSample).Append(',').Append(sf.Order).Append(',');
                Append(sb, sf.Encode); Append(sb, sf.Decode); Append(sb, sf.Samples);
                sb.Append(')');
                break;
            case ExponentialFunction ef:
                sb.Append("ef(");
                Append(sb, ef.Domain); Append(sb, ef.Range); Append(sb, ef.C0); Append(sb, ef.C1); Append(sb, ef.N);
                sb.Append(')');
                break;
            case StitchingFunction stf:
                sb.Append("stf(");
                Append(sb, stf.Domain); Append(sb, stf.Range);
                foreach (var f in stf.Functions)
                    Append(sb, f);
                Append(sb, stf.Bounds); Append(sb, stf.Encode);
                sb.Append(')');
                break;
            case AxialShading ax:
                sb.Append("ax(");
                AppendShading(sb, ax);
                break;
            case RadialShading rad:
                sb.Append("rad(");
                AppendShading(sb, rad);
                break;
            case TilingPattern tp:
                sb.Append("tp(").Append(tp.PaintType).Append(',').Append(tp.TilingType).Append(',');
                Append(sb, tp.BBox); Append(sb, tp.XStep); sb.Append(','); Append(sb, tp.YStep); Append(sb, tp.Matrix);
                foreach (var c in tp.Commands)
                    Append(sb, c);
                sb.Append(')');
                break;
            case ShadingPattern sp:
                sb.Append("sp(");
                Append(sb, sp.Shading); Append(sb, sp.Matrix);
                sb.Append(')');
                break;
            default:
                sb.Append('#').Append(value.GetType().Name).Append(':').Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value));
                break;
        }
    }

    private static void AppendShading(StringBuilder sb, Shading s)
    {
        Append(sb, s.ColorSpace);
        double[] coords = s switch { AxialShading a => a.Coords, RadialShading r => r.Coords, _ => [] };
        Append(sb, coords);
        if (s.Domain is { } d)
        {
            Append(sb, d.T0);
            sb.Append(',');
            Append(sb, d.T1);
        }
        else
        {
            sb.Append('~');
        }
        Append(sb, s.Function);
        sb.Append(s.Extend.Start ? 'T' : 'F').Append(s.Extend.End ? 'T' : 'F').Append(')');
    }
}
