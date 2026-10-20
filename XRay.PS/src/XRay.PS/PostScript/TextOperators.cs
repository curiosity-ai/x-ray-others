// Ported from ps/text_ops.py: show variants, font operators, Type 3 glyph execution,
// composite (Type 0) fonts and CMap operators.

using XRay.PS.Fonts;
using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.PostScript;

/// <summary>A font scaled by <c>scalefont</c>/<c>makefont</c>, waiting for <c>setfont</c>.</summary>
public sealed record ScaledFont(FontResource Font, double Size, Matrix? Shape = null);

/// <summary>Registers the text and font operators.</summary>
public static class TextOperators
{
    internal static readonly HashSet<string> StandardFonts =
    [
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Symbol", "ZapfDingbats",
    ];

    private static readonly Dictionary<string, TrueTypeFont> TtfCache = new(StringComparer.Ordinal);

    public static void Register(OperatorRegistry registry, RenderModelBuilder builder, FontResolver fontResolver)
    {
        var interpreter = new PsInterpreter(registry);
        registry.Register("show", ctx => EmitText(ctx, builder, interpreter, PopText(ctx)), 1);
        registry.Register("ashow", ctx => OpAshow(ctx, builder, interpreter), 3);
        registry.Register("widthshow", ctx => OpWidthShow(ctx, builder, interpreter), 4);
        registry.Register("awidthshow", ctx => OpAwidthShow(ctx, builder, interpreter), 6);
        registry.Register("xshow", ctx => OpXyShow(ctx, builder, interpreter, "x"), 2);
        registry.Register("yshow", ctx => OpXyShow(ctx, builder, interpreter, "y"), 2);
        registry.Register("xyshow", ctx => OpXyShow(ctx, builder, interpreter, "xy"), 2);
        registry.Register("kshow", ctx => OpKshow(ctx, builder, interpreter), 2);

        registry.Register("stringwidth", OpStringWidth, 1);
        registry.Register("charpath", ctx => OpCharpath(ctx, interpreter), 2);

        registry.Register("setfont", OpSetFont, 1);
        registry.Register("currentfont", OpCurrentFont);
        registry.Register("findfont", ctx => OpFindFont(ctx, fontResolver), 1);
        registry.Register("definefont", ctx => OpDefineFont(ctx, fontResolver), 2);
        registry.Register("scalefont", OpScaleFont, 2);
        registry.Register("makefont", OpMakeFont, 2);
        registry.Register("selectfont", ctx => OpSelectFont(ctx, fontResolver), 2);
        registry.Register("composefont", ctx => OpComposeFont(ctx, fontResolver), 3);
        registry.Register("glyphshow", ctx => OpGlyphShow(ctx, builder, interpreter), 1);

        registry.Register("begincmap", ctx => ctx.Extras["_cmap_map"] = new Dictionary<int, int>());
        registry.Register("endcmap", OpEndCmap);
        registry.Register("begincodespacerange", ctx => ctx.Extras["_cmap_codespace_count"] = Math.Max(0, Py.Int(PopNumber(ctx))), 1);
        registry.Register("endcodespacerange", OpEndCodespaceRange);
        registry.Register("beginbfchar", ctx => ctx.Extras["_cmap_bfchar_count"] = Math.Max(0, Py.Int(PopNumber(ctx))), 1);
        registry.Register("endbfchar", OpEndBfChar);

        registry.Register("settextmatrix", ctx => { var m = PopMatrix(ctx); State(ctx).TextMatrix = m; State(ctx).TextLineMatrix = m; }, 6);
        registry.Register("settextline", ctx => State(ctx).TextLineMatrix = PopMatrix(ctx), 6);
        registry.Register("setcharwidth", ctx => { double wy = PopNumber(ctx); double wx = PopNumber(ctx); State(ctx).Type3CharWidth = (wx, wy); }, 2);
        registry.Register("setcachedevice", OpSetCacheDevice, 6);
        registry.Register("setcachedevice2", OpSetCacheDevice2, 10);

        if (registry.Get("moveto") is null)
        {
            registry.Register("moveto", ctx =>
            {
                double y = PopNumber(ctx);
                double x = PopNumber(ctx);
                State(ctx).TextMatrix = new Matrix(1.0, 0.0, 0.0, 1.0, x, y);
                State(ctx).TextLineMatrix = State(ctx).TextMatrix;
            }, 2);
        }
        if (registry.Get("rmoveto") is null)
        {
            registry.Register("rmoveto", ctx =>
            {
                double y = PopNumber(ctx);
                double x = PopNumber(ctx);
                AdvanceText(ctx, x, y);
            }, 2);
        }
    }

    private static GraphicsState State(PsExecutionContext ctx) => ctx.GraphicsStateStack.Peek();

    private static bool IsSkipChar(string ch) => Py.Ord(ch) is 10 or 13;

    private static void OpAshow(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter)
    {
        string text = PopText(ctx);
        double dy = PopNumber(ctx);
        double dx = PopNumber(ctx);
        foreach (string ch in Py.Chars(text))
        {
            EmitGlyph(ctx, builder, interpreter, ch);
            AdvanceText(ctx, dx, dy);
        }
    }

    private static void OpWidthShow(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter)
    {
        string text = PopText(ctx);
        string adjustChar = PopText(ctx);
        double dy = PopNumber(ctx);
        double dx = PopNumber(ctx);
        string target = adjustChar.Length > 0 ? Py.Chars(adjustChar).First() : "";
        foreach (string ch in Py.Chars(text))
        {
            EmitGlyph(ctx, builder, interpreter, ch);
            if (ch == target)
                AdvanceText(ctx, dx, dy);
        }
    }

    private static void OpAwidthShow(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter)
    {
        string text = PopText(ctx);
        double ay = PopNumber(ctx);
        double ax = PopNumber(ctx);
        string adjustChar = PopText(ctx);
        double cy = PopNumber(ctx);
        double cx = PopNumber(ctx);
        string target = adjustChar.Length > 0 ? Py.Chars(adjustChar).First() : "";
        foreach (string ch in Py.Chars(text))
        {
            EmitGlyph(ctx, builder, interpreter, ch);
            AdvanceText(ctx, ax, ay);
            if (ch == target)
                AdvanceText(ctx, cx, cy);
        }
    }

    private static void OpXyShow(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter, string mode)
    {
        var adjustments = ctx.OperandStack.Pop();
        string text = PopText(ctx);
        if (adjustments is not PsArray array)
            throw new PsTypeError("adjustment array expected");
        var flat = NumericArrayValues(ctx, interpreter, array);
        var chars = Py.Chars(text).ToList();
        int count = chars.Count;
        int step = mode == "xy" ? 2 : 1;
        int expectedFull = count * step;
        int expectedBetween = Math.Max(0, (count - 1) * step);
        if (flat.Count > expectedFull)
        {
            // Real-world PS often carries redundant trailing widths; keep the per-glyph ones.
            flat = flat.GetRange(0, expectedFull);
        }
        else if (flat.Count < expectedBetween)
        {
            flat.AddRange(Enumerable.Repeat(0.0, expectedBetween - flat.Count));
        }
        bool hasTrailingAdjust = flat.Count == expectedFull;
        int index = 0;
        for (int charIndex = 0; charIndex < count; charIndex++)
        {
            string ch = chars[charIndex];
            if (hasTrailingAdjust)
            {
                // With full adjustment arrays each glyph is placed at the current point and the
                // point then advances strictly by the supplied offsets.
                var state = State(ctx);
                var previousMatrix = state.TextMatrix;
                var previousPoint = state.CurrentPoint;
                EmitGlyph(ctx, builder, interpreter, ch);
                state = State(ctx);
                state.TextMatrix = previousMatrix;
                state.CurrentPoint = previousPoint;
                var (dx, dy) = NextAdjust(flat, mode, ref index);
                AdvanceText(ctx, dx, dy);
                continue;
            }
            EmitGlyph(ctx, builder, interpreter, ch);
            if (charIndex >= count - 1)
                continue;
            var (ddx, ddy) = NextAdjust(flat, mode, ref index);
            AdvanceText(ctx, ddx, ddy);
        }
    }

