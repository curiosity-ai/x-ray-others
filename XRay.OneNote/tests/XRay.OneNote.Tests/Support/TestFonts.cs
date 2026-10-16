using XRay.OneNote.Saving;

namespace XRay.OneNote.Tests.Support;

/// <summary>
/// Save options that use only the font bundled with the tests (Liberation Sans, OFL), so PDF output
/// does not depend on the machine's fonts.
/// </summary>
internal static class TestFonts
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fonts");

    public static FontsSubsystem Bundled()
    {
        var fonts = new FontsSubsystem { SearchSystemFontDirectories = false };
        fonts.FontDirectories.Add(Directory);
        return fonts;
    }

    public static PdfSaveOptions Options() => new() { FontsSubsystem = Bundled() };

    public static byte[] ExportPdf(Document document, PdfSaveOptions? options = null)
    {
        using var stream = new MemoryStream();
        document.Save(stream, options ?? Options());
        return stream.ToArray();
    }
}
