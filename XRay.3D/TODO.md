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

- [ ] STEP: assembly tree (PRODUCT, NEXT_ASSEMBLY_USAGE_OCCURRENCE), names, units, colours, layers, properties; metadata first (S-M), then a B-rep tessellator (XL: NURBS, trimmed surfaces); start with faceted and AP242 tessellated geometry
- [ ] DXF (text): layers, units (`$INSUNITS`, note the reference viewer maps 4, 5 and 6 all to mm), blocks, 3D faces / polylines / meshes
- [ ] DWG: wrap or port a pure-C# reader (the viewer's own TypeScript fallback decoder is a readable reference); layers, blocks, units
- [ ] IFC (STEP physical file): names, GUIDs, property sets, spatial tree (the reference viewer drops these); geometry needs an extrusion/CSG kernel (XL)
- [ ] FBX, COLLADA, 3DS, USD(A/Z), VRML/X3D, OFF, AMF
- [ ] FreeCAD (`.FCStd`: zip with `Document.xml` and BREP/mesh parts), OpenSCAD (needs a CSG kernel; metadata only first), Sweet Home 3D (zip of Java-serialised home, models as OBJ)
- [ ] Rhino 3DM: layers, materials, block instances, stored render meshes; warn rather than mesh B-reps
- [ ] SketchUp, Revit, Inventor, JT, Parasolid, ACIS: metadata and thumbnails first, as the reference viewer shows geometry only
- [ ] Point clouds: LAS (trivial), LAZ (LASzip spec), PLY with splat properties, PCD, XYZ/PTS
- [ ] Archives: look inside ZIP/RAR/7z/tar for the primary model (extension priority list) and companions (OBJ+MTL+textures)
- [ ] G-code toolpath preview; Archicad (metadata and thumbnail only; the reference reader is itself partial)

## Next: conversion quality

- [ ] Textures and UVs (OBJ+MTL with companions, glTF, 3MF textures); `KHR_texture_transform`
- [x] Meshopt decode for glTF input; meshopt compression and `KHR_mesh_quantization` for output (ported codecs, checked against an independent decoder)
- [ ] Draco decode for glTF input
- [ ] Smooth/flat normal policy for welded STL (currently left to the viewer)
- [ ] Unit/up-axis hints from the caller for formats that do not say (STL, OBJ)
- [ ] Far-from-origin recentring (the viewer does it; record the offset in extras)
- [ ] Pass-through for GLB input in `ConvertToGlb` (currently re-written)
- [ ] Layers on nodes for the formats that have them; synthesise "part" layers like the viewer does
- [ ] Streaming readers for very large OBJ/STL/PLY (inputs are currently held in memory)

## Next: Tesserae component

- [ ] `XRay.ThreeD.Tesserae` (separate package, transpiled): load a GLB, orbit/pan/zoom, assembly tree with visibility, per-node properties from `extras`, explode, section plane, measure, wireframe/edges, lighting presets, layer list
- [ ] The viewer's data-model gaps to improve on: it has no assembly tree panel and no property panel, drops node properties, and does not convert units on export

## Housekeeping

- [ ] Family registration in curiosity-ai/x-ray (`Members.props`, docs section; see its `add-family-member` skill)
- [ ] Verify the licences of anything that gets ported from a third party (the research notes flag laz-perf as unverified)
- [ ] Benchmarks (large assemblies), fuzz target in the style of `XRay.Psd/fuzz`
