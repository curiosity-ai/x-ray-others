using System.Buffers;
using System.Buffers.Binary;

namespace XRay.ThreeD.Formats.Catia;

using XRay.ThreeD.Internal;

/// <summary>
/// The display tessellation CATIA keeps with a part (see docs/catia-format.md): a table of vertex coordinates (control bits, then the scalars that
/// are not repeats of the previous row) and, per face, an indexed triangle list (independent triangles, strips and fans, with big-endian handles)
/// followed by a face row that carries the face colour. Everything is found by structure, never by position, and every candidate is validated
/// (the scalars are consumed exactly, handles stay inside the table), because the stream around it is a proprietary record format that is not decoded.
/// </summary>
internal static class CatiaTessellation
{
    /// <summary>One decoded vertex table with its faces.</summary>
    internal sealed class Body
    {
        public float[] Positions = [];

        /// <summary>Three vertex indices per triangle, faces back to back.</summary>
        public uint[] Indices = [];

        /// <summary>First index (not triangle) of each face; one extra entry holds the index count.</summary>
        public int[] FaceStart = [];

        /// <summary>One packed 0xRRGGBBAA colour per face, or an empty array when the stream stores none that matches the faces.</summary>
        public uint[] FaceColors = [];

        public int Faces => FaceStart.Length - 1;
    }

    private static ReadOnlySpan<byte> TableMarker => [0xff, 0xff, 0x02, 0x00, 0x01];

    private static readonly byte[] Need = [3, 0, 1, 2];

    public static List<Body> Decode(ReadOnlySpan<byte> data, LoadOptions options)
    {
        var token = options.Cancellation;
        var bodies = new List<Body>();
        var tables = FindTables(data, token);
        long triangles = 0;
        var vertexCap = (int)Math.Min(options.MaxTriangles * 3, (long)(int.MaxValue / 16));
        for (var t = 0; t < tables.Count; t++)
        {
            token.ThrowIfCancellationRequested();
            var table = tables[t];
            if (table.Count < 3 || table.Count > vertexCap || (table.Count + 3) / 4 > data.Length)
            {
                continue;
            }

            var stop = t + 1 < tables.Count ? tables[t + 1].Marker : data.Length;
            float[]? positions = null;
            var end = 0;
            if (table.Mode == 0)
            {
                positions = DecodePositions(data, table.Count, table.Data, token, out end);
            }

            if (positions is null && table.Mode != 0)
            {
                positions = ScanPositions(data, table, Math.Min(stop, table.Data + (1 << 20)), token, out end);
            }

            if (positions is null)
            {
                continue;
            }

            if (BuildBody(data, positions, table.Count, end, stop, options, token, triangles) is { } body)
            {
                triangles += body.Indices.Length / 3;
                bodies.Add(body);
            }
        }

        return bodies;
    }

    internal readonly record struct Table(int Marker, int Count, int Mode, int Data);

    internal static List<Table> FindTables(ReadOnlySpan<byte> data, CancellationToken token)
    {
        var list = new List<Table>();
        var from = 0;
        while (from < data.Length - 16)
        {
            token.ThrowIfCancellationRequested();
            var at = data[from..].IndexOf(TableMarker);
            if (at < 0)
            {
                break;
            }

            var marker = from + at;
            var p = marker + 5;
            if (TryCount(data, ref p, out var count) && TryCount(data, ref p, out _) && p + 4 <= data.Length)
            {
                list.Add(new Table(marker, count, data[p + 3], p + 4));
            }

            from = marker + 1;
        }

        return list;
    }

