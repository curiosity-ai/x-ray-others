using System.Globalization;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Parasolid;

/// <summary>
/// The node stream of a transmit file, text or binary: for each node its type, the length of a variable length node, its index and its fields; the stream
/// ends with node type 1 and index 0. The primitives of the two encodings are in <see cref="XtTextStream"/> and <see cref="XtBinaryStream"/>; reading the
/// nodes and the schema section that an embedded-schema file carries is the same for both.
/// </summary>
internal abstract class XtStream
{
    protected XtStream(CancellationToken cancel) => Cancel = cancel;

    protected CancellationToken Cancel { get; }

    /// <summary>The number of characters or bytes left.</summary>
    public abstract int Remaining { get; }

    /// <summary>The text with its line breaks removed (the writer wraps lines inside tokens).</summary>
    public static string Unwrap(string text)
    {
        if (text.IndexOfAny(['\r', '\n']) < 0)
        {
            return text;
        }

        var buffer = new char[text.Length];
        var n = 0;
        foreach (var c in text)
        {
            if (c is not ('\r' or '\n'))
            {
                buffer[n++] = c;
            }
        }

        return new string(buffer, 0, n);
    }

    /// <summary>
    /// Reads the node stream. <paramref name="layout"/> says how a node of each type is laid out: for a plain schema the effective layouts, for an embedded
    /// schema the base layouts that the file's per-type edits (first use of a type: a count, then copy/delete/insert/append operations, or a full
    /// declaration for a type the base does not have) are applied to.
    /// </summary>
    public static Dictionary<int, XtNode> Parse(XtStream reader, int userFieldSize, XtLayout layout, int maxType, long maxNodes, CancellationToken cancel, out int dangling)
    {
        var nodes = new Dictionary<int, XtNode>();
        var effective = layout.Embedded ? new Dictionary<int, XtNodeDef>() : null;
        var tick = 0L;
        while (true)
        {
            XRay.ThreeD.Internal.Cancel.Every(cancel, tick++);
            var type = reader.NodeType();
            if (type == 1)
            {
                // The terminator: type 1, index 0.
                _ = reader.NodeIndex();
                dangling = CountDangling(nodes);
                return nodes;
            }

            if (type < 1 || (maxType > 0 && type > maxType))
            {
                throw new ModelFormatException("A Parasolid node has an impossible type.");
            }

            XtNodeDef? def;
            if (effective is not null)
            {
                if (!effective.TryGetValue(type, out def))
                {
                    if (effective.Count >= 4096)
                    {
                        throw new ModelFormatException("A Parasolid file declares too many node types.");
                    }

                    def = reader.ReadEmbeddedSchema(type, layout.Types.GetValueOrDefault(type));
                    effective[type] = def;
                }
            }
            else
            {
                def = layout.Types.GetValueOrDefault(type) ?? throw new UnsupportedModelFormatException(ModelFormats.Parasolid, $"Parasolid node type {type} is not in the field table of this schema.");
            }

            var variable = def.Variable;
            var count = 0;
            if (variable is not null)
            {
                count = reader.VarLength();
                if (count < 0 || count > reader.Remaining)
                {
                    throw new ModelFormatException("A Parasolid node has an impossible length.");
                }
            }

            var index = reader.NodeIndex();
            if (index <= 0 || nodes.Count >= maxNodes)
            {
                throw new ModelFormatException("A Parasolid node has an impossible index or the file has too many nodes.");
            }

            var ints = def.IntCount > 0 ? new long[def.IntCount] : [];
            var doubles = def.DoubleCount > 0 ? new double[def.DoubleCount] : [];
            var node = new XtNode { Def = def, Index = index, Ints = ints, Doubles = doubles };
            foreach (var field in def.Fields)
            {
                if (def.Type == 80 && field.Name == "field_names" && !layout.Embedded && reader.NextIsLogical())
                {
                    // Attribute definitions of Parasolid 10 and the format reference's own example have no field names pointer.
                    continue;
                }

                if (field.Count == 0)
                {
                    node.Variable = field.Transmitted ? reader.ReadVariable(field, count) : null;
                }
                else if (field.Count < 0)
                {
                    // A run of logicals: the allowed owners of an attribute definition (13 for Parasolid 10, 14 later, 16 in schema 35102).
                    reader.SkipRun(layout.OwnerFlags);
                }
                else
                {
                    for (var k = 0; k < field.Count; k++)
                    {
                        reader.ReadScalar(field, ints, doubles, field.Slot + (field.IsDouble ? k * field.Width : k));
                    }
                }
            }

            for (var u = 0; u < userFieldSize && def.Visible; u++)
            {
                _ = reader.UserWord();
            }

            nodes[index] = node;
        }
    }

