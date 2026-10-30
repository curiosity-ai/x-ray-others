# Input format inventory of the reference viewer

Scope: every input format the reference viewer opens, what parses it, what metadata survives into its
scene model, detection logic, limits. Source: the beautified chunks in `the beautified app chunks`
(cited as `chunk:line`, full names in the index below) plus the raw wasm/vendor files in
`the extracted resources`. Everything is client-side: pages state "no server-side conversion queue"
(`pages__app:16953`); the only server calls are share/upload/analytics endpoints
(`39776:15292,16645,22375`). The one exception is writing binary DWG on export (`pages__app:16956`).

## 0. Chunk index (shorthand used below)

| Short | File | Role |
|---|---|---|
| `reg` | `12955.ca4106348516875e.js` L1918-3002 | Format registry (id, displayName, category, extensions, parseCost, sizeLimits). Duplicated in `77458.*`, `74518.*`, `79036.*`, `81892.*`, `pages__app` L21015-22165 |
| `disp` | `68243-ae5252348383cf8a.js` | Content sniffing (module 23501, L9946-14100) and parser dispatch (L16213-17395) |
| `app` | `pages__app-8f9a78f502565f4e.js` | Registry copy, size tiers (L22187-22226), converter target list |
| `prose` | `43953-84e663e0609d4a06.js` | Per-format marketing/FAQ text; the "compatibility" blocks state coverage and engines |
| `ui` | `39776.2e95610d778bf962.js` | Viewer UI: file intake, companion prompts (L16270-16370), export menu (L1590-1930) |
| `samples` | `13926-7586588fa97ce696.js` | Sample library (one group per format, shows which formats exist in practice) |
| `scene` | `53699-7cb1be57a1412a5c.js` L1-60 (`19174` module) | `emptyScene()` and the shared scene model |

Per-format parser chunk names are given inline (prefix before the first dot or dash is enough to `ls`).

## 1. Architecture facts that matter for a port

1. **One scene model for everything.** Every parser returns `emptyScene(format)` filled in (`scene:41-60`):
   `{format, units (default "mm"), bounds, root{id,name,visible,transform?,children[],meshIds[],layerId?},
   meshes[], materials[], textures[], layers[{id,name,visible,color?,count}], metadata{}}` plus optional
   `drawing` (2D entity model), `skins`, `animations`. Mesh = `{id, positions f32, normals?, uvs?,
   colors?, indices u32, topology triangles|lines|points, groups[{start,count,materialId}], materialId,
   bounds}`. Node transforms are 4x4 column-major (identity `[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]`).
   **There is no generic per-node property bag** (no `properties`/`userData` field survives): the
   "metadata" a format surfaces is limited to node names, hierarchy, layers, materials/colors, units,
   and a flat `scene.metadata` of counters/versions/warnings.
2. **Units** used: `"mm" | "cm" | "m" | "in" | "ft" | "unitless"`. Defaults per format are in the tables.
3. **Up axis:** scenes are normalised to Y-up. Z-up formats get a root transform
   `[1,0,0,0, 0,0,-1,0, 0,1,0,0, 0,0,0,1]` (rotate -90 deg about X) and `metadata.upAxis:"z"`
   (STL `67931:60-155`, OBJ `35144:849-880`, PLY `62195:150-210`, 3MF `73456:975`, 3DS `18743:30`).
4. **Post-processing for all formats** (`disp:16213-16385`, function `x`): if a scene has no layers and
   2..300 top-level named nodes with geometry, synthesises one layer per top-level part
   (`id:"part:N"`); re-centres far-from-origin coordinates (`metadata.recentered`); recomputes bounds.
5. **Engines in use** (verified in `extract/wasm`, `extract/vendor`):
   - three.js r183 loaders (vendor/three/examples/jsm/loaders): GLTF (+Draco, +Meshopt), PLY, 3MF,
     Collada, TDS, FBX, VRML (chevrotain), USD (USDA/USDC/USDComposer). Their output three.Object3D is
     converted into the scene model by a shared converter (`78076.*`, fn exported as `p`, 854 lines).
     **OBJ and STL are NOT three loaders**; they are custom streaming parsers.
   - Rust-to-wasm cores written by the viewer's authors: `dwg_core` (DWG: `parse_dwg`,
     `parse_dwg_full`, `decode_objects_*`), `sw_core` (SolidWorks: `SwDocument.extract_mesh`,
     `component_files_json`, `preview_bmp`, `read_stream`), `pln_core` (Archicad: `decode_plan_source`,
     `decode_source`, `extract_preview_source`, `embedded_library_parts`), `docx_core` (Word layout).
   - Third-party wasm: `web-ifc` (IFC), `rhino3dm` (3DM), `occt-import-js` (OpenCASCADE, used for IGES
     only: `10209:178 ReadIgesFile`; no `ReadStepFile` call exists anywhere), `manifold` (CSG for
     OpenSCAD, 3ds Max modifiers, C4D booleans, Archicad GDL), `laz-perf` (LAZ), `libarchive`
     (RAR/7z/TAR), `capstone` (PE disassembly), pdf.js (PDF), SheetJS (spreadsheets).
   - **Pure-JS custom parsers** for the rest, including a complete STEP B-rep tessellator (NURBS,
     trimmed surfaces, assemblies), DXF, JT, Parasolid XT, ACIS SAT/SAB, SketchUp (both generations),
     Revit, Inventor, Navisworks, Solid Edge, Fusion, 3DXML, PRC, U3D, FBX preprocessing, etc.
6. **Pipelines compose**: Parasolid `.x_t/.x_b` and Solid Edge PARASOLID streams are parsed to an XT
   document, converted to synthetic STEP text, then fed to the STEP tessellator (`86150:34-120`,
   `63740:1398`). `.ifcxml` is converted to SPF bytes then fed to web-ifc (`disp:16740-16758`). DXF
   `3DSOLID/REGION/BODY` and DWG embedded solids go through the ACIS reader.
