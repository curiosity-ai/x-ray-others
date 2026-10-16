# X-Ray.PS

![X-Ray](https://raw.githubusercontent.com/curiosity-ai/x-ray/main/assets/logo.png)

Reads PostScript, Encapsulated PostScript and XPS documents and converts them to PDF and raster
images, in pure managed C#. Part of the [X-Ray](https://github.com/curiosity-ai/x-ray) family of
.NET document libraries; documentation at <https://xray.curiosity.ai/>.

X-Ray.PS is a port of an MIT-licensed open-source Python PostScript/XPS library. It contains a
PostScript interpreter, an XPS reader and writer, a render model shared by both inputs, a PDF
writer with font embedding, and a raster renderer with PNG, JPEG, BMP and TIFF encoders. The core
package has no native dependencies. `X-Ray.PS.Skia` is an optional add-on that draws pages with
SkiaSharp instead, which reproduces the original library's default raster output.

## Install

```sh
dotnet add package X-Ray.PS
dotnet add package X-Ray.PS.Skia   # optional: SkiaSharp raster output
```

## Quick start

```csharp
using XRay.PS;
using XRay.PS.PostScript;
using XRay.PS.Xps;

// PostScript or EPS to PDF and PNG
var ps = PsDocument.FromFile("drawing.ps");
File.WriteAllBytes("drawing.pdf", ps.ToPdf());
File.WriteAllBytes("drawing.png", ps.ToImage(new ImageSaveOptions(ImageFormat.Png, dpi: 150)));

// XPS to PDF, and one image per page
var xps = XpsDocument.FromFile("report.xps");
File.WriteAllBytes("report.pdf", xps.ToPdf());
List<byte[]> pages = xps.ToImages(new ImageSaveOptions(ImageFormat.Png, dpi: 96));

// Build and edit documents
var doc = PsDocument.Create(isEps: true, width: 200, height: 100);
doc.GetPage(0).Canvas.DrawText("Hello", 10, 10, "Helvetica", 12);
File.WriteAllBytes("hello.eps", doc.Save());

// Wrap a PNG, JPEG, BMP or TIFF in EPS
File.WriteAllBytes("logo.eps", ImageToEps.ConvertFile("logo.png"));
```

Fonts: standard PostScript names resolve to the bundled Liberation Serif and Dingbats fonts or to
fonts installed on the system. Set `AdditionalFontsFolder` on the save options to add a folder.
A render model (`BuildRenderModel`) is available for both input kinds.

## Differences from the Python original

- The raster output of `RenderModelRasterWriter` is the original's pure-Python renderer, ported
  pixel for pixel. The original's default writer is Skia; `X-Ray.PS.Skia` provides that.
- JPEG XR images are not decoded (the original needs an optional codec for them too).
- ICC-profile colors in XPS (`ContextColor`) use the original's fallback formula.

## License

MIT, see `LICENSE`. Bundled fonts and the ICC profile carry their own licences, listed in `NOTICE`.