    /// <summary>How many pointer values refer to a node that is not in the file: zero for a layout that fits.</summary>
    private static int CountDangling(Dictionary<int, XtNode> nodes)
    {
        var bad = 0;
        foreach (var node in nodes.Values)
        {
            foreach (var f in node.Def.Fields)
            {
                if (f.Type != 'p')
                {
                    continue;
                }

                if (f.Count > 0)
                {
                    for (var k = 0; k < f.Count; k++)
                    {
                        var v = node.Ints[f.Slot + k];
                        if (v != 0 && !nodes.ContainsKey((int)v))
                        {
                            bad++;
                        }
                    }
                }
                else if (f.Count == 0 && node.Variable is long[] list)
                {
                    foreach (var v in list)
                    {
                        if (v != 0 && !nodes.ContainsKey((int)v))
                        {
                            bad++;
                        }
                    }
                }
            }
        }

        return bad;
    }

    // ---- embedded schema ---------------------------------------------------------------------------------------

    /// <summary>
    /// The description of a node type at its first use in a file that embeds its schema. A type of the base schema carries 255 (unchanged) or the number
    /// of fields of the edited layout followed by the edit operations (C copy the next base field, D drop it, I insert a field before it, A append a
    /// field once the base is used up, Z end: the base fields not reached are dropped); a type the base lacks carries its complete declaration.
    /// </summary>
    private XtNodeDef ReadEmbeddedSchema(int type, XtNodeDef? baseDef)
    {
        var marker = Marker();
        if (marker < 0 || marker > 255)
        {
            throw new ModelFormatException("A Parasolid embedded schema has an impossible field count.");
        }

        if (baseDef is not null && marker == 255)
        {
            return baseDef;
        }

        var fields = new List<XtField>(Math.Min(marker, 64));
        if (baseDef is null)
        {
            var name = SchemaString();
            _ = SchemaString();
            for (var i = 0; i < marker; i++)
            {
                Cancel.ThrowIfCancellationRequested();
                fields.Add(ReadSchemaField());
            }

            ValidateVariable(fields);
            return XtSchema.Create(type, name, fields);
        }

        var next = 0;
        var appending = false;
        for (var guard = 0; ; guard++)
        {
            XRay.ThreeD.Internal.Cancel.Every(Cancel, guard);
            if (guard > 4096)
            {
                throw new ModelFormatException("A Parasolid embedded schema has too many edits.");
            }

            var op = Op();
            switch (op)
            {
                case 'C' when !appending && next < baseDef.Fields.Length:
                    fields.Add(baseDef.Fields[next++]);
                    break;
                case 'D' when !appending && next < baseDef.Fields.Length:
                    next++;
                    break;
                case 'I' when !appending && next < baseDef.Fields.Length:
                    fields.Add(ReadSchemaField());
                    break;
                case 'A' when next >= baseDef.Fields.Length:
                    appending = true;
                    fields.Add(ReadSchemaField());
                    break;
                case 'Z':
                    if (fields.Count != marker)
                    {
                        throw new ModelFormatException("A Parasolid embedded schema has a wrong field count.");
                    }

                    ValidateVariable(fields);
                    return XtSchema.Create(type, baseDef.Name, fields, baseDef.Visible);
                default:
                    throw new ModelFormatException("A Parasolid embedded schema has an invalid edit.");
            }

            if (fields.Count > marker)
            {
                throw new ModelFormatException("A Parasolid embedded schema has a wrong field count.");
            }
        }
    }

    private static void ValidateVariable(List<XtField> fields)
    {
        for (var i = 0; i + 1 < fields.Count; i++)
        {
            if (fields[i].Count == 0)
            {
                throw new ModelFormatException("A Parasolid embedded schema has a variable length field that is not last.");
            }
        }
    }

