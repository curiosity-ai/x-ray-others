using System.Text;

namespace XRay.Email.Msg.Mime;

internal static class CharsetDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["utf8"] = "utf-8",
        ["unicode-1-1-utf-8"] = "utf-8",
        ["unicode-2-0-utf-8"] = "utf-8",
        ["x-unicode20utf8"] = "utf-8",
        ["latin1"] = "iso-8859-1",
        ["latin-1"] = "iso-8859-1",
        ["ansi_x3.4-1968"] = "us-ascii",
        ["ascii"] = "us-ascii",
        ["cp1252"] = "windows-1252",
        ["cp1251"] = "windows-1251",
        ["cp1250"] = "windows-1250",
        ["iso-8859-8-i"] = "iso-8859-8",
        ["gb2312"] = "gb18030",
        ["gbk"] = "gb18030",
        ["x-gbk"] = "gb18030",
        ["ks_c_5601-1987"] = "ks_c_5601-1987",
        ["x-sjis"] = "shift_jis",
        ["sjis"] = "shift_jis",
        ["x-mac-roman"] = "macintosh",
    };

    static CharsetDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Decodes text in the declared charset. With no usable charset the bytes are read as UTF-8
    /// when they are valid UTF-8, and as Windows-1252 otherwise, rather than as UTF-8 with
    /// replacement characters.
    /// </summary>
    public static string DecodeText(byte[] value, string? charset)
    {
        var encoding = TryGetEncoding(charset);
        var text = encoding is not null ? encoding.GetString(value) : DecodeUnknown(value);
        return text.TrimEnd('\0');
    }

    public static byte[] EncodeText(string value, string? charset)
    {
        return GetEncoding(charset).GetBytes(value);
    }

    public static Encoding GetEncoding(string? charset) => TryGetEncoding(charset) ?? Encoding.UTF8;

    /// <summary>Decodes bytes of unknown charset: UTF-8 when valid, else Windows-1252.</summary>
    public static string DecodeUnknown(byte[] value)
    {
        try
        {
            return StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException)
        {
            return Windows1252().GetString(value);
        }
    }

    public static bool IsValidUtf8(byte[] value)
    {
        try
        {
            StrictUtf8.GetCharCount(value);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static Encoding? TryGetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return null;
        }

        var name = charset.Trim().Trim('"', '\'').Trim();
        if (Aliases.TryGetValue(name, out var alias))
        {
            name = alias;
        }

        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static Encoding Windows1252()
    {
        try
        {
            return Encoding.GetEncoding(1252);
        }
        catch (NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }
}
