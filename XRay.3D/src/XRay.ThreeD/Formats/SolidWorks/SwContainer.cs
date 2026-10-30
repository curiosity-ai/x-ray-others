using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.SolidWorks;

/// <summary>
/// The container SolidWorks 2015 and later write: a run of ZIP-like entries (version 0x14, flags 6,
/// deflate) whose signature differs per file and whose names have the two nibbles of every byte swapped.
/// Some entries near the start are decoys that claim sizes they do not have; an entry counts only
/// when its data inflates to the stated size and matches its CRC.
/// </summary>
internal sealed class SwContainer
{
    private static readonly byte[] EntryMarker = [0x14, 0x00, 0x06, 0x00, 0x08, 0x00];

    private readonly ReadOnlyMemory<byte> _data;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private SwContainer(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    private readonly record struct Entry(int DataStart, int CompressedSize, int Size, uint Crc);

    public IReadOnlyCollection<string> Names => _entries.Keys;

    public static SwContainer? TryOpen(ReadOnlyMemory<byte> data, CancellationToken cancellation = default)
    {
        var container = new SwContainer(data);
        var span = data.Span;
        var position = 0;
        while (position < span.Length - 30)
        {
            var hit = span[position..].IndexOf(EntryMarker);
            if (hit < 0)
            {
                break;
            }

            var marker = position + hit;
            position = marker + 1;
            var start = marker - 4;
            if (start < 0 || start + 30 > span.Length)
            {
                continue;
            }

            cancellation.ThrowIfCancellationRequested();
            var crc = U32(span, start + 14);
            var compressed = U32(span, start + 18);
            var size = U32(span, start + 22);
            var nameLength = U16(span, start + 26);
            var extraLength = U16(span, start + 28);
            var dataStart = (long)start + 30 + nameLength + extraLength;
            if (nameLength == 0 || nameLength > 512 || dataStart + compressed > span.Length || size > int.MaxValue || compressed > int.MaxValue)
            {
                continue;
            }

            var name = DecodeName(span.Slice(start + 30, nameLength));
            if (name is null)
            {
                continue;
            }

            var raw = BinaryHelpers.InflateExact(data.Slice((int)dataStart, (int)compressed), (int)size);
            if (raw is null || Crc32.Update(0, raw) != crc)
            {
                continue;
            }

            container._entries.TryAdd(name, new Entry((int)dataStart, (int)compressed, (int)size, crc));
            position = (int)dataStart + (int)compressed;
        }

        return container._entries.Count >= 3 ? container : null;
    }

    public bool Contains(string name) => _entries.ContainsKey(name);

    /// <summary>The stream's bytes, or null when there is none of that name.</summary>
    public byte[]? Read(string name)
    {
        if (!_entries.TryGetValue(name, out var entry))
        {
            return null;
        }

        return BinaryHelpers.InflateExact(_data.Slice(entry.DataStart, entry.CompressedSize), entry.Size);
    }

    private static string? DecodeName(ReadOnlySpan<byte> raw)
    {
        var swapped = new byte[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var b = raw[i];
            swapped[i] = (byte)((b >> 4) | (b << 4));
            if (swapped[i] < 0x20 || swapped[i] == 0x7F)
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(swapped);
    }

    private static uint U32(ReadOnlySpan<byte> s, int o) => (uint)(s[o] | (s[o + 1] << 8) | (s[o + 2] << 16) | (s[o + 3] << 24));

    private static int U16(ReadOnlySpan<byte> s, int o) => s[o] | (s[o + 1] << 8);
}
