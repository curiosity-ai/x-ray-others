using System.Buffers.Binary;
using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// The sections a DWG database is read from, in the layout every release shares once the file container is undone: each section starts with its
/// sentinel, then its size, then the bit stream (header variables, classes, handle map) or, for the object section, the objects themselves.
/// </summary>
internal sealed class DwgSections
{
    /// <summary>The release as a number: 15 (AC1015), 18 (AC1018), 21, 24, 27, 32.</summary>
    public int Generation { get; init; }

    public byte[] Header { get; init; } = [];

    public byte[] Classes { get; init; } = [];

    /// <summary>The handle map: blocks of big-endian size, then (handle delta, offset delta) pairs in modular characters.</summary>
    public byte[] Handles { get; init; } = [];

    /// <summary>Where object offsets of the handle map point: the whole file for AC1015, the decompressed object section for later releases.</summary>
    public byte[] Objects { get; init; } = [];

    /// <summary>The preview section (AC1018 and later keep it in a page of its own); empty when there is none.</summary>
    public byte[] Preview { get; init; } = [];
}

/// <summary>
/// DWG file containers. AC1015 stores its sections plain at the addresses of a locator table. AC1018 (R2004) and the releases that share its layout
/// (AC1024, AC1027, AC1032) keep them in numbered pages: a 0x80 byte header, an encrypted block with the addresses of the page map, the page map
/// (itself a compressed system page), a section map that names each section and lists its pages, and data pages whose 32 byte headers are masked with
/// their own file address. Page data is LZ77 compressed (<see cref="Decompress"/>).
/// </summary>
internal static class DwgContainer
{
    private const int MaxSectionBytes = 1 << 30;

    public static int Generation(ReadOnlySpan<byte> head) => head.Length < 6 ? 0 : Encoding.ASCII.GetString(head[..6]) switch
    {
        "AC1015" => 15,
        "AC1018" => 18,
        "AC1021" => 21,
        "AC1024" => 24,
        "AC1027" => 27,
        "AC1032" => 32,
        _ => 0,
    };

    /// <summary>Only the preview section of an AC1018 family file (the other sections are not decompressed).</summary>
    public static byte[] OpenPreview(ReadOnlyMemory<byte> data, LoadOptions options) =>
        Generation(data.Span) switch
        {
            18 or 24 or 27 or 32 => OpenR2004(data, Generation(data.Span), options.Cancellation, previewOnly: true).Preview,
            21 => OpenR2007(data, options.Cancellation, previewOnly: true).Preview,
            _ => [],
        };

    public static DwgSections Open(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var generation = Generation(data.Span);
        return generation switch
        {
            15 => OpenR2000(data),
            18 or 24 or 27 or 32 => OpenR2004(data, generation, options.Cancellation),
            21 => OpenR2007(data, options.Cancellation),
            _ => throw new UnsupportedModelFormatException(ModelFormats.Dwg, "This DWG release is not decoded."),
        };
    }

    private static DwgSections OpenR2000(ReadOnlyMemory<byte> data)
    {
        var records = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Span[0x15..]);
        if (records is < 3 or > 32 || 0x19 + records * 9 > data.Length)
        {
            throw new ModelFormatException("The DWG section locator table is damaged.");
        }

        byte[] Section(int number)
        {
            for (var i = 0; i < records; i++)
            {
                if (data.Span[0x19 + i * 9] != number)
                {
                    continue;
                }

                var seeker = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[(0x19 + i * 9 + 1)..]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[(0x19 + i * 9 + 5)..]);
                if (seeker == 0 || seeker + size > data.Length)
                {
                    return [];
                }

                return data.Span.Slice((int)seeker, (int)size).ToArray();
            }

