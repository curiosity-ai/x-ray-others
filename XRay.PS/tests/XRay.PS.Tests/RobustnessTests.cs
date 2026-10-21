// Hostile input: truncated and mutated files may fail a load or a conversion, but only with the
// package's own exception types (PsError, XpsFormatException, HostErrorException) or the
// documented argument errors, never an unrelated runtime exception.

using XRay.PS.Imaging;
using XRay.PS.PostScript;
using XRay.PS.Xps;

namespace XRay.PS.Tests;

public class RobustnessTests
{
    private static bool IsPackageException(Exception ex) =>
        ex is PsError or XpsFormatException or HostErrorException or PsQuit or ArgumentException or InvalidDataException
            or InvalidOperationException or NotSupportedException or System.Xml.XmlException;

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsPackageException(ex))
        {
        }
    }

    private static IEnumerable<byte[]> Variants(byte[] data, int seed, int mutations)
    {
        foreach (double fraction in new[] { 0.0, 0.1, 0.5, 0.9, 0.99 })
            yield return data[..(int)(data.Length * fraction)];
        var random = new Random(seed);
        for (int i = 0; i < mutations; i++)
        {
            var copy = (byte[])data.Clone();
            for (int k = 0; k < 4 && copy.Length > 0; k++)
                copy[random.Next(copy.Length)] = (byte)random.Next(256);
            yield return copy;
        }
    }

    [Fact]
    public void Truncated_and_mutated_postscript_fails_cleanly()
    {
        byte[] original = "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 100 100\n/Helvetica 12 selectfont 10 10 moveto (Hi) show 0 0 moveto 50 50 lineto stroke [1 0 0 1 0 0] concat 1 1 8 [1 0 0 1 0 0] <FF> image showpage\n"u8.ToArray();
        foreach (var variant in Variants(original, 1, 200))
            Guard(() => PsDocument.FromBytes(variant).ToPdf());
    }

    [Fact]
    public void Truncated_and_mutated_dos_eps_fails_cleanly()
    {
        byte[] program = "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 100 100\n0 0 moveto 50 50 lineto stroke showpage\n"u8.ToArray();
        var header = new byte[30];
        BitConverter.GetBytes(0xC6D3D0C5u).CopyTo(header, 0);
        BitConverter.GetBytes(30u).CopyTo(header, 4);
        BitConverter.GetBytes((uint)program.Length).CopyTo(header, 8);
        byte[] original = [.. header, .. program, .. "II*\0"u8, .. new byte[16]];
        foreach (var variant in Variants(original, 4, 200))
            Guard(() => PsDocument.FromBytes(variant).ToPdf());
        // The header fields themselves, mutated: offsets and lengths anywhere in the 32-bit range.
        var random = new Random(5);
        for (int i = 0; i < 200; i++)
        {
            var copy = (byte[])original.Clone();
            for (int k = 4; k < 12; k++)
            {
                if (random.Next(3) == 0)
                    copy[k] = (byte)random.Next(256);
            }
            Guard(() => PsDocument.FromBytes(copy).ToPdf());
        }
    }

    [Fact]
    public void Truncated_and_mutated_pjl_job_fails_cleanly()
    {
        byte[] original = "\u001b%-12345X@PJL JOB\r\n@PJL ENTER LANGUAGE = POSTSCRIPT\r\n%!PS\n0 0 moveto 9 9 lineto stroke showpage\n\u001b%-12345X@PJL EOJ\r\n"u8.ToArray();
        foreach (var variant in Variants(original, 6, 200))
            Guard(() => PsDocument.FromBytes(variant).ToPdf());
    }

    [Fact]
    public void Truncated_and_mutated_xps_fails_cleanly()
    {
        string page = "<FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" Width=\"10\" Height=\"10\"><Path Data=\"M 0,0 L 10,0 L 10,10 Z\" Fill=\"#FF0000\" /></FixedPage>";
        byte[] original = XpsTests.Package(page);
        foreach (var variant in Variants(original, 2, 100))
            Guard(() => XpsDocument.FromBytes(variant).ToPdf());
    }

    [Fact]
    public void Truncated_and_mutated_postscript_with_suppressed_errors_fails_cleanly()
    {
        byte[] original = "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 100 100\n/Helvetica 12 selectfont 10 10 moveto (Hi) show 0 0 moveto 50 50 lineto stroke showpage [1 0 0 1 0 0] concat 1 1 8 [1 0 0 1 0 0] <FF> image showpage\n"u8.ToArray();
        int recovered = 0;
        foreach (var variant in Variants(original, 3, 200))
        {
            var options = new PdfSaveOptions { SuppressErrors = true, Limits = new PsInterpreterLimits { MaxOperators = 1_000_000 } };
            try
            {
                byte[] pdf = PsDocument.FromBytes(variant, TestContext.Current.CancellationToken).ToPdf(options, TestContext.Current.CancellationToken);
                Assert.StartsWith("%PDF", System.Text.Encoding.Latin1.GetString(pdf));
                recovered++;
            }
            catch (Exception ex) when (IsPackageException(ex))
            {
            }
        }

        Assert.True(recovered > 0);
    }

    [Fact]
    public void Truncated_and_mutated_xps_with_suppressed_errors_fails_cleanly()
    {
        string page = "<FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" Width=\"10\" Height=\"10\"><Path Data=\"M 0,0 L 10,0 L 10,10 Z\" Fill=\"#FF0000\" /><Glyphs UnicodeString=\"Hi\" FontUri=\"/x.ttf\" FontRenderingEmSize=\"4\" OriginX=\"1\" OriginY=\"5\" Fill=\"#000000\" /></FixedPage>";
        byte[] original = XpsTests.Package(page);
        foreach (var variant in Variants(original, 4, 100))
        {
            var options = new PdfSaveOptions { SuppressErrors = true };
            Guard(() => XpsDocument.FromBytes(variant).ToPdf(options));
        }
    }

    [Fact]
    public void Malformed_markup_extensions_fail_cleanly()
    {
        const string ns = "http://schemas.microsoft.com/xps/2005/06";
        const string key = "http://schemas.microsoft.com/xps/2005/06/resourcedictionary-key";
        string[] imageSources =
        [
            "{ColorConvertedBitmap ../Resources/Images/red.png", "{ColorConvertedBitmap ../Resources/Images/red.png}", "{ColorConvertedBitmap}",
            "{ColorConvertedBitmap", "{}", "{", "}", "{ColorConvertedBitmap a b c}", "{ColorConvertedBitmap {a} b}", "{ColorConvertedBitmap a} b}",
            "{ColorConvertedBitmap ../Resources/Images/missing.png p.icc}", "{ColorConvertedBitmap ../../../../x.png p.icc}",
            "{ColorConvertedBitmap ../Resources/Images/red.png ../Resources/Images/red.png}", "{colorconvertedbitmap a b}", "{StaticResource x}",
            "ColorConvertedBitmap a b}", "{ColorConvertedBitmap ../Resources/Images/bad.png p.icc}",
        ];
        string[] references = ["{StaticResource missing}", "{StaticResource brush}", "{StaticResource", "{StaticResource}", "{StaticResource geometry"];
        var pages = imageSources.Select(DeviationTests.ImageBrushPage).ToList();
        foreach (string reference in references)
        {
            string resources = $"<FixedPage.Resources><ResourceDictionary><SolidColorBrush x:Key=\"brush\" Color=\"#00000000\" /></ResourceDictionary></FixedPage.Resources>";
            pages.Add($"<FixedPage xmlns=\"{ns}\" xmlns:x=\"{key}\" Width=\"10\" Height=\"10\">{resources}<Canvas Clip=\"{reference}\"><Path Data=\"M 0,0 L 10,0 L 10,10 Z\" Fill=\"#FF0000\" /></Canvas></FixedPage>");
            pages.Add($"<FixedPage xmlns=\"{ns}\" xmlns:x=\"{key}\" Width=\"10\" Height=\"10\">{resources}<Path Clip=\"{reference}\" OpacityMask=\"{reference}\" Data=\"M 0,0 L 10,0 L 10,10 Z\" Fill=\"#FF0000\" /></FixedPage>");
        }
        foreach (string page in pages)
        {
            byte[] package = XpsTests.Package(page, ("Documents/1/Resources/Images/red.png", DeviationTests.RedPng(4)), ("Documents/1/Resources/Images/bad.png", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]));
            Guard(() => XpsDocument.FromBytes(package).ToPdf());
            Guard(() => XpsDocument.FromBytes(package).ToImage(new ImageSaveOptions(ImageFormat.Png, 36)));
            // Suppressed, every malformed reference is skipped and the document still converts.
            byte[] pdf = XpsDocument.FromBytes(package, TestContext.Current.CancellationToken).ToPdf(new PdfSaveOptions { SuppressErrors = true }, null, TestContext.Current.CancellationToken);
            Assert.StartsWith("%PDF", System.Text.Encoding.Latin1.GetString(pdf, 0, 4), StringComparison.Ordinal);
        }
        // A well-formed ColorConvertedBitmap package, truncated and mutated.
        byte[] original = XpsTests.Package(DeviationTests.ImageBrushPage("{ColorConvertedBitmap ../Resources/Images/red.png ../Resources/p.icc}"),
            ("Documents/1/Resources/Images/red.png", DeviationTests.RedPng(4)), ("Documents/1/Resources/p.icc", new byte[16]));
        foreach (var variant in Variants(original, 7, 100))
        {
            Guard(() => XpsDocument.FromBytes(variant).ToPdf());
            Guard(() => XpsDocument.FromBytes(variant).ToPdf(new PdfSaveOptions { SuppressErrors = true }));
        }
    }

    [Theory]
    [InlineData("ps/functional/INTRO01/CIRCLE.PS")]
    [InlineData("ps/integration/minimal.ps")]
    public void Corpus_postscript_survives_truncation(string path)
    {
        byte[] original = File.ReadAllBytes(Corpus.File(path));
        foreach (var variant in Variants(original, 3, 20))
            Guard(() => PsDocument.FromBytes(variant).ToPdf());
    }

    [Fact]
    public void Corpus_xps_survives_truncation_and_mutation()
    {
        byte[] original = File.ReadAllBytes(Corpus.File("xps/integration/Simple.xps"));
        foreach (var variant in Variants(original, 4, 20))
            Guard(() => XpsDocument.FromBytes(variant).ToImage(new ImageSaveOptions(ImageFormat.Png, 36)));
    }

    [Fact]
    public void Image_to_eps_rejects_bad_input_cleanly()
    {
        foreach (var variant in Variants([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 2], 5, 50))
            Guard(() => ImageToEps.Convert(variant));
    }
}