7. Heavy parsers run in Web Workers (IFC `72968`, ACIS, STEP `17666` -> worker, IGES `10209`, Revit
   `66571`); `Worker` messaging uses `{type:"parse", id, data}` and returns geometry chunks.
8. Loading is chunk-lazy; failed chunk loads are retried 3x with backoff (`disp:17582`).

## 2. Detection logic

Entry points: `disp:16213 x()` (top level) -> `16396 S()` (detect + dispatch). `S` order:

1. `A()` (L17397): if depth < 3 and size <= 64 MB and the first 4 KB looks like **HTML**, scan the page
   for embedded base64 `data:` URIs and raw base64 runs whose decoded head matches
   `glTF`, `Kaydara FBX Binary`, `3D Geometry File Format`, `PXR-USDC`, `#usda`, `ply\n`,
   `ISO-10303-21;`, `solid `, `AC10` (`disp:10139-10176`, `th` L10578-10680); the largest embedded model
   is opened (a picker is offered when several).
2. Format = caller-forced `opts.format`, else `qZ()` = `t5()` (L13260). `unknown` throws
   `UnknownFormatError` with a hex head sample. Also error `ov` if registry entry is not `implemented`.
3. Size gate (section 3), then `switch(format)` (L16413-17394).

`t5()` (the sniffer, L13260-14100) works on the first **32 768 bytes** (`t3`, L13200) plus total size, in
this order:
- 0-byte file error; Apple Mail placeholder (`FA CA DE`) error; all-same-byte head (0x00/0xFF) ->
  rescued as SketchUp VFF when a trailing zip directory contains `model.dat` (`eX` L10267), else error.
- Hard rejections that explain themselves: DRM-scrambled headers (`SCDSA###`, `%TSD-Header-###%`,
  `\0MSMAMARPCRYPT`), U+FFFD "text-damaged" binaries (L13320-13400), `~$` / `.lck` lock files (Archicad
  lock `WW\x02..` detected), PTGui `.pts` projects, `.dmg`, HDR/NIfTI headers, NetCDF named `.nc`.
- Extension+magic combined checks for ambiguous containers (`.gldf/.l3d`, `.splat/.ksplat/.sog`,
  `.sldprt/.sldasm/.slddrw` CFB, `.idw`, `.gcd/.lcd/.qgd`, PowerShape, DIALux `.evo/.dlx/.stf`,
  RenderWare, TGA footers, TI Workbench, APLI `.laa`, Unreal, Unity).
- Ordered magic matcher list `tZ` (L11533-13040, ~100 entries). Key signatures:
  `glTF` (GLB), `ply\n|\r`, `MM` (3DS), `Kaydara FBX Binary \0`, `3D Geometry File Format ` (3DM),
  `LASF`, `ASTM-E57`, `AC10[0-3]\d` / `ACRODBVR` (DWG), `ISO-10303-21` (+ `IFC2X3|IFC4|IFC4X3` -> IFC else
  STEP, AP203/214/242 hint), ifcXML root, ACIS SAB header / SAT text header regex
  (`^\s*(\d{3,5})\s+\d+\s+\d+\s+\d+` + `body|lump|shell|asmheader`), IGES (columns 73 = `S`/`G`),
  STL binary (size == 84+50*n and header not starting `solid`) and ASCII (`solid` + `facet normal`),
  DXF (`0\nSECTION` after optional `999` comments; binary DXF `AutoCAD Binary DXF\r\n\x1a\0`),
  OBJ (>= N lines matching `^v|vn|vt` in first 4 KB), glTF JSON (`{` + `"asset"` + `"version":"[12]."`),
  3MF (`<model` + 3dmanufacturing namespace, or zip containing `3D/*.model`), COLLADA (`<COLLADA`),
  VRML (`#VRML V\d.\d`), X3D (`<X3D` or `#X3D V`), USD (`PXR-USDC` crate / `#usda`), DWF
  (`(DWF V`, `(W2D V`, or zip with `com.autodesk.dwf`), Blender (`BLENDER`, gzip/zstd wrapped),
  SketchUp (`SketchUp Model` UTF-16/ASCII within first 64 B, or zip with `model.dat`), JT
  (`Version \d+\.\d` + `\n\r\n` terminator at 76-78), CFB (`D0 CF 11 E0 A1 B1 1A E1`) probed for
  stream names (`ClassDirectory` -> 3ds Max; `BasicFileInfo` -> Revit; `PARASOLID` + `PSMcluster0` ->
  Solid Edge; `UFRxDoc`+`RSeStorage` -> Inventor; `ISolidWorksInformation|SwDocContentMgrInfo|CMgrHdr2|
  DLUpdateStamp|ResolvedFeatures` -> SolidWorks; `UG_PART`/`SPLMSSTR` -> NX JT), `NWD` header, Cinema 4D,
  U3D, W3D, PRC, ArtCAM `RELIEF FILE`, CMX, GLW, Affinity (`00 FF 4B 41`).
- **ZIP handling** (`te`, `tt`, `tn`, `tl` L10432-10560): reads the central directory from the last 66 560
  bytes (ZIP64 supported) or falls back to scanning local headers in the head, then classifies by entry
  names: `Home`/`Home.xml` -> Sweet Home 3D; 3DXML manifest; BCF (`bcf.version`, `markup.bcf`); FreeCAD
  (`Document.xml`); Creo View; 3MF; DIALux evo; GLDF/L3D; Fusion (`FusionAssetName[`, `Manifest.json`+`.f3d`);
  iWork (Pages/Keynote/Numbers); KMZ; ASCII grids; AccuMark; Unity project folder; `.docx/.xlsx/.pptx/
  .vsdx` by part names; otherwise `zip` (generic archive).
