# X-Ray.PS.Skia

![X-Ray](https://raw.githubusercontent.com/curiosity-ai/x-ray/main/assets/logo.png)

An optional SkiaSharp raster writer for [X-Ray.PS](https://www.nuget.org/packages/X-Ray.PS), part of
the [X-Ray](https://github.com/curiosity-ai/x-ray) family of .NET document libraries.

X-Ray.PS renders PostScript, EPS and XPS pages to PNG, JPEG, TIFF and BMP with a pure managed
rasterizer. This package adds `SkiaRasterWriter`, which draws the same render model with Skia:
anti-aliased paths and clips, hinted glyph rasterization from the resolved TrueType fonts, image and
gradient shaders, and opacity layers. Its output matches the default (Skia based) raster output of the
MIT-licensed Python library X-Ray.PS was ported from.

The core package has no native dependencies; this one brings in SkiaSharp and its native library.

## Install

```sh
dotnet add package X-Ray.PS.Skia
```

On Linux, also reference a SkiaSharp native asset package, for example
`SkiaSharp.NativeAssets.Linux.NoDependencies`.

## Usage

```csharp
using XRay.PS;
using XRay.PS.PostScript;
using XRay.PS.Skia;
using XRay.PS.Xps;

var options = new ImageSaveOptions(ImageFormat.Png, dpi: 150) { RasterWriter = new SkiaRasterWriter() };

byte[] png = PsDocument.FromFile("input.ps").ToImage(options);

var xps = XpsDocument.FromFile("input.xps");
List<byte[]> pages = xps.ToImages(options);

// XPS opacity masks are rasterized while building the PDF; Skia can do that too.
byte[] pdf = xps.ToPdf(new PdfSaveOptions { MaskRasterizer = new SkiaRasterWriter() });
```

PNG and JPEG are drawn with Skia. TIFF and BMP output, and documents whose images Skia cannot take
safely (1-pixel images, CCITT data, samples other than 8 bits, full-page soft masks), fall back to the
managed `RenderModelRasterWriter`. A writer instance keeps no state between calls and can be shared.

## License

MIT. See `LICENSE` in the package.
