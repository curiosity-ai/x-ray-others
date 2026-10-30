using System.Buffers.Binary;
using System.Text;
using XRay.ThreeD.Formats.Parasolid;

namespace XRay.ThreeD.Tests;

/// <summary>
/// Writes the nodes of a parsed text transmit file in the binary (neutral) encoding, so that the binary reader can be checked against the text reader on the
/// same nodes. A file of an embedded schema gets its schema section per type: every base field deleted and the effective fields appended (a legal edit
/// sequence), or a full declaration for a type the base lacks.
/// </summary>
internal static class ParasolidBinaryWriter
{
    public static byte[] FromText(byte[] text, string? preamble = null)
    {
        var nodes = ParasolidReader.ReadNodes(text, null, 5_000_000, CancellationToken.None, out var keyText);
        var key = XtKey.Parse(keyText)!.Value;
        var layouts = XtLayouts.Candidates(key, false, out _);
        var layout = layouts.First(l => Matches(l, nodes));
        using var ms = new MemoryStream();
        if (preamble is not null)
        {
            ms.Write(Encoding.Latin1.GetBytes(preamble));
        }

        ms.Write("PS\0\0"u8);
        var version = ": TRANSMIT FILE created by modeller version " + key.Raw.Split('_')[1];
        U16(ms, version.Length);
        ms.Write(Encoding.Latin1.GetBytes(version));
        I32(ms, key.Raw.Length);
        ms.Write(Encoding.Latin1.GetBytes(key.Raw));
        if (key.Embedded)
        {
            U16(ms, nodes.Values.Max(n => n.Def.Type));
        }

        I32(ms, 0);
        var seen = new HashSet<int>();
        foreach (var node in nodes.Values)
        {
            I16(ms, node.Def.Type);
            if (key.Embedded && seen.Add(node.Def.Type))
            {
                Schema(ms, node.Def, XtSchema.Base(legacy: false).GetValueOrDefault(node.Def.Type), layout.OwnerFlags);
            }

            if (node.Def.Variable is not null)
            {
                I32(ms, node.Variable switch { double[] d => d.Length / node.Def.Variable.Width, long[] l => l.Length, string s => s.Length, _ => 0 });
            }

            Positive(ms, node.Index);
            foreach (var f in node.Def.Fields)
            {
                if (f.Count == 0)
                {
                    Variable(ms, f, node.Variable);
                }
                else if (f.Count < 0)
                {
                    ms.Write(new byte[layout.OwnerFlags]);
                }
                else
                {
                    for (var k = 0; k < f.Count; k++)
                    {
                        Scalar(ms, f, node, f.Slot + (f.IsDouble ? k * f.Width : k));
                    }
                }
            }
        }

        I16(ms, 1);
        I16(ms, 1);
        return ms.ToArray();
    }

    private static bool Matches(XtLayout layout, Dictionary<int, XtNode> nodes) => layout.Embedded || nodes.Values.All(n => layout.Types.TryGetValue(n.Def.Type, out var d) && d.Fields.Length == n.Def.Fields.Length);

    private static void Schema(MemoryStream ms, XtNodeDef def, XtNodeDef? baseDef, int ownerFlags)
    {
        ms.WriteByte((byte)def.Fields.Length);
        if (baseDef is null)
        {
            Str(ms, def.Name);
            Str(ms, "");
        }
        else
        {
            for (var i = 0; i < baseDef.Fields.Length; i++)
            {
                ms.WriteByte((byte)'D');
            }
        }

        foreach (var f in def.Fields)
        {
            if (baseDef is not null)
            {
                ms.WriteByte((byte)'A');
            }

            Str(ms, f.Name);
            U16(ms, f.Type == 'p' ? 1 : 0);
            Positive(ms, f.Count == 1 ? 0 : f.Count == 0 ? 1 : f.Count < 0 ? ownerFlags : f.Count);
            if (f.Type != 'p')
            {
                Str(ms, f.Type.ToString());
            }

            if (f.Count == 0)
            {
                ms.WriteByte(f.Transmitted ? (byte)1 : (byte)0);
            }
        }

        if (baseDef is not null)
        {
            ms.WriteByte((byte)'Z');
        }
    }

    private static void Scalar(MemoryStream ms, XtField f, XtNode node, int slot)
    {
        if (f.IsDouble)
        {
            for (var k = 0; k < f.Width; k++)
            {
                Double(ms, node.Doubles[slot + k]);
            }

            return;
        }

        Int(ms, f.Type, node.Ints[slot]);
    }

    private static void Int(MemoryStream ms, char type, long v)
    {
        switch (type)
        {
            case 'c' or 'u':
                ms.WriteByte((byte)v);
                break;
            case 'l':
                ms.WriteByte(v == 'T' ? (byte)1 : (byte)0);
                break;
            case 'n':
                I16(ms, (int)v);
                break;
            case 'w':
                U16(ms, (int)v);
                break;
            case 'p':
                Positive(ms, (int)v);
                break;
            default:
                I32(ms, (int)v);
                break;
        }
    }

    private static void Variable(MemoryStream ms, XtField f, object? value)
    {
        switch (value)
        {
            case string s:
                ms.Write(Encoding.Latin1.GetBytes(s));
                break;
            case double[] d:
                foreach (var x in d)
                {
                    Double(ms, x);
                }

                break;
            case long[] l:
                foreach (var x in l)
                {
                    Int(ms, f.Type, x);
                }

                break;
        }
    }

    private static void Double(MemoryStream ms, double v)
    {
        if (double.IsNaN(v))
        {
            ms.Write([0xc2, 0xbc, 0x92, 0x8f, 0x99, 0x6e, 0x00, 0x00]);
            return;
        }

        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, v);
        ms.Write(b);
    }

    private static void Str(MemoryStream ms, string s)
    {
        ms.WriteByte((byte)s.Length);
        ms.Write(Encoding.Latin1.GetBytes(s));
    }

    private static void U16(MemoryStream ms, int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        ms.Write(b);
    }

    private static void I16(MemoryStream ms, int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, (short)v);
        ms.Write(b);
    }

    private static void I32(MemoryStream ms, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        ms.Write(b);
    }

    private static void Positive(MemoryStream ms, int v)
    {
        if (v <= 32766)
        {
            I16(ms, v + 1);
            return;
        }

        var q = v / 32767;
        var mag = v + 1 - q * 32767;
        I16(ms, -mag);
        I16(ms, q);
    }
}
