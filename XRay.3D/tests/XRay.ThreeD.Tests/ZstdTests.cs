using System.Security.Cryptography;
using System.Text.Json;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Tests;

/// <summary>The Zstandard decoder against files written by the python zstandard package (levels -5 to 19, with and without content size, checksums, several frames).</summary>
public class ZstdTests
{
    private static Dictionary<string, JsonElement> Golden() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(DatasetPaths.CorpusFile("zstd-golden.json")))!;

    public static IEnumerable<object[]> Files() => Golden().Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Files))]
    public void DecodesLikeTheReferenceEncoderSaid(string name)
    {
        var expected = Golden()[name];
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/zstd/" + name));
        Assert.True(ZstdDecoder.IsZstd(data));
        var output = ZstdDecoder.Decompress(data, 1 << 26, TestContext.Current.CancellationToken);
        Assert.Equal(expected.GetProperty("length").GetInt32(), output.Length);
        Assert.Equal(expected.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant());
    }

    [Fact]
    public void OutputLimitIsEnforced()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/zstd/zeros.zst"));
        Assert.Throws<ModelFormatException>(() => ZstdDecoder.Decompress(data, 1000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TruncationAndMutationOnlyThrowModelFormatException()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/zstd/text-l3.zst"));
        for (var n = 0; n < data.Length; n += 37)
        {
            try
            {
                ZstdDecoder.Decompress(data.AsSpan(0, n), 1 << 24, TestContext.Current.CancellationToken);
            }
            catch (ModelFormatException)
            {
            }
        }

        var random = new Random(7);
        for (var i = 0; i < 400; i++)
        {
            var copy = (byte[])data.Clone();
            for (var k = 0; k < 3; k++)
            {
                copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
            }

            try
            {
                ZstdDecoder.Decompress(copy, 1 << 24, TestContext.Current.CancellationToken);
            }
            catch (ModelFormatException)
            {
            }
        }
    }

    [Fact]
    public void Cancellation()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/zstd/text-l3.zst"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ZstdDecoder.Decompress(data, 1 << 24, cts.Token));
    }
}
