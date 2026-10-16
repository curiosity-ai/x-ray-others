# XRay.Psd: C# PSD/PSB library

This folder (`XRay.Psd/` in [curiosity-ai/x-ray-others](https://github.com/curiosity-ai/x-ray-others)) is a .NET 10 port of the PSD/PSB reading and compositing parts of Patchy, a C++/Qt image editor. It ships as the NuGet packages `X-Ray.Psd`, `X-Ray.Psd.Skia` and `X-Ray.Psd.Text`, part of the [X-Ray](https://github.com/curiosity-ai/x-ray) family; package ids are hyphenated like `X-Ray.Content`, while assemblies and namespaces stay `XRay.Psd`, `XRay.Psd.Skia` and `XRay.Psd.Text`.

The C++ original is the behavioral reference for this port. It is not in this repository: it lives, unmodified apart from disabled workflows, in [`.reference/` of theolivenbaum/Patchy](https://github.com/theolivenbaum/Patchy/tree/main/.reference) (about 175 MB). Clone that repository next to x-ray-others to read it; never build or edit it. Every `.reference/...` path in code comments and docs is relative to the Patchy repository root.

The x-ray-others root holds what all its packages share: `Directory.Build.props` (target framework, Curiosity GmbH package metadata, version, icon, Source Link, symbols), `Directory.Build.targets` (packs `assets/logo.png`), `global.json` and `.editorconfig`. `XRay.Psd/Directory.Build.props` imports the root one and adds the MIT license, Seth A. Robinson's copyright, the project URL and the stricter analysis settings. Do not edit root files for this package alone. Releases go out through `.devops/build-x-ray-psd-nuget.yml` at the root (Azure Pipelines, all three packages in one run at one CalVer version).

Read this file before any task. Keep it current and under 20,000 bytes; put detailed topic knowledge in `docs/<topic>.md` and link it here. `TODO.md` is the backlog; update it when you finish or discover work.

## Goals and scope

- Read PSD (version 1) and PSB (version 2) files: header, color mode data, image resources, layer tree, masks, vector masks, tagged blocks, linked/embedded files, merged image.
- Render a document to 8-bit sRGB RGBA and save it as PNG or JPEG.
- Extract all text: layer and group names, type-layer content with style runs and fonts, channel names, path names, slices, XMP/IPTC metadata, and text inside embedded PSD/PSB smart objects.
- Keep the core library dependency-free. SkiaSharp lives only in the optional `X-Ray.Psd.Skia` and `X-Ray.Psd.Text` packages; HarfBuzz only in `X-Ray.Psd.Text`, which re-renders type layers from the text model through the core's `ITextLayerRasterizer` hook.

## Layout

| Path | What |
|---|---|
| `src/XRay.Psd/` | Core library, no package dependencies |
| `src/XRay.Psd/IO/` | `BigEndianReader` (bounds-checked cursor), `ChannelCodec` (raw, PackBits RLE, ZIP, ZIP-with-prediction; SIMD sample conversion), `PsdSource` (in-memory or memory-mapped file with 64-bit offsets), `ByteRange` (payloads past 2 GB, read in windows), `ZipRowReader`/`ChannelReaders` (ZIP streams kept open between bands), `StreamLoader` |
| `src/XRay.Psd/Parallelism.cs` | `Parallelism.For`: deterministic row-strip loops, degree from `RenderOptions.MaxDegreeOfParallelism`; see `docs/performance.md` |
| `src/XRay.Psd/Cancellation.cs` | The ambient `CancellationToken` of the public call in progress (`Cancellation.Use`, `Check`); `Parallelism.For`, the parser, decoders, compositor and encoders poll it |
| `src/XRay.Psd/PsdParser.cs` | The five file sections, layer records, mask data, tagged blocks, group tree, `lnk2` linked files |
| `src/XRay.Psd/Layers/` | `PsdLayer`, `LayerMask`, `VectorPath` (path records to pixel coordinates), `VectorStrokeStyle` (`vstk`), `PsdLayerStyle` (public effects view over `Rendering/LayerEffects`), `PsdLayerMetadata` (`shmd`: layer time, per-comp state) |
| `src/XRay.Psd/Resources/` | `PsdImageResources` (`PsdDocument.Resources`): typed, lazily parsed image resources; see `docs/api.md` |
| `src/XRay.Psd/Descriptors/` | Action Manager descriptor reader (`Objc`, `VlLs`, `UntF`, `tdta`, `obj `...) |
| `src/XRay.Psd/Text/` | `EngineData` parser, `TextLayerInfo` (TySh), `TextEngineBlock` (`Txt2`) and `TextEngineResolver`, `LegacyText` (PS 5 tySh), `TextExtractor`; see `docs/text.md` |
| `src/XRay.Psd/Imaging/` | `RgbaImage` (public output), `PlanarImage` (internal float planes), `ColorSpaces`, `DocumentColors` (descriptor, text and pattern colors through the pixel transform), `Duotone`; `Icc/` holds the ICC parser, profile-to-sRGB transforms and `IccInkSpace` (sRGB back to CMYK or gray for adjustment layers) |
| `src/XRay.Psd/Rendering/` | `LayerCompositor` (+ `.Effects.cs`, `.Bevel.cs`, `.Strokes.cs`, `.Blending.cs`, `.Knockout.cs`, `.Contours.cs`, `.Bands.cs` band rendering), SIMD `BlendKernels`/`BlendOps`, `BlendIf`, `SpecialFill`, `MaskSampler`, `PathRasterizer`, `VectorStroker`, `EffectMasks`, `LayerEffects`, `LegacyEffects` (Photoshop 5 `lrFX`), `Gradient`, `GradientNoise`, `Patterns`, `StyleContour`, `Adjustments`, `MergedImageDecoder`, `PsdRenderer` |
| `src/XRay.Psd/Codecs/` | Built-in `PngEncoder` (+ `.Rows.cs`, the streaming writer behind `RenderToPng`) and baseline `JpegEncoder` |
| `src/XRay.Psd.Skia/` | Optional SkiaSharp interop (SKBitmap/SKImage, Skia encoders such as WebP) |
| `src/XRay.Psd.Text/` | Optional type-layer re-rendering (SkiaSharp + HarfBuzz): `IFontResolver` (`FontCollection`, `SystemFontResolver`, aliases), `TextLayoutEngine`, `TextWarpMesh`, `TextLayerRenderer`, `TextLayerRasterizer`; see `docs/text-rendering.md` |
| `tools/XRay.Psd.Cli/` | `psdtool info|text|render|layers` for inspection and manual checks (`info --resources --effects`) |
| `fuzz/XRay.Psd.Fuzz/` | SharpFuzz harness (the only project allowed that dependency); see `docs/testing.md` |
| `benchmarks/XRay.Psd.Benchmarks/` | BenchmarkDotNet suite (the only BenchmarkDotNet reference): load, text, decode, composite, encode, scaling |
| `tests/XRay.Psd.Tests/` | xUnit v3 tests; `Support/PsdBuilder.cs` writes synthetic PSD/PSB files |
| `tests/XRay.Psd.Skia.Tests/` | Skia interop tests; also decodes the built-in PNG/JPEG output with Skia |
| `tests/XRay.Psd.Text.Tests/` | Text re-rendering tests against the type fixtures; bundles Liberation Sans and Carlito (OFL) in `Fonts/`, never uses system fonts |
| `tests/XRay.Psd.Tests/Support/Fixtures.cs` | Resolves the fixture corpus (linked into all three test projects); see Fixtures below |
| `test_documents/psd/{psd-tools,ag-psd}/` | psd-tools and ag-psd fixtures (MIT) live in the corpus repo with their licenses; see `NOTICE` |
| `docs/porting-map.md` | Which reference files each C# area came from, and what was deliberately left out |
| `docs/rendering.md` | Compositor model, effect pipeline, calibration status |
| `docs/gradients.md` | Gradient placement and evaluation: line snapping, bounds, span, easing, document-space mixing, dither |
| `docs/adjustments.md` | Adjustment layers: payload layouts, models, accuracy against Photoshop, CMYK and gray documents, Color Lookup, noise gradients |
| `docs/text.md` | Text model: TySh, `Txt2`, PS 5 `tySh`, gap filling and extraction |
| `docs/text-rendering.md` | Type-layer re-rendering: core hook, font resolution, Photoshop layout model, validation metrics |
| `docs/color.md` | ICC color management: what converts (pixels and stored colors), duotone, Little CMS parity, accuracy |
| `docs/api.md` | Typed resource and layer-style views, CLI flags, NuGet packaging |
| `docs/testing.md` | Fixture corpus and its resolver, test suites, mutation test, fuzzing, the psd-tools fixtures |
| `docs/performance.md` | Benchmarks, parallel and SIMD rules, PNG segments, stream, async and memory-mapped loading, PSB over 2 GB |

## Commands

```bash
dotnet build XRay.Psd.slnx -c Release                 # must report 0 warnings
dotnet test --solution XRay.Psd.slnx -c Release        # Microsoft.Testing.Platform runner (global.json)
dotnet test --project tests/XRay.Psd.Tests -c Release -- --filter-class "*RenderingTests"
XRAY_PSD_SURVEY=1 dotnet test --project tests/XRay.Psd.Tests -c Release -- --filter-method "*Survey"
dotnet run --project tools/XRay.Psd.Cli -c Release -- info ../test_documents/psd/arrows.psd --resources --effects
XRAY_PSD_MUTATIONS=200 dotnet test --project tests/XRay.Psd.Tests -c Release -- --filter-class "*MutationFuzzTests"  # deep mutation run
dotnet pack XRay.Psd.slnx -c Release -o artifacts       # X-Ray.Psd, X-Ray.Psd.Skia and X-Ray.Psd.Text (+ .snupkg), 0 warnings
dotnet run --project benchmarks/XRay.Psd.Benchmarks -c Release -- --filter "*" --job short
```

Run them from `XRay.Psd/`; `global.json` at the x-ray-others root selects the Microsoft.Testing.Platform runner.

The survey writes `test-output/survey.txt` (layer compositor vs merged image vs Photoshop BMP for every fixture) plus `*.layers.png`/`*.merged.png`. Run it before and after any rendering change and compare. `test-output/` is gitignored.

`XRAY_PSD_TEXT_SURVEY=1 dotnet test --project tests/XRay.Psd.Text.Tests -c Release` writes `test-output/text-survey.txt`: every type layer re-rendered by `XRay.Psd.Text` and compared with its stored pixels. Run it before and after any change under `src/XRay.Psd.Text/`.

## Fixtures

The PSD/PSB fixtures and Photoshop reference renders (`.bmp`) are not in this repository. They live in [curiosity-ai/test_documents](https://github.com/curiosity-ai/test_documents) under `psd/` (copied from Patchy's `.reference/test-fixtures/psd`, MIT), with the psd-tools color-mode, bit-depth, smart-object and resource files in `psd/psd-tools/` (MIT, with its `LICENSE` and `NOTICE`). x-ray-others carries test_documents as the `test_documents` submodule (`git submodule update --init --depth 1 test_documents`).

`Fixtures` finds the corpus root from `XRAY_TEST_DOCUMENTS`, else the first non-empty `test_documents` directory above the test output directory. No corpus: fixture tests `Assert.Skip`, and theory data built with `Fixtures.TheoryDocuments()` collapses to one skipped row. Corpus present but a file missing: the test fails. New fixtures go into test_documents (see its `psd/README.md`), never into this folder; reference them by name through `Fixtures`, never by a machine path. `test-output/` and `local-test-fixtures/` are under `XRay.Psd/` (`Fixtures.PackageRoot`). See `docs/testing.md`.

## Rules

- Never modify Patchy's `.reference/`, and never copy it into this repository. It is the record of the calibrated C++ behavior. When a rendering rule is unclear, find it there (start with `.reference/src/render/layer_compositor.hpp`, `.reference/src/core/blend_math.cpp`, `.reference/docs/blend-modes.md`, `.reference/docs/ps-compat.md`) and cite the source in a code comment.
- The core library takes no NuGet dependencies. System.IO.Compression (zlib) and System.Xml are in the BCL and allowed. Anything that needs SkiaSharp or HarfBuzz goes in a separate package. Optional packages pin the same SkiaSharp version. SharpFuzz is allowed only in `fuzz/XRay.Psd.Fuzz`.
- Zero build warnings across the solution. Fix the cause; scope any suppression to one line.
- Untrusted input: every length, count and offset from a file goes through `BigEndianReader` or an explicit check. Malformed input must throw `PsdFormatException` or degrade (skip the block, zero the damaged rows); any other exception type escaping `PsdDocument.Load`, `Render` or `ExtractText` is a bug. `ParsingTests.Every_fixture_survives_truncation` and `MutationFuzzTests` (seeded mutations of every small fixture) guard this; new typed views over file data (`PsdDocument.Resources`, `PsdLayer.Style`) return null instead of throwing.
- Pixel data stays compressed until rendering asks for it (`PsdLayer.DecodePixels`). Text extraction and inspection must not decode pixels.
- Hot loops are planar float and SIMD: use `System.Numerics.Vector<T>` (or `Vector128/256` where a fixed width fits, as in the JPEG DCT). Blend modes are `IBlendOp`/`IChannelBlend` structs with static abstract members so the JIT emits one specialized loop per mode; do not add per-pixel virtual dispatch or delegates.
- Determinism: no `Random` or `std`-style distributions in rendering. Dissolve uses the splitmix64 hash of the document coordinate, as in the reference.
- Cancellation: every public call that loads, renders, encodes or extracts has an overload with `CancellationToken` last (an added overload where optional parameters exist), publishes it with `using var scope = Cancellation.Use(token)`, and throws `OperationCanceledException`, never `PsdFormatException` or `AggregateException`. Internals poll `Cancellation.Check()` at loops that bound latency (per record, per layer, per row strip; not per pixel) instead of taking a parameter. New loops over file-controlled counts must check; new narrow `catch` blocks must not catch it; an iterator (`RenderBands`) publishes the token only while it computes, never across a `yield`. Results must not depend on the token (`CancellationTests`). Outputs that can be large have `Stream` overloads and the `byte[]` form wraps them.
- Parallel loops use `Parallelism.For` (never `Parallel.For`), split only independent items (rows, lines, channels), and must give bit-identical output at every degree; `IAdjustment.Apply` and `CompositeLayerRow` run concurrently on different rows, so they keep no shared mutable state. Add a case to `ParallelismTests` for each new parallel loop.
- Band rendering (`RenderBands`, `RenderToPng`) must stay bit-identical to `Render` (`BandRenderingTests`). A band render keeps only the band's rows of the canvas and of row-local buffers, and `PlanarImage.Logical` holds a buffer's whole-render bounds: size regions, masks and effect domains from `Logical` (or from layer bounds), loop over `Bounds`, and never blur, dilate or distance-transform a band-clipped buffer. Work that reads across rows belongs to a layer `CollectWholeLayers` marks whole. See `docs/performance.md`.
- Public API: keep `PsdDocument`, `PsdLayer`, `RgbaImage`, `RenderOptions`, `PsdTextContent` stable; mark new internals `internal`. `PsdBlendMode` is append-only.
- Tests that need files outside the repository and the corpus copy them into `local-test-fixtures/` (gitignored) first. Never hardcode machine paths.
- Commit only verified, finished work with a short subject line. No AI attribution or co-author trailers in commits or pull requests.
- User-facing docs (README, docs/) use plain prose: no em dashes, no hype words.

## Rendering model (summary)

`PsdDocument.Render()` defaults to `RenderSource.Auto`: the saved merged image when the version-info resource (1057) says it is real, else the layer compositor. The compositor works on straight-alpha float planes and follows the calibrated reference semantics for groups, clipping, masks, fills, adjustments and layer effects; knockout, shadow and glow contours and gradient glows go beyond the reference, calibrated on psd-tools merged images. The full model, effect order and calibration status are in [docs/rendering.md](docs/rendering.md); read it before touching `src/XRay.Psd/Rendering/`.

Accuracy status per fixture is in the survey output; `RenderingTests` pins the fixtures that already match Photoshop.
