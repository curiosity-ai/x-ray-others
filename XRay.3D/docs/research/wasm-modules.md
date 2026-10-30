# WebAssembly modules: what they are, how the app uses them, and what a C# port costs

Scope: every module under `extract/wasm/` except `sw_core` (SolidWorks, handled separately).
App code cited below lives in `pretty_all/` (webpack chunks, beautified; line numbers refer to those files).
Paths: `E` = `.../the extracted resourceswasm`, `P` = `.../scratchpad/pretty_all`.
URLs in the app are `<site>/wasm/<file>`; the site name is deliberately not written here.

Effort scale: S = days, M = 1-3 weeks, L = 1-3 months, XL = 3+ months / open-ended.

## 0. Overview table

| Module | Kind | What it is | Licence (best evidence) | App consumer | C# effort | Worth it |
|---|---|---|---|---|---|---|
| dwg_core | Rust + wasm-bindgen, **proprietary in-house** | Full DWG (R9..R2018+) decoder to JSON | none shipped; deps MIT/Apache | `12955` (worker), `60563` (dispatcher); TS twin in `5605` | L (or S via ACadSharp) | Yes, via existing lib |
| pln_core | Rust, **proprietary in-house** | Graphisoft Archicad .pln (17+) decoder to meshes + floor plan | none shipped | `19612`, `68534` (worker), `92453` | XL | Probably not (partial even in the original) |
| docx_core | Rust, **proprietary in-house** | Word .docx parser AND page layout engine (line breaking, tables, pagination) emitting positioned-glyph-run JSON | none shipped | `33361` (+ renderers `30360`, `7645`, `84178`, `92895`) | XL | Only if no Office renderer exists (x-ray-office likely covers) |
| libarchive | Emscripten C | libarchive 3.7.7 (+ xz 5.6.4, zlib 1.3.1, bzip2) | BSD-2-Clause | `68243` (module `50190`) | S (SharpCompress) | Yes, trivial |
| laz-perf | Emscripten C++ (embind) | hobu/laz-perf LAZ decompressor | upstream: Apache-2.0 (not stated in the shipped file; verify) | `30547` (worker) | M | Yes, point clouds are common |
| manifold | Emscripten C++ (embind) | elalish/manifold mesh booleans + Clipper2 CrossSection | Apache-2.0 (Clipper2: Boost) | `40109`, `51940`, `65724`, `91081`, `99720` | L (basic) / XL (robust) | Needed by 5 formats; do it late |
| capstone | Emscripten C | Capstone 5.x disassembler, wrapped as `MCapstone` | BSD-3-Clause | `6007` (PE viewer) | S (Iced, x86 only) | Low value |
| occt-import-js | Emscripten C++ (embind) | kovacsv/occt-import-js around OpenCASCADE | LGPL-2.1 (+ OCCT exception) | `10209`/`63731` (worker), `13031` (FreeCAD) | L-XL | Only for IGES + OCCT .brep; STEP is already in TS (`17666`) |
| web-ifc | Emscripten C++ (embind) | ThatOpen engine_web-ifc | MPL-2.0 | `72968` (worker) | XL (full) / L (common subset) | High user value, high cost |
| rhino3dm | Emscripten C++ (embind) | McNeel rhino3dm (openNURBS) | MIT (openNURBS BSD-style) | `56536` (import), `61216` (export/resave) | L (read) / M (write meshes) | Yes for read of stored meshes |

Legal flag for the three Rust modules: they are the viewer authors' own closed code (no licence text in the glue, a Windows developer's
home-directory path in the panic strings, crate names like `geometry_walls.rs`). Treat their wasm and their TS twins as behavioural references
for output shape only. For a clean C# library, prefer open format knowledge (ACadSharp / LibreDWG / ODA spec; Open XML spec; openNURBS source).

---

## 1. dwg_core (Rust, wasm-bindgen)

**Files**: `E/dwg_core.js` (glue), `E/dwg_core_bg.wasm` (1.0 MB). No-modules wasm-bindgen target (`var wasm_bindgen = (function(exports){...})`), loaded with `importScripts` + `wasm_bindgen({module_or_path})` in a worker.

**Identity**: closed in-house crate. Source module names visible in strings: `acis.rs aec.rs bitreader.rs boundary.rs data.rs entities.rs entities_ext.rs
handles.rs hatch.rs header_vars.rs index.rs jsmap.rs jspi.rs layout.rs lib.rs object_decoder.rs parser.rs pre_r13.rs proxy_graphics.rs r2004.rs
r2004_decompress.rs r2007.rs resolve_ext.rs sab.rs ser.rs tables.rs`, plus charset tables (`gb18030.rs iso_2022_jp.rs utf_8.rs` from `encoding_rs 0.8.42`).
Deps: `serde_json 1.0.154`, `serde-wasm-bindgen 0.6.5`, `console_error_panic_hook`, `dlmalloc 0.2.14`, `hashbrown 0.17.1`, `itoa`. Built with rustc `8bab26f4f68e...`.
It is a port of a TypeScript parser that still ships in the app as a fallback (see "TS twin").

### Exported API with every doc comment (verbatim from the glue)

