using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace XRay.ThreeD.Formats.Blender;

using XRay.ThreeD.Internal;

/// <summary>
/// The container of a <c>.blend</c> file: the file header, the block list (<c>BHead</c>s), the SDNA catalogue of struct layouts that the file
/// carries about itself, and pointer resolution (blocks are keyed by the address their data had when it was saved). Nothing here knows about meshes
/// or objects: <see cref="Rec"/> reads a struct's fields by name through the SDNA, so one reader serves every Blender version whose field names
/// it knows. Compressed files (gzip before 3.0, Zstandard after) are inflated first.
/// </summary>
internal sealed class BlendFile
{
    public readonly byte[] Data;
    public readonly bool Little;
    public readonly int PtrSize;

    /// <summary>Blender version times 100 as the header states it (293 is 2.93; 402 is 4.2).</summary>
    public readonly int Version;

    /// <summary>Header layout version: 0 before Blender 4.2, 1 from 4.2 on (64-bit block headers).</summary>
    public readonly int FormatVersion;

    public readonly string Compression;
    public Block[] Blocks = [];
    public Sdna Sdna = null!;
    private ulong[] _sortedOld = [];
    private int[] _sortedIndex = [];
    private int[] _owner = [];

    /// <summary>
    /// The block (an ID such as a mesh, not a DATA block) whose data is being read. Since Blender 4.2 the addresses of DATA blocks are only
    /// unique within the ID they follow (two meshes can both have a block at the same address), so pointer resolution prefers the block of
    /// the current scope when an address occurs more than once.
    /// </summary>
    public int CurrentScope = -1;

    public static uint Code(string code)
    {
        uint v = 0;
        for (var i = 0; i < code.Length; i++)
        {
            v |= (uint)code[i] << (8 * i);
        }

        return v;
    }

    public readonly struct Block
    {
        public Block(uint code, int sdna, ulong old, long length, long count, long pos)
        {
            Code = code;
            SdnaIndex = sdna;
            Old = old;
            Length = length;
            Count = count;
            Pos = pos;
        }

        public readonly uint Code;
        public readonly int SdnaIndex;
        public readonly ulong Old;
        public readonly long Length;
        public readonly long Count;
        public readonly long Pos;
    }

    public static bool Sniff(ReadOnlySpan<byte> d) => d.Length >= 12 && d[..7].SequenceEqual("BLENDER"u8);

    /// <summary>True for the first bytes of a gzip or Zstandard stream (the two ways Blender compresses a file).</summary>
    public static bool IsCompressed(ReadOnlySpan<byte> d) => (d.Length > 3 && d[0] == 0x1F && d[1] == 0x8B) || ZstdDecoder.IsZstd(d);

    public static BlendFile Open(ReadOnlyMemory<byte> input, long maxBytes, CancellationToken token)
    {
        var span = input.Span;
        var compression = "none";
        byte[] data;
        if (span.Length > 3 && span[0] == 0x1F && span[1] == 0x8B)
        {
            compression = "gzip";
            data = Gunzip(input, maxBytes, token);
        }
        else if (ZstdDecoder.IsZstd(span))
        {
            compression = "zstd";
            data = ZstdDecoder.Decompress(span, maxBytes, token);
        }
        else
        {
            data = input.ToArray();
        }

        return new BlendFile(data, compression, token);
    }

    private static byte[] Gunzip(ReadOnlyMemory<byte> input, long maxBytes, CancellationToken token)
    {
        try
        {
            using var gzip = new GZipStream(new MemoryStream(input.ToArray(), writable: false), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            long total = 0;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new ModelFormatException("The decompressed .blend data is larger than the allowed " + maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes.");
                }

                output.Write(buffer, 0, read);
                token.ThrowIfCancellationRequested();
            }

            return output.ToArray();
        }
        catch (InvalidDataException e)
        {
            throw new ModelFormatException("Corrupt gzip data in a .blend file.", e);
        }
    }

