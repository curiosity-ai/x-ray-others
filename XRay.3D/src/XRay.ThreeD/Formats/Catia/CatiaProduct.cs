using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Catia;

using XRay.ThreeD.Internal;

/// <summary>
/// The instance structure of a CATIA V5 product (see docs/catia-format.md, "Products"): which reference each instance is of, what it is called and where it sits. The product's
/// <c>Data</c> stream starts with a catalog of length-prefixed strings; every later name is a token (the catalog index plus 0x35, one byte, or <c>0x32</c> and a little-endian
/// u32). Objects are <c>7c 06</c> headers (a class made of two tokens, <c>ea</c> and the object index) followed by a field list. An instance is an object of class
/// (<c>ASMPRODUCT</c>, reference name) with an <c>_InstanceName</c> field; its placement is the <c>_Position</c> field of the <c>3DIC</c> object that follows it, twelve numbers in the
/// stream's compact number form. Found by structure, never by position.
/// </summary>
internal static class CatiaProduct
{
    private const int FirstToken = 0x35;
    private const int MaxTokens = 1 << 18;
    private const int MaxInstances = 200_000;

    /// <summary>One instance. <see cref="Placed"/> is false when the document stores no position for it (identity); <see cref="Undecoded"/> when it stores one that could not be read.</summary>
    internal sealed record Instance(string? Reference, string? Name, Matrix4x4 Placement, bool Placed, bool Undecoded);

    /// <summary>The instances of the product held by <paramref name="data"/> (the <c>Data</c> stream), in file order; empty when the stream is not a product catalog.</summary>
    public static List<Instance> Decode(byte[] data, CancellationToken token)
    {
        var result = new List<Instance>();
        var tokens = Tokens(data, token);
        var asm = Find(tokens, "ASMPRODUCT");
        var instanceName = Find(tokens, "_InstanceName");
        var position = Find(tokens, "_Position");
        var bag = Find(tokens, "PRDBAGREP");
        var threeDic = Find(tokens, "3DIC");
        if (asm < 0 || instanceName < 0 || position < 0 || bag < 0 || threeDic < 0)
        {
            return result;
        }

        var tAsm = asm + FirstToken;
        var tName = instanceName + FirstToken;
        var tPosition = position + FirstToken;
        var tBag = bag + FirstToken;
        var tDic = threeDic + FirstToken;
        var span = data.AsSpan();
        var from = 0;
        var current = -1;
        var bound = false;
        var tick = 0;
        while (from < span.Length - 16)
        {
            Cancel.Every(token, tick++, 255);
            var at = span[from..].IndexOf((byte)0x7c);
            if (at < 0)
            {
                break;
            }

            var s = from + at;
            from = s + 1;
            if (span[s + 1] != 0x06 || !ReadObject(span, s, out var cls, out var category, out var body))
            {
                continue;
            }

            if (category == tAsm && FieldEnd(body, tName) is { } end)
            {
                if (result.Count >= MaxInstances)
                {
                    break;
                }

                // A name in the wide form (32 + u32) is numbered differently from the catalog in large products (checked against the files: the names it gives do not belong to the instance), so it is not trusted.
                var name = IsWide(body, end) ? null : NameOf(tokens, ReadToken(body, end));
                result.Add(new Instance(NameOf(tokens, cls), name, Matrix4x4.Identity, false, false));
                current = result.Count - 1;
                bound = false;
            }
            else if (category == tBag && cls == tDic && current >= 0 && !bound)
            {
                bound = true;
                if (Field(body, tPosition) is { } start)
                {
                    result[current] = result[current] with { Placed = true, Undecoded = true };
                    if (ReadPlacement(body, start) is { } matrix)
                    {
                        result[current] = result[current] with { Placement = matrix, Undecoded = false };
                    }
                }
            }
        }

        return result;
    }

    private static string? NameOf(List<string?> tokens, int token)
    {
        var index = token - FirstToken;
        return index >= 0 && index < tokens.Count ? tokens[index] : null;
    }