```
bench_build_only(n:number):number
  "Build entities and marshal nothing. Isolates construction cost."
bench_marshal_bytes(n):Uint8Array
  "Build entities and return them as a JSON byte buffer (option 3 stand-in)."
bench_marshal_js(n):any
  "Build entities and marshal them into JS objects (option 1 transport)."
decode_objects_bytes(data:Uint8Array, start:number, max_objects:number, version:string):Uint8Array
  "Decode and return the objects as one JSON byte buffer."
decode_objects_count(data,start,max_objects,version):number
  "Decode only, marshalling nothing. Isolates decode time."
decode_objects_js(data,start,max_objects,version):any
  "Decode and marshal straight into JS objects."
heap_min_grow_pages():number
  "The floor on heap growth, so a caller can check `heap_pages` against the
   policy without hardcoding it."
heap_pages():number
  "Current size of linear memory, in 64 KB pages.
   Exported so the growth policy above is observable from the outside: after
   any allocation at all, a module with this policy installed sits at
   `MIN_GROW_PAGES` or more, and one without it sits at whatever it happened
   to need. That difference is what `dwg-wasm-heap-growth.test.ts` asserts on,
   which beats timing a parse (real files vary by two orders of magnitude for
   honest reasons). Also worth reporting alongside an out-of-memory failure."
init():void
  "Install a panic hook that routes Rust panics to console.error. Debug aid
   during the port; a panic still traps and surfaces to JS as a RuntimeError,
   which the adapter catches and maps to CorruptFileError."
parse_dwg(data:Uint8Array):Uint8Array
  "Parse a complete DWG file, returning the shipping payload as a JSON byte
   buffer for the JS side to decode.
   The buffer transport is deliberate: benchmarking the two routes over 80k
   synthetic entities put serde-wasm-bindgen at 621.6 ms against 257.3 ms for
   JSON bytes, so building JS objects field-by-field across the boundary
   costs more than serialising and letting the engine's native JSON parser
   do the work.
   Errors surface as a JS exception carrying the parser's message, which the
   adapter already maps onto its calm-notice error types."
parse_dwg_full(data):Uint8Array
  "The complete document including `objects`, for parity checking against the
   TypeScript parser. Not the shipping path."
parse_dwg_stats(data):Uint8Array
  "Parse and return only the summary counters, for callers that want to probe
   a file without paying for the document marshalling."
set_hatch_arc_steps(n:number):void      (no doc; segments per arc when HATCH loops are flattened; app default 8)
version():string
  "Build identifier, so the JS side can assert it loaded the module it expected."
```

Doc-comment findings worth keeping as format/engineering facts:
- JSON-bytes transport is ~2.4x faster than building JS objects (257 ms vs 622 ms for 80k entities); in C# this is moot (return typed objects).
- Real DWG files vary by two orders of magnitude in parse time; heap growth needs a floor (`MIN_GROW_PAGES`) to avoid OOM on big files.
- A wasm trap (`WebAssembly.RuntimeError`) is treated as "this file is corrupt" and permanently disables the wasm path in that worker, then falls back to the TS parser (`12955` lines 975-985, `v=!0`).

### Data returned (what `parse_dwg` JSON looks like)

Top-level (read in `12955` lines 990-1010, and the field-name table in the wasm data segment):
```
{ version: "AC10xx",
  layers: [[handle, {name, frozen, off, locked, plotFlag, color...}], ...]      // serialised as entries of a Map
  ltypes: [[handle, {name, description, patternLength, numDashes, dashes[]}]]
  styles: [[handle, {name, fixedHeight, lastHeight, fontName, bigFontName, widthFactor, obliqueAngle...}]]
  blockHeaders: [[handle, {name, basePt, anonymous, hasAttdefs, isXref, ownedObjectCount, firstEntityHandle...}]]
  blockControl: {...}, blockEntities: [[blockHandle, Entity[]]],
  modelSpaceEntities: Entity[], paperSpaceEntities: Entity[],
  modelSpaceDuplicates / paperSpaceDuplicates: [{entity,...}]   // duplicate-handle entities kept aside
  extMin, extMax: {x,y,z}, tileMode, insUnits, hasSky,
  headerVars: { ~400 named system variables: INSUNITS, CLAYER, DIM* ..., CMATERIAL, LATITUDE/LONGITUDE, ...},
  stats: { totalObjects, parsedObjects, failedObjects, entityCounts{}, unsupportedEntities{className:count}, version, classes[] },
  acisBlobs ... }
```
Entity = flat object with `entityType`(number), `entityTypeName`, common fields (`handle, layerHandle, ltypeHandle, color, truecolor, ltypeScale,
lineweight, invisible, entmode, ownerHandle, xdicHandle, proxyGraphics, materialFlags, shadowFlags`) + type fields. Types seen in the TS twin (`5605`):
LINE, CIRCLE, ARC, ELLIPSE, POINT, SOLID, TRACE, 3DFACE, TEXT, MTEXT, ATTRIB, ATTDEF, INSERT, MINSERT, BLOCK, ENDBLK, SEQEND, LWPOLYLINE,
POLYLINE_2D/3D/MESH/PFACE (+ VERTEX_2D, VERTEX_PFACE_FACE), SPLINE, HATCH, DIMENSION(*), LEADER, MULTILEADER, MLINE, TOLERANCE, SHAPE, TABLE, VIEWPORT,
OLE2FRAME, IMAGE, WIPEOUT, PROXY_GRAPHICS, plus 3DSOLID/BODY/REGION with ACIS data.

Binary side channel: ACIS (SAT text / SAB binary) payloads for 3DSOLID/BODY/REGION. `parse_dwg` may return either plain JSON bytes or a container:
`"DWGB"` (bytes 44 57 47 42) + `u32le jsonLength` + JSON + binary blob; entities carry `sabRef:[offset,len]` into the blob which the adapter turns into
`sabData` (`12955` lines 1005-1020). Older path used base64 `dataB64`/`sabData` strings (lines 927-948).

### How the app consumes it
- `12955` lines 910-1010: worker loads the wasm, sets `set_hatch_arc_steps(arcSteps)` (default `dwg.arcSteps = 8`, `5605` ~line 9320), calls `parse_dwg`, rebuilds the `Map`s.
- `12955` lines 1100-1760 (`R()`): adapter: sniffs `AC...`/`MC0` version, rejects <R9 with a message, tries headless pre-R13 record runs, tries wasm, falls back to TS `new O().parse(bytes)` (O = module `93219`).
  Then: repairs header extents (`_`, `W`), "lowers" extended entities to primitives (module `328`), maps OCS (extrusion) onto WCS for ARC/CIRCLE/INSERT/LWPOLYLINE/SOLID/HATCH/TEXT,
  applies block base points (`99213`), places dimension blocks, builds scene: layers, units from INSUNITS (1,22 in; 2,21,23,10 ft; 4 mm; 5,14 cm; 6 m), drawing entity list, and decides 2D vs 3D
  (3D if 3DSOLID meshes via module `36437`, 3D linework `22891`, or 3D faces `38493` dominate).
- `60563` (main thread dispatcher): `MAX_BROWSER_OBJECTS = 1.2e6` (`g = 12e5`, line 92), compact-vertex weight `0.25`, worker-died message. Error from wasm of the form
  "too large for the in-browser decoder (N objects[, M vertices])" is parsed (`12955` lines ~1130) to refuse >1.2M weighted objects without a TS retry.

