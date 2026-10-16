using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using XRay.PS;
using XRay.PS.Imaging;
using XRay.PS.PostScript;
using XRay.PS.Rendering;
using XRay.PS.Skia;
using XRay.PS.Xps;

// psconvert: convert PS/EPS/XPS documents to PDF or raster images, dump the render model,
// and produce the parity outputs compared against the Python reference implementation.

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        psconvert <command> [options]

        Commands:
          pdf <input> <output.pdf> [--fonts DIR] [--no-compression]
          image <input> <output> [--dpi N] [--format png|jpeg|bmp|tiff] [--fonts DIR] [--page N]
          model <input> [--fonts DIR]          print the canonical render model
          info <input>                         page count, sizes and DSC metadata
          eps <image> <output.eps>             wrap a PNG, JPEG, BMP or TIFF image in EPS
          parity <ps|xps> <input> <outbase> [--fonts DIR] [--stages pdf,image]

        --skia draws images (and XPS opacity masks) with the SkiaSharp writer instead of the
        managed rasterizer.
        """;

    private static IRasterWriter? _writer;

    public static int Run(string[] args)
    {
        if (args.Contains("--skia"))
        {
            _writer = new SkiaRasterWriter();
            args = args.Where(a => a != "--skia").ToArray();
        }
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        try
        {
            return args[0] switch
            {
                "pdf" => Pdf(args),
                "image" => Image(args),
                "model" => Model(args),
                "info" => Info(args),
                "eps" => Eps(args),
                "parity" => Parity(args),
                _ => Fail(Usage),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static string? Option(string[] args, string name)
    {
        int idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    private static bool IsXps(string path) => Path.GetExtension(path).Equals(".xps", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".oxps", StringComparison.OrdinalIgnoreCase);

    private static int Pdf(string[] args)
    {
        string input = args[1];
        string output = args[2];
        var options = new PdfSaveOptions { AdditionalFontsFolder = Option(args, "--fonts"), NoCompression = args.Contains("--no-compression"), MaskRasterizer = _writer };
        byte[] pdf = IsXps(input) ? XpsDocument.FromFile(input).ToPdf(options) : PsDocument.FromFile(input).ToPdf(options);
        File.WriteAllBytes(output, pdf);
        return 0;
    }

    private static int Image(string[] args)
    {
        string input = args[1];
        string output = args[2];
        var options = new ImageSaveOptions
        {
            Dpi = int.Parse(Option(args, "--dpi") ?? "96", System.Globalization.CultureInfo.InvariantCulture),
            Format = ImageSaveOptions.ParseFormat(Option(args, "--format") ?? Path.GetExtension(output).TrimStart('.')),
            AdditionalFontsFolder = Option(args, "--fonts"),
            RasterWriter = _writer,
        };
        if (IsXps(input))
        {
            var pages = XpsDocument.FromFile(input).ToImages(options);
            int page = int.Parse(Option(args, "--page") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllBytes(output, pages[page - 1]);
        }
        else
        {
            File.WriteAllBytes(output, PsDocument.FromFile(input).ToImage(options));
        }
        return 0;
    }

    private static int Model(string[] args)
    {
        string input = args[1];
        RenderDocument doc = IsXps(input)
            ? XpsDocument.FromFile(input).BuildRenderModel(forPdf: true)
            : PsDocument.FromFile(input).BuildRenderModel(Option(args, "--fonts"));
        Console.Out.Write(CanonicalDump.Dump(doc));
        Console.Out.WriteLine();
        return 0;
    }

    private static int Eps(string[] args)
    {
        File.WriteAllBytes(args[2], ImageToEps.ConvertFile(args[1]));
        return 0;
    }

    private static int Info(string[] args)
    {
        string input = args[1];
        if (IsXps(input))
        {
            var xps = XpsDocument.FromFile(input);
            Console.WriteLine($"XPS, {xps.Pages.Count} page(s)");
            foreach (var page in xps.Pages)
                Console.WriteLine($"  {page.Width} x {page.Height}");
            return 0;
        }
        var doc = PsDocument.FromFile(input);
        Console.WriteLine(doc.IsEps ? "EPS" : "PostScript");
        Console.WriteLine($"pages: {doc.Pages.Count}");
        if (doc.Dsc is { } dsc)
        {
            Console.WriteLine($"bounding box: {dsc.BoundingBox}");
            Console.WriteLine($"title: {dsc.Title}");
            Console.WriteLine($"creator: {dsc.Creator}");
        }
        return 0;
    }

    private static int Parity(string[] args)
    {
        string kind = args[1];
        string input = args[2];
        string outBase = args[3];
        string? fonts = Option(args, "--fonts");
        var stages = (Option(args, "--stages") ?? "pdf,image").Split(',').ToHashSet();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outBase))!);
        var result = new Dictionary<string, object?> { ["file"] = input, ["kind"] = kind };
        var stageResults = new Dictionary<string, object?>();
        result["stages"] = stageResults;
        var models = new List<string>();

        T? Stage<T>(string name, Func<T> fn) where T : class
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var value = fn();
                stageResults[name] = new Dictionary<string, object?> { ["ok"] = true, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 3) };
                return value;
            }
            catch (Exception ex)
            {
                stageResults[name] = new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"{ex.GetType().Name}: {ex.Message}",
                    ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 3),
                };
                return null;
            }
        }

        if (kind == "ps")
        {
            var doc = Stage("load", () => PsDocument.FromFile(input));
            if (doc is not null)
            {
                result["is_eps"] = doc.IsEps;
                result["page_parts"] = doc.Pages.Count;
                var model = Stage("model", () => PsConverter.BuildRenderModel(doc, fonts));
                if (model is not null)
                    models.Add("## pdf 0\n" + CanonicalDump.Dump(model.Document));
                if (stages.Contains("pdf"))
                {
                    var pdf = Stage("pdf", () => doc.ToPdf(new PdfSaveOptions { AdditionalFontsFolder = fonts }));
                    if (pdf is not null)
                        File.WriteAllBytes(outBase + ".pdf", pdf);
                }
                if (stages.Contains("image"))
                {
                    var png = Stage("image", () => doc.ToImage(new ImageSaveOptions { Format = ImageFormat.Png, Dpi = 96, AdditionalFontsFolder = fonts, RasterWriter = _writer }));
                    if (png is not null)
                        File.WriteAllBytes(outBase + ".png", png);
                }
            }
        }
        else
        {
            var doc = Stage("load", () => XpsDocument.FromFile(input));
            if (doc is not null)
            {
                result["page_parts"] = doc.Pages.Count;
                var model = Stage("model", () => XpsConverter.BuildPdfRenderModel(doc, maskRasterizer: _writer).Document);
                if (model is not null)
                    models.Add("## pdf 0\n" + CanonicalDump.Dump(model));
                if (stages.Contains("pdf"))
                {
                    var pdf = Stage("pdf", () => doc.ToPdf(new PdfSaveOptions { MaskRasterizer = _writer }));
                    if (pdf is not null)
                        File.WriteAllBytes(outBase + ".pdf", pdf);
                }
                if (stages.Contains("image"))
                {
                    var pages = Stage("image", () => doc.ToImages(new ImageSaveOptions { Format = ImageFormat.Png, Dpi = 96, RasterWriter = _writer }, maxPages: 3));
                    if (pages is not null)
                    {
                        result["raster_pages"] = pages.Count;
                        for (int i = 0; i < pages.Count; i++)
                            File.WriteAllBytes(outBase + $".{i + 1}.png", pages[i]);
                    }
                }
            }
        }
        if (models.Count > 0)
        {
            byte[] joined = Encoding.UTF8.GetBytes(string.Join("\n", models));
            using var fs = File.Create(outBase + ".model.txt.gz");
            using var gz = new GZipStream(fs, CompressionLevel.Fastest);
            gz.Write(joined);
        }
        File.WriteAllText(outBase + ".json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
