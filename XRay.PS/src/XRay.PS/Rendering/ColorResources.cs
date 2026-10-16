// Ported from common/color_resources.py: color spaces, functions, shadings and patterns shared
// by the render model.

namespace XRay.PS.Rendering;

/// <summary>Base type for color space resources.</summary>
public abstract record ColorSpace;

/// <summary>A device color space: DeviceGray, DeviceRGB or DeviceCMYK.</summary>
public sealed record DeviceColorSpace(string Name) : ColorSpace;

/// <summary>An indexed color space over a base space with a lookup table.</summary>
public sealed record IndexedColorSpace(ColorSpace Base, int HiVal, byte[] Lookup) : ColorSpace;

/// <summary>A Separation color space with its alternate space and tint transform.</summary>
public sealed record SeparationColorSpace(string Name, ColorSpace Alternate, Function Tint) : ColorSpace;

/// <summary>A DeviceN color space with its colorant names, alternate space and tint transform.</summary>
public sealed record DeviceNColorSpace(IReadOnlyList<string> Names, ColorSpace Alternate, Function Tint) : ColorSpace;

/// <summary>A CIE-based color space carried as an ICC profile.</summary>
public sealed record CieBasedColorSpace(byte[] IccProfile, int Components, double[]? Ranges = null) : ColorSpace;

/// <summary>A Pattern color space with an optional underlying space for uncolored patterns.</summary>
public sealed record PatternColorSpace(ColorSpace? Base) : ColorSpace;

/// <summary>Base type for PostScript/PDF functions.</summary>
public abstract record Function
{
    /// <summary>Evaluates the function.</summary>
    public abstract double[] Evaluate(IReadOnlyList<double> inputs);

    internal static double Clamp(double value, double lower, double upper)
    {
        if (value < lower)
            return lower;
        if (value > upper)
            return upper;
        return value;
    }
}

/// <summary>A sampled (type 0) function.</summary>
public sealed record SampledFunction(
    double[] Domain,
    double[] Range,
    int[] Size,
    int BitsPerSample,
    int Order,
    double[] Encode,
    double[] Decode,
    byte[] Samples) : Function
{
    private int[]? _decoded;

    public override double[] Evaluate(IReadOnlyList<double> inputs)
    {
        int inputCount = Domain.Length / 2;
        if (inputs.Count != inputCount)
            throw new ArgumentException("sampled function input count mismatch");
        int outputs = Range.Length / 2;
        var samples = _decoded ??= DecodeSamples(Samples, BitsPerSample);
        var indices = new int[inputCount];
        for (int idx = 0; idx < inputCount; idx++)
        {
            double d0 = Domain[idx * 2];
            double d1 = Domain[idx * 2 + 1];
            double value = Clamp(inputs[idx], d0, d1);
            double e0 = Encode.Length > 0 ? Encode[idx * 2] : d0;
            double e1 = Encode.Length > 0 ? Encode[idx * 2 + 1] : d1;
            double mapped = d1 == d0 ? e0 : e0 + (value - d0) * (e1 - e0) / (d1 - d0);
            int size = Size[idx];
            if (size <= 1)
            {
                indices[idx] = 0;
            }
            else
            {
                double pos = e1 != e0 ? (mapped - e0) / (e1 - e0) : 0.0;
                indices[idx] = (int)Math.Round(Clamp(pos, 0.0, 1.0) * (size - 1), MidpointRounding.ToEven);
            }
        }
        long sampleIndex = 0;
        long multiplier = 1;
        int n = Math.Min(indices.Length, Size.Length);
        for (int k = 0; k < n; k++)
        {
            // zip(reversed(indices), reversed(size))
            int idx = indices[indices.Length - 1 - k];
            int size = Size[Size.Length - 1 - k];
            sampleIndex += idx * multiplier;
            multiplier *= size;
        }
        long start = sampleIndex * outputs;
        var results = new double[outputs];
        long maxSample = (1L << BitsPerSample) - 1;
        for (int outIdx = 0; outIdx < outputs; outIdx++)
        {
            int raw = samples[checked((int)(start + outIdx))];
            double d0 = Decode.Length > 0 ? Decode[outIdx * 2] : Range[outIdx * 2];
            double d1 = Decode.Length > 0 ? Decode[outIdx * 2 + 1] : Range[outIdx * 2 + 1];
            double value = maxSample == 0 ? d0 : d0 + (raw / (double)maxSample) * (d1 - d0);
            results[outIdx] = Clamp(value, Range[outIdx * 2], Range[outIdx * 2 + 1]);
        }
        return results;
    }

    internal static int[] DecodeSamples(byte[] data, int bits)
    {
        if (bits <= 0)
            throw new ArgumentException("bits per sample must be positive");
        long maxValue = (1L << bits) - 1;
        var result = new List<int>(data.Length * 8 / Math.Max(1, bits) + 1);
        long buffer = 0;
        int bufferBits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bufferBits += 8;
            while (bufferBits >= bits)
            {
                int shift = bufferBits - bits;
                result.Add((int)((buffer >> shift) & maxValue));
                bufferBits -= bits;
                buffer &= bufferBits > 0 ? (1L << bufferBits) - 1 : 0;
            }
        }
        return [.. result];
    }
}

