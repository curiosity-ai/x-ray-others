// A C# port of the decoders of meshoptimizer (https://github.com/zeux/meshoptimizer, Copyright (c) 2016-2026 Arseny Kapoulkine,
// MIT License; see THIRD_PARTY_NOTICES.md) for the glTF extension EXT_meshopt_compression: attribute versions 0 and 1,
// triangles, index sequences and the octahedral, quaternion and exponential filters. Every read is bounds-checked; damaged
// streams raise ModelFormatException.
using System.Buffers;
using System.Buffers.Binary;

namespace XRay.ThreeD.Internal;

internal static class MeshoptDecoder
{
    private const int ByteGroupSize = 16;
    private static readonly int[] BitsV0 = [0, 2, 4, 8];
    private static readonly int[] BitsV1 = [0, 1, 2, 4, 8];
    private static readonly int[][] BitsV1Variants = [BitsV1[0..4], BitsV1[1..5]];

    public static byte[] Decode(string mode, string? filter, ReadOnlySpan<byte> source, int count, int stride, CancellationToken cancellation = default)
    {
        if (count < 0 || stride <= 0 || (long)count * stride > int.MaxValue)
        {
            throw new ModelFormatException("A meshopt buffer view has an impossible size.");
        }

        byte[] result;
        switch (mode)
        {
            case "ATTRIBUTES":
                result = DecodeVertices(source, count, stride, cancellation);
                ApplyFilter(result, count, stride, filter ?? "NONE", cancellation);
                return result;
            case "TRIANGLES":
                return PackIndices(DecodeTriangles(source, count, cancellation), stride, cancellation);
            case "INDICES":
                return PackIndices(DecodeIndexSequence(source, count, cancellation), stride, cancellation);
            default:
                throw new ModelFormatException($"Unknown meshopt mode '{mode}'.");
        }
    }

