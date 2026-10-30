using System.Text.Json;
using XRay.ThreeD;

// xray3d info <file> [--json]      metadata
// xray3d convert <file> [out.glb] [--compress none|quantize|meshopt] [--edges none|auto|all]
//                                  convert to binary glTF
// xray3d package <file> <out-dir|out.zip> [--compress ...]  assembly.json + one GLB per unique part
// xray3d formats                   list known formats
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: xray3d info <file> [--json] | convert <file> [out.glb] | formats");
    return 2;
}

try
{
    switch (args[0])
    {
        case "formats":
            foreach (var f in ModelFormats.All)
            {
                Console.WriteLine($"{f.Id,-12} {f.Support,-10} {string.Join(' ', f.Extensions),-30} {f.Name}");
            }

            return 0;
        case "info" when args.Length >= 2:
            {
                var meta = ModelLoader.ReadMetadata(args[1], Loose(args[1]));
                if (args.Contains("--json"))
                {
                    Console.WriteLine(JsonSerializer.Serialize(Summary(meta), new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    Print(meta);
                }

                return 0;
            }

        case "package" when args.Length >= 3:
            {
                var model = ModelLoader.Load(args[1], Loose(args[1]));
                var glb = new GlbWriteOptions();
                if (Option(args, "--compress") is { } c)
                {
                    glb.Compression = Enum.Parse<GlbCompression>(c, ignoreCase: true);
                }

                var package = AssemblyPackage.Create(model, new AssemblyPackageOptions { Glb = glb });
                if (args[2].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllBytes(args[2], package.ToZip());
                }
                else
                {
                    package.WriteToDirectory(args[2]);
                }

                Console.WriteLine($"{args[2]}: {package.Files.Count - 1} parts, manifest {package.Files[AssemblyPackage.ManifestName].Length:N0} bytes, total {package.Files.Values.Sum(f => (long)f.Length):N0} bytes");
                return 0;
            }

        case "convert" when args.Length >= 2:
            {
                var model = ModelLoader.Load(args[1], Loose(args[1]));
                var positional = args.Skip(2).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
                var output = positional.Count > 0 ? positional[0] : Path.ChangeExtension(args[1], model.Is2D ? ".svg" : ".glb");
                if (output.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    // Drawings (DXF, ...) can be written as SVG; --flat draws the top view of a 3D model.
                    File.WriteAllText(output, SvgWriter.ToString(model, new SvgWriteOptions { ProjectFlat = args.Contains("--flat") }));
                    Console.WriteLine($"{output}: {model.Texts.Count} texts, {model.Fills.Count} fills");
                    return 0;
                }

                var options = new GlbWriteOptions();
                if (Option(args, "--compress") is { } compress)
                {
                    options.Compression = Enum.Parse<GlbCompression>(compress, ignoreCase: true);
                }

                if (Option(args, "--edges") is { } edges)
                {
                    options.Edges = Enum.Parse<EdgeMode>(edges, ignoreCase: true);
                }

                File.WriteAllBytes(output, GlbWriter.ToArray(model, options));
                Console.WriteLine($"{output}: {model.Metadata.Statistics?.Triangles:N0} triangles, {model.Meshes.Count} meshes, {model.Nodes().Count()} nodes");
                if (model.Metadata.Statistics is { SurfaceArea: > 0 } stats)
                {
                    Console.WriteLine($"area {Figure(stats.SurfaceArea)}, volume {Figure(stats.Volume)}");
                }
                foreach (var w in model.Warnings)
                {
                    Console.Error.WriteLine("warning: " + w);
                }

                return 0;
            }
    }
}
catch (ModelFormatException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

Console.Error.WriteLine("unknown command");
return 2;

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static object Summary(ModelMetadata m) => new
{
    format = m.Format.Id,
    kind = m.DocumentKind,
    application = m.Application,
    title = m.Title,
    units = m.Units?.ToString(),
    hasGeometry = m.HasGeometry,
    bounds = m.Bounds is { } b ? new { min = new[] { b.Min.X, b.Min.Y, b.Min.Z }, max = new[] { b.Max.X, b.Max.Y, b.Max.Z } } : null,
    statistics = m.Statistics,
    parts = m.PartNames,
    layers = m.LayerNames,
    sheets = m.Sheets,
    configurations = m.Configurations,
    componentFiles = m.ComponentFiles,
    properties = m.Properties,
    notes = m.Notes,
};

static void Print(ModelMetadata m)
{
    Console.WriteLine($"Format:       {m.Format.Name} ({m.DocumentKind})");
    Console.WriteLine($"Application:  {m.Application}");
    Console.WriteLine($"Title:        {m.Title}");
    Console.WriteLine($"Units / up:   {m.Units?.ToString() ?? "?"} / {m.UpAxis?.ToString() ?? "?"}");
    Console.WriteLine($"Geometry:     {(m.HasGeometry ? "yes" : "no")}  {m.Statistics}");
    if (m.Statistics is { SurfaceArea: > 0 } stats)
    {
        Console.WriteLine($"Area/volume:  {Figure(stats.SurfaceArea)} / {Figure(stats.Volume)}");
    }

    if (m.Bounds is { } box)
    {
        Console.WriteLine($"Bounds:       {box.Min} .. {box.Max}");
    }

    PrintList("Parts", m.PartNames);
    PrintList("Layers", m.LayerNames);
    PrintList("Sheets", m.Sheets);
    PrintList("Configurations", m.Configurations);
    PrintList("Component files", m.ComponentFiles);
    foreach (var p in m.Properties)
    {
        Console.WriteLine($"  [{p.Group}] {p.Name} = {p.Value}");
    }

    foreach (var n in m.Notes)
    {
        Console.WriteLine("Note: " + n);
    }
}

static void PrintList(string title, List<string> items)
{
    if (items.Count == 0)
    {
        return;
    }

    Console.WriteLine($"{title}:");
    foreach (var i in items)
    {
        Console.WriteLine("  " + i);
    }
}

// Six significant digits in fixed notation (scripts parse it): a part of a few square centimetres in meters is 0.005 with three decimals, which is
// not enough to compare against.
static string Figure(double v)
{
    if (v == 0 || !double.IsFinite(v))
    {
        return "0.000";
    }

    var decimals = Math.Clamp(5 - (int)Math.Floor(Math.Log10(Math.Abs(v))), 3, 15);
    return v.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
}

// Companion files of a loose model (an OBJ's MTL and textures): looked up under the model's own folder, never outside it.
static LoadOptions Loose(string modelPath)
{
    var root = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? ".");
    return new LoadOptions
    {
        ResolveCompanion = name =>
        {
            var full = Path.GetFullPath(Path.Combine(root, name.Replace('\\', '/')));
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) ? File.ReadAllBytes(full) : null;
        },
    };
}