    /// <summary>A count is one byte, or 0xff and a little-endian u32.</summary>
    private static bool TryCount(ReadOnlySpan<byte> data, ref int p, out int value)
    {
        value = 0;
        if (p >= data.Length)
        {
            return false;
        }

        if (data[p] != 0xff)
        {
            value = data[p++];
            return true;
        }

        if (p + 5 > data.Length)
        {
            return false;
        }

        var v = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 1)..]);
        if (v > int.MaxValue)
        {
            return false;
        }

        value = (int)v;
        p += 5;
        return true;
    }

    /// <summary>
    /// The coordinate table: ceil(n / 4) control bytes (two bits per row, lowest first: 0 reads x, y and z, 1 repeats the row above, 2 reads z, 3 reads y and z),
    /// then a count and that many float32 scalars, consumed exactly.
    /// </summary>
    internal static float[]? DecodePositions(ReadOnlySpan<byte> data, int count, int start, CancellationToken token, out int end)
    {
        end = 0;
        var controlLength = (count + 3) / 4;
        var p = start + controlLength;
        if (start < 0 || p > data.Length || !TryCount(data, ref p, out var scalars) || scalars < 1 || scalars > 3L * count || p + 4L * scalars > data.Length)
        {
            return null;
        }

        var control = data.Slice(start, controlLength);
        long needed = 0;
        for (var i = 0; i < count; i++)
        {
            needed += Need[(control[i >> 2] >> ((i & 3) * 2)) & 3];
        }

        if (needed != scalars)
        {
            return null;
        }

        var positions = new float[count * 3];
        float x = 0, y = 0, z = 0;
        var s = p;
        for (var i = 0; i < count; i++)
        {
            Cancel.Every(token, i);
            switch ((control[i >> 2] >> ((i & 3) * 2)) & 3)
            {
                case 0:
                    x = ReadFloat(data, s);
                    y = ReadFloat(data, s + 4);
                    z = ReadFloat(data, s + 8);
                    s += 12;
                    break;
                case 2:
                    z = ReadFloat(data, s);
                    s += 4;
                    break;
                case 3:
                    y = ReadFloat(data, s);
                    z = ReadFloat(data, s + 4);
                    s += 8;
                    break;
            }

            positions[3 * i] = x;
            positions[3 * i + 1] = y;
            positions[3 * i + 2] = z;
        }

        end = s;
        return positions;
    }

    private static float ReadFloat(ReadOnlySpan<byte> data, int at)
    {
        var f = BinaryPrimitives.ReadSingleLittleEndian(data[at..]);
        return float.IsFinite(f) ? f : 0f;
    }

    /// <summary>
    /// Tables whose header carries flags (mode 2) hold other arrays before the coordinates. The coordinate table is found by its own consistency: control bytes
    /// whose needs add up to the scalar count that follows them.
    /// </summary>
    private static float[]? ScanPositions(ReadOnlySpan<byte> data, Table table, int stop, CancellationToken token, out int end)
    {
        end = 0;
        var count = table.Count;
        var controlLength = (count + 3) / 4;
        long budget = 1L << 27;
        for (var p = table.Data + controlLength; p < stop - 5 && budget > 0; p++)
        {
            Cancel.Every(token, p);
            var q = p;
            if (data[p] == 0)
            {
                continue;
            }

            if (!TryCount(data, ref q, out var scalars) || scalars < 1 || scalars > 3L * count || q + 4L * scalars > data.Length)
            {
                continue;
            }

            var control = data.Slice(p - controlLength, controlLength);
            if ((control[0] & 3) != 0)
            {
                continue;
            }

            budget -= controlLength;
            long needed = 0;
            for (var i = 0; i < count && needed <= scalars; i++)
            {
                needed += Need[(control[i >> 2] >> ((i & 3) * 2)) & 3];
            }

            if (needed == scalars && DecodePositions(data, count, p - controlLength, token, out end) is { } positions)
            {
                return positions;
            }
        }

        return null;
    }

    private static Body? BuildBody(ReadOnlySpan<byte> data, float[] positions, int count, int from, int stop, LoadOptions options, CancellationToken token, long trianglesSoFar)
    {
        // The handle width is the smallest that holds a table index; the others are tried when that finds too little.
        int[] widths = count <= 256 ? [1, 2, 3] : count <= 65536 ? [2, 1, 3] : [3, 2, 1];
        List<uint>? best = null;
        List<int>? bestFaces = null;
        List<int>? bestEnds = null;
        var bestUsed = -1;
        var used = ArrayPool<bool>.Shared.Rent(count);
        try
        {
            foreach (var w in widths)
            {
                var indices = new List<uint>();
                var faces = new List<int>();
                var ends = new List<int>();
                Array.Clear(used, 0, count);
                var distinct = ScanPackets(data, from, stop, count, w, used, indices, faces, ends, options, token, trianglesSoFar);
                if (distinct > bestUsed)
                {
                    best = indices;
                    bestFaces = faces;
                    bestEnds = ends;
                    bestUsed = distinct;
                }

                if (distinct >= count)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(used);
        }

        // A real tessellation uses (nearly) every vertex of its table; anything less is something else that looks like it.
        if (best is null || bestFaces is null || bestEnds is null || best.Count == 0 || bestUsed < 3 || (long)bestUsed * 4 < (long)count * 3)
        {
            return null;
        }

        var body = new Body { Positions = positions, Indices = [.. best] };
        bodyFaces(body, bestFaces);
        body.FaceColors = FaceColors(data, bestEnds, stop);
        Orient(body);
        return body;

        static void bodyFaces(Body b, List<int> faces)
        {
            b.FaceStart = new int[faces.Count + 1];
            faces.CopyTo(b.FaceStart);
            b.FaceStart[faces.Count] = b.Indices.Length;
        }
    }

    /// <summary>Finds every valid face packet between <paramref name="from"/> and <paramref name="stop"/>; returns how many distinct vertices they reference.</summary>
    private static int ScanPackets(ReadOnlySpan<byte> data, int from, int stop, int count, int width, bool[] used, List<uint> indices, List<int> faces, List<int> ends, LoadOptions options, CancellationToken token, long trianglesSoFar)
    {
        var distinct = 0;
        long budget = 32L * (stop - from) + (1 << 20);
        var maxTriangles = options.MaxTriangles - trianglesSoFar;
        var o = from;
        while (o < stop - 4 && budget > 0)
        {
            Cancel.Every(token, o);
            if (data[o] != 1 || (data[o + 1] & 0xf0) != 0x40)
            {
                o++;
                continue;
            }

            var mark = indices.Count;
            if (TryPacket(data, o, stop, count, width, indices, ref budget, out var end))
            {
                if (indices.Count / 3 > maxTriangles)
                {
                    throw new ModelFormatException("The model has more triangles than the configured limit.");
                }

                faces.Add(mark);
                ends.Add(end);
                for (var i = mark; i < indices.Count; i++)
                {
                    var v = (int)indices[i];
                    if (!used[v])
                    {
                        used[v] = true;
                        distinct++;
                    }
                }

                o = end;
            }
            else
            {
                indices.RemoveRange(mark, indices.Count - mark);
                o++;
            }
        }

        return distinct;
    }

    /// <summary>
    /// A face packet: <c>01 kind</c>, the counts of independent triangles (A), strips (B) and fans (C) as the kind's low bits say, <c>ff N</c>, an optional
    /// 12-byte face normal (bit 3), one length per strip and fan, then N handles of <paramref name="width"/> big-endian bytes (A triples, the strips, the fans).
    /// </summary>
    private static bool TryPacket(ReadOnlySpan<byte> data, int o, int stop, int count, int width, List<uint> indices, ref long budget, out int end)
    {
        end = 0;
        var mask = data[o + 1] & 0x0f;
        var p = o + 2;
        long a = 0, b = 0, c = 0;
        if (((mask & 1) != 0 && !TryCountLong(data, ref p, out a)) || ((mask & 2) != 0 && !TryCountLong(data, ref p, out b)) || ((mask & 4) != 0 && !TryCountLong(data, ref p, out c)))
        {
            return false;
        }

        if (p + 5 > data.Length || data[p] != 0xff)
        {
            return false;
        }

        var n = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 1)..]);
        p += 5;
        if ((mask & 8) != 0)
        {
            p += 12;
        }

        var lengths = b + c;
        if (n == 0 || n > stop || lengths > stop - p)
        {
            return false;
        }

        // The lengths are one count each, or (for a two-byte handle) one big-endian u16 each.
        for (var mode = 0; mode < 2; mode++)
        {
            var q = p;
            long sum = 3 * a;
            var ok = true;
            for (long i = 0; i < lengths && ok; i++)
            {
                if (--budget <= 0)
                {
                    return false;
                }

                if (mode == 0)
                {
                    ok = TryCountLong(data, ref q, out var k);
                    sum += k;
                }
                else if (width == 2 && q + 2 <= data.Length)
                {
                    sum += (data[q] << 8) | data[q + 1];
                    q += 2;
                }
                else
                {
                    ok = false;
                }

                if (sum > n)
                {
                    ok = false;
                }
            }

            if (!ok || sum != n || q + (long)n * width > stop)
            {
                continue;
            }

            budget -= n;
            if (EmitTriangles(data, p, q, a, b, c, width, count, indices, mode == 1))
            {
                end = q + (int)n * width;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryCountLong(ReadOnlySpan<byte> data, ref int p, out long value)
    {
        value = 0;
        if (p >= data.Length)
        {
            return false;
        }

        if (data[p] != 0xff)
        {
            value = data[p++];
            return true;
        }

        if (p + 5 > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 1)..]);
        p += 5;
        return true;
    }

    /// <summary>Appends the triangles of a validated packet; false (and nothing kept by the caller) when a handle is outside the table.</summary>
    private static bool EmitTriangles(ReadOnlySpan<byte> data, int lengthsAt, int handlesAt, long a, long b, long c, int width, int count, List<uint> indices, bool u16Lengths)
    {
        var h = handlesAt;
        for (long i = 0; i < a; i++)
        {
            if (!Handle(data, ref h, width, count, out var v0) || !Handle(data, ref h, width, count, out var v1) || !Handle(data, ref h, width, count, out var v2))
            {
                return false;
            }

            indices.Add(v0);
            indices.Add(v1);
            indices.Add(v2);
        }

        var q = lengthsAt;
        for (long i = 0; i < b + c; i++)
        {
            long k;
            if (u16Lengths)
            {
                k = (data[q] << 8) | data[q + 1];
                q += 2;
            }
            else
            {
                TryCountLong(data, ref q, out k);
            }

            if (k < 3)
            {
                // A run shorter than a triangle draws nothing but its handles still have to be in range.
                for (long j = 0; j < k; j++)
                {
                    if (!Handle(data, ref h, width, count, out _))
                    {
                        return false;
                    }
                }

                continue;
            }

            if (!Handle(data, ref h, width, count, out var first) || !Handle(data, ref h, width, count, out var previous) || !Handle(data, ref h, width, count, out var current))
            {
                return false;
            }

            var strip = i < b;
            AddRunTriangle(indices, strip, 0, first, previous, current);
            for (long j = 3; j < k; j++)
            {
                if (!Handle(data, ref h, width, count, out var next))
                {
                    return false;
                }

                if (strip)
                {
                    first = previous;
                    previous = current;
                    current = next;
                    AddRunTriangle(indices, true, (int)((j - 2) & 1), first, previous, current);
                }
                else
                {
                    previous = current;
                    current = next;
                    AddRunTriangle(indices, false, 0, first, previous, current);
                }
            }
        }

        return true;
    }

    private static void AddRunTriangle(List<uint> indices, bool strip, int parity, uint a, uint b, uint c)
    {
        if (strip && parity == 1)
        {
            indices.Add(b);
            indices.Add(a);
            indices.Add(c);
            return;
        }

        indices.Add(a);
        indices.Add(b);
        indices.Add(c);
    }

    private static bool Handle(ReadOnlySpan<byte> data, ref int p, int width, int count, out uint value)
    {
        uint v = 0;
        for (var i = 0; i < width; i++)
        {
            v = (v << 8) | data[p + i];
        }

        p += width;
        value = v;
        return v < (uint)count;
    }

    /// <summary>
    /// The face rows (<c>?? 04 04 ff</c> and the bytes A, B, G, R) either follow the last packet back to back, or follow each packet in turn. A colour per face is
    /// returned only when the rows account for every face.
    /// </summary>
    private static uint[] FaceColors(ReadOnlySpan<byte> data, List<int> ends, int stop)
    {
        var faces = ends.Count;
        var colors = new uint[faces];
        var each = 0;
        for (var i = 0; i < faces; i++)
        {
            if (IsRow(data, ends[i], stop))
            {
                colors[i] = Pack(data, ends[i]);
                each++;
            }
        }

        if (each == faces)
        {
            return colors;
        }

        var at = ends[faces - 1];
        var rows = 0;
        while (rows < faces && IsRow(data, at, stop))
        {
            colors[rows++] = Pack(data, at);
            at += 8;
        }

        return rows == faces ? colors : [];
    }

    private static bool IsRow(ReadOnlySpan<byte> data, int at, int stop) => at + 8 <= stop && data[at + 1] == 0x04 && data[at + 2] == 0x04 && data[at + 3] == 0xff;

    private static uint Pack(ReadOnlySpan<byte> data, int at) => ((uint)data[at + 7] << 24) | ((uint)data[at + 6] << 16) | ((uint)data[at + 5] << 8) | data[at + 4];

    /// <summary>The strip parity of the packets draws a closed solid inside out in some files; flip every triangle when the enclosed volume is negative.</summary>
    private static void Orient(Body body)
    {
        double volume = 0;
        var p = body.Positions;
        var idx = body.Indices;
        for (var i = 0; i + 2 < idx.Length; i += 3)
        {
            int a = (int)idx[i] * 3, b = (int)idx[i + 1] * 3, c = (int)idx[i + 2] * 3;
            volume += p[a] * (p[b + 1] * (double)p[c + 2] - p[b + 2] * (double)p[c + 1])
                - p[a + 1] * (p[b] * (double)p[c + 2] - p[b + 2] * (double)p[c])
                + p[a + 2] * (p[b] * (double)p[c + 1] - p[b + 1] * (double)p[c]);
        }

        if (volume < 0)
        {
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                (idx[i + 1], idx[i + 2]) = (idx[i + 2], idx[i + 1]);
            }
        }
    }
}
