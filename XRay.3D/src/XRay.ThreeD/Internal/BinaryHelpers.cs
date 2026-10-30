using System.Buffers.Binary;
using System.IO.Compression;

namespace XRay.ThreeD.Internal;

internal static class BinaryHelpers
{
    public static bool TryReadUInt32(ReadOnlySpan<byte> data, long offset, out uint value)
    {
        if (offset < 0 || offset + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)offset, 4));
        return true;
    }

    /// <summary>Raw deflate into a byte array of exactly <paramref name="expected"/> bytes, or null when the stream disagrees.</summary>
    public static byte[]? InflateExact(ReadOnlyMemory<byte> compressed, int expected)
    {
        if (expected < 0)
        {
            return null;
        }

        try
        {
            using var input = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(compressed, out var segment) && segment.Array is not null
                ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(compressed.ToArray(), writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var buffer = new byte[expected];
            var read = 0;
            while (read < expected)
            {
                var n = deflate.Read(buffer, read, expected - read);
                if (n == 0)
                {
                    return null;
                }

                read += n;
            }

            // The stream must end where the header says it does.
            return deflate.ReadByte() == -1 ? buffer : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
