using System.Text;

namespace XRay.Ics.Internal;

/// <summary>Turns the bytes of a calendar into text, detecting BOMs, UTF-16 and legacy code pages.</summary>
internal static class TextDecoder
{
    private static readonly object Gate = new();
    private static bool registered;

    public static void EnsureCodePages()
    {
        if (registered)
        {
            return;
        }

        lock (Gate)
        {
            if (!registered)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                registered = true;
            }
        }
    }

    public static Encoding? TryGetEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        EnsureCodePages();
        var trimmed = name.Trim().Trim('"');
        // Windows-1252 is what producers mean by "ISO-8859-1" (its C1 range holds the euro sign, quotes and dashes).
        if (trimmed.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("latin1", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("iso8859-1", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("us-ascii", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("ascii", StringComparison.OrdinalIgnoreCase))
        {
            return Windows1252();
        }

        try
        {
            return Encoding.GetEncoding(trimmed, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
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

    public static Encoding Windows1252()
    {
        EnsureCodePages();
        return Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>
    /// Decodes <paramref name="data"/>. When it is neither BOM-marked, UTF-16 nor valid UTF-8,
    /// <paramref name="legacy"/> is true and the text holds one character per byte (Latin-1), so a
    /// per-line <c>CHARSET</c> can still re-decode the original bytes.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> data, out bool legacy)
    {
        legacy = false;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return DecodeLossy(Encoding.UTF8, data[3..]);
        }

        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0 && data[3] == 0)
        {
            return DecodeLossy(new UTF32Encoding(false, false), data[4..]);
        }

        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0xFE && data[3] == 0xFF)
        {
            return DecodeLossy(new UTF32Encoding(true, false), data[4..]);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return DecodeLossy(Encoding.Unicode, data[2..]);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return DecodeLossy(Encoding.BigEndianUnicode, data[2..]);
        }

        if (LooksLikeUtf16(data, out var bigEndian))
        {
            return DecodeLossy(bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode, data);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            legacy = true;
            return Encoding.Latin1.GetString(data);
        }
    }

    private static string DecodeLossy(Encoding encoding, ReadOnlySpan<byte> data) => encoding.GetString(data);

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> data, out bool bigEndian)
    {
        bigEndian = false;
        var sample = Math.Min(data.Length, 64);
        if (sample < 4)
        {
            return false;
        }

        int evenZero = 0, oddZero = 0;
        for (var i = 0; i < sample; i++)
        {
            if (data[i] == 0)
            {
                if ((i & 1) == 0)
                {
                    evenZero++;
                }
                else
                {
                    oddZero++;
                }
            }
        }

        var half = sample / 2;
        if (oddZero >= half * 3 / 4 && evenZero == 0)
        {
            return true;
        }

        if (evenZero >= half * 3 / 4 && oddZero == 0)
        {
            bigEndian = true;
            return true;
        }

        return false;
    }
}
