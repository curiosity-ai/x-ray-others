# TODO

Status as of the first commit: the model, GLB writer, six readers, detection, CLI, 74 tests and the dataset are in.
Ordered by value for the stated goals (metadata extraction, then conversion for a browser viewer, then the Tesserae component).

## Done

- [x] Rendering profile: face ranges, feature edges, PBR materials, quantisation, meshopt, GPU instancing, assembly package (manifest + one GLB per unique part)
- [x] Review of the reference viewer: formats, features, export menu, WebAssembly readers (`docs/research/`)
- [x] Target format decision: GLB, normalised to meters and Y-up by a root node, metadata in `extras`
- [x] Scene model, metadata model, format registry and detection (content first)
- [x] SolidWorks 2015+: container, properties, configurations, features, component files, preview, display mesh with
      instancing; identical to the reference reader on all dataset files (`docs/solidworks-format.md`)
- [x] STL (ASCII, binary), OBJ, PLY (three encodings, point clouds), glTF/GLB read, 3MF
- [x] `GlbWriter`: node tree, shared meshes, material primitives, vertex colours, points, extras, 16/32-bit indices; validated with an external glTF validator
- [x] Test dataset (`dataset/`, manifest with licences), golden values, truncation/mutation tests
- [x] CLI (`info`, `convert`, `formats`)

## Next: finish SolidWorks

- [x] Per-face colours and materials (colour is stateful: a face takes the latest appearance record before it; matches the reference reader on every face)
- [ ] 2024+ saves: the `FaceTessellations/` store when `DisplayLists` is absent. **Blocked**: no 2024+ file is in the dataset or obtainable under a redistributable licence, and the stream's layout is not documented in `docs/` (the reference reader's wasm is not in this repository); guessing a binary layout without a fixture would be unverifiable
- [ ] Compound-file envelope (SolidWorks 2014 and before; `__ZLB` zlib streams): a small CFB reader. **Blocked**: needs a pre-2015 fixture, and none is in the dataset or obtainable under a redistributable licence
- [x] Drawings: `Images/Sheet_*` and the `Preview` DIB (8/24/32 bit, RLE8) to PNG with the letterbox trimmed; sheets are `ModelImage`s at the paper size read from the sheet record, `Metadata.DrawingSheets`, SVG output with the sheet embedded as a data URI. Open: the drawing's vector views and dimensions
- [x] Assemblies with external components: `LoadOptions.ResolveCompanion` supplies the part files (by the name the assembly lists), merged once per file and shared by every instance under its transform; nested assemblies to three levels. Open: transforms for assemblies whose display lists carry no instance records at all (they live in a stream not yet decoded), suppressed components
- [x] A metadata-only path that does not inflate `DisplayLists`: `SwContainer.TryOpen(verify: false)` reads headers and rejects decoys by overrun, streams are CRC-checked when read; `ReadMetadata` reports geometry presence without counts

## Next: more formats, in order of value / effort (see `docs/research/formats.md` for the viewer's approach)

