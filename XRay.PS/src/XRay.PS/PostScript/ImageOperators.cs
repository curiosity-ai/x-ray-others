// Ported from ps/image_ops.py: image, colorimage and imagemask, with their data sources
// (strings, procedures reading currentfile, filter chains) and indexed color expansion.

using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.PostScript;

/// <summary>Registers the image operators.</summary>
public static class ImageOperators
{
    private static readonly HashSet<string> ImageDictKeys =
    [
        "ImageType", "Width", "Height", "BitsPerComponent", "ImageMatrix", "DataSource", "Decode", "Interpolate", "Filter",
        "MultipleDataSource",
    ];

    private sealed record ImagePayload(PsImageResource Resource, Matrix Matrix);

    public static void Register(OperatorRegistry registry, RenderModelBuilder builder, PsImageStore imageStore)
    {
        // The interpreter that runs a general data-source procedure (see ExtractFromProcedure).
        var interpreter = new PsInterpreter(registry);
        registry.Register("image", ctx => { ctx.Extras[InterpreterKey] = interpreter; OpImage(ctx, builder, imageStore); }, 1);
        registry.Register("colorimage", ctx => { ctx.Extras[InterpreterKey] = interpreter; OpColorImage(ctx, builder, imageStore); }, 4);
        registry.Register("imagemask", ctx => { ctx.Extras[InterpreterKey] = interpreter; OpImageMask(ctx, builder, imageStore); }, 1);
        registry.Register("setinterpolation", ctx => State(ctx).ImageInterpolate = PsOps.Truthy(ctx.OperandStack.Pop()), 1);
    }

    private const string InterpreterKey = "__image_interpreter__";

    private static GraphicsState State(PsExecutionContext ctx) => ctx.GraphicsStateStack.Peek();

    private static void AddImage(PsExecutionContext ctx, RenderModelBuilder builder, PsImageStore store, ImagePayload image)
    {
        string imageId = store.Register(image.Resource);
        builder.AddImage(imageId, image.Resource.Width, image.Resource.Height, image.Matrix, image.Resource.Mask,
            image.Resource.Mask ? State(ctx).FillPaint : null);
    }

    private static void OpImage(PsExecutionContext ctx, RenderModelBuilder builder, PsImageStore store)
    {
        ImagePayload image;
        if (IsDictForm(ctx))
        {
            var parameters = PopDict(ctx);
            image = ImageFromDict(ctx, parameters, false, InferImageComponents(parameters, 1));
        }
        else
        {
            image = ImageFromOperands(ctx, false);
        }
        AddImage(ctx, builder, store, image);
    }

    private static void OpColorImage(PsExecutionContext ctx, RenderModelBuilder builder, PsImageStore store)
    {
        ImagePayload image;
        if (IsDictForm(ctx))
        {
            var parameters = PopDict(ctx);
            int components = CoerceInt(parameters.Items.TryGetValue("N", out var n) ? n : 3L);
            image = ImageFromDict(ctx, parameters, false, components);
        }
        else
        {
            int components = CoerceInt(ctx.OperandStack.Pop());
            bool multi = PsOps.Truthy(ctx.OperandStack.Pop());
            if (multi)
            {
                if (components <= 0)
                    throw new PsRangeError("invalid colorimage component count");
                List<object?> sources;
                if (ctx.OperandStack.Count > 0 && ctx.OperandStack.Peek() is PsArray)
                {
                    sources = ((PsArray)ctx.OperandStack.Pop()!).Items.ToList();
                }
                else
                {
                    sources = [];
                    for (int i = 0; i < components; i++)
                        sources.Add(ctx.OperandStack.Pop());
                    sources.Reverse();
                }
                if (sources.Count != components)
                    throw new PsRangeError("multi-source colorimage source count mismatch");
                image = ImageFromOperandsMulti(ctx, sources, components);
            }
            else
            {
                image = ImageFromOperands(ctx, false, components);
            }
        }
        AddImage(ctx, builder, store, image);
    }