### TS twin (very valuable porting source, readable)
Chunk `5605.baf5adeb7df1ef4d.js` (9331 lines) holds the same decoder in JS:
`28705` (bit reader, line 2017), `78655` (common entity header, 2438), `93219` (`class ed` DWG parser, lines 2515-7866: R2000 `parseR2000`, R2004+ `parseR2004Plus`, R2007 string reader, system-variable header per version, class map, object map, per-entity decoders),
`57278` (pre-R13 / headerless record runs, 7866-8933), `46283` (settings), `9229` (default settings: `skp`, `dwg:{ocsEpsilon:1/64, arcSteps:8}`), `99213/38493/22891/328/95234/36437` (post-processing: block base points, 3D faces, linework scan, extended-entity lowering, ACIS, ...).
It rejects `AC1.x..AC1003` (pre-R9) and accepts `/^AC10[0-3][0-9]$/` (AC1012..AC103x, i.e. R13..R2018+ and the new AC1032) plus MC0/AC1.x-era 'pre-R13' record runs.

### C# effort: L from scratch; S-M by adopting ACadSharp (MIT, pure C#, reads R13-R2018+ and DXF) and adding our own post-processing.
Hard parts: R2004 section decompression (LZ77 variant), R2007 Reed-Solomon + LZ, bit-coded object stream (bitreader), object-map handle resolution, HATCH boundary flattening, ACIS SAT/SAB to mesh (module `36437`, separate big job, XL), OCS->WCS mapping, extents sanity repair.
**Recommendation**: do it, but wrap ACadSharp rather than porting; port the adapter heuristics (extents repair, OCS mapping, 2D/3D decision, units table) from `12955`/`5605`.

---

## 2. pln_core (Rust, wasm-bindgen) - Graphisoft Archicad .pln

**Files**: `E/pln_core.js` (ES module glue, async-only API), `E/pln_core_bg.wasm` (1.57 MB).
**Identity**: closed in-house crate. Source modules from strings: `associations classic codecs core earcut entry error geometry_annotations geometry_axes geometry_drawing
geometry_home_floors geometry_mesh_base geometry_mesh_cache geometry_meshes geometry_morphs geometry_objects geometry_openings geometry_polygons geometry_roofs geometry_sections
geometry_slabs geometry_stories geometry_util geometry_wall_parametric geometry_wall_vrd geometry_walls geometry_zones legacy math model module9 output_buffer preview raw reader scene ser triangulate`.
Deps: `miniz_oxide 0.8.9` (inflate), `serde_json 1.0.151`, `indexmap 2.14.2`, `hashbrown`, `once_cell`, `js-sys 0.3.105` (async via JSPI/queue futures).

### Exported API with doc comments (verbatim)
```
decode_plan_source(size:number, read:Function, progress:Function, tick:Function): Promise<any>
  "The floor plan sheet: `{scene}` holding the plan drawing (drafting,
   text, dimensions and element footprints per story) and no meshes, or
   `{scene: null}` when nothing in the project draws in plan. Two walks:
   the model walk for the footprints and stories, then the plan walk."
decode_source(size, read, progress, record:Function|null, tick): Promise<any>
  "Rust owns every format decision. Awaiting the JS read promise suspends
   the Rust future, retaining only the bounded decoder state between reads."
embedded_library_parts(size, read, tick, names:Array<string>): Promise<Array<any>>
  "The embedded library parts (.gsm containers) whose file names are
   given, read from the project's file store: `[{name, bytes}]`. Lets
   the viewer draw a part's own geometry in place of its box."
extract_preview_source(size, read, tick): Promise<Uint8Array|undefined>   (embedded preview image; no doc)
```
Calling convention: random-access reader (`read(start,end)` returns `Promise<Uint8Array>`; used as `e.read(n,t)` in `19612` line 270), never loads the whole file; `progress(ratio)`; `tick()` = yield to event loop (`setTimeout 0`).

