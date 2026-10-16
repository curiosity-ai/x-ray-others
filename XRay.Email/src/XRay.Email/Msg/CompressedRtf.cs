using System.Buffers.Binary;

namespace XRay.Email.Msg;

/// <summary>
/// Decompressor for PR_RTF_COMPRESSED, the LZ77 variant described in [MS-OXRTFCP] section 2.
/// </summary>
internal static class CompressedRtf
{
    private const uint CompressedSignature = 0x75465A4C; // "LZFu"
    private const uint UncompressedSignature = 0x414C454D; // "MELA"
    private const int HeaderSize = 16;
    private const int DictionarySize = 4096;

    private static readonly byte[] InitialDictionary = System.Text.Encoding.ASCII.GetBytes(
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript " +
        "\\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par " +
        "\\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx");

    /// <summary>Returns the decompressed RTF, or null when the payload is not valid compressed RTF.</summary>
    public static byte[]? TryDecompress(byte[] data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length < HeaderSize)
        {
            return null;
        }

        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));

        // compressedSize counts the bytes after the size field itself.
        var end = (int)Math.Min((long)data.Length, (long)compressedSize + 4);
        if (signature == UncompressedSignature)
        {
            var length = (int)Math.Min(rawSize, (uint)Math.Max(0, end - HeaderSize));
            return data.AsSpan(HeaderSize, length).ToArray();
        }

        if (signature != CompressedSignature)
        {
            return null;
        }

        var dictionary = new byte[DictionarySize];
        InitialDictionary.CopyTo(dictionary, 0);
        var writePosition = InitialDictionary.Length;
        var output = new List<byte>((int)Math.Min(rawSize, 16u * 1024 * 1024));
        var position = HeaderSize;
        while (position < end)
        {
            if ((output.Count & 0xFFFF) < 32)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var control = data[position++];
            for (var bit = 0; bit < 8; bit++)
            {
                if ((control & (1 << bit)) == 0)
                {
                    if (position >= end)
                    {
                        return output.ToArray();
                    }

                    var literal = data[position++];
                    output.Add(literal);
                    dictionary[writePosition] = literal;
                    writePosition = (writePosition + 1) % DictionarySize;
                    continue;
                }

                if (position + 1 >= end)
                {
                    return output.ToArray();
                }

                var reference = (data[position] << 8) | data[position + 1];
                position += 2;
                var offset = reference >> 4;
                var length = (reference & 0x0F) + 2;
                if (offset == writePosition)
                {
                    // A reference to the current write position marks the end of the stream.
                    return output.ToArray();
                }

                for (var index = 0; index < length; index++)
                {
                    var value = dictionary[(offset + index) % DictionarySize];
                    output.Add(value);
                    dictionary[writePosition] = value;
                    writePosition = (writePosition + 1) % DictionarySize;
                }
            }
        }

        return output.ToArray();
    }
}
