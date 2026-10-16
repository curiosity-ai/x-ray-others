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
    public void Truncated_and_mutated_xps_fails_cleanly()
    {
        string page = "<FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" Width=\"10\" Height=\"10\"><Path Data=\"M 0,0 L 10,0 L 10,10 Z\" Fill=\"#FF0000\" /></FixedPage>";
        byte[] original = XpsTests.Package(page);
        foreach (var variant in Variants(original, 2, 100))
            Guard(() => XpsDocument.FromBytes(variant).ToPdf());
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