    private XtField ReadSchemaField()
    {
        var name = SchemaString();
        var pointerClass = SchemaClass();
        var elements = SchemaElements();
        if (name.Length == 0 || pointerClass < 0 || elements < 0 || elements > 1 << 20)
        {
            throw new ModelFormatException("A Parasolid embedded schema has an invalid field.");
        }

        var letter = 'p';
        if (pointerClass == 0)
        {
            var code = SchemaString();
            if (code.Length != 1 || "ucl nwdtfivbh".IndexOf(code[0], StringComparison.Ordinal) < 0 || code[0] == ' ')
            {
                throw new ModelFormatException("A Parasolid embedded schema has an unsupported field type.");
            }

            letter = code[0] == 't' ? 'd' : code[0];
        }

        var transmitted = true;
        if (elements == 1)
        {
            transmitted = SchemaTransmitted();
        }

        // 0 stands for a scalar, 1 for a variable length field, more for a fixed array.
        return new XtField { Name = name, Type = letter, Count = elements == 0 ? 1 : elements == 1 ? 0 : elements, Slot = -1, Transmitted = transmitted };
    }


    // ---- the primitives of an encoding ---------------------------------------------------------------------------

    protected abstract int NodeType();

    protected abstract int NodeIndex();

    protected abstract int VarLength();

    protected abstract int UserWord();

    /// <summary>A run of logicals of unknown length in the text; <paramref name="count"/> of them in binary.</summary>
    protected abstract void SkipRun(int count);

    /// <summary>Whether the next value is a logical, which an attribute definition without a field names pointer has where the pointer should be (text only).</summary>
    protected abstract bool NextIsLogical();

    protected abstract void ReadScalar(XtField field, long[] ints, double[] doubles, int slot);

    protected abstract object ReadVariable(XtField field, int count);

    /// <summary>The field count (0 to 255) at the start of an embedded schema section.</summary>
    protected abstract int Marker();

    /// <summary>The next edit operation: C, D, I, A or Z.</summary>
    protected abstract char Op();

    protected abstract string SchemaString();

    protected abstract int SchemaClass();

    protected abstract int SchemaElements();

    protected abstract bool SchemaTransmitted();
}

/// <summary>
/// The text encoding: whitespace separated numbers for integers, pointers and doubles (an unset double or vector is one '?'), single characters for
/// characters and logicals (T and F, no separator). The text is wrapped at a fixed column inside tokens, so all line breaks are removed before reading.
/// </summary>
internal sealed class XtTextStream : XtStream
{
    private readonly string _text;
    private int _p;

    public XtTextStream(string text, int start, CancellationToken cancel)
        : base(cancel)
    {
        _text = text;
        _p = start;
    }

    public override int Remaining => _text.Length - _p;

    protected override int NodeType() => Int();

    protected override int NodeIndex() => Int();

    protected override int VarLength() => Int();

    protected override int UserWord() => Int();

    protected override int Marker() => Int();

    protected override int SchemaClass() => Int();

    protected override int SchemaElements() => Int();

    protected override bool SchemaTransmitted() => RawChar() == 'T';

    protected override char Op() => RawChar();

    protected override string SchemaString() => Str();

    protected override void SkipRun(int count)
    {
        SkipSpace();
        var run = 0;
        while (_p < _text.Length && _text[_p] is 'T' or 'F' && run < 64)
        {
            _p++;
            run++;
        }
    }

    /// <summary>A string: its length, one separating space, then that many characters (escapes counted as the characters they stand for).</summary>
    private string Str()
    {
        var length = Int();
        if (length < 0 || length > 65536)
        {
            throw new ModelFormatException("A Parasolid string has an impossible length.");
        }

        return ReadChars(length);
    }

    private char RawChar()
    {
        SkipSpace();
        if (_p >= _text.Length)
        {
            throw new ModelFormatException("The Parasolid node stream ends unexpectedly.");
        }

        return _text[_p++];
    }

