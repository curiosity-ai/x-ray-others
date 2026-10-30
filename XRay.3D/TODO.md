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
- [ ] 2024+ saves: the `FaceTessellations/` store when `DisplayLists` is absent
- [ ] Compound-file envelope (SolidWorks 2014 and before; `__ZLB` zlib streams): a small CFB reader. Needs a fixture
- [ ] Drawings: extract `Images/Sheet_*` (and `Preview` DIB to PNG with the letterbox trim)
- [ ] Assemblies with external components: accept the part files as companions and merge them under the instance transforms
- [ ] A metadata-only path that does not inflate `DisplayLists` (range-read the entry table first)

## Next: more formats, in order of value / effort (see `docs/research/formats.md` for the viewer's approach)

- [x] STEP (B-rep, assembly, colours, layers, units; open: AP242 tessellated entities, mapped items): assembly tree (PRODUCT, NEXT_ASSEMBLY_USAGE_OCCURRENCE), names, units, colours, layers, properties; metadata first (S-M), then a B-rep tessellator (XL: NURBS, trimmed surfaces); start with faceted and AP242 tessellated geometry
- [x] DXF (ASCII): layers, units (`$INSUNITS` per the AutoCAD table; the reference viewer maps 4, 5 and 6 all to mm), blocks and arrays, lines/polylines/arcs/splines/hatch outlines, 3D faces, polyface and MESH; checked against ezdxf. Open: binary DXF, hatch fills, text as geometry, ACIS solids
- [x] IGES: metadata and curve geometry (110, 100, 106, 126, 124). Open: tessellating 128/144 trimmed surfaces and 186 B-reps, 112 splines, 102 composites
- [ ] DWG: wrap or port a pure-C# reader (the viewer's own TypeScript fallback decoder is a readable reference); layers, blocks, units
- [x] IFC (STEP physical file): spatial tree, names, GUIDs, property sets and quantities, units, styles; face sets, faceted B-reps, extrusions, mapped items. Open: openings and boolean operations (first operand drawn), swept disks, revolved and B-spline solids, IfcXML
- [x] FBX, COLLADA, 3DS, USDA, VRML/X3D, OFF, AMF (open: USDC/USDZ)
- [x] FreeCAD (`.FCStd`): document, object tree, properties, placements, colours, thumbnail, embedded STL/OBJ meshes. Open: tessellating `.brp` (OpenCASCADE text BREP), decoding `.bms` meshes, Link/App::Link placement
- [ ] OpenSCAD (needs a CSG kernel; metadata only first)
- [x] Sweet Home 3D (`Home.xml`, 5.3+): levels, walls, rooms, furniture with fitted OBJ models. Open: the Java-serialised `Home` of older files, curved walls, doors cut into walls, textures
- [ ] Rhino 3DM: layers, materials, block instances, stored render meshes; warn rather than mesh B-reps
- [ ] SketchUp, Revit, Inventor, JT, Parasolid, ACIS: metadata and thumbnails first, as the reference viewer shows geometry only
- [x] Point clouds: LAS, PCD, XYZ/PTS (open: LAZ, PLY splats)
- [x] Archives: ZIP packages (primary model by extension priority, companions, OBJ+MTL colours). Open: RAR/7z/tar, MTL textures, loading every model of a package as one scene
- [x] G-code toolpath preview (lines by extrusion/travel or cut/rapid). Open: per-layer nodes, feature-type colours, tool numbers, canned cycles
- [ ] Archicad (metadata and thumbnail only; the reference reader is itself partial)

## Next: conversion quality

- [x] Textures and UVs: OBJ+MTL `map_Kd` (companions, ZIP packages), glTF and 3MF textures, `TEXCOORD_0` and images/samplers in the GLB, `KHR_texture_transform`. Open: other maps (normal, emissive, metallic-roughness), UV sets above 0, UVs for FBX/COLLADA/X3D/USD/3DS/PLY readers
- [x] Meshopt decode for glTF input; meshopt compression and `KHR_mesh_quantization` for output (ported codecs, checked against an independent decoder)
- [x] Draco decode for glTF input (`KHR_draco_mesh_compression`): sequential and EdgeBreaker (standard and valence) connectivity, quantised, normal and generic attributes, all prediction schemes; checked against an independent decoder on 40 fixtures. Open: kd-tree point clouds, bitstreams older than 2.2
- [ ] Smooth/flat normal policy for welded STL (currently left to the viewer)
- [ ] Unit/up-axis hints from the caller for formats that do not say (STL, OBJ)
- [ ] Far-from-origin recentring (the viewer does it; record the offset in extras)
- [ ] Pass-through for GLB input in `ConvertToGlb` (currently re-written)
- [ ] Layers on nodes for the formats that have them; synthesise "part" layers like the viewer does
- [ ] Streaming readers for very large OBJ/STL/PLY (inputs are currently held in memory)

## Next: Tesserae component

- [ ] `XRay.ThreeD.Tesserae` (separate package, transpiled): load a GLB, orbit/pan/zoom, assembly tree with visibility, per-node properties from `extras`, explode, section plane, measure, wireframe/edges, lighting presets, layer list
- [ ] The viewer's data-model gaps to improve on: it has no assembly tree panel and no property panel, drops node properties, and does not convert units on export

## 2D output

- [x] `Model3D.Is2D`, text and filled-area primitives, DXF TEXT/MTEXT/ATTRIB/SOLID/solid HATCH, `SvgWriter` and `ModelLoader.ConvertToSvg` (layers, strokes, text, fills, `<use>` for shared meshes, metadata), checked against ezdxf for text and fills
- [ ] DXF blocks as shared meshes (they are expanded per insert, so DXF output has no `<use>` yet), line weights, line types, hatch patterns and gradients, text styles and fonts, dimension geometry as text, paper-space layouts
- [ ] SolidWorks drawing sheets as vector content (flagged `Is2D`, no geometry extracted yet)

## Housekeeping

- [ ] Family registration in curiosity-ai/x-ray (`Members.props`, docs section; see its `add-family-member` skill)
- [ ] Verify the licences of anything that gets ported from a third party (the research notes flag laz-perf as unverified)
- [ ] Benchmarks (large assemblies), fuzz target in the style of `XRay.Psd/fuzz`