    private static void OpImageMask(PsExecutionContext ctx, RenderModelBuilder builder, PsImageStore store)
    {
        ImagePayload image = IsDictForm(ctx) ? ImageFromDict(ctx, PopDict(ctx), true, 1) : ImageFromOperands(ctx, true);
        AddImage(ctx, builder, store, image);
    }

    private static ImagePayload ImageFromOperands(PsExecutionContext ctx, bool mask, int colorComponents = 1)
    {
        var dataSource = ctx.OperandStack.Pop();
        var matrix = CoerceMatrix(ctx, ctx.OperandStack.Pop());
        bool maskPolarity = true;
        int bits;
        if (mask)
        {
            maskPolarity = PsOps.Truthy(ctx.OperandStack.Pop());
            bits = 1;
        }
        else
        {
            bits = CoerceInt(ctx.OperandStack.Pop());
        }
        int height = CoerceInt(ctx.OperandStack.Pop());
        int width = CoerceInt(ctx.OperandStack.Pop());
        return BuildImage(ctx, width, height, bits, matrix, dataSource, mask, colorComponents, null, maskPolarity: maskPolarity);
    }

    private static ImagePayload ImageFromDict(PsExecutionContext ctx, PsDict parameters, bool mask, int colorComponents)
    {
        object? Get(string key, object? fallback = null) => parameters.Items.TryGetValue(key, out var v) ? v : fallback;
        int width = CoerceInt(Get("Width"));
        int height = CoerceInt(Get("Height"));
        int bits = CoerceInt(Get("BitsPerComponent", mask ? 1L : null));
        var matrix = CoerceMatrix(ctx, Get("ImageMatrix"));
        var dataSource = Get("DataSource");
        var filters = ExtractFilters(Get("Filter"));
        if (filters.Count == 0 && dataSource is PsFile { Name: "currentfile" } file)
            filters = [.. file.Filters];
        if (filters.Count == 0)
            filters = ExtractImplicitFilters(parameters);
        var interpolate = Get("Interpolate");
        var decode = CoerceDecode(Get("Decode"));
        var indexedColor = ParseIndexedColorSpace(ctx, parameters.Get("ColorSpace"));
        bool maskPolarity = true;
        if (mask && Get("Decode") is PsArray { Items.Count: >= 2 } decodeObj)
        {
            var first = decodeObj.Items[0];
            var second = decodeObj.Items[1];
            if (PsOps.IsNumeric(first) && PsOps.IsNumeric(second))
                maskPolarity = PsOps.ToDouble(first) <= PsOps.ToDouble(second);
        }
        // Deviation from the original, which rejected it: a dictionary image with
        // MultipleDataSources reads one source per component, calling them in turn (they often
        // share one file), and the planes are interleaved into pixels.
        if (!mask && bits == 8 && PsOps.Truthy(Get("MultipleDataSources")) && dataSource is PsArray { Items.Count: > 1 } planes
            && planes.Items.Count == colorComponents)
        {
            dataSource = InterleavePlanes(ctx, planes.Items, width, height);
            filters = [];
        }
        return BuildImage(ctx, width, height, bits, matrix, dataSource, mask, colorComponents, filters, interpolate, decode, maskPolarity, indexedColor);
    }

    private static byte[] InterleavePlanes(PsExecutionContext ctx, List<object?> sources, int width, int height)
    {
        int count = sources.Count;
        long perPlane = Math.Max(0, (long)width * height);
        Guard.Charge(perPlane * count * 2, "image");
        var planes = new MemoryStream[count];
        for (int i = 0; i < count; i++)
            planes[i] = new MemoryStream();
        var interpreter = ctx.Extras.TryGetValue(InterpreterKey, out var it) ? it as PsInterpreter : null;
        bool progress = true;
        while (progress && planes.Any(p => p.Length < perPlane))
        {
            Guard.Check();
            progress = false;
            for (int i = 0; i < count; i++)
            {
                if (planes[i].Length >= perPlane)
                    continue;
                byte[] chunk;
                if (sources[i] is PsProcedure proc && interpreter is not null)
                {
                    int depth = ctx.OperandStack.Count;
                    interpreter.ExecuteProcedure(proc, ctx);
                    if (ctx.OperandStack.Count <= depth || ctx.OperandStack.Pop() is not PsString s)
                        throw new PsTypeError("image data source procedure must return a string");
                    chunk = s.Value;
                }
                else
                {
                    chunk = ExtractBytes(ctx, sources[i], perPlane, []);
                }
                if (chunk.Length == 0)
                    continue;
                planes[i].Write(chunk, 0, (int)Math.Min(chunk.Length, perPlane - planes[i].Length));
                progress = true;
            }
        }
        var interleaved = new byte[perPlane * count];
        for (int i = 0; i < count; i++)
        {
            var plane = planes[i].GetBuffer();
            long length = planes[i].Length;
            for (long px = 0; px < length; px++)
                interleaved[px * count + i] = plane[px];
        }
        return interleaved;
    }

