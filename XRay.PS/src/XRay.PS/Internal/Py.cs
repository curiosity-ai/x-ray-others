using System.Globalization;
using System.Numerics;
using System.Text;

namespace XRay.PS.Internal;

/// <summary>Python semantics the port reproduces bit for bit: float modulo, rounding, number
/// parsing and formatting, whitespace and line splitting over Latin-1 text.</summary>
internal static class Py
{
    public static readonly Encoding Latin1 = Encoding.Latin1;

    public static HostErrorException ZeroDivision(string message = "float division by zero") => new("ZeroDivisionError", message);
    public static HostErrorException ValueError(string message) => new("ValueError", message);
    public static HostErrorException TypeError(string message) => new("TypeError", message);
    public static HostErrorException IndexError(string message = "list index out of range") => new("IndexError", message);
    public static HostErrorException KeyError(string message) => new("KeyError", message);
    public static HostErrorException OverflowError(string message) => new("OverflowError", message);

    /// <summary>Python <c>a / b</c> on floats.</summary>
    public static double Div(double a, double b)
    {
        if (b == 0.0)
            throw ZeroDivision();
        return a / b;
    }

    /// <summary>Python <c>a % b</c> on floats (result takes the sign of the divisor).</summary>
    public static double Mod(double a, double b)
    {
        if (b == 0.0)
            throw ZeroDivision("float modulo");
        double mod = CFmod(a, b);
        if (mod != 0.0)
        {
            if ((b < 0) != (mod < 0))
                mod += b;
        }
        else
        {
            mod = Math.CopySign(0.0, b);
        }
        return mod;
    }

    private static double CFmod(double a, double b) => a % b; // C# % on doubles is C fmod

    /// <summary>Python floor division on floats.</summary>
    public static double FloorDiv(double a, double b)
    {
        if (b == 0.0)
            throw ZeroDivision("float floor division by zero");
        double mod = CFmod(a, b);
        double div = (a - mod) / b;
        if (mod != 0.0 && (b < 0) != (mod < 0))
            div -= 1.0;
        double floordiv;
        if (div != 0.0)
        {
            floordiv = Math.Floor(div);
            if (div - floordiv > 0.5)
                floordiv += 1.0;
        }
        else
        {
            floordiv = Math.CopySign(0.0, a / b);
        }
        return floordiv;
    }

    /// <summary>Python <c>int(x)</c> for a float: truncation, with the original's errors.</summary>
    public static long Int(double value)
    {
        if (double.IsNaN(value))
            throw ValueError("cannot convert float NaN to integer");
        if (double.IsInfinity(value))
            throw OverflowError("cannot convert float infinity to integer");
        double t = Math.Truncate(value);
        if (t >= 9.2233720368547758E18 || t < -9.2233720368547758E18)
            return t > 0 ? long.MaxValue : long.MinValue;
        return (long)t;
    }

    /// <summary>Python <c>round(x)</c> to an integer (ties to even).</summary>
    public static long Round(double value) => Int(Math.Round(value, MidpointRounding.ToEven));

    /// <summary>Python <c>int(round(x))</c>.</summary>
    public static int RoundInt(double value) => (int)Round(value);

