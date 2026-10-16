using XRay.Psd.Imaging;
using XRay.Psd.IO;
using XRay.Psd.Layers;
using XRay.Psd.Rendering;
using XRay.Psd.Text;

namespace XRay.Psd;

/// <summary>Options for <see cref="PsdDocument.Load(string, PsdLoadOptions?)"/>.</summary>
public sealed class PsdLoadOptions
{
    /// <summary>Parse layer records. Turn off to read only the header, resources and merged image.</summary>
    public bool ReadLayers { get; init; } = true;

    /// <summary>Parse embedded smart-object files from the global link blocks.</summary>
    public bool ReadLinkedFiles { get; init; } = true;

    /// <summary>
    /// Convert gray, RGB, indexed and CMYK pixels to sRGB through the document's
    /// embedded ICC profile (resource 1039), as Photoshop displays them. Turn off
    /// to treat RGB and gray as sRGB and use the naive CMYK formula. Documents
    /// without a usable profile convert the same way either way.
    /// </summary>
    public bool ColorManagement { get; init; } = true;

    /// <summary>
    /// Map the file into memory instead of reading it into a managed array (the path
    /// overloads only). Pages load when parsing or rendering touches them, so opening a
    /// large document reads little more than its layer records. The document then holds
    /// the file open until it is disposed. Files too large for one array (2 GB and up)
    /// are always mapped.
    /// </summary>
    public bool MemoryMap { get; init; }
}

/// <summary>
/// A Photoshop document (PSD or PSB) parsed into memory. Pixel data stays
/// compressed until rendering or <see cref="PsdLayer.GetPixels()"/> decodes it,
/// so text extraction and inspection stay cheap.
/// </summary>
/// <remarks>
/// Disposing matters only for memory-mapped documents (<see cref="PsdLoadOptions.MemoryMap"/>,
/// or files of 2 GB and more): it closes the mapping, after which reading pixel data
/// throws <see cref="ObjectDisposedException"/>. For documents loaded into memory it does nothing.
/// </remarks>
public sealed class PsdDocument : IDisposable
{
    private readonly List<PsdLayer> _layers = [];
    private readonly List<PsdLayer> _rootLayers = [];

    internal PsdDocument()
    {
    }

    /// <summary>File format version: 1 for PSD, 2 for PSB (large document).</summary>
    public int Version { get; internal set; }

    public bool IsLargeDocument => Version == 2;

    public int Width { get; internal set; }

    public int Height { get; internal set; }

    /// <summary>Total stored channels in the merged image, including alpha channels.</summary>
    public int ChannelCount { get; internal set; }

    /// <summary>Bits per channel: 1, 8, 16 or 32.</summary>
    public int Depth { get; internal set; }

    public PsdColorMode ColorMode { get; internal set; }

    public PsdRect Bounds => new(0, 0, Width, Height);

    /// <summary>Raw color mode data (the indexed palette, or duotone specification).</summary>
    public ReadOnlyMemory<byte> ColorModeData { get; internal set; }

    /// <summary>The 256-entry palette for indexed documents.</summary>
    public PsdColor[]? Palette { get; internal set; }

    public IReadOnlyList<ImageResource> ImageResources { get; internal set; } = [];

    private Resources.PsdImageResources? _resources;

    /// <summary>Typed, lazily parsed views of the common image resources (resolution, guides, thumbnail, slices, layer comps, metadata).</summary>
    public Resources.PsdImageResources Resources => LazyInitializer.EnsureInitialized(ref _resources, () => new Resources.PsdImageResources(this));

    /// <summary>Every layer record in file order (bottom to top), including group records but not the hidden group-end dividers.</summary>
    public IReadOnlyList<PsdLayer> Layers => _layers;

    /// <summary>Top-level layers and groups, bottom to top.</summary>
    public IReadOnlyList<PsdLayer> RootLayers => _rootLayers;

    /// <summary>Document-level tagged blocks that follow the layer info (<c>Txt2</c>, <c>Patt</c>, <c>lnk2</c>...).</summary>
    public IReadOnlyList<TaggedBlock> GlobalTaggedBlocks { get; internal set; } = [];

    /// <summary>Embedded and linked smart-object source files.</summary>
    public IReadOnlyList<LinkedFile> LinkedFiles { get; internal set; } = [];

    /// <summary>Whether the first extra merged channel is document transparency (negative layer count flag).</summary>
    public bool MergedImageHasTransparency { get; internal set; }

    /// <summary>
    /// False when Photoshop saved without "Maximize Compatibility": the merged
    /// image section then holds a placeholder rather than the real composite
    /// (version-info resource 1057).
    /// </summary>
    public bool HasRealMergedImage { get; internal set; } = true;

    internal ReadOnlyMemory<byte> MergedImageData { get; set; }

