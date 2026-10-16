using XRay.Psd.Imaging;
using XRay.Psd.Tests.Support;
using SkiaSharp;

namespace XRay.Psd.Skia.Tests;

public sealed class SkiaInteropTests
{
    [Fact]
    public void Bitmap_round_trip_preserves_straight_alpha()
    {
        var image = new RgbaImage(3, 2);
        image.SetPixel(0, 0, new PsdColor(255, 0, 0));
        image.SetPixel(1, 0, new PsdColor(10, 200, 30, 128));
        image.SetPixel(2, 1, new PsdColor(1, 2, 3, 255));

        using var bitmap = image.ToSKBitmap();
        Assert.Equal(new SKColor(255, 0, 0, 255), bitmap.GetPixel(0, 0));
        Assert.Equal(image.Pixels, bitmap.ToRgbaImage().Pixels);
    }

    [Fact]
    public void Built_in_png_decodes_identically_in_skia()
    {
        var rendered = Fixtures.Load("photoshop-shape-live-rect.psd").Render();

        using var decoded = SKBitmap.Decode(rendered.ToPng());

        Assert.Equal(rendered.Pixels, decoded.ToRgbaImage().Pixels);
    }

    [Theory]
    [InlineData(95, 3.0)]
    [InlineData(75, 6.0)]
    public void Built_in_jpeg_decodes_in_skia_close_to_the_source(int quality, double maxMeanError)
    {
        var rendered = Fixtures.Load("qual_rca_pinout.psd").Render();

        using var decoded = SKBitmap.Decode(rendered.ToJpeg(quality));

        Assert.NotNull(decoded);
        Assert.Equal(rendered.Width, decoded.Width);
        Assert.Equal(rendered.Height, decoded.Height);
        var pixels = decoded.ToRgbaImage().Pixels;
        double error = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            error += Math.Abs(pixels[i] - rendered.Pixels[i]) + Math.Abs(pixels[i + 1] - rendered.Pixels[i + 1]) + Math.Abs(pixels[i + 2] - rendered.Pixels[i + 2]);
        }

        var mean = error / (pixels.Length / 4 * 3);
        Assert.True(mean < maxMeanError, $"mean error {mean:F2} at quality {quality}");
    }

    [Fact]
    public void Skia_encoders_are_available()
    {
        var document = Fixtures.Load("photoshop-shape-boolean.psd");
        var webp = document.RenderAndEncode(SKEncodedImageFormat.Webp, 90);
        Assert.Equal("RIFF"u8.ToArray(), webp[..4]);
        using var bitmap = document.RenderToSKBitmap();
        Assert.Equal(document.Width, bitmap.Width);
    }

    [Fact]
    public void Jpeg_thumbnail_decodes_and_resembles_the_merged_image()
    {
        var document = Fixtures.Load("photoshop-shape-solid.psd");
        var thumbnail = document.Resources.Thumbnail;
        Assert.NotNull(thumbnail);

        var image = thumbnail.Decode();

        Assert.NotNull(image);
        Assert.Equal((thumbnail.Width, thumbnail.Height), (image.Width, image.Height));

        // The thumbnail is a downscaled, JPEG-compressed copy of the composite over white.
        var merged = document.Render(new Rendering.RenderOptions { Background = PsdColor.White });
        var center = image.GetPixel(image.Width / 2, image.Height / 2);
        var expected = merged.GetPixel(merged.Width / 2, merged.Height / 2);
        Assert.True(Math.Abs(center.R - expected.R) + Math.Abs(center.G - expected.G) + Math.Abs(center.B - expected.B) < 30, $"{center} vs {expected}");
    }

    [Fact]
    public void Raw_bgr_thumbnail_decodes_to_rgb()
    {
        var thumbnail = new Resources.PsdThumbnail(Resources.PsdThumbnailFormat.RawRgb, 2, 1, 8, 24, IsBgr: true, new byte[] { 1, 2, 3, 4, 5, 6, 0, 0 });

        var image = thumbnail.Decode();

        Assert.NotNull(image);
        Assert.Equal(new PsdColor(3, 2, 1), image.GetPixel(0, 0));
        Assert.Equal(new PsdColor(6, 5, 4), image.GetPixel(1, 0));
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    public void Encoding_to_a_stream_equals_the_byte_array(SKEncodedImageFormat format)
    {
        var image = new RgbaImage(40, 30);
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = (byte)(i / 4);
            image.Pixels[i + 1] = (byte)(i / 8);
            image.Pixels[i + 2] = 90;
            image.Pixels[i + 3] = 255;
        }

        using var stream = new MemoryStream();
        image.Encode(stream, format, 80);

        Assert.Equal(image.Encode(format, 80), stream.ToArray());
        Assert.Equal(image.Encode(format, 80), image.Encode(format, 80, CancellationToken.None));
    }

    [Fact]
    public void A_cancelled_token_stops_skia_rendering_and_encoding()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var document = Fixtures.Load("photoshop-shape-solid.psd");
        var image = new RgbaImage(4, 4);

        Assert.ThrowsAny<OperationCanceledException>(() => document.RenderToSKBitmap(null, source.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => document.RenderAndEncode(SKEncodedImageFormat.Png, 90, null, source.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => document.RenderAndEncode(new MemoryStream(), SKEncodedImageFormat.Png, 90, null, source.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => image.Encode(new MemoryStream(), SKEncodedImageFormat.Png, 90, source.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => image.Encode(SKEncodedImageFormat.Png, 90, source.Token));
    }

    [Fact]
    public void Rendering_into_a_stream_matches_rendering_to_bytes()
    {
        var document = Fixtures.Load("photoshop-shape-solid.psd");
        using var stream = new MemoryStream();
        document.RenderAndEncode(stream, SKEncodedImageFormat.Png);
        Assert.Equal(document.RenderAndEncode(SKEncodedImageFormat.Png), stream.ToArray());
    }

    [Fact]
    public void The_core_thumbnail_writer_agrees_with_the_skia_decoder_on_raw_thumbnails()
    {
        var thumbnail = new Resources.PsdThumbnail(Resources.PsdThumbnailFormat.RawRgb, 2, 1, 8, 24, IsBgr: true, new byte[] { 1, 2, 3, 4, 5, 6, 0, 0 });
        Assert.Equal(thumbnail.Decode()!.Pixels, thumbnail.ToRgba()!.Pixels);
    }

    [Fact]
    public void A_stored_jpeg_thumbnail_is_copied_without_re_encoding()
    {
        var document = Fixtures.Load("photoshop-shape-solid.psd");
        var thumbnail = document.Resources.Thumbnail!;
        using var stream = new MemoryStream();
        Assert.True(thumbnail.WriteJpeg(stream, 10));
        Assert.Equal(thumbnail.Data.ToArray(), stream.ToArray());
        using var decoded = SKBitmap.Decode(stream.ToArray());
        Assert.Equal((thumbnail.Width, thumbnail.Height), (decoded.Width, decoded.Height));
    }
}