    /// <summary>Python <c>round(x, n)</c> for a float: rounds the exact binary value half to even.</summary>
    public static double Round(double value, int digits)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value == 0)
            return value;
        long bits = BitConverter.DoubleToInt64Bits(value);
        bool negative = bits < 0;
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
            exponent = 1;
        else
            mantissa |= 1L << 52;
        exponent -= 1075;
        // value = mantissa * 2^exponent; scaled = value * 10^digits = num / den.
        BigInteger num = mantissa;
        BigInteger den = BigInteger.One;
        if (exponent >= 0)
            num <<= exponent;
        else
            den <<= -exponent;
        if (digits >= 0)
            num *= BigInteger.Pow(10, digits);
        else
            den *= BigInteger.Pow(10, -digits);
        BigInteger q = BigInteger.DivRem(num, den, out BigInteger rem);
        BigInteger twice = rem * 2;
        int cmp = twice.CompareTo(den);
        if (cmp > 0 || (cmp == 0 && !q.IsEven))
            q += 1;
        string text = q.ToString(CultureInfo.InvariantCulture);
        string literal = digits >= 0 ? text + "e-" + digits.ToString(CultureInfo.InvariantCulture) : text + "e" + (-digits).ToString(CultureInfo.InvariantCulture);
        double result = double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);
        return negative ? -result : result;
    }

    /// <summary>Python <c>str.isspace()</c> for a Latin-1 character.</summary>
    public static bool IsSpace(char ch) =>
        ch is ' ' or '\t' or '\n' or '\v' or '\f' or '\r' or '\x1c' or '\x1d' or '\x1e' or '\x1f' or '\x85' or '\xa0'
        || (ch > '\xff' && char.IsWhiteSpace(ch));

    /// <summary>Python <c>str.isdigit()</c> for a Latin-1 character.</summary>
    public static bool IsDigit(char ch) => ch is >= '0' and <= '9' or '\xb2' or '\xb3' or '\xb9';

    /// <summary>Python <c>str.strip()</c>.</summary>
    public static string Strip(string text)
    {
        int start = 0;
        int end = text.Length;
        while (start < end && IsSpace(text[start]))
            start++;
        while (end > start && IsSpace(text[end - 1]))
            end--;
        return text.Substring(start, end - start);
    }

    public static string LStrip(string text)
    {
        int start = 0;
        while (start < text.Length && IsSpace(text[start]))
            start++;
        return text[start..];
    }

    public static string RStrip(string text)
    {
        int end = text.Length;
        while (end > 0 && IsSpace(text[end - 1]))
            end--;
        return text[..end];
    }

    /// <summary>Python <c>str.split()</c> with no separator.</summary>
    public static List<string> Split(string text)
    {
        var result = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && IsSpace(text[i]))
                i++;
            if (i >= text.Length)
                break;
            int start = i;
            while (i < text.Length && !IsSpace(text[i]))
                i++;
            result.Add(text.Substring(start, i - start));
        }
        return result;
    }

    /// <summary>Python <c>str.splitlines()</c>.</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            int eol;
            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                eol = 2;
            else if (ch is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
                eol = 1;
            else
            {
                i++;
                continue;
            }
            lines.Add(text.Substring(start, i - start));
            i += eol;
            start = i;
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines;
    }

    /// <summary>Python <c>str.splitlines(keepends=True)</c>.</summary>
    public static List<string> SplitLinesKeepEnds(string text)
    {
        var lines = new List<string>();
        int start = 0;
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            int eol;
            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                eol = 2;
            else if (ch is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
                eol = 1;
            else
            {
                i++;
                continue;
            }
            i += eol;
            lines.Add(text.Substring(start, i - start));
            start = i;
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines;
    }

    /// <summary>Python <c>bytes.strip()</c> (ASCII whitespace).</summary>
    public static ReadOnlySpan<byte> StripBytes(ReadOnlySpan<byte> data)
    {
        static bool Ws(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0B or 0x0C;
        int start = 0;
        int end = data.Length;
        while (start < end && Ws(data[start]))
            start++;
        while (end > start && Ws(data[end - 1]))
            end--;
        return data[start..end];
    }

    /// <summary>Python <c>bytes.decode("utf-8")</c>, falling back to Latin-1 where it raises.</summary>
    public static string DecodeUtf8OrLatin1(ReadOnlySpan<byte> data)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Latin1String(data);
        }
    }

    /// <summary>Latin-1 decoding (every byte maps to the code point of the same value).</summary>
    public static string Latin1String(ReadOnlySpan<byte> data) => Latin1.GetString(data);

    /// <summary>Python <c>float(text)</c>; false where Python raises <c>ValueError</c>.</summary>
    public static bool TryFloat(string text, out double value)
    {
        value = 0;
        string s = Strip(text);
        if (s.Length == 0)
            return false;
        string lower = s.ToLowerInvariant();
        string body = lower.TrimStart('+', '-');
        bool neg = lower.StartsWith('-');
        if (lower.Length - body.Length > 1)
            return false;
        if (body is "inf" or "infinity")
        {
            value = neg ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }
        if (body == "nan")
        {
            value = double.NaN;
            return true;
        }
        if (s.Contains('_', StringComparison.Ordinal))
        {
            if (!ValidUnderscores(s))
                return false;
            s = s.Replace("_", "", StringComparison.Ordinal);
        }
        foreach (char c in s)
        {
            if (!(c is >= '0' and <= '9' or '.' or 'e' or 'E' or '+' or '-'))
                return false;
        }
        // Reject forms .NET accepts but Python does not (for example "1e").
        int dot = 0;
        bool digit = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '+' or '-')
            {
                if (i != 0 && s[i - 1] is not ('e' or 'E'))
                    return false;
            }
            else if (c == '.')
            {
                dot++;
            }
            else if (c is 'e' or 'E')
            {
                if (!digit || i == s.Length - 1)
                    return false;
            }
            else
            {
                digit = true;
            }
        }
        if (dot > 1 || !digit)
            return false;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool ValidUnderscores(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '_')
                continue;
            if (i == 0 || i == s.Length - 1 || !char.IsAsciiDigit(s[i - 1]) || !char.IsAsciiDigit(s[i + 1]))
                return false;
        }
        return true;
    }

    /// <summary>Python <c>int(text, base)</c>; false where Python raises <c>ValueError</c>.</summary>
    public static bool TryInt(string text, int radix, out long value)
    {
        value = 0;
        string s = Strip(text);
        if (s.Length == 0)
            return false;
        bool neg = false;
        if (s[0] is '+' or '-')
        {
            neg = s[0] == '-';
            s = s[1..];
        }
        if (s.Length == 0)
            return false;
        // int("0x1f", 16) and friends: the prefix matching the base is accepted.
        if (s.Length > 2 && s[0] == '0' && ((radix == 16 && s[1] is 'x' or 'X') || (radix == 8 && s[1] is 'o' or 'O') || (radix == 2 && s[1] is 'b' or 'B')))
        {
            s = s[2..];
            if (s.StartsWith('_'))
                s = s[1..];
        }
        if (s.Contains('_', StringComparison.Ordinal))
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '_' && (i == 0 || i == s.Length - 1 || s[i - 1] == '_'))
                    return false;
            }
            s = s.Replace("_", "", StringComparison.Ordinal);
        }
        BigInteger acc = BigInteger.Zero;
        foreach (char c in s)
        {
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => 99,
            };
            if (d >= radix)
                return false;
            acc = acc * radix + d;
        }
        if (neg)
            acc = -acc;
        if (acc > long.MaxValue)
            value = long.MaxValue;
        else if (acc < long.MinValue)
            value = long.MinValue;
        else
            value = (long)acc;
        return true;
    }

    /// <summary>Python <c>repr(float)</c> / <c>str(float)</c>.</summary>
    public static string Repr(double value)
    {
        if (double.IsNaN(value))
            return "nan";
        if (double.IsPositiveInfinity(value))
            return "inf";
        if (double.IsNegativeInfinity(value))
            return "-inf";
        if (value == 0)
            return double.IsNegative(value) ? "-0.0" : "0.0";
        (string digits, int exp, bool negative) = ShortestDigits(value);
        var sb = new StringBuilder();
        if (negative)
            sb.Append('-');
        // exp is the decimal exponent of the first digit (value = 0.d1d2... * 10^(exp+1)).
        if (exp >= -5 && exp < 16)
        {
            if (exp >= 0)
            {
                if (digits.Length <= exp + 1)
                {
                    sb.Append(digits).Append('0', exp + 1 - digits.Length).Append(".0");
                }
                else
                {
                    sb.Append(digits, 0, exp + 1).Append('.').Append(digits, exp + 1, digits.Length - exp - 1);
                }
            }
            else
            {
                sb.Append("0.").Append('0', -exp - 1).Append(digits);
            }
            if (exp < -4)
            {
                // Python switches to scientific notation below 1e-4.
                sb.Clear();
                if (negative)
                    sb.Append('-');
                AppendSci(sb, digits, exp);
            }
        }
        else
        {
            AppendSci(sb, digits, exp);
        }
        return sb.ToString();
    }

    private static void AppendSci(StringBuilder sb, string digits, int exp)
    {
        sb.Append(digits[0]);
        if (digits.Length > 1)
            sb.Append('.').Append(digits, 1, digits.Length - 1);
        sb.Append('e').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
    }

    private static (string Digits, int Exp, bool Negative) ShortestDigits(double value)
    {
        string r = Math.Abs(value).ToString("E16", CultureInfo.InvariantCulture);
        // Use the shortest round-trip representation.
        string shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        int ePos = shortest.IndexOfAny(['E', 'e']);
        string mant = ePos >= 0 ? shortest[..ePos] : shortest;
        int exp10 = ePos >= 0 ? int.Parse(shortest[(ePos + 1)..], CultureInfo.InvariantCulture) : 0;
        int dot = mant.IndexOf('.', StringComparison.Ordinal);
        string intPart = dot >= 0 ? mant[..dot] : mant;
        string frac = dot >= 0 ? mant[(dot + 1)..] : "";
        string all = (intPart + frac).TrimStart('0');
        int leadingZeros = (intPart + frac).Length - all.Length;
        int exp = exp10 + intPart.Length - 1 - leadingZeros;
        all = all.TrimEnd('0');
        if (all.Length == 0)
            all = "0";
        _ = r;
        return (all, exp, value < 0);
    }

    /// <summary>Python <c>"%.{precision}g" % value</c>.</summary>
    public static string FormatG(double value, int precision)
    {
        if (double.IsNaN(value))
            return "nan";
        if (double.IsInfinity(value))
            return value > 0 ? "inf" : "-inf";
        if (precision == 0)
            precision = 1;
        if (value == 0)
            return double.IsNegative(value) ? "-0" : "0";
        string e = value.ToString("E" + (precision - 1), CultureInfo.InvariantCulture);
        int ePos = e.IndexOf('E', StringComparison.Ordinal);
        int exp = int.Parse(e[(ePos + 1)..], CultureInfo.InvariantCulture);
        if (exp >= -4 && exp < precision)
        {
            string fixedText = value.ToString("F" + (precision - 1 - exp), CultureInfo.InvariantCulture);
            if (fixedText.Contains('.', StringComparison.Ordinal))
                fixedText = fixedText.TrimEnd('0').TrimEnd('.');
            return fixedText;
        }
        string mant = e[..ePos];
        if (mant.Contains('.', StringComparison.Ordinal))
            mant = mant.TrimEnd('0').TrimEnd('.');
        return mant + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>Python <c>"%.{decimals}f" % value</c>: the exact binary value rounded half to even.</summary>
    public static string FormatF(double value, int decimals)
    {
        if (double.IsNaN(value))
            return "nan";
        if (double.IsInfinity(value))
            return value > 0 ? "inf" : "-inf";
        long bits = BitConverter.DoubleToInt64Bits(value);
        bool negative = bits < 0;
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
            exponent = 1;
        else
            mantissa |= 1L << 52;
        exponent -= 1075;
        BigInteger q;
        if (mantissa == 0)
        {
            q = BigInteger.Zero;
        }
        else if (exponent >= 0)
        {
            q = (new BigInteger(mantissa) << exponent) * BigInteger.Pow(10, decimals);
        }
        else if (exponent > -64 && decimals <= 18 && mantissa < (1L << 53))
        {
            // Fast path with 128-bit arithmetic (the shift is below 64 here).
            UInt128 num = (UInt128)(ulong)mantissa * (UInt128)Pow10(decimals);
            int shift = -exponent;
            UInt128 quotient = num >> shift;
            UInt128 remainder = num - (quotient << shift);
            UInt128 half = (UInt128)1 << (shift - 1);
            if (remainder > half || (remainder == half && (quotient & 1) == 1))
                quotient++;
            q = new BigInteger((ulong)(quotient >> 64)) << 64 | new BigInteger((ulong)quotient);
        }
        else
        {
            BigInteger num = new BigInteger(mantissa) * BigInteger.Pow(10, decimals);
            BigInteger den = BigInteger.One << -exponent;
            q = BigInteger.DivRem(num, den, out BigInteger rem);
            int cmp = (rem * 2).CompareTo(den);
            if (cmp > 0 || (cmp == 0 && !q.IsEven))
                q += 1;
        }
        string digits = q.ToString(CultureInfo.InvariantCulture);
        if (decimals > 0)
        {
            if (digits.Length <= decimals)
                digits = new string('0', decimals - digits.Length + 1) + digits;
            digits = digits[..^decimals] + "." + digits[^decimals..];
        }
        return negative ? "-" + digits : digits;
    }

    private static ulong Pow10(int n)
    {
        ulong r = 1;
        for (int i = 0; i < n; i++)
            r *= 10;
        return r;
    }

    /// <summary>Iterates a string by code point, as Python iterates a <c>str</c>.</summary>
    public static IEnumerable<string> Chars(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return text.Substring(i, 2);
                i++;
            }
            else
            {
                yield return text[i].ToString();
            }
        }
    }

    /// <summary>The number of code points (Python <c>len(str)</c>).</summary>
    public static int Len(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            count++;
        }
        return count;
    }

    /// <summary>Python <c>ord(ch)</c>.</summary>
    public static int Ord(string ch) => ch.Length == 2 && char.IsSurrogatePair(ch[0], ch[1]) ? char.ConvertToUtf32(ch[0], ch[1]) : ch[0];

    /// <summary>Python <c>chr(code)</c> (lone surrogates are kept as single UTF-16 units).</summary>
    public static string Chr(int code) => code is >= 0xD800 and <= 0xDFFF || code < 0x10000 ? ((char)code).ToString() : char.ConvertFromUtf32(code);

    /// <summary>Python <c>str(int)</c>.</summary>
    public static string Str(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Python <c>math.hypot(x, y)</c>.</summary>
    public static double Hypot(double x, double y) => double.Hypot(x, y);

    /// <summary>Python <c>math.radians</c>.</summary>
    public static double Radians(double degrees) => degrees * (Math.PI / 180.0);

    /// <summary>Python <c>math.degrees</c>.</summary>
    public static double Degrees(double radians) => radians * (180.0 / Math.PI);

    /// <summary>Python <c>math.sqrt</c> (raises on a negative argument).</summary>
    public static double Sqrt(double value)
    {
        if (value < 0)
            throw ValueError("math domain error");
        return Math.Sqrt(value);
    }

    /// <summary>Python <c>x ** y</c> on floats.</summary>
    public static double Pow(double x, double y)
    {
        if (x == 0.0 && y < 0)
            throw ZeroDivision("0.0 cannot be raised to a negative power");
        return Math.Pow(x, y);
    }
}