    private static ImagePayload ImageFromOperandsMulti(PsExecutionContext ctx, List<object?> sources, int colorComponents)
    {
        var matrix = CoerceMatrix(ctx, ctx.OperandStack.Pop());
        int bits = CoerceInt(ctx.OperandStack.Pop());
        int height = CoerceInt(ctx.OperandStack.Pop());
        int width = CoerceInt(ctx.OperandStack.Pop());
        if (bits != 8)
            throw new PsUndefinedError("multi-source colorimage supports 8-bit components only");
        long pixelCount = Math.Max(0, (long)width * height);
        Guard.Charge(pixelCount * colorComponents, "image");
        var interleaved = new byte[pixelCount * colorComponents];
        int rowBytes = (int)ExpectedDataLength(width, 1, bits, 1, false);
        var specs = sources.Select(s => s is PsProcedure p ? CurrentfileReadSpec(p) : null).ToList();
        // Source procedures are invoked once per output row, for plain and filtered currentfile
        // procedures alike (for example ASCII85+RunLength rows in EPS exports).
        bool rowwise = specs.All(s => s is not null);
        if (rowwise)
        {
            for (int row = 0; row < height; row++)
            {
                long rowBase = (long)row * width * colorComponents;
                for (int component = 0; component < sources.Count; component++)
                {
                    var rowData = ExtractBytes(ctx, sources[component], rowBytes, []);
                    if (rowData.Length < rowBytes)
                        rowData = [.. rowData, .. new byte[rowBytes - rowData.Length]];
                    for (int x = 0; x < width; x++)
                        interleaved[rowBase + (long)x * colorComponents + component] = rowData[x];
                }
            }
        }
        else
        {
            long perComponent = ExpectedDataLength(width, height, bits, 1, false);
            var decoded = sources.Select(s => ExtractBytes(ctx, s, perComponent, [])).ToList();
            for (long pixel = 0; pixel < pixelCount; pixel++)
            {
                for (int component = 0; component < colorComponents; component++)
                {
                    var data = decoded[component];
                    interleaved[pixel * colorComponents + component] = pixel < data.Length ? data[pixel] : (byte)0;
                }
            }
        }
        return BuildImage(ctx, width, height, bits, matrix, interleaved, false, colorComponents, null);
    }