    /// <summary>Reads characters of a character array: after the separating space, with the escapes \0 \n \r \\ and \9 (nine spaces).</summary>
    private string ReadChars(int count)
    {
        if (_p < _text.Length && _text[_p] == ' ')
        {
            _p++;
        }

        if (count > _text.Length - _p)
        {
            throw new ModelFormatException("A Parasolid string runs past the end of the file.");
        }

        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            if (_p >= _text.Length)
            {
                throw new ModelFormatException("A Parasolid string runs past the end of the file.");
            }

            var c = _text[_p++];
            if (c != '\\' || _p >= _text.Length)
            {
                chars[i] = c;
                continue;
            }

            var e = _text[_p++];
            switch (e)
            {
                case '0':
                    chars[i] = '\0';
                    break;
                case 'n':
                    chars[i] = '\r';
                    break;
                case 'r':
                    chars[i] = '\n';
                    break;
                case '\\':
                    chars[i] = '\\';
                    break;
                case '9':
                {
                    // Nine spaces stand for themselves in one escape.
                    for (var k = 0; k < 9 && i < count; k++, i++)
                    {
                        chars[i] = ' ';
                    }

                    i--;
                    break;
                }

                default:
                    throw new ModelFormatException("A Parasolid string has an invalid escape.");
            }
        }

