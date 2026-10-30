using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.SolidWorks;

/// <summary>
/// The container SolidWorks 2015 and later write: a run of ZIP-like entries (version 0x14, flags 6,
/// deflate) whose signature differs per file and whose names have the two nibbles of every byte swapped.
/// Some entries near the start are decoys that claim sizes they do not have; an entry counts only
/// when its data inflates to the stated size and matches its CRC.
/// SolidWorks 2014 and earlier wrote an OLE compound file instead: the same logical streams, each under its own name or, when compressed, with
/// the suffix <c>__ZLB</c> (a 16-byte signature, then chunks of u32 inflated size, u32 deflated size and a zlib stream, ended by two zero
/// words). <see cref="TryOpen"/> opens either and <see cref="Read"/> and <see cref="Contains"/> take the logical name.
/// </summary>
internal sealed class SwContainer
{
    private static readonly byte[] EntryMarker = [0x14, 0x00, 0x06, 0x00, 0x08, 0x00];

    private readonly ReadOnlyMemory<byte> _data;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _lazy;
    private CompoundFile? _compound;
    private CancellationToken _cancellation;

    private static readonly byte[] ZlbSignature = [0x23, 0x1D, 0xD5, 0x71, 0xDA, 0x81, 0x48, 0xA2, 0xA8, 0x58, 0x98, 0xB2, 0x1B, 0x89, 0xEF, 0x99];
    private const string ZlbSuffix = "__ZLB";

    private SwContainer(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    /// <summary>The OLE compound file behind a SolidWorks 2014-or-earlier document, or null for the newer container.</summary>
    public CompoundFile? Compound => _compound;

    private readonly record struct Entry(int DataStart, int CompressedSize, int Size, uint Crc);

    public IReadOnlyCollection<string> Names => _entries.Keys;

    /// <summary>
    /// Opens the container. With <paramref name="verify"/> every entry is inflated and checked against its CRC up front (decoys are exactly the
    /// entries that fail). Without it only the entry headers are read: an entry counts when its name is printable and its data runs to the
    /// end without swallowing the header of any other entry (a decoy overruns the next header; real data never contains one), and the bytes
    /// are inflated and CRC-checked only when <see cref="Read"/> asks for them, so a large stream nobody asks for is never inflated.
    /// </summary>
    public static SwContainer? TryOpen(ReadOnlyMemory<byte> data, CancellationToken cancellation = default, bool verify = true)
    {
        if (CompoundFile.HasSignature(data.Span))
        {
            return OpenCompound(data, cancellation);
        }

        if (!verify)
        {
            return OpenLazy(data, cancellation);
        }

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

    private static SwContainer? OpenLazy(ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        var span = data.Span;
        var candidates = new List<(string Name, int Start, Entry Entry)>();
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

            Cancel.Every(cancellation, candidates.Count, 255);
            var compressed = U32(span, start + 18);
            var size = U32(span, start + 22);
            var nameLength = U16(span, start + 26);
            var dataStart = (long)start + 30 + nameLength + U16(span, start + 28);
            if (nameLength == 0 || nameLength > 512 || dataStart + compressed > span.Length || size > int.MaxValue || compressed > int.MaxValue)
            {
                continue;
            }

            if (DecodeName(span.Slice(start + 30, nameLength)) is { } name)
            {
                candidates.Add((name, start, new Entry((int)dataStart, (int)compressed, (int)size, U32(span, start + 14))));
            }
        }

        var container = new SwContainer(data) { _lazy = true };
        for (var i = 0; i < candidates.Count; i++)
        {
            var (name, _, entry) = candidates[i];
            var end = (long)entry.DataStart + entry.CompressedSize;
            // The next header (candidates are in file order) must not lie inside this entry's data.
            if (i + 1 < candidates.Count && candidates[i + 1].Start < end)
            {
                continue;
            }

            container._entries.TryAdd(name, entry);
        }

        return container._entries.Count >= 3 ? container : null;
    }

    private static SwContainer? OpenCompound(ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        CompoundFile file;
        try
        {
            file = new CompoundFile(data, cancellation);
        }
        catch (ModelFormatException)
        {
            return null;
        }

        // Only a SolidWorks document has these streams; any other compound file (Office, Inventor, Revit) is not ours.
        var isSolidWorks = file.Contains("Header2") || file.Contains("ISolidWorksInformation")
            || file.StreamNames.Any(n => n.StartsWith("Contents/Config-", StringComparison.Ordinal) || n.StartsWith("_MO_VERSION_", StringComparison.Ordinal));
        return isSolidWorks ? new SwContainer(data) { _compound = file, _cancellation = cancellation } : null;
    }

    /// <summary>The model-version marker of a compound document (a storage named <c>_MO_VERSION_7000</c>), or null.</summary>
    public string? CompoundVersion() =>
        _compound?.StreamNames.Select(n => n.Split('/')[0]).FirstOrDefault(n => n.StartsWith("_MO_VERSION_", StringComparison.Ordinal))?["_MO_VERSION_".Length..];

    /// <summary>Where an entry's compressed bytes lie in the file, or null when there is no such entry.</summary>
    public (int Start, int Length)? RangeOf(string name) => _entries.TryGetValue(name, out var e) ? (e.DataStart, e.CompressedSize) : null;

    public bool Contains(string name) =>
        _compound is { } file ? file.Contains(name + ZlbSuffix) || file.Contains(name) : _entries.ContainsKey(name);

    /// <summary>The stream's bytes, or null when there is none of that name.</summary>
    public byte[]? Read(string name)
    {
        if (_compound is { } file)
        {
            return ReadCompound(file, name);
        }

        if (!_entries.TryGetValue(name, out var entry))
        {
            return null;
        }

        var raw = BinaryHelpers.InflateExact(_data.Slice(entry.DataStart, entry.CompressedSize), entry.Size);
        return _lazy && raw is not null && Crc32.Update(0, raw) != entry.Crc ? null : raw;
    }

    private const int MaxStreamBytes = 1 << 30;

    private byte[]? ReadCompound(CompoundFile file, string name)
    {
        if (file.Read(name + ZlbSuffix, _cancellation, 1L << 28) is { } packed)
        {
            return packed.Length == 0 ? [] : Unpack(packed, _cancellation);
        }

        return file.Read(name, _cancellation, 1L << 28);
    }

    /// <summary>Inflates a <c>__ZLB</c> stream; null when it does not hold what its chunk headers promise.</summary>
    private static byte[]? Unpack(byte[] data, CancellationToken cancellation)
    {
        if (data.Length < 24 || !data.AsSpan(0, 16).SequenceEqual(ZlbSignature))
        {
            return null;
        }

        var output = new MemoryStream();
        var position = 16;
        var chunk = 0L;
        while (position + 8 <= data.Length)
        {
            Cancel.Every(cancellation, chunk++, 0);
            var inflated = BitConverter.ToUInt32(data, position);
            var deflated = BitConverter.ToUInt32(data, position + 4);
            position += 8;
            if (inflated == 0 && deflated == 0)
            {
                return output.ToArray();
            }

            // A zlib stream: two header bytes, the deflate data, and an Adler-32 trailer that is left unchecked.
            if (deflated < 6 || deflated > data.Length - position || inflated > MaxChunkBytes || output.Length + inflated > MaxStreamBytes
                || BinaryHelpers.InflateExact(data.AsMemory(position + 2, (int)deflated - 2), (int)inflated) is not { } bytes)
            {
                return null;
            }

            output.Write(bytes);
            position += (int)deflated;
        }

        return output.ToArray();
    }

    private const uint MaxChunkBytes = 1 << 24;

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