- [x] STEP (B-rep, assembly, colours, layers on nodes, units, AP242 tessellated entities, mapped items; open: tessellated edges/curve sets, repositioned tessellated items, layers on single faces): assembly tree (PRODUCT, NEXT_ASSEMBLY_USAGE_OCCURRENCE), names, units, colours, layers, properties; metadata first (S-M), then a B-rep tessellator (XL: NURBS, trimmed surfaces); start with faceted and AP242 tessellated geometry
- [x] DXF (ASCII): layers, units (`$INSUNITS` per the AutoCAD table; the reference viewer maps 4, 5 and 6 all to mm), blocks and arrays, lines/polylines/arcs/splines/hatch outlines, 3D faces, polyface and MESH; checked against ezdxf. Open: binary DXF, hatch fills, text as geometry, ACIS solids
- [x] IGES: metadata, curve geometry (110, 100, 106, 126, 102, 124) and surfaces: 128, 144, 143, 142/141 boundaries, 108/120/122, quadrics 190 to 198, B-rep 186/514/510/508/504/502, checked against OpenCASCADE's IGES reader. Open: 114 parametric spline and 118 ruled surfaces, 112 splines, 108 bounded planes drawn free-standing, instances (408) and subfigures, per-face colours given by 314 on loops
- [~] DWG: version detection (AC1.x to AC1032), code page, section locator table, preview image (AC1012 to AC1018 header address; synthetic fixtures, the two sample files carry none), layers of AC1012 to AC1015 via the object map (checked against the DXF twin of the sample drawing). **Blocked / open**: geometry, blocks and units of any release (the R2000 header-variable and entity bit streams are not decoded: `$INSUNITS` sits after some 250 variables whose exact order cannot be verified without a second reader for DWG), AC1018+ page decompression (LZ77 variant, encrypted system page), summary info (AcDb:SummaryInfo, a compressed section from AC1018) and AC1021+ previews (Reed-Solomon coded pages)
- [x] IFC (STEP physical file): spatial tree, names, GUIDs, property sets and quantities, units, styles; face sets, faceted B-reps, extrusions, mapped items. Open: openings and boolean operations (first operand drawn), swept disks, revolved and B-spline solids, IfcXML
- [x] FBX, COLLADA, 3DS, USDA, USDC (crate 0.4+) and USDZ, VRML/X3D, OFF, AMF (USD open: cross-layer references, payloads, variants, time samples, textures)
- [x] FreeCAD (`.FCStd`): document, object tree, properties, placements, colours, thumbnail, embedded STL/OBJ meshes. Open: tessellating `.brp` (OpenCASCADE text BREP), decoding `.bms` meshes, Link/App::Link placement
- [x] OpenSCAD: language evaluator, primitives, extrusions, hull, BSP-tree difference/intersection (checked by closed-form volumes and a sampled estimate of the rook). Open: minkowski, projection, text, import, 2D boolean clipping, rounded offsets, a faster boolean for twisted surfaces (the vase takes seconds and fragments to 130k triangles)
- [x] Sweet Home 3D (`Home.xml`, 5.3+): levels, walls, rooms, furniture with fitted OBJ models. Open: the Java-serialised `Home` of older files, curved walls, doors cut into walls, textures
- [x] Rhino 3DM (archive 50+, Rhino 5 to 8): layers, materials, attributes (name, colour, material, visibility, mode, user strings), units, blocks and references, points, point clouds, line/polyline/arc/NURBS curves, meshes, display meshes inside B-reps and extrusions; checked against rhino3dm on generated fixtures and on real Rhino 7/8 files (local only, `XRAY_3D_LOCAL_SAMPLES`). Open: archives before 50 (32-bit chunk lengths), B-reps and extrusions saved without display meshes (needs a B-rep tessellator for ON_Brep), NURBS surfaces, poly-curves, SubD, text and hatches, preview images, texture and RDK materials, double-precision mesh vertices, layer chunk versions before 1.15, user strings of the document
- [~] Inventor and Revit: document properties and thumbnails from the OLE compound file (`Internal/CompoundFile`, reusable for pre-2015 SolidWorks: see the blocked item above, which still lacks a fixture). Fixtures are synthetic (olefile-validated); a real Revit/Inventor file would confirm the `BasicFileInfo` and thumbnail stream layouts. **Skipped / not available**: SketchUp (no documented container: `.skp` is a proprietary binary stream, not a zip or CFB, and there is no fixture), JT, Parasolid, ACIS, CATIA, NX (proprietary geometry, no documented metadata container checked); geometry of every one of these
- [x] Point clouds: LAS, PCD, XYZ/PTS, LAZ point types 0 to 3 (chunked LASzip, v2 items: POINT10, GPSTIME11, RGB12, extra bytes), PLY gaussian splats. Open: LAZ point types 6 to 10 (the layered 1.4 coding: POINT14/RGB14/RGBNIR14/BYTE14 v3; not attempted, since the model and context layout cannot be verified without the public source at hand, only byte-identity against lazrs would prove it), unchunked LAZ, splat rotations and spherical-harmonic bands above DC
- [x] Archives: ZIP packages (primary model by extension priority, companions, OBJ+MTL colours). Open: RAR/7z/tar, MTL textures, loading every model of a package as one scene
- [x] G-code toolpath preview (lines by extrusion/travel or cut/rapid). Open: per-layer nodes, feature-type colours, tool numbers, canned cycles
- [ ] Archicad (metadata and thumbnail only). Not attempted: the dataset's `.pln` files are planogram text and its `.gsm` files carry a `WW-` header with no documented layout, so there is nothing to verify a reader against

