using System.Security.Cryptography;
using System.Text.Json.Nodes;
using XRay.OneNote.Internal.Pdf;

namespace XRay.OneNote.Tests;

/// <summary>
/// The managed image decoders against Pillow: Goldens/images.json holds small images in each
/// supported encoding with the SHA-256 of the RGB (and alpha) samples Pillow decodes from them.
/// </summary>
public sealed class ImageDecoderTests
{
    private static readonly JsonObject Cases = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Goldens", "images.json")))!.AsObject();

    public static TheoryData<string> RasterCases() => [.. Cases.Select(c => c.Key).Where(key => !key.StartsWith("jpeg", StringComparison.Ordinal))];

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Theory]
    [MemberData(nameof(RasterCases))]
    public void Decodes_like_pillow(string name)
    {
        var expected = Cases[name]!;
        var image = ImageDecoder.Decode(Convert.FromBase64String(expected["data"]!.GetValue<string>()), TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        Assert.Equal(expected["w"]!.GetValue<int>(), image.Width);
        Assert.Equal(expected["h"]!.GetValue<int>(), image.Height);

        var rgb = image.Components == 3 ? image.Pixels! : [.. image.Pixels!.SelectMany(gray => (byte[])[gray, gray, gray])];
        Assert.Equal(expected["rgb"]!.GetValue<string>(), Hash(rgb));

        var alpha = expected["alpha"]?.GetValue<string>();
        if (alpha is null)
        {
            Assert.True(image.Alpha is null || image.Alpha.All(a => a == 255));
        }
        else
        {
            Assert.Equal(alpha, Hash(image.Alpha ?? [.. Enumerable.Repeat((byte)255, image.Width * image.Height)]));
        }
    }

    [Theory]
    [InlineData("jpeg", false)]
    [InlineData("jpeg_cmyk", true)]
    public void Passes_jpeg_through(string name, bool inverted)
    {
        var expected = Cases[name]!;
        var data = Convert.FromBase64String(expected["data"]!.GetValue<string>());
        var image = ImageDecoder.Decode(data, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        Assert.Same(data, image.Jpeg);
        Assert.Equal((expected["w"]!.GetValue<int>(), expected["h"]!.GetValue<int>(), expected["components"]!.GetValue<int>()), (image.Width, image.Height, image.Components));
        Assert.Equal(inverted, image.InvertedCmyk);
    }

    [Fact]
    public void Rejects_unknown_and_damaged_data()
    {
        Assert.Null(ImageDecoder.Decode([], TestContext.Current.CancellationToken));
        Assert.Null(ImageDecoder.Decode("II*\0 not really a tiff"u8.ToArray(), TestContext.Current.CancellationToken));
        foreach (var (name, value) in Cases)
        {
            var data = Convert.FromBase64String(value!["data"]!.GetValue<string>());
            for (var length = 0; length < data.Length; length += 3)
            {
                // Truncated data either decodes (when the tail is not needed) or is rejected; it never throws.
                _ = ImageDecoder.Decode(data[..length], TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public void Fixture_images_decode()
    {
        foreach (var fixture in (string[])["3ImagesWithDifferentAlignment.one", "ImageWithTag.one", "SimpleImageFromSeparateFile.one"])
        {
            foreach (var picture in Support.Fixture.Load(fixture).GetChildNodes<Image>())
            {
                Assert.NotNull(ImageDecoder.Decode(picture.Bytes, TestContext.Current.CancellationToken));
            }
        }
    }
}