            return [];
        }

        return new DwgSections { Generation = 15, Header = Section(0), Classes = Section(1), Handles = Section(2), Objects = data.ToArray() };
    }

    private sealed record SectionPage(int Number, int Size, long Offset);

    private sealed record SectionDescription(string Name, long Size, int MaxPageSize, int Compression, List<SectionPage> Pages);

    private static DwgSections OpenR2004(ReadOnlyMemory<byte> data, int generation, CancellationToken cancel, bool previewOnly = false)
    {
        var s = data.Span;
        if (s.Length < 0x100)
        {
            throw new ModelFormatException("The DWG file is too short.");
        }

        // The 108 bytes at 0x80 are XORed with a linear congruential stream.
        Span<byte> header = stackalloc byte[0x6C];
        s.Slice(0x80, 0x6C).CopyTo(header);
        uint seed = 1;
        for (var i = 0; i < header.Length; i++)
        {
            seed = unchecked(seed * 0x343FD + 0x269EC3);
            header[i] ^= (byte)(seed >> 16);
        }

        if (!header[..11].SequenceEqual("AcFssFcAJMB"u8))
        {
            throw new ModelFormatException("The DWG file header block is damaged.");
        }

        var pageMapAddress = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[0x54..]) + 0x100;
        var sectionMapId = BinaryPrimitives.ReadInt32LittleEndian(header[0x5C..]);
        var pageMapPage = SystemPage(s, pageMapAddress, 0x41630E3B);

        // Page map: (page number, size) pairs; a negative number is a gap that carries 16 more bytes. Pages follow each other from 0x100.
        var pages = new Dictionary<int, (long Address, int Size)>();
        long address = 0x100;
        for (var p = 0; p + 8 <= pageMapPage.Length && pages.Count < 1_000_000;)
        {
            var number = BinaryPrimitives.ReadInt32LittleEndian(pageMapPage.AsSpan(p));
            var size = BinaryPrimitives.ReadInt32LittleEndian(pageMapPage.AsSpan(p + 4));
            p += 8;
            if (size < 0 || size > MaxSectionBytes)
            {
                throw new ModelFormatException("The DWG page map is damaged.");
            }

            if (number >= 0)
            {
                pages[number] = (address, size);
            }
            else
            {
                p += 16;
            }

            address += size;
        }

        if (!pages.TryGetValue(sectionMapId, out var mapPage))
        {
            throw new ModelFormatException("The DWG section map page is missing.");
        }

        var map = SystemPage(s, mapPage.Address, 0x4163003B);
        var descriptions = ReadSectionMap(map);
        byte[] Section(string name)
        {
            foreach (var description in descriptions)
            {
                if (description.Name == name)
                {
                    return ReadSection(data.Span, description, pages, cancel);
                }
            }

            return [];
        }

        return new DwgSections
        {
            Generation = generation,
            Header = previewOnly ? [] : Section("AcDb:Header"),
            Classes = previewOnly ? [] : Section("AcDb:Classes"),
            Handles = previewOnly ? [] : Section("AcDb:Handles"),
            Objects = previewOnly ? [] : Section("AcDb:AcDbObjects"),
            Preview = Section("AcDb:Preview"),
        };
    }

    /// <summary>
    /// AC1021 (AutoCAD 2007). The header at 0x80 is three interleaved Reed-Solomon (255, 239) blocks around a compressed 0x110 byte table (<see cref="DwgLz21"/>)
    /// that gives the page map and section map; both are Reed-Solomon coded pages, as is each data page. Pages lie from 0x480 on, in the order of the page
    /// map. The parity bytes are skipped, not used to repair data.
    /// </summary>
    private static DwgSections OpenR2007(ReadOnlyMemory<byte> data, CancellationToken cancel, bool previewOnly = false)
    {
        var s = data.Span;
        if (s.Length < 0x480)
        {
            throw new ModelFormatException("The DWG file is too short.");
        }

        var decoded = new byte[3 * 239];
        ReedSolomonDeinterleave(s.Slice(0x80, 0x400), decoded, 3, 239);
        var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(decoded.AsSpan(24));
        var table = new byte[0x110];
        if (compressedLength < 0)
        {
            decoded.AsSpan(32, Math.Min(table.Length, decoded.Length - 32)).CopyTo(table);
        }
        else if (compressedLength > decoded.Length - 32)
        {
            throw new ModelFormatException("The DWG file header block is damaged.");
        }
        else
        {
            DwgLz21.Decompress(decoded.AsSpan(32, compressedLength), table, cancel);
        }

        long Field(int offset) => BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(offset));
        var pagesMapOffset = Field(0x38);
        var pagesMapCompressed = Field(0x50);
        var pagesMapUncompressed = Field(0x58);
        var pagesMapFactor = Field(0x18);
        var sectionsMapId = Field(0xC0);
        var sectionsMapCompressed = Field(0xB0);
        var sectionsMapUncompressed = Field(0xC8);
        var sectionsMapFactor = Field(0xD8);

        var pageMap = Page21(s, pagesMapOffset, pagesMapCompressed, pagesMapUncompressed, pagesMapFactor, 239, cancel);
        var pages = new Dictionary<int, (long Address, int Size)>();
        long address = 0;
        for (var p = 0; p + 16 <= pageMap.Length && pages.Count < 1_000_000; p += 16)
        {
            var size = BinaryPrimitives.ReadInt64LittleEndian(pageMap.AsSpan(p));
            var id = BinaryPrimitives.ReadInt64LittleEndian(pageMap.AsSpan(p + 8));
            if (size < 0 || size > MaxSectionBytes || address > s.Length)
            {
                throw new ModelFormatException("The DWG page map is damaged.");
            }

            pages[(int)Math.Abs(id)] = (address, (int)size);
            address += size;
        }

        if (sectionsMapId is < 0 or > int.MaxValue || !pages.TryGetValue((int)sectionsMapId, out var mapPage))
        {
            throw new ModelFormatException("The DWG section map page is missing.");
        }

        var map = Page21(s, mapPage.Address, sectionsMapCompressed, sectionsMapUncompressed, sectionsMapFactor, 239, cancel);
        var descriptions = ReadSectionMap21(map);
        byte[] Section(string name)
        {
            foreach (var description in descriptions)
            {
                if (description.Name == name)
                {
                    return ReadSection21(data.Span, description, pages, cancel);
                }
            }

            return [];
        }

        return new DwgSections
        {
            Generation = 21,
            Header = previewOnly ? [] : Section("AcDb:Header"),
            Classes = previewOnly ? [] : Section("AcDb:Classes"),
            Handles = previewOnly ? [] : Section("AcDb:Handles"),
            Objects = previewOnly ? [] : Section("AcDb:AcDbObjects"),
            Preview = Section("AcDb:Preview"),
        };
    }

    /// <summary>Undoes the interleaving of a Reed-Solomon coded page: block i takes every factor-th byte starting at i, <paramref name="blockSize"/> data bytes of each 255.</summary>
    private static void ReedSolomonDeinterleave(ReadOnlySpan<byte> encoded, Span<byte> buffer, int factor, int blockSize)
    {
        var index = 0;
        var remaining = buffer.Length;
        for (var block = 0; block < factor && block < encoded.Length; block++)
        {
            var size = Math.Min(remaining, blockSize);
            remaining -= size;
            var from = block;
            for (var i = 0; i < size && from < encoded.Length; i++)
            {
                buffer[index++] = encoded[from];
                from += factor;
            }
        }
    }

    /// <summary>A page of the page map or section map: coded in blocks of 255 bytes, 239 of them data, then compressed.</summary>
    private static byte[] Page21(ReadOnlySpan<byte> s, long offset, long compressedSize, long uncompressedSize, long factor, int blockSize, CancellationToken cancel)
    {
        if (offset < 0 || compressedSize < 0 || compressedSize > 1 << 24 || uncompressedSize < 0 || uncompressedSize > 1 << 26 || factor is < 1 or > 1 << 10)
        {
            throw new ModelFormatException("A DWG system page is damaged.");
        }

        var aligned = (compressedSize + 7) & ~7L;
        var total = aligned * factor;
        if (total > 1 << 26)
        {
            throw new ModelFormatException("A DWG system page is damaged.");
        }

        var blocks = (int)((total + blockSize - 1) / blockSize);
        var length = blocks * 255L;
        var start = 0x480 + offset;
        if (start + length > s.Length)
        {
            throw new ModelFormatException("A DWG system page lies outside the file.");
        }

        var compressed = new byte[total];
        ReedSolomonDeinterleave(s.Slice((int)start, (int)length), compressed, blocks, blockSize);
        var output = new byte[uncompressedSize];
        DwgLz21.Decompress(compressed.AsSpan(0, (int)Math.Min(compressedSize, compressed.Length)), output, cancel);
        return output;
    }

    private sealed record SectionPage21(long Offset, long Size, int PageId, long Uncompressed, long Compressed);

    private sealed record SectionDescription21(string Name, long Size, long Encoding, List<SectionPage21> Pages);

    private static List<SectionDescription21> ReadSectionMap21(byte[] map)
    {
        var result = new List<SectionDescription21>();
        var p = 0;
        while (p + 64 <= map.Length && result.Count < 4096)
        {
            var size = BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p));
            var nameLength = BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p + 0x20));
            var encoding = BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p + 0x30));
            var pageCount = BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p + 0x38));
            p += 64;
            if (nameLength < 0 || nameLength > 1024 || pageCount < 0 || pageCount > 1_000_000 || p + nameLength + pageCount * 56 > map.Length)
            {
                throw new ModelFormatException("The DWG section map is damaged.");
            }

            var name = Encoding.Unicode.GetString(map, p, (int)nameLength).Replace("\0", "", StringComparison.Ordinal);
            p += (int)nameLength;
            var pages = new List<SectionPage21>((int)pageCount);
            for (var j = 0; j < pageCount; j++)
            {
                pages.Add(new SectionPage21(
                    BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p)),
                    BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p + 8)),
                    (int)Math.Abs(BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p + 16))),
                    BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p + 24)),
                    BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p + 32))));
                p += 56;
            }

            if (nameLength > 0)
            {
                result.Add(new SectionDescription21(name, size, encoding, pages));
            }
        }

        return result;
    }

    private static byte[] ReadSection21(ReadOnlySpan<byte> file, SectionDescription21 section, Dictionary<int, (long Address, int Size)> pages, CancellationToken cancel)
    {
        long total = 0;
        foreach (var page in section.Pages)
        {
            if (page.Uncompressed < 0 || page.Compressed < 0 || page.Offset < 0 || page.Offset + page.Uncompressed > MaxSectionBytes)
            {
                throw new ModelFormatException("A DWG section has an impossible size.");
            }

            total = Math.Max(total, page.Offset + page.Uncompressed);
        }

        var result = new byte[total];
        var tick = 0L;
        foreach (var page in section.Pages)
        {
            Cancel.Every(cancel, tick++, 15);
            if (page.Uncompressed == 0)
            {
                continue;
            }

            if (!pages.TryGetValue(page.PageId, out var location) || 0x480 + location.Address + location.Size > file.Length || page.Compressed > MaxSectionBytes)
            {
                throw new ModelFormatException("A DWG data page is missing.");
            }

            var raw = file.Slice((int)(0x480 + location.Address), location.Size);
            ReadOnlySpan<byte> bytes = raw;
            byte[]? decoded = null;
            if (section.Encoding == 4)
            {
                var aligned = (page.Compressed + 7) & ~7L;
                var blocks = (int)((aligned + 250) / 251);
                decoded = new byte[(long)blocks * 251];
                ReedSolomonDeinterleave(raw, decoded, blocks, 251);
                bytes = decoded;
            }

            var destination = result.AsSpan((int)page.Offset, (int)page.Uncompressed);
            if (page.Compressed != page.Uncompressed)
            {
                DwgLz21.Decompress(bytes[..(int)Math.Min(page.Compressed, bytes.Length)], destination, cancel);
            }
            else
            {
                bytes[..Math.Min(bytes.Length, destination.Length)].CopyTo(destination);
            }
        }

        return result;
    }

    /// <summary>A system page: a 20 byte header (type, decompressed size, compressed size, compression, checksum) and the data, compressed when the type says so.</summary>
    private static byte[] SystemPage(ReadOnlySpan<byte> s, long address, uint expectedType)
    {
        if (address < 0 || address + 20 > s.Length)
        {
            throw new ModelFormatException("A DWG system page lies outside the file.");
        }

        var h = s[(int)address..];
        var type = BinaryPrimitives.ReadUInt32LittleEndian(h);
        var decompressed = BinaryPrimitives.ReadInt32LittleEndian(h[4..]);
        var compressed = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
        var method = BinaryPrimitives.ReadInt32LittleEndian(h[12..]);
        if (type != expectedType || decompressed is < 0 or > 1 << 24 || compressed < 0 || address + 20 + compressed > s.Length)
        {
            throw new ModelFormatException("A DWG system page is damaged.");
        }

        var source = h.Slice(20, compressed);
        if (method != 2)
        {
            return source.ToArray();
        }

        var output = new byte[decompressed];
        Decompress(source, output);
        return output;
    }

    private static List<SectionDescription> ReadSectionMap(byte[] map)
    {
        var result = new List<SectionDescription>();
        if (map.Length < 20)
        {
            throw new ModelFormatException("The DWG section map is too short.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(map);
        var p = 20;
        for (var i = 0; i < count && i < 4096; i++)
        {
            if (p + 96 > map.Length)
            {
                throw new ModelFormatException("The DWG section map is truncated.");
            }

            var size = BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p));
            var pageCount = BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(p + 8));
            var maxPage = BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(p + 12));
            var compression = BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(p + 20));
            var nameBytes = map.AsSpan(p + 32, 64);
            var end = nameBytes.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(end < 0 ? nameBytes : nameBytes[..end]);
            p += 96;
            if (pageCount < 0 || pageCount > 1_000_000 || p + (long)pageCount * 16 > map.Length)
            {
                throw new ModelFormatException("The DWG section map lists impossible pages.");
            }

            var pages = new List<SectionPage>(pageCount);
            for (var j = 0; j < pageCount; j++)
            {
                pages.Add(new SectionPage(BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(p)), BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(p + 4)), BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(p + 8))));
                p += 16;
            }

            if (name.Length > 0)
            {
                result.Add(new SectionDescription(name, size, maxPage, compression, pages));
            }
        }

        return result;
    }

    private static byte[] ReadSection(ReadOnlySpan<byte> file, SectionDescription section, Dictionary<int, (long Address, int Size)> pages, CancellationToken cancel)
    {
        if (section.Size < 0 || section.Size > MaxSectionBytes || section.MaxPageSize is <= 0 or > 1 << 26 || section.Size > (long)section.Pages.Count * section.MaxPageSize)
        {
            throw new ModelFormatException("A DWG section has an impossible size.");
        }

        var result = new byte[section.Size];
        byte[]? scratch = null;
        try
        {
            var tick = 0L;
            Span<byte> header = stackalloc byte[32];
            foreach (var page in section.Pages)
            {
                Cancel.Every(cancel, tick++, 15);
                if (!pages.TryGetValue(page.Number, out var location) || location.Address + 32 > file.Length)
                {
                    throw new ModelFormatException("A DWG data page is missing.");
                }

                // The 32 byte page header is masked with 0x4164536B xor the page's own address.
                file.Slice((int)location.Address, 32).CopyTo(header);
                var mask = unchecked(0x4164536Bu ^ (uint)location.Address);
                for (var i = 0; i < 32; i += 4)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(header[i..], BinaryPrimitives.ReadUInt32LittleEndian(header[i..]) ^ mask);
                }

                var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
                if (compressedSize < 0 || location.Address + 32 + compressedSize > file.Length || page.Offset < 0 || page.Offset > section.Size)
                {
                    throw new ModelFormatException("A DWG data page is damaged.");
                }

                var source = file.Slice((int)location.Address + 32, compressedSize);
                var room = (int)Math.Min(section.MaxPageSize, section.Size - page.Offset);
                if (section.Compression == 2)
                {
                    scratch ??= new byte[section.MaxPageSize];
                    var written = Decompress(source, scratch.AsSpan(0, section.MaxPageSize));
                    scratch.AsSpan(0, Math.Min(written, room)).CopyTo(result.AsSpan((int)page.Offset));
                }
                else
                {
                    source[..Math.Min(source.Length, room)].CopyTo(result.AsSpan((int)page.Offset));
                }
            }
        }
        finally
        {
            scratch = null;
        }

        return result;
    }

    /// <summary>
    /// The LZ77 variant of AC1018 and later: a literal run, then opcodes that copy from earlier output (0x40 and up: short offsets; 0x20 to 0x3F and
    /// 0x10 to 0x1F: longer ones; 0x11 ends the stream), each carrying the length of the literal run that follows it. Returns the bytes written, at
    /// most <paramref name="destination"/>.Length.
    /// </summary>
    internal static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination) => new Lz(source, destination).Run();

    private ref struct Lz
    {
        private readonly ReadOnlySpan<byte> _source;
        private readonly Span<byte> _destination;
        private int _s;
        private int _d;

        public Lz(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            _source = source;
            _destination = destination;
            _s = 0;
            _d = 0;
        }

        private int Byte()
        {
            if (_s >= _source.Length)
            {
                throw new ModelFormatException("A compressed DWG page is truncated.");
            }

            return _source[_s++];
        }

        private int Extension()
        {
            var total = 0;
            var b = Byte();
            while (b == 0)
            {
                total += 0xFF;
                b = Byte();
                if (total > 1 << 28)
                {
                    throw new ModelFormatException("A compressed DWG page has an impossible run length.");
                }
            }

            return total + b;
        }

        private void Literal(int count)
        {
            if (count < 0 || _s + count > _source.Length || _d + count > _destination.Length)
            {
                throw new ModelFormatException("A compressed DWG page overruns its buffer.");
            }

            _source.Slice(_s, count).CopyTo(_destination[_d..]);
            _s += count;
            _d += count;
        }

        private int LiteralLength(int opcode) => (opcode & 0x0F) != 0 ? (opcode & 0x0F) + 3 : 0x0F + Extension() + 3;

        public int Run()
        {
            var op = Byte();
            if ((op & 0xF0) == 0)
            {
                Literal(LiteralLength(op));
                op = Byte();
            }

            while (op != 0x11)
            {
                int length, offset, literal;
                if (op >= 0x40)
                {
                    length = ((op & 0xF0) >> 4) - 1;
                    var b2 = Byte();
                    offset = ((b2 << 2) | ((op & 0x0C) >> 2)) + 1;
                    literal = op & 3;
                }
                else if (op >= 0x20)
                {
                    length = op & 0x1F;
                    length = (length == 0 ? 0x1F + Extension() : length) + 2;
                    var b1 = Byte();
                    var b2 = Byte();
                    offset = ((b1 >> 2) | (b2 << 6)) + 1;
                    literal = b1 & 3;
                }
                else if (op >= 0x10)
                {
                    length = op & 7;
                    length = (length == 0 ? 7 + Extension() : length) + 2;
                    var b1 = Byte();
                    var b2 = Byte();
                    offset = ((op & 8) << 11) + ((b1 >> 2) | (b2 << 6)) + 0x4000;
                    literal = b1 & 3;
                }
                else
                {
                    throw new ModelFormatException("A compressed DWG page has an unknown opcode.");
                }

                if (offset > _d || _d + length > _destination.Length)
                {
                    throw new ModelFormatException("A compressed DWG page copies from outside its output.");
                }

                for (var i = 0; i < length; i++)
                {
                    _destination[_d] = _destination[_d - offset];
                    _d++;
                }

                if (literal == 0)
                {
                    op = Byte();
                    if ((op & 0xF0) == 0)
                    {
                        Literal(LiteralLength(op));
                        op = Byte();
                    }
                }
                else
                {
                    Literal(literal);
                    op = Byte();
                }
            }

            return _d;
        }
    }
}
