using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XRay.Email.Msg.Html;

/// <summary>
/// RTF to HTML and RTF to text for message bodies, including the encapsulated-HTML case of
/// [MS-OXRTFEX] (<c>\fromhtml</c>, <c>\htmltag</c>, <c>\htmlrtf</c>), where the original HTML is
/// recovered rather than approximated.
/// </summary>
internal static partial class RtfConverter
{
    private const int MaxPictureHexLength = 24 * 1024 * 1024;
    private const int MaxGroupDepth = 512;

    static RtfConverter()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>True when the RTF wraps HTML (<c>\fromhtml1</c>), the form Outlook writes for an HTML message.</summary>
    public static bool IsEncapsulatedHtml(string rtf) => HeaderContains(rtf, "\\fromhtml");

    /// <summary>True when the RTF only wraps plain text (<c>\fromtext</c>).</summary>
    public static bool IsEncapsulatedText(string rtf) => HeaderContains(rtf, "\\fromtext");

    private static bool HeaderContains(string rtf, string keyword)
    {
        var head = rtf.AsSpan(0, Math.Min(rtf.Length, 2048));
        var index = head.IndexOf(keyword, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var after = index + keyword.Length;
        // \fromhtml1 (or \fromhtml with no parameter); not \fromhtml0
        return !(after < head.Length && head[after] == '0');
    }

    /// <summary>Recovers the HTML an encapsulated-HTML RTF was made from.</summary>
    public static string DeencapsulateHtml(string rtf, CancellationToken cancellationToken = default) =>
        new Engine(rtf, Mode.EncapsulatedHtml, cancellationToken).Run();

    /// <summary>Renders RTF as HTML paragraphs with character formatting, lists as text, tables, links and embedded PNG/JPEG pictures.</summary>
    public static string ToHtml(string rtf, CancellationToken cancellationToken = default) =>
        new Engine(rtf, Mode.Html, cancellationToken).Run();

    /// <summary>Plain text of an RTF document, with CRLF paragraph breaks.</summary>
    public static string ToText(string rtf, CancellationToken cancellationToken = default) =>
        new Engine(rtf, Mode.Text, cancellationToken).Run();

    private enum Mode
    {
        Html,
        Text,
        EncapsulatedHtml,
    }

    private enum Dest
    {
        Normal,
        Skip,
        FontTable,
        ColorTable,
        FieldInstruction,
        FieldResult,
        Picture,
        HtmlTag,
    }

    private sealed class State
    {
        public Dest Dest;
        public bool Star;
        public bool HtmlRtf;
        public bool Skip;
        public bool IsFieldGroup;
        public bool LinkOpen;
        public int Uc = 1;
        public int Codepage;
        public int Font = -1;
        public int FontSize;
        public int Color = -1;
        public int Highlight = -1;
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Strike;
        public int Script; // 1 super, -1 sub
        public int Align; // 0 left, 1 center, 2 right, 3 justify
        public bool InTable;

        public State Clone()
        {
            var copy = (State)MemberwiseClone();
            copy.Star = false;
            copy.IsFieldGroup = false;
            copy.LinkOpen = false;
            return copy;
        }

        public string SpanStyle(Engine engine)
        {
            var css = new StringBuilder();
            if (Bold)
            {
                css.Append("font-weight:bold;");
            }

            if (Italic)
            {
                css.Append("font-style:italic;");
            }

            if (Underline || Strike)
            {
                css.Append("text-decoration:").Append(Underline ? "underline" : string.Empty).Append(Underline && Strike ? " " : string.Empty).Append(Strike ? "line-through" : string.Empty).Append(';');
            }

            if (Script != 0)
            {
                css.Append(Script > 0 ? "vertical-align:super;" : "vertical-align:sub;");
            }

            if (FontSize > 0)
            {
                css.Append("font-size:").Append((FontSize / 2.0).ToString("0.#", CultureInfo.InvariantCulture)).Append("pt;");
            }

            if (engine.FontName(Font) is { } family)
            {
                css.Append("font-family:").Append(family).Append(';');
            }

            if (engine.ColorAt(Color) is { } color)
            {
                css.Append("color:").Append(color).Append(';');
            }

            if (engine.ColorAt(Highlight) is { } background)
            {
                css.Append("background-color:").Append(background).Append(';');
            }

            return css.ToString();
        }
    }

    private sealed class Field
    {
        public StringBuilder Instruction { get; } = new();
    }

    private sealed class Engine
    {
        private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
        {
            "stylesheet", "info", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf",
            "footnote", "listtable", "listoverridetable", "rsidtbl", "filetbl", "revtbl", "generator", "private",
            "xmlnstbl", "latentstyles", "datastore", "themedata", "colorschememapping", "object", "objdata",
            "bkmkstart", "bkmkend", "pnseclvl", "pgptbl", "template", "passwordhash", "wgrffmtfilter", "userprops",
            "mmathPr", "docvar", "nonshppict", "sp", "sn", "sv", "mhtmltag", "title", "subject", "author",
            "operator", "keywords", "comment", "company", "manager", "category", "hlinkbase", "doccomm",
            "pn", "pnlvl", "listlevel", "levelnumbers", "leveltext", "revtbl", "rtlch_unused", "blipuid",
            "falt", "panose", "fname", "fontemb", "fontfile", "ud", "upr", "background", "protusertbl",
            "ftnsep", "ftnsepc", "aftnsep", "aftnsepc", "ftncn", "aftncn", "formfield", "fldtype", "datafield",
            "pgdsctbl", "shpinst_unused", "pnsecl", "oldcprops", "oldpprops", "oldsprops", "oldtprops",
        };

        private static readonly HashSet<string> ProcessedStarDestinations = new(StringComparer.Ordinal)
        {
            "htmltag", "fldinst", "shppict", "listtext", "pntext", "shpinst", "shptxt", "shprslt",
        };

        private readonly string _rtf;
        private readonly Mode _mode;
        private readonly CancellationToken _cancellationToken;
        private readonly StringBuilder _out = new();
        private readonly Stack<State> _stack = new();
        private readonly List<byte> _pendingBytes = [];
        private readonly List<string?> _colors = [];
        private readonly Dictionary<int, FontInfo> _fonts = [];
        private readonly Stack<Field> _fields = new();
        private readonly StringBuilder _picture = new();
        private State _state = new();
        private int _position;
        private int _skip;
        private int _documentCodepage = 1252;
        private int _fontEntry = -1;
        private readonly StringBuilder _fontName = new();
        private int _red = -1, _green = -1, _blue = -1;
        private string _pictureType = string.Empty;
        private int _pictureWidthTwips;
        private int _tick;

        // HTML mode layout state
        private bool _paragraphOpen;
        private string? _openSpan;
        private bool _tableOpen, _rowOpen, _cellOpen, _cellHasContent;
        private int _emptyParagraphsPending;

        public Engine(string rtf, Mode mode, CancellationToken cancellationToken)
        {
            _rtf = rtf;
            _mode = mode;
            _cancellationToken = cancellationToken;
            _state.Codepage = 0;
        }

        private sealed class FontInfo
        {
            public string Name = string.Empty;
            public int Codepage;
        }

        public string? FontName(int index)
        {
            if (index < 0 || !_fonts.TryGetValue(index, out var font) || font.Name.Length == 0)
            {
                return null;
            }

            var name = font.Name.Replace("'", string.Empty, StringComparison.Ordinal).Replace("\"", string.Empty, StringComparison.Ordinal).Replace("<", string.Empty, StringComparison.Ordinal).Replace(">", string.Empty, StringComparison.Ordinal).Replace("&", string.Empty, StringComparison.Ordinal).Replace(";", string.Empty, StringComparison.Ordinal);
            return name.Length == 0 ? null : $"'{name}'";
        }

        public string? ColorAt(int index) => index >= 0 && index < _colors.Count ? _colors[index] : null;

        public string Run()
        {
            var length = _rtf.Length;
            while (_position < length)
            {
                if ((++_tick & 0xFFF) == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                var c = _rtf[_position];
                switch (c)
                {
                    case '{':
                        FlushBytes();
                        if (_stack.Count >= MaxGroupDepth)
                        {
                            // absurd nesting: ignore the rest of the damaged document
                            _position = length;
                            break;
                        }

                        _stack.Push(_state);
                        _state = _state.Clone();
                        _position++;
                        break;
                    case '}':
                        FlushBytes();
                        PopGroup();
                        _position++;
                        break;
                    case '\\':
                        ReadControl();
                        break;
                    case '\r':
                    case '\n':
                        _position++;
                        break;
                    default:
                        ReadRun();
                        break;
                }
            }

            FlushBytes();
            while (_stack.Count > 0)
            {
                PopGroup();
            }

            return Finish();
        }

        private void ReadRun()
        {
            var start = _position;
            var length = _rtf.Length;
            while (_position < length)
            {
                var c = _rtf[_position];
                if (c is '{' or '}' or '\\' or '\r' or '\n')
                {
                    break;
                }

                if (c >= 0x80)
                {
                    if (_position > start)
                    {
                        Text(_rtf.AsSpan(start, _position - start));
                    }

                    if (ConsumeSkip())
                    {
                        // a byte of the \u fallback
                    }
                    else
                    {
                        _pendingBytes.Add((byte)c);
                    }

                    _position++;
                    start = _position;
                    continue;
                }

                _position++;
            }

            if (_position > start)
            {
                Text(_rtf.AsSpan(start, _position - start));
            }
        }

        private bool ConsumeSkip()
        {
            if (_skip > 0)
            {
                _skip--;
                return true;
            }

            return false;
        }

        private void Text(ReadOnlySpan<char> text)
        {
            if (_skip > 0)
            {
                var drop = Math.Min(_skip, text.Length);
                _skip -= drop;
                text = text[drop..];
                if (text.IsEmpty)
                {
                    return;
                }
            }

            FlushBytes();
            Emit(text.ToString());
        }

        private void FlushBytes()
        {
            if (_pendingBytes.Count == 0)
            {
                return;
            }

            var bytes = _pendingBytes.ToArray();
            _pendingBytes.Clear();
            Emit(DecodeBytes(bytes));
        }

        private string DecodeBytes(byte[] bytes)
        {
            var codepage = _state.Codepage != 0 ? _state.Codepage : _documentCodepage;
            if (_state.Font >= 0 && _fonts.TryGetValue(_state.Font, out var font) && font.Codepage != 0)
            {
                codepage = font.Codepage;
            }

            try
            {
                return Encoding.GetEncoding(codepage).GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }

        // ---- control words -------------------------------------------------------------------

        private void ReadControl()
        {
            var length = _rtf.Length;
            _position++; // the backslash
            if (_position >= length)
            {
                return;
            }

            var next = _rtf[_position];
            if (!char.IsAsciiLetter(next))
            {
                _position++;
                ControlSymbol(next);
                return;
            }

            var start = _position;
            while (_position < length && char.IsAsciiLetter(_rtf[_position]))
            {
                _position++;
            }

            var word = _rtf[start.._position];
            int? parameter = null;
            var paramStart = _position;
            if (_position < length && _rtf[_position] == '-')
            {
                _position++;
            }

            var digitsStart = _position;
            while (_position < length && char.IsAsciiDigit(_rtf[_position]))
            {
                _position++;
            }

            if (_position > digitsStart)
            {
                if (int.TryParse(_rtf.AsSpan(paramStart, _position - paramStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                {
                    parameter = value;
                }
            }
            else
            {
                _position = paramStart;
            }

            if (_position < length && _rtf[_position] == ' ')
            {
                _position++; // the delimiter belongs to the control word
            }

            ControlWord(word, parameter);
        }

        private void ControlSymbol(char symbol)
        {
            switch (symbol)
            {
                case '\\' or '{' or '}':
                    Text(symbol.ToString());
                    break;
                case '\'':
                    if (_position + 1 < _rtf.Length && byte.TryParse(_rtf.AsSpan(_position, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                    {
                        _position += 2;
                        if (!ConsumeSkip() && !_state.Skip)
                        {
                            _pendingBytes.Add(value);
                        }
                    }

                    break;
                case '*':
                    _state.Star = true;
                    break;
                case '~':
                    Text(" ");
                    break;
                case '_':
                    Text("‑");
                    break;
                case '\n' or '\r':
                    Paragraph();
                    break;
            }
        }

        private void ControlWord(string word, int? parameter)
        {
            FlushBytes(); // bytes read so far belong to the state in force before this word
            if (_state.Star)
            {
                _state.Star = false;
                if (!ProcessedStarDestinations.Contains(word))
                {
                    _state.Skip = true;
                    _state.Dest = Dest.Skip;
                    return;
                }
            }

            if (word == "bin" && parameter is > 0)
            {
                SkipBinary(parameter.Value);
                return;
            }

            // Destinations first: they apply even where text is otherwise being dropped.
            switch (word)
            {
                case "fonttbl":
                    _state.Dest = Dest.FontTable;
                    return;
                case "colortbl":
                    _state.Dest = Dest.ColorTable;
                    _colors.Clear();
                    _red = _green = _blue = -1;
                    return;
                case "htmltag":
                    _state.Dest = _mode == Mode.EncapsulatedHtml ? Dest.HtmlTag : Dest.Skip;
                    _state.Skip = _mode != Mode.EncapsulatedHtml;
                    _state.HtmlRtf = false;
                    return;
                case "fldinst":
                    _state.Dest = Dest.FieldInstruction;
                    return;
                case "fldrslt":
                    BeginFieldResult();
                    return;
                case "field":
                    _state.IsFieldGroup = true;
                    _fields.Push(new Field());
                    return;
                case "pict":
                    if (_mode == Mode.Html)
                    {
                        _state.Dest = Dest.Picture;
                        _picture.Clear();
                        _pictureType = string.Empty;
                        _pictureWidthTwips = 0;
                    }
                    else
                    {
                        _state.Dest = Dest.Skip;
                        _state.Skip = true;
                    }

                    return;
                case "listtext" or "pntext":
                    if (_mode == Mode.EncapsulatedHtml)
                    {
                        _state.Skip = true;
                        _state.Dest = Dest.Skip;
                    }

                    return;
            }

            if (SkippedDestinations.Contains(word))
            {
                _state.Skip = true;
                _state.Dest = Dest.Skip;
                return;
            }

            if (_state.Dest == Dest.FontTable)
            {
                FontTableWord(word, parameter);
                return;
            }

            if (_state.Dest == Dest.ColorTable)
            {
                switch (word)
                {
                    case "red": _red = parameter ?? 0; break;
                    case "green": _green = parameter ?? 0; break;
                    case "blue": _blue = parameter ?? 0; break;
                }

                return;
            }

            if (_state.Dest == Dest.Picture)
            {
                switch (word)
                {
                    case "pngblip": _pictureType = "image/png"; break;
                    case "jpegblip": _pictureType = "image/jpeg"; break;
                    case "picwgoal": _pictureWidthTwips = parameter ?? 0; break;
                }

                return;
            }

            if (_state.Skip)
            {
                return;
            }

            switch (word)
            {
                case "ansicpg":
                    if (parameter is > 0)
                    {
                        _documentCodepage = parameter.Value;
                    }

                    break;
                case "ansi": _documentCodepage = 1252; break;
                case "mac": _documentCodepage = 10000; break;
                case "pc": _documentCodepage = 437; break;
                case "pca": _documentCodepage = 850; break;
                case "uc": _state.Uc = Math.Max(0, parameter ?? 1); break;
                case "u":
                    if (parameter is { } code)
                    {
                        if (code < 0)
                        {
                            code += 65536;
                        }

                        FlushBytes();
                        Emit(((char)code).ToString());
                        _skip = _state.Uc;
                    }

                    break;
                case "htmlrtf":
                    _state.HtmlRtf = parameter is null or not 0;
                    break;
                case "par": Paragraph(); break;
                case "pard":
                    _state.Align = 0;
                    _state.InTable = false;
                    break;
                case "line": LineBreak(); break;
                case "tab": Emit("\t"); break;
                case "page" or "sect": Paragraph(); break;
                case "lquote": Emit("‘"); break;
                case "rquote": Emit("’"); break;
                case "ldblquote": Emit("“"); break;
                case "rdblquote": Emit("”"); break;
                case "endash": Emit("–"); break;
                case "emdash": Emit("—"); break;
                case "bullet": Emit("•"); break;
                case "enspace": Emit(" "); break;
                case "emspace": Emit(" "); break;
                case "qmspace": Emit(" "); break;
                case "zwj": Emit("‍"); break;
                case "zwnj": Emit("‌"); break;
                case "intbl": _state.InTable = true; break;
                case "trowd": BeginRow(); break;
                case "cell" or "nestcell": EndCell(); break;
                case "row" or "nestrow": EndRow(); break;
                case "plain":
                    _state.Bold = _state.Italic = _state.Underline = _state.Strike = false;
                    _state.Script = 0;
                    _state.FontSize = 0;
                    _state.Font = -1;
                    _state.Color = -1;
                    _state.Highlight = -1;
                    break;
                case "b": _state.Bold = parameter != 0; break;
                case "i": _state.Italic = parameter != 0; break;
                case "ul" or "uld" or "uldb" or "ulw" or "ulth" or "ulwave": _state.Underline = parameter != 0; break;
                case "ulnone": _state.Underline = false; break;
                case "strike" or "striked": _state.Strike = parameter != 0; break;
                case "super": _state.Script = 1; break;
                case "sub": _state.Script = -1; break;
                case "nosupersub": _state.Script = 0; break;
                case "fs": _state.FontSize = parameter ?? 0; break;
                case "f": _state.Font = parameter ?? -1; break;
                case "cf": _state.Color = parameter ?? 0; break;
                case "highlight" or "cb": _state.Highlight = parameter ?? 0; break;
                case "ql": _state.Align = 0; break;
                case "qc": _state.Align = 1; break;
                case "qr": _state.Align = 2; break;
                case "qj": _state.Align = 3; break;
            }
        }

        private void SkipBinary(int count)
        {
            var take = Math.Min(count, _rtf.Length - _position);
            if (_state.Dest == Dest.Picture && !_state.Skip)
            {
                // raw bytes of a picture; stored as hex so one path serves both encodings
                for (var i = 0; i < take && _picture.Length < MaxPictureHexLength; i++)
                {
                    _picture.Append(((byte)_rtf[_position + i]).ToString("x2", CultureInfo.InvariantCulture));
                }
            }

            _position += take;
        }

        private void FontTableWord(string word, int? parameter)
        {
            switch (word)
            {
                case "f":
                    FinishFontEntry();
                    _fontEntry = parameter ?? -1;
                    _fontName.Clear();
                    if (_fontEntry >= 0)
                    {
                        _fonts[_fontEntry] = new FontInfo();
                    }

                    break;
                case "fcharset":
                    if (_fontEntry >= 0 && _fonts.TryGetValue(_fontEntry, out var font))
                    {
                        font.Codepage = CharsetToCodepage(parameter ?? 0);
                    }

                    break;
            }
        }

        private void FinishFontEntry()
        {
            if (_fontEntry >= 0 && _fonts.TryGetValue(_fontEntry, out var font) && font.Name.Length == 0)
            {
                font.Name = _fontName.ToString().Trim();
            }

            _fontName.Clear();
        }

        private static int CharsetToCodepage(int charset) => charset switch
        {
            77 => 10000,
            128 => 932,
            129 => 949,
            130 => 1361,
            134 => 936,
            136 => 950,
            161 => 1253,
            162 => 1254,
            163 => 1258,
            177 => 1255,
            178 => 1256,
            186 => 1257,
            204 => 1251,
            222 => 874,
            238 => 1250,
            254 => 437,
            255 => 850,
            _ => 0,
        };

        private void PopGroup()
        {
            if (_stack.Count == 0)
            {
                return;
            }

            var closing = _state;
            switch (closing.Dest)
            {
                case Dest.FieldInstruction when _fields.Count > 0:
                    break;
                case Dest.Picture:
                    FinishPicture();
                    break;
                case Dest.FontTable:
                    FinishFontEntry();
                    break;
            }

            if (closing.LinkOpen)
            {
                CloseSpan();
                _out.Append("</a>");
            }

            if (closing.IsFieldGroup && _fields.Count > 0)
            {
                _fields.Pop();
            }

            _state = _stack.Pop();
        }

        // ---- fields ---------------------------------------------------------------------------

        private void BeginFieldResult()
        {
            _state.Dest = Dest.FieldResult;
            if (_mode != Mode.Html || _fields.Count == 0)
            {
                return;
            }

            var instruction = _fields.Peek().Instruction.ToString();
            var match = HyperlinkPattern().Match(instruction);
            if (!match.Success || instruction.Contains("\\l ", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var url = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
            if (!IsSafeLink(url))
            {
                return;
            }

            OpenParagraph();
            CloseSpan();
            _out.Append("<a href=\"").Append(HtmlBodyProcessor.EscapeAttribute(url)).Append("\">");
            _state.LinkOpen = true;
        }

        private static bool IsSafeLink(string url) =>
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase);

        // ---- emission --------------------------------------------------------------------------

        private void Emit(string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            switch (_state.Dest)
            {
                case Dest.Skip:
                    return;
                case Dest.FontTable:
                    foreach (var ch in text)
                    {
                        if (ch == ';')
                        {
                            FinishFontEntry();
                        }
                        else
                        {
                            _fontName.Append(ch);
                        }
                    }

                    return;
                case Dest.ColorTable:
                    foreach (var ch in text)
                    {
                        if (ch == ';')
                        {
                            _colors.Add(_red >= 0 && _green >= 0 && _blue >= 0
                                ? $"#{Math.Clamp(_red, 0, 255):x2}{Math.Clamp(_green, 0, 255):x2}{Math.Clamp(_blue, 0, 255):x2}"
                                : null);
                            _red = _green = _blue = -1;
                        }
                    }

                    return;
                case Dest.FieldInstruction:
                    if (_fields.Count > 0)
                    {
                        _fields.Peek().Instruction.Append(text);
                    }

                    return;
                case Dest.Picture:
                    foreach (var ch in text)
                    {
                        if (Uri.IsHexDigit(ch) && _picture.Length < MaxPictureHexLength)
                        {
                            _picture.Append(ch);
                        }
                    }

                    return;
                case Dest.HtmlTag:
                    _out.Append(text);
                    return;
            }

            if (_state.Skip)
            {
                return;
            }

            if (_mode == Mode.EncapsulatedHtml)
            {
                if (_state.HtmlRtf)
                {
                    return;
                }

                _out.Append(text.Replace("<", "&lt;", StringComparison.Ordinal));
                return;
            }

            if (_mode == Mode.Text)
            {
                _out.Append(text);
                return;
            }

            EmitHtmlText(text);
        }

        private void EmitHtmlText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) && !_paragraphOpen && text.Trim('\t', ' ').Length == 0 && !_state.InTable)
            {
                // stray whitespace between paragraphs
                return;
            }

            OpenParagraph();
            var style = _state.SpanStyle(this);
            if (!string.Equals(style, _openSpan, StringComparison.Ordinal))
            {
                CloseSpan();
                if (style.Length > 0)
                {
                    _out.Append("<span style=\"").Append(style).Append("\">");
                    _openSpan = style;
                }
            }

            var escaped = HtmlText.Escape(text).Replace("\t", "&emsp;", StringComparison.Ordinal);
            _out.Append(escaped);
            _cellHasContent = true;
            _emptyParagraphsPending = 0;
        }

        private void CloseSpan()
        {
            if (_openSpan is not null)
            {
                _out.Append("</span>");
                _openSpan = null;
            }
        }

        private void OpenParagraph()
        {
            if (_mode != Mode.Html || _paragraphOpen)
            {
                return;
            }

            if (_state.InTable)
            {
                EnsureCell();
            }
            else if (_tableOpen)
            {
                CloseTable();
            }

            FlushEmptyParagraphs();
            _out.Append("<p style=\"margin:0;").Append(_state.Align switch { 1 => "text-align:center;", 2 => "text-align:right;", 3 => "text-align:justify;", _ => string.Empty }).Append("\">");
            _paragraphOpen = true;
        }

        private void FlushEmptyParagraphs()
        {
            while (_emptyParagraphsPending > 0)
            {
                _out.Append("<p style=\"margin:0\">&nbsp;</p>");
                _emptyParagraphsPending--;
            }
        }

        private void ClosePara()
        {
            if (_paragraphOpen)
            {
                CloseSpan();
                _out.Append("</p>");
                _paragraphOpen = false;
            }
        }

        private void Paragraph()
        {
            if (_state.Skip || _state.Dest is not (Dest.Normal or Dest.FieldResult))
            {
                return;
            }

            switch (_mode)
            {
                case Mode.Text:
                    _out.Append("\r\n");
                    break;
                case Mode.EncapsulatedHtml:
                    if (!_state.HtmlRtf)
                    {
                        _out.Append("\r\n");
                    }

                    break;
                default:
                    if (_paragraphOpen)
                    {
                        ClosePara();
                    }
                    else if (!_state.InTable)
                    {
                        _emptyParagraphsPending++;
                    }

                    break;
            }
        }

        private void LineBreak()
        {
            if (_state.Skip || _state.Dest is not (Dest.Normal or Dest.FieldResult))
            {
                return;
            }

            switch (_mode)
            {
                case Mode.Text:
                    _out.Append("\r\n");
                    break;
                case Mode.EncapsulatedHtml:
                    if (!_state.HtmlRtf)
                    {
                        _out.Append("\r\n");
                    }

                    break;
                default:
                    OpenParagraph();
                    _out.Append("<br>");
                    break;
            }
        }

        // ---- tables ----------------------------------------------------------------------------

        private void BeginRow()
        {
            if (_state.Skip)
            {
                return;
            }

            _state.InTable = true;
            if (_mode == Mode.Html && !_tableOpen)
            {
                ClosePara();
                FlushEmptyParagraphs();
                _out.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse\">");
                _tableOpen = true;
            }
        }

        private void EnsureCell()
        {
            if (!_tableOpen)
            {
                ClosePara();
                _out.Append("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse\">");
                _tableOpen = true;
            }

            if (!_rowOpen)
            {
                _out.Append("<tr>");
                _rowOpen = true;
            }

            if (!_cellOpen)
            {
                _out.Append("<td valign=\"top\">");
                _cellOpen = true;
                _cellHasContent = false;
            }
        }

        private void EndCell()
        {
            if (_state.Skip)
            {
                return;
            }

            switch (_mode)
            {
                case Mode.Text:
                    _out.Append('\t');
                    return;
                case Mode.EncapsulatedHtml:
                    return;
            }

            EnsureCell();
            ClosePara();
            if (!_cellHasContent)
            {
                _out.Append("&nbsp;");
            }

            _out.Append("</td>");
            _cellOpen = false;
        }

        private void EndRow()
        {
            if (_state.Skip)
            {
                return;
            }

            switch (_mode)
            {
                case Mode.Text:
                    _out.Append("\r\n");
                    return;
                case Mode.EncapsulatedHtml:
                    return;
            }

            ClosePara();
            if (_cellOpen)
            {
                _out.Append("</td>");
                _cellOpen = false;
            }

            if (_rowOpen)
            {
                _out.Append("</tr>");
                _rowOpen = false;
            }
        }

        private void CloseTable()
        {
            ClosePara();
            if (_cellOpen)
            {
                _out.Append("</td>");
                _cellOpen = false;
            }

            if (_rowOpen)
            {
                _out.Append("</tr>");
                _rowOpen = false;
            }

            if (_tableOpen)
            {
                _out.Append("</table>");
                _tableOpen = false;
            }
        }

        // ---- pictures --------------------------------------------------------------------------

        private void FinishPicture()
        {
            if (_mode != Mode.Html || _pictureType.Length == 0 || _picture.Length < 16 || _picture.Length % 2 != 0)
            {
                return;
            }

            try
            {
                var bytes = Convert.FromHexString(_picture.ToString());
                OpenParagraph();
                CloseSpan();
                _out.Append("<img src=\"data:").Append(_pictureType).Append(";base64,").Append(Convert.ToBase64String(bytes)).Append('"');
                if (_pictureWidthTwips > 0)
                {
                    _out.Append(" style=\"width:").Append((_pictureWidthTwips / 15).ToString(CultureInfo.InvariantCulture)).Append("px;height:auto\"");
                }

                _out.Append(" alt=\"\">");
                _cellHasContent = true;
                _emptyParagraphsPending = 0;
            }
            catch (FormatException)
            {
                // damaged hex: leave the picture out
            }
            finally
            {
                _picture.Clear();
            }
        }

        private string Finish()
        {
            switch (_mode)
            {
                case Mode.Text:
                    return TrimText(_out.ToString());
                case Mode.EncapsulatedHtml:
                    return _out.ToString();
                default:
                    CloseTable();
                    ClosePara();
                    return _out.ToString();
            }
        }

        private static string TrimText(string text)
        {
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var lines = normalized.Split('\n').Select(line => line.TrimEnd(' ', '\t')).ToList();
            var result = new StringBuilder(text.Length);
            var blank = 0;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    blank++;
                    continue;
                }

                if (result.Length > 0)
                {
                    result.Append("\r\n");
                    if (blank > 0)
                    {
                        result.Append("\r\n");
                    }
                }
                blank = 0;
                result.Append(line);
            }

            return result.ToString();
        }
    }

    [GeneratedRegex(@"HYPERLINK\s+(?:\\[a-z]\s+)*(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HyperlinkPattern();
}
