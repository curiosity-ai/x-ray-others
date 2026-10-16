using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.Imaging;

/// <summary>Renders a render document to encoded raster bytes.</summary>
public interface IRasterWriter
{
    /// <summary>Renders the document's first page and encodes it in <see cref="ImageSaveOptions.Format"/>.</summary>
    byte[] Write(RenderDocument document, ImageSaveOptions options);

    /// <summary>As <see cref="Write(RenderDocument, ImageSaveOptions)"/>, observing a cancellation
    /// token. The default publishes the token to the built-in decoders and renderer; a writer
    /// with its own rendering loops overrides it.</summary>
    byte[] Write(RenderDocument document, ImageSaveOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var guard = Guard.Begin(cancellationToken);
        return Write(document, options);
    }

    /// <summary>Renders the first page and writes the encoded image to <paramref name="output"/>.
    /// The default buffers the encoded image; writers that can stream override it.</summary>
    void Write(RenderDocument document, ImageSaveOptions options, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        byte[] bytes = Write(document, options, cancellationToken);
        output.Write(bytes);
    }
}
