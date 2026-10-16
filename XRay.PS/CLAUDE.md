# XRay.PS: C# PostScript / EPS / XPS library

Read this file before any task here. Keep it under 20,000 bytes.

`X-Ray.PS` (namespace `XRay.PS`) is a pure managed port of an MIT-licensed open-source Python
PostScript/XPS library (see `LICENSE`, kept verbatim, and `NOTICE`). The Python source is a separate checkout (agents find it next to the other family clones; ask the
coordinator for its path). It is the behavioural reference: read it, never edit it.

## Packages and layout

| Path | What |
|---|---|
| `src/XRay.PS/` | `X-Ray.PS`, no native dependencies |
| `src/XRay.PS.Skia/` | `X-Ray.PS.Skia`: `SkiaRasterWriter` (SkiaSharp 4.153.1, same pin as X-Ray.Psd) |
| `tests/XRay.PS.Tests/` | xUnit v3 (Microsoft.Testing.Platform), includes the Skia tests |
| `tools/XRay.PS.Cli/` | `psconvert`: `pdf`, `image`, `model`, `info`, `eps`, `parity`; `--skia` selects the Skia writer |
| `tools/parity/` | Python scripts that compare this port with the original (see below) |
| `NOTICE`, `LICENSE` | third-party notices; the original's MIT licence |

Inside `src/XRay.PS/`: `PostScript/` (tokenizer, parser, interpreter, operators, `PsDocument`,
`PsConverter`, `ImageToEps`), `Xps/` (package, renderer, editing, print tickets, converter),
`Rendering/` (the render model and canonical dump), `Pdf/` (writer, font embedding), `Imaging/`
(raster renderer, encoders, JPEG/PNG/TIFF/CCITT decoders, Pillow-compatible drawing),
`Fonts/` (resolver, TrueType, Type 1, subsetting), `Internal/` (Python-semantics helpers `Py`,
zlib inflater, entity tables), `Resources/` (Liberation Serif, Dingbats, sRGB profile).

## Commands

Run from `/home/user/x-ray-others` so `global.json` applies:

```sh
dotnet build XRay.PS/XRay.PS.slnx -c Release          # must report 0 warnings
dotnet test --project XRay.PS/tests/XRay.PS.Tests -c Release
XRAY_PS_PARITY=1 dotnet test --project XRay.PS/tests/XRay.PS.Tests -c Release -- --filter-class "*CorpusSweepTests"
dotnet pack XRay.PS/src/XRay.PS -c Release -o artifacts
```

## Porting map

Each C# file cites its Python source in its header comment. Dropped on purpose: the MCP server,
the build/metrics scripts, and Skia in the core (moved to `X-Ray.PS.Skia`).

| Python | C# |
|---|---|
| `ps/*` (tokenizer, parser, interpreter, operators, color, text, image ops, pipeline) | `PostScript/` |
| `ps/fonts`, `font_cache`, `font_types`, `type1_parser`, `ttf_*`, `pdf_font_embed` | `Fonts/`, `Pdf/PdfFontEmbedder.cs` |
| `ps/document`, `editing`, `serializer`, `xmp`, `output` | `PostScript/PsDocument.cs`, `PsConverter*.cs` |
| `ps/image_to_eps` | `PostScript/ImageToEps.cs` |
| `xps/*` | `Xps/` |
| `common/*` | `Rendering/` |
| `pdf/*` | `Pdf/` |
| `image/raster_renderer`, `encoders`, `raster_writer` | `Imaging/` |
| `image/skia_raster_writer` | `src/XRay.PS.Skia/SkiaRasterWriter.cs` |

## Rules

- **Python semantics are the spec.** Parity is byte-level where the original is deterministic, so
  the port reproduces Python behaviour: `Py` has banker's rounding, truncating `int()`, float
  parsing/formatting, `or` defaults, code-point strings. Where Python raises a built-in error
  (division by zero, bad index), `HostErrorException` is raised at the same place. A deliberate
  deviation gets a comment saying so (known ones are listed below).