    private static (double Dx, double Dy) NextAdjust(List<double> flat, string mode, ref int index)
    {
        double At(int i) => i < flat.Count ? flat[i] : throw Py.IndexError();
        switch (mode)
        {
            case "x":
                return (At(index++), 0.0);
            case "y":
                return (0.0, At(index++));
            default:
                double dx = At(index);
                double dy = At(index + 1);
                index += 2;
                return (dx, dy);
        }
    }

    private static List<double> NumericArrayValues(PsExecutionContext ctx, PsInterpreter interpreter, PsArray values)
    {
        if (values.Items.All(PsOps.IsNumeric))
            return values.Items.Select(PsOps.ToDouble).ToList();
        int baseline = ctx.OperandStack.Count;
        List<object?> produced;
        try
        {
            foreach (var item in values.Items.ToList())
            {
                if (PsOps.IsNumeric(item))
                    ctx.OperandStack.Push(item);
                else
                    interpreter.ExecuteObject(item, ctx);
            }
            var all = ctx.OperandStack.Items;
            produced = baseline <= all.Count ? all.GetRange(baseline, all.Count - baseline) : [];
        }
        finally
        {
            while (ctx.OperandStack.Count > baseline)
                ctx.OperandStack.Pop();
        }
        var result = new List<double>(produced.Count);
        foreach (var item in produced)
        {
            if (!PsOps.IsNumeric(item))
                throw new PsTypeError("number expected");
            result.Add(PsOps.ToDouble(item));
        }
        return result;
    }

    private static void OpKshow(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter)
    {
        ctx.OperandStack.Pop(); // procedure ignored
        string text = PopText(ctx);
        foreach (string ch in Py.Chars(text))
            EmitGlyph(ctx, builder, interpreter, ch);
    }

    private static void OpStringWidth(PsExecutionContext ctx)
    {
        string text = PopText(ctx);
        ctx.OperandStack.Push(StringWidth(ctx, text, includeSpacing: false));
        ctx.OperandStack.Push(0.0);
    }

    private static void OpCharpath(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        ctx.OperandStack.Pop(); // bool for stroke
        string text = PopText(ctx);
        foreach (string ch in Py.Chars(text))
            AppendCharpath(ctx, interpreter, ch);
    }

    private static void OpSetFont(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        switch (value)
        {
            case ScaledFont sf:
                State(ctx).Font = sf.Font;
                State(ctx).FontSize = sf.Size;
                State(ctx).FontShape = sf.Shape;
                return;
            case PsDict d:
                State(ctx).Font = FontResourceFromDict(d, ctx.FontResolver);
                State(ctx).FontShape = null;
                return;
            case FontResource fr:
                State(ctx).Font = fr;
                State(ctx).FontShape = null;
                return;
        }
        throw new PsTypeError("font expected");
    }

    private static void OpCurrentFont(PsExecutionContext ctx)
    {
        var font = State(ctx).Font ?? throw new PsUndefinedError("no current font");
        ctx.OperandStack.Push(FontResourceToDict(font));
    }

    private static void OpDefineFont(PsExecutionContext ctx, FontResolver resolver)
    {
        var fontObj = ctx.OperandStack.Pop();
        var key = ctx.OperandStack.Pop();
        string name = key switch
        {
            PsName n => n.Value,
            string s => s,
            // Deviation from the original, which required a name: any object can be the key (dvips
            // uses the font dictionary itself); it is given a name of its own, stable per job.
            null or PsMark => throw new PsTypeError("definefont expects name key"),
            PsString text => text.Text,
            _ => AnonymousFontName(ctx, key),
        };
        FontResource resource = fontObj switch
        {
            FontResource fr => fr,
            PsDict d => FontResourceFromDict(d, resolver, name),
            _ => throw new PsTypeError("definefont expects dictionary"),
        };
        string? baseName = null;
        if (fontObj is PsDict fd && fd.Get("__font_resource__") is FontResource marker)
            baseName = marker.Name;
        resource = WithFontName(resource, name);
        var fontDict = FontResourceToDict(resource);
        if (ctx.Systemdict.Get("FontDirectory") is PsDict directory)
            directory.Items[name] = fontDict;
        // Alias derived fonts (eg re-encoded Type1 fonts) back to their base typeface so raster
        // text can resolve the correct physical font file.
        resolver.Aliases[name] = string.IsNullOrEmpty(baseName) ? resource.Name : baseName;
        resolver.RegisterDefinedFont(name, resource);
        ctx.OperandStack.Push(fontDict);
    }

