using System.Text;

namespace XRay.Email.Msg.Mime;

internal static class TransferEncodingDecoder
{
    public static byte[] Decode(string? transferEncoding, byte[] bodyBytes)
    {
        if (bodyBytes.Length == 0)
        {
            return [];
        }

        return transferEncoding?.Trim().ToLowerInvariant() switch
        {
            "base64" => DecodeBase64Lenient(Encoding.Latin1.GetString(bodyBytes)),
            "quoted-printable" => DecodeQuotedPrintable(bodyBytes),
            _ => bodyBytes.ToArray(),
        };
    }

    /// <summary>Strict base64 decode; returns the ASCII bytes of the input when it is not base64.</summary>
    public static byte[] DecodeBase64(string value)
    {
        try
        {
            var normalized = new string(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            return string.IsNullOrEmpty(normalized) ? [] : Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            return Encoding.ASCII.GetBytes(value);
        }
    }

    /// <summary>
    /// Base64 decode that tolerates what real mailers produce: line breaks, stray characters outside
    /// the alphabet, missing padding, and data after the padding.
    /// </summary>
    public static byte[] DecodeBase64Lenient(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '=')
            {
                break;
            }

            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')
            {
                builder.Append(ch);
            }
        }

        switch (builder.Length % 4)
        {
            case 1:
                builder.Length--; // a single trailing sextet carries no complete byte
                break;
            case 2:
                builder.Append("==");
                break;
            case 3:
                builder.Append('=');
                break;
        }

        return builder.Length == 0 ? [] : Convert.FromBase64String(builder.ToString());
    }

    public static byte[] DecodeQuotedPrintable(byte[] value)
    {
        var output = new List<byte>(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current is (byte)' ' or (byte)'\t')
            {
                // RFC 2045 6.7 rule 3: trailing whitespace on an encoded line is not data.
                var lookahead = index;
                while (lookahead < value.Length && value[lookahead] is (byte)' ' or (byte)'\t')
                {
                    lookahead++;
                }

                if (lookahead == value.Length || value[lookahead] is (byte)'\r' or (byte)'\n')
                {
                    index = lookahead - 1;
                    continue;
                }

                output.Add(current);
                continue;
            }

            if (current != (byte)'=')
            {
                output.Add(current);
                continue;
            }

            // Soft line break, allowing whitespace between '=' and the line break.
            var next = index + 1;
            while (next < value.Length && value[next] is (byte)' ' or (byte)'\t')
            {
                next++;
            }

            if (next < value.Length && value[next] == (byte)'\n')
            {
                index = next;
                continue;
            }

            if (next + 1 < value.Length && value[next] == (byte)'\r' && value[next + 1] == (byte)'\n')
            {
                index = next + 1;
                continue;
            }

            if (next == value.Length)
            {
                index = next - 1;
                continue;
            }

            if (index + 2 < value.Length && IsHex(value[index + 1]) && IsHex(value[index + 2]))
            {
                output.Add((byte)((HexValue(value[index + 1]) << 4) | HexValue(value[index + 2])));
                index += 2;
                continue;
            }

            output.Add(current);
        }

        return output.ToArray();
    }

    public static byte[] DecodeEncodedWordQuotedPrintable(string value)
    {
        return DecodeQuotedPrintable(Encoding.Latin1.GetBytes(value.Replace('_', ' ')));
    }

    private static bool IsHex(byte value) => value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

    private static int HexValue(byte value) => value switch
    {
        <= (byte)'9' => value - '0',
        <= (byte)'F' => value - 'A' + 10,
        _ => value - 'a' + 10,
    };
}
