using System.Runtime.InteropServices;
using XRay.Psd.Imaging;
using XRay.Psd.Rendering;
using XRay.Psd.Resources;
using SkiaSharp;

namespace XRay.Psd.Skia;

/// <summary>Bridges <see cref="RgbaImage"/> and <see cref="PsdDocument"/> to SkiaSharp.</summary>
public static class PsdSkiaExtensions
{
    /// <summary>Copies the image into a new unpremultiplied RGBA8888 <see cref="SKBitmap"/>.</summary>
    public static SKBitmap ToSKBitmap(this RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var destination = bitmap.GetPixels();
        var rowBytes = image.Width * 4;
        if (bitmap.RowBytes == rowBytes)
        {
            Marshal.Copy(image.Pixels, 0, destination, image.Pixels.Length);
        }
        else
        {
            for (var y = 0; y < image.Height; y++)
            {
                Marshal.Copy(image.Pixels, y * rowBytes, destination + (y * bitmap.RowBytes), rowBytes);
            }
        }

        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    /// <summary>Creates an immutable <see cref="SKImage"/> from the pixels.</summary>
    public static SKImage ToSKImage(this RgbaImage image)
    {
        using var bitmap = image.ToSKBitmap();
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>Copies any Skia bitmap into an <see cref="RgbaImage"/> (converting to unpremultiplied RGBA).</summary>
    public static RgbaImage ToRgbaImage(this SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = bitmap.PeekPixels();
            if (pixmap is null || !pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
            {
                throw new InvalidOperationException("Skia could not convert the bitmap to RGBA8888.");
            }
        }
        finally
        {
            handle.Free();
        }

        return new RgbaImage(bitmap.Width, bitmap.Height, pixels);
    }

    /// <summary>Encodes with a Skia codec (PNG, JPEG, WebP; AVIF where the native build supports it).</summary>
    public static byte[] Encode(this RgbaImage image, SKEncodedImageFormat format, int quality = 90)
    {
        using var skImage = image.ToSKImage();
        using var data = skImage.Encode(format, quality) ?? throw new NotSupportedException($"Skia cannot encode {format} on this platform.");
        return data.ToArray();
    }

    /// <summary>
    /// Encodes with a Skia codec into <paramref name="output"/>. The encoder's native buffer is
    /// written to the stream directly, without a managed copy of the compressed image. The token is
    /// checked before the pixels are handed to Skia and again before the encoded bytes are written
    /// (Skia's own encode call cannot be interrupted). The stream is not disposed.
    /// </summary>
    public static void Encode(this RgbaImage image, Stream output, SKEncodedImageFormat format, int quality = 90, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        using var skImage = image.ToSKImage();
        cancellationToken.ThrowIfCancellationRequested();
        using var data = skImage.Encode(format, quality) ?? throw new NotSupportedException($"Skia cannot encode {format} on this platform.");
        cancellationToken.ThrowIfCancellationRequested();
        output.Write(data.AsSpan());
    }

    /// <summary>Encodes with a Skia codec, observing a cancellation token (see <see cref="Encode(RgbaImage, Stream, SKEncodedImageFormat, int, CancellationToken)"/>).</summary>
    public static byte[] Encode(this RgbaImage image, SKEncodedImageFormat format, int quality, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        image.Encode(output, format, quality, cancellationToken);
        return output.ToArray();
    }

    /// <summary>
    /// Decodes the document thumbnail (<see cref="PsdImageResources.Thumbnail"/>):
    /// the JPEG form through Skia, the raw form directly. Photoshop 4 thumbnails
    /// (resource 1033) store BGR and are swapped to RGB. Returns null when the
    /// payload does not decode.
    /// </summary>
    public static RgbaImage? Decode(this PsdThumbnail thumbnail)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);
        RgbaImage? image;
        if (thumbnail.Format == PsdThumbnailFormat.Jpeg)
        {
            using var bitmap = SKBitmap.Decode(thumbnail.Data.Span);
            image = bitmap?.ToRgbaImage();
        }
        else
        {
            image = DecodeRaw(thumbnail);
        }

        if (image is not null && thumbnail.IsBgr)
        {
            var pixels = image.Pixels;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }
        }

        return image;
    }

    private static RgbaImage? DecodeRaw(PsdThumbnail thumbnail)
    {
        var rowBytes = thumbnail.RowBytes > 0 ? thumbnail.RowBytes : thumbnail.Width * 3;
        if (thumbnail.BitsPerPixel != 24 || rowBytes < thumbnail.Width * 3 || (long)rowBytes * thumbnail.Height > thumbnail.Data.Length)
        {
            return null;
        }

        var data = thumbnail.Data.Span;
        var image = new RgbaImage(thumbnail.Width, thumbnail.Height);
        for (var y = 0; y < thumbnail.Height; y++)
        {
            var row = data.Slice(y * rowBytes, thumbnail.Width * 3);
            for (var x = 0; x < thumbnail.Width; x++)
            {
                image.SetPixel(x, y, new PsdColor(row[x * 3], row[(x * 3) + 1], row[(x * 3) + 2]));
            }
        }

        return image;
    }

    /// <summary>Renders the document and returns it as an <see cref="SKBitmap"/>.</summary>
    public static SKBitmap RenderToSKBitmap(this PsdDocument document, RenderOptions? options = null) =>
        document.Render(options).ToSKBitmap();

    /// <summary>Renders the document (see <see cref="PsdDocument.Render(RenderOptions?, CancellationToken)"/>) and returns it as an <see cref="SKBitmap"/>, observing a cancellation token.</summary>
    public static SKBitmap RenderToSKBitmap(this PsdDocument document, RenderOptions? options, CancellationToken cancellationToken) =>
        document.Render(options, cancellationToken).ToSKBitmap();

    /// <summary>Renders the document and encodes it with a Skia codec.</summary>
    public static byte[] RenderAndEncode(this PsdDocument document, SKEncodedImageFormat format, int quality = 90, RenderOptions? options = null) =>
        document.Render(options).Encode(format, quality);

    /// <summary>Renders the document and encodes it with a Skia codec, observing a cancellation token.</summary>
    public static byte[] RenderAndEncode(this PsdDocument document, SKEncodedImageFormat format, int quality, RenderOptions? options, CancellationToken cancellationToken) =>
        document.Render(options, cancellationToken).Encode(format, quality, cancellationToken);

    /// <summary>
    /// Renders the document and encodes it with a Skia codec into <paramref name="output"/> (see
    /// <see cref="Encode(RgbaImage, Stream, SKEncodedImageFormat, int, CancellationToken)"/>). The stream is not disposed.
    /// </summary>
    public static void RenderAndEncode(this PsdDocument document, Stream output, SKEncodedImageFormat format, int quality = 90, RenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Render(options, cancellationToken).Encode(output, format, quality, cancellationToken);
    }
}