    private static string AnonymousFontName(PsExecutionContext ctx, object key)
    {
        if (ctx.Extras.TryGetValue("_ps_font_keys", out var v) is false || v is not Dictionary<object, string> names)
        {
            names = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            ctx.Extras["_ps_font_keys"] = names;
        }
        if (!names.TryGetValue(key, out var name))
        {
            name = "XRayAnonymousFont" + (names.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            names[key] = name;
        }
        return name;
    }

    private static void OpFindFont(PsExecutionContext ctx, FontResolver resolver)
    {
        var value = ctx.OperandStack.Pop();
        string fontName = value switch
        {
            PsName n => n.Value,
            string s => s,
            _ => throw new PsTypeError("font name expected"),
        };
        var directory = ctx.Systemdict.Get("FontDirectory") as PsDict;
        if (directory is not null && directory.Items.TryGetValue(fontName, out var existing))
        {
            if (existing is FontResource fr)
            {
                existing = FontResourceToDict(fr);
                directory.Items[fontName] = existing;
            }
            if (existing is PsDict d)
            {
                ctx.OperandStack.Push(d);
                return;
            }
        }
        var resource = resolver.Resolve(fontName);
        var fontDict = FontResourceToDict(resource);
        if (directory is not null)
            directory.Items[fontName] = fontDict;
        ctx.OperandStack.Push(fontDict);
    }

    private static void OpScaleFont(PsExecutionContext ctx)
    {
        double size = PopNumber(ctx);
        var font = ctx.OperandStack.Pop();
        if (font is PsDict d)
            font = FontResourceFromDict(d, ctx.FontResolver);
        if (font is FontResource fr)
        {
            ctx.OperandStack.Push(new ScaledFont(fr, size));
            return;
        }
        // Deviation from the original, which rejected it: scaling a font already scaled
        // multiplies the sizes (`/F findfont 4 scalefont 2 scalefont` is an 8-point font).
        if (font is ScaledFont scaled)
        {
            ctx.OperandStack.Push(scaled with { Size = scaled.Size * size });
            return;
        }
        throw new PsTypeError("font expected");
    }

    private static void OpMakeFont(PsExecutionContext ctx)
    {
        var matrix = PopMatrix(ctx);
        var font = ctx.OperandStack.Pop();
        if (font is PsDict d)
            font = FontResourceFromDict(d, ctx.FontResolver);
        // Deviation (see OpScaleFont): a scaled font is transformed again; the size is the new
        // matrix's x scale and the rest of it (a flip, a slant) is kept as the font's shape.
        if (font is ScaledFont scaled)
        {
            var old = scaled.Shape ?? Matrix.Identity;
            var current = new Matrix(old.A * scaled.Size, old.B * scaled.Size, old.C * scaled.Size, old.D * scaled.Size, 0.0, 0.0);
            var combined = Multiply(matrix, current);
            double newSize = Py.Hypot(combined.A, combined.B);
            if (newSize == 0)
                newSize = 1.0;
            var shape = new Matrix(combined.A / newSize, combined.B / newSize, combined.C / newSize, combined.D / newSize, 0.0, 0.0);
            ctx.OperandStack.Push(new ScaledFont(scaled.Font, newSize, shape == Matrix.Identity ? null : shape));
            return;
        }
        if (font is not FontResource fr)
            throw new PsTypeError("font expected");
        double size = Py.Hypot(matrix.A, matrix.B);
        ctx.OperandStack.Push(new ScaledFont(fr, size != 0 ? size : 1.0));
    }

    private static void OpSelectFont(PsExecutionContext ctx, FontResolver resolver)
    {
        var second = ctx.OperandStack.Pop();
        var first = ctx.OperandStack.Pop();
        double size;
        if (PsOps.IsNumeric(second))
        {
            size = PsOps.ToDouble(second);
        }
        else
        {
            var matrix = MatrixFromObject(second);
            size = Py.Hypot(matrix.A, matrix.B);
            if (size == 0)
                size = 1.0;
        }
        var font = ResolveFontOperand(first, resolver, ctx);
        State(ctx).Font = font;
        State(ctx).FontSize = size;
        State(ctx).FontShape = null;
    }

    private static void OpComposeFont(PsExecutionContext ctx, FontResolver resolver)
    {
        var descendantSpec = ctx.OperandStack.Pop();
        var cmapValue = ctx.OperandStack.Pop();
        var fontValue = ctx.OperandStack.Pop();
        string fontName = fontValue switch
        {
            PsName n => n.Value,
            string s => s,
            _ => throw new PsTypeError("font name expected"),
        };
        FontResource? baseFont = descendantSpec is PsArray { Items.Count: > 0 } array
            ? ResolveCompositeDescendant(array.Items[0], resolver, ctx)
            : ResolveCompositeDescendant(descendantSpec, resolver, ctx);
        baseFont ??= resolver.Resolve(fontName);
        var codeMap = ResolveCmapMapping(cmapValue, ctx);
        var resource = ComposeFontResource(baseFont, fontName, codeMap);
        var fontDict = FontResourceToDict(resource);
        if (ctx.Systemdict.Get("FontDirectory") is PsDict directory)
            directory.Items[fontName] = fontDict;
        resolver.RegisterDefinedFont(fontName, resource);
        ctx.OperandStack.Push(fontDict);
    }

    private static void OpEndCmap(PsExecutionContext ctx)
    {
        if (ctx.Extras.TryGetValue("_cmap_map", out var mapping) && mapping is Dictionary<int, int> map)
            ctx.DictionaryStack.Peek().Items["__CodeMap__"] = new Dictionary<int, int>(map);
    }

    private static void OpEndCodespaceRange(PsExecutionContext ctx)
    {
        long count = ctx.Extras.TryGetValue("_cmap_codespace_count", out var v) && v is long l ? l : 0;
        for (long i = 0; i < count; i++)
        {
            if (ctx.OperandStack.Count < 2)
                break;
            ctx.OperandStack.Pop();
            ctx.OperandStack.Pop();
        }
    }

    private static void OpEndBfChar(PsExecutionContext ctx)
    {
        long count = ctx.Extras.TryGetValue("_cmap_bfchar_count", out var v) && v is long l ? l : 0;
        if (!ctx.Extras.TryGetValue("_cmap_map", out var m) || m is not Dictionary<int, int> mapping)
        {
            mapping = [];
            ctx.Extras["_cmap_map"] = mapping;
        }
        for (long i = 0; i < count; i++)
        {
            if (ctx.OperandStack.Count < 2)
                break;
            var dst = ctx.OperandStack.Pop();
            var src = ctx.OperandStack.Pop();
            long? srcCode = CodeFromHexObject(src);
            long? dstCode = CodeFromHexObject(dst);
            if (srcCode is not null && dstCode is not null)
                mapping[(int)srcCode.Value] = (int)dstCode.Value;
        }
    }

    private static void OpGlyphShow(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter)
    {
        var value = ctx.OperandStack.Pop();
        string glyphName = value switch
        {
            PsName n => n.Value,
            string s => s,
            _ => throw new PsTypeError("glyphshow expects glyph name"),
        };
        var font = State(ctx).Font ?? throw new PsUndefinedError("no current font");
        var searchFont = font.Descendant ?? font;
        int? code = null;
        foreach (var (k, v) in searchFont.Encoding)
        {
            if (v == glyphName)
            {
                code = k;
                break;
            }
        }
        string ch = code is null ? (glyphName.Length > 0 ? Py.Chars(glyphName).First() : " ") : Py.Chr(code.Value);
        EmitGlyph(ctx, builder, interpreter, ch);
    }

    private static void OpSetCacheDevice(PsExecutionContext ctx)
    {
        double ury = PopNumber(ctx), urx = PopNumber(ctx), lly = PopNumber(ctx), llx = PopNumber(ctx);
        double wy = PopNumber(ctx), wx = PopNumber(ctx);
        var state = State(ctx);
        state.Type3CharWidth = (wx, wy);
        state.Type3CacheBBox = (llx, lly, urx, ury);
    }

    private static void OpSetCacheDevice2(PsExecutionContext ctx)
    {
        for (int i = 0; i < 4; i++)
            PopNumber(ctx); // vy, vx, w1y, w1x
        double ury = PopNumber(ctx), urx = PopNumber(ctx), lly = PopNumber(ctx), llx = PopNumber(ctx);
        double wy = PopNumber(ctx), wx = PopNumber(ctx);
        var state = State(ctx);
        state.Type3CharWidth = (wx, wy);
        state.Type3CacheBBox = (llx, lly, urx, ury);
    }

    private static void EmitText(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter, string text)
    {
        if (text.Length == 0)
            return;
        var state = State(ctx);
        var font = state.Font ?? throw new PsUndefinedError("no current font");
        if (font.FontType == "Type0" && font.FdepVector is { Count: > 0 })
        {
            EmitType0Text(ctx, builder, interpreter, text);
            return;
        }
        if (Py.Chars(text).Any(IsSkipChar))
        {
            foreach (string ch in Py.Chars(text))
                EmitGlyph(ctx, builder, interpreter, ch);
            return;
        }
        if (font.FontType == "Type3")
        {
            foreach (string ch in Py.Chars(text))
                RenderType3Glyph(ctx, interpreter, ch);
            return;
        }
        AddTextRun(ctx, builder, state, font.Descendant ?? font, text, state.Font);
    }

    /// <summary>The shared tail of _emit_text and _emit_glyph: emit one text command and
    /// advance by the string width.</summary>
    private static void AddTextRun(PsExecutionContext ctx, RenderModelBuilder builder, GraphicsState state, FontResource renderFont, string text, FontResource? fallbackFont)
    {
        double fontSize = state.FontSize != 0 ? state.FontSize : 1.0;
        var matrix = GlyphSpace(State(ctx).Ctm, state);
        ctx.TextFontOverrides.TryGetValue(renderFont.Name, out var overrideName);
        string displayText = DisplayTextForFont(renderFont, text);
        if (!string.IsNullOrEmpty(overrideName))
        {
            if (displayText.Length > 0)
                builder.AddText(displayText, overrideName, fontSize, matrix, state.FillPaint);
            AdvanceText(ctx, StringWidth(ctx, text), 0.0);
            return;
        }
        if (!CanUseRequestedTextFont(ctx, renderFont.Name))
            MarkTextFallback(ctx, renderFont.Name, fallbackFont);
        ctx.TextFontOverrides.TryGetValue(renderFont.Name, out overrideName);
        string fontName = string.IsNullOrEmpty(overrideName) ? renderFont.Name : overrideName;
        if (displayText.Length > 0)
            builder.AddText(displayText, fontName, fontSize, matrix, state.FillPaint);
        AdvanceText(ctx, StringWidth(ctx, text), 0.0);
    }

    private static void EmitGlyph(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter, string ch)
    {
        if (IsSkipChar(ch))
            return;
        var state = State(ctx);
        var font = state.Font ?? throw new PsUndefinedError("no current font");
        if (font.FontType == "Type0" && font.FdepVector is { Count: > 0 })
        {
            EmitType0Text(ctx, builder, interpreter, ch);
            return;
        }
        if (font.FontType == "Type3")
        {
            RenderType3Glyph(ctx, interpreter, ch);
            return;
        }
        AddTextRun(ctx, builder, state, font.Descendant ?? font, ch, state.Font);
    }

    internal static double StringWidth(PsExecutionContext ctx, string text, bool includeSpacing = true)
    {
        var font = State(ctx).Font ?? throw new PsUndefinedError("no current font");
        if (font.FontType == "Type0" && font.FdepVector is { Count: > 0 })
        {
            double total = 0.0;
            foreach (var (descendant, run) in IterType0Runs(font, text))
                total += StringWidthForFont(ctx, descendant, run, includeSpacing);
            return total;
        }
        double width = 0.0;
        foreach (string ch in Py.Chars(text))
            width += GlyphAdvance(ctx, ch, includeSpacing);
        return width;
    }

    private static double GlyphAdvance(PsExecutionContext ctx, string ch, bool includeSpacing = true)
    {
        var font = State(ctx).Font ?? throw new PsUndefinedError("no current font");
        return GlyphAdvanceForFont(ctx, font, ch, includeSpacing);
    }

    private static double StringWidthForFont(PsExecutionContext ctx, FontResource font, string text, bool includeSpacing = true)
    {
        double width = 0.0;
        foreach (string ch in Py.Chars(text))
            width += GlyphAdvanceForFont(ctx, font, ch, includeSpacing);
        return width;
    }

    private static double GlyphAdvanceForFont(PsExecutionContext ctx, FontResource font, string ch, bool includeSpacing = true)
    {
        if (IsSkipChar(ch))
            return 0.0;
        var state = State(ctx);
        if (font.Descendant is not null)
            font = font.Descendant;
        if (ctx.FontResolver is null)
            throw new PsUndefinedError("no font resolver");
        int code = Py.Ord(ch);
        double? widthUnits = null;
        if (font.CodeWidths is not null && font.CodeWidths.TryGetValue(code, out double w))
            widthUnits = w;
        if (widthUnits is null)
        {
            string glyphName = font.Encoding.TryGetValue(code, out var gn) ? gn : ".notdef";
            widthUnits = ctx.FontResolver.GetGlyphWidth(font, glyphName);
        }
        double units = widthUnits.Value;
        if (units == 0.0)
            units = font.UnitsPerEm * 0.5;
        double advance = Py.Div(units, font.UnitsPerEm) * state.FontSize;
        if (includeSpacing)
        {
            advance += state.CharSpacing;
            if (ch == " ")
                advance += state.WordSpacing;
        }
        return advance;
    }

    private static void EmitType0Text(PsExecutionContext ctx, RenderModelBuilder builder, PsInterpreter interpreter, string text)
    {
        var state = State(ctx);
        var baseFont = state.Font ?? throw new PsUndefinedError("no current font");
        foreach (var (descendant, run) in IterType0Runs(baseFont, text))
        {
            if (run.Length == 0)
                continue;
            if (descendant.FontType == "Type3")
            {
                var previousFont = state.Font;
                state.Font = descendant;
                try
                {
                    foreach (string ch in Py.Chars(run))
                    {
                        // Type0 descendants frequently use low-byte codes as valid glyph
                        // selectors; only NUL is skipped.
                        if (ch == "\0")
                            continue;
                        RenderType3Glyph(ctx, interpreter, ch, baseFont);
                    }
                }
                finally
                {
                    State(ctx).Font = previousFont;
                }
                continue;
            }
            var renderFont = descendant.Descendant ?? descendant;
            string visible = string.Concat(Py.Chars(run).Where(c => !IsSkipChar(c)));
            string display = DisplayTextForFont(renderFont, visible);
            var matrix = GlyphSpace(state.Ctm, state);
            ctx.TextFontOverrides.TryGetValue(renderFont.Name, out var overrideName);
            if (overrideName is null && !CanUseRequestedTextFont(ctx, renderFont.Name))
            {
                MarkTextFallback(ctx, renderFont.Name, descendant);
                ctx.TextFontOverrides.TryGetValue(renderFont.Name, out overrideName);
            }
            string fontName = string.IsNullOrEmpty(overrideName) ? renderFont.Name : overrideName;
            if (display.Length > 0)
                builder.AddText(display, fontName, state.FontSize != 0 ? state.FontSize : 1.0, matrix, state.FillPaint);
            AdvanceText(ctx, StringWidthForFont(ctx, descendant, run), 0.0);
        }
    }

    private static IEnumerable<(FontResource Font, string Run)> IterType0Runs(FontResource font, string text)
    {
        IReadOnlyList<FontResource> descendants = font.FdepVector is { Count: > 0 } fv ? fv
            : font.Descendant is not null ? [font.Descendant] : [];
        if (descendants.Count == 0)
            yield break;
        var data = new List<byte>(text.Length);
        foreach (char c in text)
        {
            if (c <= 0xFF)
                data.Add((byte)c);
        }
        int currentIndex = 0;
        var chunk = new System.Text.StringBuilder();
        int index = 0;
        int DescendantIndex(int selector)
        {
            long mapped = selector;
            if (font.FmapEncoding is { Count: > 0 } fe && selector >= 0 && selector < fe.Count)
                mapped = fe[selector];
            if (mapped >= 0 && mapped < descendants.Count)
                return (int)mapped;
            return 0;
        }
        // FMapType 2 is an 8/8 mapping: one byte selects the descendant font, the next byte is
        // the character code in that descendant.
        if (font.FmapType == 2)
        {
            while (index < data.Count)
            {
                int nextIndex = DescendantIndex(data[index]);
                index++;
                if (index >= data.Count)
                    break;
                if (chunk.Length > 0 && nextIndex != currentIndex)
                {
                    yield return (descendants[currentIndex], chunk.ToString());
                    chunk.Clear();
                }
                currentIndex = nextIndex;
                chunk.Append(Py.Chr(data[index]));
                index++;
            }
            if (chunk.Length > 0)
                yield return (descendants[currentIndex], chunk.ToString());
            yield break;
        }
        while (index < data.Count)
        {
            int code = data[index];
            if (font.EscChar is not null && code == font.EscChar && index + 1 < data.Count)
            {
                if (chunk.Length > 0)
                {
                    yield return (descendants[currentIndex], chunk.ToString());
                    chunk.Clear();
                }
                currentIndex = DescendantIndex(data[index + 1]);
                index += 2;
                continue;
            }
            var (mappedCode, consumed) = DecodeType0Code(font, data, index);
            chunk.Append(mappedCode is >= 0 and <= 0x10FFFF ? Py.Chr((int)mappedCode) : "�");
            index += consumed;
        }
        if (chunk.Length > 0)
            yield return (descendants[currentIndex], chunk.ToString());
    }

    private static (long Code, int Consumed) DecodeType0Code(FontResource font, List<byte> data, int start)
    {
        if (font.CodeMap is { Count: > 0 } codeMap)
        {
            var lengths = codeMap.Keys.Where(k => k >= 0).Select(k => Math.Max(1, (BitLength(k) + 7) / 8)).Distinct().OrderByDescending(l => l);
            foreach (int length in lengths)
            {
                int end = start + length;
                if (end > data.Count)
                    continue;
                long src = 0;
                for (int i = start; i < end; i++)
                    src = (src << 8) | data[i];
                if (src <= int.MaxValue && codeMap.TryGetValue((int)src, out int dst))
                    return (dst, length);
            }
        }
        return (data[start], 1);
    }

    private static int BitLength(int value)
    {
        int bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }
        return bits;
    }