    private static int Find(List<string?> tokens, string name)
    {
        for (var i = 0; i < tokens.Count && i < 96; i++)
        {
            if (string.Equals(tokens[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The token table: from <c>CATCatalogManager</c> on, length-prefixed strings (the length counts its own byte), a <c>00</c> and a u32 length for a block (a constraint
    /// description, which takes a token of its own and no name), and single <c>01</c> or <c>21</c> separators.
    /// </summary>
    private static List<string?> Tokens(byte[] d, CancellationToken token)
    {
        var list = new List<string?>();
        var start = d.AsSpan().IndexOf("CATCatalogManager"u8);
        if (start < 1)
        {
            return list;
        }

        var k = start - 1;
        var tick = 0;
        while (k < d.Length && list.Count < MaxTokens)
        {
            Cancel.Every(token, tick++, 255);
            int length = d[k];
            if (length >= 2 && k + length <= d.Length && Printable(d.AsSpan(k + 1, length - 1)))
            {
                list.Add(Encoding.UTF8.GetString(d, k + 1, length - 1));
                k += length;
            }
            else if (length == 0 && k + 5 <= d.Length)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(k + 1));
                if (size == 0 || size > d.Length - k - 5)
                {
                    break;
                }

                list.Add(null);
                k += 5 + (int)size;
            }
            else if (length is 1 or 0x21)
            {
                k++;
            }
            else
            {
                break;
            }
        }

        return list;
    }

    private static bool Printable(ReadOnlySpan<byte> s)
    {
        foreach (var c in s)
        {
            if (c < 0x20 || c == 0x7f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A <c>7c 06</c> object header at <paramref name="s"/>: <c>len u32</c>, the class tokens, <c>ea</c>, the object index, references, and a trailing <c>7c 07 len</c> that opens the body.
    /// <paramref name="category"/> and <paramref name="cls"/> are the two class tokens.
    /// </summary>
    private static bool ReadObject(ReadOnlySpan<byte> d, int s, out int cls, out int category, out ReadOnlySpan<byte> body)
    {
        cls = category = -1;
        body = default;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(d[(s + 2)..]);
        if (length is < 14 or > 256 || s + 6 + length > d.Length)
        {
            return false;
        }

        var header = d.Slice(s + 6, (int)length);
        if (header[^6] != 0x7c || header[^5] != 0x07)
        {
            return false;
        }

        // The class ends at the first ea whose object index fits in three bytes.
        var ea = -1;
        for (var i = 2; i + 5 <= header.Length; i++)
        {
            if (header[i] == 0xea && header[i + 4] == 0)
            {
                ea = i;
                break;
            }
        }

        if (ea < 2)
        {
            return false;
        }

        var classBytes = header[..ea];
        cls = TokenBefore(classBytes, classBytes.Length, out var before);
        category = before > 0 ? TokenBefore(classBytes, before, out _) : -1;
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(header[^4..]);
        var bodyStart = s + 6 + (int)length;
        if (bodyLength > d.Length - bodyStart)
        {
            bodyLength = (uint)(d.Length - bodyStart);
        }

        body = d.Slice(bodyStart, (int)bodyLength);
        return true;
    }

    /// <summary>The token that ends at <paramref name="end"/>: <c>32 u32</c> or a single byte. <paramref name="begin"/> is where it starts.</summary>
    private static int TokenBefore(ReadOnlySpan<byte> d, int end, out int begin)
    {
        if (end >= 5 && d[end - 5] == 0x32 && d[end - 3] == 0 && d[end - 2] == 0 && d[end - 1] == 0)
        {
            begin = end - 5;
            return BinaryPrimitives.ReadUInt16LittleEndian(d[(end - 4)..]);
        }

        begin = end - 1;
        return end >= 1 ? d[end - 1] : -1;
    }

    /// <summary>Where the value of the field <paramref name="field"/> starts (a field starts the body or follows a <c>fe</c>), or null.</summary>
    private static int? Field(ReadOnlySpan<byte> body, int field)
    {
        for (var i = 0; i + 2 < body.Length; i++)
        {
            if (body[i] == field && (i == 0 || body[i - 1] == 0xfe) && body[i + 1] >= 0x80)
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>The position of the <c>fe</c> that ends the field <paramref name="field"/> (the value token sits just before it), or null when the body has no such field.</summary>
    private static int? FieldEnd(ReadOnlySpan<byte> body, int field)
    {
        if (Field(body, field) is not { } start)
        {
            return null;
        }

        var end = body[start..].IndexOf((byte)0xfe);
        return end < 0 ? null : start + end;
    }

    private static bool IsWide(ReadOnlySpan<byte> d, int end) => end >= 5 && d[end - 5] == 0x32 && d[end - 3] == 0 && d[end - 2] == 0 && d[end - 1] == 0;

    private static int ReadToken(ReadOnlySpan<byte> body, int end) => TokenBefore(body, end, out _);

    /// <summary>
    /// The twelve numbers after <c>8d 81</c>: the three axis vectors (the images of x, y and z) and the origin, each in the compact form <c>e6</c> + eight bytes (a little-endian
    /// double), <c>e7</c> (1), <c>e8</c> (0) or <c>e9</c> (-1).
    /// </summary>
    private static Matrix4x4? ReadPlacement(ReadOnlySpan<byte> body, int from)
    {
        var limit = Math.Min(body.Length, from + 24);
        var marker = -1;
        for (var i = from; i + 1 < limit; i++)
        {
            if (body[i] == 0x8d && body[i + 1] == 0x81)
            {
                marker = i + 2;
                break;
            }
        }

        if (marker < 0)
        {
            return null;
        }

        Span<double> v = stackalloc double[12];
        var p = marker;
        for (var n = 0; n < 12; n++)
        {
            if (p >= body.Length)
            {
                return null;
            }

            switch (body[p])
            {
                case 0xe6 when p + 9 <= body.Length:
                    v[n] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(body[(p + 1)..]));
                    p += 9;
                    break;
                case 0xe7:
                    v[n] = 1;
                    p++;
                    break;
                case 0xe8:
                    v[n] = 0;
                    p++;
                    break;
                case 0xe9:
                    v[n] = -1;
                    p++;
                    break;
                default:
                    return null;
            }

            if (!double.IsFinite(v[n]) || Math.Abs(v[n]) > 1e9)
            {
                return null;
            }
        }

        return new Matrix4x4(
            (float)v[0], (float)v[1], (float)v[2], 0,
            (float)v[3], (float)v[4], (float)v[5], 0,
            (float)v[6], (float)v[7], (float)v[8], 0,
            (float)v[9], (float)v[10], (float)v[11], 1);
    }
}