## Next: conversion quality

- [x] Textures and UVs: OBJ+MTL `map_Kd` (companions, ZIP packages), glTF and 3MF textures, `TEXCOORD_0` and images/samplers in the GLB, `KHR_texture_transform`. Open: other maps (normal, emissive, metallic-roughness), UV sets above 0, UVs for FBX/COLLADA/X3D/USD/3DS/PLY readers
- [x] Meshopt decode for glTF input; meshopt compression and `KHR_mesh_quantization` for output (ported codecs, checked against an independent decoder)
- [x] Draco decode for glTF input (`KHR_draco_mesh_compression`): sequential and EdgeBreaker (standard and valence) connectivity, quantised, normal and generic attributes, all prediction schemes; checked against an independent decoder on 40 fixtures. Open: kd-tree point clouds, bitstreams older than 2.2
- [x] Smooth/flat normal policy for meshes without normals (`GlbWriteOptions.Normals`; default still leaves it to the viewer)
- [x] Unit/up-axis hints from the caller for formats that do not say (`LoadOptions.AssumedUnits`, `AssumedUpAxis`)
- [x] Far-from-origin recentring per mesh (`RecentreFarModels`; the centre is in `mesh.extras.origin` and in the node transforms). Open: one global shift for a whole assembly
- [x] Pass-through for GLB input in `ConvertToGlb` (when no options are given)
- [x] Layers on nodes where the format has them, and synthesised "part" layers (one per named part, instances share it; `LoadOptions.SynthesizePartLayers`, `ModelLayer.Synthesized`) for models with two or more parts and no layers of their own
- [ ] Streaming readers for very large OBJ/STL/PLY (inputs are currently held in memory)

## Next: Tesserae component

- [ ] `XRay.ThreeD.Tesserae` (separate package, transpiled): load a GLB, orbit/pan/zoom, assembly tree with visibility, per-node properties from `extras`, explode, section plane, measure, wireframe/edges, lighting presets, layer list
- [ ] The viewer's data-model gaps to improve on: it has no assembly tree panel and no property panel, drops node properties, and does not convert units on export

## 2D output

- [x] `Model3D.Is2D`, text and filled-area primitives, DXF TEXT/MTEXT/ATTRIB/SOLID/solid HATCH, `SvgWriter` and `ModelLoader.ConvertToSvg` (layers, strokes, text, fills, `<use>` for shared meshes, metadata), checked against ezdxf for text and fills
- [ ] DXF blocks as shared meshes (they are expanded per insert, so DXF output has no `<use>` yet), line weights, line types, hatch patterns and gradients, text styles and fonts, dimension geometry as text, paper-space layouts
- [ ] SolidWorks drawing sheets as vector content (views, dimensions; the sheets are rasters for now)

## Housekeeping

- [ ] Family registration in curiosity-ai/x-ray (`Members.props`, docs section; see its `add-family-member` skill)
- [ ] Verify the licences of anything that gets ported from a third party (the research notes flag laz-perf as unverified; the LAZ decoder is not a port of it, it was written from the public LASzip description and verified against lazrs output)
- [x] Benchmarks (large assemblies): `BenchmarkTests`, skipped unless `XRAY_3D_BENCH` is set (`docs/benchmarks.md`). Allocation pass on the Part 21 tokenizer, STEP tessellator, DXF reader, meshopt codecs and SolidWorks display-list ordering, with per-loop cancellation in the meshopt encoder and decoder. Open: STEP surface tessellation still allocates about 130 bytes per input byte; the SolidWorks display-list decode already works on spans and was left as is
- [ ] Fuzz target in the style of `XRay.Psd/fuzz`