    private static readonly HashSet<string> SymbolicFonts = ["symbol", "zapfdingbats", "wingdings", "webdings"];

    internal static string DisplayTextForFont(FontResource font, string text)
    {
        if (text.Length == 0)
            return text;
        var mapped = new System.Text.StringBuilder(text.Length);
        bool symbolic = SymbolicFonts.Contains(font.Name.ToLowerInvariant());
        foreach (string ch in Py.Chars(text))
        {
            int code = Py.Ord(ch);
            if (symbolic && code < 256)
            {
                font.Encoding.TryGetValue(code, out var name);
                if (name is null || name == ".notdef" || code < 32)
                    continue;
                mapped.Append(Py.Chr(code));
                continue;
            }
            if (code < 256)
            {
                font.Encoding.TryGetValue(code, out var glyphName);
                string? glyphChar = GlyphNameToUnicode(glyphName);
                if (glyphChar is not null)
                {
                    mapped.Append(glyphChar);
                    continue;
                }
                if (glyphName is null || glyphName == ".notdef" || code < 32)
                    continue;
            }
            mapped.Append(ch);
        }
        return mapped.ToString();
    }

    internal static string? GlyphNameToUnicode(string? glyphName)
    {
        if (string.IsNullOrEmpty(glyphName))
            return null;
        int dot = glyphName.IndexOf('.', StringComparison.Ordinal);
        string b = dot >= 0 ? glyphName[..dot] : glyphName;
        if (b == ".notdef")
            return "";
        if (Py.Len(b) == 1)
            return b;
        if (GlyphNameTable.Unicode.TryGetValue(b, out var direct))
            return direct;
        if (b.StartsWith("uni", StringComparison.Ordinal) && b.Length > 3)
        {
            string hexPart = b[3..];
            if (hexPart.Length % 4 == 0)
            {
                var chars = new System.Text.StringBuilder();
                for (int idx = 0; idx < hexPart.Length; idx += 4)
                {
                    if (!Py.TryInt(hexPart.Substring(idx, 4), 16, out long cp))
                        return null;
                    chars.Append(Py.Chr((int)cp));
                }
                return chars.ToString();
            }
        }
        if (b.StartsWith('u') && b.Length is 5 or 6 or 7)
        {
            if (!Py.TryInt(b[1..], 16, out long cp) || cp < 0 || cp > 0x10FFFF)
                return null;
            return Py.Chr((int)cp);
        }
        if (b.Contains('_', StringComparison.Ordinal))
        {
            var chars = new System.Text.StringBuilder();
            foreach (string part in b.Split('_'))
            {
                string? partChar = GlyphNameToUnicode(part);
                if (partChar is null)
                    return null;
                chars.Append(partChar);
            }
            return chars.ToString();
        }
        return null;
    }

