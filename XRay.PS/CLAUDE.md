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

136 xUnit tests (135 run by default); ported from the Python `tests/ps`, `xps`, `pdf` and `image` folders (MCP, build and
metrics tests dropped), plus robustness and an opt-in corpus sweep (`XRAY_PS_PARITY=1`).