    private BlendFile(byte[] data, string compression, CancellationToken token)
    {
        Data = data;
        Compression = compression;
        if (!Sniff(data))
        {
            throw new ModelFormatException("Not a Blender file: the BLENDER header is missing.");
        }

        int headerSize;
        if (data[7] >= '0' && data[7] <= '9')
        {
            // 4.2 and later: BLENDER17-01v0402 (header size, '-', header layout version, 'v', four-digit version).
            headerSize = Digits(data, 7, 2);
            if (headerSize < 17 || headerSize > data.Length || data[9] != '-' || data[12] != 'v')
            {
                throw new ModelFormatException("Unrecognised Blender file header.");
            }

            FormatVersion = Digits(data, 10, 2);
            if (FormatVersion != 1)
            {
                throw new UnsupportedModelFormatException(ModelFormats.Blender, "Blender file header layout " + FormatVersion + " is newer than this reader knows.");
            }

            PtrSize = 8;
            Little = true;
            Version = Digits(data, 13, 4);
        }
        else
        {
            headerSize = 12;
            PtrSize = data[7] == '_' ? 4 : data[7] == '-' ? 8 : throw new ModelFormatException("Unrecognised Blender pointer size.");
            Little = data[8] == 'v' ? true : data[8] == 'V' ? false : throw new ModelFormatException("Unrecognised Blender byte order.");
            Version = Digits(data, 9, 3);
        }

        ParseBlocks(headerSize, token);
        if (Sdna is null)
        {
            throw new ModelFormatException("The Blender file has no DNA1 block.");
        }

        var count = 0;
        foreach (var b in Blocks)
        {
            if (b.Old != 0 && b.Length > 0)
            {
                count++;
            }
        }

        var index = new int[count];
        var k = 0;
        _owner = new int[Blocks.Length];
        var owner = -1;
        for (var i = 0; i < Blocks.Length; i++)
        {
            if (Blocks[i].Code != Code("DATA"))
            {
                owner = i;
            }

            _owner[i] = owner;
            if (Blocks[i].Old != 0 && Blocks[i].Length > 0)
            {
                index[k++] = i;
            }
        }

        var blocks = Blocks;
        Array.Sort(index, (a, b) => blocks[a].Old != blocks[b].Old ? blocks[a].Old.CompareTo(blocks[b].Old) : a.CompareTo(b));
        var old = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            old[i] = Blocks[index[i]].Old;
        }

