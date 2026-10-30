using System.Buffers.Binary;

namespace XRay.ThreeD.Formats.Game;

using XRay.ThreeD.Internal;

/// <summary>
/// The editor's raw mesh of a static mesh asset: a bulk-data block holding a version, then counted arrays (face material indices, smoothing masks,
/// vertex positions, wedge indices, three tangent arrays, eight texture-coordinate sets, wedge colours, material map). The block is found by
/// scanning the export's bytes for a bulk-data header (flags, element count, size on disk, 64-bit offset; the offset counts from the summary's
/// bulk-data start when the payload is at the end of the file, or the payload follows inline) whose payload parses completely as such arrays; a
/// candidate that does not is not a raw mesh. Compressed payloads are not read.
/// </summary>
internal static class RawMeshes
{
    internal sealed class Raw
    {
        public int[] FaceMaterial = [];
        public float[] Positions = [];
        public uint[] Wedges = [];
        public float[] Normals = [];
        public float[] Uvs = [];
    }

    public static Raw? TryRead(ReadOnlySpan<byte> d, UassetReader.Package pk, UassetReader.Export e, LoadOptions options)
    {
        if (e.Offset <= 0 || e.Size < 20 || e.Offset + e.Size > d.Length)
        {
            return null;
        }

        var body = d.Slice((int)e.Offset, (int)e.Size);
        for (var i = 0; i + 20 <= body.Length; i++)
        {
            Cancel.Every(options.Cancellation, i);
            var count = BinaryPrimitives.ReadInt32LittleEndian(body[(i + 4)..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(body[(i + 8)..]);
            if (count != size || size < 40)
            {
                continue;
            }

            var flags = BinaryPrimitives.ReadUInt32LittleEndian(body[i..]);
            var offset = BinaryPrimitives.ReadInt64LittleEndian(body[(i + 12)..]);
            long start;
            if ((flags & 1) != 0)
            {
                start = pk.BulkDataStart + offset;
            }
            else if ((flags & 0x20) != 0 || offset == 0)
            {
                start = e.Offset + i + 20;
            }
            else
            {
                continue;
            }

            if (start < 0 || start + size > d.Length)
            {
                continue;
            }

            if (Parse(d.Slice((int)start, size), options) is { } raw)
            {
                return raw;
            }
        }

        return null;
    }

    private static bool ReadArray(ReadOnlySpan<byte> b, ref int p, int elementSize, out int count, out int at)
    {
        count = 0;
        at = p;
        if (p + 4 > b.Length)
        {
            return false;
        }

        count = BinaryPrimitives.ReadInt32LittleEndian(b[p..]);
        p += 4;
        at = p;
        if (count < 0 || (long)count * elementSize > b.Length - p)
        {
            return false;
        }

        p += count * elementSize;
        return true;
    }

    private static Raw? Parse(ReadOnlySpan<byte> b, LoadOptions options)
    {
        // Version and licensee version, then the arrays.
        if (b.Length < 8)
        {
            return null;
        }

        var p = 8;
        if (BinaryPrimitives.ReadInt32LittleEndian(b) is < 0 or > 16 || BinaryPrimitives.ReadInt32LittleEndian(b[4..]) is < 0 or > 1 << 20)
        {
            return null;
        }

        if (!ReadArray(b, ref p, 4, out var faces, out var matAt) || !ReadArray(b, ref p, 4, out var smooth, out _) || smooth != faces
            || !ReadArray(b, ref p, 12, out var verts, out var posAt) || !ReadArray(b, ref p, 4, out var wedges, out var wedgeAt) || wedges != faces * 3 || verts == 0 || faces == 0
            || !ReadArray(b, ref p, 12, out var tx, out _) || !ReadArray(b, ref p, 12, out var ty, out _) || !ReadArray(b, ref p, 12, out var tz, out var tzAt))
        {
            return null;
        }

        var uvAt = 0;
        var uvCount = 0;
        for (var set = 0; set < 8; set++)
        {
            if (!ReadArray(b, ref p, 8, out var n, out var at))
            {
                return null;
            }

            if (set == 0)
            {
                uvCount = n;
                uvAt = at;
            }
        }

        if (!ReadArray(b, ref p, 4, out _, out _) || !ReadArray(b, ref p, 4, out _, out _) || p != b.Length)
        {
            return null;
        }

        var raw = new Raw
        {
            FaceMaterial = new int[faces],
            Positions = new float[verts * 3],
            Wedges = new uint[wedges],
        };
        for (var i = 0; i < faces; i++)
        {
            raw.FaceMaterial[i] = BinaryPrimitives.ReadInt32LittleEndian(b[(matAt + i * 4)..]);
        }

        for (var i = 0; i < verts * 3; i++)
        {
            Cancel.Every(options.Cancellation, i);
            var v = BinaryPrimitives.ReadSingleLittleEndian(b[(posAt + i * 4)..]);
            if (!float.IsFinite(v))
            {
                return null;
            }

            raw.Positions[i] = v;
        }

        for (var i = 0; i < wedges; i++)
        {
            var w = BinaryPrimitives.ReadUInt32LittleEndian(b[(wedgeAt + i * 4)..]);
            if (w >= verts)
            {
                return null;
            }

            raw.Wedges[i] = w;
        }

        if (tz == wedges)
        {
            raw.Normals = new float[wedges * 3];
            for (var i = 0; i < wedges * 3; i++)
            {
                var v = BinaryPrimitives.ReadSingleLittleEndian(b[(tzAt + i * 4)..]);
                raw.Normals[i] = float.IsFinite(v) ? v : 0;
            }
        }

        if (uvCount == wedges)
        {
            raw.Uvs = new float[wedges * 2];
            for (var i = 0; i < wedges * 2; i++)
            {
                var v = BinaryPrimitives.ReadSingleLittleEndian(b[(uvAt + i * 4)..]);
                raw.Uvs[i] = float.IsFinite(v) ? v : 0;
            }
        }

        _ = tx;
        _ = ty;
        return raw;
    }
}