    internal static void AdvanceText(PsExecutionContext ctx, double dx, double dy)
    {
        var state = State(ctx);
        var m = state.TextMatrix;
        double newE = m.E + m.A * dx + m.C * dy;
        double newF = m.F + m.B * dx + m.D * dy;
        state.TextMatrix = m with { E = newE, F = newF };
        state.CurrentPoint = (newE, newF);
    }

    private static void AppendGlyphRect(PsExecutionContext ctx, double width)
    {
        var state = State(ctx);
        double height = state.FontSize;
        var m = GlyphSpace(state.Ctm, state);
        var path = state.CurrentPath;
        path.Segments.Add(PathSegment.MoveTo(m.E, m.F));
        path.Segments.Add(PathSegment.LineTo(m.E + m.A * width, m.F + m.B * width));
        path.Segments.Add(PathSegment.LineTo(m.E + m.A * width + m.C * height, m.F + m.B * width + m.D * height));
        path.Segments.Add(PathSegment.LineTo(m.E + m.C * height, m.F + m.D * height));
        path.Segments.Add(PathSegment.ClosePath());
    }

    private static void RenderType3Glyph(PsExecutionContext ctx, PsInterpreter interpreter, string ch, FontResource? rootFont = null)
    {
        var state = State(ctx);
        var font = state.Font;
        if (font is null)
        {
            AdvanceText(ctx, GlyphAdvance(ctx, ch), 0.0);
            return;
        }
        var fontDict = font.FontDict;
        var execFontDict = fontDict;
        if (fontDict is not null && fontDict.Get("__font_dict__") is PsDict candidateDict)
            execFontDict = candidateDict;
        int code = Py.Ord(ch);
        string glyphName = font.Encoding.TryGetValue(code, out var gn) ? gn : ".notdef";
        PsProcedure? proc = null;
        var fontMatrix = Type3FontMatrix(font);
        double fontSize = state.FontSize != 0 ? state.FontSize : 1.0;
        var scaledFontMatrix = new Matrix(fontMatrix.A * fontSize, fontMatrix.B * fontSize, fontMatrix.C * fontSize,
            fontMatrix.D * fontSize, fontMatrix.E * fontSize, fontMatrix.F * fontSize);
        var outerState = state.Clone();
        state.Type3CharWidth = null;
        state.Type3CacheBBox = null;
        var textCtm = GlyphSpace(outerState.Ctm, state);
        state.Ctm = Multiply(textCtm, scaledFontMatrix);
        PsProcedure? buildGlyph = null;
        PsProcedure? buildChar = null;
        if (execFontDict is not null)
        {
            if (execFontDict.Get("BuildGlyph") is PsProcedure bg)
                buildGlyph = bg;
            if (execFontDict.Get("BuildChar") is PsProcedure bc)
                buildChar = bc;
        }
        if (buildGlyph is null && buildChar is null)
        {
            if (font.CharProcs is not null && font.CharProcs.TryGetValue(glyphName, out var cp))
                proc = cp;
            if (proc is null && execFontDict is not null)
                proc = ResolveType3CharProc(execFontDict, code, glyphName);
        }
        bool previousType3Flag = ctx.InType3Glyph;
        ctx.InType3Glyph = true;
        bool pushedRootScope = false;
        PsDict? rootDict = null;
        if (rootFont?.FontDict is { } rfd && rfd.Items.ContainsKey("RootBuildChar"))
            rootDict = rfd;
        if (rootDict is null && fontDict is not null && fontDict.Items.ContainsKey("RootBuildChar"))
            rootDict = fontDict;
        if (rootDict is null)
        {
            var scopes = ctx.DictionaryStack.Items;
            for (int i = scopes.Count - 1; i >= 0; i--)
            {
                if (scopes[i].Items.ContainsKey("RootBuildChar"))
                {
                    rootDict = scopes[i];
                    break;
                }
            }
        }
        rootDict ??= FindRootFontDict(ctx);
        PsProcedure? rootBuildChar = rootDict?.Get("RootBuildChar") as PsProcedure;
        if (buildGlyph is null && buildChar is null && proc is null && rootBuildChar is null)
        {
            // As in the original, this early exit leaves the glyph's CTM and the Type 3 flag set.
            AdvanceText(ctx, GlyphAdvance(ctx, ch), 0.0);
            return;
        }
        bool hasRootFontBinding = ctx.DictionaryStack.Items.Any(scope => scope.Items.ContainsKey("rootfont"));
        if (rootDict is not null)
        {
            var scope = new PsDict();
            scope.Items["rootfont"] = rootDict;
            ctx.DictionaryStack.Push(scope);
            pushedRootScope = true;
        }
        (double Wx, double Wy)? charWidth;
        try
        {
            if (proc is not null)
            {
                bool pushedDict = false;
                if (execFontDict is not null)
                {
                    ctx.DictionaryStack.Push(execFontDict);
                    pushedDict = true;
                }
                try
                {
                    interpreter.ExecuteProcedure(proc, ctx);
                }
                finally
                {
                    if (pushedDict)
                        ctx.DictionaryStack.Pop();
                }
            }
            else if (buildGlyph is not null && execFontDict is not null)
            {
                ctx.OperandStack.Push(execFontDict);
                ctx.OperandStack.Push(new PsName(glyphName, true));
                interpreter.ExecuteProcedure(buildGlyph, ctx);
            }
            else if (buildChar is not null && execFontDict is not null && (pushedRootScope || hasRootFontBinding))
            {
                ctx.OperandStack.Push(execFontDict);
                ctx.OperandStack.Push((long)code);
                interpreter.ExecuteProcedure(buildChar, ctx);
            }
            else if (rootBuildChar is not null && execFontDict is not null && (pushedRootScope || hasRootFontBinding))
            {
                // Some composite Type0 descendants don't expose BuildChar directly; call
                // RootBuildChar from the composite root font.
                ctx.OperandStack.Push(execFontDict);
                ctx.OperandStack.Push((long)code);
                interpreter.ExecuteProcedure(rootBuildChar, ctx);
            }
        }
        finally
        {
            charWidth = state.Type3CharWidth;
            if (pushedRootScope)
                ctx.DictionaryStack.Pop();
            ctx.InType3Glyph = previousType3Flag;
            // Type3 char procedures must not leak modified text/path/graphics state.
            ctx.GraphicsStateStack.Items[^1] = outerState;
            state = State(ctx);
        }
        if (charWidth is { } cw)
        {
            double advanceX = fontSize * (fontMatrix.A * cw.Wx + fontMatrix.C * cw.Wy);
            double advanceY = fontSize * (fontMatrix.B * cw.Wx + fontMatrix.D * cw.Wy);
            advanceX += state.CharSpacing;
            if (ch == " ")
                advanceX += state.WordSpacing;
            AdvanceText(ctx, advanceX, advanceY);
            return;
        }
        AdvanceText(ctx, GlyphAdvance(ctx, ch), 0.0);
    }