- Extension fallback table `tK` (L13168): extension -> format built from the registry (ids in `k3`
  are excluded because their extensions are ambiguous: planogram, remplanner, cnckad, cdr, prisma3d,
  citygml, cityjson, bazis, blitz3d, gmp, noteworthy, labsolutions, ps2mc, tiwb, laa, unity, asciigrid
  (`app:21018`)). If the extension names a format that *requires* a signature (table `eQ` L10349:
  skp, dwg, 3dm, las, e57, pcd, renderware, glb, solidedge, solidworks, inventor, jt, artcam, w3d,
  director, prc, cmx, glw, affinity) and the head does not match, the result is a "Not a X file"
  error (or `text` when the head is printable). Other extensions fall through with
  `confidence:"low", via:"extension"` (L14088-14096).
- Last resorts: printable-text sniff `tH` (L13136: no NUL, <2% control chars, UTF-8/Latin-1/UTF-16
  BOM) -> `text`; Gaussian-splat heuristic; else `unknown`.
- Detection result shape: `{format, confidence: high|medium|low, via: magic|sniff|extension|none, detail}`.
- Misc: partial-download names (`.crdownload .opdownload .partial .part .download`) are stripped and the
  inner name used (`disp:17957-17969`); `.nwf/.asm/.prt.N` multi-version names are handled in archive
  entry picking.

## 3. Size limits and limits in general

- Device tier (`app:22185-22190`): Android/iPhone/iPad/Mobile UA -> `low`; else `navigator.deviceMemory`
  <=2 -> low, <=4 -> medium, else (or undefined) high.
- Default limits by `parseCost` (`app:22192-22211`): string 40 / 75 / 100 MB; binary 150 / 300 / 500 MB
  (low/medium/high). Per-format `sizeLimits` in the registry override:
  STEP 50/100/200 MB; OBJ 150/512/1536; IFC 100/200/400; DWF 50/100/200; PTS 256/1024/2048; E57 and PCD
  512/2048/4096; Parasolid 25/50/100; Solid Edge 25/50/100; Inventor 100/250/600; JT 100/250/600;
  Navisworks 150/400/1024; eDrawings 50/100/200; Revit 50/100/200; Archicad 256/1024/1024; G-code
  256/1024/2048 (`reg:1940-2879`).
- Exceeding the tier raises `FileTooLargeError` marked *overridable* (`opts.ignoreSizeLimit`, user
  confirms); exceeding the `high` value is "over-ceiling" (also overridable but with stronger text)
  (`disp:17528-17535`, `app:22236+`). The check is skipped for: nested zip content, SketchUp/3ds Max
  from Blob (separate scene-data budget), VRML >= 32 MB (`OA`, streamed parser), 3DM >= 192 MB (`e`),
  oversized PSD (ranged read), RAGE `.rpf` (`disp:16413-16417`).
- Other hard limits: archive nesting depth 3; total expanded archive content 1 GB
  (`disp:17503-17515`); companion bundle 512 MB (`disp:9838`); archive entry picker lists <= 500
  (`F=500`, L8473); STEP "faceted stream" fast path >= 64 MB and triangle budget 1/3/6 M
  (`17666:44-47`); STL smoothing only <= 4 M triangles (`67931:u,h`); OBJ lean parse >= 256 MB
  (`35144:657`); IFC worker restarts when heap > 256 MB; ~2 GB wasm heap errors are reported as
  `ParserMemoryError` (`reg:3004`). Legacy-version errors use `LegacyFormatVersionError`.

## 4. Archives, multi-file projects, external references

- **Companions**: the UI (`ui:16270-16380`, `a7` L16334) lets the user drop several files; `Ez` picks the
  primary, all others become `companions: Map<lowercased basename, Uint8Array>` passed to the parser
  (`disp:9681-9850`). A parser that needs more files throws `cG` (MissingCompanion) carrying a
  `wants` descriptor `{names[], accept[], required[], matchNames, anyNameExts, purpose}`; the UI then
  shows "Still needed: ..." and re-runs the load when files arrive (`ui:16300-16325`). Lookup by
  basename with `Xe(companions, path)` (decodes percent-escapes, strips directories, matches name with or
  without extension). Helpers define accepted types: textures `.png .jpg .jpeg .gif .bmp .webp
  (+ .tga .dds .tif .tiff when named)`, OBJ `.mtl` + textures, level files `.b3d .x .3ds .md2`,
  Datasmith `.udsmesh .ies`, RenderWare `.txd` (`disp:9690-9780`).
- **ZIP** (`format zip`, `disp:17318-17370`): `extractModelEntry` (module 12766, L8204-9140) lists entries,
  drops junk (`__MACOSX/`, `._*`, `~$*`, `.DS_Store`, `Thumbs.db`, `desktop.ini`), and **picks the
  primary model by extension priority list `az`** (`disp:9160`): `.glw .umap .udatasmith .skp .3dm .step
  .stp .iges .igs .x_t .x_b .xmt_* .sat .sab .asat .asab .ifc .ifcxml .ifczip .gbxml .evo .dlx .stf .dwg
  .dxf .dwf .dwfx .glb .gltf .fbx .dae .3mf .usdz .usdc .usda .usd .obj .ply .stl .udsmesh .blend .max
  .c4d .rvt .rfa .pln .sldasm .sldprt .slddrw .par .psm .dft .f3d .f3z .cbf .sh3d .eprt .easm .edrw
  .hsf .3dxml .pvz .ol .sev .nwd .nwc .iam .ipt .idw .jt .fcstd .unitypackage .3ds .wrl .x3d .svg ...`,
  shallowest path first. All other entries become companions (so OBJ+MTL+textures work from a zip).
  Special assembly pickers: `.asm` (Solid Edge), `.nwf` (Navisworks), `.sldasm` (prefers top-level),
  Unity project (`unityProjectEntries`), Gerber set. If several candidate models exist the scene gets
  `metadata.archiveEntries {depth, entries, labels, index}` and the UI shows a picker; if the first
  entry fails the next <= 3 are tried (`I()` L17435) and the scene records `archiveFallback`.
