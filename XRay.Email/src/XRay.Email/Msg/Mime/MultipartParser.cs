using System.Text;

namespace XRay.Email.Msg.Mime;

internal static class MultipartParser
{
    /// <summary>
    /// Splits a multipart body into its parts (RFC 2046 5.1.1). Boundary lines may carry trailing
    /// whitespace; the line break before a boundary belongs to the boundary, so exactly one is removed
    /// from each part; a missing closing boundary ends the last part at the end of the data.
    /// </summary>
    public static IReadOnlyList<byte[]> SplitParts(byte[] bodyBytes, string boundary)
    {
        ArgumentNullException.ThrowIfNull(bodyBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);

        var delimiter = Encoding.Latin1.GetBytes($"--{boundary}");
        var parts = new List<byte[]>();
        var activePartStart = -1;
        foreach (var (lineStart, contentEnd, nextLineStart) in EnumerateLines(bodyBytes))
        {
            var kind = ClassifyBoundaryLine(bodyBytes.AsSpan(lineStart, contentEnd - lineStart), delimiter);
            if (kind == BoundaryKind.None)
            {
                continue;
            }

            if (activePartStart >= 0)
            {
                AddPart(parts, bodyBytes, activePartStart, lineStart);
            }

            if (kind == BoundaryKind.Close)
            {
                return parts;
            }

            activePartStart = nextLineStart;
        }

        if (activePartStart >= 0 && activePartStart < bodyBytes.Length)
        {
            AddPart(parts, bodyBytes, activePartStart, bodyBytes.Length, trimAllTrailingBreaks: true);
        }

        return parts;
    }

    private static void AddPart(List<byte[]> parts, byte[] bodyBytes, int start, int end, bool trimAllTrailingBreaks = false)
    {
        var length = end - start;
        if (trimAllTrailingBreaks)
        {
            while (length > 0 && bodyBytes[start + length - 1] is (byte)'\r' or (byte)'\n')
            {
                length--;
            }
        }
        else if (length > 0 && bodyBytes[start + length - 1] == (byte)'\n')
        {
            length--;
            if (length > 0 && bodyBytes[start + length - 1] == (byte)'\r')
            {
                length--;
            }
        }

        if (length > 0)
        {
            parts.Add(bodyBytes.AsSpan(start, length).ToArray());
        }
    }

    private static BoundaryKind ClassifyBoundaryLine(ReadOnlySpan<byte> line, byte[] delimiter)
    {
        if (!line.StartsWith(delimiter))
        {
            return BoundaryKind.None;
        }

        var rest = line[delimiter.Length..];
        var kind = BoundaryKind.Open;
        if (rest.StartsWith("--"u8))
        {
            kind = BoundaryKind.Close;
            rest = rest[2..];
        }

        foreach (var b in rest)
        {
            if (b is not ((byte)' ' or (byte)'\t'))
            {
                return BoundaryKind.None;
            }
        }

        return kind;
    }

    private static IEnumerable<(int LineStart, int ContentEnd, int NextLineStart)> EnumerateLines(byte[] bytes)
    {
        var index = 0;
        while (index < bytes.Length)
        {
            var lineStart = index;
            while (index < bytes.Length && bytes[index] is not ((byte)'\r') and not ((byte)'\n'))
            {
                index++;
            }

            var contentEnd = index;
            if (index < bytes.Length && bytes[index] == (byte)'\r')
            {
                index++;
            }

            if (index < bytes.Length && bytes[index] == (byte)'\n')
            {
                index++;
            }

            yield return (lineStart, contentEnd, index);
        }
    }

    private enum BoundaryKind
    {
        None,
        Open,
        Close,
    }
}