    private static Matrix Type3FontMatrix(FontResource font)
    {
        if (font.FontDict is not null)
        {
            var raw = font.FontDict.Get("FontMatrix");
            List<object?>? items = raw switch
            {
                PsArray a => a.Items,
                List<object?> l => l,
                _ => null,
            };
            if (items is not null && items.Count == 6 && items.All(PsOps.IsNumeric))
                return new Matrix(PsOps.ToDouble(items[0]), PsOps.ToDouble(items[1]), PsOps.ToDouble(items[2]), PsOps.ToDouble(items[3]), PsOps.ToDouble(items[4]), PsOps.ToDouble(items[5]));
        }
        return new Matrix(0.001, 0.0, 0.0, 0.001, 0.0, 0.0);
    }

    private static PsProcedure? ResolveType3CharProc(PsDict fontDict, int code, string glyphName)
    {
        if (fontDict.Get("CharProcs") is not PsDict charProcs)
            return null;
        if (charProcs.Get(glyphName) is PsProcedure direct)
            return direct;
        string? mappedName = null;
        if (fontDict.Get("Encoding") is PsArray encoding && code >= 0 && code < encoding.Items.Count)
        {
            mappedName = encoding.Items[code] switch
            {
                PsName n => n.Value,
                string s => s,
                _ => null,
            };
        }
        if (mappedName is null)
            return null;
        return charProcs.Get(mappedName) as PsProcedure;
    }

    private static PsDict? FindRootFontDict(PsExecutionContext ctx)
    {
        if (ctx.Systemdict.Get("FontDirectory") is not PsDict directory)
            return null;
        foreach (var value in directory.Items.Values)
        {
            PsDict? candidate = value switch
            {
                PsDict d => d,
                FontResource { FontDict: { } fd } => fd,
                _ => null,
            };
            if (candidate is null || !candidate.Items.ContainsKey("RootBuildChar") || !candidate.Items.ContainsKey("CharProcs"))
                continue;
            return candidate;
        }
        return null;
    }

    private static void AppendCharpath(PsExecutionContext ctx, PsInterpreter interpreter, string ch)
    {
        var state = State(ctx);
        var font = state.Font;
        if (font is not null && font.FontType == "Type3")
        {
            var originalPath = state.CurrentPath;
            state.CurrentPath = new RenderPath();
            ctx.CharpathMode = true;
            try
            {
                RenderType3Glyph(ctx, interpreter, ch);
            }
            finally
            {
                ctx.CharpathMode = false;
            }
            originalPath.Segments.AddRange(state.CurrentPath.Segments);
            state.CurrentPath = originalPath;
            return;
        }
        var renderFont = font?.Descendant ?? font;
        var ttfFont = ResolveTtfFont(ctx, renderFont?.Name ?? "");
        if (ttfFont is not null && renderFont is not null)
        {
            var m = GlyphSpace(state.Ctm, state);
            double sizeScale = (state.FontSize != 0 ? state.FontSize : 1.0) / Math.Max(1.0, ttfFont.UnitsPerEm);
            int glyphId = ttfFont.GlyphIdForCode(Py.Ord(ch));
            var contours = ttfFont.GlyphOutline(glyphId);
            if (contours.Count > 0)
            {
                var path = GlyphOutlines.ContoursToPath(contours, sizeScale, m.A, m.B, m.C, m.D, m.E, m.F);
                state.CurrentPath.Segments.AddRange(path.Segments);
            }
            AdvanceText(ctx, ttfFont.GlyphAdvance(glyphId) * sizeScale, 0.0);
            return;
        }
        double width = GlyphAdvance(ctx, ch);
        AppendGlyphRect(ctx, width);
        AdvanceText(ctx, width, 0.0);
    }

    private static void MarkTextFallback(PsExecutionContext ctx, string fontName, FontResource? font)
    {
        if (ctx.TextFontOverrides.ContainsKey(fontName))
            return;
        string lower = fontName.ToLowerInvariant();
        bool isBold = lower.Contains("bold", StringComparison.Ordinal);
        bool isItalic = lower.Contains("italic", StringComparison.Ordinal) || lower.Contains("oblique", StringComparison.Ordinal);
        if (lower.Contains("palatino", StringComparison.Ordinal))
        {
            ctx.TextFontOverrides[fontName] = isBold && isItalic ? "Palatino-BoldItalic" : isBold ? "Palatino-Bold" : isItalic ? "Palatino-Italic" : "Palatino-Roman";
            return;
        }
        string b;
        if (lower.Contains("times", StringComparison.Ordinal) || lower.Contains("serif", StringComparison.Ordinal))
            b = "Times-Roman";
        else if (lower.Contains("symbol", StringComparison.Ordinal))
            b = "Symbol";
        else if (lower.Contains("dingbats", StringComparison.Ordinal))
            b = "ZapfDingbats";
        else if (lower.Contains("courier", StringComparison.Ordinal) || (font is not null && IsMonospaced(font)))
            b = "Courier";
        else
            b = "Helvetica";
        string suffix = isBold && isItalic ? "-BoldOblique" : isBold ? "-Bold" : isItalic ? "-Oblique" : "";
        ctx.TextFontOverrides[fontName] = b + suffix;
    }