- **RAR / 7z / TAR family** (`.rar .7z .tar .tgz .tbz2 .txz .gz .bz2 .xz .cpio .cpgz`) via libarchive
  wasm (`disp:17371`, module 50190). **Self-extracting `.exe`** (PE + embedded archive, capstone) and
  `.msg/.eml` attachments and `.pptx` embedded objects are opened recursively (depth <= 3).
- **Assemblies referencing external parts** (need companions / zip): SolidWorks `.sldasm`
  (`readInstanceTreeXml`, component file names listed in the error note, `48503:506-533`), Solid
  Edge `.asm`, Inventor `.iam` (own display cache preferred; part files only when cache missing),
  JT multi-file (partition `fileName` strings, `76676:1903,2075-2089`), Navisworks `.nwf`, Creo `.pvs`
  -> `.ol` files, FreeCAD `App::Link` to other `.FCStd`, Datasmith `_Assets/*.udsmesh`, Unreal `.umap`
  -> `.uasset`, glTF (`.bin` + images), OBJ (`.mtl` + images), VRML/X3D textures, 3DS/FBX/DAE textures,
  IGES/IFC none, `.ifczip` -> generic zip path. Textures that are missing are recorded as
  `metadata.missingTextures` / `missingMaterials` and a neutral placeholder is used.

## 5. Group A: simple / documented formats

### A1. Triangle mesh formats

| Format | Extensions | Parser | Metadata surfaced | Notes / evidence |
|---|---|---|---|---|
| STL | `.stl` | custom `67931` (253 lines): binary + ASCII state machine; ascii detected if size != 84+50n and head has `solid`/`facet normal` | root node `stl`; 1 mesh, default grey material; `metadata {ascii, triangleCount, shading:"crease"}`; flat-plate Z-up heuristic (`upAxis:"z", upAxisSource:"flat-plate"` when dz <= 0.25*min(dx,dy)); units default mm | welds by position then crease-shades (angle 40 deg) when <= 4 M tris (`67931:55-110`); no colour (no VisCAM/Materialise colour attr) |
| OBJ | `.obj` (+ `.mtl`, textures) | custom streaming `35144` (1348 lines), 16 MB chunks, "lean" mode >= 256 MB | **each `g/o/usemtl` block -> mesh + child node named after the group** (`root.name:"obj"`); lines (`l`) and points (`p`); vertex colours `v x y z r g b`; MTL (`Kd Ks Ke Ns d Tr Pr Pm map_Kd map_d`); `metadata{groupCount,triangleCount,lineCount,pointCount,missingMaterials}`; Z-up heuristic `objStandsOnZ0` | mtllib resolved by basename in companions (`35144:698-720`); TGA/PNG/JPG textures; units default mm |
| PLY | `.ply` (ascii, binary LE/BE) | three `PLYLoader` (`62195:99-210`) | single mesh or Points; `metadata{vertexCount,triangleCount,pointCloud,upAxis,upAxisSource}`; Z-up from header `comment` (module 23891 `G0`) else flat-base heuristic | 3D-Gaussian-splat PLYs are redirected to the splat parser (`isSplatPlyHead`); no node names |
| 3MF | `.3mf` | three `3MFLoader` + heavy preprocess `73456` (1017 lines) | `model unit` -> units (micron..meter, default millimeter, `73456:823-846`); object names; base materials, colour groups, textures; components/build items; **production extension flattened** (`p:path` refs); slicer plates (`Metadata/plate_N.gcode`) rejected with explanatory error unless geometry present; thumbnails `Metadata/thumbnail.png` only used for the notice; XML repair (`threemfSalvaged`) | rotated -90 deg X (Z-up); Bambu/Prusa/Orca/Cura extensions ignored (per `prose`) |
| GLB / glTF | `.glb`, `.gltf` (+`.bin`, images) | three `GLTFLoader` + Draco decoder (vendor/three/.../draco/gltf) + Meshopt (`52242:640-690`, `35078`) | node names, hierarchy, transforms, materials (PBR + KHR transmission/ior/clearcoat/emissive/volume), UV sets, vertex colours, **skins, morph targets, animations** (`78076:233-460`); `metadata{generator,version,missingTextures,animationCount}`; units m | glTF 1.0 binary header validated (`52242:757-790`) but loader is r183 (2.0); external files via companions URL modifier; truncated-GLB diagnostics (`E`, `D`) |
| COLLADA | `.dae` | three `ColladaLoader` + XML repair pass (`72408:100-267`) | nodes, materials, textures (companions), up-axis/unit via loader; error when file > 256 MB text | tolerant-XML rewrite when loader throws (unescaped `&`, unclosed tags) |
| 3D Studio | `.3ds` | three `TDSLoader` (`18743:14-58`) | node names, materials, texture refs (TGA decoded); `metadata.masterScale`; units m; rotated -90 X; materials forced double-sided | animation parsed but not exposed |
| FBX | `.fbx` | three `FBXLoader` + extensive repair (`15601` 1289 lines) | node hierarchy/names, materials, textures (embedded or companions), skins/animations; `metadata{fbxHeader, truncatedFbx, missingTextures}` | accepts binary >= 6.4 and ASCII >= 7.0 only; older -> `LegacyFormatVersionError` (`15601:466`); truncated-file reconstruction (`declared` size) ; re-binds skinned meshes whose bind pose is off (`R`, L1119) |
| USD | `.usdz .usd .usda .usdc` | three `USDLoader`: `USDAParser`, `USDCParser`, `USDComposer` (`97567`) | prim hierarchy -> nodes, UsdPreviewSurface, bundled textures, `metersPerUnit` -> units, primitives (sphere/cube/cylinder/cone/capsule/plane) | static; loose layers that reference other files list the missing ones |
| VRML / X3D | `.wrl .vrml .wrz .x3d .x3dv` | `32297` (942 lines) + three `VRMLLoader`; VRML 1.0 via custom `27065`; big files (>= 32 MB) via custom stream parser `36828` (1432 lines) | Transform hierarchy, DEF/USE, IFS/ILS, primitives, Material, textures (companions or http); `.wrz` gunzipped; X3D XML translated to VRML97 first (`32297:399`); units m; `missingTextures` | ROUTE/Script/sensors/PROTO not expanded; message names unresolved PROTOs; point/triangle budgets by device tier |
| OFF | `.off` | **not an input** in the registry; only an export target (`ui:1721`, `app` targets). OpenSCAD `import()` reads OFF | n/a | If X-Ray.3D wants OFF input it is not evidenced here |
| AMF | `.amf` | **export only** (`app` targets); no input parser | n/a | |
| U3D | `.u3d` and U3D streams inside 3D PDF | custom `43763` (1149 lines) | node tree (multi-parent), transforms, base mesh + progressive refinements, diffuse/emissive/opacity; Y-up unless PDF view says otherwise; units unitless | no textures/lights/animation (`prose:6522`) |
| PRC | `.prc` and PRC streams in 3D PDF | custom `60822` (4214 lines) | product structure (assemblies, part defs, names), triangle/fan/strip tessellation, wire tessellation, `TESS_3D_Compressed`, styles/colours; Z-up->Y-up; mm | exact B-rep not evaluated (`prose:6780`) |
| Shockwave 3D / Director | `.w3d .dcr .dir .dxr .cst .cct .cxt` | custom `18954`, `79078` | node tree, CLOD meshes, materials, JPEG textures; each top-level group a layer | Director 8.5.0-era files undecoded |
| Others (mesh-like game formats) | `.b3d` Blitz3D, `.glw`, `.dff/.txd` RenderWare, `.ydr/.ydd/.yft/.ytd` RAGE, `.uasset/.umap` Unreal <= 4.27, Unity text assets, `.unitypackage`, `.prisma`, `.ps2/.mcd/.psu` | custom chunks `70686,67479,52106,53958,47641,59731,58533,2111` | see `prose` blocks (6059, 4176, 6194) | low priority for X-Ray.3D |

