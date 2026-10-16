using System.Buffers.Binary;
using System.Diagnostics;
using XRay.Psd.Codecs;
using XRay.Psd.Imaging;
using XRay.Psd.Layers;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// Every load, render, encode and extract entry point takes a token, observes it at the loops
/// that bound its latency, and throws <see cref="OperationCanceledException"/> (never a
/// <see cref="PsdFormatException"/> or an <see cref="AggregateException"/>). A token that is
/// never cancelled changes nothing.
/// </summary>
public sealed class CancellationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CancellationToken Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    /// <summary>A document of <paramref name="layerCount"/> full-canvas raw layers with varied pixels.</summary>
    private static byte[] Layered(int width, int height, int layerCount, bool thumbnail = false)
    {
        var builder = new PsdBuilder { Width = width, Height = height, LayerCompression = PsdCompression.Raw, MergedCompression = PsdCompression.Raw };
        for (var index = 0; index < layerCount; index++)
        {
            var seed = index;
            var layer = new BuilderLayer { Name = $"layer {index}", Rect = new PsdRect(0, 0, width, height), Opacity = (byte)(200 - ((index % 5) * 20)) };
            layer.Channels[-1] = PsdBuilder.Plane8(width, height, 255);
            layer.Channels[0] = PsdBuilder.Plane8(width, height, (x, y) => (byte)((x * 3) + (seed * 17)));
            layer.Channels[1] = PsdBuilder.Plane8(width, height, (x, y) => (byte)((y * 5) + (seed * 31)));
            layer.Channels[2] = PsdBuilder.Plane8(width, height, (x, y) => (byte)(((x ^ y) * 7) + seed));
            builder.Layers.Add(layer);
        }

        for (var channel = 0; channel < 3; channel++)
        {
            builder.MergedChannels.Add(PsdBuilder.Plane8(width, height, (x, y) => (byte)((x + y + channel) * 9)));
        }

        if (thumbnail)
        {
            var pixels = new byte[4 * 3 * 3];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)(i * 11);
            }

            builder.Resources.Add((1036, string.Empty, ThumbnailResource(format: 0, width: 4, height: 3, rowBytes: 12, bits: 24, payload: pixels)));
        }

        return builder.Build();
    }

    private static byte[] ThumbnailResource(uint format, int width, int height, int rowBytes, ushort bits, byte[] payload)
    {
        var data = new byte[28 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, format);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), (uint)rowBytes);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)(rowBytes * height));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(24), bits);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(26), 1);
        payload.CopyTo(data.AsSpan(28));
        return data;
    }

    private static RgbaImage Gradient(int width, int height)
    {
        var image = new RgbaImage(width, height);
        var random = new Random(7);
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            var p = i / 4;
            image.Pixels[i] = (byte)((p % width) + random.Next(8));
            image.Pixels[i + 1] = (byte)((p / width) + random.Next(8));
            image.Pixels[i + 2] = (byte)(p + random.Next(8));
            image.Pixels[i + 3] = 255;
        }

        return image;
    }

    // ---- a cancelled token stops every entry point ----

    [Fact]
    public void A_cancelled_token_stops_loading()
    {
        var token = Cancelled();
        var data = Layered(16, 16, 3);
        Assert.ThrowsAny<OperationCanceledException>(() => PsdDocument.Load(data, null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => PsdDocument.Load(new ReadOnlyMemory<byte>(data), null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => PsdDocument.Load(new MemoryStream(data), null, token));
        var path = Path.Combine(Path.GetTempPath(), $"xray-psd-cancel-{Guid.NewGuid():N}.psd");
        File.WriteAllBytes(path, data);
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => PsdDocument.Load(path, null, token));
            Assert.ThrowsAny<OperationCanceledException>(() => PsdDocument.Load(path, new PsdLoadOptions { MemoryMap = true }, token));
            // A cancelled memory-mapped load leaves nothing open: the file can be deleted at once.
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task A_cancelled_token_stops_async_loading()
    {
        var token = Cancelled();
        var data = Layered(16, 16, 3);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PsdDocument.LoadAsync(new MemoryStream(data), null, token));
        var path = Path.Combine(Path.GetTempPath(), $"xray-psd-cancel-{Guid.NewGuid():N}.psd");
        File.WriteAllBytes(path, data);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PsdDocument.LoadAsync(path, null, token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_cancelled_token_stops_rendering_extraction_and_encoding()
    {
        var token = Cancelled();
        var document = PsdDocument.Load(Layered(24, 24, 3, thumbnail: true));
        var image = Gradient(24, 24);

        Assert.ThrowsAny<OperationCanceledException>(() => document.Render(null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => document.Render(new RenderOptions { Source = RenderSource.Layers }, token));
        Assert.ThrowsAny<OperationCanceledException>(() => document.GetMergedImage(token));
        Assert.ThrowsAny<OperationCanceledException>(() => document.ExtractText(null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => document.RenderBands(null, token).First());
        Assert.ThrowsAny<OperationCanceledException>(() => document.RenderToPng(new MemoryStream(), null, PngCompressionLevel.Default, token));
        Assert.ThrowsAny<OperationCanceledException>(() => document.RenderToJpeg(new MemoryStream(), 80, null, null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => document.Layers[0].GetPixels(token));
        Assert.ThrowsAny<OperationCanceledException>(() => document.Layers[0].Render(token));
        Assert.ThrowsAny<OperationCanceledException>(() => image.ToPng(true, PngCompressionLevel.Default, token));
        Assert.ThrowsAny<OperationCanceledException>(() => image.ToPng(new MemoryStream(), true, PngCompressionLevel.Default, token));
        Assert.ThrowsAny<OperationCanceledException>(() => image.ToJpeg(80, null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => image.ToJpeg(new MemoryStream(), 80, null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => PngEncoder.Encode(image, true, PngCompressionLevel.Default, token));
        Assert.ThrowsAny<OperationCanceledException>(() => JpegEncoder.Encode(image, 80, null, token));
        Assert.NotNull(document.Resources.Thumbnail);
        Assert.ThrowsAny<OperationCanceledException>(() => document.Resources.Thumbnail!.WriteJpeg(new MemoryStream(), 80, token));
    }

    [Fact]
    public void A_cancelled_save_leaves_an_existing_file_alone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xray-psd-save-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => Gradient(8, 8).SavePng(path, true, Cancelled()));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_cancelled_png_render_to_a_file_leaves_no_partial_file()
    {
        var document = PsdDocument.Load(Layered(24, 24, 2));
        var path = Path.Combine(Path.GetTempPath(), $"xray-psd-render-{Guid.NewGuid():N}.png");
        Assert.ThrowsAny<OperationCanceledException>(() => document.RenderToPng(path, null, PngCompressionLevel.Default, Cancelled()));
        Assert.False(File.Exists(path));
    }

    // ---- cancelling part way ----

    [Fact]
    public void Cancelling_between_layers_stops_the_compositor()
    {
        var document = PsdDocument.Load(Layered(64, 64, 12));
        var full = 0;
        document.Render(new RenderOptions { Source = RenderSource.Layers, LayerVisibility = layer => { full++; return layer.IsVisible; } }, CancellationToken.None);

        using var source = new CancellationTokenSource();
        var calls = 0;
        var options = new RenderOptions
        {
            Source = RenderSource.Layers,
            LayerVisibility = layer =>
            {
                if (++calls == 4)
                {
                    source.Cancel();
                }

                return layer.IsVisible;
            },
        };
        Assert.ThrowsAny<OperationCanceledException>(() => document.Render(options, source.Token));
        Assert.True(calls < full, $"{calls} visibility checks after cancelling, {full} for the whole render");
    }

    [Fact]
    public void Cancelling_between_bands_stops_a_band_sequence_and_a_png_render()
    {
        var document = PsdDocument.Load(Layered(32, 60, 3));
        var options = new RenderOptions { Source = RenderSource.Layers, BandHeight = 10 };
        using var source = new CancellationTokenSource();
        var seen = 0;
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            foreach (var band in document.RenderBands(options, source.Token))
            {
                Assert.Equal(seen * 10, band.Top);
                if (++seen == 2)
                {
                    source.Cancel();
                }
            }
        });
        Assert.Equal(2, seen);

        using var pngSource = new CancellationTokenSource();
        var calls = 0;
        var cancelling = new RenderOptions
        {
            Source = RenderSource.Layers,
            BandHeight = 10,
            LayerVisibility = layer =>
            {
                if (++calls == 3)
                {
                    pngSource.Cancel();
                }

                return layer.IsVisible;
            },
        };
        Assert.ThrowsAny<OperationCanceledException>(() => document.RenderToPng(new MemoryStream(), cancelling, PngCompressionLevel.Default, pngSource.Token));
    }

    [Fact]
    public void Cancelling_during_a_large_parse_stops_it_promptly()
    {
        // Many small layer records: the parse walks every one of them.
        var data = ManyLayers(60_000);
        using var source = new CancellationTokenSource();
        source.CancelAfter(TimeSpan.FromMilliseconds(15));
        var watch = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => PsdDocument.Load(data, null, source.Token));
        Assert.True(watch.Elapsed < Bound, $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Cancelling_during_async_loading_stops_the_parse()
    {
        var data = ManyLayers(60_000);
        using var source = new CancellationTokenSource();
        source.CancelAfter(TimeSpan.FromMilliseconds(15));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PsdDocument.LoadAsync(new MemoryStream(data), null, source.Token));
        Assert.True(watch.Elapsed < Bound, $"took {watch.Elapsed}");
    }

    private static byte[] ManyLayers(int count)
    {
        var builder = new PsdBuilder { Width = 8, Height = 8, LayerCompression = PsdCompression.Raw, MergedCompression = PsdCompression.Raw };
        var rect = new PsdRect(0, 0, 1, 1);
        for (var i = 0; i < count; i++)
        {
            var layer = new BuilderLayer { Name = "l", Rect = rect };
            layer.Channels[-1] = [255];
            layer.Channels[0] = [(byte)i];
            layer.Channels[1] = [1];
            layer.Channels[2] = [2];
            builder.Layers.Add(layer);
        }

        for (var channel = 0; channel < 3; channel++)
        {
            builder.MergedChannels.Add(PsdBuilder.Plane8(8, 8, 0));
        }

        return builder.Build();
    }

    [Fact]
    public void A_stream_that_cancels_while_it_is_read_stops_the_load()
    {
        var data = Layered(16, 16, 2);
        using var source = new CancellationTokenSource();
        var stream = new CancellingStream(data, source);
        Assert.ThrowsAny<OperationCanceledException>(() => PsdDocument.Load(stream, null, source.Token));
    }

    private sealed class CancellingStream(byte[] data, CancellationTokenSource source) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Hand out a few bytes, then cancel: the loader must notice before the next read.
            var n = Math.Min(Math.Min(count, 16), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            if (_position >= 16)
            {
                source.Cancel();
            }

            return n;
        }
    }

    // ---- Parallelism.For ----

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_parallel_loop_stops_launching_strips_once_cancelled(int degree)
    {
        using var source = new CancellationTokenSource();
        var strips = 0;
        int total;
        using (Cancellation.Use(source.Token))
        using (Parallelism.Use(degree, minimumStripWork: 1))
        {
            total = Parallelism.StripCount(4000, 1);
            var caught = Assert.ThrowsAny<OperationCanceledException>(() =>
                Parallelism.For(4000, 1, (start, end) =>
                {
                    if (Interlocked.Increment(ref strips) == 3)
                    {
                        source.Cancel();
                    }

                    Thread.Sleep(2);
                }));
            Assert.IsNotType<AggregateException>(caught);
        }

        Assert.True(total > 100);
        Assert.True(strips < (total / 2) + degree, $"{strips} of {total} strips ran");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void A_live_token_leaves_a_parallel_loop_identical(int degree)
    {
        var expected = new int[1000];
        var actual = new int[1000];
        using (Parallelism.Use(degree, minimumStripWork: 1))
        {
            Parallelism.For(1000, 1, (start, end) =>
            {
                for (var i = start; i < end; i++)
                {
                    expected[i] = i * i;
                }
            });
            using var source = new CancellationTokenSource();
            using (Cancellation.Use(source.Token))
            {
                Parallelism.For(1000, 1, (start, end) =>
                {
                    for (var i = start; i < end; i++)
                    {
                        actual[i] = i * i;
                    }
                });
            }
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_cancelled_token_does_not_leak_into_later_calls()
    {
        var document = PsdDocument.Load(Layered(16, 16, 2));
        Assert.ThrowsAny<OperationCanceledException>(() => document.Render(null, Cancelled()));
        // The scope ended with the call: the next render, with no token, runs.
        Assert.Equal(16, document.Render().Width);
        Assert.False(Cancellation.IsActive);
    }

    // ---- results do not depend on the token ----

    [Fact]
    public void A_live_token_does_not_change_any_result()
    {
        var data = Layered(40, 30, 5, thumbnail: true);
        using var source = new CancellationTokenSource();
        var token = source.Token;
        var plain = PsdDocument.Load(data);
        var tokened = PsdDocument.Load(data, null, token);
        Assert.Equal(plain.Layers.Count, tokened.Layers.Count);

        foreach (var kind in new[] { RenderSource.Layers, RenderSource.MergedImage })
        {
            var options = new RenderOptions { Source = kind, BandHeight = 7 };
            Assert.Equal(plain.Render(options).Pixels, tokened.Render(options, token).Pixels);
            Assert.Equal(
                plain.RenderBands(options).SelectMany(b => b.Image.Pixels).ToArray(),
                tokened.RenderBands(options, token).SelectMany(b => b.Image.Pixels).ToArray());
            var a = new MemoryStream();
            var b = new MemoryStream();
            plain.RenderToPng(a, options);
            tokened.RenderToPng(b, options, PngCompressionLevel.Default, token);
            Assert.Equal(a.ToArray(), b.ToArray());
        }

        Assert.Equal(plain.GetMergedImage().Pixels, tokened.GetMergedImage(token).Pixels);
        Assert.Equal(plain.ExtractText().ToString(), tokened.ExtractText(null, token).ToString());
        Assert.Equal(plain.Layers[1].GetPixels()!.Pixels, tokened.Layers[1].GetPixels(token)!.Pixels);
        Assert.Equal(plain.Layers[1].Render().Pixels, tokened.Layers[1].Render(token).Pixels);
    }

    [Fact]
    public async Task Async_loading_with_a_live_token_matches_the_synchronous_load()
    {
        var data = Layered(20, 20, 4);
        using var source = new CancellationTokenSource();
        var loaded = await PsdDocument.LoadAsync(new MemoryStream(data), null, source.Token);
        Assert.Equal(PsdDocument.Load(data).Render().Pixels, loaded.Render().Pixels);
    }

    // ---- stream outputs ----

    [Fact]
    public void Jpeg_written_to_a_stream_equals_the_byte_array()
    {
        var image = Gradient(70, 45);
        foreach (var quality in new[] { 50, 90 })
        {
            using var stream = new NonSeekableStream();
            image.ToJpeg(stream, quality);
            Assert.Equal(image.ToJpeg(quality), stream.ToArray());
            Assert.Equal(image.ToJpeg(quality), JpegEncoder.Encode(image, quality, null, CancellationToken.None));
        }

        // Transparent areas flatten over the background the same way.
        var translucent = new RgbaImage(8, 8, Enumerable.Range(0, 256).Select(i => (byte)(i % 4 == 3 ? 100 : i)).ToArray());
        using var withBackground = new NonSeekableStream();
        translucent.ToJpeg(withBackground, 80, new PsdColor(10, 20, 30));
        Assert.Equal(translucent.ToJpeg(80, new PsdColor(10, 20, 30)), withBackground.ToArray());
    }

    [Fact]
    public void Png_written_to_a_stream_decodes_to_the_same_pixels()
    {
        // Small: one deflate segment, so the bytes are identical too.
        var small = Gradient(33, 17);
        using (var stream = new NonSeekableStream())
        {
            small.ToPng(stream);
            Assert.Equal(small.ToPng(), stream.ToArray());
            Assert.Equal(small.Pixels, PngDecoder.Decode(stream.ToArray()).Pixels);
        }

        // Large: several deflate segments, written as several IDAT chunks.
        var large = Gradient(1100, 900);
        using (var stream = new NonSeekableStream())
        {
            large.ToPng(stream);
            Assert.Equal(large.Pixels, PngDecoder.Decode(stream.ToArray()).Pixels);
            Assert.Equal(large.Pixels, PngDecoder.Decode(large.ToPng()).Pixels);
        }

        // With alpha.
        var translucent = Gradient(20, 10);
        translucent.SetPixel(3, 3, new PsdColor(1, 2, 3, 40));
        using var alpha = new NonSeekableStream();
        translucent.ToPng(alpha);
        Assert.Equal(translucent.Pixels, PngDecoder.Decode(alpha.ToArray()).Pixels);
    }

    [Fact]
    public void RenderToJpeg_matches_encoding_the_rendered_image()
    {
        var document = PsdDocument.Load(Layered(48, 32, 3));
        var options = new RenderOptions { Source = RenderSource.Layers };
        using var stream = new NonSeekableStream();
        document.RenderToJpeg(stream, 85, options);
        Assert.Equal(document.Render(options).ToJpeg(85), stream.ToArray());
    }

    [Fact]
    public void Thumbnails_copy_their_stored_jpeg_and_encode_raw_ones()
    {
        // A raw 4x3 thumbnail encodes to a JPEG.
        var document = PsdDocument.Load(Layered(8, 8, 1, thumbnail: true));
        var raw = document.Resources.Thumbnail!;
        Assert.False(raw.IsJpeg);
        var rgba = raw.ToRgba()!;
        Assert.Equal((4, 3), (rgba.Width, rgba.Height));
        Assert.Equal(new PsdColor(0, 11, 22), rgba.GetPixel(0, 0));
        using var encoded = new NonSeekableStream();
        Assert.False(raw.WriteJpeg(encoded, 80));
        Assert.Equal(rgba.ToJpeg(80), encoded.ToArray());
        using var copied = new NonSeekableStream();
        raw.CopyTo(copied);
        Assert.Equal(raw.Data.ToArray(), copied.ToArray());

        // A stored JPEG is copied byte for byte, with no decode and no re-encode.
        var jpeg = Gradient(16, 12).ToJpeg(70);
        var builder = new PsdBuilder { Width = 4, Height = 4, LayerCompression = PsdCompression.Raw, MergedCompression = PsdCompression.Raw };
        for (var channel = 0; channel < 3; channel++)
        {
            builder.MergedChannels.Add(PsdBuilder.Plane8(4, 4, 0));
        }

        builder.Resources.Add((1036, string.Empty, ThumbnailResource(format: 1, width: 16, height: 12, rowBytes: 48, bits: 24, payload: jpeg)));
        var stored = PsdDocument.Load(builder.Build()).Resources.Thumbnail!;
        Assert.True(stored.IsJpeg);
        Assert.Null(stored.ToRgba());
        using var copy = new NonSeekableStream();
        Assert.True(stored.WriteJpeg(copy));
        Assert.Equal(jpeg, copy.ToArray());
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public byte[] ToArray() => _inner.ToArray();
    }
}
