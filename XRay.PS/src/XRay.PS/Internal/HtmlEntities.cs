// A port of Python's html.unescape, which the XPS reader applied to Glyphs UnicodeString values.

using System.Globalization;
using System.Text.RegularExpressions;

namespace XRay.PS.Internal;

internal static partial class HtmlEntities
{
    [GeneratedRegex(@"&(#[0-9]+;?|#[xX][0-9a-fA-F]+;?|[^\t\n\f <&#;]{1,32};?)")]
    private static partial Regex CharRef();

    /// <summary>Replaces named and numeric character references.</summary>
    public static string Unescape(string text)
    {
        if (!text.Contains('&', StringComparison.Ordinal))
            return text;
        return CharRef().Replace(text, m => Replace(m.Groups[1].Value));
    }

    private static string Replace(string s)
    {
        if (s[0] == '#')
        {
            bool hex = s.Length > 1 && (s[1] == 'x' || s[1] == 'X');
            string digits = (hex ? s[2..] : s[1..]).TrimEnd(';');
            int num;
            if (!int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out num))
                num = int.MaxValue; // beyond 0x10FFFF
            if (InvalidCharRefs.TryGetValue(num, out string? replacement))
                return replacement;
            if (num is >= 0xD800 and <= 0xDFFF || num > 0x10FFFF)
                return "�";
            if (InvalidCodePoints.Contains(num))
                return "";
            return char.ConvertFromUtf32(num);
        }
        if (Named.TryGetValue(s, out string? named))
            return named;
        for (int x = s.Length - 1; x > 1; x--)
        {
            if (Named.TryGetValue(s[..x], out string? prefix))
                return prefix + s[x..];
        }
        return "&" + s;
    }
}