### A2. Point clouds / splats / toolpaths

| Format | Extensions | Parser | Notes |
|---|---|---|---|
| LAS/LAZ | `.las .laz` | custom + `laz-perf` wasm (`50673`, `65383`, `30547`) | LASF header sniff: version, point count, LAZ flag; units m |
| E57 | `.e57` | custom `44504`,`9994`,`28240` | `ASTM-E57` header; 512 MB/2/4 GB tiers |
| PCD | `.pcd` | custom `44969` | ascii/binary/binary_compressed header |
| PTS / PTX / XYZ | `.pts .ptx .xyz` | custom `54783` (text sniffer `M.Y`) | `.pts` used by PTGui projects is rejected |
| Gaussian splats | `.splat .ksplat .spz .sog`, splat `.ply` | custom `86878` | separate renderer |
| ESRI ASCII grid | `.asc .grd` | custom `33404` | DEM -> mesh |
| G-code | `.gcode .gco .gc .nc .ngc .tap .cnc` | custom `41973` | polyline layers per feature/tool; maxSegments 1.5/4/8 M by tier (`disp:16600`) |
| OpenSCAD | `.scad` | custom interpreter `28488` + manifold + Liberation fonts | built-in modules only; BOSL/BOSL2/MCAD bundled |

## 6. Group B: CAD interchange