    private static bool IsMonospaced(FontResource font)
    {
        List<double> widths;
        if (font.CodeWidths is { Count: > 0 })
            widths = font.CodeWidths.Values.Where(v => v > 0).ToList();
        else if (font.GlyphWidths is { Count: > 0 })
            widths = font.GlyphWidths.Values.Where(v => v > 0).ToList();
        else
            return false;
        if (widths.Count == 0)
            return false;
        return Math.Abs(widths.Max() - widths.Min()) <= 1.0;
    }

    internal static TrueTypeFont? ResolveTtfFont(PsExecutionContext ctx, string fontName)
    {
        if (string.IsNullOrEmpty(fontName) || ctx.FontResolver is null)
            return null;
        var path = ctx.FontResolver.ResolveTtfPath(fontName);
        if (path is null || !path.Exists)
            return null;
        string key = path.ToString();
        lock (TtfCache)
        {
            if (TtfCache.TryGetValue(key, out var cached))
                return cached;
        }
        TrueTypeFont font;
        try
        {
            font = TrueTypeFont.Load(path);
        }
        catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
        {
            return null;
        }
        lock (TtfCache)
            TtfCache[key] = font;
        return font;
    }

    private static bool CanUseRequestedTextFont(PsExecutionContext ctx, string fontName)
    {
        if (string.IsNullOrEmpty(fontName))
            return false;
        if (StandardFonts.Contains(fontName))
            return true;
        if (ResolveTtfFont(ctx, fontName) is not null)
            return true;
        var resolver = ctx.FontResolver;
        if (resolver is null)
            return false;
        if (resolver.GetEmbeddedType42(fontName) is not null)
            return true;
        FontResource resource;
        try
        {
            resource = resolver.Resolve(fontName);
        }
        catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
        {
            return false;
        }
        if (resource.FontProgram is { Length: > 0 })
            return true;
        return resource.Descendant?.FontProgram is { Length: > 0 };
    }

    internal static PsDict FontResourceToDict(FontResource font)
    {
        var data = new PsDict();
        data.Items["FontName"] = new PsName(font.Name, true);
        data.Items["FontType"] = font.FontType switch
        {
            "Type0" => 0L,
            "Type1" => 1L,
            "Type3" => 3L,
            "Type42" => 42L,
            _ => font.FontType,
        };
        data.Items["__font_resource__"] = font;
        if (font.FontDict is not null && font.FontDict.Items.TryGetValue("FontMatrix", out var fm))
        {
            data.Items["FontMatrix"] = fm;
        }
        else
        {
            double units = font.UnitsPerEm != 0 ? font.UnitsPerEm : 1000.0;
            double scale = 1.0 / units;
            data.Items["FontMatrix"] = new PsArray([scale, 0.0, 0.0, scale, 0.0, 0.0]);
        }
        if (font.FdepVector is { Count: > 0 })
            data.Items["FDepVector"] = new PsArray(font.FdepVector.Select(d => (object?)FontResourceToDict(d)).ToList());
        if (font.EscChar is not null)
            data.Items["EscChar"] = (long)font.EscChar.Value;
        if (font.FmapType is not null)
            data.Items["FMapType"] = (long)font.FmapType.Value;
        if (font.FmapEncoding is { Count: > 0 })
            data.Items["Encoding"] = new PsArray(font.FmapEncoding.Select(v => (object?)(long)v).ToList());
        else if (font.Encoding.Count > 0)
            data.Items["Encoding"] = EncodingArrayFromMapping(font.Encoding);
        if (font.FontDict is not null)
            data.Items["__font_dict__"] = font.FontDict;
        if (font.CodeMap is { Count: > 0 })
            data.Items["__CodeMap__"] = new Dictionary<int, int>(font.CodeMap);
        return data;
    }

    private static PsArray EncodingArrayFromMapping(IReadOnlyDictionary<int, string> mapping)
    {
        var values = new List<object?>(256);
        for (int i = 0; i < 256; i++)
            values.Add(new PsName(".notdef", true));
        foreach (var (code, name) in mapping)
        {
            if (code is < 0 or > 255)
                continue;
            values[code] = new PsName(string.IsNullOrEmpty(name) ? ".notdef" : name, true);
        }
        return new PsArray(values);
    }

    internal static FontResource FontResourceFromDict(PsDict fontDict, FontResolver? resolver, string? nameHint = null)
    {
        if (!fontDict.Items.ContainsKey("FontName"))
        {
            if (nameHint is null)
                nameHint = fontDict.Get("__font_resource__") is FontResource m ? m.Name : "UnknownFont";
            fontDict.Items["FontName"] = new PsName(nameHint, true);
        }
        if (fontDict.Get("__font_resource__") is FontResource marker)
        {
            string resolvedName = fontDict.Get("FontName") switch
            {
                PsName n => n.Value,
                string s => s,
                _ => marker.Name,
            };
            var encoding = EncodingFromValue(fontDict.Get("Encoding"), marker.Encoding);
            var codeMap = marker.CodeMap;
            if (fontDict.Get("__CodeMap__") is Dictionary<int, int> raw && raw.Count > 0)
                codeMap = new Dictionary<int, int>(raw);
            int? fmapType = marker.FmapType;
            var rawFmap = fontDict.Get("FMapType");
            if (PsOps.IsNumeric(rawFmap))
                fmapType = (int)Py.Int(PsOps.ToDouble(rawFmap));
            return marker with { Name = resolvedName, Encoding = encoding, CodeMap = codeMap, FmapType = fmapType };
        }
        if (resolver is null)
            throw new PsUndefinedError("no font resolver");
        return resolver.ResolveFromDict(fontDict);
    }

    private static IReadOnlyDictionary<int, string> EncodingFromValue(object? value, IReadOnlyDictionary<int, string> fallback)
    {
        static IReadOnlyDictionary<int, string> Named(string name) => name switch
        {
            "SymbolEncoding" => Encodings.Symbol,
            "ZapfDingbatsEncoding" => Encodings.ZapfDingbats,
            "ISOLatin1Encoding" or "ISOLatin1" => Encodings.IsoLatin1,
            _ => Encodings.Standard,
        };
        switch (value)
        {
            case string s:
                return Named(s);
            case PsName n:
                return Named(n.Value);
            case PsArray a:
            {
                var mapping = new Dictionary<int, string>();
                for (int i = 0; i < a.Items.Count; i++)
                {
                    if (a.Items[i] is PsName pn)
                        mapping[i] = pn.Value;
                    else if (a.Items[i] is string str)
                        mapping[i] = str;
                }
                if (mapping.Count > 0)
                    return mapping;
                break;
            }
        }
        return fallback;
    }