/// <summary>An exponential interpolation (type 2) function.</summary>
public sealed record ExponentialFunction(double[] Domain, double[] Range, double[] C0, double[] C1, double N) : Function
{
    public override double[] Evaluate(IReadOnlyList<double> inputs)
    {
        if (inputs.Count == 0)
            throw new ArgumentException("exponential function requires one input");
        double x = Clamp(inputs[0], Domain[0], Domain[1]);
        int count = Math.Min(C0.Length, C1.Length);
        var results = new double[count];
        for (int idx = 0; idx < count; idx++)
        {
            double value = C0[idx] + Math.Pow(x, N) * (C1[idx] - C0[idx]);
            results[idx] = Clamp(value, Range[idx * 2], Range[idx * 2 + 1]);
        }
        return results;
    }
}

/// <summary>A stitching (type 3) function.</summary>
public sealed record StitchingFunction(double[] Domain, double[] Range, IReadOnlyList<Function> Functions, double[] Bounds, double[] Encode) : Function
{
    public override double[] Evaluate(IReadOnlyList<double> inputs)
    {
        if (inputs.Count == 0)
            throw new ArgumentException("stitching function requires one input");
        double x = Clamp(inputs[0], Domain[0], Domain[1]);
        var segments = new double[Bounds.Length + 2];
        segments[0] = Domain[0];
        Array.Copy(Bounds, 0, segments, 1, Bounds.Length);
        segments[^1] = Domain[1];
        int index = 0;
        for (int idx = 0; idx < segments.Length - 1; idx++)
        {
            if (segments[idx] <= x && x <= segments[idx + 1])
            {
                index = idx;
                break;
            }
        }
        var func = Functions[index];
        double e0 = Encode[index * 2];
        double e1 = Encode[index * 2 + 1];
        double d0 = segments[index];
        double d1 = segments[index + 1];
        double mapped = d1 == d0 ? e0 : e0 + (x - d0) * (e1 - e0) / (d1 - d0);
        return func.Evaluate([mapped]);
    }
}

/// <summary>Base type for shadings.</summary>
public abstract record Shading(ColorSpace ColorSpace, (double T0, double T1)? Domain, Function Function, (bool Start, bool End) Extend);

/// <summary>An axial (type 2) shading between two points.</summary>
public sealed record AxialShading(ColorSpace ColorSpace, double[] Coords, (double T0, double T1)? Domain, Function Function, (bool Start, bool End) Extend)
    : Shading(ColorSpace, Domain, Function, Extend);

/// <summary>A radial (type 3) shading between two circles.</summary>
public sealed record RadialShading(ColorSpace ColorSpace, double[] Coords, (double T0, double T1)? Domain, Function Function, (bool Start, bool End) Extend)
    : Shading(ColorSpace, Domain, Function, Extend);

/// <summary>Base type for patterns.</summary>
public abstract record Pattern;

/// <summary>A tiling pattern: a cell of render commands repeated over the plane.</summary>
public sealed record TilingPattern(
    int PaintType,
    int TilingType,
    double[] BBox,
    double XStep,
    double YStep,
    Matrix Matrix,
    List<RenderCommand> Commands) : Pattern;

/// <summary>A shading pattern.</summary>
public sealed record ShadingPattern(Shading Shading, Matrix Matrix) : Pattern;

/// <summary>A paint in a registered color space.</summary>
public sealed record ColorSpacePaint(string SpaceId, double[] Components)
{
    public bool Equals(ColorSpacePaint? other) =>
        other is not null && SpaceId == other.SpaceId && Components.AsSpan().SequenceEqual(other.Components);

    public override int GetHashCode() => SpaceId.GetHashCode(StringComparison.Ordinal);
}

/// <summary>A paint that references a registered pattern.</summary>
public sealed record PatternPaint(string PatternId, string? BaseSpaceId, double[]? BaseComponents)
{
    public bool Equals(PatternPaint? other) =>
        other is not null && PatternId == other.PatternId && BaseSpaceId == other.BaseSpaceId &&
        (BaseComponents is null ? other.BaseComponents is null : other.BaseComponents is not null && BaseComponents.AsSpan().SequenceEqual(other.BaseComponents));

    public override int GetHashCode() => PatternId.GetHashCode(StringComparison.Ordinal);
}
