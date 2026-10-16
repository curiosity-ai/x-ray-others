# X-Ray.Psd

A .NET 10 library for reading Photoshop documents (PSD and PSB), rendering them to PNG or JPEG, and extracting their text. The core package has no dependencies outside the .NET base class library. Hot paths use `System.Numerics.Vector<T>` SIMD.

It is part of [X-Ray](https://github.com/curiosity-ai/x-ray), Curiosity's family of pure C# .NET libraries for reading documents, and lives in the `XRay.Psd/` folder of [curiosity-ai/x-ray-others](https://github.com/curiosity-ai/x-ray-others). The code is a port of the PSD engine in [Patchy](https://github.com/theolivenbaum/Patchy), a C++ image editor whose source, kept in Patchy's [`.reference/`](https://github.com/theolivenbaum/Patchy/tree/main/.reference), is the behavioral reference.

## Packages

| Package | What | Dependencies |
|---|---|---|
| `X-Ray.Psd` | Reader, compositor, text extraction, PNG/JPEG output | none |
| `X-Ray.Psd.Skia` | `SKBitmap`/`SKImage` interop, Skia encoders (WebP and others), thumbnail decoding | `X-Ray.Psd`, SkiaSharp |
| `X-Ray.Psd.Text` | Re-renders type layers from their text | `X-Ray.Psd`, SkiaSharp, SkiaSharp.HarfBuzz |

```bash
dotnet add package X-Ray.Psd
```

Package ids are hyphenated like the rest of the family; assemblies and namespaces are `XRay.Psd`, `XRay.Psd.Skia` and `XRay.Psd.Text`.

## Features

- PSD and PSB, 1/8/16/32 bits per channel, RGB, grayscale, bitmap, indexed, CMYK, Lab, duotone and multichannel.
- Raw, RLE, ZIP and ZIP-with-prediction channel data.
- Layer tree with groups, blend modes, opacity, fill, clipping, raster masks and vector masks.
- Rendering from the merged image Photoshop saved, or from the layers with a compositor that follows Photoshop's blending rules.
- Built-in PNG and JPEG encoders. The optional `X-Ray.Psd.Skia` package adds `SKBitmap` interop and Skia encoders such as WebP.
- Text extraction: layer and group names, type-layer content with fonts and style runs, channel and path names, slices, XMP and IPTC metadata, and text inside embedded PSD/PSB smart objects.
- Optional type-layer re-rendering (`X-Ray.Psd.Text`, SkiaSharp and HarfBuzz): draws type layers from their text, for files that store no text pixels or when the text should be redrawn with other fonts. It follows Photoshop's layout model for point, box, vertical and on-path text, leading, tracking, glyph scales, alignment, transforms and Warp Text. See `docs/text-rendering.md`.
- Typed views of the common image resources (resolution, guides and grid, thumbnail, slices, layer comps, print scale, pixel aspect ratio, version info, ICC profile, XMP, IPTC), of each layer's effects, and of each layer's metadata (layer time and the visibility, position and appearance each layer comp records).

The layer compositor renders layer effects (shadows, glows, overlays, satin, strokes, bevel and emboss), adjustment layers (Levels, Curves, Hue/Saturation, Brightness/Contrast, Exposure, Invert, Threshold, Posterize) and solid, gradient and pattern fills. Not rendered from layers yet: vector strokes on shape layers, Blend If, some adjustments (Color Balance, Vibrance, Black and White, Channel Mixer, Selective Color, Gradient Map, Photo Filter). Documents saved with "Maximize Compatibility" render exactly through the merged image. See `TODO.md`.

## Usage

```csharp
using XRay.Psd;
using XRay.Psd.Rendering;

var document = PsdDocument.Load("poster.psd");

// Render and save. Auto uses Photoshop's merged image when the file has one.
document.Render().SavePng("poster.png");
document.Render(new RenderOptions { Background = PsdColor.White }).SaveJpeg("poster.jpg", quality: 85);

// Force the layer compositor, or hide layers.
var withoutText = document.Render(new RenderOptions
{
    LayerVisibility = layer => layer.IsVisible && layer.Kind != PsdLayerKind.Text,
});

// All text, in Layers panel order.
foreach (var item in document.ExtractText().Items)
{
    Console.WriteLine($"{item.Kind}: {item.Source}: {item.Text}");
}

// Walk the layer tree.
foreach (var layer in document.EnumerateLayersTopDown())
{
    Console.WriteLine($"{layer.Path} {layer.Kind} {layer.BlendMode} {layer.Bounds}");
    if (layer.Text is { } text)
    {
        Console.WriteLine($"  \"{text.Text}\" in {string.Join(", ", text.Fonts)}");
    }

    layer.GetPixels()?.SavePng($"{layer.Index}.png"); // the layer's own pixels, no masks

    // Layer effects as a typed view (the raw descriptor stays in layer.Effects).
    foreach (var effect in layer.Style?.ActiveEffects ?? [])
    {
        Console.WriteLine($"  {effect.Kind} {effect.BlendMode} {effect.Opacity:P0} {effect.Color} size {effect.Size}");
    }
}

// Typed image resources, parsed on first access (null when absent or damaged).
var resources = document.Resources;
Console.WriteLine($"{resources.Resolution?.HorizontalPpi} ppi, written by {resources.VersionInfo?.ReaderName}");
foreach (var guide in resources.GridAndGuides?.Guides ?? [])
{
    Console.WriteLine($"{guide.Orientation} guide at {guide.Position} px");
}

foreach (var comp in resources.LayerComps?.Comps ?? [])
{
    Console.WriteLine($"layer comp \"{comp.Name}\"");
    foreach (var layer in document.Layers)
    {
        // What the comp records for each layer (null values: the comp keeps the saved state).
        if (layer.Metadata?.GetCompState(comp.Id) is { } state)
        {
            Console.WriteLine($"  {layer.Name}: visible {state.Visible}, offset {state.Offset}, opacity {state.Opacity}");
        }
    }
}

var thumbnailJpeg = resources.Thumbnail?.Data; // decode with X-Ray.Psd.Skia: resources.Thumbnail.Decode()
```

Re-rendering type layers with the optional `X-Ray.Psd.Text` package:

```csharp
using XRay.Psd.Text;

var fonts = new FontCollection();
fonts.AddFile("fonts/LiberationSans-Regular.ttf"); // ArialMT resolves to it through the metric-compatible aliases

using var rasterizer = new TextLayerRasterizer(new TextRenderSettings
{
    FontResolver = new CompositeFontResolver(fonts, SystemFontResolver.Default),
});

// Type layers without stored pixels are drawn from their text; Always redraws every type layer.
document.Render(new RenderOptions { TextRasterizer = rasterizer }).SavePng("filled-in.png");
document.Render(new RenderOptions { TextRasterizer = rasterizer, TextRasterMode = TextRasterMode.Always }).SavePng("redrawn.png");

// Fonts the resolver cannot find (those runs use the fallback face).
foreach (var layer in document.EnumerateLayersTopDown())
{
    if (layer.Text is { } text && rasterizer.Renderer.FindMissingFonts(text) is { Count: > 0 } missing)
    {
        Console.WriteLine($"{layer.Path}: missing {string.Join(", ", missing)}");
    }
}
```

The package needs the Skia and HarfBuzz native assets for the platform (for example `SkiaSharp.NativeAssets.Linux` and `HarfBuzzSharp.NativeAssets.Linux`).

`docs/api.md` lists every resource view and effect property.

Loading and threading:

```csharp
// Read asynchronously, or map a large file instead of reading it (dispose to release the mapping).
var fromStream = await PsdDocument.LoadAsync(stream, cancellationToken: token);
using var mapped = PsdDocument.Load("huge.psb", new PsdLoadOptions { MemoryMap = true });

// Rendering uses every processor by default; the result is the same at any degree.
var image = mapped.Render(new RenderOptions { MaxDegreeOfParallelism = 1 });
```

Cancellation and streams. Every call that does real work takes a `CancellationToken` as its last parameter, and the outputs that can be large write to a stream:

```csharp
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = timeout.Token;

using var document = PsdDocument.Load("poster.psd", new PsdLoadOptions { MemoryMap = true }, token);
var text = document.ExtractText(null, token);

// JPEG and PNG straight into the caller's stream (a temp file, a response body).
using var jpeg = File.Create("poster.jpg");
document.RenderToJpeg(jpeg, quality: 60, cancellationToken: token);
using var png = File.Create("poster.png");
document.RenderToPng(png, new RenderOptions { Source = RenderSource.Layers }, PngCompressionLevel.Default, token);

// The thumbnail Photoshop stored: a stored JPEG is copied byte for byte, no decode or re-encode.
if (document.Resources.Thumbnail is { } thumbnail)
{
    using var small = File.Create("thumb.jpg");
    thumbnail.WriteJpeg(small, quality: 80, token);
}
```

A cancelled call throws `OperationCanceledException`, never `PsdFormatException`. The token is observed while the file is read, at every layer record and tagged block the parser walks, at every channel decode, between layers and at every strip of the parallel passes (no new strip starts once it is cancelled), per band, and per paragraph, line and glyph in `XRay.Psd.Text`. A render that completes is bit-identical with or without a token.

Large documents render in horizontal bands, holding one band of the canvas at a time. The bands are bit-identical to the same rows of `Render`, and they work past the size one canvas can hold (about 46000 x 46000 pixels, and about 23000 x 23000 for one `RgbaImage`):

```csharp
// Stream straight into a PNG (RGBA, or RGB when a background or an opaque merged image makes it opaque).
mapped.RenderToPng("huge.png", new RenderOptions { Source = RenderSource.Layers });

// Or take the bands yourself, top to bottom.
foreach (var band in mapped.RenderBands(new RenderOptions { BandHeight = 512 }))
{
    Consume(band.Top, band.Image); // band.Image is document-wide, band.Image.Height rows
}
```

Files of 2 GB and more are always memory-mapped and parsed with 64-bit offsets; channels, the merged image and 16/32-bit layer blocks past 2 GB are read in pieces. See `docs/performance.md` for what band rendering keeps whole, the parallel model and the benchmarks.

Command line:

```bash
dotnet run --project tools/XRay.Psd.Cli -- info file.psd --resources --effects --metadata
dotnet run --project tools/XRay.Psd.Cli -- text file.psd
dotnet run --project tools/XRay.Psd.Cli -- render file.psd out.png --layers
dotnet run --project tools/XRay.Psd.Cli -- render huge.psb out.png --bands 0   # band rendering, 0 picks the band height
```

## Building and testing

The commands run from `XRay.Psd/` in a clone of x-ray-others. The test fixtures live in [curiosity-ai/test_documents](https://github.com/curiosity-ai/test_documents) (`psd/`), which x-ray-others carries as a submodule; without it the fixture tests are skipped.

```bash
git submodule update --init --depth 1 test_documents   # once, from the x-ray-others root
dotnet build XRay.Psd.slnx -c Release
dotnet test --solution XRay.Psd.slnx -c Release
```

Set `XRAY_TEST_DOCUMENTS` to use a test_documents clone somewhere else. `docs/testing.md` has the details.

Benchmarks (BenchmarkDotNet): `dotnet run -c Release --project benchmarks/XRay.Psd.Benchmarks -- --filter "*" --job short`.

Tests run every fixture in `test_documents/psd` (parse, decode, truncation and seeded mutation robustness), compare renders with Photoshop's own captures, and cover the formats without fixtures through a synthetic PSD writer. Real 16/32-bit, grayscale, indexed, Lab, duotone, multichannel, bitmap and embedded PSB files come from the psd-tools collection (MIT), committed under `tests/fixtures/psd-tools/`, and a type-on-a-path file from ag-psd (MIT) under `tests/fixtures/ag-psd/` (see `NOTICE`).

NuGet packages (`X-Ray.Psd`, `X-Ray.Psd.Skia`, `X-Ray.Psd.Text`, with `.snupkg` symbols and Source Link):

```bash
dotnet pack XRay.Psd.slnx -c Release -o artifacts
```

## Fuzzing

`fuzz/XRay.Psd.Fuzz` is a [SharpFuzz](https://github.com/Metalnem/sharpfuzz) harness over `PsdDocument.Load`, `Render` and `ExtractText`; any exception other than `PsdFormatException` counts as a crash. Build it, instrument the library, then run it under libFuzzer or AFL:

```bash
dotnet tool install --global SharpFuzz.CommandLine
dotnet build fuzz/XRay.Psd.Fuzz -c Release
sharpfuzz fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.dll

# libFuzzer, with libfuzzer-dotnet from https://github.com/Metalnem/libfuzzer-dotnet/releases
./libfuzzer-dotnet --target_path=fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz -timeout=20 corpus/

# AFL++
afl-fuzz -i corpus/ -o findings/ -t 20000 -m none dotnet fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz.dll --afl

# Replay a saved input without a fuzzer
dotnet fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz.dll crash-file
```

Seed `corpus/` with a few small files from `test_documents/psd`. `docs/testing.md` has the details.

## License

MIT, see `LICENSE` (copyright Seth A. Robinson and contributors). The bundled test font and the third-party test files are listed in `NOTICE`.
