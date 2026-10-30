# X-Ray.3D: 3D and CAD files for .NET

`X-Ray.3D` (assembly and namespace `XRay.ThreeD`, identifies 3D and CAD
files, extracts their metadata (part and assembly names, component files, layers, units, document properties,
previews) and converts their geometry to **binary glTF (`.glb`)**, the format browsers load directly. Pure managed
code, no dependencies beyond the BCL, no native binaries.

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
| Wavefront OBJ | `.obj` | objects, material names, `mtllib` | objects as nodes, material groups, normals, texture coordinates; with companions the MTL's `map_Kd` image (options `-s`, `-o`, `-clamp`; BMP and TGA converted to PNG) |
| PLY | `.ply` (ASCII, binary LE/BE) | comments | triangles, vertex colours, normals; no faces = point cloud |
| glTF 2.0 | `.glb` `.gltf` | scene, node names, extras, materials, generator | node tree, shared meshes, PBR base colours, `TEXCOORD_0` and base-colour textures (samplers, `KHR_texture_transform`; images from buffer views, data URIs or companions) |
| 3MF | `.3mf` | title, designer, units, materials | objects, components, build transforms, base-material colours, `texture2d`/`texture2dgroup` textures |
| AutoCAD DXF | `.dxf` (ASCII) | version, units, layers (colour, visibility), blocks, text, entity counts, dates | one node per layer; lines, polylines, arcs, splines, hatch outlines as lines; 3D faces, polyface meshes, MESH, solids as triangles; blocks and arrays expanded |
| STEP | `.step` `.stp` `.p21` | header, product tree, descriptions, versions, materials, layers, units | B-rep faces triangulated (planes, quadrics, tori, B-spline, revolution, extrusion; faceted B-reps), NAUO assembly instances, STYLED_ITEM colours; area and volume match OpenCASCADE within 0.3% |
| IFC | `.ifc` `.ifczip` (STEP physical file) | header, schema, units, project/site/building/storey tree, GlobalIds, property sets and quantities (node extras), materials | face sets (triangulated, polygonal), faceted B-reps and surface models, extruded solids (rectangle, circle, hollow, polyline and indexed-curve profiles with voids), mapped items, local placement chains; other representations leave a node and a warning |
| IGES | `.igs` `.iges` | start and global sections (names, units, author, dates, version), entity counts by type | lines, arcs, copious-data paths and rational B-spline curves sampled as line meshes by level and colour, placed by transformation matrices; surfaces and B-rep faces are counted and reported, not tessellated |
| FreeCAD | `.FCStd` | document properties, every object (label, type, properties), Group/Links/origin tree, placements, colours, preview thumbnail | objects with meshes embedded as STL, OBJ, PLY, OFF or 3MF; B-rep shapes (`.brp`) and `.bms` meshes are listed, not drawn |
| G-code | `.gcode` `.gco` `.nc` `.ngc` | slicer and header values, layer count and height, units, move and arc counts | G0/G1/G2/G3 (centre and radius arcs, three planes, helical), absolute/relative, G20/G21, G92; line meshes: extrusion and travel (printer) or cut and rapid (CNC) |
| ZIP packages | `.zip` | the primary model's metadata, plus `container`, `entry` and the other models in the package (`ComponentFiles`) | the model with the best extension (glb, gltf, 3mf, usdz, fbx, dae, obj, ply, stl, step, ... last gcode; largest of a kind) is loaded with the package as its companions: an OBJ finds its `.mtl` (diffuse colour, opacity, shininess) by relative path or file name; a model that fails falls through to the next; `LoadOptions.ResolveCompanion` does the same for loose files |
| Sweet Home 3D | `.sh3d` (Home.xml) | name, version, levels, walls, rooms, the furniture list with catalogue ids, sizes, prices | furniture from its embedded OBJ model fitted to width, depth and height (shared meshes), a box when the model is missing; walls as boxes, rooms as floors; levels as layered nodes |
| FBX | `.fbx` (binary, ASCII) | scene, node and material names | meshes, node transforms, materials |
| COLLADA | `.dae` | asset, units, up axis, node names | geometry, scene graph, effect colours |
| X3D / VRML | `.x3d` `.wrl` | titles, meta, DEF names | shapes, transforms, colours |
| USD | `.usda` | stage metadata, prim names, units | meshes, xforms, display colours |
| 3DS, AMF, OFF | `.3ds` `.amf` `.off` | names, units, materials | triangles, colours |
| Point clouds | `.las` `.pcd` `.xyz` `.pts` | header, point counts, bounds, CRS where given | points with colours |
| Recognised only | DWG, 3DM, ... | the format is named | `UnsupportedModelFormatException` |

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

## Output profile for a renderer

`GlbWriteOptions` selects what the GLB carries; the defaults suit CAD:

| Feature | How | Option |
|---|---|---|
| Assembly structure | one node per component instance with its transform and properties; one mesh per unique part shared by its instances | always |
| Face identity (picking, highlighting) | `mesh.extras.faceRanges`: `(first index, index count)` per source face, indices in primitive order | `IncludeMetadata` |
| Colours and materials | one primitive per material, PBR base colour, metallic/roughness (SolidWorks: from the saved appearance, metals detected by family) | always |
| Textures | `TEXCOORD_0`, one embedded PNG/JPEG/WebP image per used texture, samplers (wrap, filter), `KHR_texture_transform` when a transform is set (glTF convention: v grows downwards; OBJ and 3MF readers flip v) | always |
| Edges | a `LINES` primitive per CAD mesh (`extras.role = "edges"`): face boundaries, open borders, creases over the crease angle | `Edges` (`Auto`, `None`, `All`) |
| Size | `Quantize`: 16-bit positions with the dequantisation folded into the node matrices, 8-bit normals and colours. `Meshopt`: also meshopt-compressed vertices and indices with octahedral normals (about 4x smaller than plain for the sample assembly) | `Compression` |
| Draw calls | `Gpu`: sibling instances of one mesh merged with `EXT_mesh_gpu_instancing`; names and properties move to `extras.instances` | `Instancing` (uncompressed output only) |
| Lazy loading | `AssemblyPackage`: `assembly.json` (instance tree, metadata) plus one GLB per unique part | `AssemblyPackage.Create` |

`Quantize`, `Meshopt` and `Gpu` files declare their extensions as required (three.js loads them with the meshopt decoder set).
Reading handles them all, so `ModelLoader.Load` accepts what `GlbWriter` writes.

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
