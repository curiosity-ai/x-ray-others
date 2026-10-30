using System.Globalization;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Parasolid;

/// <summary>
/// The node stream of a text transmit file: whitespace separated numbers for integers, pointers and doubles (an unset double or vector is one '?'),
/// single characters for characters and logicals (T and F, no separator), then for each node its type, the length of a variable length node, its index
/// and its fields. The text is wrapped at a fixed column inside tokens, so all line breaks are removed before reading. The stream ends with node
/// type 1 and index 0.
/// </summary>
internal sealed class XtStream
{
    private readonly string _text;
    private readonly CancellationToken _cancel;
    private int _p;

    private XtStream(string text, int start, CancellationToken cancel)
    {
        _text = text;
        _p = start;
        _cancel = cancel;
    }

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

    public static Dictionary<int, XtNode> Parse(string text, int start, int userFieldSize, bool legacy, long maxNodes, CancellationToken cancel)
    {
        var reader = new XtStream(text, start, cancel);
        var nodes = new Dictionary<int, XtNode>();
        var tick = 0L;
        while (true)
        {
            Cancel.Every(cancel, tick++);
            var type = reader.Int();
            if (type == 1)
            {
                // The terminator: type 1, index 0.
                _ = reader.Int();
                return nodes;
            }

            var def = XtSchema.Get(type, legacy) ?? throw new UnsupportedModelFormatException(ModelFormats.Parasolid, $"Parasolid node type {type} is not in the field table of this schema.");
            var variable = def.Variable;
            var count = 0;
            if (variable is not null)
            {
                count = reader.Int();
                if (count < 0 || count > text.Length - reader._p)
                {
                    throw new ModelFormatException("A Parasolid node has an impossible length.");
                }
            }

            var index = reader.Int();
            if (index <= 0 || nodes.Count >= maxNodes)
            {
                throw new ModelFormatException("A Parasolid node has an impossible index or the file has too many nodes.");
            }

            var ints = def.IntCount > 0 ? new long[def.IntCount] : [];
            var doubles = def.DoubleCount > 0 ? new double[def.DoubleCount] : [];
            var node = new XtNode { Def = def, Index = index, Ints = ints, Doubles = doubles };
            foreach (var field in def.Fields)
            {
                if (def.Type == 80 && field.Name == "field_names" && reader.NextIsLogical())
                {
                    // Attribute definitions of Parasolid 10 and the format reference's own example have no field names pointer.
                    continue;
                }

                if (field.Count == 0)
                {
                    node.Variable = reader.ReadVariable(field, count);
                }
                else if (field.Count < 0)
                {
                    // A run of T and F: the allowed owners of an attribute definition (13 for Parasolid 10, 14 later).
                    reader.SkipSpace();
                    var run = 0;
                    while (reader._p < reader._text.Length && reader._text[reader._p] is 'T' or 'F' && run < 64)
                    {
                        reader._p++;
                        run++;
                    }
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
                _ = reader.Int();
            }

            nodes[index] = node;
        }
    }

    private bool NextIsLogical()
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

    private int Int() => long.TryParse(Token(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v is >= int.MinValue and <= int.MaxValue
        ? (int)v
        : throw new ModelFormatException("A Parasolid integer is malformed.");

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

    private void ReadScalar(XtField field, long[] ints, double[] doubles, int slot)
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
                ints[slot] = Int();
                break;
        }
    }

    private object ReadVariable(XtField field, int count)
    {
        if (field.Type == 'c')
        {
            // The characters follow the index after its separating space, without separators.
            if (_p < _text.Length && _text[_p] == ' ')
            {
                _p++;
            }

            if (_p + count > _text.Length)
            {
                throw new ModelFormatException("A Parasolid string runs past the end of the file.");
            }

            var s = _text.Substring(_p, count);
            _p += count;
            return s;
        }

        if (field.IsDouble)
        {
            var width = field.Width;
            var values = new double[(long)count * width];
            for (var i = 0; i < values.Length; i += width)
            {
                Cancel.Every(_cancel, i);
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
            Cancel.Every(_cancel, i);
            longs[i] = field.Type is 'l' ? Char() : Int();
        }

        return longs;
    }
}