| Format | Ext | Parser | Hierarchy / names | Layers | Units / up | Colours / materials | Props / thumbs | Limits / caveats (evidence) |
|---|---|---|---|---|---|---|---|---|
| **STEP** | `.step .stp` | **custom JS** B-rep tessellator `98128` (4922 lines, entity cases L3322-3960: planes, cylinders, cones, spheres, tori, extrusions, revolutions, B-spline curves/surfaces (+rational, Bezier, quasi/uniform), trimmed/composite curves, faces/loops/edges, CLOSED/OPEN_SHELL, FACETED_BREP, MANIFOLD_SOLID_BREP, BREP_WITH_VOIDS, SBSM, **tessellated** AP242 entities, products, NAUO, mapped items) driven by `17666` (worker + sync `parseStepSync`); earcut triangulation (module at `98128:630`) | **Assembly tree** from `PRODUCT_DEFINITION` / `NEXT_ASSEMBLY_USAGE_OCCURRENCE` + `ITEM_DEFINED_TRANSFORMATION` / `REPRESENTATION_MAP` / `MAPPED_ITEM`; node name from `PRODUCT` name (id fallback; ignores `design|part|none|unknown`) (`17666:759-770`; `98128:3777-3845`, `4160-4330`) | `PRESENTATION_LAYER_ASSIGNMENT` is parsed as an inert container: **layers are not surfaced** (`98128:3931`) | `mm`; length unit read from `GLOBAL_UNIT_ASSIGNED_CONTEXT` (`SI_UNIT` prefix table, `CONVERSION_BASED_UNIT` + `LENGTH_MEASURE_WITH_UNIT`) and meshes scaled to mm (`98128:855-935`, `17666:1104`) | `STYLED_ITEM -> ... -> COLOUR_RGB` per solid and per face; materials `RGB r,g,b` or `STEP Default` (grey 0.78,0.78,0.82) | no PMI, no property sets, no GD&T; `metadata{meshCount,triangleCount,materialCount, facetStream, simplified, truncatedStep}` | 50/100/200 MB tier; fast path for tessellated/faceted-only files >= 64 MB with triangle budget 1/3/6 M and decimation (`17666:36-47`); truncated files (no `END-ISO-10303-21`) still render with `truncatedStep`; HTTP-error-page-in-file diagnosis |
| **IGES** | `.iges .igs` | `21739` (1348 lines) JS wireframe/level reader + **occt-import-js wasm** worker (`10209`) `ReadIgesFile` | flat; one node per mesh | **IGES levels -> layers** ("Level N", "Multiple levels") (`21739:718-760`) | mm; DE/global unit -> mm via `unitToMm` (`21739:203-250`) | RGB when present; default grey | no props | "up to a minute" on large NURBS files (`prose:629`); OCCT heap 2 GB error text |
| **ACIS SAT/SAB** | `.sat .sab .asat .asab` | custom `80416` + worker (`8329`,`5184`), SAT text (ACIS 1.0-2023) and SAB binary, ASM (ShapeManager) text/binary incl. 64-bit | each body = node `Body N` / body name; colours per body/face | none | **units from file header** (`80416:158-257`) | per-body colour; default `ACIS default` | none | filters the 10x10x10 unit-cube placeholder; spline surfaces tessellated; empty -> `EmptyModelError` |
| **Parasolid** | `.x_t .x_b .xmt_txt .xmt_bin` | custom XT parser `44408` (`parseXt`) -> synthetic STEP (`49407` `xtToStep`) -> STEP tessellator (`86150:34-120`); faceless wireframe via `65231` | one body = part; `metadata{brepBodyCount,brepFaceCount,parasolidEncoding,parasolidKernel,parasolidApplication}` | single "Curves" layer for wireframe | mm | colours from attributes if mapped to STEP | key/app name from header | **schema >= SCH_13006 (Parasolid v13+) only**; older -> `LegacyFormatVersionError` with Rhino-specific text; 25/50/100 MB |
| **JT** | `.jt` (+ NX `.prt` that embeds JT) | custom `76676` (2765 lines): segment/TOC walk, zlib + LZMA/XZ decompress, LSG nodes, tri-strip sets, quantised coords | **product structure with part names**, partition `fileName` references, string atoms (names), instance transforms (`76676:1903-2089`) | none | file units (default mm; NX `.prt` -> m) (`2014`, `2728`) | saved colours | no JT properties, no PMI, no B-rep/XT segments | JT 8.0-10.5; "Huffman packets not supported" (`:829`); multi-file assembly asks for part files (`cG`); 100/250/600 MB |
| **DXF** | `.dxf` ASCII + binary | custom `66863` (6528 lines) + `12120` | layers table; blocks, INSERT/ATTRIB; model + paper space | **layers with entity counts**; frozen/off layers filtered (`66863:5560-5590`) | `$INSUNITS` 1,2 -> in; 4,5,6 -> mm; else unitless (`66863:5534-5545`) | ACI colour + truecolor, lineweights, linetypes | `$ACADVER` in metadata | R12-R2018 (AC1009-AC1032); entities: LINE CIRCLE ARC LWPOLYLINE POLYLINE SPLINE ELLIPSE TEXT MTEXT DIMENSION LEADER HATCH SOLID TRACE 3DFACE MESH POLYFACE HELIX IMAGE + 3DSOLID/REGION/BODY via ACIS; proxy graphics of vertical products not interpreted |
| **DWG** | `.dwg` | Rust `dwg_core` wasm via `60563` (1054 lines) + `29940`; R1.x..R2.6 "headless" runs decoded in JS (`60563:81-91`, `473`) | model/paper space entities, block definitions, `blockHeaders` | **layers (name, frozen/off)** (`60563:608-622`) | `INSUNITS` map 1,22 -> in; 2,21,23,10 -> ft; 4 -> mm; 5,14 -> cm; 6 -> m (`60563:584-609`) | colours, lineweights | `metadata{version, entityCount, layerCount, blockCount, modelSpaceEntityCount, paperSpaceEntityCount, unsupportedEntities[{cls,count}], fontUsage (SHX/TTF names), hasSky}` | 1.2 M object cap for the in-browser decoder (`60563:A()`); embedded ACIS solids -> 3D; DWG written only through a server converter |
| **DWF/DWFx** | `.dwf .dwfx` | `97818`, `96494` (WHIP 2D, HOOPS HSF 3D, XPS pages) | multi-sheet picker; 3D assembly tree, per-part colour/transparency | layers (2D) | per sheet | per part | none | 50/100/200 MB |
| **IFC** | `.ifc .ifcxml .ifczip` | **web-ifc wasm** in worker `72968` driven by `93149` (356 lines); ifcXML -> SPF via `17180` (`readIfcXml`) | **flat element list**: node name = `"<Type> #<expressID>"` (e.g. `Wall #1234`), **not** `IfcRoot.Name`; no storey/space hierarchy; instanced geometry shared (`geomIndex`) (`93149:262-330`) | **layer per IFC type** (Walls, Slabs, Windows, Doors, Columns, Beams, Stairs, Railings, Furniture, Pipes, Ducts, Terrain...) via table `s` (`93149:20-50`) | `scaleToMeters` from `GetLinearScalingFactor`; scene units "m"; `COORDINATE_TO_ORIGIN`; elevation offset | per-instance colour RGBA -> `RGBA r,g,b,a` materials | **no property sets, no GlobalId, no classification** read; `metadata{schema, productCount, instanceCount, uniqueGeometryCount, scaleToMeters, elevationShift}` | pre-pass repairs nested `IfcBooleanResult`/`IfcBooleanClippingResult` chains (`72968:80-125`); 100/200/400 MB; OOM -> friendly error; IFC2X3, 4, 4X3 |
| **gbXML** | `.gbxml` / `.xml` | custom `62818` | surfaces coloured by `surfaceType`, openings cut | by type | `lengthUnit` of schema | per type | none | 0.37-7.x, UTF-8/16 |
| **CityGML / CityJSON** | `.gml`, `.json/.jsonl` | `89298`, `41239` | buildings -> nodes | LoD layers | m / ft (`2559:243-272`) | | | ambiguous extension, content-sniffed |
| **3DXML** | `.3dxml` | custom `86883` | product structure (parts, instances, positions) | none | mm | face + material colours | none | triangles only; CATIA binary CGR (old schemas) handled |
| **Datasmith** | `.udatasmith .udsmesh` | `55830` | actor hierarchy, transforms | scene layers + Lights/Cameras layers | UE cm Z-up -> m Y-up | material colours/textures | lights/cameras as markers | UE5 Oodle meshes become boxes |
| **BCF** | `.bcf .bcfzip` | `91836` | issue list | | | | viewpoint thumbnails | document-like (low priority) |

