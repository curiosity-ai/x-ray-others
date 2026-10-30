using System.Buffers.Binary;
using System.Text;

namespace XRay.ThreeD.Internal;

/// <summary>
/// A read-only OLE compound file (structured storage, [MS-CFB]): header, FAT and DIFAT chains, mini stream, and the directory tree as
/// slash-separated stream paths. Every chain walk is bounded by the sector count, so a damaged or hostile file ends in a
/// <see cref="ModelFormatException"/> instead of a loop.
/// </summary>
internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const int MaxEntries = 100_000;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private uint[] _miniFat = [];
    private byte[] _miniStream = [];
    private readonly Dictionary<string, Entry> _streams = new(StringComparer.Ordinal);

    private readonly record struct Entry(long Start, long Size);

    public static bool HasSignature(ReadOnlySpan<byte> data) => data.Length >= 512 && data.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });

    /// <summary>Paths of every stream, such as <c>Formats/Latest</c> or <c>\u0005SummaryInformation</c>.</summary>
    public IEnumerable<string> StreamNames => _streams.Keys;

    public CompoundFile(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        _data = data;
        var h = data.Span;
        if (!HasSignature(h))
        {
            throw new ModelFormatException("Not an OLE compound file.");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(h[0x1A..]);
        var shift = BinaryPrimitives.ReadUInt16LittleEndian(h[0x1E..]);
        var miniShift = BinaryPrimitives.ReadUInt16LittleEndian(h[0x20..]);
        if (!(major == 3 && shift == 9) && !(major == 4 && shift == 12) || miniShift != 6)
        {
            throw new ModelFormatException("The compound file's sector sizes are invalid.");
        }

        _sectorSize = 1 << shift;
        _miniSectorSize = 1 << miniShift;
        _miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(h[0x38..]);
        var sectorCount = (data.Length - _sectorSize) / _sectorSize + 1;
        var fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(h[0x2C..]);
        var dirFirst = BinaryPrimitives.ReadUInt32LittleEndian(h[0x30..]);
        var miniFatFirst = BinaryPrimitives.ReadUInt32LittleEndian(h[0x3C..]);
        var miniFatCount = BinaryPrimitives.ReadUInt32LittleEndian(h[0x40..]);
        var difatFirst = BinaryPrimitives.ReadUInt32LittleEndian(h[0x44..]);
        var difatCount = BinaryPrimitives.ReadUInt32LittleEndian(h[0x48..]);
        if (fatSectors > sectorCount || difatCount > sectorCount || miniFatCount > sectorCount)
        {
            throw new ModelFormatException("The compound file's allocation tables are larger than the file.");
        }

        // FAT sector numbers: 109 in the header, then DIFAT sectors each holding (sector size / 4 - 1) more and a link.
        var fatIds = new List<uint>((int)fatSectors);
        for (var i = 0; i < 109 && fatIds.Count < fatSectors; i++)
        {
            fatIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(h[(0x4C + 4 * i)..]));
        }

        var difat = difatFirst;
        for (uint n = 0; n < difatCount && fatIds.Count < fatSectors && difat < sectorCount; n++)
        {
            token.ThrowIfCancellationRequested();
            var sector = Sector(difat);
            for (var i = 0; i < _sectorSize / 4 - 1 && fatIds.Count < fatSectors; i++)
            {
                fatIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector[(4 * i)..]));
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector[(_sectorSize - 4)..]);
        }

        _fat = new uint[fatIds.Count * (_sectorSize / 4)];
        for (var i = 0; i < fatIds.Count; i++)
        {
            if (fatIds[i] >= sectorCount)
            {
                throw new ModelFormatException("A compound file FAT sector is out of range.");
            }

            var sector = Sector(fatIds[i]);
            for (var k = 0; k < _sectorSize / 4; k++)
            {
                _fat[i * (_sectorSize / 4) + k] = BinaryPrimitives.ReadUInt32LittleEndian(sector[(4 * k)..]);
            }
        }

        var directory = ReadChain(dirFirst, long.MaxValue, token);
        if (miniFatFirst < sectorCount && miniFatCount > 0)
        {
            var bytes = ReadChain(miniFatFirst, miniFatCount * (long)_sectorSize, token);
            _miniFat = new uint[bytes.Length / 4];
            for (var i = 0; i < _miniFat.Length; i++)
            {
                _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4 * i));
            }
        }

        ReadDirectory(directory, token);
    }

    private ReadOnlySpan<byte> Sector(uint id)
    {
        var offset = ((long)id + 1) * _sectorSize;
        if (offset + _sectorSize > _data.Length)
        {
            // The last sector of a file may be short; pad nothing, refuse.
            throw new ModelFormatException("A compound file sector lies beyond the end of the file.");
        }

        return _data.Span.Slice((int)offset, _sectorSize);
    }

    /// <summary>Follows a FAT chain, returning at most <paramref name="limit"/> bytes.</summary>
    private byte[] ReadChain(uint first, long limit, CancellationToken token)
    {
        var chain = new MemoryStream();
        var id = first;
        var steps = 0;
        while (id < 0xFFFFFFFA && chain.Length < limit)
        {
            if (id >= _fat.Length || ++steps > _fat.Length + 1)
            {
                throw new ModelFormatException("A compound file chain is damaged.");
            }

            if ((steps & 1023) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var sector = Sector(id);
            var take = (int)Math.Min(sector.Length, limit - chain.Length);
            chain.Write(sector[..take]);
            id = _fat[id];
        }

        return chain.ToArray();
    }

    private byte[] ReadMiniChain(uint first, long size, CancellationToken token)
    {
        var result = new byte[size];
        var written = 0;
        var id = first;
        var steps = 0;
        while (written < size && id < 0xFFFFFFFA)
        {
            if (id >= _miniFat.Length || ++steps > _miniFat.Length + 1)
            {
                throw new ModelFormatException("A compound file mini stream chain is damaged.");
            }

            if ((steps & 1023) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var offset = (long)id * _miniSectorSize;
            if (offset + _miniSectorSize > _miniStream.Length)
            {
                throw new ModelFormatException("A compound file mini sector lies beyond the mini stream.");
            }

            var take = (int)Math.Min(_miniSectorSize, size - written);
            _miniStream.AsSpan((int)offset, take).CopyTo(result.AsSpan(written));
            written += take;
            id = _miniFat[id];
        }

        return written == size ? result : throw new ModelFormatException("A compound file mini stream is shorter than its directory entry says.");
    }

    private void ReadDirectory(byte[] directory, CancellationToken token)
    {
        var count = directory.Length / 128;
        if (count == 0 || count > MaxEntries)
        {
            throw new ModelFormatException("The compound file directory is empty or implausibly large.");
        }

        string NameOf(int index)
        {
            var o = index * 128;
            var length = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(o + 64)), 0, 64);
            return Encoding.Unicode.GetString(directory, o, Math.Max(0, length - 2));
        }

        byte TypeOf(int index) => directory[index * 128 + 66];
        uint Left(int i) => BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(i * 128 + 68));
        uint Right(int i) => BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(i * 128 + 72));
        uint Child(int i) => BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(i * 128 + 76));
        long Start(int i) => BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(i * 128 + 116));
        long Size(int i) => _sectorSize == 512 ? BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(i * 128 + 120)) : (long)BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(i * 128 + 120));

        if (TypeOf(0) != 5)
        {
            throw new ModelFormatException("The compound file has no root entry.");
        }

        var rootSize = Size(0);
        if (rootSize > 0 && rootSize <= _data.Length)
        {
            _miniStream = ReadChain((uint)Start(0), rootSize, token);
        }

        // Walk the tree of every storage iteratively: (entry, path prefix).
        var visited = new HashSet<int>();
        var work = new Stack<(int Index, string Prefix)>();
        if (Child(0) < count)
        {
            work.Push(((int)Child(0), ""));
        }

        var tick = 0;
        while (work.Count > 0)
        {
            var (index, prefix) = work.Pop();
            Cancel.Every(token, tick++);
            if (!visited.Add(index))
            {
                throw new ModelFormatException("The compound file directory contains a cycle.");
            }

            var name = NameOf(index);
            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            if (Left(index) < count)
            {
                work.Push(((int)Left(index), prefix));
            }

            if (Right(index) < count)
            {
                work.Push(((int)Right(index), prefix));
            }

            switch (TypeOf(index))
            {
                case 2:
                    _streams[path] = new Entry(Start(index), Size(index));
                    break;
                case 1 when Child(index) < count:
                    work.Push(((int)Child(index), path));
                    break;
            }
        }
    }

    public bool Contains(string path) => _streams.ContainsKey(path);

    /// <summary>The stream's bytes, or null when there is no such stream. Streams over <paramref name="maxBytes"/> are not read.</summary>
    public byte[]? Read(string path, CancellationToken token, long maxBytes = 1L << 28)
    {
        if (!_streams.TryGetValue(path, out var entry) || entry.Size < 0 || entry.Size > maxBytes)
        {
            return null;
        }

        if (entry.Size == 0)
        {
            return [];
        }

        if (entry.Size < _miniCutoff)
        {
            return ReadMiniChain((uint)entry.Start, entry.Size, token);
        }

        var bytes = ReadChain((uint)entry.Start, entry.Size, token);
        return bytes.Length == entry.Size ? bytes : throw new ModelFormatException("A compound file stream is shorter than its directory entry says.");
    }
}
