using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XRay.Email.Msg.Mime;

internal static class HeaderParser
{
    private static readonly Regex EncodedWordPattern = new(@"=\?([^?\s]+)\?([bBqQ])\?([^?\s]*)\?=", RegexOptions.Compiled);

    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly Dictionary<string, int> ZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = 0,
        ["UTC"] = 0,
        ["GMT"] = 0,
        ["Z"] = 0,
        ["EST"] = -5 * 60,
        ["EDT"] = -4 * 60,
        ["CST"] = -6 * 60,
        ["CDT"] = -5 * 60,
        ["MST"] = -7 * 60,
        ["MDT"] = -6 * 60,
        ["PST"] = -8 * 60,
        ["PDT"] = -7 * 60,
        ["CET"] = 60,
        ["CEST"] = 120,
        ["BST"] = 60,
    };

    /// <summary>
    /// Splits an entity into its header block and body at the first empty line. An entity that
    /// starts with an empty line has no headers.
    /// </summary>
    public static (byte[] HeaderBytes, byte[] BodyBytes) SplitHeaderAndBody(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var lineStart = 0;
        while (lineStart < data.Length)
        {
            var lineEnd = lineStart;
            while (lineEnd < data.Length && data[lineEnd] != (byte)'\n')
            {
                lineEnd++;
            }

            var contentEnd = lineEnd > lineStart && data[lineEnd - 1] == (byte)'\r' ? lineEnd - 1 : lineEnd;
            if (lineEnd < data.Length && contentEnd == lineStart)
            {
                // Empty line: the header block ends before it, the body starts after it.
                return (data[..lineStart], data[(lineEnd + 1)..]);
            }

            lineStart = lineEnd + 1;
        }

        return (data, []);
    }

    /// <summary>
    /// Parses a header block. Bytes are read as UTF-8 when the block is valid UTF-8 (RFC 6532) and as
    /// ISO-8859-1 otherwise. Folded lines are unfolded by removing the line break only.
    /// </summary>
    public static IReadOnlyList<MimeHeader> ParseHeaders(byte[] headerBytes)
    {
        var text = (CharsetDecoder.IsValidUtf8(headerBytes) ? Encoding.UTF8.GetString(headerBytes) : Encoding.Latin1.GetString(headerBytes))
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = text.Split('\n');
        var logicalLines = new List<string>();
        var current = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if ((line[0] == ' ' || line[0] == '\t') && current.Length > 0)
            {
                current.Append(line);
                continue;
            }

            if (current.Length > 0 && !line.Contains(':'))
            {
                // A fold that lost its leading whitespace (seen in the wild for long encoded-word
                // subjects). It cannot start a header, so it continues the previous one.
                current.Append(' ').Append(line);
                continue;
            }

            if (current.Length > 0)
            {
                logicalLines.Add(current.ToString());
                current.Clear();
            }

            current.Append(line);
        }

        if (current.Length > 0)
        {
            logicalLines.Add(current.ToString());
        }

        var headers = new List<MimeHeader>(logicalLines.Count);
        foreach (var line in logicalLines)
        {
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = line[..separatorIndex].TrimEnd();
            if (name.Length == 0 || name.Any(ch => ch <= ' ' || ch > '~'))
            {
                // Not a header field, for example an mbox "From " separator line.
                continue;
            }

            headers.Add(MimeHeader.FromWire(name, line[(separatorIndex + 1)..].Trim()));
        }

        return headers;
    }

    /// <summary>
    /// Parses a structured header such as Content-Type or Content-Disposition from its wire form into
    /// the main value and a parameter map. RFC 2231 extended and continued parameters
    /// (<c>filename*=utf-8''...</c>, <c>name*0*=...</c>) are decoded, as are RFC 2047 encoded words
    /// inside parameter values, which many mailers emit.
    /// </summary>
    public static (string MainValue, IReadOnlyDictionary<string, string> Parameters) ParseStructuredHeaderValue(string? value, string? defaultMainValue)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return (defaultMainValue ?? string.Empty, parameters);
        }

        var segments = SplitParameters(value.Trim());
        var mainValue = segments.Count == 0 ? string.Empty : StripComments(segments[0]).Trim();
        if (mainValue.Length == 0)
        {
            mainValue = defaultMainValue ?? string.Empty;
        }

        var continuations = new Dictionary<string, SortedDictionary<int, (string Value, bool Extended)>>(StringComparer.OrdinalIgnoreCase);
        var extendedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < segments.Count; index++)
        {
            var segment = segments[index];
            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = segment[..equalsIndex].Trim();
            var parameterValue = Unquote(segment[(equalsIndex + 1)..].Trim());
            var extended = key.EndsWith('*');
            if (extended)
            {
                key = key[..^1];
            }

            var starIndex = key.LastIndexOf('*');
            if (starIndex > 0 && int.TryParse(key[(starIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var sectionNumber))
            {
                var baseName = key[..starIndex];
                if (!continuations.TryGetValue(baseName, out var sections))
                {
                    sections = [];
                    continuations[baseName] = sections;
                }

                sections[sectionNumber] = (parameterValue, extended);
                continue;
            }

            if (extended)
            {
                parameters[key] = DecodeExtendedValue(parameterValue);
                extendedKeys.Add(key);
            }
            else if (!extendedKeys.Contains(key))
            {
                // An RFC 2231 form of the same parameter, when present, wins over the plain one.
                parameters[key] = DecodeEncodedWords(parameterValue);
            }
        }

        foreach (var (name, sections) in continuations)
        {
            // RFC 2231 section 4.1: the charset is named in the first section only, and each
            // section is percent-encoded only when it carries the trailing asterisk.
            var bytes = new List<byte>();
            string? charset = null;
            var first = true;
            foreach (var (_, (sectionValue, sectionExtended)) in sections)
            {
                if (sectionExtended)
                {
                    var text = sectionValue;
                    if (first)
                    {
                        var parts = sectionValue.Split('\'', 3);
                        if (parts.Length == 3)
                        {
                            charset = parts[0];
                            text = parts[2];
                        }
                    }

                    bytes.AddRange(PercentDecode(text));
                }
                else
                {
                    bytes.AddRange(Encoding.Latin1.GetBytes(sectionValue));
                }

                first = false;
            }

            var decoded = charset is null ? CharsetDecoder.DecodeUnknown(bytes.ToArray()) : CharsetDecoder.DecodeText(bytes.ToArray(), charset);
            parameters[name] = DecodeEncodedWords(decoded);
        }

        return (mainValue, parameters);
    }

    /// <summary>
    /// Parses an RFC 5322 date, including the obsolete forms still common in archives: comments,
    /// missing day of week, two-digit years, named zones (GMT, EST, ...) and missing seconds.
    /// </summary>
    public static DateTimeOffset? ParseDateHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = StripComments(value).Replace(',', ' ');
        var tokens = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count > 0 && tokens[0].Length >= 3 && char.IsLetter(tokens[0][0]) && MonthIndex(tokens[0]) < 0)
        {
            tokens.RemoveAt(0); // day of week
        }

        int? day = null;
        int? month = null;
        int? year = null;
        TimeSpan? time = null;
        int? offsetMinutes = null;
        foreach (var rawToken in tokens)
        {
            var token = rawToken;
            if (time is not null && offsetMinutes is null && token.Length == 6 && (token[0] == '+' || token[0] == '-') && token[3] == ':')
            {
                token = token.Remove(3, 1); // "+01:00", written by some generators instead of "+0100"
            }

            if (time is null && token.Contains(':'))
            {
                var parts = token.Split(':');
                if (parts.Length is 2 or 3 &&
                    int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) &&
                    int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
                {
                    var seconds = 0;
                    if (parts.Length == 3 && !int.TryParse(parts[2].Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
                    {
                        return FallbackParse(value);
                    }

                    if (hours > 23 || minutes > 59 || seconds > 60)
                    {
                        return FallbackParse(value);
                    }

                    time = new TimeSpan(hours, minutes, Math.Min(seconds, 59));
                    continue;
                }
            }

            if (time is not null && offsetMinutes is null && (token[0] == '+' || token[0] == '-') && token.Length == 5 &&
                int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var zone))
            {
                var minutes = ((zone / 100) * 60) + (zone % 100);
                offsetMinutes = token[0] == '-' ? -minutes : minutes;
                continue;
            }

            if (month is null && MonthIndex(token) is var monthIndex && monthIndex >= 0)
            {
                month = monthIndex + 1;
                continue;
            }

            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                if (day is null && month is null && number is >= 1 and <= 31 && token.Length <= 2)
                {
                    day = number;
                }
                else if (year is null && (month is not null || token.Length >= 3))
                {
                    year = token.Length switch
                    {
                        <= 2 => number < 50 ? 2000 + number : 1900 + number,
                        3 => 1900 + number,
                        _ => number,
                    };
                }
                else if (day is null && number is >= 1 and <= 31)
                {
                    day = number;
                }

                continue;
            }

            if (time is not null && offsetMinutes is null)
            {
                if (ZoneOffsets.TryGetValue(token, out var named))
                {
                    offsetMinutes = named;
                }
                else if (token.Length == 1 && char.IsLetter(token[0]))
                {
                    offsetMinutes = 0; // military zones are ambiguous; RFC 5322 says treat as -0000
                }
            }
        }

        if (day is null || month is null || year is null)
        {
            return FallbackParse(value);
        }

        try
        {
            var date = new DateTime(year.Value, month.Value, day.Value, 0, 0, 0, DateTimeKind.Unspecified) + (time ?? TimeSpan.Zero);
            var offset = TimeSpan.FromMinutes(offsetMinutes ?? 0);
            if (offset < TimeSpan.FromHours(-14) || offset > TimeSpan.FromHours(14))
            {
                offset = TimeSpan.Zero;
            }

            return new DateTimeOffset(date, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return FallbackParse(value);
        }
    }

    /// <summary>
    /// Decodes RFC 2047 encoded words. Whitespace between two adjacent encoded words is removed, and
    /// adjacent words in the same charset are joined at the byte level before decoding, so a multi-byte
    /// character split across two words decodes correctly.
    /// </summary>
    public static string DecodeEncodedWords(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("=?", StringComparison.Ordinal))
        {
            return value;
        }

        var matches = EncodedWordPattern.Matches(value);
        if (matches.Count == 0)
        {
            return value;
        }

        var output = new StringBuilder(value.Length);
        var pendingBytes = new List<byte>();
        string? pendingCharset = null;
        var position = 0;
        foreach (Match match in matches)
        {
            var gap = value[position..match.Index];
            var adjacent = pendingCharset is not null && string.IsNullOrWhiteSpace(gap);
            if (!adjacent)
            {
                Flush();
                output.Append(gap);
            }

            var charset = match.Groups[1].Value;
            var languageIndex = charset.IndexOf('*');
            if (languageIndex >= 0)
            {
                charset = charset[..languageIndex]; // RFC 2231 section 5 language suffix
            }

            byte[] bytes;
            try
            {
                bytes = match.Groups[2].Value.Equals("B", StringComparison.OrdinalIgnoreCase)
                    ? TransferEncodingDecoder.DecodeBase64Lenient(match.Groups[3].Value)
                    : TransferEncodingDecoder.DecodeEncodedWordQuotedPrintable(match.Groups[3].Value);
            }
            catch (FormatException)
            {
                Flush();
                output.Append(match.Value);
                position = match.Index + match.Length;
                continue;
            }

            if (pendingCharset is not null && !string.Equals(pendingCharset, charset, StringComparison.OrdinalIgnoreCase))
            {
                Flush();
            }

            pendingCharset = charset;
            pendingBytes.AddRange(bytes);
            position = match.Index + match.Length;
        }

        Flush();
        output.Append(value[position..]);
        return output.ToString();

        void Flush()
        {
            if (pendingCharset is null)
            {
                return;
            }

            output.Append(CharsetDecoder.DecodeText(pendingBytes.ToArray(), pendingCharset));
            pendingBytes.Clear();
            pendingCharset = null;
        }
    }

    /// <summary>Removes RFC 5322 comments (parenthesised, nestable) outside quoted strings.</summary>
    public static string StripComments(string value)
    {
        if (!value.Contains('('))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var depth = 0;
        var inQuotes = false;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == '\\' && index + 1 < value.Length && (inQuotes || depth > 0))
            {
                if (depth == 0)
                {
                    builder.Append(ch).Append(value[index + 1]);
                }

                index++;
                continue;
            }

            if (depth == 0 && ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && ch == '(')
            {
                depth++;
                continue;
            }
            else if (!inQuotes && ch == ')' && depth > 0)
            {
                depth--;
                continue;
            }

            if (depth == 0)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static List<string> SplitParameters(string input)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < input.Length; index++)
        {
            var ch = input[index];
            if (inQuotes && ch == '\\' && index + 1 < input.Length)
            {
                current.Append(ch).Append(input[index + 1]);
                index++;
                continue;
            }

            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }

            if (ch == ';' && !inQuotes)
            {
                segments.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return segments;
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"')
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 1; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch == '\\' && index + 1 < value.Length)
            {
                builder.Append(value[++index]);
                continue;
            }

            if (ch == '"')
            {
                break;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string DecodeExtendedValue(string value)
    {
        string? charset = null;
        var text = value;
        var parts = value.Split('\'', 3);
        if (parts.Length == 3)
        {
            charset = parts[0];
            text = parts[2];
        }

        var bytes = PercentDecode(text);
        return string.IsNullOrEmpty(charset) ? CharsetDecoder.DecodeUnknown(bytes) : CharsetDecoder.DecodeText(bytes, charset);
    }

    private static byte[] PercentDecode(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '%' && index + 2 < text.Length &&
                byte.TryParse(text.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var decoded))
            {
                bytes.Add(decoded);
                index += 2;
                continue;
            }

            if (ch <= 0xFF)
            {
                bytes.Add((byte)ch);
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(ch.ToString()));
            }
        }

        return bytes.ToArray();
    }

    private static int MonthIndex(string token)
    {
        if (token.Length < 3)
        {
            return -1;
        }

        var prefix = token[..3].ToLowerInvariant();
        return Array.IndexOf(MonthNames, prefix) is var index && index >= 0 && token.All(char.IsLetter) ? index : -1;
    }

    private static DateTimeOffset? FallbackParse(string value)
    {
        return DateTimeOffset.TryParse(StripComments(value).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }
}