    private static ImagePayload BuildImage(PsExecutionContext ctx, int width, int height, int bits, Matrix imageMatrix, object? dataSource,
        bool mask, int colorComponents, List<FilterSpec>? filters, object? interpolateOverride = null, double[]? decode = null,
        bool maskPolarity = true, (string Space, int HiVal, byte[] Table)? indexedColor = null)
    {
        if (width <= 0 || height <= 0)
            throw new PsRangeError("invalid image dimensions");
        var normalized = NormalizeImageMatrix(imageMatrix, width, height);
        var effective = TextOperators.Multiply(State(ctx).Ctm, normalized);
        long expectedBytes = ExpectedDataLength(width, height, bits, colorComponents, mask);
        Guard.Charge(expectedBytes, "image");
        var filterResult = ExtractFilteredImageSource(ctx, dataSource, expectedBytes, filters ?? []);
        byte[] imageData = filterResult.Data;
        if (filterResult.RemainingFilter is null)
            imageData = MaybeFlipRows(imageData, width, height, mask ? 1 : bits, colorComponents, mask);
        string colorSpace = ColorSpaceFromComponents(colorComponents, mask);
        if (!mask && indexedColor is not null && filterResult.RemainingFilter is null)
        {
            var expanded = ExpandIndexedData(imageData, width, height, bits, indexedColor.Value);
            if (expanded is not null)
            {
                imageData = expanded;
                colorSpace = indexedColor.Value.Space;
                colorComponents = ComponentsForColorSpace(colorSpace);
                bits = 8;
                decode = null;
                // Deviation from the original, which flipped an indexed image's rows again here
                // when the matrix is top-down ([w 0 0 -h 0 h]) and so drew it upside down; the
                // rows are already in the order MaybeFlipRows gives every other image.
            }
        }
        bool interpolate = State(ctx).ImageInterpolate;
        if (interpolateOverride is not null)
            interpolate = PsOps.Truthy(interpolateOverride);
        // Deviation (see MaybeFlipRows): data still encoded (DCT, CCITT) cannot have its rows
        // reversed, so the placement is flipped instead, top of the unit square to the bottom.
        if (filterResult.RemainingFilter is not null)
            effective = TextOperators.Multiply(effective, new Matrix(1.0, 0.0, 0.0, -1.0, 0.0, 1.0));
        var matrix = SnapImageTranslation(effective);
        return new ImagePayload(
            new PsImageResource("", imageData, width, height, mask ? 1 : bits, colorSpace, interpolate, mask, filterResult.RemainingFilter,
                filterResult.Params, decode, maskPolarity),
            matrix);
    }

    private static FilterResult ExtractFilteredImageSource(PsExecutionContext ctx, object? dataSource, long expectedBytes, List<FilterSpec> filters)
    {
        if (dataSource is PsString s)
        {
            var result = new FilterResult(s.Value, s.RemainingFilter, s.FilterParams);
            if (filters.Count > 0)
            {
                var outer = Filters.Decode(result.Data, filters, allowEncoded: true);
                if (outer.RemainingFilter is not null)
                    return outer;
                if (result.RemainingFilter is not null)
                    return new FilterResult(outer.Data, result.RemainingFilter, result.Params);
                return outer;
            }
            return result;
        }
        // Keep remaining encoded filters (eg CCITTFaxDecode/DCTDecode) when image data is read
        // from currentfile procedures with inline filter chains.
        if (dataSource is PsProcedure proc)
        {
            var spec = CurrentfileReadSpec(proc);
            if (spec is not null)
            {
                var (readOp, procFilters) = spec.Value;
                var tokenizer = GetTokenizer(ctx) ?? throw new PsUndefinedError("currentfile unavailable");
                if (readOp == "readhexstring" && procFilters.Count == 0)
                {
                    var (decoded, _) = tokenizer.ReadAsciiHexDecoded(ClampCount(expectedBytes));
                    return Filters.Decode(decoded, filters, allowEncoded: true);
                }
                var source = ReadCurrentfileSource(ctx, expectedBytes, procFilters);
                var procResult = procFilters.Count > 0 ? Filters.Decode(source, procFilters, allowEncoded: true) : new FilterResult(source, null, null);
                var outerResult = Filters.Decode(procResult.Data, filters, allowEncoded: true);
                if (outerResult.RemainingFilter is not null)
                    return outerResult;
                if (procResult.RemainingFilter is not null)
                    return new FilterResult(outerResult.Data, procResult.RemainingFilter, procResult.Params);
                return outerResult;
            }
        }
        var data = ExtractBytes(ctx, dataSource, expectedBytes, []);
        return Filters.Decode(data, filters, allowEncoded: true);
    }

    private static int ClampCount(long value) => (int)Math.Min(int.MaxValue, value);

    private static string ColorSpaceFromComponents(int components, bool mask)
    {
        if (mask)
            return "DeviceGray";
        return components switch
        {
            1 => "DeviceGray",
            3 => "DeviceRGB",
            4 => "DeviceCMYK",
            _ => throw new PsRangeError("unsupported color components"),
        };
    }