    /// <summary>The image data section, wherever it lives (a file range past one span on large PSB files).</summary>
    internal ByteRange MergedImage { get; set; }

    internal ReadOnlyMemory<byte> FileData { get; set; }

    /// <summary>The memory-mapped file behind the document, released by <see cref="Dispose"/>.</summary>
    internal IO.PsdSource? Source { get; set; }

    /// <summary>Global light angle in degrees (resource 1037), used by effects with "Use Global Light".</summary>
    public float GlobalLightAngle { get; internal set; } = 120f;

    /// <summary>Global light altitude in degrees (resource 1049).</summary>
    public float GlobalLightAltitude { get; internal set; } = 30f;

    /// <summary>Raw global layer mask info.</summary>
    public ReadOnlyMemory<byte> GlobalLayerMaskInfo { get; internal set; }

    private Dictionary<string, Rendering.PatternTile>? _patterns;

    /// <summary>Decoded pattern tiles from the global pattern blocks, keyed by pattern ID (decoded on first use).</summary>
    internal Dictionary<string, Rendering.PatternTile> Patterns => _patterns ??= Rendering.PatternStore.Parse(this);

    private TextEngineBlock? _textEngine;
    private bool _textEngineParsed;

    /// <summary>
    /// The document-level text engine block (<c>Txt2</c>) with one text object per
    /// type layer, parsed on first use; null when the document has none or it does not decode.
    /// </summary>
    public TextEngineBlock? TextEngine
    {
        get
        {
            if (!_textEngineParsed)
            {
                _textEngine = TextEngineResolver.ParseBlock(this);
                _textEngineParsed = true;
            }

            return _textEngine;
        }
    }

    private System.Runtime.CompilerServices.StrongBox<Imaging.Icc.IccSrgbTransform?>? _colorTransform;

    /// <summary>Whether pixel decoding uses the embedded ICC profile (<see cref="PsdLoadOptions.ColorManagement"/>).</summary>
    internal bool ColorManagementEnabled { get; set; } = true;

    /// <summary>The cached embedded-profile-to-sRGB transform, or null when none applies (built on first use).</summary>
    internal Imaging.Icc.IccSrgbTransform? ColorTransform =>
        LazyInitializer.EnsureInitialized(ref _colorTransform, () => new(ColorManagementEnabled ? Imaging.Icc.IccSrgbTransform.ForDocument(this) : null)).Value;

    private System.Runtime.CompilerServices.StrongBox<Imaging.Icc.IccInkSpace?>? _inkSpace;

    /// <summary>The cached device-channel space the adjustment layers of CMYK, gray and duotone documents run in (built on first use).</summary>
    internal Imaging.Icc.IccInkSpace? InkSpace =>
        LazyInitializer.EnsureInitialized(ref _inkSpace, () => new(Imaging.Icc.IccInkSpace.ForDocument(this))).Value;

    private DocumentColors? _colors;

    /// <summary>Converts descriptor, text and pattern colors the same way as the pixels (see <see cref="DocumentColors"/>).</summary>
    internal DocumentColors Colors => LazyInitializer.EnsureInitialized(ref _colors, () => new DocumentColors(this));

    internal void AddLayer(PsdLayer layer) => _layers.Add(layer);

    internal void AddRootLayer(PsdLayer layer) => _rootLayers.Add(layer);

    /// <summary>Loads a file. See <see cref="PsdLoadOptions.MemoryMap"/> for mapping instead of reading it.</summary>
    public static PsdDocument Load(string path, PsdLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new PsdLoadOptions();
        if (options.MemoryMap || new FileInfo(path).Length > Array.MaxLength)
        {
            return LoadMapped(path, options);
        }

        return Load(File.ReadAllBytes(path), options);
    }

