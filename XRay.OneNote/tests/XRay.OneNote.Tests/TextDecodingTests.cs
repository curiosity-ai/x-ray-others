using System.Text;
using XRay.OneNote.Internal.MsOne;
using XRay.OneNote.Internal.OneStore;

namespace XRay.OneNote.Tests;

/// <summary>TextExtendedAscii and UTF-16 decoding (ported from the Python text decoding tests).</summary>
public sealed class TextDecodingTests
{
    private static string? Ascii(byte[] bytes) => OneNoteLoader.DecodeExtendedAscii(PropertyValue.FromBytes(bytes));

    [Fact]
    public void Decodes_cp1252_umlauts() => Assert.Equal("café Äöüß", Ascii([.. "caf"u8, 0xE9, 0x20, 0xC4, 0xF6, 0xFC, 0xDF]));

    [Fact]
    public void Decodes_cp1252_typography() => Assert.Equal("“x” — €", Ascii([0x93, (byte)'x', 0x94, 0x20, 0x97, 0x20, 0x80]));

    [Fact]
    public void Drops_bytes_undefined_in_cp1252() => Assert.Equal("ab", Ascii([(byte)'a', 0x81, (byte)'b', 0x8D]));

    [Fact]
    public void Prefers_genuine_utf8() => Assert.Equal("café", Ascii(Encoding.UTF8.GetBytes("café")));

    [Fact]
    public void Strips_trailing_nul() => Assert.Equal("café", Ascii([.. "caf"u8, 0xE9, 0x00]));

    [Fact]
    public void Empty_bytes_decode_to_null() => Assert.Null(Ascii([]));

    [Fact]
    public void No_value_decodes_to_null() => Assert.Null(OneNoteLoader.DecodeExtendedAscii(PropertyValue.None));

    [Fact]
    public void Utf16_drops_unpaired_surrogates_and_trailing_nuls()
    {
        byte[] bytes = [(byte)'a', 0, 0x00, 0xD8, (byte)'b', 0, 0x3D, 0xD8, 0x00, 0xDE, 0, 0, 0];
        Assert.Equal("ab\U0001F600", TextDecoding.DecodeUtf16(bytes));
    }
}
