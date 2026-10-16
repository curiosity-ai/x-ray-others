// Ported from ps/filters.py.

using XRay.PS.Internal;

namespace XRay.PS.PostScript;

/// <summary>The result of decoding a filter chain: the data, and the first filter left encoded
/// (DCTDecode or CCITTFaxDecode) with its parameters.</summary>
public sealed record FilterResult(byte[] Data, string? RemainingFilter, Dictionary<string, object?>? Params);

/// <summary>PostScript decode filters.</summary>
public static class Filters
{
    public static FilterResult Decode(byte[] data, IReadOnlyList<FilterSpec> filters, bool allowEncoded = false)
    {
        string? remainingFilter = null;
        Dictionary<string, object?>? remainingParams = null;
        byte[] current = data;
        foreach (var (name, parameters) in filters)
        {
            string filterName = NormalizeName(name);
            if (filterName is "DCTDecode" or "CCITTFaxDecode")
            {
                if (allowEncoded)
                {
                    remainingFilter = filterName;
                    remainingParams = parameters;
                    break;
                }
                throw new PsUndefinedError($"unsupported filter {filterName}");
            }
            switch (filterName)
            {
                case "ReusableStreamDecode":
                    continue;
                case "SubFileDecode":
                    current = SubFileDecode(current, parameters?.GetValueOrDefault("Count"));
                    continue;
                case "ASCIIHexDecode":
                    current = AsciiHexDecode(current);
                    break;
                case "ASCII85Decode":
                    current = Ascii85.DecodeFilter(current);
                    break;
                case "LZWDecode":
                {
                    long earlyChange = 1;
                    var raw = parameters?.GetValueOrDefault("EarlyChange");
                    if (PsOps.IsNumeric(raw))
                        earlyChange = Py.Int(PsOps.ToDouble(raw));
                    current = LzwDecode(current, earlyChange);
                    break;
                }
                case "FlateDecode":
                    current = Zlib.Decompress(current);
                    break;
                case "RunLengthDecode":
                    current = RunLengthDecode(current);
                    break;
                default:
                    throw new PsUndefinedError($"unsupported filter {filterName}");
            }
        }
        return new FilterResult(current, remainingFilter, remainingParams);
    }

    public static string NormalizeName(string name) => name.StartsWith('/') ? name[1..] : name;

    public static byte[] AsciiHexDecode(ReadOnlySpan<byte> data)
    {
        var hex = new List<byte>(data.Length);
        foreach (byte ch in data)
        {
            if (ch is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0)
                continue;
            if (ch == '>')
                break;
            hex.Add(ch);
        }
        if (hex.Count % 2 == 1)
            hex.Add((byte)'0');
        // bytes.fromhex accepts ASCII whitespace between byte pairs.
        var result = new List<byte>(hex.Count / 2);
        int i = 0;
        while (i < hex.Count)
        {
            byte c = hex[i];
            if (c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0B or (byte)'\f')
            {
                i++;
                continue;
            }
            if (i + 1 >= hex.Count || !IsHex(c) || !IsHex(hex[i + 1]))
                throw new PsRangeError("invalid ASCIIHex data");
            result.Add((byte)((PsTokenizer.HexValue(c) << 4) | PsTokenizer.HexValue(hex[i + 1])));
            i += 2;
        }
        return [.. result];
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    public static byte[] RunLengthDecode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length * 2);
        int index = 0;
        while (index < data.Length)
        {
            int length = data[index++];
            if (length == 128)
                break;
            if (length <= 127)
            {
                int count = length + 1;
                int end = Math.Min(data.Length, index + count);
                for (int i = index; i < end; i++)
                    output.Add(data[i]);
                index += count;
            }
            else
            {
                int count = 257 - length;
                if (index >= data.Length)
                    break;
                byte value = data[index];
                for (int i = 0; i < count; i++)
                    output.Add(value);
                index++;
            }
        }
        return [.. output];
    }

    public static byte[] LzwDecode(ReadOnlySpan<byte> data, long earlyChange = 1)
    {
        if (data.IsEmpty)
            return [];
        var reader = new BitReader(data);
        const int clearCode = 256;
        const int endCode = 257;
        int codeSize = 9;
        var dictionary = NewLzwDictionary();
        int nextCode = 258;
        var output = new List<byte>(data.Length * 3);
        byte[]? prev = null;
        int early = (int)Math.Max(0, Math.Min(1, earlyChange));
        while (true)
        {
            int code = reader.Read(codeSize);
            if (code < 0)
                break;
            if (code == clearCode)
            {
                dictionary = NewLzwDictionary();
                nextCode = 258;
                codeSize = 9;
                prev = null;
                continue;
            }
            if (code == endCode)
                break;
            byte[] entry;
            if (dictionary.TryGetValue(code, out var found))
                entry = found;
            else if (prev is not null && code == nextCode)
                entry = [.. prev, prev[0]];
            else
                throw new PsRangeError("invalid LZW stream");
            output.AddRange(entry);
            if (prev is not null)
            {
                dictionary[nextCode] = [.. prev, entry[0]];
                nextCode++;
                if (codeSize < 12)
                {
                    int threshold = (1 << codeSize) - early;
                    if (nextCode == threshold)
                        codeSize++;
                }
            }
            prev = entry;
        }
        return [.. output];
    }

    private static Dictionary<int, byte[]> NewLzwDictionary()
    {
        var d = new Dictionary<int, byte[]>(4096);
        for (int i = 0; i < 256; i++)
            d[i] = [(byte)i];
        return d;
    }

    public static byte[] SubFileDecode(byte[] data, object? count)
    {
        if (count is null)
            return data;
        long n = count switch
        {
            long l => l,
            int i => i,
            bool b => b ? 1 : 0,
            double d when d < 0 => throw new PsRangeError("invalid SubFileDecode count"),
            double => throw Py.TypeError("slice indices must be integers or None or have an __index__ method"),
            _ => throw Py.TypeError("'<' not supported"),
        };
        if (n < 0)
            throw new PsRangeError("invalid SubFileDecode count");
        return data.AsSpan(0, (int)Math.Min(n, data.Length)).ToArray();
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bytePos;
        private int _bitPos;

        public int Read(int bits)
        {
            int value = 0;
            for (int i = 0; i < bits; i++)
            {
                if (_bytePos >= _data.Length)
                    return -1;
                value <<= 1;
                value |= (_data[_bytePos] >> (7 - _bitPos)) & 1;
                _bitPos++;
                if (_bitPos == 8)
                {
                    _bitPos = 0;
                    _bytePos++;
                }
            }
            return value;
        }
    }
}
