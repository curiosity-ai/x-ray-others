# XRay.3D: 3D and CAD files for .NET

Published as `X-Ray.3D` (assembly and namespace `XRay.ThreeD`). Identifies 3D/CAD formats, extracts metadata and
converts geometry to GLB. No package dependencies; the BCL only. Read `README.md` for the public surface,
`TODO.md` for the plan, `docs/solidworks-format.md` before touching the SolidWorks reader and `docs/catia-format.md` before touching the CATIA one.

**Provenance.** A re-implementation, in C# and without web technology, of the parts of a browser 3D viewer that
matter for metadata extraction and conversion. The reference viewer's JavaScript was reviewed and its WebAssembly
readers were decompiled; what was learned is written down in `docs/`. **Do not name the reference viewer or its
publisher anywhere** (code, comments, docs, package metadata, commit messages, dataset manifests): call it "the
reference viewer" and its SolidWorks reader "the reference reader". No code was copied; the wasm binaries are not
in this repository. `LICENSE` is MIT for Curiosity GmbH.

## Layout

| Path | What |
|---|---|
| `src/XRay.ThreeD/Model/` | `Model3D`, `SceneNode`, `MeshData`, `ModelMetadata`, `FormatInfo`, exceptions |
| `src/XRay.ThreeD/Formats/` | `ModelFormats` (registry + detection), `SolidWorks/`, `Mesh/` (STL, OBJ, PLY, glTF, 3MF, OFF, AMF, 3DS, COLLADA, X3D/VRML, FBX, USDA), `Cad/` (DXF, IGES, FreeCAD), `Bim/` (IFC, Sweet Home 3D), `Toolpath/` (G-code), `Packages/` (ZIP archives: primary model and companions), `PointCloud/` (LAS, PCD, XYZ), `Step/` (Part 21 parser, geometry, tessellator, reader), `Catia/` (CATIA V5: container, metadata, display tessellation) |
| `src/XRay.ThreeD/Output/` | `GlbWriter` (the output format and its profile), `SvgWriter` (drawings: layers, text, fills), `AssemblyPackage` (manifest + per-part GLBs) |
| `src/XRay.ThreeD/Internal/` | `Triangulator` (constrained Delaunay), `MeshoptEncoder`/`MeshoptDecoder` (ports, see `THIRD_PARTY_NOTICES.md`), CRC and binary helpers |
| `src/XRay.ThreeD/ModelLoader.cs` | entry point: `Detect`, `ReadMetadata`, `Load`, `ConvertToGlb`; the exception guard and sanitiser |
| `tests/XRay.ThreeD.Tests/` | `SolidWorksTests` (golden), `MeshFormatTests`, `GlbWriterTests`, `FormatTests`, `RobustnessTests` |
| `dataset/` | committed fixtures (about 30 MB); `manifest.json` has licence, source and sha256 of each |
| `docs/benchmarks.md` | the timing harness (`BenchmarkTests`, skipped unless `XRAY_3D_BENCH` is set) and its reference numbers |
| `tools/oracle/make_dxf_golden.py` | regenerates `dxf-golden.json` from ezdxf (an independent DXF reader) |
| `tools/oracle/make_golden.mjs` | regenerates `tests/.../Corpus/solidworks-golden.json` from the reference reader (not in the repo) |
| `tools/fixtures/make_fixtures.py` | writes `dataset/synthetic/` byte for byte |

## Commands

Run from the x-ray-others root so `global.json` applies:

```sh
dotnet build XRay.3D/XRay.3D.slnx -c Release          # must report 0 warnings
dotnet test --solution XRay.3D/XRay.3D.slnx -c Release
XRAY_3D_MUTATIONS=600 dotnet test --project XRay.3D/tests/XRay.ThreeD.Tests -c Release -- --filter-class "*RobustnessTests"
dotnet pack XRay.3D/src/XRay.ThreeD -c Release -o artifacts
dotnet run --project XRay.3D/tools/XRay.ThreeD.Cli -c Release -- info XRay.3D/dataset/sldasm/nist-mtc-assembly.sldasm
python3 -I XRay.3D/tools/fixtures/make_fixtures.py XRay.3D/dataset/synthetic
```

