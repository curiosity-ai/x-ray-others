# Public API notes

The entry points are `PsdDocument` (load, render, extract text), `PsdLayer` (the layer tree) and `RgbaImage` (output). This page covers the typed views added on top of the raw blocks, and the packages.

## Image resources: `PsdDocument.Resources`

`document.Resources` (`XRay.Psd.Resources.PsdImageResources`) parses the common image resources on first access and caches the result. A property is null (or an empty list) when the resource is missing or does not decode; a damaged resource never throws. The raw blocks stay in `PsdDocument.ImageResources` and `GetImageResource(id)`.

| Property | Resource | Notes |
|---|---|---|
| `Resolution` | 1005 | Horizontal and vertical pixels per inch plus the display units. The stored value is always per inch, whatever the unit says (the reference verified this against Photoshop; `print_settings_from_resolution_resource`). Non-positive values read as 300, as in the reference. |
| `GridAndGuides` | 1032 | Grid cycles and guide positions in pixels (stored in 1/32 pixel). Guides keep their sign; the reference clamps negatives to 0 only for its own document model. A grid cycle of 0 or less reads as 18 px. |
| `Thumbnail` | 1036, else 1033 | Format (JPEG or raw), size, row bytes and the encoded payload. 1033 (Photoshop 4) stores BGR; `IsBgr` says so. The core library has no JPEG decoder; `XRay.Psd.Skia` adds `PsdThumbnail.Decode()`. Every real file seen so far stores JPEG (all 356 psd-tools files, the committed fixtures and their embedded documents); the raw layout is covered by synthetic tests only. |
| `Slices` | 1050 | Version 6 binary records (with the per-slice descriptor Photoshop 7 and later append, skipped) or the version 7/8 descriptor, checked against Photoshop CC's version 8 resource in psd-tools' `metadata.psd`: `baseName` becomes `GroupName`, the URL key is the three-character `url`, `horzAlign`/`vertAlign` map to the version 6 numbers, the outsets fill `TopOutset`..`RightOutset`, and `bgColor` counts only when `bgColorType` is `Clr `. Text extraction reads slice text through this view. |
| `LayerComps` | 1065 | Name, comment, comp ID and the visibility/position/appearance capture flags, plus the last applied comp. What each comp records per layer is in `PsdLayer.Metadata` (below). |
| `PrintScale` | 1062 | Style, position and scale. |
| `PixelAspectRatio` | 1064 | Width over height. |
| `VersionInfo` | 1057 | Writer and reader names, file version, and the "real merged data" flag that `HasRealMergedImage` and `RenderSource.Auto` use. |
| `IccProfile` | 1039 | Raw bytes plus the description, color space, class and version from the managed ICC parser. |
| `Xmp` | 1060 | The XMP packet as text. |
| `Iptc` | 1028 | Every IPTC-NAA dataset with its record and dataset numbers; `Text` decodes UTF-8 and falls back to Latin-1. |
| `ChannelNames` | 1045, else 1006 | Alpha channel names. |

