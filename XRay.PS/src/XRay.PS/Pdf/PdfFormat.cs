// Ported from pdf/utils.py: PDF serialization helpers.

using System.Globalization;
using System.Numerics;
using System.Text;
using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.Pdf;

/// <summary>Number, string and matrix formatting for PDF syntax.</summary>
public static class PdfFormat
{
    /// <summary>Escapes special characters for a PDF literal string: <c>A (B)</c> becomes
    /// <c>A \(B\)</c>.</summary>
    public static string EscapeString(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Formats a matrix for content streams (<c>1 0 0 1 0 0</c> for the identity).</summary>
    public static string FormatMatrix(Matrix matrix) =>
        $"{Number(matrix.A)} {Number(matrix.B)} {Number(matrix.C)} {Number(matrix.D)} {Number(matrix.E)} {Number(matrix.F)}";

    /// <summary>Formats a rectangle for PDF dictionaries.</summary>
    public static string FormatRect(Rect rect) =>
        $"{Number(rect.XMin)} {Number(rect.YMin)} {Number(rect.XMax)} {Number(rect.YMax)}";

    /// <summary>Formats a number the way the PDF writer does: integers without a fraction,
    /// otherwise at most six decimals with trailing zeros removed.</summary>
    public static string Number(double value)
    {
        if (Math.Abs(value) < 1e-12)
            return "0";
        if (double.IsNaN(value))
            throw Py.ValueError("cannot convert float NaN to integer");
        if (double.IsInfinity(value))
            throw Py.OverflowError("cannot convert float infinity to integer");
        double rounded = Math.Round(value, MidpointRounding.ToEven);
        if (Math.Abs(value - rounded) < 1e-9)
        {
            if (Math.Abs(rounded) < 9e18)
                return ((long)rounded).ToString(CultureInfo.InvariantCulture);
            return new BigInteger(rounded).ToString(CultureInfo.InvariantCulture);
        }
        string text = Py.FormatF(value, 6).TrimEnd('0').TrimEnd('.');
        return text == "-0" ? "0" : text;
    }

    /// <summary>Formats an integer.</summary>
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Encodes text as ASCII, failing on other characters as the original's
    /// <c>str.encode("ascii")</c> did.</summary>
    internal static byte[] Ascii(string text)
    {
        foreach (char ch in text)
        {
            if (ch > 0x7F)
                throw new HostErrorException("UnicodeEncodeError", "'ascii' codec can't encode character");
        }
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>Encodes text as Latin-1, replacing other characters with '?'.</summary>
    internal static byte[] Latin1Replace(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (string ch in Py.Chars(text))
        {
            int code = Py.Ord(ch);
            bytes.Add(code <= 0xFF ? (byte)code : (byte)'?');
        }
        return [.. bytes];
    }
}