## 7. Group C: proprietary formats

| Format | Ext | Parser | What it gives | Caveats |
|---|---|---|---|---|
| **SolidWorks** | `.sldprt .sldasm .slddrw` | Rust `sw_core` wasm (`48503`, `70161`, assembly `72713`) | Parts/assemblies: **embedded display mesh** decoded face-by-face from SW triangle strips, per-face colour, per-part split (nodes `node-N`), units **m**, `metadata{solidWorksKind,faceCount,triangleCount}`; assembly assembled from the instance-tree XML stream + component files; drawings: preview image per sheet, sheet names, paper size in mm; thumbnails `PreviewPNG`/preview BMP for parts | Works for 2012-2026 (per `prose:1711`); both generations (CFB through 2014; record container after); no B-rep, no feature tree, no custom properties; suppressed-component handling; external components need companions |
| **Rhino 3DM** | `.3dm` | `rhino3dm` wasm (McNeel, open source) via `56536` (1609 lines) | **layers** (full path name, visible, colour, count), **materials** (diffuse, transparency, shine), object table (Brep/Extrusion via stored render mesh, SubD control net, curves as lines, meshes, point clouds), **block definitions/instance references -> instanced nodes**, hidden objects/layers skipped; `metadata{archiveVersion, applicationName, applicationDetails, warnings, triangleCount, objectsUnreadable}` | **units not read (stays "mm")**; Breps without render meshes appear empty; file tables walked by custom reader for diagnostics (`56536:56-125`); >= 192 MB bypasses size gate |
| **SketchUp** | `.skp .skb` | custom: MFC archive reader `46825` (< v21) and VFF reader `45140` (>= v21, zip with `model.dat` + `textures/`), scene `78713` (1885 lines) | **layers (tags)** by name with visibility, component definitions/instances, groups, face/edge/vertex counts, materials with textures (base colour, opacity, colorize, UV scale), hidden flags; `metadata{version, versionString, faceCount, edgeCount, vertexCount, materialCount, layerCount}`; units **m** | recovery modes for damaged headers (`skpRepairedHeader`, `skpRecoveredBlankHead`, `skpDamagedModelData`, `skpCutOffModelData`); texture budget by tier |
| **Revit** | `.rvt .rfa .rte .rft` | custom compound-file reader `71659` (7768 lines) in worker `66571`; shell `476` | geometry from stored solids/tessellation (`Partitions/N`, `Global/ElemTable`), categories (`-2001120` etc.), `PartAtom` category for families, `BasicFileInfo` for version; **element names/parameters not surfaced**; linked-model names listed in a notice | Revit 2010+; 50/100/200 MB; partial coverage per element kind (`prose:318`); thumbnail/preview shown when unreadable |
| **Inventor** | `.ipt .iam .idw` | custom `85938` (2223 lines) | display tessellation per face with colours, assemblies with occurrence transforms, units **cm**; Inventor 2013-2026 | no iProperties/model tree; `.idw` = saved sheet image; `.ipn` refused |
| **Archicad** | `.pln .pla` (+ lock file `.lck` detection) | Rust `pln_core` wasm (`19612`, `68534`, `92453`) | walls with openings, slabs, roofs, zones, beams, columns, morphs, meshes, terrain; GDL library parts via manifold | Archicad 25/27/29; element coverage varies; 256 MB/1 GB tiers |
| **Solid Edge** | `.par .psm .asm .dft .sev` | `63740` (1410 lines): Parasolid streams in CFB -> XT -> STEP path; SEV viewer docs `79513` | bodies, assembly via companions, drafts as metafile sheets | assembly needs the part files |
| **Navisworks** | `.nwd .nwc` (`.nwf` project) | custom `69313` (2064 lines) | placed parts with materials/transforms, line work; units ft for Revit exports | no selection tree/properties/viewpoints |
| **Fusion** | `.f3d .f3z` | custom `34241` (2730 lines) | display mesh (2026+) or exact bodies tessellated; occurrences; hidden layer; units cm | no appearances |
| **Creo View** | `.pvz .pvs .ol` | `72455` | product structure with part names, tessellation, colours, units m | |
| **eDrawings** | `.eprt .easm .edrw .hsf` | `43954` + `96494` (HOOPS HSF) | assemblies, per-body colours, edges layer, drawing sheets; units m / mm | |
| **FreeCAD** | `.FCStd .FCStd1 .FCBak` | `13031` (1000+ lines): zip of `Document.xml` + stored shapes; custom OpenCASCADE BREP-text reader/tessellator (`13031:54-781`, errors like "BREP curve type N"), Mesh workbench meshes; the `occt-import-js` require at L142 is a Node test path, not used in the browser | visible objects, App::Part containers/groups/links, per-object and per-face colours, labels as node names, mm | stored shape only (no recompute); shapes kept in OCC binary form -> notice with document thumbnail |
| **Blender** | `.blend .blend1 .blend2` (+ gzip/zstd) | custom DNA reader `52786` (4100+ lines) | mesh objects with transforms/parents, **collections -> layers**, material colours, packed textures, collection instances; Blender 1.6-5.x; units m | |
| **3ds Max** | `.max` | custom CFB reader `65724` + manifold modifiers | Editable Mesh/Poly, primitives, splines+Extrude/Lathe, hierarchy, instances, **layers**, object names, materials; units from file (`65724:2266`) | no V-Ray/Corona proxies |
| **Cinema 4D** | `.c4d .lib4d` | custom `51940` + manifold | polygon objects, generators (Extrude, Lathe, Sweep, Loft, Boolean, Cloner...), materials, hidden state; R12-2025; units cm | |
| **Sweet Home 3D** | `.sh3d` | `68929` | walls/rooms/furniture, **layers per level and kind**, models via the generic parser | |
| **PowerShape / Autodesk** | `.psmodel` | `6890` | **thumbnail only** | geometry enciphered |
| Others | Unreal/Unity/RAGE/Prisma3D/Bazis/Remplanner/planogram | see `prose` | | low priority |

