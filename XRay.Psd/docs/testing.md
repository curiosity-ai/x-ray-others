# Testing

How the test suite is organized, which fixtures it uses, and how to run the robustness checks.

## Fixtures

The PSD and PSB fixtures are not in this repository. They live in the `psd/` folder of the shared X-Ray fixture corpus, [curiosity-ai/test_documents](https://github.com/curiosity-ai/test_documents), with the psd-tools subset in `psd/psd-tools/`; x-ray-others carries that repository as the `test_documents` submodule:

```bash
git submodule update --init --depth 1 test_documents
```

`tests/XRay.Psd.Tests/Support/Fixtures.cs` (linked into all three test projects) resolves the corpus root: `XRAY_TEST_DOCUMENTS` when it is set, otherwise the first non-empty directory named `test_documents` found walking up from the test output directory (the submodule, or a clone next to x-ray-others). When no corpus is found, every test that needs a fixture is skipped with a message saying how to get it, and data-driven theories collapse to one skipped row. When the corpus is there but a file is missing, the test fails. The synthetic `PsdBuilder` tests and the bundled Liberation Sans face in `tests/XRay.Psd.Text.Tests/Fonts/` need no corpus.

To add a fixture, commit it to `psd/` in test_documents (with its license if it is third-party, see the `README.md` there), then bump the submodule here.

## Suites

- `tests/XRay.Psd.Tests`: the core library. Every fixture in `test_documents/psd` is parsed, decoded, truncated and mutated; renders are compared with Photoshop's merged image and reference BMPs (`RenderingTests`); formats without a fixture are covered by `Support/PsdBuilder.cs`, which writes synthetic PSD and PSB files.
- `tests/XRay.Psd.Skia.Tests`: SkiaSharp interop, Skia decoding of the built-in PNG and JPEG output, thumbnail decoding.
- `DiagnosticsSurvey` (opt-in, `XRAY_PSD_SURVEY=1`): compositor versus merged image versus BMP for every fixture, written to `test-output/survey.txt`.
- `BandRenderingTests`: band rendering against the whole render for every fixture (and their truncated and mutated copies), and the streamed PNG against `ToPng`.
- `LargeDocumentTests` (opt-in, `XRAY_PSD_LARGE=1`, about two minutes): a sparse 50000 x 43000 PSB written to the temporary directory, with channels and a merged image past 2 GB, rendered in bands and deleted.

## Cancellation

`CancellationTests` covers the token on every entry point: a cancelled token throws `OperationCanceledException` from load (bytes, memory, stream, path, memory-mapped, async), render, bands, PNG and JPEG output, text extraction, merged image, layer pixels, the encoders and the thumbnail writer; cancelling part way stops the compositor between layers (a `LayerVisibility` callback cancels on its fourth call), a band sequence between bands, a parse of 60000 layer records (timer), a load whose stream cancels while it is read, and `Parallelism.For` between strips at degrees 1 and 4. A token that is never cancelled changes nothing: renders, bands, PNG bytes, merged image, text and layer pixels are compared with and without it. The stream outputs are compared with the `byte[]` forms (JPEG bytes equal; PNG pixels equal, bytes equal for one deflate segment). The xUnit1051 analyzer, which asks for a token on every call to a method that accepts one, is disabled in the three test projects: the existing tests call the token-less forms on purpose.

## Robustness

The rule in CLAUDE.md is that only `PsdFormatException` may escape `PsdDocument.Load`, `Render` and `ExtractText`. Three layers check it.

1. `ParsingTests.Every_fixture_survives_truncation` cuts every fixture at 10, 50 and 90 percent.
2. `MutationFuzzTests` is a dependency-free randomized test. For each fixture up to 96 KB it applies six seeded mutations: bit flips, truncation, 16 and 32-bit boundary values, corrupted section and resource length fields, and random byte runs. Three times in four the mutation lands inside a structured payload (an image resource, a tagged block, blending ranges or the color mode data) found by loading the fixture and mapping the payload slices back to file offsets, because random offsets mostly hit pixel data. Seeds come from splitmix64 and an FNV-1a hash of the fixture name, so a failure names a seed that reproduces it. Each mutated file is loaded, its text extracted, its typed resources and layer styles read, and it is rendered from the merged image and from the layers (skipped when a mutated header claims a canvas far larger than the original). The default run takes a few seconds. A deeper local run covers every fixture:

   ```bash
   XRAY_PSD_MUTATIONS=200 dotnet test --project tests/XRay.Psd.Tests -c Release -- --filter-class "*MutationFuzzTests"
   ```

3. `fuzz/XRay.Psd.Fuzz` is a coverage-guided harness built on SharpFuzz (the only project allowed that dependency). See below.

A 150-iteration run over the small fixtures, a 60-iteration run over all of them, and 20 to 250 mutations of each file in the psd-tools collection found no escaping exception at the time of writing.

## Fuzzing with SharpFuzz

The harness runs the same sequence as the mutation test (load, text, resources, styles, both render paths, band rendering) and treats any exception other than `PsdFormatException` as a crash. It has three modes: libFuzzer (default), AFL (`--afl`), and replay (`XRay.Psd.Fuzz file...`) for reproducing a saved crash without a fuzzer.

One-time setup:

```bash
dotnet tool install --global SharpFuzz.CommandLine
# libFuzzer driver for .NET: https://github.com/Metalnem/libfuzzer-dotnet/releases
```

Build, then instrument the library in the harness output folder (instrument again after every build):

```bash
dotnet build fuzz/XRay.Psd.Fuzz -c Release
sharpfuzz fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.dll
```

Seed a corpus with small fixtures, then run libFuzzer:

```bash
mkdir -p local-test-fixtures/fuzz/corpus local-test-fixtures/fuzz/crashes
cp ../test_documents/psd/photoshop-basic.psb ../test_documents/psd/photoshop-shape-solid.psd local-test-fixtures/fuzz/corpus/
./libfuzzer-dotnet --target_path=fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz \
  -timeout=20 -rss_limit_mb=6144 -artifact_prefix=local-test-fixtures/fuzz/crashes/ local-test-fixtures/fuzz/corpus
```

or AFL++:

```bash
afl-fuzz -i local-test-fixtures/fuzz/corpus -o local-test-fixtures/fuzz/findings -t 20000 -m none \
  dotnet fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz.dll --afl
```

Replay a finding with `dotnet fuzz/XRay.Psd.Fuzz/bin/Release/net10.0/XRay.Psd.Fuzz.dll <file>`; the stack trace shows where the check is missing. Keep fixes narrow (an explicit bounds or count check that throws `PsdFormatException` or skips the block) and add the input, or a `PsdBuilder` reconstruction of it, as a test.

## psd-tools fixtures

The reference repository has no Photoshop-saved files in 16 or 32-bit, grayscale, indexed, Lab, duotone, multichannel or bitmap mode, and no embedded PSB smart object. The psd-tools project (MIT, copyright Kota Yamaguchi) has small single-feature files for all of them. The 46 files the tests use (7.4 MB) are committed under `test_documents/psd/psd-tools/` with the psd-tools `LICENSE`, keeping their paths relative to psd-tools' `tests/psd_files/`; `NOTICE` at the repository root credits them. They come from psd-tools commit `605ee1284952c18c649ba60ebe73df4dabca9fbb`, the one `.reference/testy/fetch_psd_tools_corpus.py` pins.

`PsdToolsCorpusTests` runs over them: every `colormodes/` file loads with the expected mode and depth, its layer names are extracted, and the layer compositor matches the merged image; `smart-object-slice.psd` exposes its embedded PSB and text extraction recurses into it; `layer_comps.psd` (comps and per-layer comp state), `metadata.psd` (guides, version 8 slices) and `slices.psd` pin the typed resources; `masks.psd` pins the `Txt2` caps key against its TySh runs. `ColorAdjustmentTests` uses `fill_adjustments.psd` (adjustment chain) and `effect-stroke-gradient.psd` (noise gradient); `AdjustmentTests` and `ColorManagementTests` use five files of `adjustments/` (CMYK and gray adjustments on the inks, the real SWOP and Dot Gain 20% profiles, Threshold and 16-bit Posterize). `EffectCorpusTests` uses `transparency/knockout-*.psd` (knockout) and `layer_params.psd` (contours, gradient glow); `LegacyEffectTests` uses `effects/blend-modes.psd` (the `lrFX` mirrors). `GradientTests` strips the stored pixels of the gradient fills in `gradient-styles.psd`, `gradient-sizes.psd`, `masks3.psd`, `colorprofiles/north_america_newspaper.psd` and the 4x4 `colormodes/` files and checks that the regenerated fills match Photoshop's; `effects/stroke-composite.psd` pins the two-stop overlay easing. `Fixtures.PsdTools(path)` resolves a file. The full collection (356 files, 51 MB) is not committed; for wider mutation runs, sparse-check out `tests/psd_files/` of that commit into the gitignored `local-test-fixtures/psd-tools/` (at the repository root or any folder above it). With it present, `PsdToolsCorpusTests.Psd_tools_adjustment_collection_matches_photoshop_if_available` checks all 39 files of `adjustments/` against Photoshop's merged images; without it those cases skip.

## ag-psd fixture

The psd-tools collection has no type on a path. ag-psd (MIT, copyright Agamnentzar) has one Photoshop file with it, `test/read/text-path/src.psd`, committed as `test_documents/psd/ag-psd/text-path.psd` (48 KB) with ag-psd's `LICENSE` and credited in `NOTICE`. `Fixtures.AgPsd(name)` resolves it; `TextEngineTests` reads its path and `FixtureRenderTests` re-renders it with the bundled Carlito (Calibri's metrics).

## Corpus text check

`ExtractText` over all 356 psd-tools files raises nothing, and its layer names and type-layer text equal psd-tools' own (`PSDImage.descendants()`, `layer.text`) except where this library names unnamed layers "Layer N" as Photoshop shows them.
