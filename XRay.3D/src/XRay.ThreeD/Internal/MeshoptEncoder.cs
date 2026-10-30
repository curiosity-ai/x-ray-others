// The codecs in this file are a C# port of the encoders in meshoptimizer (https://github.com/zeux/meshoptimizer,
// Copyright (c) 2016-2026 Arseny Kapoulkine, MIT License; see THIRD_PARTY_NOTICES.md), producing the bitstreams of the
// glTF extension EXT_meshopt_compression: attribute version 0, triangle and index-sequence version 1, and the
// octahedral normal filter. Only the encoding side is ported; the output is checked against an independent decoder.
using System.Buffers.Binary;

namespace XRay.ThreeD.Internal;

internal static class MeshoptEncoder
{
    private const int ByteGroupSize = 16;
    private const int VertexBlockSizeBytes = 8192;
    private const int VertexBlockMaxSize = 256;
    private static readonly int[] BitsV0 = [0, 2, 4, 8];

    // ---- attributes (mode 0), version 0 -------------------------------------------------------------------------

    /// <summary>Encodes <paramref name="count"/> vertices of <paramref name="stride"/> bytes (a multiple of 4, at most 256).</summary>
    public static byte[] EncodeVertices(ReadOnlySpan<byte> vertices, int count, int stride)
    {
        if (stride <= 0 || stride > 256 || stride % 4 != 0 || vertices.Length < count * stride)
        {
            throw new ArgumentException("Invalid vertex layout.");
        }

        var output = new List<byte>(count * stride / 2 + 64) { 0xA0 };
        var firstVertex = new byte[256];
        if (count > 0)
        {
            vertices[..stride].CopyTo(firstVertex);
        }

        var lastVertex = (byte[])firstVertex.Clone();
        var blockSize = Math.Min((VertexBlockSizeBytes / stride) & ~(ByteGroupSize - 1), VertexBlockMaxSize);
        var offset = 0;
        var buffers = new byte[VertexBlockMaxSize * 4];
        while (offset < count)
        {
            var n = Math.Min(blockSize, count - offset);
            EncodeBlock(output, vertices.Slice(offset * stride, n * stride), n, stride, lastVertex, buffers);
            offset += n;
        }

        // Tail: the baseline vertex, padded to at least 32 bytes.
        var padding = Math.Max(0, 32 - stride);
        for (var i = 0; i < padding; i++)
        {
            output.Add(0);
        }

        for (var i = 0; i < stride; i++)
        {
            output.Add(firstVertex[i]);
        }

        return output.ToArray();
    }

    private static void EncodeBlock(List<byte> output, ReadOnlySpan<byte> vertices, int count, int stride, byte[] lastVertex, byte[] buffers)
    {
        var aligned = (count + ByteGroupSize - 1) & ~(ByteGroupSize - 1);
        Array.Clear(buffers);
        for (var k = 0; k < stride; k++)
        {
            if (k % 4 == 0)
            {
                // Deltas of the next four byte lanes, zigzag coded, lane-major.
                for (var i = 0; i < count; i++)
                {
                    for (var j = 0; j < 4; j++)
                    {
                        var previous = i == 0 ? lastVertex[k + j] : vertices[(i - 1) * stride + k + j];
                        var value = vertices[i * stride + k + j];
                        var delta = (byte)(value - previous);
                        buffers[j * VertexBlockMaxSize + i] = (byte)((delta & 0x80) != 0 ? ~(delta << 1) : delta << 1);
                    }
                }
            }

            EncodeBytes(output, buffers.AsSpan((k % 4) * VertexBlockMaxSize, aligned));
        }

        vertices.Slice((count - 1) * stride, stride).CopyTo(lastVertex);
    }

    private static void EncodeBytes(List<byte> output, ReadOnlySpan<byte> buffer)
    {
        var groups = buffer.Length / ByteGroupSize;
        var headerSize = (groups + 3) / 4;
        var headerStart = output.Count;
        for (var i = 0; i < headerSize; i++)
        {
            output.Add(0);
        }

        var lastBits = -1;
        for (var g = 0; g < groups; g++)
        {
            var group = buffer.Slice(g * ByteGroupSize, ByteGroupSize);
            var bestK = 3;
            var bestSize = MeasureGroup(group, BitsV0[3]);
            for (var k = 0; k < 3; k++)
            {
                var size = MeasureGroup(group, BitsV0[k]);
                if (size < bestSize || (size == bestSize && BitsV0[k] == lastBits && BitsV0[bestK] != 8))
                {
                    bestK = k;
                    bestSize = size;
                }
            }

            output[headerStart + g / 4] |= (byte)(bestK << ((g % 4) * 2));
            EncodeGroup(output, group, BitsV0[bestK]);
            lastBits = BitsV0[bestK];
        }
    }

