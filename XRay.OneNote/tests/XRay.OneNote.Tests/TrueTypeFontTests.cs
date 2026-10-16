using XRay.OneNote.Internal.Pdf;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>TrueType parsing and subsetting for embedded PDF fonts.</summary>
public sealed class TrueTypeFontTests
{
    private static TrueTypeFont Load() => TrueTypeFont.TryLoad(Path.Combine(TestFonts.Directory, "LiberationSans-Regular.ttf"))!;

    [Fact]
    public void Reads_metrics_and_character_map()
    {
        var font = Load();
        Assert.Equal("LiberationSans", font.PostScriptName);
        Assert.Equal(2048, font.UnitsPerEm);
        Assert.NotEqual(0, font.GlyphFor('A'));
        Assert.NotEqual(0, font.GlyphFor('Ж'));
        Assert.Equal(font.GlyphFor(' '), font.GlyphFor(' '));
        Assert.Equal(font.DefaultWidth, font.CharWidth(0x10FFFD));
        Assert.InRange(font.CharWidth('A'), 600, 700);
    }

    [Fact]
    public void Subset_keeps_glyph_ids_and_parses_back()
    {
        var font = Load();
        var glyphs = new[] { font.GlyphFor('A'), font.GlyphFor('Ж'), font.GlyphFor('Å') };
        var subset = font.Subset(glyphs);

        var reparsed = TrueTypeFont.TryParse(subset, requireCmap: false);
        Assert.NotNull(reparsed);
        Assert.Equal(font.GlyphCount, reparsed.GlyphCount);
        Assert.True(subset.Length < new FileInfo(Path.Combine(TestFonts.Directory, "LiberationSans-Regular.ttf")).Length / 4);
        Assert.Equal(font.GlyphWidth(glyphs[1]), reparsed.GlyphWidth(glyphs[1]));
        Assert.Null(TrueTypeFont.TryParse(subset));
    }

    [Fact]
    public void Rejects_files_that_are_not_truetype()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xray-onenote-bad-{Guid.NewGuid():N}.ttf");
        try
        {
            File.WriteAllBytes(path, [0, 1, 0, 0, 0, 9, 0, 0]);
            Assert.Null(TrueTypeFont.TryLoad(path));
            Assert.Null(TrueTypeFont.TryLoad(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
