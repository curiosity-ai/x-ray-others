using XRay.Psd.Imaging;
using XRay.Psd.Descriptors;

namespace XRay.Psd.Resources;

/// <summary>Display unit of a resolution value (resource 1005). The stored value is always pixels per inch.</summary>
public enum PsdResolutionUnit
{
    Unknown = 0,
    PixelsPerInch = 1,
    PixelsPerCentimeter = 2,
}

/// <summary>Display unit of the document width or height in the Image Size dialog (resource 1005).</summary>
public enum PsdDimensionUnit
{
    Unknown = 0,
    Inches = 1,
    Centimeters = 2,
    Points = 3,
    Picas = 4,
    Columns = 5,
}

/// <summary>
/// Print resolution from the <c>ResolutionInfo</c> resource (1005). Both
/// resolutions are in pixels per inch whatever the display units say, as
/// Photoshop reports them (.reference/src/psd/psd_image_resources.cpp,
/// <c>print_settings_from_resolution_resource</c>).
/// </summary>
public sealed record PsdResolutionInfo(
    double HorizontalPpi,
    PsdResolutionUnit HorizontalDisplayUnit,
    PsdDimensionUnit WidthDisplayUnit,
    double VerticalPpi,
    PsdResolutionUnit VerticalDisplayUnit,
    PsdDimensionUnit HeightDisplayUnit);

/// <summary>Guide orientation.</summary>
public enum PsdGuideOrientation
{
    /// <summary>A vertical line at an x position.</summary>
    Vertical = 0,

    /// <summary>A horizontal line at a y position.</summary>
    Horizontal = 1,
}

/// <summary>One ruler guide. <c>Position</c> is in document pixels (stored in 1/32 pixel steps) and may lie outside the canvas.</summary>
public readonly record struct PsdGuide(double Position, PsdGuideOrientation Orientation);

/// <summary>Grid spacing and ruler guides from resource 1032. Grid cycles are in pixels.</summary>
public sealed record PsdGridAndGuides(double HorizontalGridCycle, double VerticalGridCycle, IReadOnlyList<PsdGuide> Guides);

/// <summary>Thumbnail payload encoding.</summary>
public enum PsdThumbnailFormat
{
    /// <summary>Uncompressed rows of 24-bit pixels, <see cref="PsdThumbnail.RowBytes"/> apart.</summary>
    RawRgb = 0,

    /// <summary>A JPEG file (JFIF).</summary>
    Jpeg = 1,
}

/// <summary>
/// The thumbnail Photoshop stores for file browsers: resource 1036, or the
/// Photoshop 4 resource 1033 whose pixels are in BGR order (<c>IsBgr</c>).
/// <c>Data</c> holds the encoded payload; the optional XRay.Psd.Skia package
/// decodes the JPEG form.
/// </summary>
public sealed record PsdThumbnail(
    PsdThumbnailFormat Format,
    int Width,
    int Height,
    int RowBytes,
    int BitsPerPixel,
    bool IsBgr,
    ReadOnlyMemory<byte> Data)
{
    /// <summary>Whether <see cref="Data"/> is a complete JPEG file.</summary>
    public bool IsJpeg => Format == PsdThumbnailFormat.Jpeg;

    /// <summary>
    /// Writes the stored payload to <paramref name="destination"/> unchanged: a JPEG file for
    /// <see cref="PsdThumbnailFormat.Jpeg"/> thumbnails (nothing is decoded or re-encoded),
    /// the pixel rows for raw ones. The stream is not disposed.
    /// </summary>
    public void CopyTo(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        destination.Write(Data.Span);
    }

    /// <summary>
    /// Writes the thumbnail to <paramref name="destination"/> as a JPEG file. A stored JPEG is
    /// copied byte for byte, which is both cheaper and lossless; a raw thumbnail is
    /// encoded at <paramref name="quality"/>. The stream is not disposed.
    /// </summary>
    /// <returns>True when the stored JPEG was copied, false when a raw thumbnail was encoded.</returns>
    public bool WriteJpeg(Stream destination, int quality = 90, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsJpeg)
        {
            destination.Write(Data.Span);
            return true;
        }

        (ToRgba() ?? throw new PsdFormatException("The thumbnail holds no pixels.")).ToJpeg(destination, quality, null, cancellationToken);
        return false;
    }

    /// <summary>
    /// The pixels of a raw thumbnail as RGBA (rows <see cref="RowBytes"/> apart, 24 bits per
    /// pixel, swapped when <see cref="IsBgr"/>); null for a JPEG thumbnail (decode it with
    /// XRay.Psd.Skia) or a payload too short for its dimensions.
    /// </summary>
    public RgbaImage? ToRgba()
    {
        if (IsJpeg || BitsPerPixel != 24 || RowBytes < Width * 3 || Data.Length < (long)RowBytes * (Height - 1) + (Width * 3L))
        {
            return null;
        }

        var image = new RgbaImage(Width, Height);
        var source = Data.Span;
        var target = image.Pixels;
        for (var y = 0; y < Height; y++)
        {
            var row = source.Slice(y * RowBytes, Width * 3);
            var offset = y * Width * 4;
            for (var x = 0; x < Width; x++)
            {
                var r = row[x * 3];
                var g = row[(x * 3) + 1];
                var b = row[(x * 3) + 2];
                target[offset] = IsBgr ? b : r;
                target[offset + 1] = g;
                target[offset + 2] = IsBgr ? r : b;
                target[offset + 3] = 255;
                offset += 4;
            }
        }

        return image;
    }
}

