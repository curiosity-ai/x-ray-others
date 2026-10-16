using XRay.PS.Rendering;

namespace XRay.PS.Imaging;

/// <summary>Renders a render document to encoded raster bytes.</summary>
public interface IRasterWriter
{
    /// <summary>Renders the document's first page and encodes it in <see cref="ImageSaveOptions.Format"/>.</summary>
    byte[] Write(RenderDocument document, ImageSaveOptions options);
}
