// Ported from ps/tokenizer.py and ps/parser.py.

using XRay.PS.Internal;

namespace XRay.PS.PostScript;

/// <summary>Token kinds produced by <see cref="PsTokenizer"/>.</summary>
public enum PsTokenKind
{
    Number,
    Name,
    String,
    Comment,
    ArrayStart,
    ArrayEnd,
    ProcedureStart,
    ProcedureEnd,
    DictStart,
    DictEnd,
}

/// <summary>A token: numbers carry long or double, names a (name, literal) pair, strings bytes,
/// comments text.</summary>
public readonly record struct PsToken(PsTokenKind Kind, object? Value, bool Literal = false);

/// <summary>Tokenizes PostScript bytes.</summary>
public sealed class PsTokenizer
{
    private readonly byte[] _data;
    private int _pos;
    private readonly int _length;
    private int _literalArrayDepth;

    public PsTokenizer(byte[] data)
    {
        _data = data;
        _length = data.Length;
    }

    /// <summary>The read position in the input.</summary>
    public int Position
    {
        get => _pos;
        set => _pos = Math.Clamp(value, 0, _length);
    }

    public byte[] Data => _data;

    public int Length => _length;

    private static bool IsWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;

    private static bool IsDelimiter(byte b) => b is (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'/';

    private static bool IsHexDigit(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    /// <summary>Returns the next token, or null at the end of the input.</summary>
    public PsToken? NextToken()
    {
        while (true)
        {
            SkipWhitespace();
            if (_pos >= _length)
                return null;
            byte ch = _data[_pos];
            switch (ch)
            {
                case (byte)'%':
                    return ReadComment();
                case (byte)'[':
                    _pos++;
                    if (LooksLikeLiteralArray())
                    {
                        _literalArrayDepth++;
                        return new PsToken(PsTokenKind.ArrayStart, "[");
                    }
                    return new PsToken(PsTokenKind.Name, "[", false);
                case (byte)']':
                    _pos++;
                    if (_literalArrayDepth > 0)
                    {
                        _literalArrayDepth--;
                        return new PsToken(PsTokenKind.ArrayEnd, "]");
                    }
                    return new PsToken(PsTokenKind.Name, "]", false);
                case (byte)'{':
                    _pos++;
                    return new PsToken(PsTokenKind.ProcedureStart, "{");
                case (byte)'}':
                    _pos++;
                    return new PsToken(PsTokenKind.ProcedureEnd, "}");
                case (byte)'<':
                    if (Peek(1) == '<')
                    {
                        _pos += 2;
                        return new PsToken(PsTokenKind.DictStart, "<<");
                    }
                    return ReadHexString();
                case (byte)'>':
                    if (Peek(1) == '>')
                    {
                        _pos += 2;
                        return new PsToken(PsTokenKind.DictEnd, ">>");
                    }
                    // Be permissive with malformed streams where a dangling '>' may remain
                    // after tolerant hex-string parsing.
                    _pos++;
                    continue;
                case (byte)'(':
                    return ReadString();
            }
            char c = (char)ch;
            if (c is '+' or '-' or '.' || Py.IsDigit(c))
            {
                var token = ReadNumberOrName();
                if (token is not null)
                    return token;
            }
            return ReadName();
        }
    }

    private void SkipWhitespace()
    {
        while (_pos < _length)
        {
            byte ch = _data[_pos];
            if (IsWhitespace(ch))
            {
                _pos++;
                continue;
            }
            return;
        }
    }

    private int Peek(int offset)
    {
        int pos = _pos + offset;
        return pos >= _length ? -1 : _data[pos];
    }

    /// <summary>Heuristic for distinguishing array literals from mark-operator usage.</summary>
    private bool LooksLikeLiteralArray()
    {
        int index = _pos;
        int bracketDepth = 1;
        int procedureDepth = 0;
        while (index < _length)
        {
            byte ch = _data[index];
            if (IsWhitespace(ch))
            {
                index++;
                continue;
            }
            switch (ch)
            {
                case (byte)'%':
                    while (index < _length && _data[index] is not ((byte)'\r' or (byte)'\n'))
                        index++;
                    continue;
                case (byte)'(':
                    index = SkipString(index + 1);
                    continue;
                case (byte)'[':
                    bracketDepth++;
                    index++;
                    continue;
                case (byte)']':
                    bracketDepth--;
                    index++;
                    if (bracketDepth == 0)
                        return true;
                    continue;
                case (byte)'{':
                    procedureDepth++;
                    index++;
                    continue;
                case (byte)'}':
                    if (procedureDepth == 0)
                        return false;
                    procedureDepth--;
                    index++;
                    continue;
            }
            index++;
        }
        return false;
    }

    private int SkipString(int index)
    {
        int depth = 1;
        while (index < _length && depth > 0)
        {
            byte ch = _data[index];
            if (ch == '\\')
            {
                index += 2;
                continue;
            }
            if (ch == '(')
                depth++;
            else if (ch == ')')
                depth--;
            index++;
        }
        return index;
    }

    private PsToken ReadComment()
    {
        int start = _pos + 1;
        while (_pos < _length)
        {
            byte ch = _data[_pos];
            if (ch is (byte)'\r' or (byte)'\n')
                break;
            _pos++;
        }
        return new PsToken(PsTokenKind.Comment, Py.Latin1String(_data.AsSpan(start, Math.Max(0, _pos - start))));
    }

    private PsToken ReadString()
    {
        _pos++;
        int depth = 1;
        var buf = new List<byte>();
        while (_pos < _length && depth > 0)
        {
            byte ch = _data[_pos];
            if (ch == '\\')
            {
                _pos++;
                if (_pos >= _length)
                    break;
                char esc = (char)_data[_pos];
                int mapped = esc switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    '\\' => '\\',
                    '(' => '(',
                    ')' => ')',
                    _ => -1,
                };
                if (mapped >= 0)
                {
                    buf.Add((byte)mapped);
                }
                else if (Py.IsDigit(esc))
                {
                    string octal = esc.ToString();
                    for (int i = 0; i < 2; i++)
                    {
                        if (_pos + 1 < _length && Py.IsDigit((char)_data[_pos + 1]))
                        {
                            _pos++;
                            octal += (char)_data[_pos];
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (!Py.TryInt(octal, 8, out long value))
                        throw Py.ValueError($"invalid literal for int() with base 8: '{octal}'");
                    buf.Add((byte)(value & 0xFF));
                }
                else
                {
                    buf.Add((byte)esc);
                }
                _pos++;
                continue;
            }
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth == 0)
                {
                    _pos++;
                    break;
                }
            }
            if (depth > 0)
            {
                buf.Add(_data[_pos]);
                _pos++;
            }
        }
        return new PsToken(PsTokenKind.String, buf.ToArray());
    }

    private PsToken ReadHexString()
    {
        _pos++;
        // ASCII85 literal string form: <~ ... ~>
        if (_pos < _length && _data[_pos] == '~')
        {
            _pos++;
            int start = _pos;
            while (_pos < _length)
            {
                if (_data[_pos] == '~' && _pos + 1 < _length && _data[_pos + 1] == '>')
                {
                    byte[] payload = _data.AsSpan(start, _pos - start).ToArray();
                    _pos += 2;
                    return new PsToken(PsTokenKind.String, Ascii85.DecodeStrict(payload) ?? payload);
                }
                _pos++;
            }
            return new PsToken(PsTokenKind.String, _data.AsSpan(start, _pos - start).ToArray());
        }

        var hex = new List<byte>();
        while (_pos < _length)
        {
            byte ch = _data[_pos];
            if (ch == '>')
            {
                _pos++;
                break;
            }
            if (IsWhitespace(ch))
            {
                _pos++;
                continue;
            }
            if (!IsHexDigit(ch))
            {
                // Be permissive with malformed input: consume until the terminating '>'.
                while (_pos < _length && _data[_pos] != '>')
                    _pos++;
                if (_pos < _length && _data[_pos] == '>')
                    _pos++;
                break;
            }
            hex.Add(ch);
            _pos++;
        }
        if (hex.Count % 2 == 1)
            hex.Add((byte)'0');
        return new PsToken(PsTokenKind.String, HexDecode(hex));
    }

    internal static byte[] HexDecode(List<byte> hexChars)
    {
        var result = new byte[hexChars.Count / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = (byte)((HexValue(hexChars[i * 2]) << 4) | HexValue(hexChars[i * 2 + 1]));
        return result;
    }

    internal static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => 0,
    };

    private PsToken? ReadNumberOrName()
    {
        int start = _pos;
        string token = ReadUntilDelimiter();
        if (token is "+" or "-" or ".")
        {
            _pos = start;
            return null;
        }
        if (TryParseRadix(token, out long radixValue, out bool radixError))
            return new PsToken(PsTokenKind.Number, radixValue);
        if (radixError)
        {
            _pos = start;
            return null;
        }
        if (token.Contains('.') || token.Contains('e') || token.Contains('E'))
        {
            if (Py.TryFloat(token, out double d))
                return new PsToken(PsTokenKind.Number, d);
        }
        else if (Py.TryInt(token, 10, out long l))
        {
            return new PsToken(PsTokenKind.Number, l);
        }
        _pos = start;
        return null;
    }

    private static bool TryParseRadix(string token, out long value, out bool error)
    {
        value = 0;
        error = false;
        long sign = 1;
        string body = token;
        if (body.StartsWith('+'))
        {
            body = body[1..];
        }
        else if (body.StartsWith('-'))
        {
            sign = -1;
            body = body[1..];
        }
        int hash = body.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
            return false;
        string baseText = body[..hash];
        string numberText = body[(hash + 1)..];
        if (baseText.Length == 0 || numberText.Length == 0 || !Py.TryInt(baseText, 10, out long radix) || radix < 2 || radix > 36
            || !Py.TryInt(numberText, (int)radix, out long number))
        {
            error = true;
            return false;
        }
        value = sign * number;
        return true;
    }

    private PsToken ReadName()
    {
        bool literal = false;
        if (_pos < _length && _data[_pos] == '/')
        {
            literal = true;
            _pos++;
        }
        int before = _pos;
        string name = ReadUntilDelimiter();
        if (!literal && name.Length == 0 && _pos == before && _pos < _length)
        {
            // A stray ')' would otherwise be re-read forever.
            _pos++;
        }
        return new PsToken(PsTokenKind.Name, name, literal);
    }

    private string ReadUntilDelimiter()
    {
        int start = _pos;
        while (_pos < _length)
        {
            byte ch = _data[_pos];
            if (IsWhitespace(ch) || IsDelimiter(ch) || ch == '%')
                break;
            _pos++;
        }
        return Py.Latin1String(_data.AsSpan(start, _pos - start));
    }

    // Stream helpers used by runtime operators (image data sources and readstring).

    public (byte[] Data, bool Complete) ReadAsciiHexDecoded(int maxBytes)
    {
        if (maxBytes <= 0)
            return ([], true);
        var nibbles = new List<byte>();
        bool ended = false;
        while (_pos < _length && nibbles.Count < (long)maxBytes * 2)
        {
            byte ch = _data[_pos];
            if (IsWhitespace(ch))
            {
                _pos++;
                continue;
            }
            if (ch == '>')
            {
                _pos++;
                ended = true;
                break;
            }
            if (!IsHexDigit(ch))
            {
                ended = true;
                break;
            }
            nibbles.Add(ch);
            _pos++;
        }
        if (nibbles.Count % 2 == 1)
            nibbles.Add((byte)'0');
        byte[] decoded = HexDecode(nibbles);
        bool complete = decoded.Length >= maxBytes;
        if (decoded.Length > maxBytes)
            decoded = decoded[..maxBytes];
        return (decoded, complete && !ended);
    }

    public byte[] ReadAsciiHexSource(int maxDecodedBytes)
    {
        if (maxDecodedBytes <= 0)
            return [];
        int start = _pos;
        long nibbles = 0;
        while (_pos < _length && nibbles < (long)maxDecodedBytes * 2)
        {
            byte ch = _data[_pos];
            if (IsWhitespace(ch))
            {
                _pos++;
                continue;
            }
            if (ch == '>')
            {
                _pos++;
                break;
            }
            if (!IsHexDigit(ch))
                break;
            nibbles++;
            _pos++;
        }
        return _data.AsSpan(start, _pos - start).ToArray();
    }

    public byte[] ReadUntilAsciiHexEod()
    {
        int start = _pos;
        while (_pos < _length)
        {
            if (_data[_pos] == '>')
            {
                _pos++;
                break;
            }
            _pos++;
        }
        return _data.AsSpan(start, _pos - start).ToArray();
    }

    public byte[] ReadUntilAscii85Eod()
    {
        int start = _pos;
        while (_pos < _length)
        {
            if (_data[_pos] == '~' && _pos + 1 < _length && _data[_pos + 1] == '>')
            {
                _pos += 2;
                break;
            }
            _pos++;
        }
        return _data.AsSpan(start, _pos - start).ToArray();
    }

    public byte[] ReadRaw(int count)
    {
        if (count <= 0)
            return [];
        int end = (int)Math.Min(_length, (long)_pos + count);
        byte[] data = _data.AsSpan(_pos, end - _pos).ToArray();
        _pos = end;
        return data;
    }

    public byte[] ReadRemaining()
    {
        byte[] data = _data.AsSpan(_pos).ToArray();
        _pos = _length;
        return data;
    }
}

/// <summary>Parses tokens into PostScript objects.</summary>
public sealed class PsParser(PsTokenizer tokenizer)
{
    private readonly Stack<PsToken> _buffer = new();
    private int _depth;
    private int _polls;