    /// <summary>
    /// Loads a file, observing a cancellation token: while the file is read and at every layer
    /// record, tagged block and channel the parser walks. A cancelled load throws
    /// <see cref="OperationCanceledException"/> and releases a memory mapping it had opened.
    /// </summary>
    public static PsdDocument Load(string path, PsdLoadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = Cancellation.Use(cancellationToken);
        options ??= new PsdLoadOptions();
        if (options.MemoryMap || new FileInfo(path).Length > Array.MaxLength)
        {
            return LoadMapped(path, options);
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Load(StreamLoader.ReadAll(file, cancellationToken), options, cancellationToken);
    }

    /// <summary>
    /// Loads from a stream, reading it from its current position to the end. A seekable
    /// stream is read straight into one array of the remaining length; other streams are
    /// buffered once. The stream is not disposed.
    /// </summary>
    public static PsdDocument Load(Stream stream, PsdLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Load(StreamLoader.ReadAll(stream), options);
    }

    /// <summary>Loads from a stream (see <see cref="Load(Stream, PsdLoadOptions?)"/>), observing a cancellation token while reading and parsing.</summary>
    public static PsdDocument Load(Stream stream, PsdLoadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return Load(StreamLoader.ReadAll(stream, cancellationToken), options, cancellationToken);
    }

    public static PsdDocument Load(byte[] data, PsdLoadOptions? options = null) => Load(new ReadOnlyMemory<byte>(data), options);

    /// <summary>Loads from bytes, observing a cancellation token while parsing.</summary>
    public static PsdDocument Load(byte[] data, PsdLoadOptions? options, CancellationToken cancellationToken) =>
        Load(new ReadOnlyMemory<byte>(data), options, cancellationToken);

    public static PsdDocument Load(ReadOnlyMemory<byte> data, PsdLoadOptions? options = null)
    {
        options ??= new PsdLoadOptions();
        return PsdParser.Parse(data, options);
    }

    /// <summary>Loads from memory, observing a cancellation token at every layer record, tagged block and channel the parser walks.</summary>
    public static PsdDocument Load(ReadOnlyMemory<byte> data, PsdLoadOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = Cancellation.Use(cancellationToken);
        return Load(data, options);
    }

    /// <summary>
    /// Reads a file asynchronously, then parses it. Parsing reads only the structure
    /// (pixel data stays compressed), so it runs on the calling thread after the read.
    /// With <see cref="PsdLoadOptions.MemoryMap"/> (or a file of 2 GB and more) the file is
    /// mapped instead, which involves no bulk read.
    /// </summary>
    public static async Task<PsdDocument> LoadAsync(string path, PsdLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new PsdLoadOptions();
        if (options.MemoryMap || new FileInfo(path).Length > Array.MaxLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Load(path, options, cancellationToken);
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            var data = await StreamLoader.ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
            return Load(data, options, cancellationToken);
        }
    }

    /// <summary>Reads a stream asynchronously from its current position to the end, then parses it. The stream is not disposed.</summary>
    public static async Task<PsdDocument> LoadAsync(Stream stream, PsdLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var data = await StreamLoader.ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        return Load(data, options, cancellationToken);
    }

    private static PsdDocument LoadMapped(string path, PsdLoadOptions options)
    {
        var source = IO.MappedFileSource.Open(path);
        try
        {
            var document = PsdParser.Parse(source, options);
            document.ColorManagementEnabled = options.ColorManagement;
            document.Source = source;
            return document;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Releases the memory mapping of a mapped document; does nothing for documents loaded into memory.</summary>
    public void Dispose() => Source?.Dispose();

    /// <summary>True when the bytes start with the <c>8BPS</c> signature.</summary>
    public static bool IsPsd(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == (byte)'8' && data[1] == (byte)'B' && data[2] == (byte)'P' && data[3] == (byte)'S';

    public ImageResource? GetImageResource(ushort id)
    {
        foreach (var resource in ImageResources)
        {
            if (resource.Id == id)
            {
                return resource;
            }
        }

        return null;
    }

    public TaggedBlock? GetGlobalTaggedBlock(string key)
    {
        foreach (var block in GlobalTaggedBlocks)
        {
            if (block.Key == key)
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>Iterates the layer tree top to bottom (the order the Layers panel shows), depth first.</summary>
    public IEnumerable<PsdLayer> EnumerateLayersTopDown()
    {
        static IEnumerable<PsdLayer> Walk(IReadOnlyList<PsdLayer> layers)
        {
            for (var i = layers.Count - 1; i >= 0; i--)
            {
                yield return layers[i];
                foreach (var child in Walk(layers[i].Children))
                {
                    yield return child;
                }
            }
        }

        return Walk(_rootLayers);
    }

    /// <summary>Decodes the merged (flattened) image Photoshop stored in the file.</summary>
    public RgbaImage GetMergedImage() => MergedImageDecoder.Decode(this).ToRgba(Bounds);

    /// <summary>Decodes the merged image (see <see cref="GetMergedImage()"/>), observing a cancellation token while the channels decode.</summary>
    public RgbaImage GetMergedImage(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = Cancellation.Use(cancellationToken);
        return GetMergedImage();
    }

    /// <summary>Renders the document to 8-bit RGBA. See <see cref="RenderOptions"/> for the source choice.</summary>
    public RgbaImage Render(RenderOptions? options = null) => PsdRenderer.Render(this, options ?? new RenderOptions());

    /// <summary>
    /// Renders the document (see <see cref="Render(RenderOptions?)"/>), observing a cancellation
    /// token: between layers, between the strips of every parallel pass (no new strip starts once
    /// the token is cancelled) and while channels decode. A cancelled render throws
    /// <see cref="OperationCanceledException"/>; the pixels of a render that completes do not
    /// depend on the token.
    /// </summary>
    public RgbaImage Render(RenderOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = Cancellation.Use(cancellationToken);
        return PsdRenderer.Render(this, options ?? new RenderOptions());
    }

    /// <summary>
    /// Renders the document in horizontal bands of <see cref="RenderOptions.BandHeight"/> rows,
    /// top to bottom, each rendered when the sequence reaches it. The bands' pixels are
    /// identical to the same rows of <see cref="Render(RenderOptions?)"/>, but only one band of the canvas is
    /// held at a time, so documents too large for <see cref="Render(RenderOptions?)"/> (past about 46000 x 46000
    /// pixels, or too large for memory) can render. Layers with effects, groups with effects,
    /// feathered masks, stroked shapes and re-rendered type still work on their whole extent.
    /// </summary>
    public IEnumerable<RenderBand> RenderBands(RenderOptions? options = null) => PsdRenderer.RenderBands(this, options ?? new RenderOptions());

    /// <summary>
    /// Renders band by band (see <see cref="RenderBands(RenderOptions?)"/>), observing a
    /// cancellation token while each band renders and between bands. The token is read when
    /// the sequence is enumerated, so a sequence can be cancelled between bands by cancelling
    /// the token passed here.
    /// </summary>
    public IEnumerable<RenderBand> RenderBands(RenderOptions? options, CancellationToken cancellationToken) =>
        PsdRenderer.RenderBands(this, options ?? new RenderOptions(), cancellationToken);

    /// <summary>
    /// Renders the document band by band (<see cref="RenderBands(RenderOptions?)"/>) straight into a PNG
    /// stream, holding one band and the deflate segments in progress. The image is RGB when it
    /// is opaque by construction (a <see cref="RenderOptions.Background"/>, or a merged image
    /// without transparency) and RGBA otherwise. The stream is not disposed.
    /// </summary>
    public void RenderToPng(Stream output, RenderOptions? options = null, Codecs.PngCompressionLevel level = Codecs.PngCompressionLevel.Default)
    {
        ArgumentNullException.ThrowIfNull(output);
        PsdRenderer.RenderToPng(this, output, options ?? new RenderOptions(), level);
    }

    /// <summary>
    /// Renders band by band into a PNG stream (see <see cref="RenderToPng(Stream, RenderOptions?, Codecs.PngCompressionLevel)"/>),
    /// observing a cancellation token. A cancelled render stops with the PNG incomplete in
    /// <paramref name="output"/>; discard it.
    /// </summary>
    public void RenderToPng(Stream output, RenderOptions? options, Codecs.PngCompressionLevel level, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = Cancellation.Use(cancellationToken);
        PsdRenderer.RenderToPng(this, output, options ?? new RenderOptions(), level);
    }

    /// <summary>
    /// Renders the document into a JPEG stream. The document is rendered once (with
    /// <see cref="Render(RenderOptions?, CancellationToken)"/>) and the compressed bytes are
    /// written to <paramref name="output"/> as they are produced. Transparent areas are
    /// flattened over <paramref name="background"/> (white by default); a
    /// <see cref="RenderOptions.Background"/> takes precedence. The stream is not disposed.
    /// </summary>
    public void RenderToJpeg(Stream output, int quality = 90, RenderOptions? options = null, PsdColor? background = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = Cancellation.Use(cancellationToken);
        Render(options).ToJpeg(output, quality, background, cancellationToken);
    }

    /// <summary>Renders the document band by band into a PNG file (see <see cref="RenderToPng(Stream, RenderOptions?, Codecs.PngCompressionLevel)"/>).</summary>
    public void RenderToPng(string path, RenderOptions? options = null, Codecs.PngCompressionLevel level = Codecs.PngCompressionLevel.Default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        RenderToPng(file, options, level);
    }

    /// <summary>
    /// Renders band by band into a PNG file, observing a cancellation token. A cancelled
    /// render deletes the partial file it created.
    /// </summary>
    public void RenderToPng(string path, RenderOptions? options, Codecs.PngCompressionLevel level, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        var completed = false;
        try
        {
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                RenderToPng(file, options, level, cancellationToken);
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // The failure being reported matters more than the leftover file.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>Extracts every piece of text: layer and group names, type-layer content, channel, path and slice names, and text inside embedded smart objects.</summary>
    public PsdTextContent ExtractText(TextExtractionOptions? options = null) => TextExtractor.Extract(this, options ?? new TextExtractionOptions());

    /// <summary>Extracts every piece of text (see <see cref="ExtractText(TextExtractionOptions?)"/>), observing a cancellation token per layer and per embedded smart object.</summary>
    public PsdTextContent ExtractText(TextExtractionOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = Cancellation.Use(cancellationToken);
        return TextExtractor.Extract(this, options ?? new TextExtractionOptions());
    }

    public override string ToString() => $"{(IsLargeDocument ? "PSB" : "PSD")} {Width}x{Height} {ColorMode} {Depth}-bit, {_layers.Count} layers";
}