    private static bool CodeMapEquals(IReadOnlyDictionary<int, int>? a, IReadOnlyDictionary<int, int>? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out int v) && v == kv.Value);
    }

    private static FontResource WithFontName(FontResource font, string name, Dictionary<int, int>? codeMap = null)
    {
        if (font.Name == name && (codeMap is null || CodeMapEquals(codeMap, font.CodeMap)))
            return font;
        var resolved = codeMap is { Count: > 0 } ? new Dictionary<int, int>(codeMap) : font.CodeMap;
        // A fresh resource (the original built a new dataclass), not a shared one.
        return new FontResource(name, font.FontType, font.UnitsPerEm, font.Encoding, font.GlyphWidths, font.Substitute,
            font.CharProcs, font.CodeWidths, font.Descendant, font.EscChar, font.FdepVector, font.FmapEncoding, font.FontDict,
            resolved, font.FontProgram, font.FmapType);
    }

    private static FontResource ComposeFontResource(FontResource baseFont, string compositeName, Dictionary<int, int>? codeMap)
    {
        var resolved = codeMap is { Count: > 0 } ? new Dictionary<int, int>(codeMap) : null;
        return new FontResource(compositeName, "Type0", 1000, baseFont.Encoding, baseFont.GlyphWidths, baseFont.Substitute,
            CodeWidths: baseFont.CodeWidths, Descendant: baseFont, FdepVector: [baseFont], EscChar: baseFont.EscChar,
            FmapEncoding: baseFont.FmapEncoding, FontDict: baseFont.FontDict, CodeMap: resolved, FmapType: baseFont.FmapType);
    }

    private static Matrix MatrixFromObject(object? value)
    {
        var items = value switch
        {
            PsArray a => a.Items,
            List<object?> l => l,
            _ => throw new PsTypeError("matrix expected"),
        };
        if (items.Count != 6)
            throw new PsRangeError("matrix must have 6 elements");
        var result = new double[6];
        for (int i = 0; i < 6; i++)
        {
            if (!PsOps.IsNumeric(items[i]))
                throw new PsTypeError("matrix number expected");
            result[i] = PsOps.ToDouble(items[i]);
        }
        return Matrix.FromArray(result);
    }

    private static FontResource ResolveFontOperand(object? value, FontResolver resolver, PsExecutionContext ctx) => value switch
    {
        FontResource fr => fr,
        PsDict d => FontResourceFromDict(d, ctx.FontResolver ?? resolver),
        PsName n => ResolveFontFromDirectory(n.Value, resolver, ctx),
        string s => ResolveFontFromDirectory(s, resolver, ctx),
        _ => throw new PsTypeError("font name expected"),
    };

    private static FontResource? ResolveCompositeDescendant(object? value, FontResolver resolver, PsExecutionContext ctx) => value switch
    {
        FontResource fr => fr,
        PsDict d => FontResourceFromDict(d, ctx.FontResolver ?? resolver),
        PsName n => ResolveFontFromDirectory(n.Value, resolver, ctx),
        string s => ResolveFontFromDirectory(s, resolver, ctx),
        _ => null,
    };

    private static FontResource ResolveFontFromDirectory(string fontName, FontResolver resolver, PsExecutionContext ctx)
    {
        if (ctx.Systemdict.Get("FontDirectory") is PsDict directory && directory.Items.TryGetValue(fontName, out var value))
        {
            if (value is FontResource fr)
                return fr;
            if (value is PsDict d)
                return FontResourceFromDict(d, ctx.FontResolver ?? resolver);
        }
        return resolver.Resolve(fontName);
    }

    private static Dictionary<int, int>? ResolveCmapMapping(object? value, PsExecutionContext ctx)
    {
        var cmapDict = ResolveCmapDict(value, ctx);
        if (cmapDict?.Get("__CodeMap__") is not Dictionary<int, int> raw)
            return null;
        return raw.Count > 0 ? new Dictionary<int, int>(raw) : null;
    }

    private static PsDict? ResolveCmapDict(object? value, PsExecutionContext ctx)
    {
        if (value is PsDict d)
            return d;
        string? name = value switch
        {
            PsName n => n.Value,
            string s => s,
            _ => null,
        };
        if (string.IsNullOrEmpty(name))
            return null;
        var resources = ctx.Systemdict.Get("__resources__");
        if (resources is Dictionary<object, PsDict> map)
        {
            if (map.TryGetValue("CMap", out var cat) && cat.Get(name) is PsDict candidate)
                return candidate;
        }
        else if (resources is PsDict rd && rd.Get("CMap") is PsDict cat2 && cat2.Get(name) is PsDict candidate2)
        {
            return candidate2;
        }
        var scopes = ctx.DictionaryStack.Items;
        for (int i = scopes.Count - 1; i >= 0; i--)
        {
            if (scopes[i].Get(name) is PsDict candidate)
                return candidate;
        }
        return null;
    }

    private static long? CodeFromHexObject(object? value)
    {
        static long? FromBytes(byte[] bytes)
        {
            if (bytes.Length == 0)
                return null;
            long result = 0;
            foreach (byte b in bytes)
                result = (result << 8) | b;
            return result;
        }
        switch (value)
        {
            case PsString s:
                return FromBytes(s.Value);
            case byte[] b:
                return FromBytes(b);
            case PsName n:
            {
                string? glyphChar = GlyphNameToUnicode(n.Value);
                if (!string.IsNullOrEmpty(glyphChar) && Py.Len(glyphChar) == 1)
                    return Py.Ord(glyphChar);
                if (n.Value.StartsWith("uni", StringComparison.Ordinal) && n.Value.Length > 3)
                    return Py.TryInt(n.Value[3..], 16, out long cp) ? cp : null;
                if (Py.Len(n.Value) == 1)
                    return Py.Ord(n.Value);
                return null;
            }
            case string str:
            {
                string? glyphChar = GlyphNameToUnicode(str);
                if (!string.IsNullOrEmpty(glyphChar) && Py.Len(glyphChar) == 1)
                    return Py.Ord(glyphChar);
                if (Py.Len(str) == 1)
                    return Py.Ord(str);
                return null;
            }
        }
        return null;
    }

    internal static string PopText(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        return value switch
        {
            PsString s => s.Text,
            byte[] b => Py.Latin1String(b),
            string str => str,
            _ => throw new PsTypeError("string expected"),
        };
    }

    private static double PopNumber(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (PsOps.IsNumeric(value))
            return PsOps.ToDouble(value);
        throw new PsTypeError("number expected");
    }

    private static Matrix PopMatrix(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        var items = value switch
        {
            PsArray a => a.Items,
            List<object?> l => l,
            _ => throw new PsTypeError("matrix array expected"),
        };
        if (items.Count != 6)
            throw new PsRangeError("matrix must have 6 elements");
        var v = items.Select(ColorOperators.PyFloat).ToArray();
        return Matrix.FromArray(v);
    }

    // The CTM times the text matrix, and the font's shape (see GraphicsState.FontShape) when it
    // has one: identical to the original's product for every font it supported.
    private static Matrix GlyphSpace(Matrix ctm, GraphicsState state)
    {
        var m = Multiply(ctm, state.TextMatrix);
        return state.FontShape is { } shape ? Multiply(m, shape) : m;
    }

    /// <summary><c>_multiply_matrix(ctm, matrix)</c>: apply <paramref name="matrix"/>, then
    /// <paramref name="ctm"/>.</summary>
    internal static Matrix Multiply(Matrix ctm, Matrix matrix) => new(
        ctm.A * matrix.A + ctm.C * matrix.B,
        ctm.B * matrix.A + ctm.D * matrix.B,
        ctm.A * matrix.C + ctm.C * matrix.D,
        ctm.B * matrix.C + ctm.D * matrix.D,
        ctm.A * matrix.E + ctm.C * matrix.F + ctm.E,
        ctm.B * matrix.E + ctm.D * matrix.F + ctm.F);
}
