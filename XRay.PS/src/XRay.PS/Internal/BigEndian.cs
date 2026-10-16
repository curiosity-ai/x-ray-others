using System.Buffers.Binary;

namespace XRay.PS.Internal;

/// <summary>Bounds-checked big-endian reads. A read past the end raises the same failure the
/// original's <c>struct.unpack</c> raised, so malformed fonts fail where they failed before.</summary>
internal static class BigEndian
{
    public static int U16(byte[] data, long offset)
    {
        Check(data, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)offset, 2));
    }

    public static int I16(byte[] data, long offset)
    {
        Check(data, offset, 2);
        return BinaryPrimitives.ReadInt16BigEndian(data.AsSpan((int)offset, 2));
    }

    public static long U32(byte[] data, long offset)
    {
        Check(data, offset, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)offset, 4));
    }

    public static int I32(byte[] data, long offset)
    {
        Check(data, offset, 4);
        return BinaryPrimitives.ReadInt32BigEndian(data.AsSpan((int)offset, 4));
    }

    public static int I8(byte[] data, long offset)
    {
        Check(data, offset, 1);
        return (sbyte)data[offset];
    }

    public static string Tag(byte[] data, long offset)
    {
        // data[offset:offset+4] in the original never fails; it truncates.
        if (offset < 0 || offset >= data.Length)
            return "";
        int len = (int)Math.Min(4, data.Length - offset);
        return Py.Latin1String(data.AsSpan((int)offset, len));
    }

    private static void Check(byte[] data, long offset, int size)
    {
        if (offset < 0 || offset + size > data.Length)
            throw new HostErrorException("struct.error", $"unpack requires a buffer of {size} bytes");
    }
}

/// <summary>A port of the part of Python's <c>difflib.SequenceMatcher</c> the font cache uses.</summary>
internal static class SequenceMatcher
{
    /// <summary><c>SequenceMatcher(a=a, b=b).ratio()</c> without a junk function.</summary>
    public static double Ratio(string a, string b)
    {
        int total = a.Length + b.Length;
        if (total == 0)
            return 1.0;
        int matches = MatchingCharacters(a, b);
        return 2.0 * matches / total;
    }

    private static int MatchingCharacters(string a, string b)
    {
        // b2j with the autojunk heuristic (only active for len(b) >= 200).
        var b2j = new Dictionary<char, List<int>>();
        for (int i = 0; i < b.Length; i++)
        {
            if (!b2j.TryGetValue(b[i], out var list))
                b2j[b[i]] = list = [];
            list.Add(i);
        }
        if (b.Length >= 200)
        {
            int ntest = b.Length / 100 + 1;
            foreach (var key in b2j.Where(kv => kv.Value.Count > ntest).Select(kv => kv.Key).ToList())
                b2j.Remove(key);
        }
        int count = 0;
        var queue = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        queue.Push((0, a.Length, 0, b.Length));
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var (i, j, k) = FindLongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (k > 0)
            {
                count += k;
                if (alo < i && blo < j)
                    queue.Push((alo, i, blo, j));
                if (i + k < ahi && j + k < bhi)
                    queue.Push((i + k, ahi, j + k, bhi));
            }
        }
        return count;
    }

    private static (int I, int J, int K) FindLongestMatch(string a, string b, Dictionary<char, List<int>> b2j, int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        var j2len = new Dictionary<int, int>();
        for (int i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var positions))
            {
                foreach (int j in positions)
                {
                    if (j < blo)
                        continue;
                    if (j >= bhi)
                        break;
                    int k = (j2len.TryGetValue(j - 1, out var prev) ? prev : 0) + 1;
                    newj2len[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }
            j2len = newj2len;
        }
        // Extend the match over adjacent equal characters (there is no junk function).
        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1])
        {
            besti--;
            bestj--;
            bestsize++;
        }
        while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize])
            bestsize++;
        return (besti, bestj, bestsize);
    }
}