/// <summary>Where a slice came from.</summary>
public enum PsdSliceOrigin
{
    AutoGenerated = 0,
    LayerBased = 1,
    UserGenerated = 2,
}

/// <summary>Slice content type.</summary>
public enum PsdSliceType
{
    NoImage = 0,
    Image = 1,
}

/// <summary>One web slice from resource 1050.</summary>
public sealed record PsdSlice
{
    public int Id { get; init; }

    public int GroupId { get; init; }

    public PsdSliceOrigin Origin { get; init; }

    /// <summary>The layer ID a layer-based slice follows, or 0.</summary>
    public int AssociatedLayerId { get; init; }

    public string Name { get; init; } = string.Empty;

    public PsdSliceType Type { get; init; } = PsdSliceType.Image;

    public PsdRect Bounds { get; init; }

    public string Url { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string AltTag { get; init; } = string.Empty;

    public bool CellTextIsHtml { get; init; }

    public string CellText { get; init; } = string.Empty;

    /// <summary>Cell text alignment: 0 default, 1 left, 2 center, 3 right.</summary>
    public int HorizontalAlignment { get; init; }

    /// <summary>Cell text alignment: 0 default, 1 top, 2 middle, 3 bottom, 4 baseline.</summary>
    public int VerticalAlignment { get; init; }

    /// <summary>Outsets in pixels (version 7 and 8 descriptors only; 0 otherwise).</summary>
    public int TopOutset { get; init; }

    public int LeftOutset { get; init; }

    public int BottomOutset { get; init; }

    public int RightOutset { get; init; }

    /// <summary>Background color with its alpha (0 for none).</summary>
    public PsdColor BackgroundColor { get; init; } = PsdColor.Transparent;
}

/// <summary>
/// The slices resource (1050): the slice group bounds and name, and every slice.
/// <c>Version</c> is 6 for the binary layout, 7 or 8 for the descriptor layout,
/// whose raw descriptor is kept in <c>Descriptor</c>. In the descriptor layout
/// <c>GroupName</c> is the <c>baseName</c> (every corpus file stores "User").
/// </summary>
public sealed record PsdSlices(int Version, PsdRect Bounds, string GroupName, IReadOnlyList<PsdSlice> Slices, Descriptor? Descriptor);

/// <summary>One layer comp from resource 1065. <c>Id</c> is the comp ID (<c>compID</c>).</summary>
public sealed record PsdLayerComp(int Id, string Name, string Comment, bool CapturesVisibility, bool CapturesPosition, bool CapturesAppearance);

/// <summary>The layer comps resource (1065). <c>LastAppliedCompId</c> is the comp last applied in Photoshop, when recorded.</summary>
public sealed record PsdLayerComps(IReadOnlyList<PsdLayerComp> Comps, int? LastAppliedCompId, Descriptor Descriptor);

/// <summary>Print placement style from resource 1062.</summary>
public enum PsdPrintScaleStyle
{
    Centered = 0,
    SizeToFit = 1,
    UserDefined = 2,
}

/// <summary>Print scale and position (resource 1062).</summary>
public sealed record PsdPrintScale(PsdPrintScaleStyle Style, float X, float Y, float Scale);

/// <summary>
/// Version information (resource 1057). <c>HasRealMergedData</c> is false
/// when the file was saved without "Maximize Compatibility".
/// </summary>
public sealed record PsdVersionInfo(int Version, bool HasRealMergedData, string WriterName, string ReaderName, int FileVersion);

/// <summary>
/// The embedded ICC profile (resource 1039): the raw bytes, the description
/// tag (<c>desc</c>), the device color space signature (<c>RGB </c>, <c>GRAY</c>,
/// <c>CMYK</c>...), the profile class (<c>mntr</c>, <c>prtr</c>...) and the
/// major.minor version. The parsed fields are empty (version 0.0) when the
/// profile does not parse.
/// </summary>
public sealed record PsdIccProfile(ReadOnlyMemory<byte> Data, string Description, string ColorSpace, string DeviceClass, Version Version);

/// <summary>One IPTC-NAA dataset (resource 1028), such as 2:120 caption or 2:25 keyword.</summary>
public sealed record PsdIptcDataSet(byte Record, byte DataSet, ReadOnlyMemory<byte> Value)
{
    /// <summary>The value as text: UTF-8 when it decodes cleanly, else Latin-1.</summary>
    public string Text => PsdImageResources.DecodeIptcText(Value.Span);
}