    /// <summary>The deepest <c>{ }</c> nesting accepted.</summary>
    public int MaxNesting { get; set; } = 10_000;

    /// <summary>A sentinel distinct from every PostScript value, returned at the end of input.</summary>
    public static readonly object EndOfInput = new();

    /// <summary>Parses one object; returns <see cref="EndOfInput"/> at the end of the input.</summary>
    public object? ParseObject()
    {
        var maybe = NextNonComment();
        if (maybe is not { } token)
            return EndOfInput;
        switch (token.Kind)
        {
            case PsTokenKind.Number:
                return token.Value;
            case PsTokenKind.Name:
                return new PsName((string)token.Value!, token.Literal);
            case PsTokenKind.String:
                return new PsString((byte[])token.Value!);
            case PsTokenKind.ArrayStart:
                return new PsName("[", false);
            case PsTokenKind.DictStart:
                return new PsName("<<", false);
            case PsTokenKind.ProcedureStart:
                return ParseProcedure();
            case PsTokenKind.DictEnd:
                return new PsName(">>", false);
            case PsTokenKind.ArrayEnd:
                return new PsName("]", false);
            case PsTokenKind.ProcedureEnd:
                throw new PsSyntaxError("unexpected token procedure_end");
        }
        return EndOfInput;
    }

