namespace XRay.PS.Internal;

/// <summary>ASCII85 decoding in the two flavours the original used.</summary>
internal static class Ascii85
{
    /// <summary>Python <c>base64.a85decode(data, adobe=False)</c>; null where it raises.</summary>
    public static byte[]? DecodeStrict(ReadOnlySpan<byte> data)
    {
        var decoded = new List<byte>(data.Length);
        Span<byte> curr = stackalloc byte[5];
        int count = 0;
        int total = data.Length + 4;
        for (int i = 0; i < total; i++)
        {
            byte x = i < data.Length ? data[i] : (byte)'u';
            if (x >= (byte)'!' && x <= (byte)'u')
            {
                curr[count++] = x;
                if (count == 5)
                {
                    ulong acc = 0;
                    for (int k = 0; k < 5; k++)
                        acc = 85 * acc + (ulong)(curr[k] - 33);
                    if (acc > uint.MaxValue)
                        return null;
                    decoded.Add((byte)(acc >> 24));
                    decoded.Add((byte)(acc >> 16));
                    decoded.Add((byte)(acc >> 8));
                    decoded.Add((byte)acc);
                    count = 0;
                }
            }
            else if (x == (byte)'z')
            {
                if (count > 0)
                    return null;
                decoded.AddRange(new byte[4]);
            }
            else if (x is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0B)
            {
                continue;
            }
            else
            {
                return null;
            }
        }
        int padding = 4 - count;
        if (padding > 0)
            decoded.RemoveRange(Math.Max(0, decoded.Count - padding), Math.Min(padding, decoded.Count));
        return [.. decoded];
    }

    /// <summary>The lenient decoder of the original's ASCII85Decode filter.</summary>
    public static byte[] DecodeFilter(ReadOnlySpan<byte> data)
    {
        string text = Py.Latin1String(data);
        var filtered = new List<char>(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            if (text[index] == '~' && index + 1 < text.Length && text[index + 1] == '>')
                break;
            char ch = text[index];
            if (Py.IsSpace(ch))
            {
                index++;
                continue;
            }
            filtered.Add(ch);
            index++;
        }
        var output = new List<byte>(filtered.Count);
        var group = new List<char>(5);
        foreach (char ch in filtered)
        {
            if (ch == 'z' && group.Count == 0)
            {
                output.AddRange(new byte[4]);
                continue;
            }
            group.Add(ch);
            if (group.Count == 5)
            {
                output.AddRange(DecodeGroup(group));
                group.Clear();
            }
        }
        if (group.Count > 0)
        {
            int padding = 5 - group.Count;
            for (int i = 0; i < padding; i++)
                group.Add('u');
            var decoded = DecodeGroup(group);
            for (int i = 0; i < 4 - padding; i++)
                output.Add(decoded[i]);
        }
        return [.. output];
    }

    private static byte[] DecodeGroup(List<char> group)
    {
        System.Numerics.BigInteger value = 0;
        foreach (char ch in group)
            value = value * 85 + (ch - 33);
        if (value < 0 || value > uint.MaxValue)
            throw Py.OverflowError("int too big to convert");
        uint v = (uint)value;
        return [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }
}
