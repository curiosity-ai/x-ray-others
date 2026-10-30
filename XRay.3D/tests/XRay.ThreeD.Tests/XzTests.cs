using System.Security.Cryptography;
using System.Text.Json;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Tests;

/// <summary>The xz / LZMA2 decoder against files written by liblzma (python lzma): presets 0 to 9, CRC-32, CRC-64 and no check, lc/lp/pb variants, uncompressed chunks, concatenated streams.</summary>
public class XzTests
{
    private static Dictionary<string, JsonElement> Golden() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(DatasetPaths.CorpusFile("xz-golden.json")))!;

    public static IEnumerable<object[]> Files() => Golden().Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Files))]
    public void DecodesLikeTheReferenceEncoderSaid(string name)
    {
        var expected = Golden()[name];
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/xz/" + name));
        Assert.True(XzDecoder.StartsWithMagic(data));
        var output = XzDecoder.DecodeConcatenated(data, 1 << 26, TestContext.Current.CancellationToken);
        Assert.Equal(expected.GetProperty("length").GetInt32(), output.Length);
        Assert.Equal(expected.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant());
    }

    [Fact]
    public void OutputLimitIsEnforced()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/xz/zeros.xz"));
        Assert.Throws<ModelFormatException>(() => XzDecoder.DecodeConcatenated(data, 1000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TruncationAndMutationOnlyThrowModelFormatException()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/xz/text-p6.xz"));
        for (var n = 0; n < data.Length; n += 41)
        {
            try
            {
                XzDecoder.DecodeConcatenated(data.AsSpan(0, n), 1 << 24, TestContext.Current.CancellationToken);
            }
            catch (ModelFormatException)
            {
            }
        }

        var rng = new Random(5);
        for (var i = 0; i < 300; i++)
        {
            var copy = (byte[])data.Clone();
            copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
            try
            {
                XzDecoder.DecodeConcatenated(copy, 1 << 24, TestContext.Current.CancellationToken);
            }
            catch (ModelFormatException)
            {
            }
        }
    }
}