The reference reads only 1005 and 1032 ([`.reference/src/psd/psd_image_resources.cpp`](https://github.com/theolivenbaum/Patchy/blob/main/.reference/src/psd/psd_image_resources.cpp)); the other layouts follow Adobe's file format specification and were checked against Photoshop-saved files.

## Layer effects: `PsdLayer.Style`

`layer.Style` (`XRay.Psd.Layers.PsdLayerStyle`) is a read-only view of the effects descriptor that `PsdLayer.Effects` already exposes raw. `Effects` lists every effect instance in file order, including disabled ones and every instance of a multi-instance effect (`lmfx`); `ActiveEffects` keeps the ones that render; `Visible` is the master switch.

Each `PsdLayerEffect` has its kind, switch, blend mode, opacity (0 to 1), color, and where they apply size, distance, angle (with "Use Global Light" resolved), spread or choke, stroke position, bevel style, technique, altitude and depth, and the bevel shadow mode, color and opacity. The values come from the compositor's own parser (`Rendering/LayerEffects.cs`), so defaults and clamps match what renders. A disabled effect is parsed as if it were enabled, so it reports the same values Photoshop would apply if switched on. Gradient and pattern fills, and gradient glows, report `HasGradient` and a null `Color`.

Layers with only Photoshop 5 effects (`lrFX`, no `lfx2`, `lfxs` or `lmfx`) get them translated into the same descriptor shape: `IsLegacy` is true, `Descriptor` (and `PsdLayer.Effects`) hold the translation, and the effects list drop and inner shadows, outer and inner glows, bevel and emboss, and color fill with the values the compositor renders. The translation rules are in [rendering.md](rendering.md#photoshop-5-effects-legacyeffectscs). Beside a modern block the `lrFX` mirror is ignored, as Photoshop does.

## Layer metadata and comp state: `PsdLayer.Metadata`

`layer.Metadata` (`XRay.Psd.Layers.PsdLayerMetadata`) reads the layer's metadata block (`shmd`) on first use; it is null without one, and a damaged block keeps the items that decoded. `Items` lists every entry with its key, "copy on sheet duplication" flag, raw payload and, for the versioned descriptors (`cust`, `cmls`, `mlst`, `tmln`, `extn`), the descriptor. `LayerTime` is `cust/layerTime` (Unix seconds).

`CompStates` is the layer comp state (`cmls/layerSettings`), one `PsdLayerCompState` per entry: `CompIds` (the comp IDs of resource 1065 it applies to; ID 0 is the document state Photoshop keeps beside the comps), `Visible`, `Offset` (pixels from the saved position), `VectorMaskOffset`, `EffectsReferencePoint`, `BlendMode`, `Opacity` and `FillOpacity` (0 to 1), `Effects` (the comp's effects descriptor, `lfx2` layout) and the raw `Descriptor`. A null value means the comp does not change that property. `GetCompState(compId)` finds a comp's entry; `CompLayerId` and `OriginalEffectsReferencePoint` are `LyrI` and `origFXRefPoint`. Checked against psd-tools' `layer_comps.psd`: the last applied comp's visibility equals every layer's saved visibility, and the "Scary" comp hides one shape and moves both eye groups 9 px right. Applying a comp when rendering is not implemented.

## Cancellation and streams

Public entry points that load, render, encode or extract take `CancellationToken` as the last parameter. Where the method already had optional parameters the token goes on an added overload (`Render(RenderOptions?, CancellationToken)`, `Load(path, options, token)`), so existing binaries keep working. The overloads:

| Area | Members |
|---|---|
| Load | `PsdDocument.Load(string/Stream/byte[]/ReadOnlyMemory<byte>, PsdLoadOptions?, CancellationToken)`; `LoadAsync` publishes its token to the parser too. |
| Render | `Render(options, token)`, `RenderBands(options, token)`, `RenderToPng(Stream/string, options, level, token)`, `RenderToJpeg(Stream, quality, options, background, token)`, `GetMergedImage(token)`, `PsdLayer.GetPixels(token)`, `PsdLayer.Render(token)`. |
| Text | `ExtractText(options, token)`; in `XRay.Psd.Text`, `TextLayerRenderer.Render(layer / text, ..., token)` and `TextLayerRasterizer.Rasterize(layer, token)`, which the compositor calls through `ITextLayerRasterizer.Rasterize(layer, token)` (a default interface member, so older implementations keep working). |
| Encode | `RgbaImage.ToPng/ToJpeg/SavePng/SaveJpeg/Save` with a token, `PngEncoder.Encode` and `JpegEncoder.Encode` with a token; `XRay.Psd.Skia` `Encode`, `RenderAndEncode`, `RenderToSKBitmap` with a token. |

Stream outputs: `RgbaImage.ToPng(Stream, ...)` and `ToJpeg(Stream, ...)`, `PngEncoder.Encode(image, Stream, ...)`, `JpegEncoder.Encode(image, Stream, ...)`, `PsdDocument.RenderToJpeg(Stream, ...)`, `PsdThumbnail.CopyTo(Stream)` and `WriteJpeg(Stream, quality, token)`, and `XRay.Psd.Skia` `Encode(Stream, format, ...)` and `RenderAndEncode(Stream, format, ...)`. The `byte[]` forms are thin wrappers over these. PNG goes through `PngEncoder.RowWriter` a band of rows at a time, so a large image is written as one IDAT chunk per deflate segment (the zlib stream is the one the whole-image encoder writes; small images still get one chunk). JPEG keeps the quantized blocks in memory (the Huffman tables are optimized over the whole image) and writes the entropy-coded bytes as they are produced. A stored JPEG thumbnail is copied unchanged; a raw one is encoded.

How it works: `Cancellation` (`Cancellation.cs`) publishes the call's token in an `AsyncLocal`, like `Parallelism`'s degree, so it reaches parser loops, decoders and the worker threads of `Parallelism.For` without a parameter on every internal method. `Parallelism.For` checks it before each strip and passes it to `Parallel.For`; with a cancellable token and a degree of 1 it runs the strips one after another so the token is noticed between them (the strip partition is fixed by the work size, so results do not change). The scope ends with the call, and `RenderBands` publishes the token only while a band is computed, never across a `yield`. Narrow `catch` blocks that degrade damaged data (`PsdFormatException`, `InvalidDataException`) do not catch `OperationCanceledException`; keep it that way.

## Band rendering

`PsdDocument.RenderBands(options)` yields `RenderBand` values (`Top`, and an `Image` as wide as the document) top to bottom, rendering each when the enumeration reaches it; `RenderOptions.BandHeight` sets the rows per band (0 picks about four million pixels). `RenderToPng(stream)` and `RenderToPng(path)` stream the bands into a PNG. The pixels equal `Render`'s, and `Render` itself throws `PsdFormatException` for documents whose canvas does not fit one array. What still works on its whole extent is listed in `docs/performance.md`.

## Command line

`psdtool info file.psd --resources` prints the typed resources; `--effects` prints each layer's effects under it, marking Photoshop 5 styles, and `--metadata` its layer time and comp states. `psdtool text` adds the path segments and text range of type on a path. `psdtool render file.psb out.png --bands ROWS` renders through `RenderToPng` (`ROWS` 0 picks the band height).

## Packages

`dotnet pack -c Release` produces the packages `X-Ray.Psd`, `X-Ray.Psd.Skia` and `X-Ray.Psd.Text` with their symbol packages (`.snupkg`). Package ids are hyphenated like the rest of the X-Ray family (`X-Ray.Content`); assemblies and namespaces stay `XRay.Psd`, `XRay.Psd.Skia` and `XRay.Psd.Text`. The Skia and Text packages depend on `X-Ray.Psd` through their project reference, so all three carry one version. Package metadata comes from two files: the x-ray-others root `Directory.Build.props` (authors, version, icon, repository, Source Link and symbols, shared by every package there) and `XRay.Psd/Directory.Build.props`, which imports it and adds the MIT license, the copyright and the project URL. Only the three libraries set `IsPackable`. Source Link comes from the .NET SDK (`PublishRepositoryUrl`, `EmbedUntrackedSources`), and builds under GitHub Actions, Azure Pipelines or `CI=true` set `ContinuousIntegrationBuild` for reproducible paths. The core package carries `XRay.Psd/README.md`; the Skia and Text packages have their own (`src/XRay.Psd.Skia/README.md`, `src/XRay.Psd.Text/README.md`). The family icon (`assets/logo.png` at the x-ray-others root) is packed by the root `Directory.Build.targets`.

Releases are published by the Azure Pipelines job `.devops/build-x-ray-others-nuget.yml` at the x-ray-others root, which publishes every package in the repository: on a push to `main` that touches `XRay.Psd/` or another package folder, it packs them all with a CalVer version (`yy.M.<build id>`) and pushes them, with their symbols, to nuget.org.
