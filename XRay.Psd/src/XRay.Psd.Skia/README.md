# X-Ray.Psd.Skia

SkiaSharp interop for [X-Ray.Psd](https://www.nuget.org/packages/X-Ray.Psd), the dependency-free PSD/PSB reader and renderer for .NET.

- `RgbaImage.ToSKBitmap()`, `ToSKImage()` and `SKBitmap.ToRgbaImage()` convert between XRay.Psd output and Skia (straight alpha is preserved).
- `RgbaImage.Encode(SKEncodedImageFormat, quality)` and `PsdDocument.RenderAndEncode(...)` encode with Skia's codecs, such as WebP.
- `PsdThumbnail.Decode()` decodes the thumbnail Photoshop stores in the file (`document.Resources.Thumbnail`).

```csharp
using SkiaSharp;
using XRay.Psd;
using XRay.Psd.Skia;

var document = PsdDocument.Load("poster.psd");
File.WriteAllBytes("poster.webp", document.RenderAndEncode(SKEncodedImageFormat.Webp, quality: 90));
using var bitmap = document.RenderToSKBitmap();
var thumbnail = document.Resources.Thumbnail?.Decode();
```

Applications add the SkiaSharp native asset package for their platform (for example `SkiaSharp.NativeAssets.Linux.NoDependencies`).

Part of the [X-Ray](https://github.com/curiosity-ai/x-ray) family of .NET libraries by Curiosity GmbH. Source: [curiosity-ai/x-ray-others](https://github.com/curiosity-ai/x-ray-others/tree/main/XRay.Psd).

License: MIT.