        return new string(chars);
    }

    protected override bool NextIsLogical()
    {
        SkipSpace();
        return _p < _text.Length && _text[_p] is 'T' or 'F';
    }

    private void SkipSpace()
    {
        while (_p < _text.Length && _text[_p] is ' ' or '\t')
        {
            _p++;
        }
    }

    private string Token()
    {
        SkipSpace();
        var start = _p;
        while (_p < _text.Length && _text[_p] is not (' ' or '\t'))
        {
            _p++;
        }

        if (_p == start)
        {
            throw new ModelFormatException("The Parasolid node stream ends unexpectedly.");
        }

        return _text[start.._p];
    }

    private int Int()
    {
        return long.TryParse(Token(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v is >= int.MinValue and <= int.MaxValue
            ? (int)v
            : throw new ModelFormatException("A Parasolid integer is malformed.");
    }

    private double Double()
    {
        SkipSpace();
        if (_p < _text.Length && _text[_p] == '?')
        {
            _p++;
            return double.NaN;
        }

        return double.TryParse(Token(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new ModelFormatException("A Parasolid number is malformed.");
    }

    private char Char()
    {
        SkipSpace();
        if (_p >= _text.Length)
        {
            throw new ModelFormatException("The Parasolid node stream ends unexpectedly.");
        }

        return _text[_p++];
    }

    protected override void ReadScalar(XtField field, long[] ints, double[] doubles, int slot)
    {
        switch (field.Type)
        {
            case 'c' or 'l':
                ints[slot] = Char();
                break;
            case 'f':
                doubles[slot] = Double();
                break;
            case 'v' or 'i' or 'b' or 'h':
                SkipSpace();
                if (_p < _text.Length && _text[_p] == '?')
                {
                    // A null vector is a single '?'.
                    _p++;
                    for (var k = 0; k < field.Width; k++)
                    {
                        doubles[slot + k] = double.NaN;
                    }
                }
                else
                {
                    for (var k = 0; k < field.Width; k++)
                    {
                        doubles[slot + k] = Double();
                    }
                }

                break;
            default:
                ints[slot] = IntOrNull();
                break;
        }
    }

    /// <summary>An integer, where a single '?' is the unset value (read as zero).</summary>
    private int IntOrNull()
    {
        SkipSpace();
        if (_p < _text.Length && _text[_p] == '?')
        {
            _p++;
            return 0;
        }

        return Int();
    }

    protected override object ReadVariable(XtField field, int count)
    {
        if (field.Type == 'c')
        {
            return ReadChars(count);
        }

        if (field.IsDouble)
        {
            var width = field.Width;
            var values = new double[(long)count * width];
            for (var i = 0; i < values.Length; i += width)
            {
                XRay.ThreeD.Internal.Cancel.Every(Cancel, i);
                if (width > 1)
                {
                    SkipSpace();
                    if (_p < _text.Length && _text[_p] == '?')
                    {
                        _p++;
                        values.AsSpan(i, width).Fill(double.NaN);
                        continue;
                    }
                }

                for (var k = 0; k < width; k++)
                {
                    values[i + k] = Double();
                }
            }

            return values;
        }

        var longs = new long[count];
        for (var i = 0; i < count; i++)
        {
            XRay.ThreeD.Internal.Cancel.Every(Cancel, i);
            longs[i] = field.Type is 'l' ? Char() : IntOrNull();
        }

        return longs;
    }
}

/// <summary>
/// The binary (neutral) encoding: big-endian numbers. A node type is a 16 bit integer, a variable length and a user word 32 bit integers, a node index and a
/// pointer a "positive integer" (one positive short holding the value plus one, or, from 32767 on, a negative remainder short and a positive quotient short),
/// an integer or short an integer with a reserved null value, a double an IEEE binary64 with a reserved null value, a character or logical or byte one byte.
/// </summary>
internal sealed class XtBinaryStream : XtStream
{
    private static readonly byte[] DoubleNull = [0xc2, 0xbc, 0x92, 0x8f, 0x99, 0x6e, 0x00, 0x00];

    private readonly byte[] _data;
    private int _p;

    public XtBinaryStream(byte[] data, int start, CancellationToken cancel)
        : base(cancel)
    {
        _data = data;
        _p = start;
    }

    public override int Remaining => _data.Length - _p;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || n > _data.Length - _p)
        {
            throw new ModelFormatException("The Parasolid node stream ends unexpectedly.");
        }

        var span = _data.AsSpan(_p, n);
        _p += n;
        return span;
    }

    private int U8() => Take(1)[0];

    private int I16() => System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(Take(2));

    private int U16() => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    private int I32() => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(Take(4));

    private int Positive()
    {
        var remainder = I16();
        if (remainder > 0)
        {
            return remainder - 1;
        }

        if (remainder is 0 or short.MinValue)
        {
            throw new ModelFormatException("A Parasolid positive integer is malformed.");
        }

        var quotient = I16();
        if (quotient <= 0)
        {
            throw new ModelFormatException("A Parasolid positive integer is malformed.");
        }

        return (int)Math.Min(int.MaxValue, (long)quotient * 32767 + -remainder - 1);
    }

    private int NullableShort()
    {
        var v = I16();
        return v == unchecked((short)0x8004) ? 0 : v;
    }

    private int NullableInt()
    {
        var span = Take(4);
        return span[0] == 0xff && span[1] == 0xff && span[2] == 0x80 && span[3] == 0x04 ? 0 : System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(span);
    }

    private double Double()
    {
        var span = Take(8);
        return span.SequenceEqual(DoubleNull) ? double.NaN : System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(span);
    }

    protected override int NodeType() => I16();

    protected override int NodeIndex() => Positive();

    protected override int VarLength() => I32();

    protected override int UserWord() => NullableInt();

    protected override int Marker() => U8();

    protected override char Op() => (char)U8();

    protected override string SchemaString()
    {
        var length = U8();
        return System.Text.Encoding.Latin1.GetString(Take(length));
    }

    protected override int SchemaClass() => U16();

    protected override int SchemaElements() => Positive();

    protected override bool SchemaTransmitted() => U8() != 0;

    protected override void SkipRun(int count) => _ = Take(count);

    protected override bool NextIsLogical() => false;

    private long Scalar(char type) => type switch
    {
        'c' or 'u' => U8(),
        'l' => U8() != 0 ? 'T' : 'F',
        'n' => NullableShort(),
        'w' => U16(),
        'p' => Positive(),
        _ => NullableInt(),
    };

    protected override void ReadScalar(XtField field, long[] ints, double[] doubles, int slot)
    {
        if (field.IsDouble)
        {
            for (var k = 0; k < field.Width; k++)
            {
                doubles[slot + k] = Double();
            }
        }
        else
        {
            ints[slot] = Scalar(field.Type);
        }
    }

    protected override object ReadVariable(XtField field, int count)
    {
        if (field.Type == 'c')
        {
            return System.Text.Encoding.Latin1.GetString(Take(count));
        }

        if (field.IsDouble)
        {
            var width = field.Width;
            if ((long)count * width * 8 > Remaining)
            {
                throw new ModelFormatException("The Parasolid node stream ends unexpectedly.");
            }

            var values = new double[(long)count * width];
            for (var i = 0; i < values.Length; i++)
            {
                XRay.ThreeD.Internal.Cancel.Every(Cancel, i);
                values[i] = Double();
            }

            return values;
        }

        var longs = new long[count];
        for (var i = 0; i < count; i++)
        {
            XRay.ThreeD.Internal.Cancel.Every(Cancel, i);
            longs[i] = Scalar(field.Type);
        }

        return longs;
    }
}