        _sortedOld = old;
        _sortedIndex = index;
    }

    private static int Digits(byte[] d, int at, int n)
    {
        var v = 0;
        for (var i = 0; i < n; i++)
        {
            var c = d[at + i];
            if (c < '0' || c > '9')
            {
                throw new ModelFormatException("Unrecognised Blender version in the file header.");
            }

            v = v * 10 + (c - '0');
        }

        return v;
    }

    public uint U32(long pos) => Little ? BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)pos)) : BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)pos));

    public ulong U64(long pos) => Little ? BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)pos)) : BinaryPrimitives.ReadUInt64BigEndian(Data.AsSpan((int)pos));

    public ushort U16(long pos) => Little ? BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan((int)pos)) : BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan((int)pos));

    public float F32(long pos) => BitConverter.UInt32BitsToSingle(U32(pos));

    public ulong Ptr(long pos) => PtrSize == 8 ? U64(pos) : U32(pos);

    private void ParseBlocks(int start, CancellationToken token)
    {
        var d = Data;
        var headerBytes = FormatVersion == 1 ? 32 : PtrSize == 8 ? 24 : 20;
        var list = new List<Block>();
        long p = start;
        var n = 0L;
        while (p + headerBytes <= d.Length)
        {
            Cancel.Every(token, n++);
            var code = U32(p);
            int sdna;
            ulong old;
            long len, nr;
            if (FormatVersion == 1)
            {
                sdna = (int)U32(p + 4);
                old = U64(p + 8);
                len = (long)U64(p + 16);
                nr = (long)U64(p + 24);
            }
            else
            {
                len = (int)U32(p + 4);
                old = Ptr(p + 8);
                sdna = (int)U32(p + 8 + PtrSize);
                nr = (int)U32(p + 12 + PtrSize);
            }

            p += headerBytes;
            if (len < 0 || len > d.Length - p)
            {
                if (code == Code("ENDB"))
                {
                    break;
                }

                throw new ModelFormatException("A Blender block runs past the end of the file.");
            }

            if (code == Code("ENDB"))
            {
                break;
            }

            var block = new Block(code, sdna, old, len, nr, p);
            list.Add(block);
            if (code == Code("DNA1") && Sdna is null)
            {
                Sdna = Sdna.Parse(this, p, len);
            }

            p += len;
        }

        Blocks = list.ToArray();
    }

    /// <summary>The scope (owning ID block) of the block that contains file position <paramref name="pos"/>, or -1.</summary>
    public int ScopeOf(long pos)
    {
        int lo = 0, hi = Blocks.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (Blocks[mid].Pos + Blocks[mid].Length <= pos)
            {
                lo = mid + 1;
            }
            else if (Blocks[mid].Pos > pos)
            {
                hi = mid - 1;
            }
            else
            {
                return _owner[mid];
            }
        }

        return -1;
    }

    private bool Find(ulong ptr, bool exact, out Block block, out ulong offset)
    {
        block = default;
        offset = 0;
        if (ptr == 0 || _sortedOld.Length == 0)
        {
            return false;
        }

        var i = Array.BinarySearch(_sortedOld, ptr);
        if (i < 0)
        {
            if (exact)
            {
                return false;
            }

            i = ~i - 1;
            if (i < 0)
            {
                return false;
            }
        }

        var old = _sortedOld[i];
        while (i > 0 && _sortedOld[i - 1] == old)
        {
            i--;
        }

        var found = false;
        for (var j = i; j < _sortedOld.Length && _sortedOld[j] == old; j++)
        {
            var b = Blocks[_sortedIndex[j]];
            var off = ptr - old;
            if (off >= (ulong)b.Length)
            {
                continue;
            }

            if (!found)
            {
                block = b;
                offset = off;
                found = true;
            }

            if (_owner[_sortedIndex[j]] == CurrentScope)
            {
                block = b;
                offset = off;
                break;
            }
        }

        return found;
    }

    /// <summary>Finds the block holding <paramref name="ptr"/> and returns the file position of that address and the bytes left in the block.</summary>
    public bool TryResolve(ulong ptr, out long pos, out long remaining)
    {
        pos = 0;
        remaining = 0;
        if (!Find(ptr, false, out var b, out var off))
        {
            return false;
        }

        pos = b.Pos + (long)off;
        remaining = b.Length - (long)off;
        return true;
    }

    /// <summary>The block that starts exactly at <paramref name="ptr"/>, or null.</summary>
    public Block? BlockAt(ulong ptr) => Find(ptr, true, out var b, out _) ? b : null;

    /// <summary>The bytes <paramref name="ptr"/> points at, when the block holds at least <paramref name="bytes"/> of them.</summary>
    public bool TryRaw(ulong ptr, long bytes, out ReadOnlySpan<byte> data)
    {
        data = default;
        if (bytes < 0 || !TryResolve(ptr, out var pos, out var remaining) || remaining < bytes)
        {
            return false;
        }

        data = Data.AsSpan((int)pos, (int)bytes);
        return true;
    }

    /// <summary>A struct record at the address <paramref name="ptr"/>, or an empty one when it does not point into a block that can hold it.</summary>
    public Rec Deref(ulong ptr, string structName)
    {
        if (!Sdna.ByName.TryGetValue(structName, out var s) || !TryResolve(ptr, out var pos, out var remaining) || remaining < s.Size)
        {
            return default;
        }

        return new Rec(this, s, pos);
    }

    public Rec At(string structName, long pos)
    {
        if (!Sdna.ByName.TryGetValue(structName, out var s) || pos < 0 || pos + s.Size > Data.Length)
        {
            return default;
        }

        return new Rec(this, s, pos);
    }

    /// <summary>Walks a Blender linked list (each element's first field is the next pointer), at most as many elements as the file has blocks.</summary>
    public IEnumerable<Rec> Walk(ulong first, string structName)
    {
        var guard = Blocks.Length + 1L;
        var ptr = first;
        while (ptr != 0 && guard-- > 0)
        {
            var rec = Deref(ptr, structName);
            if (rec.IsNull)
            {
                yield break;
            }

            yield return rec;
            ptr = Ptr(rec.Pos);
        }
    }

    public string? StructNameOf(in Block block) =>
        block.SdnaIndex > 0 && block.SdnaIndex < Sdna.Structs.Length ? Sdna.Structs[block.SdnaIndex].Name : null;
}

