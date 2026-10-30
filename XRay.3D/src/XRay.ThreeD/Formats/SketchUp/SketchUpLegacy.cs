using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.SketchUp;

using XRay.ThreeD.Internal;
using Definition = SketchUpVff.Definition;
using Face = SketchUpVff.Face;
using Instance = SketchUpVff.Instance;
using SkpMaterial = SketchUpVff.SkpMaterial;

/// <summary>
/// The geometry of a SketchUp model of 2013 to 2020 (and the older releases that share the layout): a Microsoft Foundation Classes <c>CArchive</c>
/// object stream behind the two header strings. An object is introduced by a 16-bit tag: <c>0</c> is null, <c>0xFFFF</c> a new class (schema number,
/// name length, name) followed by its first object, <c>0x8000 | n</c> a new object of the class stored in slot n, <c>0x7FFF</c> an escape to a 32-bit
/// tag for slots above 32767, and a plain number a back reference to an object already read. Every class and every object takes the next slot, in
/// stream order, so a reader that walks the records exactly as the writer produced them recovers every reference. The classes are <c>CVertex</c>,
/// <c>CEdge</c>, <c>CEdgeUse</c> (the coedge of a loop), <c>CLoop</c>, <c>CFace</c>, <c>CComponentDefinition</c> (also groups), <c>CComponentInstance</c>
/// and <c>CGroup</c> (a definition slot and a 13-double matrix), <c>CLayer</c>, <c>CMaterial</c>, attribute dictionaries, text, dimensions, section
/// planes and construction geometry; a reader needs each one's size even when it ignores its value. The record layouts, the probes that find the
/// first slot number of the model section and the correction for slots that the writer numbers without writing (<c>burns</c>) follow the MIT-licensed
/// reader noted in THIRD_PARTY_NOTICES.md. Lengths are inches.
/// </summary>
internal static class SketchUpLegacy
{
    private const int MaxSlots = 40_000_000;
    private const int MaxDepth = 400;
    private const int FirstV4 = 4;
    private const int FirstDibObject = 6;
    private const int FirstNamedInstance = 5;
    private const int MaxV20FillerPad = 29;

    private static readonly byte[] StrMarker = [0xFF, 0xFE, 0xFF];

    // ---------------------------------------------------------------- object model

    private abstract class Obj
    {
    }

    private sealed class VertexObj : Obj
    {
        public double X, Y, Z;
    }

    private struct DrawBase
    {
        public int Mat;
        public int Hidden;
        public int Soft;
        public int Smooth;
        public int Layer;
    }

    private sealed class EdgeObj : Obj
    {
        public DrawBase Db;
        public int V1, V2, Curve;
        public Obj? CurveValue;
    }

    private sealed class CurveObj : Obj
    {
    }

    private sealed class EdgeUseObj : Obj
    {
        public int Edge;
        public int Sense;
    }

    private sealed class LoopObj : Obj
    {
        public readonly List<EdgeUseObj> Uses = new();
    }

    private sealed class FaceObj : Obj
    {
        public DrawBase Db;
        public double Nx, Ny, Nz;
        public readonly List<LoopObj> Loops = new();
        public int BackMat;
        public FtcObj? Ftc;
    }

    private sealed class LayerObj : Obj
    {
        public string Name = "";
        public int Hidden;
        public byte[] Rgba = [136, 136, 136, 255];
    }

    private sealed class MaterialObj : Obj
    {
        public string Name = "";
        public byte[] Rgba = [128, 128, 128, 255];
        public double Opacity = 1;
        public bool UseOpacity;
        public int TexDib = -1;
        public byte[]? TexData;
        public double TexW, TexH;
        public string TexFile = "";
        public bool Textured;
    }

    private sealed class DibObj : Obj
    {
        public byte[] Data = [];
    }

    private sealed class AttrContainerObj : Obj
    {
        public readonly List<Obj> Children = new();
    }

    private sealed class FtcObj : Obj
    {
        public double[] Front = [], Back = [];
        public bool FrontProjected, BackProjected;
    }

    private sealed class DefinitionObj : Obj
    {
        public string Name = "";
        public bool IsGroup;
        public List<(int Slot, Obj? Value)> Ents = new();
    }

    private sealed class InstanceObj : Obj
    {
        public DrawBase Db;
        public int Def;
        public double[] Xf = [];
        public string Name = "";
        public bool IsImage;
    }

    private sealed class OtherObj : Obj
    {
    }

    private sealed class Slot
    {
        public bool IsClass;
        public string? Name;
        public Obj? Value;
    }

    private readonly record struct Ref(int Slot, string? Name, Obj? Value);

    private sealed class WalkException : ModelFormatException
    {
        public WalkException(string message, int at)
            : base(message + " at 0x" + at.ToString("x", System.Globalization.CultureInfo.InvariantCulture))
        {
            At = at;
        }

        public int At { get; }
    }

    // ---------------------------------------------------------------- byte reader

    private sealed class R
    {
        public readonly byte[] Data;
        public int Pos;

        public R(byte[] data, int pos = 0)
        {
            Data = data;
            Pos = pos;
        }

        private void Need(int n)
        {
            if (n < 0 || Pos < 0 || Pos > Data.Length - n)
            {
                throw Fail("read past the end of the stream");
            }
        }

        public WalkException Fail(string message) => new(message, Pos);

        public int U8()
        {
            Need(1);
            return Data[Pos++];
        }

        public int U16()
        {
            Need(2);
            var v = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(Pos));
            Pos += 2;
            return v;
        }

