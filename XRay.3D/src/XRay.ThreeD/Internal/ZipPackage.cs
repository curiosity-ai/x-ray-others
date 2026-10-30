using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace XRay.ThreeD.Internal;

/// <summary>
/// A small ZIP reader for packages whose entries .NET's <see cref="ZipArchive"/> cannot open: method 93 (Zstandard, written by Fusion 360) next to stored and
/// deflated entries. The central directory is read from the end (offsets may count from the start of the package when something precedes it), declared sizes
/// are never trusted beyond the caller's limit, and entries needing ZIP64 or encryption are listed but not readable.
/// </summary>
internal sealed class ZipPackage
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly long _shift;
    private readonly List<Entry> _entries = new();

    public sealed record Entry(string Name, int Method, long CompressedSize, long Size, long HeaderOffset, int Flags);

    private ZipPackage(ReadOnlyMemory<byte> data, long shift)
    {
        _data = data;
        _shift = shift;
    }

    public IReadOnlyList<Entry> Entries => _entries;

    public static ZipPackage? TryOpen(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        var d = data.Span;
        var from = Math.Max(0, d.Length - 65557);
        var eocd = d[from..].LastIndexOf(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
        if (eocd < 0 || from + eocd + 22 > d.Length)
        {
            return null;
        }

        eocd += from;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(d[(eocd + 10)..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(d[(eocd + 12)..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(d[(eocd + 16)..]);
        var shift = (long)eocd - size - offset;
        if (shift < 0 || count > 100_000 || offset + shift + size > d.Length)
        {
            return null;
        }

        var package = new ZipPackage(data, shift);
        var p = offset + shift;
        for (var i = 0; i < count; i++)
        {
            Cancel.Every(token, i);
            if (p + 46 > d.Length || BinaryPrimitives.ReadUInt32LittleEndian(d[(int)p..]) != 0x02014B50)
            {
                return null;
            }

            var s = d[(int)p..];
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(s[10..]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
            var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(s[30..]);
            var comment = BinaryPrimitives.ReadUInt16LittleEndian(s[32..]);
            var header = BinaryPrimitives.ReadUInt32LittleEndian(s[42..]);
            if (p + 46 + nameLength + extra + comment > d.Length)
            {
                return null;
            }

            var name = Encoding.UTF8.GetString(s.Slice(46, nameLength));
            package._entries.Add(new Entry(name, method, compressed, uncompressed, header, flags));
            p += 46 + nameLength + extra + comment;
        }

        return package;
    }

    public Entry? Find(string name) => _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

    /// <summary>The entry's bytes, or null when it cannot be read (unsupported method, encrypted, damaged). Throws when it would inflate past <paramref name="limit"/>.</summary>
    public byte[]? Read(Entry entry, long limit, CancellationToken token)
    {
        if ((entry.Flags & 1) != 0 || entry.CompressedSize == 0xFFFFFFFFL || entry.Size == 0xFFFFFFFFL)
        {
            return null;
        }

        var d = _data.Span;
        var at = entry.HeaderOffset + _shift;
        if (at < 0 || at + 30 > d.Length || BinaryPrimitives.ReadUInt32LittleEndian(d[(int)at..]) != 0x04034B50)
        {
            return null;
        }

        var start = at + 30 + BinaryPrimitives.ReadUInt16LittleEndian(d[(int)(at + 26)..]) + BinaryPrimitives.ReadUInt16LittleEndian(d[(int)(at + 28)..]);
        if (start + entry.CompressedSize > d.Length)
        {
            return null;
        }

        var raw = _data.Slice((int)start, (int)entry.CompressedSize);
        switch (entry.Method)
        {
            case 0:
                return entry.CompressedSize > limit ? throw TooLarge(entry, limit) : raw.ToArray();
            case 8:
            {
                using var deflate = new DeflateStream(new MemoryStream(raw.ToArray(), writable: false), CompressionMode.Decompress);
                var output = new MemoryStream();
                var buffer = new byte[81920];
                try
                {
                    int read;
                    while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        if (output.Length + read > limit)
                        {
                            throw TooLarge(entry, limit);
                        }

                        output.Write(buffer, 0, read);
                    }
                }
                catch (InvalidDataException)
                {
                    return null;
                }

                return output.ToArray();
            }

            case 93:
                return ZstdDecoder.Decompress(raw.Span, limit, token);
            default:
                return null;
        }
    }

    private static ModelFormatException TooLarge(Entry entry, long limit) =>
        new($"The ZIP entry '{entry.Name}' is larger than the {limit} byte limit.");
}