    private static int MeasureGroup(ReadOnlySpan<byte> group, int bits)
    {
        if (bits == 0)
        {
            foreach (var b in group)
            {
                if (b != 0)
                {
                    return int.MaxValue;
                }
            }

            return 0;
        }

        if (bits == 8)
        {
            return ByteGroupSize;
        }

        var sentinel = (1 << bits) - 1;
        var rest = 0;
        foreach (var b in group)
        {
            if (b >= sentinel)
            {
                rest++;
            }
        }

        return ByteGroupSize / 8 * bits + rest;
    }

    private static void EncodeGroup(List<byte> output, ReadOnlySpan<byte> group, int bits)
    {
        if (bits == 0)
        {
            return;
        }

        if (bits == 8)
        {
            foreach (var b in group)
            {
                output.Add(b);
            }

            return;
        }

        var perByte = 8 >> (bits >> 1);
        var sentinel = (1 << bits) - 1;
        for (var i = 0; i < ByteGroupSize; i += perByte)
        {
            var packed = 0;
            for (var k = 0; k < perByte; k++)
            {
                var encoded = group[i + k] >= sentinel ? sentinel : group[i + k];
                packed = (packed << bits) | encoded;
            }

            output.Add((byte)packed);
        }

        foreach (var b in group)
        {
            if (b >= sentinel)
            {
                output.Add(b);
            }
        }
    }

    // ---- triangles (mode 1) and index sequences (mode 2), version 1 -----------------------------------------------

    private static readonly byte[] CodeAuxTable = [0x00, 0x76, 0x87, 0x56, 0x67, 0x78, 0xA9, 0x86, 0x65, 0x89, 0x68, 0x98, 0x01, 0x69, 0, 0];

    public static byte[] EncodeTriangles(ReadOnlySpan<uint> indices)
    {
        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException("Triangle index count must be a multiple of three.");
        }

        var output = new List<byte>(indices.Length / 3 * 2 + 32);
        var code = new List<byte>(indices.Length / 3);
        var data = new List<byte>();
        var edgeFifo = new (uint, uint)[16];
        var vertexFifo = new uint[16];
        Array.Fill(edgeFifo, (uint.MaxValue, uint.MaxValue));
        Array.Fill(vertexFifo, uint.MaxValue);
        var edgeOffset = 0;
        var vertexOffset = 0;
        uint next = 0;
        uint last = 0;
        const int fecMax = 13;
        ReadOnlySpan<int> rotations = [0, 1, 2, 0, 1];

        for (var i = 0; i < indices.Length; i += 3)
        {
            var fer = GetEdgeFifo(edgeFifo, indices[i], indices[i + 1], indices[i + 2], edgeOffset);
            if (fer >= 0 && (fer >> 2) < 15)
            {
                var order = rotations[(fer & 3)..];
                uint a = indices[i + order[0]], b = indices[i + order[1]], c = indices[i + order[2]];
                var fe = fer >> 2;
                var fc = GetVertexFifo(vertexFifo, c, vertexOffset);
                int fec;
                if (fc >= 1 && fc < fecMax)
                {
                    fec = fc;
                }
                else if (c == next)
                {
                    next++;
                    fec = 0;
                }
                else
                {
                    fec = 15;
                }

                if (fec == 15)
                {
                    if (c + 1 == last)
                    {
                        fec = 13;
                        last = c;
                    }

                    if (c == last + 1)
                    {
                        fec = 14;
                        last = c;
                    }
                }

                code.Add((byte)((fe << 4) | fec));
                if (fec == 15)
                {
                    EncodeIndex(data, c, last);
                    last = c;
                }

                if (fec == 0 || fec >= fecMax)
                {
                    PushVertex(vertexFifo, c, ref vertexOffset);
                }

                PushEdge(edgeFifo, c, b, ref edgeOffset);
                PushEdge(edgeFifo, a, c, ref edgeOffset);
            }
            else
            {
                var i0 = indices[i];
                var i1 = indices[i + 1];
                var i2 = indices[i + 2];
                var rotation = i1 == next ? 1 : (i2 == next ? 2 : 0);
                var order = rotations[rotation..];
                uint a = indices[i + order[0]], b = indices[i + order[1]], c = indices[i + order[2]];
                _ = i0;
                var reset = false;
                if (a == 0 && b == 1 && c == 2 && next > 0)
                {
                    reset = true;
                    next = 0;
                    Array.Fill(vertexFifo, uint.MaxValue);
                }

                var fb = GetVertexFifo(vertexFifo, b, vertexOffset);
                var fc = GetVertexFifo(vertexFifo, c, vertexOffset);
                int fea;
                if (a == next)
                {
                    next++;
                    fea = 0;
                }
                else
                {
                    fea = 15;
                }

                int feb;
                if (fb >= 0 && fb < 14)
                {
                    feb = fb + 1;
                }
                else if (b == next)
                {
                    next++;
                    feb = 0;
                }
                else
                {
                    feb = 15;
                }

                int fec2;
                if (fc >= 0 && fc < 14)
                {
                    fec2 = fc + 1;
                }
                else if (c == next)
                {
                    next++;
                    fec2 = 0;
                }
                else
                {
                    fec2 = 15;
                }

                var codeaux = (byte)((feb << 4) | fec2);
                var auxIndex = Array.IndexOf(CodeAuxTable, codeaux);
                if (auxIndex > 13)
                {
                    auxIndex = -1;
                }

                if (fea == 0 && auxIndex >= 0 && auxIndex < 14 && !reset)
                {
                    code.Add((byte)((15 << 4) | auxIndex));
                }
                else
                {
                    code.Add((byte)((15 << 4) | 14 | fea));
                    data.Add(codeaux);
                }

                if (fea == 15)
                {
                    EncodeIndex(data, a, last);
                    last = a;
                }

                if (feb == 15)
                {
                    EncodeIndex(data, b, last);
                    last = b;
                }

                if (fec2 == 15)
                {
                    EncodeIndex(data, c, last);
                    last = c;
                }

                if (fea == 0 || fea == 15)
                {
                    PushVertex(vertexFifo, a, ref vertexOffset);
                }

                if (feb == 0 || feb == 15)
                {
                    PushVertex(vertexFifo, b, ref vertexOffset);
                }

                if (fec2 == 0 || fec2 == 15)
                {
                    PushVertex(vertexFifo, c, ref vertexOffset);
                }

                PushEdge(edgeFifo, b, a, ref edgeOffset);
                PushEdge(edgeFifo, c, b, ref edgeOffset);
                PushEdge(edgeFifo, a, c, ref edgeOffset);
            }
        }