        public uint U32()
        {
            Need(4);
            var v = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Pos));
            Pos += 4;
            return v;
        }

        public int I32() => (int)U32();

        public double F64()
        {
            Need(8);
            var v = BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan(Pos));
            Pos += 8;
            return v;
        }

        public double[] F64s(int n)
        {
            var result = new double[n];
            for (var i = 0; i < n; i++)
            {
                result[i] = F64();
            }

            return result;
        }

        public byte[] Raw(long n)
        {
            if (n < 0 || n > Data.Length - Pos)
            {
                throw Fail("read past the end of the stream");
            }

            var v = Data.AsSpan(Pos, (int)n).ToArray();
            Pos += (int)n;
            return v;
        }

        public void Skip(long n)
        {
            if (n < 0 || n > Data.Length - Pos)
            {
                throw Fail("read past the end of the stream");
            }

            Pos += (int)n;
        }

        public int PeekU16()
        {
            Need(2);
            return BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(Pos));
        }

        public bool PeekIsStrMarker() => Pos >= 0 && Pos + 3 <= Data.Length && Data.AsSpan(Pos, 3).SequenceEqual(StrMarker);

        public string Utf16()
        {
            if (!PeekIsStrMarker())
            {
                throw Fail("expected a string record");
            }

            Pos += 3;
            long n = U8();
            if (n == 255)
            {
                n = U16();
                if (n == 65535)
                {
                    n = U32();
                }
            }

            if (2 * n > Data.Length - Pos)
            {
                throw Fail("string past the end of the stream");
            }

            var s = Encoding.Unicode.GetString(Data, Pos, (int)(2 * n));
            Pos += (int)(2 * n);
            return s;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static int At(byte[] d, int i) => i >= 0 && i < d.Length ? d[i] : -1;

    private static bool MatchesAscii(byte[] d, int offset, string s)
    {
        if (offset < 0 || offset + s.Length > d.Length)
        {
            return false;
        }

        for (var i = 0; i < s.Length; i++)
        {
            if (d[offset + i] != s[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsClassRef(byte[] d, int p, int slot)
    {
        if (slot <= 32767)
        {
            return p >= 0 && p + 2 <= d.Length && BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)) == (0x8000 | slot);
        }

        return p >= 0 && p + 6 <= d.Length && BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)) == 0x7FFF && BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 2)) == (0x80000000u | (uint)slot);
    }

    /// <summary>Index of the pattern in [start, end), where a negative entry matches any byte.</summary>
    private static int FindPattern(byte[] d, ReadOnlySpan<int> pattern, int start, int end, CancellationToken token)
    {
        var last = Math.Min(end, d.Length) - pattern.Length;
        for (var i = Math.Max(0, start); i <= last; i++)
        {
            Cancel.Every(token, i);
            var ok = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (pattern[j] >= 0 && d[i + j] != pattern[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindMarker(byte[] d, int start, int end)
    {
        start = Math.Max(0, start);
        end = Math.Min(end, d.Length);
        if (end <= start)
        {
            return -1;
        }

        var i = d.AsSpan(start, end - start).IndexOf(StrMarker);
        return i < 0 ? -1 : start + i;
    }

    // ---------------------------------------------------------------- archive

    private sealed class Archive
    {
        public readonly byte[] Data;
        public readonly int Ver;
        public readonly bool HasPid;
        public readonly R R;
        public readonly Dictionary<int, Slot> Slots = new();
        public readonly Dictionary<string, int> ClassSlot = new(StringComparer.Ordinal);
        public int NextSlot;
        public int WalkBase;
        public int? CurrentLoop;
        public int? CurrentObjSlot;
        public readonly List<(int Start, int Width)> Burns = new();
        public int CumDelta;
        public int? AnnotWatermark;
        public readonly List<int> BurnStack = new();
        public int? ClineTail;
        public int Depth;
        public CancellationToken Token;
        private long _tick;

        public Archive(byte[] data, int ver, CancellationToken token)
        {
            Data = data;
            Ver = ver;
            HasPid = ver >= 17;
            R = new R(data);
            Token = token;
        }

        public int Alloc(Slot entry)
        {
            if (Slots.Count >= MaxSlots)
            {
                throw R.Fail("too many objects");
            }

            var s = NextSlot++;
            Slots[s] = entry;
            return s;
        }

        public Ref ReadObject(R r, string? expect = null)
        {
            Cancel.Every(Token, _tick++);
            var tag = r.U16();
            if (tag == 0)
            {
                return new Ref(-1, null, null);
            }

            if (tag == 0x7FFF)
            {
                var big = r.U32();
                if ((big & 0x80000000u) != 0)
                {
                    return NewOfClass(r, (int)(big & 0x7FFFFFFF), expect);
                }

                return BackRef((int)Math.Min(big, int.MaxValue), r);
            }

            if (tag == 0xFFFF)
            {
                var schema = r.U16();
                _ = schema;
                var nameLength = r.U16();
                if (nameLength > 40)
                {
                    throw r.Fail("implausible class name length");
                }

                var name = Encoding.UTF8.GetString(r.Raw(nameLength));
                Alloc(new Slot { IsClass = true, Name = name });
                ClassSlot[name] = NextSlot - 1;
                return NewObj(r, name);
            }

            if ((tag & 0x8000) != 0)
            {
                return NewOfClass(r, tag & 0x7FFF, expect);
            }

            return BackRef(tag, r);
        }

        private Ref NewOfClass(R r, int classSlot, string? expect)
        {
            if (!Slots.TryGetValue(classSlot, out var entry))
            {
                if (expect is null)
                {
                    throw r.Fail("class reference to an unknown slot " + classSlot);
                }

                entry = new Slot { IsClass = true, Name = expect };
                Slots[classSlot] = entry;
                ClassSlot[expect] = classSlot;
            }

            if (!entry.IsClass)
            {
                throw r.Fail("class reference to a non-class slot " + classSlot);
            }

            return NewObj(r, entry.Name!);
        }

        private Ref NewObj(R r, string name)
        {
            if (++Depth > MaxDepth)
            {
                throw r.Fail("objects nested too deeply");
            }

            try
            {
                var slot = Alloc(new Slot { Name = name });
                var prevSlot = CurrentObjSlot;
                CurrentObjSlot = slot;
                Obj? value;
                try
                {
                    value = ReadClass(this, r, name);
                }
                finally
                {
                    CurrentObjSlot = prevSlot;
                }

                Slots[slot].Value = value;
                if (name is "CDimensionLinear" or "CText")
                {
                    AnnotWatermark = NextSlot;
                }

                return new Ref(slot, name, value);
            }
            finally
            {
                Depth--;
            }
        }

        private Ref BackRef(int slot, R r)
        {
            if (Burns.Count > 0 && slot >= Burns[0].Start)
            {
                var offset = 0;
                var phantom = false;
                foreach (var (start, width) in Burns)
                {
                    if (slot < start)
                    {
                        break;
                    }

                    if (slot < start + width)
                    {
                        phantom = true;
                        break;
                    }

                    offset += width;
                }

                if (phantom)
                {
                    return new Ref(slot, "reserved", null);
                }

                slot -= offset;
            }

            if (!Slots.TryGetValue(slot, out var entry))
            {
                if (slot < WalkBase)
                {
                    return new Ref(slot, "premodel", null);
                }

                throw r.Fail("back reference to an unwalked slot " + slot);
            }

            if (entry.IsClass)
            {
                throw r.Fail("back reference to a class slot " + slot);
            }

            return new Ref(slot, entry.Name, entry.Value);
        }

        public bool ClayerRecordAt(int at, bool unmatchedClassRef)
        {
            if (at < 0 || at + 2 > Data.Length)
            {
                return false;
            }

            var tag = Data[at] | (Data[at + 1] << 8);
            if (tag == 0xFFFF && at + 12 <= Data.Length && (Data[at + 4] | (Data[at + 5] << 8)) == 6 && MatchesAscii(Data, at + 6, "CLayer"))
            {
                return true;
            }

            if (ClassSlot.TryGetValue("CLayer", out var cs) && IsClassRef(Data, at, cs))
            {
                return true;
            }

            return unmatchedClassRef && (tag & 0x8000) != 0 && tag != 0xFFFF;
        }

        public void SkipClayerColourExt()
        {
            const int pad = 12, ext = 16;
            if (R.Pos + ext + 2 > Data.Length)
            {
                return;
            }

            for (var i = 0; i < pad; i++)
            {
                if (Data[R.Pos + i] != 0)
                {
                    return;
                }
            }

            if (ClayerRecordAt(R.Pos + ext, true))
            {
                R.Pos += ext;
            }
        }

        public void SkipClayerParentRef(int? selfSlot)
        {
            if (R.Pos + 4 > Data.Length || selfSlot is null)
            {
                return;
            }

            var a = Data[R.Pos] | (Data[R.Pos + 1] << 8);
            var b = Data[R.Pos + 2] | (Data[R.Pos + 3] << 8);
            if (a == 0 || (a & 0x8000) != 0 || a == 0x7FFF || a == selfSlot)
            {
                return;
            }

            if (b == 0 || (b & 0x8000) != 0 || b == 0x7FFF)
            {
                return;
            }

            if (!Slots.TryGetValue(a, out var ea) || !Slots.TryGetValue(b, out var eb))
            {
                return;
            }

            if (ea.IsClass || eb.IsClass || ea.Name != "CLayer" || eb.Name != "CLayer")
            {
                return;
            }

            R.Pos += 2;
        }

        public void CollectTrailingLayers(List<int>? slotsOut, List<(int Slot, LayerObj Layer)>? layersOut, bool unmatchedClassRef)
        {
            while (R.Pos + 2 <= Data.Length)
            {
                var tag = R.PeekU16();
                if (tag == 0)
                {
                    R.Pos += 2;
                    continue;
                }

                if (!ClayerRecordAt(R.Pos, unmatchedClassRef))
                {
                    break;
                }

                var (s, _, v) = ReadObject(R, "CLayer");
                if (v is LayerObj layer)
                {
                    slotsOut?.Add(s);
                    layersOut?.Add((s, layer));
                }
            }
        }
    }

    private static bool PlausibleListTag(Archive ar, byte[] d, int at)
    {
        if (at < 0 || at + 2 > d.Length)
        {
            return false;
        }

        var t = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
        if (t is 0 or 32767 or 65535)
        {
            return true;
        }

        if ((t & 0x8000) != 0)
        {
            return ar.Slots.TryGetValue(t & 0x7FFF, out var ent) && ent.IsClass;
        }

        return t < ar.NextSlot;
    }

    private static bool StrictNextTag(Archive ar, byte[] d, int at, bool allowNull)
    {
        if (at < 0 || at + 2 > d.Length)
        {
            return false;
        }

        var t = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
        if (t == 0)
        {
            return allowNull;
        }

        if (t is 0x7FFF or 0xFFFF)
        {
            return true;
        }

        if ((t & 0x8000) != 0)
        {
            return ar.Slots.TryGetValue(t & 0x7FFF, out var ent) && ent.IsClass;
        }

        return false;
    }

    private static (long Count, int Next)? FindCountAfterV20Filler(byte[] d, int countPos, long limit, Archive? ar)
    {
        var markerAt = -1;
        for (var i = Math.Max(0, countPos); i < countPos + 12 && i + 4 <= d.Length; i++)
        {
            if (d[i] == 0xFF && d[i + 1] == 0xFE && d[i + 2] == 0xFF)
            {
                markerAt = i;
                break;
            }
        }

        if (markerAt < 0 || d[markerAt + 3] != 0)
        {
            return null;
        }

        for (var pad = 1; pad <= MaxV20FillerPad; pad += 4)
        {
            var at = markerAt + 4 + pad;
            if (at + 4 > d.Length)
            {
                break;
            }

            var count = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
            if (count > 0 && count <= limit && (ar is null || PlausibleListTag(ar, d, at + 4)))
            {
                return (count, at + 4);
            }
        }

        return null;
    }

    private static long? RetryCountAfterV20Filler(R r, int countPos, long limit, Archive? ar)
    {
        var hit = FindCountAfterV20Filler(r.Data, countPos, limit, ar);
        if (hit is null)
        {
            return null;
        }

        r.Pos = hit.Value.Next;
        return hit.Value.Count;
    }

    // ---------------------------------------------------------------- class readers

    private static Obj? ReadClass(Archive ar, R r, string name) => name switch
    {
        "CVertex" => ReadVertex(ar, r),
        "CEdge" => ReadEdge(ar, r),
        "CCurve" => ReadCurve(ar, r),
        "CArcCurve" => ReadArcCurve(ar, r),
        "CEdgeUse" => ReadEdgeUse(ar, r),
        "CLoop" => ReadLoop(ar, r),
        "CFace" => ReadFace(ar, r),
        "CLayer" => ReadLayer(ar, r),
        "CMaterial" => ReadMaterial(ar, r),
        "CDib" => ReadDib(r),
        "CAttributeContainer" => ReadAttrContainer(ar, r),
        "CAttributeNamed" => ReadAttrNamed(ar, r),
        "CCamera" => ReadCamera(ar, r),
        "CThumbnail" => ReadThumbnail(ar, r),
        "CRelationship" => ReadRelationship(ar, r),
        "CComponentDefinition" => ReadDefinition(ar, r),
        "CImage" => ReadImage(ar, r),
        "CComponentInstance" or "CGroup" => ReadInstance(ar, r),
        "CFaceTextureCoords" => ReadFtc(ar, r),
        "CConstructionLine" => ReadConstructionLine(ar, r),
        "CConstructionPoint" => ReadConstructionPoint(ar, r),
        "CSectionPlane" => ReadSectionPlane(ar, r),
        "CSkFont" => ReadSkFont(ar, r),
        "CDimensionLinear" => ReadDimLinear(ar, r),
        "CText" => ReadText(ar, r),
        _ => throw r.Fail("no reader for class " + name),
    };

    private static (Obj? Attrs, int AttrsSlot) Preamble(Archive ar, R r)
    {
        var (slot, _, attrs) = ar.ReadObject(r, "CAttributeContainer");
        if (ar.HasPid)
        {
            var mask = r.U8();
            for (var bit = 0; bit < 8; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                {
                    r.U8();
                }
            }
        }

        return (attrs, slot);
    }

    private static (Obj? Attrs, int AttrsSlot) EarlyPreamble(Archive ar, R r) => ar.Ver < FirstV4 ? (null, -1) : Preamble(ar, r);

    private static DrawBase Drawbase(Archive ar, R r)
    {
        var b = r.Raw(ar.Ver < FirstV4 ? 5 : ar.Ver < 5 ? 7 : 8);
        var tag = r.PeekU16();
        int layer;
        if (ar.ClassSlot.TryGetValue("CLayer", out var layerClass) && tag == (0x8000 | layerClass))
        {
            ar.ReadObject(r, "CLayer");
            layer = 0;
        }
        else if (tag == 0x7FFF)
        {
            r.U16();
            var big = r.U32();
            if ((big & 0x80000000u) != 0)
            {
                throw r.Fail("drawbase layer: unexpected class");
            }

            layer = 0;
        }
        else
        {
            layer = r.U16();
        }

        if (ar.Ver < FirstV4)
        {
            return new DrawBase { Layer = layer };
        }

        return new DrawBase { Mat = b[0] | (b[1] << 8), Hidden = b[2], Soft = b[5], Smooth = b[6], Layer = layer };
    }

    private static Obj ReadVertex(Archive ar, R r)
    {
        EarlyPreamble(ar, r);
        var v = r.F64s(3);
        return new VertexObj { X = v[0], Y = v[1], Z = v[2] };
    }

    private static Obj ReadEdge(Archive ar, R r)
    {
        Preamble(ar, r);
        var db = Drawbase(ar, r);
        var (s1, _, _) = ar.ReadObject(r, "CVertex");
        var (s2, _, _) = ar.ReadObject(r, "CVertex");
        var (cs, cn, cv) = ar.ReadObject(r);
        if (cn is not null && cn != "CCurve" && cn != "CArcCurve")
        {
            throw r.Fail("edge curve pointer resolved to " + cn);
        }

        return new EdgeObj { Db = db, V1 = s1, V2 = s2, Curve = cs, CurveValue = cv };
    }

    private static Obj ReadCurve(Archive ar, R r)
    {
        Preamble(ar, r);
        r.U8();
        r.U32();
        return new CurveObj();
    }

    private static Obj ReadArcCurve(Archive ar, R r)
    {
        Preamble(ar, r);
        r.Skip(5);
        r.F64s(14);
        return new CurveObj();
    }

    private static void RegisterBurn(Archive ar, int delta)
    {
        ar.Burns.Add((ar.AnnotWatermark!.Value + ar.CumDelta, delta));
        ar.CumDelta += delta;
        ar.AnnotWatermark = null;
        if (ar.BurnStack.Count > 0)
        {
            ar.BurnStack[^1]++;
        }
    }

    private static Obj ReadEdgeUse(Archive ar, R r)
    {
        EarlyPreamble(ar, r);
        var (es, _, _) = ar.ReadObject(r, "CEdge");
        var sense = r.U8();
        var p0 = r.Pos;
        var tag = r.U16();
        int? ps;
        if (tag == 0x7FFF)
        {
            var big = r.U32();
            if ((big & 0x80000000u) != 0)
            {
                throw r.Fail("edge-use parent is a new object");
            }

            ps = (int)Math.Min(big, int.MaxValue);
        }
        else if (tag == 0xFFFF || (tag & 0x8000) != 0)
        {
            throw r.Fail("edge-use parent is a new object");
        }
        else
        {
            ps = tag != 0 ? tag : null;
        }

        int? expected = ar.CurrentLoop is { } loop ? loop + ar.CumDelta : null;
        if (ps != expected)
        {
            var delta = ps is { } p && expected is { } e ? p - e : 0;
            if (delta > 0 && delta <= 4096 && ar.AnnotWatermark is not null)
            {
                RegisterBurn(ar, delta);
            }
            else
            {
                r.Pos = p0;
                throw r.Fail("edge-use parent slot " + ps + " does not match the current loop " + expected);
            }
        }

        return new EdgeUseObj { Edge = es, Sense = sense };
    }

    private static Obj ReadLoop(Archive ar, R r)
    {
        var mySlot = ar.NextSlot - 1;
        var prev = ar.CurrentLoop;
        ar.CurrentLoop = mySlot;
        try
        {
            EarlyPreamble(ar, r);
            r.Skip(2);
            var loop = new LoopObj();
            while (true)
            {
                if (r.PeekU16() == 0)
                {
                    r.Pos += 2;
                    break;
                }

                var (_, _, v) = ar.ReadObject(r, "CEdgeUse");
                if (v is EdgeUseObj use)
                {
                    loop.Uses.Add(use);
                }

                if (loop.Uses.Count > 1_000_000)
                {
                    throw r.Fail("implausible loop length");
                }
            }

            return loop;
        }
        finally
        {
            ar.CurrentLoop = prev;
        }
    }

    private static Obj ReadFace(Archive ar, R r)
    {
        var (attrs, attrsSlot) = Preamble(ar, r);
        var db = Drawbase(ar, r);
        if (ar.Ver < FirstV4 && attrs is MaterialObj)
        {
            db.Mat = attrsSlot;
            attrs = null;
        }

        var plane = r.F64s(4);
        var nloops = r.U32();
        if (nloops > 10_000)
        {
            throw r.Fail("implausible loop count " + nloops);
        }

        var face = new FaceObj { Db = db, Nx = plane[0], Ny = plane[1], Nz = plane[2] };
        for (var i = 0; i < nloops; i++)
        {
            var (_, _, v) = ar.ReadObject(r, "CLoop");
            if (v is LoopObj loop)
            {
                face.Loops.Add(loop);
            }
        }

        face.BackMat = r.U16();
        if (attrs is AttrContainerObj container)
        {
            foreach (var child in container.Children)
            {
                if (child is FtcObj ftc)
                {
                    face.Ftc = ftc;
                }
            }
        }

        return face;
    }

    private static Obj ReadAttrContainer(Archive ar, R r)
    {
        Preamble(ar, r);
        var container = new AttrContainerObj();
        while (true)
        {
            if (r.PeekU16() == 0)
            {
                r.Pos += 2;
                break;
            }

            var (_, _, v) = ar.ReadObject(r, "CAttributeNamed");
            if (v is not null)
            {
                container.Children.Add(v);
            }

            if (container.Children.Count > 100_000)
            {
                throw r.Fail("implausible attribute count");
            }
        }

        return container;
    }

    private static void SkipTyped(R r, int t, int depth)
    {
        if (depth > 8)
        {
            throw r.Fail("attribute arrays nested too deeply");
        }

        switch (t)
        {
            case 0:
                return;
            case 4 or 9:
                r.U32();
                return;
            case 6 or 12:
                r.F64();
                return;
            case 7:
                r.U8();
                return;
            case 10:
                r.Utf16();
                return;
            case 11:
            {
                var n = r.U32();
                if (n > 100_000)
                {
                    throw r.Fail("implausible attribute array count");
                }

                for (var i = 0; i < n; i++)
                {
                    SkipTyped(r, r.U8(), depth + 1);
                }

                return;
            }

            case 17 or 18:
                r.Skip(24);
                return;
            default:
                throw r.Fail("unknown attribute value type 0x" + t.ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static Obj ReadAttrNamed(Archive ar, R r)
    {
        Preamble(ar, r);
        r.Skip(4);
        r.Utf16();
        var entries = 0;
        while (true)
        {
            var key = r.Utf16();
            if (key.Length == 0)
            {
                break;
            }

            SkipTyped(r, r.U8(), 0);
            if (++entries > 1_000_000)
            {
                throw r.Fail("implausible attribute dictionary");
            }
        }

        if (ar.Ver >= 7)
        {
            r.U32();
        }

        return new OtherObj();
    }

    private static Obj ReadLayer(Archive ar, R r)
    {
        EarlyPreamble(ar, r);
        var name = r.Utf16();
        var mid = new List<int>();
        while (mid.Count < 8 && !r.PeekIsStrMarker())
        {
            mid.Add(r.U8());
        }

        r.Utf16();
        var flags = r.U16();
        var layer = new LayerObj { Name = name, Hidden = mid.Count > 0 ? mid[0] : 0 };
        if ((flags & 255) != 0)
        {
            var tex = TextureBlock(ar, r);
            r.Skip(4);
            layer.Rgba = tex.Rgba;
            return layer;
        }

        layer.Rgba = r.Raw(4);
        r.Utf16();
        r.Skip(ar.Ver < FirstV4 ? 17 : 21);
        ar.SkipClayerColourExt();
        ar.SkipClayerParentRef(ar.CurrentObjSlot);
        return layer;
    }

    private static MaterialObj TextureBlock(Archive ar, R r)
    {
        var tex = new MaterialObj { Textured = true };
        if (ar.Ver < FirstDibObject)
        {
            tex.TexData = ReadDib(r).Data;
        }
        else
        {
            r.Pos -= 1;
            Preamble(ar, r);
            var (s, _, dib) = ar.ReadObject(r, "CDib");
            if (dib is not DibObj)
            {
                throw r.Fail("texture object is not a dib");
            }

            tex.TexDib = s;
        }

        var marker = FindMarker(r.Data, r.Pos, r.Pos + 28);
        if (marker - r.Pos == 20)
        {
            r.U32();
        }
        else if (marker - r.Pos != 16)
        {
            throw r.Fail("texture size block misaligned");
        }

        tex.TexW = r.F64();
        tex.TexH = r.F64();
        tex.TexFile = r.Utf16();
        var avg = r.Raw(9);
        r.Utf16();
        r.Skip(8);
        tex.Opacity = r.F64();
        tex.UseOpacity = r.U8() != 0;
        tex.Rgba = avg[..4];
        return tex;
    }

    private static Obj ReadMaterial(Archive ar, R r)
    {
        EarlyPreamble(ar, r);
        var name = r.Utf16();
        var texflag = r.U16();
        if (texflag == 0)
        {
            var m = new MaterialObj { Name = name, Rgba = r.Raw(4) };
            r.Utf16();
            r.Skip(8);
            m.Opacity = r.F64();
            m.UseOpacity = r.U8() != 0;
            return m;
        }

        var tex = TextureBlock(ar, r);
        tex.Name = name;
        return tex;
    }

    private static DibObj ReadDib(R r)
    {
        r.U32();
        var length = r.U32();
        if (length > r.Data.Length)
        {
            throw r.Fail("implausible dib length " + length);
        }

        return new DibObj { Data = r.Raw(length) };
    }

    private static Obj ReadFtc(Archive ar, R r)
    {
        Preamble(ar, r);
        r.U32();
        var ks = r.F64s(24);
        var frontPins = r.U32();
        if (frontPins > 1_000_000)
        {
            throw r.Fail("implausible pin count");
        }

        r.Skip(frontPins * 32L);
        var backPins = r.U32();
        if (backPins > 1_000_000)
        {
            throw r.Fail("implausible pin count");
        }

        r.Skip(backPins * 32L);
        var fflags = r.U32();
        var bflags = r.U32();
        return new FtcObj { Front = ks[..9], Back = ks[12..21], FrontProjected = (fflags & 2) != 0, BackProjected = (bflags & 2) != 0 };
    }

    private static Obj ReadCamera(Archive ar, R r)
    {
        r.Skip(137);
        r.U16();
        r.Utf16();
        r.Skip(ar.Ver >= 6 ? 33 : 10);
        return new OtherObj();
    }

    private static Obj ReadThumbnail(Archive ar, R r)
    {
        Preamble(ar, r);
        if (ar.Ver < FirstV4)
        {
            r.Skip(129);
            return new OtherObj();
        }

        ar.ReadObject(r, "CCamera");
        if (ar.Ver >= 6)
        {
            ar.ReadObject(r, "CDib");
        }

        return new OtherObj();
    }

    private static Obj ReadImage(Archive ar, R r)
    {
        Preamble(ar, r);
        var db = Drawbase(ar, r);
        var (ds, _, _) = ar.ReadObject(r);
        var xform = r.F64s(12);
        r.F64();
        r.Utf16();
        r.Skip(16);
        return new InstanceObj { Db = db, Def = ds, Xf = xform, IsImage = true };
    }

    private static int EntityRef(R r)
    {
        var tag = r.U16();
        if (tag == 0)
        {
            return -1;
        }

        if (tag == 0x7FFF)
        {
            var big = r.U32();
            if ((big & 0x80000000u) != 0)
            {
                throw r.Fail("entity reference is a new object");
            }

            return (int)Math.Min(big, int.MaxValue);
        }

        if (tag == 0xFFFF || (tag & 0x8000) != 0)
        {
            throw r.Fail("entity reference is a new object");
        }

        return tag;
    }

    private static void ConnectionPaths(Archive ar, R r)
    {
        EntityRef(r);
        for (var k = 0; k < 2; k++)
        {
            var count = r.U32();
            if (count > 1000)
            {
                throw r.Fail("implausible dimension path length");
            }

            for (var i = 0; i < count; i++)
            {
                PathRef(ar, r);
            }
        }
    }

    private static void PathRef(Archive ar, R r)
    {
        var tag = r.PeekU16();
        var isNew = tag == 0xFFFF || (tag != 0x7FFF && (tag & 0x8000) != 0);
        if (tag == 0x7FFF)
        {
            if (r.Pos + 6 > r.Data.Length)
            {
                throw r.Fail("read past the end of the stream");
            }

            isNew = (BinaryPrimitives.ReadUInt32LittleEndian(r.Data.AsSpan(r.Pos + 2)) & 0x80000000u) != 0;
        }

        if (isNew)
        {
            ar.ReadObject(r);
        }
        else
        {
            EntityRef(r);
        }
    }

    private static Obj ReadRelationship(Archive ar, R r)
    {
        Preamble(ar, r);
        EntityRef(r);
        EntityRef(r);
        return new OtherObj();
    }

    private static Obj ReadConstructionLine(Archive ar, R r)
    {
        Preamble(ar, r);
        Drawbase(ar, r);
        r.F64s(8);
        var k = ar.ClineTail;
        if (k is null)
        {
            var dflt = ar.Ver == 17 ? 7 : 4;
            int[] order = [dflt, .. new[] { 0, 4, 7 }.Where(c => c != dflt)];
            foreach (var allowNull in new[] { false, true })
            {
                foreach (var cand in order)
                {
                    if (StrictNextTag(ar, r.Data, r.Pos + cand, allowNull))
                    {
                        k = cand;
                        break;
                    }
                }

                if (k is not null)
                {
                    break;
                }
            }

            k ??= dflt;
            ar.ClineTail = k;
        }

        r.Skip(k.Value);
        return new OtherObj();
    }

    private static Obj ReadConstructionPoint(Archive ar, R r)
    {
        Preamble(ar, r);
        Drawbase(ar, r);
        r.F64s(6);
        r.U8();
        return new OtherObj();
    }

    private static Obj ReadSectionPlane(Archive ar, R r)
    {
        Preamble(ar, r);
        Drawbase(ar, r);
        if (r.Pos + 8 > r.Data.Length)
        {
            throw r.Fail("read past the end of the stream");
        }

        var first = BinaryPrimitives.ReadDoubleLittleEndian(r.Data.AsSpan(r.Pos));
        if (!(Math.Abs(first) <= 1.0001))
        {
            ar.ReadObject(r);
        }

        r.F64s(4);
        if (r.PeekIsStrMarker())
        {
            r.Utf16();
            r.Utf16();
        }

        return new OtherObj();
    }

    private static Obj ReadSkFont(Archive ar, R r)
    {
        ar.ReadObject(r, "CAttributeContainer");
        if (ar.HasPid)
        {
            var mask = r.U8();
            var n = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                {
                    n++;
                }
            }

            r.Skip(n);
        }

        r.Utf16();
        r.Skip(15);
        return new OtherObj();
    }

    private static Obj ReadDimLinear(Archive ar, R r)
    {
        Preamble(ar, r);
        Drawbase(ar, r);
        r.Utf16();
        if (ar.Ver >= 6)
        {
            ar.ReadObject(r, "CSkFont");
        }

        r.Skip(ar.Ver >= 6 ? 37 : 32);
        EntityRef(r);
        ConnectionPaths(ar, r);
        r.Skip(32);
        EntityRef(r);
        ConnectionPaths(ar, r);
        r.Skip(ar.Ver >= 6 ? 72 : 68);
        return new OtherObj();
    }

    private static Obj ReadText(Archive ar, R r)
    {
        Preamble(ar, r);
        Drawbase(ar, r);
        ar.ReadObject(r, "CSkFont");
        var p = r.Pos;
        int idx;
        while (true)
        {
            idx = FindMarker(r.Data, p, r.Pos + 512);
            if (idx < 0)
            {
                throw r.Fail("text delimiter not found");
            }

            if (idx >= 11)
            {
                var b = r.Data.AsSpan(idx - 11, 11);
                if (b[0] == 1 && b[1] == 0 && b[2] == 0 && b[3] == 0 && b[6] <= 8 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 1)
                {
                    break;
                }
            }

            p = idx + 3;
        }

        r.Skip(idx - r.Pos);
        r.Utf16();
        r.Skip(5);
        while (r.Pos + 6 <= r.Data.Length && r.Data[r.Pos] == 0xFF && r.Data[r.Pos + 1] == 0x7F)
        {
            var val = BinaryPrimitives.ReadUInt32LittleEndian(r.Data.AsSpan(r.Pos + 2));
            if ((val & 0x80000000u) != 0)
            {
                break;
            }

            r.Pos += 6;
        }

        return new OtherObj();
    }

    private static List<(int Slot, Obj? Value)> ReadEntityList(Archive ar, R r, long count, string owner)
    {
        var ents = new List<(int, Obj?)>((int)Math.Min(count, 1 << 16));
        ar.BurnStack.Add(0);
        try
        {
            while (ents.Count < count)
            {
                var p = r.Pos;
                var hasBurnCredit = owner == "def" && ar.BurnStack.Count > 0 && ar.BurnStack[^1] > 0;
                if (hasBurnCredit && p + 25 <= r.Data.Length && BinaryPrimitives.ReadUInt32LittleEndian(r.Data.AsSpan(p)) == 0 && r.Data.AsSpan(p + 22, 3).SequenceEqual(StrMarker))
                {
                    break;
                }

                try
                {
                    var (s, _, v) = ar.ReadObject(r);
                    ents.Add((s, v));
                }
                catch (WalkException)
                {
                    if (owner == "root" || hasBurnCredit)
                    {
                        r.Pos = p;
                        break;
                    }

                    throw;
                }
            }

            return ents;
        }
        finally
        {
            ar.BurnStack.RemoveAt(ar.BurnStack.Count - 1);
        }
    }

    private static bool DefinitionStartsAt(Archive ar, byte[] d, int at)
    {
        if (at >= 0 && at + 2 <= d.Length && d[at] == 255 && d[at + 1] == 255 && MatchesAscii(d, at + 6, "CComponentDefinition"))
        {
            return true;
        }

        return ar.ClassSlot.TryGetValue("CComponentDefinition", out var cs) && IsClassRef(d, at, cs);
    }

    private static Obj ReadDefinition(Archive ar, R r)
    {
        Preamble(ar, r);
        r.Skip(ar.Ver >= 17 ? 22 : ar.Ver >= 5 ? 20 : ar.Ver >= FirstV4 ? 19 : 13);
        var nlayers = r.U32();
        if (nlayers > 10_000)
        {
            throw r.Fail("implausible definition layer count");
        }

        var got = 0;
        while (got < nlayers)
        {
            if (r.PeekU16() == 0)
            {
                r.Pos += 2;
                continue;
            }

            ar.ReadObject(r, "CLayer");
            got++;
        }

        if (ar.Ver == 19 && nlayers > 0 && r.PeekU16() == 0)
        {
            r.Pos += 2;
        }

        var decl = r.U16();
        if (decl == 0x7FFF)
        {
            r.U32();
        }

        long? count = null;
        if (ar.Ver >= 20)
        {
            count = RetryCountAfterV20Filler(r, r.Pos, 5_000_000, ar);
        }

        if (count is null)
        {
            r.U32();
            count = r.U32();
        }

        if (count > 5_000_000 || count == 0)
        {
            var retry = RetryCountAfterV20Filler(r, r.Pos - 4, 5_000_000, ar);
            if (retry is not null)
            {
                count = retry;
            }
        }

        if (count > 5_000_000)
        {
            throw r.Fail("implausible definition entity count");
        }

        var def = new DefinitionObj { Ents = ReadEntityList(ar, r, count.Value, "def") };
        long nrel = r.U32();
        if (nrel > 100_000)
        {
            var retry = RetryCountAfterV20Filler(r, r.Pos - 4, 100_000, ar);
            if (retry is not null)
            {
                nrel = retry.Value;
            }
        }

        if (nrel > 100_000)
        {
            throw r.Fail("definition list misaligned");
        }

        for (var i = 0; i < nrel; i++)
        {
            ar.ReadObject(r, "CRelationship");
        }

        r.U16();
        if (!(r.Pos + 19 <= r.Data.Length && r.Data.AsSpan(r.Pos + 16, 3).SequenceEqual(StrMarker)))
        {
            for (var skip = 1; skip <= 4; skip++)
            {
                var at = r.Pos + skip;
                if (at + 19 <= r.Data.Length && r.Data.AsSpan(at + 16, 3).SequenceEqual(StrMarker))
                {
                    r.Pos = at;
                    break;
                }
            }
        }

        r.Skip(16);
        def.Name = r.Utf16();
        r.Utf16();
        r.Utf16();
        r.U32();
        var tpos = -1;
        ar.ClassSlot.TryGetValue("CThumbnail", out var thumbSlot);
        var hasThumbSlot = ar.ClassSlot.ContainsKey("CThumbnail");
        for (var off = 0; off < 96; off++)
        {
            var p = r.Pos + off;
            if (At(r.Data, p) == 255 && At(r.Data, p + 1) == 255 && At(r.Data, p + 4) == 10 && At(r.Data, p + 5) == 0 && MatchesAscii(r.Data, p + 6, "CThumbnail"))
            {
                tpos = p;
                break;
            }

            if (hasThumbSlot && IsClassRef(r.Data, p, thumbSlot))
            {
                tpos = p;
                break;
            }
        }

        if (tpos < 0)
        {
            throw r.Fail("definition tail: thumbnail not found");
        }

        var gap = r.Raw(tpos - r.Pos);
        def.IsGroup = gap.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(gap.AsSpan(gap.Length - 4)) == 1;
        ar.ReadObject(r, "CThumbnail");
        return def;
    }

    private static Obj ReadInstance(Archive ar, R r)
    {
        Preamble(ar, r);
        var db = Drawbase(ar, r);
        var (ds, dn, _) = ar.ReadObject(r, "CComponentDefinition");
        if (dn != "CComponentDefinition")
        {
            throw r.Fail("instance definition reference is " + dn);
        }

        var xf = r.F64s(13);
        var name = ar.Ver >= FirstNamedInstance || r.PeekIsStrMarker() ? r.Utf16() : "";
        if (ar.Ver >= 14)
        {
            r.Skip(16);
        }

        return new InstanceObj { Db = db, Def = ds, Xf = xf, Name = name };
    }

    // ---------------------------------------------------------------- the model section

    private static readonly int[] MaterialPattern = [255, 255, -1, -1, 9, 0, .. "CMaterial"u8.ToArray().Select(b => (int)b)];
    private static readonly int[] LayerPattern = [255, 255, -1, -1, 6, 0, .. "CLayer"u8.ToArray().Select(b => (int)b)];

    private static int? FindVersionMajor(byte[] data)
    {
        // The first 96 bytes with the zero bytes of the UTF-16 text removed: look for "{<digits>.".
        var text = new StringBuilder();
        for (var i = 0; i < Math.Min(96, data.Length); i++)
        {
            if (data[i] != 0)
            {
                text.Append((char)data[i]);
            }
        }

        var s = text.ToString();
        var open = s.IndexOf('{', StringComparison.Ordinal);
        while (open >= 0)
        {
            var end = open + 1;
            while (end < s.Length && char.IsAsciiDigit(s[end]))
            {
                end++;
            }

            if (end > open + 1 && end < s.Length && s[end] == '.' && int.TryParse(s.AsSpan(open + 1, end - open - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major))
            {
                return major;
            }

            open = s.IndexOf('{', open + 1);
        }

        return null;
    }

    private static Archive Probe(byte[] data, int ver, int start, CancellationToken token)
    {
        var boot = new Archive(data, ver, token) { NextSlot = 1 << 20, WalkBase = 1 << 20 };
        boot.R.Pos = start;
        return boot;
    }

    private static int BootstrapTwoMaterials(byte[] data, int ver, int matHdr, CancellationToken token)
    {
        var boot = Probe(data, ver, matHdr, token);
        boot.ReadObject(boot.R, "CMaterial");
        var tag = boot.R.PeekU16();
        if (tag == 0xFFFF || (tag & 0x8000) == 0)
        {
            throw boot.R.Fail("cannot bootstrap the slot base");
        }

        return tag & 0x7FFF;
    }

    private static List<int> ProbeLayerAnchorBases(byte[] data, int ver, int start, int matCount, CancellationToken token)
    {
        var boot = Probe(data, ver, start, token);
        const int b0 = 1 << 20;
        for (var i = 0; i < matCount; i++)
        {
            boot.ReadObject(boot.R, "CMaterial");
        }

        boot.R.U32();
        if (ver >= 17)
        {
            boot.R.U8();
        }

        long layerCount = 0;
        if (ver >= FirstV4)
        {
            layerCount = boot.R.U32();
            if (!(layerCount >= 1 && layerCount <= 100_000))
            {
                throw boot.R.Fail("implausible layer count in the base probe");
            }
        }

        var layerSlots = new List<int>();
        for (var i = 0; i < layerCount; i++)
        {
            layerSlots.Add(boot.ReadObject(boot.R, "CLayer").Slot);
        }

        boot.CollectTrailingLayers(layerSlots, null, true);
        var (s, n, _) = boot.ReadObject(boot.R);
        if (n != "premodel")
        {
            throw boot.R.Fail("base probe: the anchor resolved to " + n);
        }

        return layerSlots.Select(rel => s - (rel - b0)).Where(c => c > 0 && c < b0).ToList();
    }

    private sealed class Walked
    {
        public Archive Ar = null!;
        public List<(int Slot, Obj? Value)> Root = new();
        public List<(int Slot, LayerObj Layer)> Layers = new();
        public List<(int Slot, MaterialObj Material)> Materials = new();
    }

    private static Walked Walk(byte[] data, CancellationToken token)
    {
        var ver = FindVersionMajor(data) ?? throw new ModelFormatException("The SketchUp stream has no version string.");
        var matHdr = FindPattern(data, MaterialPattern, 0, data.Length, token);
        int start, matCount;
        if (matHdr >= 4)
        {
            start = matHdr;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(matHdr - 4));
            if (count > 100_000)
            {
                throw new ModelFormatException("The SketchUp stream has an implausible material count.");
            }

            matCount = (int)count;
        }
        else
        {
            var layerHdr = FindPattern(data, LayerPattern, 0, data.Length, token);
            if (layerHdr < 0)
            {
                throw new ModelFormatException("The SketchUp stream has no material or layer class record.");
            }

            matCount = 0;
            start = layerHdr - (ver >= 17 ? 9 : 8);
        }

        var bases = matCount >= 2 ? [BootstrapTwoMaterials(data, ver, start, token)] : ProbeLayerAnchorBases(data, ver, start, matCount, token);
        WalkException? best = null;
        foreach (var b in bases)
        {
            try
            {
                return WalkModel(data, ver, start, matCount, b, token);
            }
            catch (WalkException e)
            {
                if (best is null || e.At > best.At)
                {
                    best = e;
                }
            }
        }

        throw best ?? new ModelFormatException("No slot base of the SketchUp stream could be found.");
    }

    private static Walked WalkModel(byte[] data, int ver, int start, int matCount, int slotBase, CancellationToken token)
    {
        var ar = new Archive(data, ver, token) { NextSlot = slotBase, WalkBase = slotBase };
        var r = ar.R;
        r.Pos = start;
        var walked = new Walked { Ar = ar };
        for (var i = 0; i < matCount; i++)
        {
            var (s, _, v) = ar.ReadObject(r, "CMaterial");
            if (v is MaterialObj m)
            {
                walked.Materials.Add((s, m));
            }
        }

        r.U32();
        if (ver >= 17)
        {
            r.U8();
        }

        long layerCount = ver < FirstV4 ? long.MaxValue : r.U32();
        if (layerCount > 100_000 && ver >= FirstV4)
        {
            throw r.Fail("implausible layer count");
        }

        var layerTick = 0L;
        while (walked.Layers.Count < layerCount)
        {
            Cancel.Every(token, layerTick++);
            var tag = r.PeekU16();
            if (tag == 0)
            {
                r.Pos += 2;
                continue;
            }

            if (tag != 0xFFFF && (tag & 0x8000) == 0)
            {
                break;
            }

            var (s, _, v) = ar.ReadObject(r, "CLayer");
            if (v is LayerObj layer)
            {
                walked.Layers.Add((s, layer));
            }
        }

        ar.CollectTrailingLayers(null, walked.Layers, false);
        var (_, dn, _) = ar.ReadObject(r);
        if (dn != "CLayer")
        {
            throw r.Fail("definition-list anchor is " + dn + ", not a layer");
        }

        long defCount = r.U32();
        if (defCount > 1_000_000)
        {
            var retry = RetryCountAfterV20Filler(r, r.Pos - 4, 1_000_000, ar);
            if (retry is not null)
            {
                defCount = retry.Value;
                if (!DefinitionStartsAt(ar, r.Data, r.Pos))
                {
                    defCount = 0;
                    r.Pos -= 4;
                }
            }
        }

        if (defCount > 1_000_000)
        {
            throw r.Fail("implausible definition count");
        }

        for (var i = 0; i < defCount; i++)
        {
            ar.ReadObject(r, "CComponentDefinition");
        }

        ar.ClassSlot.TryGetValue("CComponentDefinition", out var defClass);
        var hasDefClass = ar.ClassSlot.ContainsKey("CComponentDefinition");
        while (true)
        {
            Cancel.Every(token, defCount++);
            var tag = r.PeekU16();
            var isDef = hasDefClass && tag == (0x8000 | defClass);
            if (!isDef && tag == 0xFFFF && MatchesAscii(r.Data, r.Pos + 6, "CComponentDefinition"))
            {
                isDef = true;
            }

            if (!isDef)
            {
                break;
            }

            ar.ReadObject(r);
        }

        long rootCount = r.U32();
        if (rootCount > 5_000_000)
        {
            var retry = RetryCountAfterV20Filler(r, r.Pos - 4, 5_000_000, ar);
            if (retry is not null)
            {
                rootCount = retry.Value;
            }
        }

        if (rootCount > 5_000_000)
        {
            throw r.Fail("implausible root entity count");
        }

        walked.Root = ReadEntityList(ar, r, rootCount, "root");
        return walked;
    }

    // ---------------------------------------------------------------- scene

    public static Model3D Read(ReadOnlyMemory<byte> memory, ModelMetadata meta, LoadOptions options)
    {
        var token = options.Cancellation;
        if (memory.Length > (1 << 29))
        {
            throw new ModelFormatException("The SketchUp stream is too large.");
        }

        var data = memory.ToArray();
        Walked walked;
        try
        {
            walked = Walk(data, token);
        }
        catch (WalkException e)
        {
            throw new UnsupportedModelFormatException(ModelFormats.SketchUp, "The SketchUp object stream could not be walked (" + e.Message + "); " + meta.Application + " files of this layout are not all covered.");
        }

        var ar = walked.Ar;
        var model = new Model3D();
        model.Metadata.Format = ModelFormats.SketchUp;
        model.Metadata.DocumentKind = "model";
        model.Metadata.Units = LengthUnit.Inch;
        model.Metadata.UpAxis = UpAxis.Z;
        model.Metadata.FormatVersion = meta.FormatVersion;
        model.Metadata.Application = meta.Application;
        model.Metadata.HasPreview = meta.HasPreview;
        foreach (var p in meta.Properties)
        {
            model.Metadata.Properties.Add(p);
        }

        var materials = new Dictionary<string, SkpMaterial>(StringComparer.Ordinal);
        var materialNames = new Dictionary<long, string>();
        foreach (var (slot, m) in walked.Materials)
        {
            var alpha = m.UseOpacity ? (float)Math.Clamp(m.Opacity, 0, 1) : 1f;
            materials[m.Name] = new SkpMaterial(m.Name, new Vector4(m.Rgba[0] / 255f, m.Rgba[1] / 255f, m.Rgba[2] / 255f, alpha), m.Textured);
            materialNames[slot] = m.Name;
        }

        var layers = new Dictionary<long, (string, bool)>();
        foreach (var (slot, l) in walked.Layers)
        {
            layers[slot] = (l.Name, l.Hidden != 0);
        }

        var definitions = new Dictionary<long, Definition>();
        var tick = 0L;
        foreach (var (slot, entry) in ar.Slots)
        {
            Cancel.Every(token, tick++);
            if (!entry.IsClass && entry.Value is DefinitionObj d)
            {
                var def = new Definition { Name = d.Name, IsGroup = d.IsGroup };
                Fill(def, d.Ents, ar.Slots, token);
                definitions[slot] = def;
            }
        }

        var root = new Definition { Name = "Model" };
        Fill(root, walked.Root, ar.Slots, token);

        return SketchUpVff.Assemble(model, definitions, root, materials, materialNames, layers, options,
            "Geometry decoded from the object stream of " + meta.Application + " (vertices, edges, faces with holes, groups and components with transforms, material colours, tags). Textures, UV mapping, edge display, section planes, text and dimensions are not read. Lengths are inches.");
    }

    private static void AddEdge(Definition def, int slot, EdgeObj e, Dictionary<int, Slot> slots)
    {
        if (def.Edges.ContainsKey(slot))
        {
            return;
        }

        foreach (var vs in new[] { e.V1, e.V2 })
        {
            if (vs >= 0 && !def.Vertices.ContainsKey(vs) && slots.TryGetValue(vs, out var ent) && ent.Value is VertexObj v)
            {
                def.Vertices[vs] = new Vector3((float)v.X, (float)v.Y, (float)v.Z);
            }
        }

        def.Edges[slot] = (e.V1, e.V2);
    }

    private static void Fill(Definition def, List<(int Slot, Obj? Value)> ents, Dictionary<int, Slot> slots, CancellationToken token)
    {
        var tick = 0L;
        foreach (var (s, v) in ents)
        {
            Cancel.Every(token, tick++);
            switch (v)
            {
                case EdgeObj edge:
                    AddEdge(def, s, edge, slots);
                    break;
                case FaceObj f:
                {
                    var face = new Face { Nx = f.Nx, Ny = f.Ny, Nz = f.Nz, Material = f.Db.Mat > 0 ? f.Db.Mat : -1, Hidden = f.Db.Hidden != 0 };
                    foreach (var loop in f.Loops)
                    {
                        var list = new List<(long, bool)>(loop.Uses.Count);
                        foreach (var use in loop.Uses)
                        {
                            Cancel.Every(token, tick++);
                            if (!slots.TryGetValue(use.Edge, out var ent) || ent.Value is not EdgeObj eo)
                            {
                                continue;
                            }

                            AddEdge(def, use.Edge, eo, slots);
                            list.Add((use.Edge, use.Sense == 0));
                        }

                        face.Loops.Add(list.ToArray());
                    }

                    def.Faces[s] = face;
                    break;
                }

                case InstanceObj i:
                {
                    var inst = new Instance
                    {
                        Definition = i.Def,
                        Name = i.Name,
                        Material = i.Db.Mat > 0 ? i.Db.Mat : -1,
                        Layer = i.Db.Layer > 0 ? i.Db.Layer : -1,
                        Hidden = i.Db.Hidden != 0,
                        Transform = SketchUpVff.MatrixFrom13(i.Xf),
                    };
                    def.Instances.Add(inst);
                    break;
                }
            }
        }
    }
}