- **Pure managed core.** No P/Invoke or native assets in `X-Ray.PS`; Skia stays in `.Skia`.
- **Untrusted input.** Loads and conversions may throw only `PsError` subclasses,
  `XpsFormatException`, `HostErrorException` or argument/invalid-operation errors.
  `RobustnessTests` truncates and mutates PS, XPS and image inputs; extend it with new parsers.
- **No vendor names** anywhere but `LICENSE` (`grep -rIi <vendor> XRay.PS` must match only it).
  Producer string is `XRay.PS`.
- **Zero build warnings.** Fix the cause.
- **Corpus.** Fixtures live in `test_documents/ps/` and `test_documents/xps/` (provenance in their
  READMEs). `Corpus` resolves `XRAY_TEST_DOCUMENTS`, else the nearest non-empty `test_documents`
  above the binaries (an empty directory, an uninitialised submodule, is skipped). No corpus:
  tests skip. Corpus but missing file: the test fails.
- The original's commercial `necessary_fonts/` and `arialbd.ttf` are not in the corpus; glyph
  shapes of tests that need them differ, structure does not.

## Validation against the Python original

`tools/parity/python_golden.py` runs the original over every fixture and writes goldens (summary
JSON, canonical render-model dump, PDF, Skia and pure-Python PNGs) outside the repository.
`psconvert parity` writes the same for the port, and `parity.py run|compare` diffs them: success
agreement, render-model identity (canonical dump of the model handed to the PDF writer), PDF page
count/sizes/extracted text, and pixel metrics of rasters (`--skia` run for the Skia writer).
Needs a venv with Pillow, numpy, pypdf, and skia-python for the Skia goldens. Re-run after any
change to the interpreter, renderer or writers.

Latest results (583 PS/EPS inputs, 62 XPS inputs):

| Check | PS | XPS |
|---|---|---|
| success/failure agrees with the original | 583/583 (400 convert) | 62/62 (57 convert) |
| render model identical | 400/400 | 56/57 |
| PDF pages, sizes, text | 400/400 | 57/57 |
| raster vs original pure-Python renderer (managed writer) | pixel-identical 400/400 | n/a |
| raster vs original Skia output (Skia writer) | identical 398/400, max mean diff 0.005 | 74/85 pages identical, 84/85 under 1% px off |
| raster vs original Skia output (managed writer) | median mean diff 0.93, 247/400 under 1% px off | 54/85 pages same size, median 6% px off (the original needs Skia for XPS images; use `X-Ray.PS.Skia`) |

## Cancellation, streams, limits, recovery (additions to the port)

None of this is in the Python original; it is what a host that converts untrusted files needs.
Parity output is unchanged (re-checked against the previous build on all 645 PS/EPS/XPS corpus
inputs: PDFs, PNGs and render models byte-identical apart from the PDF timestamp).

- **Cancellation convention** (family-wide): every public conversion has an overload with
  `CancellationToken` last; it throws `OperationCanceledException`, never a package exception or a
  warning. Internals do not take the token as a parameter: `Internal/Guard` publishes it per
  thread for the duration of a public call (`Guard.Begin`), and decoders, parsers, rasterizer,
  PDF writer and XPS renderer poll `Guard.Check()` (per row, record, object batch). The
  interpreter's large-stack thread (`RunWithLargeStack`) republishes it. New loops over
  input-controlled counts must call `Guard.Check()` / `Guard.Every`. Broad `catch (Exception)`
  sites must exclude `OperationCanceledException` (and `PsResourceLimitError` where the
  interpreter is on the stack); `stopped` does not catch `PsResourceLimitError`.