    private static int ComponentsForColorSpace(string colorSpace) => colorSpace switch
    {
        "DeviceGray" => 1,
        "DeviceRGB" => 3,
        "DeviceCMYK" => 4,
        _ => throw new PsRangeError("unsupported color space"),
    };

    private static (string, int, byte[])? ParseIndexedColorSpace(PsExecutionContext ctx, object? value)
    {
        if (value is not PsArray array || array.Items.Count < 4)
            return null;
        if (NameOrString(array.Items[0]) != "Indexed")
            return null;
        string? baseName = LookupColorSpaceName(ctx, array.Items[1]);
        if (baseName is null)
            return null;
        var hivalObj = array.Items[2];
        if (!PsOps.IsNumeric(hivalObj))
            return null;
        long hival = Py.Int(PsOps.ToDouble(hivalObj));
        if (hival < 0)
            return null;
        var table = LookupColorTable(ctx, array.Items[3]);
        if (table is null)
            return null;
        long needed = (hival + 1) * ComponentsForColorSpace(baseName);
        if (table.Length < needed)
            return null;
        return (baseName, (int)hival, table);
    }

    private static string? LookupColorSpaceName(PsExecutionContext ctx, object? value)
    {
        if (value is PsArray { Items.Count: > 0 } array)
        {
            // Color space objects can be wrapped as one-element arrays (eg [/DeviceRGB]).
            return LookupColorSpaceName(ctx, array.Items[0]);
        }
        string? name = NameOrString(value);
        if (name is null && value is PsName n)
            name = NameOrString(LookupName(ctx, n.Value));
        return name switch
        {
            "DefaultGray" => "DeviceGray",
            "DefaultRGB" => "DeviceRGB",
            "DefaultCMYK" => "DeviceCMYK",
            "DeviceGray" or "DeviceRGB" or "DeviceCMYK" => name,
            _ => null,
        };
    }

    private static byte[]? LookupColorTable(PsExecutionContext ctx, object? value) => value switch
    {
        PsString s => s.Value,
        byte[] b => b,
        PsName n => LookupName(ctx, n.Value) is { } resolved ? LookupColorTable(ctx, resolved) : null,
        _ => null,
    };

    private static string? NameOrString(object? value) => value switch
    {
        PsName n => n.Value,
        string s => s,
        _ => null,
    };

    private static byte[]? ExpandIndexedData(byte[] data, int width, int height, int bits, (string Space, int HiVal, byte[] Table) indexed)
    {
        if (bits is not (1 or 2 or 4 or 8))
            return null;
        int components = ComponentsForColorSpace(indexed.Space);
        long rowBits = (long)width * bits;
        long rowBytes = (rowBits + 7) / 8;
        long expected = rowBytes * height;
        if (data.Length < expected)
            return null;
        Guard.Charge((long)width * height * components, "image");
        var output = new byte[(long)width * height * components];
        long write = 0;
        int mask = (1 << bits) - 1;
        for (int y = 0; y < height; y++)
        {
            long rowStart = y * rowBytes;
            long bitIndex = 0;
            for (int x = 0; x < width; x++)
            {
                long bytePos = bitIndex / 8;
                int shift = 8 - bits - (int)(bitIndex % 8);
                int sample = (data[rowStart + bytePos] >> shift) & mask;
                bitIndex += bits;
                if (sample > indexed.HiVal)
                    sample = indexed.HiVal;
                int b = sample * components;
                // out[write:write+components] = table[b:b+components]; the table is long enough.
                for (int k = 0; k < components; k++)
                    output[write + k] = indexed.Table[b + k];
                write += components;
            }
        }
        return output;
    }