## 8. Group D: non-3D

| Format | Ext | Parser |
|---|---|---|
| PDF (+3D PDF via U3D/PRC) | `.pdf` | pdf.js (`pdf-worker`), `18701`; 3D streams `91990` |
| Word | `.docx .docm .doc .dot .pages` | Rust `docx_core` wasm + legacy `.doc` converter (`78636`), iWork preview (`50284`) |
| Excel / ODS / Numbers / CSV | `.xlsx .xlsm .xlsb .xls .ods .numbers .csv .tsv` | SheetJS (`38856`, `49054`) |
| PowerPoint | `.pptx .pptm .ppsx .ppsm .potx .potm` (not `.ppt`) | `7852` |
| Visio | `.vsdx .vsdm .vssx ...`, legacy `.vsd` | `72226`, `47341` |
| Illustrator / EPS / PostScript | `.ai .ait .eps .epsf .epsi .ps` | `7214`, `98521` |
| SVG | `.svg` | `13584` |
| Vector/CAM misc | `.cdr .cdt .cdrx .cmx .studio3 .emf .wmf .emz .wmz embroidery(.dst .pes .pec .exp .jef .vp3 .emb) .gbr/.drl (Gerber) .gencad .cbf .cnckad .accumark .laa .tiwb` | one chunk each |
| GIS | `.shp .kml .kmz .gmp` | `41680`, `57017`, `10567` |
| Images | `.png .jpg .gif .bmp .webp .avif .heic .tif .exr .hdr .psd/.psb .tga .afdesign...` | `93322` (browser decode), `35828`, `64947`, `83813`, `85318`, `53997` |
| Photometry | `.ies .ldt .tm33 .gldf .l3d` | `70920`, `30470`, `53253`, `4843` |
| Fonts / text / email / PE | `.ttf .otf .woff .woff2 .ttc`, source/text list (`reg:2924`), `.msg .eml`, `.exe .dll ...` | `53347`, `79647`, `28305`, `6007` (capstone) |
| Other | `.noteworthy .nwctxt`, `.gcd` LabSolutions, `.skm` SketchUp material, `.layout` | `59746`, `15971`, `8501` |

## 9. Recognised but not opened (named error, suggests STEP/IFC/DWG export)

`disp:14440-14890`: Vectorworks `.vwx/.mcd`, Chief Architect `.plan`, MicroStation `.dgn`, CATIA V5
`.catpart/.cgr/.catproduct/.catdrawing`, CATIA V4 `.model`, Siemens NX native `.prt`, Creo native
`.prt/.asm`, Maya `.mb/.ma`, ZBrush `.ztl/.zpr`, Modo `.lxo`, LightWave `.lwo/.lws`, Houdini `.hip*`,
Twinmotion `.tm`, ReCap `.rcp/.rcs`, FARO `.fls/.fws`, RIEGL `.rxp`, ESRI `.mxd`, Unity built-game data,
Ruida laser. (`.nwf` Navisworks project files open only together with their linked `.nwd/.nwc`.) (Revit/Archicad/SolidWorks/Inventor/Solid Edge/Fusion
entries here are the "file opened but holds nothing drawable" messages for formats that otherwise parse.)

## 10. Export targets (output side; all client-side)

`app` converter targets: STL, OBJ, GLB, glTF (+bin), PLY, USDZ, 3MF, WRL, X3D, DAE, FBX, 3DS, AMF, OFF, XYZ,
PCD, LAS, STEP (AP214), SAT, IFC4, DXF (R12), DWG (server step), 3DM, PDF, DOCX, SVG, EPS, PNG, JPG, self-
contained HTML (`ui:1590-1930`). Three exporters shipped in vendor: GLTFExporter, OBJExporter, STLExporter.

## 11. Gaps that a port can fill (factual, for planning)

- IFC: names/GlobalId/property sets/spatial tree/classification are **not** extracted by the reference.
- STEP: layers (`PRESENTATION_LAYER_ASSIGNMENT`), PMI, properties, thumbnails are not extracted.
- SolidWorks/Inventor/Revit/Archicad: no custom properties, no feature trees; only display geometry.
- 3DM: units unread; DXF INSUNITS only maps in/mm; no `$EXTMIN` use.
- JT: no metadata properties, no PMI, no XT/B-rep segments.
- Generic: no per-node property bag exists in the scene model; any "properties" output is new work.
