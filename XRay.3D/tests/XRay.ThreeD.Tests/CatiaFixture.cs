using System.Buffers.Binary;
using System.Text;

namespace XRay.ThreeD.Tests;

/// <summary>Builds CATIA V5 containers for tests: a stream directory, a nested graphical-representation container, and the tessellation records inside it.</summary>
internal static class CatiaFixture
{
    public static byte[] Container(params (string Name, byte[] Data)[] streams) => Wrap(streams, nested: false);

    private static byte[] Wrap((string Name, byte[] Data)[] streams, bool nested)
    {
        var body = new List<byte>();
        var offsets = new List<int>();
        foreach (var (_, data) in streams)
        {
            offsets.Add(64 + body.Count);
            body.AddRange(data);
        }

        var directory = new List<byte>(Encoding.ASCII.GetBytes("CATIA_V5 CB0001\0"));
        for (var i = 0; i < streams.Length; i++)
        {
            var raw = Encoding.Unicode.GetBytes(streams[i].Name);
            directory.AddRange(BitConverter.GetBytes((ushort)(raw.Length + 2)));
            directory.AddRange(raw);
            directory.AddRange(new byte[3]);
            var descriptor = new byte[0x50];
            BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x0c), (uint)streams[i].Data.Length);
            directory.AddRange(descriptor);
            foreach (var word in new uint[] { 1, (uint)offsets[i], (uint)streams[i].Data.Length, (uint)streams[i].Data.Length, 0, 0 })
            {
                directory.AddRange(BigEndian(word));
            }
        }

        directory.AddRange(Encoding.ASCII.GetBytes("CB__END"));
        var file = new List<byte>(Encoding.ASCII.GetBytes("V5_CFV2\0"));
        file.AddRange(BigEndian((uint)(64 + body.Count)));
        file.AddRange(BigEndian((uint)directory.Count));
        file.AddRange(Enumerable.Repeat((byte)0xff, 8));
        file.AddRange(new byte[40]);
        file.AddRange(body);
        file.AddRange(directory);
        return [.. file];
    }

    public static byte[] Nested(params (string Name, byte[] Data)[] streams) => Wrap(streams, nested: true);

    private static byte[] BigEndian(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    public static byte[] Count(long n) => n < 255 ? [(byte)n] : [0xff, .. BitConverter.GetBytes((uint)n)];

    /// <summary>The coordinate table; <paramref name="flags"/> are the four bytes after the counts and <paramref name="filler"/> bytes sit between them and the control bits.</summary>
    public static byte[] Positions(IReadOnlyList<(float X, float Y, float Z)> rows, byte[]? flags = null, int filler = 0)
    {
        var control = new byte[(rows.Count + 3) / 4];
        var scalars = new List<float>();
        (float X, float Y, float Z) previous = default;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int code;
            if (i > 0 && r == previous)
            {
                code = 1;
            }
            else if (i > 0 && r.X == previous.X && r.Y == previous.Y)
            {
                code = 2;
                scalars.Add(r.Z);
            }
            else if (i > 0 && r.X == previous.X)
            {
                code = 3;
                scalars.Add(r.Y);
                scalars.Add(r.Z);
            }
            else
            {
                code = 0;
                scalars.AddRange([r.X, r.Y, r.Z]);
            }

            control[i / 4] |= (byte)(code << ((i % 4) * 2));
            previous = r;
        }

        var bytes = new List<byte>([0xff, 0xff, 0x02, 0x00, 0x01]);
        bytes.AddRange(Count(rows.Count));
        bytes.AddRange(Count(rows.Count));
        bytes.AddRange(flags ?? new byte[4]);
        bytes.AddRange(new byte[filler]);
        bytes.AddRange(control);
        bytes.AddRange(Count(scalars.Count));
        foreach (var s in scalars)
        {
            bytes.AddRange(BitConverter.GetBytes(s));
        }

        return [.. bytes];
    }

    /// <summary>A face packet of independent triangles (kind 0x41).</summary>
    public static byte[] Triangles(int width, params int[] handles)
    {
        var bytes = new List<byte>([0x01, 0x41]);
        bytes.AddRange(Count(handles.Length / 3));
        bytes.Add(0xff);
        bytes.AddRange(BitConverter.GetBytes((uint)handles.Length));
        foreach (var h in handles)
        {
            for (var i = width - 1; i >= 0; i--)
            {
                bytes.Add((byte)(h >> (8 * i)));
            }
        }

        return [.. bytes];
    }

    /// <summary>A face packet of one strip (kind 0x42), or of one fan (kind 0x44).</summary>
    public static byte[] Run(int width, bool fan, params int[] handles)
    {
        var bytes = new List<byte>([0x01, (byte)(fan ? 0x44 : 0x42)]);
        bytes.AddRange(Count(1));
        bytes.Add(0xff);
        bytes.AddRange(BitConverter.GetBytes((uint)handles.Length));
        bytes.AddRange(Count(handles.Length));
        foreach (var h in handles)
        {
            for (var i = width - 1; i >= 0; i--)
            {
                bytes.Add((byte)(h >> (8 * i)));
            }
        }

        return [.. bytes];
    }

    public static byte[] Row(byte r, byte g, byte b, byte a) => [0x30, 0x04, 0x04, 0xff, a, b, g, r];

    /// <summary>A part: the nested container holding the given main data stream, wrapped in an outer container with the property streams a real part has.</summary>
    public static byte[] Part(byte[] mainData, string kindClass = "CATPrtCont")
    {
        var inner = Nested(("MainDataStream", new byte[19]), ("MainDataStream", mainData), ("SurfacicReps", new byte[32]));
        return Container(
            ("Data", [.. new byte[8], .. Encoding.ASCII.GetBytes(kindClass + "\0")]),
            ("CATCGRStream", inner),
            ("CATStorageProperty", [.. new byte[8], .. Encoding.ASCII.GetBytes("CATIA\0CATCGRCont\0")]));
    }
}