    private static byte[] ExtractBytes(PsExecutionContext ctx, object? value, long expectedBytes, List<FilterSpec> filters)
    {
        switch (value)
        {
            case PsString s:
                return s.Value;
            case byte[] b:
                return b;
            case PsProcedure p:
                return ExtractFromProcedure(ctx, p, expectedBytes, filters);
            case PsName n:
            {
                if (n.Value == "currentfile")
                    return ReadCurrentfileSource(ctx, expectedBytes, filters);
                var resolved = LookupName(ctx, n.Value) ?? throw new PsUndefinedError($"undefined name {n.Value}");
                return ExtractBytes(ctx, resolved, expectedBytes, filters);
            }
            case PsFile f:
                if (f.Name == "currentfile")
                    return ReadCurrentfileSource(ctx, expectedBytes, filters.Count > 0 ? filters : [.. f.Filters]);
                // Only currentfile files carry data in this interpreter.
                throw new PsTypeError("file data must be bytes");
            case "currentfile":
                return ReadCurrentfileSource(ctx, expectedBytes, filters);
        }
        throw new PsTypeError("image data source must be string or file");
    }

    private static List<FilterSpec> ExtractFilters(object? value) => value switch
    {
        null => [],
        PsName n => [new FilterSpec(n.Value, null)],
        string s => [new FilterSpec(s, null)],
        PsArray a => a.Items.Select(item => item switch
        {
            PsName n => new FilterSpec(n.Value, null),
            string s => new FilterSpec(s, null),
            _ => null,
        }).Where(f => f is not null).Select(f => f!).ToList(),
        _ => [],
    };

    private static double[]? CoerceDecode(object? value)
    {
        if (value is not PsArray array)
            return null;
        var decoded = array.Items.Where(PsOps.IsNumeric).Select(PsOps.ToDouble).ToList();
        return decoded.Count < 2 ? null : [.. decoded];
    }

    private static List<FilterSpec> ExtractImplicitFilters(PsDict dictionary)
    {
        var filters = new List<FilterSpec>();
        foreach (var (key, value) in dictionary.Items)
        {
            if (key is string sk && ImageDictKeys.Contains(sk))
                continue;
            if ((value is PsName { Value: "filter" }) || (value is "filter"))
            {
                if (key is not string name)
                    throw Py.TypeError("'int' object has no attribute 'startswith'");
                filters.Add(new FilterSpec(name, null));
            }
        }
        return filters;
    }

    private static Matrix CoerceMatrix(PsExecutionContext ctx, object? value)
    {
        var items = value switch
        {
            PsArray a => a.Items,
            List<object?> l => l,
            _ => throw new PsTypeError("matrix array expected"),
        };
        if (items.Count != 6)
            throw new PsRangeError("matrix must have 6 elements");
        var coerced = new double[6];
        for (int i = 0; i < 6; i++)
        {
            var current = items[i];
            if (current is PsName n)
                current = LookupName(ctx, n.Value) ?? throw new PsUndefinedError($"undefined name {n.Value}");
            if (!PsOps.IsNumeric(current))
                throw new PsTypeError("matrix values must be numeric");
            coerced[i] = PsOps.ToDouble(current);
        }
        return Matrix.FromArray(coerced);
    }

    /// <summary>Converts a PostScript ImageMatrix (user space to sample space) into the render
    /// model's unit-square-to-user-space transform: inverse(ImageMatrix) x Scale(width, height).</summary>
    private static Matrix NormalizeImageMatrix(Matrix matrix, int width, int height)
    {
        var inv = InvertAffine(matrix);
        if (inv is null)
        {
            // Permissive fallback for malformed input.
            double sx = 1.0 / Math.Max(1, width);
            double sy = 1.0 / Math.Max(1, height);
            return new Matrix(matrix.A * sx, matrix.B * sx, matrix.C * sy, matrix.D * sy, matrix.E * sx, matrix.F * sy);
        }
        var sampleScale = new Matrix(Math.Max(1, width), 0.0, 0.0, Math.Max(1, height), 0.0, 0.0);
        return TextOperators.Multiply(inv.Value, sampleScale);
    }

    private static Matrix? InvertAffine(Matrix m)
    {
        double det = m.A * m.D - m.B * m.C;
        if (Math.Abs(det) <= 1e-12)
            return null;
        double invDet = 1.0 / det;
        double na = m.D * invDet;
        double nb = -m.B * invDet;
        double nc = -m.C * invDet;
        double nd = m.A * invDet;
        double ne = -(na * m.E + nc * m.F);
        double nf = -(nb * m.E + nd * m.F);
        return new Matrix(na, nb, nc, nd, ne, nf);
    }

