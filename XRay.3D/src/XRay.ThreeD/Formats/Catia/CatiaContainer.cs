using System.Buffers.Binary;
using System.Text;

namespace XRay.ThreeD.Formats.Catia;

using XRay.ThreeD.Internal;

/// <summary>
/// The CATIA V5 file container (see docs/catia-format.md): a 64-byte header (<c>V5_CFV2</c>, a big-endian offset and length of a trailing stream directory), the stream bytes
/// in the file body and a directory of named logical streams, each a list of extents. A file can hold a second, nested container (the graphical representation cache) with
/// its own directory; its offsets are relative to its own magic. Directories are found by a self-consistency scan, so every candidate is validated before it is trusted.
/// </summary>
internal sealed class CatiaContainer
{
    internal const string Magic = "V5_CFV2\0";
    internal const string DirectoryMagic = "CATIA_V5 CB0001\0";

    private const int MaxDirectoryBytes = 4 << 20;
    private const int MaxExtents = 64;

    private readonly ReadOnlyMemory<byte> _data;

    private CatiaContainer(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    /// <summary>One named logical stream: its extents are concatenated in order.</summary>
    internal sealed record Stream(string Name, bool Nested, long Length, (long Offset, int Length)[] Extents);

    public List<Stream> Streams { get; } = new();

    /// <summary>Offset of the nested container's magic, or -1 when the file has none.</summary>
    public long InnerOffset { get; private set; } = -1;

    public static bool Sniff(ReadOnlySpan<byte> head) => head.Length >= 8 && head[..6].SequenceEqual("V5_CFV"u8) && head[6] == (byte)'2' && head[7] == 0;

    /// <summary>Opens the outer container and, when present, the nested one. Null when the header or the outer directory is damaged.</summary>
    public static CatiaContainer? TryOpen(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        var span = data.Span;
        if (span.Length < 0x40 || !Sniff(span))
        {
            return null;
        }

        var container = new CatiaContainer(data);
        var directoryOffset = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
        var directoryLength = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
        if (!container.ReadDirectory(0, directoryOffset, directoryLength, nested: false, token))
        {
            return null;
        }

        // The nested container: the first magic after the header whose own directory is consistent.
        var from = 8;
        while (from < span.Length - 16)
        {
            token.ThrowIfCancellationRequested();
            var at = span[from..].IndexOf("V5_CFV2\0"u8);
            if (at < 0)
            {
                break;
            }

            var inner = from + at;
            if (inner + 16 <= span.Length)
            {
                var delta = BinaryPrimitives.ReadUInt32BigEndian(span[(inner + 8)..]);
                var length = BinaryPrimitives.ReadUInt32BigEndian(span[(inner + 12)..]);
                if (container.ReadDirectory(inner, inner + (long)delta, length, nested: true, token))
                {
                    container.InnerOffset = inner;
                    break;
                }
            }

            from = inner + 1;
        }

        return container;
    }

    private bool ReadDirectory(long baseOffset, long directoryOffset, long directoryLength, bool nested, CancellationToken token)
    {
        var span = _data.Span;
        if (directoryOffset < 0 || directoryLength < DirectoryMagic.Length + 8 || directoryLength > MaxDirectoryBytes || directoryOffset + directoryLength > span.Length)
        {
            return false;
        }

        var region = span.Slice((int)directoryOffset, (int)directoryLength);
        if (!region.StartsWith("CATIA_V5 CB0001\0"u8))
        {
            return false;
        }

        var before = Streams.Count;
        Span<(long, int)> scratch = stackalloc (long, int)[MaxExtents];
        for (var i = 0x50; i + 4 + 20 <= region.Length; i++)
        {
            Cancel.Every(token, i);
            var k = BinaryPrimitives.ReadUInt32BigEndian(region[i..]);
            if (k is < 1 or > MaxExtents || i + 4 + 20 * (long)k > region.Length)
            {
                continue;
            }

            long cumulative = 0;
            var ok = true;
            for (var e = 0; e < k; e++)
            {
                var p = i + 4 + 20 * e;
                var physicalOffset = BinaryPrimitives.ReadUInt32BigEndian(region[p..]);
                var physicalLength = BinaryPrimitives.ReadUInt32BigEndian(region[(p + 4)..]);
                var logicalLength = BinaryPrimitives.ReadUInt32BigEndian(region[(p + 8)..]);
                var logicalOffset = BinaryPrimitives.ReadUInt32BigEndian(region[(p + 12)..]);
                if (physicalLength == 0 || baseOffset + physicalOffset + physicalLength > span.Length || logicalOffset != cumulative || logicalLength != physicalLength)
                {
                    ok = false;
                    break;
                }

                scratch[e] = (baseOffset + physicalOffset, (int)physicalLength);
                cumulative += physicalLength;
            }

            var ds = i - 0x50;
            if (!ok || ds < 0 || BinaryPrimitives.ReadUInt32BigEndian(region[(ds + 0x0c)..]) != cumulative)
            {
                continue;
            }

            var extents = new (long, int)[k];
            scratch[..(int)k].CopyTo(extents);
            Streams.Add(new Stream(NameBefore(region, ds), nested, cumulative, extents));
            i += 4 + 20 * (int)k - 1;
        }

        return Streams.Count > before;
    }

    /// <summary>The UTF-16 name that ends three bytes before a descriptor; a printable length prefix that falls into the run is dropped.</summary>
    private static string NameBefore(ReadOnlySpan<byte> region, int ds)
    {
        var end = ds - 3;
        if (end < 2)
        {
            return "";
        }

        var start = end;
        while (start >= 2 && region[start - 1] == 0 && region[start - 2] is >= 0x20 and < 0x7f)
        {
            start -= 2;
        }

        if (end - start < 4)
        {
            return "";
        }

        var chars = (end - start) / 2;
        if (region[start] == 2 * chars && chars > 2)
        {
            start += 2;
            chars--;
        }

        var sb = new StringBuilder(chars);
        for (var p = start; p < end; p += 2)
        {
            sb.Append((char)region[p]);
        }

        return sb.ToString();
    }

    /// <summary>The logical stream's bytes, or null when it is longer than <paramref name="limit"/>.</summary>
    public byte[]? Read(Stream stream, long limit)
    {
        if (stream.Length > limit || stream.Length > int.MaxValue / 2)
        {
            return null;
        }

        var buffer = new byte[stream.Length];
        var at = 0;
        var span = _data.Span;
        foreach (var (offset, length) in stream.Extents)
        {
            span.Slice((int)offset, length).CopyTo(buffer.AsSpan(at));
            at += length;
        }

        return buffer;
    }

    public IEnumerable<Stream> Named(string name, bool? nested = null) => Streams.Where(s => string.Equals(s.Name, name, StringComparison.Ordinal) && (nested is null || s.Nested == nested));
}