## Dataset formats added in the second pass

- [x] B3D: the Blitz3D chunk format (TEXS, BRUS, NODE, MESH, VRTS, TRIS) with brushes, normals, vertex colours, UV set 0 and the node tree, checked against an independent chunk walk (node, vertex, triangle and brush counts of both Blitz3D samples). Open: bones and weights, animation keys, UV sets above 0, the rotation sign convention of the quaternion (assumed standard, then mirrored). The two Bazis samples are a different, proprietary container (`BZ85`, zlib streams): identified, title read, geometry not decoded
- [x] X3D classic encoding (`.x3dv`): the VRML parser with PROFILE, COMPONENT, META, IMPORT/EXPORT statements; same scene as the XML and VRML fixtures gives the same model. Open: `UNIT` statements are skipped (units stay metres), PROTO bodies
- [x] Unity package (`.unitypackage`, tar.gz): asset list and metadata, text-YAML prefabs/scenes/meshes/materials, contained model files; mesh bounds and counts checked against the `m_LocalAABB` and vertex counts Unity wrote into the file. Open: binary-serialised assets, compressed/skinned meshes, selecting the sub-mesh of a model file by fileID, `.blend` inside a package, terrain, prefab-instance overrides other than the root transform and name
- [x] Unreal `.uasset`/`.umap`: summary (UE4 and UE5 headers), names, imports, exports, tagged properties, thumbnail, and StaticMesh geometry from the editor's raw mesh bulk data (both dataset files: counts, volume and bounds checked against an independent parse). **Open**: cooked packages (FStaticMeshRenderData: LOD vertex/index buffers, needs a cooked sample), UE5 layouts (summary walked from memory of the format and untested, no UE5 file in the dataset), compressed bulk data, skeletal meshes, materials and textures (Texture2D mips), the export dependency table, `.uexp`/`.ubulk` companions
- [x] PlayStation 2 save (`.psu`): directory records, `icon.sys` title and icon names, icon models with texture and vertex colours (vertex count, extents and creation time checked against an independent parse). Open: animation shapes beyond the first, the run-length texture variant (written from the public icon description, no sample uses it), icons embedded in other save containers
- [~] Relief height map (`.rlf`): the banner is read (dimensions, units, height range, resolution). **Not decoded**: the height raster (about 1.25 bytes per pixel, bytes in the 0x00 to 0xFF range with long runs of `0x7F` in flat areas, so a delta or run-length coding); no public description exists and the four samples are the only evidence, which is not enough to pin a coding down without guessing

## Blender and Zstandard

- [x] Zstandard decoder (`Internal/ZstdDecoder`, written from RFC 8878: raw/RLE/compressed blocks, Huffman literals in one and four streams with direct and FSE weights, treeless reuse, FSE sequence tables (predefined, RLE, compressed, repeat), repeat offsets, several frames, skippable frames) checked byte for byte against the python `zstandard` package on 14 files (levels -5 to 19, with and without content size or checksum, small windows). Open: dictionaries, content checksum verification
- [x] Blender `.blend` (2.81 to 5.2): container (old and 5.x block headers, 32/64-bit pointers, both byte orders), SDNA, scoped pointer resolution (since 4.2 data block addresses repeat between IDs), scene/collection/object tree, meshes in the legacy and attribute layouts, materials with Principled BSDF values. Checked against Blender's own Python module on real files (not redistributable, read from `XRAY_3D_LOCAL_SAMPLES`; golden values committed) and a synthetic 5.2 scene. Open: modifiers, geometry nodes, shape keys, skinning and armature pose, constraints and animation, textures (image nodes, packed images), vertex colours, curves/text/metaballs, per-object material links, view-layer visibility other than exclude/hide, big-endian and 32-bit files (the code reads them, nothing tests them)