    /// <summary>Parses every remaining object.</summary>
    public List<object?> ParseAll()
    {
        var objects = new List<object?>();
        while (true)
        {
            var obj = ParseObject();
            if (ReferenceEquals(obj, EndOfInput))
                break;
            objects.Add(obj);
        }
        return objects;
    }

    private PsProcedure ParseProcedure()
    {
        if (++_depth > MaxNesting)
            throw new PsResourceLimitError($"procedure nesting deeper than {MaxNesting}");
        try
        {
            return ParseProcedureBody();
        }
        finally
        {
            _depth--;
        }
    }

    private PsProcedure ParseProcedureBody()
    {
        var items = new List<object?>();
        while (true)
        {
            var maybe = NextNonComment();
            if (maybe is not { } token)
                throw new PsSyntaxError("unterminated procedure");
            if (token.Kind == PsTokenKind.ProcedureEnd)
                break;
            _buffer.Push(token);
            var item = ParseObject();
            if (ReferenceEquals(item, EndOfInput))
                throw new PsSyntaxError("unterminated procedure");
            items.Add(item);
        }
        return new PsProcedure(items);
    }

    private PsToken? NextNonComment()
    {
        while (true)
        {
            Guard.Every(ref _polls, 0xFF);
            var token = _buffer.Count > 0 ? _buffer.Pop() : tokenizer.NextToken();
            if (token is null)
                return null;
            if (token.Value.Kind != PsTokenKind.Comment)
                return token;
        }
    }
}