    private static int CoerceInt(object? value)
    {
        if (PsOps.IsNumeric(value))
        {
            long l = value is long lv ? lv : Py.Int(PsOps.ToDouble(value));
            return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        }
        throw new PsTypeError("integer expected");
    }

    private static bool IsDictForm(PsExecutionContext ctx) => ctx.OperandStack.Count > 0 && ctx.OperandStack.Peek() is PsDict;

    private static PsDict PopDict(PsExecutionContext ctx) =>
        ctx.OperandStack.Pop() as PsDict ?? throw new PsTypeError("image dictionary expected");

    private static int InferImageComponents(PsDict parameters, int fallback)
    {
        if (parameters.Get("Decode") is PsArray decode)
        {
            int count = decode.Items.Count;
            if (count >= 2 && count % 2 == 0)
            {
                int components = count / 2;
                if (components is >= 1 and <= 4)
                    return components;
            }
        }
        string? name = NameOrString(parameters.Get("ColorSpace"));
        return name switch
        {
            "DeviceGray" => 1,
            "DeviceRGB" => 3,
            "DeviceCMYK" => 4,
            _ => fallback,
        };
    }

    private static object? LookupName(PsExecutionContext ctx, string name) =>
        PsInterpreter.LookupDict(name, ctx, out var value) ? value : null;

    private static long ExpectedDataLength(int width, int height, int bits, int colorComponents, bool mask)
    {
        int components = mask ? 1 : Math.Max(1, colorComponents);
        long rowBits = (long)width * Math.Max(1, bits) * components;
        long rowBytes = (rowBits + 7) / 8;
        return rowBytes * height;
    }

    private static byte[] MaybeFlipRows(byte[] data, int width, int height, int bits, int colorComponents, bool mask)
    {
        // Deviation from the original, which reversed the rows only for a bottom-up matrix
        // ([w 0 0 h 0 0]) and for [-w 0 0 -h w h] under a quarter turn, and so drew every other
        // image upside down, the common top-down [w 0 0 -h 0 h] above all: the placement matrix
        // (the CTM times the inverse image matrix, scaled to the sample grid) takes the first
        // sample row to the bottom of the PDF unit square whatever the image matrix, so the rows
        // are always reversed to put it at the top.
        return FlipImageRows(data, width, height, bits, colorComponents, mask);
    }

    private static byte[] FlipImageRows(byte[] data, int width, int height, int bits, int colorComponents, bool mask)
    {
        int components = mask ? 1 : Math.Max(1, colorComponents);
        long rowBits = (long)width * Math.Max(1, bits) * components;
        long rowBytes = (rowBits + 7) / 8;
        long needed = rowBytes * height;
        if (rowBytes <= 0 || height <= 1 || data.Length < needed)
            return data;
        var flipped = new byte[data.Length];
        for (int row = 0; row < height; row++)
            Array.Copy(data, row * rowBytes, flipped, (height - 1 - row) * rowBytes, rowBytes);
        if (data.Length > needed)
            Array.Copy(data, needed, flipped, needed, data.Length - needed);
        return flipped;
    }

    private static byte[] ExtractFromProcedure(PsExecutionContext ctx, PsProcedure proc, long expectedBytes, List<FilterSpec> filters)
    {
        var spec = CurrentfileReadSpec(proc);
        if (spec is not null)
        {
            var (readOp, procFilters) = spec.Value;
            var tokenizer = GetTokenizer(ctx) ?? throw new PsUndefinedError("currentfile unavailable");
            if (readOp == "readhexstring" && procFilters.Count == 0)
                return tokenizer.ReadAsciiHexDecoded(ClampCount(expectedBytes)).Data;
            var source = ReadCurrentfileSource(ctx, expectedBytes, procFilters);
            return procFilters.Count > 0 ? Filters.Decode(source, procFilters, allowEncoded: true).Data : source;
        }
        if (proc.Items.Count == 1)
        {
            switch (proc.Items[0])
            {
                case PsString s:
                    return s.Value;
                case PsName n:
                {
                    var resolved = LookupName(ctx, n.Value) ?? throw new PsUndefinedError($"undefined name {n.Value}");
                    return ExtractBytes(ctx, resolved, expectedBytes, filters);
                }
                case PsOperator op:
                    return ExtractBytes(ctx, new PsName(op.Name), expectedBytes, filters);
            }
        }
        if (ctx.Extras.TryGetValue(InterpreterKey, out var i) && i is PsInterpreter interpreter)
            return RunDataSourceProcedure(ctx, interpreter, proc, expectedBytes);
        throw new PsTypeError("unsupported image data source procedure");
    }