internal sealed class SdnaField
{
    public string Name = "";
    public string Type = "";
    public int Offset;
    public int Size;
    public bool IsPointer;
    public int Count = 1;
    public bool Signed;
}

internal sealed class SdnaStruct
{
    public string Name = "";
    public int Size;
    public SdnaField[] Fields = [];
    public Dictionary<string, SdnaField> ByName = new(StringComparer.Ordinal);
}

internal sealed class Sdna
{
    public SdnaStruct[] Structs = [];
    public Dictionary<string, SdnaStruct> ByName = new(StringComparer.Ordinal);

    public static Sdna Parse(BlendFile file, long pos, long length)
    {
        var d = file.Data;
        var end = pos + length;
        var p = pos;

        void Expect(string tag)
        {
            if (p + 4 > end || Encoding.ASCII.GetString(d, (int)p, 4) != tag)
            {
                throw new ModelFormatException("Corrupt Blender DNA block (expected " + tag + ").");
            }

            p += 4;
        }

        int Count()
        {
            if (p + 4 > end)
            {
                throw new ModelFormatException("Truncated Blender DNA block.");
            }

            var n = (int)file.U32(p);
            p += 4;
            if (n < 0 || n > length)
            {
                throw new ModelFormatException("Implausible count in the Blender DNA block.");
            }

            return n;
        }

        void Align() => p = pos + ((p - pos + 3) & ~3L);

        string[] Strings(int n)
        {
            var result = new string[n];
            for (var i = 0; i < n; i++)
            {
                var q = p;
                while (q < end && d[q] != 0)
                {
                    q++;
                }

                if (q >= end)
                {
                    throw new ModelFormatException("Unterminated string in the Blender DNA block.");
                }

                result[i] = Encoding.UTF8.GetString(d, (int)p, (int)(q - p));
                p = q + 1;
            }

            return result;
        }

        Expect("SDNA");
        Expect("NAME");
        var names = Strings(Count());
        Align();
        Expect("TYPE");
        var types = Strings(Count());
        Align();
        Expect("TLEN");
        if (p + 2L * types.Length > end)
        {
            throw new ModelFormatException("Truncated Blender DNA block.");
        }

        var lengths = new int[types.Length];
        for (var i = 0; i < lengths.Length; i++)
        {
            lengths[i] = file.U16(p);
            p += 2;
        }

        Align();
        Expect("STRC");
        var count = Count();
        var sdna = new Sdna { Structs = new SdnaStruct[count] };
        for (var i = 0; i < count; i++)
        {
            if (p + 4 > end)
            {
                throw new ModelFormatException("Truncated Blender DNA block.");
            }

            int type = file.U16(p);
            int fieldCount = file.U16(p + 2);
            p += 4;
            if (type >= types.Length || p + 4L * fieldCount > end)
            {
                throw new ModelFormatException("Corrupt struct in the Blender DNA block.");
            }

            var s = new SdnaStruct { Name = types[type], Size = lengths[type], Fields = new SdnaField[fieldCount] };
            var offset = 0;
            for (var f = 0; f < fieldCount; f++)
            {
                int ft = file.U16(p);
                int fn = file.U16(p + 2);
                p += 4;
                if (ft >= types.Length || fn >= names.Length)
                {
                    throw new ModelFormatException("Corrupt field in the Blender DNA block.");
                }

                var decl = names[fn];
                var field = new SdnaField { Type = types[ft], Offset = offset };
                var name = decl;
                if (name.StartsWith("(*", StringComparison.Ordinal))
                {
                    field.IsPointer = true;
                    var close = name.IndexOf(')');
                    name = close > 2 ? name[2..close] : name[2..];
                }
                else
                {
                    var stars = 0;
                    while (stars < name.Length && name[stars] == '*')
                    {
                        stars++;
                    }

                    field.IsPointer = stars > 0;
                    name = name[stars..];
                }

                var count2 = 1L;
                var bracket = name.IndexOf('[');
                if (bracket >= 0)
                {
                    var rest = name[bracket..];
                    name = name[..bracket];
                    foreach (var part in rest.Split(['[', ']'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        count2 *= int.TryParse(part, out var dim) && dim > 0 ? dim : 1;
                        if (count2 > 1 << 24)
                        {
                            throw new ModelFormatException("Implausible array size in the Blender DNA block.");
                        }
                    }
                }

                field.Name = name;
                field.Count = (int)count2;
                var elem = field.IsPointer ? file.PtrSize : lengths[ft];
                field.Size = (int)(elem * count2);
                field.Signed = field.Type is "short" or "int" or "long" or "int64_t" or "int8_t" or "int16_t" or "int32_t";
                offset += field.Size;
                s.Fields[f] = field;
                s.ByName.TryAdd(name, field);
            }

            sdna.Structs[i] = s;
            sdna.ByName.TryAdd(s.Name, s);
        }

        return sdna;
    }
}

/// <summary>A struct at a file position, read by field name through the file's own SDNA.</summary>
internal readonly struct Rec
{
    public readonly BlendFile File;
    public readonly SdnaStruct Struct;
    public readonly long Pos;

    public Rec(BlendFile file, SdnaStruct s, long pos)
    {
        File = file;
        Struct = s;
        Pos = pos;
    }

    public bool IsNull => Struct is null;

    public bool Has(string name) => Struct is not null && Struct.ByName.ContainsKey(name);

    /// <summary>The first of the names this struct has (field names changed between Blender versions), or the first name when none is present.</summary>
    public string Pick(string a, string b, string? c = null) => Has(a) ? a : Has(b) ? b : c is not null && Has(c) ? c : a;

    private bool Field(string name, out SdnaField f)
    {
        f = null!;
        return Struct is not null && Struct.ByName.TryGetValue(name, out f!);
    }

    public long Int(string name, long fallback = 0)
    {
        if (!Field(name, out var f))
        {
            return fallback;
        }

        var at = Pos + f.Offset;
        return f.Size switch
        {
            1 => f.Signed ? (sbyte)File.Data[at] : File.Data[at],
            2 => f.Signed ? (short)File.U16(at) : File.U16(at),
            4 => f.Signed ? (int)File.U32(at) : File.U32(at),
            _ => f.Size >= 8 ? (long)File.U64(at) : fallback,
        };
    }

    public float Float(string name, int index = 0, float fallback = 0)
    {
        if (!Field(name, out var f) || (index + 1) * 4 > f.Size)
        {
            return fallback;
        }

        return File.F32(Pos + f.Offset + index * 4);
    }

    public ulong Ptr(string name) => Field(name, out var f) && f.IsPointer ? File.Ptr(Pos + f.Offset) : 0;

    public string Str(string name)
    {
        if (!Field(name, out var f))
        {
            return "";
        }

        var span = File.Data.AsSpan((int)(Pos + f.Offset), f.Size);
        var z = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(z < 0 ? span : span[..z]);
    }

    public int Count(string name) => Field(name, out var f) ? f.Count : 0;

    /// <summary>An embedded struct field.</summary>
    public Rec Sub(string name) => Field(name, out var f) && !f.IsPointer ? File.At(f.Type, Pos + f.Offset) : default;

    /// <summary>The first pointer of an embedded <c>ListBase</c>.</summary>
    public ulong ListFirst(string name) => Field(name, out var f) && f.Type == "ListBase" ? File.Ptr(Pos + f.Offset) : 0;

    public IEnumerable<Rec> List(string name, string elementStruct) => File.Walk(ListFirst(name), elementStruct);

    public System.Numerics.Matrix4x4 Matrix(string name)
    {
        if (!Field(name, out var f) || f.Size < 64)
        {
            return System.Numerics.Matrix4x4.Identity;
        }

        var o = Pos + f.Offset;
        var file = File;
        float G(int i) => file.F32(o + 4 * i);
        return new System.Numerics.Matrix4x4(G(0), G(1), G(2), G(3), G(4), G(5), G(6), G(7), G(8), G(9), G(10), G(11), G(12), G(13), G(14), G(15));
    }

    /// <summary>The ID name without its two-letter type prefix (<c>OBCube</c> is <c>Cube</c>).</summary>
    public string IdName()
    {
        var id = Sub("id");
        var name = id.IsNull ? Str("name") : id.Str("name");
        return name.Length >= 2 ? name[2..] : name;
    }
}