- **`PsInterpreterLimits`** (`PdfSaveOptions.Limits`, `ImageSaveOptions.Limits`): `MaxOperators`
  counts executed objects and procedure calls (every loop passes through `PsInterpreter.Tick`, so
  `{} loop` is bounded), `MaxOperandStackDepth`, `MaxExecutionDepth` (default 985, the original's
  recursion limit, so recursion fails as `HostErrorException` `RecursionError` exactly as before),
  `MaxNestingDepth` (parser), `MaxMemoryBytes` (each allocation a program asks for and every decoded
  filter or image buffer through `Guard.Charge`, plus a periodic managed-heap growth check; for
  rasterization, the page buffer: 4 GiB when no `Limits` is set), `MaxSuppressedErrors`.
- **`SuppressErrors`** (PDF and image options; warnings go to `Warnings`): PostScript abandons the
  page that raised the error (keeping what it drew), skips source objects up to the next
  `showpage`, runs that `showpage` and continues; a limit error ends the job and keeps the pages so
  far. XPS skips the element that failed, rolling back its half-drawn commands, and a page whose XML
  cannot be read. Cancellation is never suppressed.
- **Streams**: `PsDocument.ToPdf(Stream, ...)`, `XpsDocument.ToPdf(Stream, ...)`, `ToImage(..., Stream)`,
  `ToImages(..., Func<int, Stream>)`, `ImageToEps.Convert(..., Stream)`. The PDF writer builds each
  object as a thunk and writes it in object-number order (so the bytes equal the old
  `byte[]` path) keeping only the xref offsets; the render model itself is still in memory.
  PNG is encoded straight into the stream (`RasterEncoders.WritePng`); JPEG, BMP and TIFF encoders
  need the whole image. `IRasterWriter` has default-implemented `Write(..., CancellationToken)` and
  `Write(..., Stream, CancellationToken)` members; the Skia writer overrides them.
- **`PdfSaveOptions.JpegQuality`** re-encodes raw 8-bit gray/RGB image XObjects (never masks,
  CMYK, or already-compressed data) as JPEG when the result is smaller than the deflated pixels.
- Tests: `CancellationTests` (loops cancelled from another thread, limits, recovery, stream
  equality, JPEG), plus suppressed-error cases in `RobustnessTests`. The xUnit1051 analyzer wants
  a token on every call to a method that accepts one; existing tests pass
  `TestContext.Current.CancellationToken`, `CancellationTests` disables the rule for the file
  because it exercises the token-less overloads on purpose.
- Not covered: the Type 1 resource regexes in `PsPipeline` (linear but not interruptible), the XML
  parser for one part, and the CLR's own zlib.

## Known gaps and deviations

- XPS `ContextColor` with an ICC profile uses the fallback formula (the original uses Little CMS):
  `OSHARED-36950.xps` differs in its render model and raster. Patchy's ICC code
  (`XRay.Psd/.../Icc`) could be reused.
- XPS opacity masks are rasterized through an injectable `IRasterWriter` (`PdfSaveOptions.
  MaskRasterizer`); the default is the managed writer, the original used Skia. Pass
  `SkiaRasterWriter` to match it. Without it, `canvas-opacity-mask`/`glyphs-opacity-mask` differ.
- JPEG XR is unsupported (also fails in the original without its optional codec).
- Skia text rasterization depends on the installed Skia/FreeType, so glyph edges can differ by
  small amounts across platforms.
- The original's XPS path-data parser can loop forever on malformed `C`/`S` data; the port breaks
  out of the loop.
- Gradient caches keyed by Python object id cannot be replicated; the port keys by reference.
- Python tests that fail against the original itself (parser arrays, pipeline page size,
  concat y, Type 3 charpath, widthshow operand order) are ported pinning the shared behaviour,
  with a comment at each.

## Tests

Test count: 136 xUnit tests before the cancellation work (135 run by default), now 179 (178 run)
(`CancellationTests` and two robustness cases added); ported from the Python `tests/ps`, `xps`, `pdf` and `image` folders (MCP, build and
metrics tests dropped), plus robustness and an opt-in corpus sweep (`XRAY_PS_PARITY=1`).
