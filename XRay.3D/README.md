# X-Ray.3D: 3D and CAD files for .NET

`X-Ray.3D` (assembly and namespace `XRay.ThreeD`, because `3D` is not a valid identifier) identifies 3D and CAD
files, extracts their metadata (part and assembly names, component files, layers, units, document properties,
previews) and converts their geometry to **binary glTF (`.glb`)**, the format browsers load directly. Pure managed
code, no dependencies beyond the BCL, no native binaries.

It re-implements, without any web technology, what a browser-based universal 3D viewer does with a file before it
draws it. A Tesserae component that displays the GLB is planned next (see [TODO.md](TODO.md)).

```csharp
using XRay.ThreeD;

// Metadata only: cheap, no mesh is built.
ModelMetadata meta = ModelLoader.ReadMetadata("gearbox.sldasm");
Console.WriteLine($"{meta.Format.Name} {meta.DocumentKind}, {meta.Application}");
foreach (var file in meta.ComponentFiles) Console.WriteLine($"needs {file}");
foreach (var p in meta.Properties) Console.WriteLine($"{p.Name} = {p.Value}");

// Convert: bytes in, GLB bytes out.
byte[] glb = ModelLoader.ConvertToGlb(File.ReadAllBytes("gearbox.sldasm"), "gearbox.sldasm");

// Or keep the scene graph and inspect it first.
Model3D model = ModelLoader.Load("gearbox.sldasm");
foreach (var node in model.Nodes()) Console.WriteLine(node.Name);
File.WriteAllBytes("gearbox.glb", GlbWriter.ToArray(model));
```

A command line tool lives in `tools/XRay.ThreeD.Cli`: `xray3d info <file> [--json]`, `xray3d convert <file> [out.glb]`,
`xray3d formats`.

## Formats

| Format | Extensions | Metadata | Geometry |
|---|---|---|---|
| SolidWorks 2015+ | `.sldprt` `.sldasm` `.slddrw` | document and custom properties, configurations, features, part names, component files, preview | the display mesh SolidWorks saved, assemblies as instances placed by their transforms |
| STL | `.stl` (binary, ASCII) | solid name | welded triangles |
| Wavefront OBJ | `.obj` | objects, material names, `mtllib` | objects as nodes, material groups, normals |
| PLY | `.ply` (ASCII, binary LE/BE) | comments | triangles, vertex colours, normals; no faces = point cloud |
| glTF 2.0 | `.glb` `.gltf` | scene, node names, extras, materials, generator | node tree, shared meshes, PBR base colours |
| 3MF | `.3mf` | title, designer, units, materials | objects, components, build transforms, base-material colours |
| Recognised only | STEP, IGES, DXF, DWG, IFC, 3DM, FBX, DAE, USD, ... | the format is named | `UnsupportedModelFormatException` |

`ModelFormats.All` lists everything the library can name. Detection looks at the content first (magic bytes, ZIP
entry names) and the file name second.

## Why GLB

glTF 2.0 carries a node hierarchy with names, shared meshes (instancing), PBR materials, and arbitrary `extras`
for per-node and per-model metadata; every web 3D engine loads it, and the reference viewer's own exporter and
HTML export use it. Other targets lose the assembly structure (STL, OBJ, PLY are flat) or are print- or AR-oriented
(3MF, USDZ). The writer puts the source's units and up axis in `asset.extras`, and **normalises to meters and Y-up
with a root node transform**, so vertex data stays as stored.

What goes where in the GLB:

* `asset.extras`: format, document kind, application, title, author, dates, `sourceUnits`, `sourceUpAxis`,
  `partNames`, `layers`, `configurations`, `materials`, `componentFiles`, `properties[]`.
* `node.extras`: per-node properties (SolidWorks: `instancePath`, `file`; 3MF: `partNumber`; glTF: the source extras), `layer`.
* An instanced part is one glTF mesh referenced by several nodes.

## Robustness

Every reader takes hostile input: bounds-checked reads, limits on size and triangle count (`LoadOptions`),
cancellation, and nothing but `ModelFormatException` escapes a load. Non-finite coordinates are repaired.
Truncation and mutation tests run over every loadable fixture.

## Layout

| Path | What |
|---|---|
| `src/XRay.ThreeD/` | the library: `Model/` scene model, `Formats/` readers, `Output/GlbWriter`, `ModelLoader` |
| `tests/XRay.ThreeD.Tests/` | xUnit v3 on Microsoft.Testing.Platform |
| `tools/XRay.ThreeD.Cli/` | `xray3d` |
| `tools/fixtures/`, `tools/oracle/` | fixture generator; golden-value generator for the SolidWorks tests |
| `dataset/` | test files with licences, see `dataset/README.md` |
| `docs/` | `solidworks-format.md` and `research/` (notes on the reference viewer) |

Licence: MIT. Dataset files keep their own licences (`dataset/manifest.json`).