    private static byte[] PackIndices(uint[] indices, int stride, CancellationToken cancellation)
    {
        if (stride is not (2 or 4))
        {
            throw new ModelFormatException("Meshopt index data must have a stride of 2 or 4.");
        }

        var bytes = new byte[indices.Length * stride];
        for (var i = 0; i < indices.Length; i++)
        {
            Cancel.Every(cancellation, i);
            if (stride == 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 * i), indices[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), (ushort)indices[i]);
            }
        }

        return bytes;
    }

    // ---- attributes ------------------------------------------------------------------------------------------------

    public static byte[] DecodeVertices(ReadOnlySpan<byte> source, int count, int stride, CancellationToken cancellation = default)
    {
        if (stride % 4 != 0 || stride > 256)
        {
            throw new ModelFormatException("Meshopt attribute stride must be a multiple of 4 and at most 256.");
        }

        if (source.Length < 1 || (source[0] & 0xF0) != 0xA0)
        {
            throw new ModelFormatException("Not a meshopt attribute stream.");
        }

        var version = source[0] & 0x0F;
        if (version > 1)
        {
            throw new ModelFormatException("Unsupported meshopt attribute version.");
        }

        var tailSize = stride + (version == 0 ? 0 : stride / 4);
        var tailPad = Math.Max(tailSize, version == 0 ? 32 : 24);
        if (source.Length - 1 < tailPad)
        {
            throw new ModelFormatException("A meshopt attribute stream is too short.");
        }

        var dataEnd = source.Length;
        var tail = source[(dataEnd - tailSize)..];
        Span<byte> lastVertex = stackalloc byte[256];
        lastVertex.Clear();
        tail[..stride].CopyTo(lastVertex);
        var channels = version == 0 ? default : tail.Slice(stride, stride / 4);

        var result = new byte[count * stride];
        var blockSizeMax = Math.Min((8192 / stride) & ~(ByteGroupSize - 1), 256);
        var position = 1;
        var offset = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 4 + 16);
        var transposed = ArrayPool<byte>.Shared.Rent(8192 + 256);
        try
        {
            while (offset < count)
            {
                cancellation.ThrowIfCancellationRequested();
                var block = Math.Min(blockSizeMax, count - offset);
                position = DecodeBlock(source, position, dataEnd, result.AsSpan(offset * stride, block * stride), block, stride, lastVertex, channels, version, buffer, transposed);
                offset += block;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(transposed);
        }

        if (dataEnd - position != tailPad)
        {
            throw new ModelFormatException("A meshopt attribute stream has trailing or missing data.");
        }

        return result;
    }

    private static int DecodeBlock(ReadOnlySpan<byte> source, int position, int dataEnd, Span<byte> vertices, int count, int stride, Span<byte> lastVertex, ReadOnlySpan<byte> channels, int version, byte[] buffer, byte[] transposed)
    {
        var aligned = (count + ByteGroupSize - 1) & ~(ByteGroupSize - 1);
        var controlSize = version == 0 ? 0 : stride / 4;
        if (dataEnd - position < controlSize)
        {
            throw new ModelFormatException("A meshopt attribute block is truncated.");
        }

        var control = source.Slice(position, controlSize);
        position += controlSize;
        if (count * stride > transposed.Length)
        {
            throw new ModelFormatException("A meshopt attribute block is larger than the format allows.");
        }

        for (var k = 0; k < stride; k += 4)
        {
            var controlByte = version == 0 ? 0 : control[k / 4];
            for (var j = 0; j < 4; j++)
            {
                var ctrl = (controlByte >> (j * 2)) & 3;
                var lane = buffer.AsSpan(j * aligned, aligned);
                if (ctrl == 3)
                {
                    if (dataEnd - position < count)
                    {
                        throw new ModelFormatException("A meshopt attribute block is truncated.");
                    }

                    lane.Clear();
                    source.Slice(position, count).CopyTo(lane);
                    position += count;
                }
                else if (ctrl == 2)
                {
                    lane.Clear();
                }
                else
                {
                    position = DecodeBytes(source, position, dataEnd, lane, aligned, version == 0 ? BitsV0 : BitsV1Variants[ctrl]);
                }
            }

            var channel = version == 0 ? 0 : channels[k / 4];
            switch (channel & 3)
            {
                case 0:
                    DecodeDeltas(buffer, aligned, transposed, count, stride, lastVertex, k, 1, 0, false);
                    break;
                case 1:
                    DecodeDeltas(buffer, aligned, transposed, count, stride, lastVertex, k, 2, 0, false);
                    break;
                case 2:
                    DecodeDeltas(buffer, aligned, transposed, count, stride, lastVertex, k, 4, (32 - (channel >> 4)) & 31, true);
                    break;
                default:
                    throw new ModelFormatException("A meshopt attribute block uses an unknown channel type.");
            }
        }

        transposed.AsSpan(0, count * stride).CopyTo(vertices);
        transposed.AsSpan((count - 1) * stride, stride).CopyTo(lastVertex);
        return position;
    }

    private static int DecodeBytes(ReadOnlySpan<byte> source, int position, int dataEnd, Span<byte> destination, int size, int[] bits)
    {
        var headerSize = (size / ByteGroupSize + 3) / 4;
        if (dataEnd - position < headerSize)
        {
            throw new ModelFormatException("A meshopt byte block is truncated.");
        }

        var header = source.Slice(position, headerSize);
        position += headerSize;
        for (var i = 0; i < size; i += ByteGroupSize)
        {
            if (dataEnd - position < 24)
            {
                throw new ModelFormatException("A meshopt byte group is truncated.");
            }

            var groupIndex = i / ByteGroupSize;
            var bitsK = (header[groupIndex / 4] >> ((groupIndex % 4) * 2)) & 3;
            position = DecodeGroup(source, position, destination.Slice(i, ByteGroupSize), bits[bitsK]);
        }

        return position;
    }

    private static int DecodeGroup(ReadOnlySpan<byte> source, int position, Span<byte> destination, int bits)
    {
        if (bits == 0)
        {
            destination.Clear();
            return position;
        }

        if (bits == 8)
        {
            source.Slice(position, ByteGroupSize).CopyTo(destination);
            return position + ByteGroupSize;
        }

        var packedBytes = ByteGroupSize * bits / 8;
        var extra = position + packedBytes;
        var sentinel = (1 << bits) - 1;
        var written = 0;
        for (var p = 0; p < packedBytes; p++)
        {
            int b = source[position + p];
            if (bits == 1)
            {
                b = ReverseBits(b);
            }

            for (var n = 0; n < 8 / bits; n++)
            {
                var encoded = b >> (8 - bits);
                b = (b << bits) & 0xFF;
                if (encoded == sentinel)
                {
                    destination[written++] = source[extra++];
                }
                else
                {
                    destination[written++] = (byte)encoded;
                }
            }
        }

        return extra;
    }

    private static int ReverseBits(int b)
    {
        var r = 0;
        for (var i = 0; i < 8; i++)
        {
            r |= ((b >> i) & 1) << (7 - i);
        }

        return r;
    }

    private static void DecodeDeltas(byte[] buffer, int aligned, byte[] transposed, int count, int stride, ReadOnlySpan<byte> lastVertex, int k, int width, int rotation, bool xor)
    {
        // Four byte lanes starting at vertex byte k, decoded as 4 / width channels of width bytes.
        for (var c = 0; c < 4; c += width)
        {
            uint previous = 0;
            for (var j = 0; j < width; j++)
            {
                previous |= (uint)lastVertex[k + c + j] << (8 * j);
            }

            for (var i = 0; i < count; i++)
            {
                uint value = 0;
                for (var j = 0; j < width; j++)
                {
                    value |= (uint)buffer[(c + j) * aligned + i] << (8 * j);
                }

                if (xor)
                {
                    value = ((value << rotation) | (value >> ((32 - rotation) & 31))) ^ previous;
                }
                else if (width == 1)
                {
                    value = (uint)(byte)(((value >> 1) ^ (uint)(-(int)(value & 1))) + previous);
                }
                else
                {
                    value = (uint)(ushort)(((value >> 1) ^ (uint)(-(int)(value & 1))) + previous);
                }

                for (var j = 0; j < width; j++)
                {
                    transposed[i * stride + k + c + j] = (byte)(value >> (8 * j));
                }

                previous = value;
            }
        }
    }

    // ---- indices ---------------------------------------------------------------------------------------------------

    public static uint[] DecodeTriangles(ReadOnlySpan<byte> source, int indexCount, CancellationToken cancellation = default)
    {
        if (indexCount % 3 != 0 || source.Length < 1 + indexCount / 3 + 16)
        {
            throw new ModelFormatException("A meshopt triangle stream is too short.");
        }

        if ((source[0] & 0xF0) != 0xE0 || (source[0] & 0x0F) > 1)
        {
            throw new ModelFormatException("Not a meshopt triangle stream.");
        }

        var version = source[0] & 0x0F;
        var fecMax = version >= 1 ? 13 : 15;
        var edgeFifo = new (uint, uint)[16];
        var vertexFifo = new uint[16];
        var edgeOffset = 0;
        var vertexOffset = 0;
        uint next = 0;
        uint last = 0;
        var result = new uint[indexCount];
        var code = 1;
        var codeEnd = code + indexCount / 3;
        var data = codeEnd;
        var safeEnd = source.Length - 16;
        var aux = source[safeEnd..];
        var o = 0;

        while (code < codeEnd)
        {
            Cancel.Every(cancellation, code);
            var tri = source[code++];
            uint a, b, c;
            if (tri < 0xF0)
            {
                var fe = tri >> 4;
                (a, b) = edgeFifo[(edgeOffset - 1 - fe) & 15];
                var fec = tri & 15;
                if (fec < fecMax)
                {
                    var cf = vertexFifo[(vertexOffset - 1 - fec) & 15];
                    c = fec == 0 ? next : cf;
                    if (fec == 0)
                    {
                        next++;
                    }

                    PushVertex(vertexFifo, c, ref vertexOffset, fec == 0 ? 1 : 0);
                }
                else
                {
                    if (data > safeEnd)
                    {
                        throw new ModelFormatException("A meshopt triangle stream is truncated.");
                    }

                    last = c = fec != 15 ? last + (uint)(fec * 2 - 27) : DecodeIndex(source, ref data, safeEnd, last);
                    PushVertex(vertexFifo, c, ref vertexOffset, 1);
                }

                edgeFifo[edgeOffset] = (c, b);
                edgeOffset = (edgeOffset + 1) & 15;
                edgeFifo[edgeOffset] = (a, c);
                edgeOffset = (edgeOffset + 1) & 15;
            }
            else
            {
                if (tri < 0xFE)
                {
                    var codeAux = aux[tri & 15];
                    var feb = codeAux >> 4;
                    var fec = codeAux & 15;
                    a = next++;
                    var bf = vertexFifo[(vertexOffset - feb) & 15];
                    b = feb == 0 ? next : bf;
                    var feb0 = feb == 0 ? 1 : 0;
                    next += (uint)feb0;
                    var cf = vertexFifo[(vertexOffset - fec) & 15];
                    c = fec == 0 ? next : cf;
                    var fec0 = fec == 0 ? 1 : 0;
                    next += (uint)fec0;
                    PushVertex(vertexFifo, a, ref vertexOffset, 1);
                    PushVertex(vertexFifo, b, ref vertexOffset, feb0);
                    PushVertex(vertexFifo, c, ref vertexOffset, fec0);
                }
                else
                {
                    if (data > safeEnd)
                    {
                        throw new ModelFormatException("A meshopt triangle stream is truncated.");
                    }

                    var codeAux = source[data++];
                    var fea = tri == 0xFE ? 0 : 15;
                    var feb = codeAux >> 4;
                    var fec = codeAux & 15;
                    if (codeAux == 0)
                    {
                        next = 0;
                    }

                    a = fea == 0 ? next++ : 0;
                    b = feb == 0 ? next++ : vertexFifo[(vertexOffset - feb) & 15];
                    c = fec == 0 ? next++ : vertexFifo[(vertexOffset - fec) & 15];
                    if (fea == 15)
                    {
                        last = a = DecodeIndex(source, ref data, safeEnd, last);
                    }

                    if (feb == 15)
                    {
                        last = b = DecodeIndex(source, ref data, safeEnd, last);
                    }

                    if (fec == 15)
                    {
                        last = c = DecodeIndex(source, ref data, safeEnd, last);
                    }

                    PushVertex(vertexFifo, a, ref vertexOffset, 1);
                    PushVertex(vertexFifo, b, ref vertexOffset, feb == 0 || feb == 15 ? 1 : 0);
                    PushVertex(vertexFifo, c, ref vertexOffset, fec == 0 || fec == 15 ? 1 : 0);
                }

                edgeFifo[edgeOffset] = (b, a);
                edgeOffset = (edgeOffset + 1) & 15;
                edgeFifo[edgeOffset] = (c, b);
                edgeOffset = (edgeOffset + 1) & 15;
                edgeFifo[edgeOffset] = (a, c);
                edgeOffset = (edgeOffset + 1) & 15;
            }

            result[o++] = a;
            result[o++] = b;
            result[o++] = c;
        }

        if (data != safeEnd)
        {
            throw new ModelFormatException("A meshopt triangle stream has trailing or missing data.");
        }

        return result;
    }

    public static uint[] DecodeIndexSequence(ReadOnlySpan<byte> source, int indexCount, CancellationToken cancellation = default)
    {
        if (source.Length < 1 + indexCount + 4)
        {
            throw new ModelFormatException("A meshopt index stream is too short.");
        }

        if ((source[0] & 0xF0) != 0xD0 || (source[0] & 0x0F) > 1)
        {
            throw new ModelFormatException("Not a meshopt index stream.");
        }

        var data = 1;
        var safeEnd = source.Length - 4;
        Span<uint> last = stackalloc uint[2];
        var result = new uint[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            Cancel.Every(cancellation, i);
            if (data >= safeEnd)
            {
                throw new ModelFormatException("A meshopt index stream is truncated.");
            }

            var v = DecodeVByte(source, ref data, safeEnd + 4);
            var current = (int)(v & 1);
            v >>= 1;
            var d = (v >> 1) ^ (uint)(-(int)(v & 1));
            var index = last[current] + d;
            last[current] = index;
            result[i] = index;
        }

        if (data != safeEnd)
        {
            throw new ModelFormatException("A meshopt index stream has trailing or missing data.");
        }

        return result;
    }

    private static void PushVertex(uint[] fifo, uint v, ref int offset, int advance)
    {
        fifo[offset] = v;
        offset = (offset + advance) & 15;
    }

    private static uint DecodeIndex(ReadOnlySpan<byte> source, ref int data, int limit, uint last)
    {
        var v = DecodeVByte(source, ref data, limit + 16);
        var d = (v >> 1) ^ (uint)(-(int)(v & 1));
        return last + d;
    }

    private static uint DecodeVByte(ReadOnlySpan<byte> source, ref int data, int limit)
    {
        if (data >= limit || data >= source.Length)
        {
            throw new ModelFormatException("A meshopt stream is truncated.");
        }

        var lead = source[data++];
        if (lead < 128)
        {
            return lead;
        }

        uint result = (uint)(lead & 127);
        var shift = 7;
        for (var i = 0; i < 4; i++)
        {
            if (data >= source.Length)
            {
                throw new ModelFormatException("A meshopt stream is truncated.");
            }

            var group = source[data++];
            result |= (uint)(group & 127) << shift;
            shift += 7;
            if (group < 128)
            {
                break;
            }
        }

        return result;
    }

    // ---- filters ---------------------------------------------------------------------------------------------------

    public static void ApplyFilter(byte[] data, int count, int stride, string filter, CancellationToken cancellation = default)
    {
        switch (filter)
        {
            case "NONE":
                return;
            case "OCTAHEDRAL":
                if (stride is not (4 or 8))
                {
                    throw new ModelFormatException("The octahedral filter needs a stride of 4 or 8.");
                }

                for (var i = 0; i < count; i++)
                {
                    Cancel.Every(cancellation, i);
                    DecodeOctahedral(data.AsSpan(i * stride, stride), stride == 8);
                }

                return;
            case "QUATERNION":
                if (stride != 8)
                {
                    throw new ModelFormatException("The quaternion filter needs a stride of 8.");
                }

                for (var i = 0; i < count; i++)
                {
                    Cancel.Every(cancellation, i);
                    DecodeQuaternion(data.AsSpan(i * 8, 8));
                }

                return;
            case "EXPONENTIAL":
                if (stride % 4 != 0)
                {
                    throw new ModelFormatException("The exponential filter needs a stride that is a multiple of 4.");
                }

                for (var i = 0; i < count * stride / 4; i++)
                {
                    Cancel.Every(cancellation, i);
                    var input = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4 * i, 4));
                    var e = input >> 24;
                    var m = (input << 8) >> 8;
                    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4 * i, 4), MathF.Pow(2f, Math.Clamp(e, -100, 100)) * m);
                }

                return;
            default:
                throw new ModelFormatException($"Unknown meshopt filter '{filter}'.");
        }
    }

    private static void DecodeOctahedral(Span<byte> v, bool wide)
    {
        float one, x, y;
        int max;
        if (wide)
        {
            one = BinaryPrimitives.ReadInt16LittleEndian(v[4..]);
            x = BinaryPrimitives.ReadInt16LittleEndian(v) / one;
            y = BinaryPrimitives.ReadInt16LittleEndian(v[2..]) / one;
            max = 32767;
        }
        else
        {
            one = (sbyte)v[2];
            x = (sbyte)v[0] / one;
            y = (sbyte)v[1] / one;
            max = 127;
        }

        var z = 1f - MathF.Abs(x) - MathF.Abs(y);
        var t = MathF.Min(z, 0f);
        x -= MathF.CopySign(t, x);
        y -= MathF.CopySign(t, y);
        var length = MathF.Sqrt(x * x + y * y + z * z);
        x /= length;
        y /= length;
        z /= length;
        if (wide)
        {
            BinaryPrimitives.WriteInt16LittleEndian(v, (short)MathF.Round(x * max));
            BinaryPrimitives.WriteInt16LittleEndian(v[2..], (short)MathF.Round(y * max));
            BinaryPrimitives.WriteInt16LittleEndian(v[4..], (short)MathF.Round(z * max));
        }
        else
        {
            v[0] = (byte)(sbyte)MathF.Round(x * max);
            v[1] = (byte)(sbyte)MathF.Round(y * max);
            v[2] = (byte)(sbyte)MathF.Round(z * max);
        }
    }

    private static void DecodeQuaternion(Span<byte> v)
    {
        var i0 = BinaryPrimitives.ReadInt16LittleEndian(v);
        var i1 = BinaryPrimitives.ReadInt16LittleEndian(v[2..]);
        var i2 = BinaryPrimitives.ReadInt16LittleEndian(v[4..]);
        var i3 = BinaryPrimitives.ReadInt16LittleEndian(v[6..]);
        var range = 1f / MathF.Sqrt(2f);
        var one = (float)(i3 | 3);
        var x = i0 / one * range;
        var y = i1 / one * range;
        var z = i2 / one * range;
        var w = MathF.Sqrt(MathF.Max(0f, 1f - x * x - y * y - z * z));
        var maxComponent = i3 & 3;
        Span<short> output = stackalloc short[4];
        output[(maxComponent + 1) % 4] = (short)MathF.Round(x * 32767f);
        output[(maxComponent + 2) % 4] = (short)MathF.Round(y * 32767f);
        output[(maxComponent + 3) % 4] = (short)MathF.Round(z * 32767f);
        output[maxComponent % 4] = (short)MathF.Round(w * 32767f);
        for (var k = 0; k < 4; k++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(v[(2 * k)..], output[k]);
        }
    }
}