    // Deviation from the original, which recognised only procedures shaped like
    // {currentfile ... readstring pop}: as the PLRM specifies, call the procedure until it has
    // delivered the image's data, each call leaving one string, and stop early at an empty one.
    private static byte[] RunDataSourceProcedure(PsExecutionContext ctx, PsInterpreter interpreter, PsProcedure proc, long expectedBytes)
    {
        long wanted = Math.Max(0, expectedBytes);
        using var output = new MemoryStream();
        while (output.Length < wanted)
        {
            Guard.Check();
            int depth = ctx.OperandStack.Count;
            interpreter.ExecuteProcedure(proc, ctx);
            if (ctx.OperandStack.Count <= depth || ctx.OperandStack.Pop() is not PsString chunk)
                throw new PsTypeError("image data source procedure must return a string");
            if (chunk.Value.Length == 0)
                break;
            output.Write(chunk.Value, 0, (int)Math.Min(chunk.Value.Length, wanted - output.Length));
        }
        return output.ToArray();
    }

    private static (string ReadOp, List<FilterSpec> Filters)? CurrentfileReadSpec(PsProcedure proc)
    {
        var items = proc.Items;
        if (items.Count < 3)
            return null;
        if (ExecName(items[0]) != "currentfile")
            return null;
        if (ExecName(items[^1]) != "pop")
            return null;
        string? readOp = ExecName(items[^2]);
        if (readOp is not ("readhexstring" or "readstring"))
            return null;
        var filters = new List<FilterSpec>();
        int index = 1;
        int end = items.Count - 3;
        while (index + 1 <= end)
        {
            if (items[index] is PsName candidate && ExecName(items[index + 1]) == "filter")
            {
                filters.Add(new FilterSpec(candidate.Value, null));
                index += 2;
                continue;
            }
            break;
        }
        return (readOp, filters);
    }

    private static string? ExecName(object? value) => value switch
    {
        PsName n => n.Value,
        PsOperator o => o.Name,
        _ => null,
    };

    private static PsTokenizer? GetTokenizer(PsExecutionContext ctx) => ctx.Systemdict.Get(PsInterpreter.TokenizerKey) as PsTokenizer;

    private static byte[] ReadCurrentfileSource(PsExecutionContext ctx, long expectedBytes, IReadOnlyList<FilterSpec> filters)
    {
        var tokenizer = GetTokenizer(ctx) ?? throw new PsUndefinedError("currentfile unavailable");
        if (filters.Count > 0)
        {
            string first = Filters.NormalizeName(filters[0].Name);
            if (first == "ASCIIHexDecode")
                return expectedBytes > 0 ? tokenizer.ReadAsciiHexSource(ClampCount(expectedBytes)) : tokenizer.ReadUntilAsciiHexEod();
            if (first == "ASCII85Decode")
                return tokenizer.ReadUntilAscii85Eod();
        }
        return expectedBytes > 0 ? tokenizer.ReadRaw(ClampCount(expectedBytes)) : tokenizer.ReadRemaining();
    }

    private static Matrix SnapImageTranslation(Matrix m)
    {
        // Many legacy PS generators place images at near-integer translations (eg 0.75 or
        // 14.17 points); snapping the residual improves parity with the functional baselines.
        double e = m.E, f = m.F;
        double eRound = Py.Round(e);
        double fRound = Py.Round(f);
        if (Math.Abs(e - eRound) <= 0.26)
            e = eRound;
        if (Math.Abs(f - fRound) <= 0.26)
            f = fRound;
        return m with { E = e, F = f };
    }
}