## Rules

- **Pure managed.** No native code. Formats needing a native engine would go in a separate optional package.
- **Untrusted input.** Bounds-check every read, bound every loop and recursion, enforce `LoadOptions` limits, honour
  the cancellation token, and let only `ModelFormatException` (and `OperationCanceledException`) escape. Anything a
  reader returns must be writable as glTF: `ModelLoader.Sanitize` repairs non-finite numbers and rejects bad indices.
  New readers get truncation and mutation coverage by adding their fixture to `RobustnessTests.Fixtures`.
- **Cancellation.** `LoadOptions.Cancellation` (and `GlbWriteOptions.Cancellation`, `AssemblyPackageOptions.Cancellation`) must
  reach every parser and writer. Any loop whose bound comes from the file (record, chunk and entity tables, per-point, per-face and
  per-vertex loops, recursion over references, inflate and codec loops, tessellation and triangulation) calls
  `Cancel.Every(token, i)` (`Internal/Cancel.cs`; every 1024 iterations by default, never every item in a hot loop), and every
  stage start checks the token. Cancellation surfaces as `OperationCanceledException`, never `ModelFormatException`;
  `CancellationTests` covers every fixture and a few heavy inputs (stop within seconds).
- **Allocations.** Hot paths (parsers, tessellation, codecs, writers) rent scratch buffers from `ArrayPool<T>.Shared` (return in
  `finally`, never hand a rented array to a caller or keep it past the call, never rely on its length), use `stackalloc` or
  `Span<T>` for small clamped temporaries (about 256 bytes, never sized from file data), slice spans instead of `Substring` and
  `ToArray`, avoid LINQ and closures in loops, and pre-size collections from validated counts.
- **Conventions.** Transforms are System.Numerics row-vector (`world = local * parent`). `MeshData` holds one welded or
  per-face vertex array and an index array in triangles; `Groups` split indices by material. Units and up axis are
  recorded in metadata, never baked into vertices (the GLB writer adds a root node).
- **Statistics** count what is drawn (instances per use) plus the unique stored counts.
- **A reader's contract.** `Read` returns a `Model3D` whose `Metadata` is complete; formats with cheap metadata
  also get a metadata-only path in `ModelLoader.ReadMetadata` (SolidWorks does). Unrecognised variants throw
  `UnsupportedModelFormatException` with a message saying what is missing.
- **Parity with the reference.** SolidWorks results are compared with the reference reader's on every dataset file
  (counts, bounds, coordinate sums). When a decoder changes, regenerate nothing: the golden file is the spec. When adding
  a file to the dataset, regenerate it with `tools/oracle/make_golden.mjs <engine-dir> dataset`.
- **Dataset.** Add files only with a licence that permits redistribution and an entry in `dataset/manifest.json`
  (do not name the reference viewer as a source). Keep single files small; skip gaussian-splat and point-cloud giants.
- **Zero build warnings.** Fix the cause.

## Adding a format

1. A reader in `Formats/<Name>/`: `static Model3D Read(ReadOnlyMemory<byte>, LoadOptions)`, filling `Metadata`.
2. Move its `FormatInfo` from the identified list to a public property in `ModelFormats`, set `FormatSupport.Full`, add detection.
3. One line in `ModelLoader.Dispatch`.
4. Fixtures, tests (round trip through `GlbWriter`), robustness entry, a row in `README.md`, a tick in `TODO.md`.

## Publishing

`.devops/build-x-ray-others-nuget.yml` publishes this package with the rest of the repository (one version for all).
See the repository `CLAUDE.md`. Adding the family entry (`Members.props`, docs section) in curiosity-ai/x-ray is
still open: see TODO.md.