### Format findings visible in strings (no doc comments exist beyond the above; these are the decoder's own diagnostics/limitations text)
Containers: "legacy Archicad container" (magic `PLANFLDT`), "classic Archicad container", "Archicad 9 module container" (`module9.rs`), and the modern "FDB" physical layout
(root directory, nested group directories, active vs. initial directory chains, `outer group` / `outer chunk` framing, `class prefix`, `inner chunk`, alignment padding, GUID-keyed groups; all bounded by explicit budgets: "FDB allocation count budget", "Total expanded heap budget", ...).
Version table from the app (`19612` lines 40-48): header word `WW` (LE) or `MM` (BE) at offset 0; value 64=>Archicad 6, 71=>7, 128=>9, 170=>10, 260=>11-13, 350=>16. Anything older than 17 is "legacy layout, not read".
Element kinds: `wall (parametric, vrd), slab, roof (poly-roof, pitch, frame), beam/column (rectangle-section, profiled-section, segments, junctions), morph (mesh, nonplanar faces excluded), mesh, zone, opening (door/window as panels), library-part-reference/object-box, terrain, home-floor/story tables, section/axes/annotations/drafting (lines, arcs, polylines, splines, hatch, text, dimensions).`
Honest self-reported limitations (the scene's `limitations`):
"Door and window library parts excluded"; "Doors and windows shown as panels in their openings; their library part geometry is not drawn"; "Partial building shell";
"Wall openings and profile cuts unresolved"; "Rectangular and profiled beam and column solids; offset, sloped, curved and rounded ones remain axis lines";
"Single-level rectangular hip roofs; other roof geometry remains unresolved"; "Unsupported terrain layouts and stair surfaces unresolved"; "Materials and visibility settings unresolved";
"Walls without a saved outline drawn from their reference line, thickness and heights; joins, profiles and roof trims not applied";
"Library parts (furniture, fixtures and other objects) drawn as boxes from their placement and size parameters: Archicad does not save their 3D in the project file";
"Plan drafting only: lines, arcs, polylines and fill outlines placed on stories, in one stroke; pens, line types, fill patterns, text, dimensions and library parts are not drawn".
Key insight: Archicad does NOT store library-part 3D in the .pln; parts are GDL scripts in embedded `.gsm` containers, which the app interprets in TS (chunk `91081`, 2767 lines: a GDL interpreter with `cutpoly/cutplane/cutshape/sweepgroup/subgroup/while/...`, booleans via manifold).

### Data returned
`decode_source` => `{ model, geometry, scene }`:
- `scene`: the app's shared scene object (`meshes[{positions:number[]->Float32Array, indices->Uint32Array, normals?}]`, `materials`, `layers`, `drawing{entities}`, `metadata`); axis: summary says `sourceUpAxis Z`, scene up axis Y.
- `geometry.summary`: `{source.elements{counts per kind, "library-file"}, twoDOnly, limitations[], omissionDetails[{reason, face?, ...}], decodedCounts, storedCounts, renderedCounts, axisCount, triangleCount, checkedPolygonCaps, ...}`
- `model`: `{summary, libraryParts[{kind:"library-part", file, placement,...}], ...}`.
`decode_plan_source` => `{scene}` (2D floor plan sheet, sheet index 1; app sheets: `[{title:"Model", is3d:true},{title:"Floor plan"}]`).

### How the app consumes it
- `19612` lines 1-323: `parsePln*`, sheet logic (`PLN_SHEETS`), `.mod` and `.gsm` rejection / GSM route (`l.C`), 2D-only fallback message (`WHY_2D_ONLY`: "holds only 2D drawing elements..., export to DWG"), worker dispatch (`Worker(68534)`), and loader `d()` at 281-300 (`<site>/wasm/pln_core.js?v=12`, node path for tests).
- `68534` (worker, same module + glue), `92453` (small helper). Library parts: `a.J` (`91081` line 2626) draws `library-part`s through the GDL interpreter using `embedded_library_parts`.
- Typed arrays are re-wrapped after the call (`19612` ~line 316: `new Float32Array(e.positions)` etc.).

### C# effort: XL, and the decoded result is intentionally partial (shell, boxes for objects, no materials, many unresolved cases). Value: low-moderate (Archicad is niche for a viewer; most users export IFC/DWG).
**Recommendation**: skip unless a customer demands it; at minimum support `extract_preview_source` equivalent (embedded preview thumbnail) cheaply. If attempted, expect reverse-engineering of FDB/group framing with no spec; this module represents months of the original authors' work.

---

## 3. docx_core (Rust, wasm-bindgen) - Word layout engine

**Files**: `E/docx_core.js` (class-based glue, `DocxEngine`), `E/docx_core_bg.wasm` (863 KB).
**Identity**: closed in-house crate. Modules: `parser parser_para parser_pr element attributes state emit escape pi stream read map dict` (OOXML), `line_breaker paragraph_layout table_layout page_layout page_layout_fixup page_layout_place metrics gvar`
(layout; `gvar` = variable-font deltas), `cp437 crc32 magic_finder searcher twoway slice_reader` (own zip handling plus `zip 2.4.2`), `quick-xml 0.37.5`, `ttf-parser 0.25.1` (font metrics only; no HarfBuzz shaping), `miniz_oxide`, `memchr`, `serde_json`, `hashbrown`, `indexmap`.

### Exported API (class `DocxEngine`) with doc comments (verbatim)
```
new DocxEngine()
parse(bytes:Uint8Array)
  "Parse the .docx package. Errors carry the engine's message (the JS
   adapter maps them onto its calm notices exactly as it does for the TS
   engine's throws)."
font_requirements():string
  "JSON array of raw font cuts: [{"family","bold","italic"}, …]."
register_font(family:string, bold:boolean, italic:boolean, bytes:Uint8Array)       (no doc; TTF bytes for metrics)
set_alias(from:string, to:string)                                                   (no doc; font-family substitution)
set_vertical_override(family, ascender, descender, line_gap)                        (no doc; per-family vertical metrics override)
image_ids():string      "JSON array of embedded-image relationship ids, in document order."
image_bytes(rel_id):Uint8Array
image_mime(rel_id):string
layout_json(year, month0, day, weekday0, hours, minutes, seconds):string
  "Lay the parsed document out and return the DocumentLayout JSON.
   The broken-down LOCAL time feeds DATE/TIME field pictures (wasm has no
   timezone database; pass what `new Date()` reports)."
layout_json_incremental(year, month0, day, weekday0, hours, minutes, seconds):string
  "Lay out again and return only what changed since the last call (or
   prime_layout): {"pageCount", "totalLines", "pages": [page | {"reuse": j, "vol": [...]}]}."
layout_profile():string
  "Where a layout's time goes (ms): pagination, building the JSON value,
   serializing it. For measuring editing speed on long documents."
prime_layout(year, month0, day, weekday0, hours, minutes, seconds):number
  "Lay out and remember the pages (an editing session's starting point:
   the browser already shows them). Returns the page count."
reparse()
  "Parse the package again with the parts set since; fonts stay registered."
set_part(path:string, bytes:Uint8Array)
  "Replace or add one part of the parsed package (patched XML, new
   media). Takes effect at the next reparse()."
free()
```

### Data returned: `DocumentLayout` JSON (shape from the serde field-name table in the wasm and from the renderers)
```
{ pages: [ { pageWidthPt, pageHeightPt, marginLeftPt/RightPt/TopPt/BottomPt,
             paragraphs[], tables[], headerParagraphs[], headerTables[], footerParagraphs[], footerTables[],
             footnoteParagraphs[], footnoteSeparatorYPt, pageBorders, lineNumbering, balloonStripWidthPt, floatingImages[],
             comments: {id->...} },
           ...  // or, incrementally: {reuse:j, vol:[numbers patched into volatile fields]}
         ],
  pageCount, totalLines }
paragraph = { paraIdx, page, firstLine, lastLine, contentStartYPt, contentEndYPt, contentXOffsetPt, dropCap, marker, firstLineIdx, lastLineIdx, yPt,
              paragraph:{src,...}, layout:{ lines[], contentHeightPt, leftEdgePt, rightEdgePt, spaceBeforePt, spaceAfterPt, firstLineIndentPt, barTabsPt, alignment, ...} }
line.segments[] = text runs with: text, xPt, widthPt, family, sizePt, italic, weight, letterSpacingPt, kerningEnabled, baselinePt, textBaselinePt,
                  color, underline, strike, doubleStrike, emphasisMark, runBorder, highlight, image{relId, ...}, hyperlink,
                  underlineOffsetPt, underlineThickPt, fieldInstr, tabLeaderChar, baselineShiftPt, footnoteRefId, isEndnoteRef, bookmarkName, commentIds,
                  endsWithHardBreak, endsWithPageBreak, imageMaxHeightPt, src[2] (source offsets for caret mapping)
table = { startXPt, startYPt, layout:{ rows:[{ heightPt, cells:[{ xPt, widthPt, vMergeHidden, shadingFill, borders, vAlign, textDirection,
          innerContentHeightPt, cellMarLeftPtOverride, cellMarRightPtOverride, paragraphs[], nestedTables[] }] }], insideH, insideV, cellSpacingTwips } }
```
So the engine is a complete Word-style layout (line breaking, tab stops/leaders, tables with merges/nesting, floating images, headers/footers/footnotes, drop caps, line numbering, page borders, fields incl. DATE/TIME, comments, numbering markers). Output is absolute positions in points; renderers just paint.

### Fonts
App ships Carlito/Caladea (metric-compatible Calibri/Cambria), Liberation Sans/Serif/Mono, Gelasio (Georgia) under `/fonts/word-viewer/<Family>-<Cut>.ttf`;
substitution table (Calibri/Aptos=>Carlito, Cambria=>Caladea, Arial/Helvetica=>Liberation Sans, Times=>Liberation Serif, Courier New=>Liberation Mono, Georgia=>Gelasio) and vertical-metric override for Gelasio
(`33361` lines 5-78; `set_vertical_override("Gelasio", 1878/2048, -449/2048, 0)`). Layout fidelity depends entirely on these metric-compatible fonts.

### How the app consumes it
- `33361` lines 140-360 (`DocxEngine` load, `font_requirements` -> fetch fonts -> `register_font`/`set_alias`, `layout_json(...)`, image decode, scene with `metadata.docx = {layout, images, pageCount, pageGap:16, source}`, `docxEngine:"wasm"`), `DocxLayoutSession` (lines ~420-520: editing loop `set_part` -> `reparse` -> `layout_json_incremental`, page reuse via `vol`).
- Renderers: `30360` (selectable text layer), `7645` (canvas painter), `84178` (HTML flow), `92895` (editor + PDF export, 12.4k lines).
- Dispatch for `docx`/`pages` in `68243` ~line 16950; legacy .doc (CFB `D0CF11E0`) is converted by `78636`, Pages (`PK` + iWork) by `50284` before the wasm.

### C# effort: XL. Parsing OOXML is easy (DocumentFormat.OpenXml); the cost is the layout engine (justification, kinsoku/CJK, bidi, tab leaders, table autofit, pagination with widow/orphan, floating anchors, footnotes, fields) and glyph metrics (no shaping in this engine, only advance widths + kerning from `ttf-parser`, so C# could use SkiaSharp/HarfBuzz or SixLabors.Fonts for metrics).
**Recommendation**: only if `X-Ray.Office` does not already render Word. If it does, reuse it; this module is the single most expensive item and duplicates that package's purpose.

---

## 4. libarchive (Emscripten C)

**Files**: `E/libarchive.js` (74 KB, MODULARIZE factory `libarchive(moduleArg)`), `E/libarchive.wasm` (613 KB).
**Identity**: libarchive **3.7.7** (`archive_version_string` strings), zlib 1.3.1, xz/liblzma 5.6.4, bzip2, with 7zip/RAR/RAR5/CAB/ISO9660/tar/cpio/etc. readers. External zstd/lz4 are spawn-out stubs (non-functional in wasm). Licence BSD-2-Clause (libarchive); xz public domain; zlib licence.
**Exports used** (`cwrap`, `68243` lines 140-165): `archive_read_new_memory(ptr,len,fmt)`, `archive_read_next_entry`, `archive_read_data(a,buf,len)`, `archive_read_data_skip`, `archive_read_free`, `archive_error_string`, `archive_entry_pathname_utf8`, `archive_entry_filetype`, `archive_entry_size`, `archive_entry_is_encrypted`, plus `_malloc/_free`, `HEAP8`. Also exported but unused: `archive_read_support_format_all`, `archive_read_support_filter_all`, `archive_read_add_passphrase`, `archive_read_has_encrypted_entries`, `archive_entry_{atime,ctime,mtime,birthtime,symlink_utf8,hardlink_utf8}`, `archive_version_*`.
**Consumer**: `68243` module `50190` (lines 123-420+): used only for **RAR, 7z and tar** (`ARCHIVE_LABEL = {rar, 7z, tar}`; zip/gzip go through `fflate`). Flow: list entries (name, size, directory, encrypted), pick the model entry, extract with 512 MB cap per entry, 1 MB read chunks; gzip-wrapped tar handled by `gunzipBounded` (`GZIP_MAX_EXPANDED_BYTES = 512 MiB`); bzip2/xz single-file wrappers get a "extract it yourself" message; "dictionary size is not supported" mapped to a >64 MB dictionary notice. Companion images/sidecars are extracted alongside (`S()`).
**C# effort: S.** SharpCompress (MIT, pure managed) reads RAR/RAR5, 7z (LZMA/LZMA2/PPMd/BCJ), tar(.gz/.bz2/.xz), zip. Keep the same safety caps (entry count, expanded size, path normalisation).
**Worth it**: yes, trivial wrapper; do not port libarchive.

---

## 5. laz-perf (Emscripten C++, embind)

**Files**: `E/laz-perf.js` (87 KB, `createLazPerf`), `E/laz-perf.wasm` (214 KB).
**Identity**: hobu/laz-perf (`lazperf::reader::mem_file`, `chunk_decompressor`, strings "LASF_Spec", "laszip encoded", "Couldn't find LASZIP VLR"). Decompress-only build. Upstream licence believed Apache-2.0; the shipped file carries no licence header, verify before copying code.
**API** (embind classes, from the wasm string table + usage): `LASZip` { `open(ptr,len)`, `getPointLength()`, `getPointFormat()`, `getCount()`, `getPoint(destPtr)`, `delete()` }, `ChunkDecoder` (not used by the app), plus a third helper class. Memory model: copy the whole file into the heap (`_malloc`+`HEAPU8.set`), call `getPoint` per point into a record buffer, read fields at fixed offsets.
**Data**: raw LAS point records in the point-format layout (formats 0-10); the app reads `i32[0..2]` = X,Y,Z scaled ints, `u16` RGB at `rgbOffset`, classification at `classificationOffset`, intensity at offset 12 (`u16 index 6`).
**Consumer**: `30547` (worker). Lines 1-200: LAS header + point-format table (`size`, `classificationOffset`, `rgbOffset`, `nirOffset`, `extended`; formats 0-10 sizes 20,28,26,34,57,63,30,36,38,59,...); lines 200-520: wasm loader, VLR parsing (LASzip VLR `userId "laszip encoded"`, `recordId 22204`; GeoKeys 34735; WKT 2112 -> EPSG, units), pre-flight checks (rejects pointwise-compressor v1 "predates the chunked encoding", detects unfinished/truncated files via the chunk-table offset at `offsetToPointData` (int64) -- placeholder -1 => never finalised), subsampling to `maxPoints`, recentering (Z-up -> Y-up swap: `[x, z, -y]`), colour depth detection (8 vs 16-bit), classification palette fallback.
**C# effort: M.** The LASzip chunked format (compressor 2/3: arithmetic coder + per-field models for point10, gpstime, rgb12/14, extra bytes, point14 layered with per-field streams, chunk table) is public and about 3-4 k lines of C++ in laz-perf. Existing C# option: LASzip.Net (license check required; LGPL for original LASzip lineage). Writing our own decoder with the LAS 1.4 / LASzip spec is safe.
Uncompressed LAS is ~100 lines in C# (all the header/VLR/CRS logic in `30547` is directly portable and does not need the wasm).
**Worth it**: yes, point clouds (LAS/LAZ, COPC) are a major use case; ship LAS first, LAZ second.

---

## 6. manifold (Emscripten C++, embind)

**Files**: `E/manifold.js` (82 KB, factory default export), `E/manifold.wasm` (541 KB, built without threads: `initTBB` stub present).
**Identity**: elalish/manifold (v3.x: has `ExecutionContext`, `Smoothness`, `RayHit`, `CrossSection`, `Manifold.reserveIDs`), Clipper2 inside (error string "There is an undefined error in Clipper2"). Licence Apache-2.0 (Clipper2 Boost). The app calls `wasm.setup()` after load (`40109` line 2755).
**API surface the app actually uses** (grep across the 5 consumer chunks):
- `new Mesh({numProp:3, vertProperties:Float32Array, triVerts:Uint32Array, runIndex?:Uint32Array, runOriginalID?:Uint32Array}); mesh.merge(); new Manifold(mesh)`; `status()` ("NoError" check), `isEmpty()`, `volume()` (negative volume => flip winding and retry).
- Booleans: `add/union`, `subtract`, `intersect` (and N-ary via sequence), `trimByPlane`, `transform(mat4)`, `translate/rotate/scale/mirror`.
- Original-ID tracking: `Manifold.reserveIDs(n)`, input mesh `runIndex/runOriginalID`, output `getMesh().runIndex/runOriginalID` to recover which source object each output triangle came from (per-material/per-part colouring after CSG). `getMesh()` returns `{numProp, vertProperties, triVerts, runIndex, runOriginalID}`.
- CrossSection (2D) path in the OpenSCAD interpreter: `extrude`, `revolve`, `offset`, `hull`, `project`, `slice`, `Manifold.cube/cylinder/sphere`-style primitives, `union/difference` on cross sections, `hull` of points.
**Consumers**:
- `40109` (OpenSCAD interpreter in TS: lexer/parser/evaluator at lines 2760+, geometry kernel using `this.wasm.Manifold/CrossSection` at ~1870-2430; loader `7355` lines 2725-2760; FAQ in `43953` ~5049 says CSG is by manifold-3d).
- `51940` (Cinema 4D parser, Boolean/Symmetry/Extrude generators, `solid()`/`applySteps(op, data)` lines ~255-300).
- `65724` (3ds Max .max: `shapeStack`/`boolean`/`subtractBA`/`ParamBlock`, same helper, lines 40-100).
- `91081` (Archicad GDL interpreter: wall/roof cut operations `cutpoly/cutplane/...`, `i.manifold && (n.ops = x(manifold))` line 815, op replay at 2386; helper `87192` lines 5-70 with `reserveIDs/runOriginalID`).
- `99720` (Bazis furniture: panel/hardware boolean subtraction; same `87192` helper at 855-977).
All five reuse the same two helper shapes: a "triangle soup => Manifold with dedup (merge) and winding fix" and a "run original ids => per-source triangle groups".
**C# effort**: robust general mesh booleans = XL (Manifold's collider, symbolic perturbation "Kernel12", halfedge topology, simplification; ~15k lines plus Clipper2 for 2D). A pragmatic L-scale subset: halfedge mesh + AABB-tree triangle-triangle intersection with exact predicates + classification, plus Clipper2 (there is a C# port: Clipper2 by Angus Johnson ships a C# version, Boost licence) for CrossSection and our own extrude/revolve/offset. Expected failure modes on non-manifold or coplanar input; mitigate by falling back to un-subtracted meshes (which the app already does when `status()!=="NoError"`).
**Worth it**: only as a shared service for OpenSCAD/C4D/3ds Max/GDL/Bazis; defer. Check whether a maintained C# binding of Manifold (native) is acceptable, otherwise it conflicts with the "pure managed" rule.

---

## 7. capstone (Emscripten C)

**Files**: `E/capstone.js` (331 KB, global `MCapstone` factory, not embind: exposes `_cs_*` C API plus JS `Capstone` class and ARCH_*/MODE_* constants), `E/capstone.wasm` (3.3 MB).
**Identity**: Capstone **5.x** (ARCH_MAX=18 includes TRICORE, SH, RISCV, WASM, BPF, MOS65XX, EVM, TMS320C64X, M680X, XCORE, SYSZ, SPARC, M68K). BSD-3-Clause. Wrapper style similar to the `capstone-wasm` npm packaging: `new Capstone(arch, mode).disasm(bytes, addr)` returns `[{address, bytes, mnemonic, op_str, size, ...}]`.
**Exports**: `cs_open/close/option/disasm/disasm_iter/free/malloc/version/support/errno/strerror/reg_name/insn_name/group_name/insn_group/reg_read/reg_write/op_count/op_index/regs_access`.
**Consumer**: `6007` (a PE executable/DLL "code view" feature: parse PE sections, sections' bytes => linear disassembly up to 4000 instructions; lines 1195-1420; machine map: 0x14c x86-32, 0x8664 x86-64, ARM64 0xAA64/..., ARM 0x1C0, Thumb2 0x1C2/0x1C4, PPC 0x1F0/0x1F1, MIPS 0x166/0x366/0x466, RISC-V 0x5032/0x5064). Post-processing resolves RIP-relative / absolute operands to strings and imported API names (a table of ~hundreds of Win32 prototypes), `.data` gap rows for undecodable bytes.
**C# effort**: S for x86/x64 using Iced (MIT, pure C#, full decoder + formatter); other architectures XL each. **Worth it**: low; it is a binary-inspection niche, not 3D. Only do x86 via Iced if PE support is in scope.

---

## 8. occt-import-js (Emscripten C++, embind)

**Files**: `E/occt-import-js.js` (97 KB, global `occtimportjs` factory), `E/occt-import-js.wasm` (7.6 MB).
**Identity**: kovacsv/occt-import-js (OpenCASCADE 7.x: STEP/IGES/BREP readers, `BRepMesh`). Licence LGPL-2.1 (occt-import-js) over OCCT (LGPL-2.1 with OCCT exception).
**API** (embind functions): `ReadStepFile(Uint8Array, params)`, `ReadIgesFile(Uint8Array, params)`, `ReadBrepFile(Uint8Array, params)`, `ReadFile` (generic). `params`: `{linearUnit, linearDeflectionType, linearDeflection, angularDeflection}` (all optional; app passes `null`).
**Returns**: `{ success, root:{name, meshes:[idx], children:[...]}, meshes:[{ name, color?:[r,g,b], attributes:{position:{array}, normal?:{array}}, index:{array}, brep_faces:[{first,last,color?}] }] }` (`brep_faces` = triangle ranges per B-rep face, used for face picking/per-face colour).
**Consumers** (only IGES and BREP are used; **STEP is parsed by a TS parser in chunk `17666`**, `ReadStepFile` is never called):
- `10209` / `63731` (worker, same code): `parse` => `ReadIgesFile` (line 178), `brep` => `ReadBrepFile` for an array of `{key,data}` (line 118); progress by parsing OCCT's stdout text (`print/printErr` hooked, lines forwarded as detail).
- `13031` (FreeCAD .FCStd reader): extracts `.brp/.brep` files from the zip, tessellates in the worker via `ReadBrepFile`, a Node fallback `require("occt-import-js")` (line 142); also contains a TS BREP text parser with NURBS evaluation (`Locations|Curve2ds|Curves|Polygon3D|PolygonOnTriangulations|Surfaces|Triangulations|TShapes` blocks, line ~118 `b` regex; "BREP curve type" error line 781), used where the wasm is skipped. FreeCAD binary shapes ("OpenCASCADE's binary format") are explicitly unsupported (line 985).
**C# effort**: BREP text with stored triangulations: M (parse the OCCT BREP v1-3 text, and if `Triangulations` blocks exist just read them; the TS code in `13031` is a ready reference, otherwise needs surface tessellation). IGES from scratch: L-XL (entity set 100-144, 126/128 NURBS, 142/143/144 trimmed surfaces, 186 MSBO; trimmed-surface tessellation robust enough for real files is the hard part). A general B-rep mesher = XL (this is OCCT).
**Worth it**: the BREP-with-triangulation path yes (FreeCAD), IGES maybe later; do not attempt to reproduce OCCT's mesher.

---

## 9. web-ifc (Emscripten C++, embind)

**Files**: `E/web-ifc-api-iife.js` (6 MB: bundled `WebIFC` global incl. the TS API, the IFC type/property schema tables and the embedded glue), `E/web-ifc.wasm` (1.3 MB, non-threaded).
**Identity**: ThatOpen `engine_web-ifc` ("Source: https://github.com/ThatOpen/engine_web-ifc", has `OpenModelFromCallback`, `GetAllAlignments`, `GetAllCrossSections2D`, newer Emscripten with `applySignatureConversions`, so roughly 0.0.6x-0.0.7x). Uses glm, tinynurbs, earcut/clipper-style boolean code; schemas IFC2X3, IFC4, IFC4X3. Licence MPL-2.0.
**API actually used** (`72968`): `new WebIFC.IfcAPI(); SetWasmPath(url,true); Init(); SetLogLevel(LOG_LEVEL_OFF); OpenModel(Uint8Array, {COORDINATE_TO_ORIGIN:true}) | OpenModelFromCallback((offset,len)=>Uint8Array, settings); StreamAllMeshes(modelID, cb(flatMesh)); GetGeometry(modelID, geomExpressID) -> {GetVertexData(),GetVertexDataSize(),GetIndexData(),GetIndexDataSize()}; GetVertexArray/GetIndexArray(ptr,size); GetLineType; GetNameFromTypeCode; GetLineIDsWithType; GetLine; WriteLine; GetLinearScalingFactor; GetModelSchema; GetCoordinationMatrix; CloseModel; wasmModule.HEAPU8`.
Other IfcAPI methods present but unused: `GetFlatMesh, StreamAllMeshesWithTypes, GetAllLines, GetAllTypesOfModel, GetRawLineData, CreateModel/SaveModel, GetMaxExpressID, SetGeometryTransformation, GetAllAlignments, GetAllCrossSections2D`.
**Data returned**: per `flatMesh {expressID, geometries[]}`; each geometry `{geometryExpressID, flatTransformation:number[16], color:{x,y,z,w}}`; vertex data is interleaved `[x,y,z,nx,ny,nz]` floats (6 per vertex, de-interleaved in `72968`), index data Uint32. Geometries are deduplicated by `geometryExpressID` (instancing), colours per placed geometry.
Extra app logic in the worker (`72968` lines 85-130): a pre-pass that repairs chained `IfcBooleanClippingResult`/`IfcBooleanResult` DIFFERENCE trees (type codes `2736907675` IfcBooleanResult, `3649129432` IfcBooleanClippingResult) by deduplicating repeated second operands and rewriting with `WriteLine`; chunks results to the main thread when >32 MiB; `elevationOffset = -coordinationMatrix[13]`; `scaleToMeters = GetLinearScalingFactor`.
**Consumer**: `72968` (worker, 245 lines, everything above); `40825` (catalogue text "walls, slabs, windows, doors, and MEP, grouped into per-type layers. Tessellated via web-ifc").
**C# effort**: XL for parity: a STEP-physical-file tokenizer/line index (M), IFC schema tables (generated, M), and geometry: extruded/swept/revolved area solids with profiles, faceted/advanced BREP, tessellated sets, CSG/boolean clipping & half-space, mapped items, B-spline surfaces, curve-based sweeps, alignments, placement chains, units, openings (voids) as booleans (needs a boolean kernel => ties to manifold/mesh CSG above). A common-subset (extrusions, faceted breps, mapped items, tessellated, placements, colours, openings via CSG) is L.
**Worth it**: high user value (BIM) but expensive; consider after the shared mesh CSG exists. xBIM is native-dependent for geometry, so not a pure-managed shortcut.

---

## 10. rhino3dm (Emscripten C++, embind)

**Files**: `E/rhino3dm/rhino3dm.module.js` (143 KB, ES module factory `rhino3dm(config)`; loaded via `self.__offlineImport`), `E/rhino3dm/rhino3dm.wasm` (2.6 MB).
**Identity**: McNeel rhino3dm (openNURBS built from `src/lib/opennurbs/*` in CI path `/__w/rhino3dm/...`; includes `DracoCompressionOptions`). Licence MIT (rhino3dm), openNURBS BSD-style.
**API used**:
Import (`56536`): `File3dm.fromByteArray(Uint8Array)`, `.archiveVersion`, `.layers()/.materials()/.objects()/.instanceDefinitions()` (`.count`, `.get(i)`), object `.geometry()`/`.attributes()`, `geometry.objectType` (`ObjectType.{Mesh,Brep,Surface,Extrusion,Curve,SubD,...}`), `Mesh.vertices()/faces()/normals()` (faces are quads/tris `[a,b,c,d]`), `Extrusion.getMesh(MeshType.Render|Default)`, `Brep.faces().get(i).getMesh(...)`, `toBrep()`, `toNurbsSurface()`, curve sampling (`J()` helper), `Mesh.createFromSubDControlNet`, `ObjectMaterialSource`, `InstanceDefinitionUpdateType`; every wasm object must be `.delete()`d (helper `s()`/`S()`).
Export/resave (`61216` lines 3944-4070): `new File3dm(); layers().addLayer(name,color); objects().addLine(...)/add(Mesh,attrs); Mesh.createFromThreejsJSON({data:{attributes:{position,normal},index}}); new File3dmWriteOptions(); opts.version = 2..8; file.toByteArrayOptions(opts) -> Uint8Array`; and re-version an existing 3dm (`fromByteArray` => delete objects on hidden layers => `toByteArrayOptions`).
**Notable app logic**:
- The app does NOT ask rhino3dm to mesh B-reps: it reads **stored render meshes** (`getMesh(Render)` then `Default`) and, for faces with none, uses its own TS fallback mesher (`56536` lines ~900-1100, including reading the 3dm's embedded "archive3dm >= 50" base64 data blocks and meshing from edges), with a triangle budget by device class.
- `56536` has its own **streaming 3dm chunk walker** in TS (lines 20-120 + `L` at 280+): header "3D Geometry File Format NN", chunk header `{tcode u32, value i32|i64 (wide for archive >= 50)}`, table tcodes (materials 0x10000010, layers 0x...11, lights 0x...12, object table 0x10000013, properties, settings, bitmaps, groups, fonts, dimension styles, block definitions, hatch patterns, linetypes, layer sets, texture mappings), splits the object table into batches, feeds each batch to rhino3dm by synthesising mini 3dm archives (`v()`), and bisects on failure to isolate bad objects. This is the way to open 500 MB+ files in a tab; C# does not need this since it can stream natively.
- Coordinate swap Rhino Z-up => scene Y-up: `(x, z, -y)`; instance definitions expanded (`expandInstances`, depth cap `U`, triangle budget), layers => scene layers with counts, materials by `ObjectMaterialSource`.
**C# effort**: reading stored meshes + layers + materials + instances + curves: L (3dm chunk container is documented in the openNURBS sources; need zlib inflate of compressed buffers, mesh/brep render-mesh deserialisers for each archive version 2-8, Draco-compressed meshes (v7+) optionally; user data skipped). Full B-rep meshing: XL (use stored meshes and warn). Writing meshes + layers to 3dm: M.
**Worth it**: yes for read (Rhino is a core CAD format); write is optional. No pure C# openNURBS reader exists; Rhino3dmIO .NET is native.

---

## 11. Cross-cutting notes for the C# design

1. Every wasm module here is already isolated behind a small adapter returning plain buffers (positions/normals/indices) or JSON. Mirror that: one reader per format returning a common scene model; keep loader heuristics (units, up-axis, extents repair, 2D-vs-3D decision, triangle budgets) separate from decoders.
2. Hostile input rules (family CLAUDE.md): every Rust module has explicit budgets ("FDB allocation count budget", "Total expanded heap budget", `MAX_BROWSER_OBJECTS = 1.2e6`, 512 MiB caps in the archive path, per-chunk bisecting in the 3dm path). Copy the budget concepts, not the numbers blindly.
3. Shared dependency ordering for a roadmap: (a) wrap libraries: SharpCompress, ACadSharp, Iced; (b) LAS/LAZ; (c) 3dm read; (d) shared mesh CSG + Clipper2; (e) IFC subset; (f) BREP/IGES; (g) docx layout (only if Office package lacks it); (h) PLN last.
4. The app keeps TS twins/fallbacks for DWG (`5605`), STEP (`17666`), BREP text (`13031`), 3dm chunk walking (`56536`), GDL (`91081`), OpenSCAD (`40109`), Bazis (`99720`), C4D (`51940`), 3ds Max (`65724`). These are readable references for the *output model and heuristics*; they are the viewer authors' code, so port behaviour, not text.