        output.Add(0xE1);
        output.AddRange(code);
        output.AddRange(data);
        output.AddRange(CodeAuxTable);
        return output.ToArray();
    }

    public static byte[] EncodeIndexSequence(ReadOnlySpan<uint> indices)
    {
        var output = new List<byte>(indices.Length + 8) { 0xD1 };
        Span<uint> last = stackalloc uint[2];
        var current = 0;
        foreach (var index in indices)
        {
            var cd = (int)(index - last[current]);
            current ^= Math.Abs(cd) >= 30 ? 1 : 0;
            var d = index - last[current];
            var v = (d << 1) ^ (uint)((int)d >> 31);
            WriteVByte(output, (v << 1) | (uint)current);
            last[current] = index;
        }

        output.AddRange([0, 0, 0, 0]);
        return output.ToArray();
    }

    private static int GetEdgeFifo((uint, uint)[] fifo, uint a, uint b, uint c, int offset)
    {
        for (var i = 0; i < 16; i++)
        {
            var (e0, e1) = fifo[(offset - 1 - i) & 15];
            if (e0 == a && e1 == b)
            {
                return (i << 2) | 0;
            }

            if (e0 == b && e1 == c)
            {
                return (i << 2) | 1;
            }

            if (e0 == c && e1 == a)
            {
                return (i << 2) | 2;
            }
        }

        return -1;
    }

    private static void PushEdge((uint, uint)[] fifo, uint a, uint b, ref int offset)
    {
        fifo[offset] = (a, b);
        offset = (offset + 1) & 15;
    }

    private static int GetVertexFifo(uint[] fifo, uint v, int offset)
    {
        for (var i = 0; i < 16; i++)
        {
            if (fifo[(offset - 1 - i) & 15] == v)
            {
                return i;
            }
        }

        return -1;
    }

    private static void PushVertex(uint[] fifo, uint v, ref int offset)
    {
        fifo[offset] = v;
        offset = (offset + 1) & 15;
    }

    private static void EncodeIndex(List<byte> data, uint index, uint last)
    {
        var d = index - last;
        WriteVByte(data, (d << 1) ^ (uint)((int)d >> 31));
    }

    private static void WriteVByte(List<byte> data, uint v)
    {
        do
        {
            data.Add((byte)((v & 127u) | (v > 127u ? 128u : 0u)));
            v >>= 7;
        }
        while (v != 0);
    }

    // ---- octahedral filter -------------------------------------------------------------------------------------

    /// <summary>
    /// Encodes unit vectors as four signed 8-bit components (octahedral x, y, then 127 for 1.0, then 0) for the OCTAHEDRAL filter at stride 4.
    /// </summary>
    public static byte[] EncodeOctahedral8(ReadOnlySpan<float> normals)
    {
        var count = normals.Length / 3;
        var result = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            float x = normals[3 * i], y = normals[3 * i + 1], z = normals[3 * i + 2];
            var l1 = MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z);
            var scale = l1 == 0 ? 0 : 1 / l1;
            x *= scale;
            y *= scale;
            z *= scale;
            var u = z >= 0 ? x : (1 - MathF.Abs(y)) * (x >= 0 ? 1 : -1);
            var v = z >= 0 ? y : (1 - MathF.Abs(x)) * (y >= 0 ? 1 : -1);
            result[4 * i] = (byte)(sbyte)Math.Clamp((int)MathF.Round(u * 127), -127, 127);
            result[4 * i + 1] = (byte)(sbyte)Math.Clamp((int)MathF.Round(v * 127), -127, 127);
            result[4 * i + 2] = 127;
            result[4 * i + 3] = 0;
        }

        return result;
    }

    internal static void WriteUInt16(Span<byte> destination, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
}
