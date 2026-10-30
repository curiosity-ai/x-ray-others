# Reference viewer: data-model features, export menu, scene-tree construction

Scope: everything in the reference viewer that is not pure rendering UI but needs a data model
(for X-Ray.3D), the complete Export menu, and how scene trees are built from each loader.
Brand/site name deliberately omitted ("the reference viewer").

Chunk legend (all under `the beautified app chunks`, line numbers are of the beautified files):

| Short | File | What it is |
|---|---|---|
| UI    | `39776.2e95610d778bf962.js` | Main viewer React component: menus, panels, export dispatch |
| P68   | `68243-ae5252348383cf8a.js` | Loader front-door (`parse`), format dispatch, post-parse normalisation, explode math, SOG reader |
| P53   | `53699-7cb1be57a1412a5c.js` | The **Scene data model** helpers: `emptyScene`, flatten (`u$`), bounds, matrix helpers, edit ops, cut, layer drop |
| EXP   | `61216.f301940b09701055.js` | **All exporters** (GLB/STL/OBJ/USDZ/3MF/...), HTML-export packager, rhino3dm loader |
| R3D   | `8523.f40da954b9584da8.js` | Three.js renderer class: Scene -> Three objects, explode, layers, render modes, picking |
| TH3   | `78076.bf1eeb58978da432.js` | module 97397: **Three.js Object3D -> Scene converter** shared by GLB/GLTF/FBX/DAE/USD/VRML/PLY |
| GLB   | `52242.a002a70331aa1c6d.js` | `parseGlb` (uses GLTFLoader + Draco + meshopt) |
| STL   | `67931.f8763552a5eb7eee.js`, OBJ `35144.3543921b10e805c2.js`, STEP `17666.15731ba6da80a25e.js`, IFC `93149.9228d077bf502999.js`, 3DM `56536.b8b274b491f73c96.js`, 3MF `73456.bad4caa8812f04c5.js` | per-format parsers |
| HTMLX | `extract/vendor/html-export/viewer3d.min.js` | Stand-alone three.js viewer embedded in "HTML export" files |

---------------------------------------------------------------------------------------------

## 0. The one thing to understand first: a single in-memory `Scene` is the hub

Every one of the ~100 importers returns the same plain-JS object; every exporter, every panel,
the explode/cut/measure tools and the renderer consume that object. There is **no serialised
internal format**: the model is never cached or persisted as such (see section B.4). For the C#
library this object *is* the design target (an `XRay.3D.Scene` class). Reconstructed shape
(from `emptyScene` P53:~31-48, the TH3 converter, `buildMesh`/`buildMaterial`/`buildTexture` in
R3D:3129-3336, and the GLB exporter EXP:969-1135):

```
Scene {
  format: string                       // loader id: "stl","obj","step","glb","ifc","3dm",...
  units: "mm"|"cm"|"m"|"in"|"ft"|"unitless"   // default "mm" (P53 emptyScene)
  displayUnits?: same enum             // used for drawings (EXP/43461:1793-1847)
  bounds: { min:[x,y,z], max:[x,y,z] } // recomputed by nK() if not finite (P68:16213-16380)
  root: Node                           // tree, see below
  meshes: Mesh[]                       // flat pool, referenced by id from nodes
  materials: Material[]                // flat pool
  textures: Texture[]                  // flat pool
  layers: Layer[]                      // flat list (NOT a tree)
  animations?: Clip[], skins?: Skin[]  // rigs and clips (R3D:965-1060, EXP:45587-)
  drawing?: { entities[], bounds }     // 2D drawings (DXF/DWG/SVG/PDF): separate branch
  metadata: { [k:string]: any }        // free-form per-loader bag (see 1.9)
}
Node  { id, name?, visible, transform?: number[16] /*column-major*/, layerId?,
        meshIds?: string[], children?: Node[], isJoint?: bool }
Mesh  { id, positions: Float32Array(3n), indices: Uint32Array,
        normals?, uvs?(2n), colors?(Float32 RGBA 4n) | colorsRGB8?(Uint8 RGB),
        colorSpace?: "linear"(else sRGB), topology?: "triangles"(default)|"lines"|"points",
        materialId?, groups?: [{start,count,materialId}] /*in TRIANGLES*/,
        bounds?, pointSize?, splat?, skin?, skinIndices?, skinWeights?,
        morphTargets?: [{name,positions,normals?}], morphWeights?, edgeOverlay? }
Material { id, name?, color?:[r,g,b,a] (0..1), colorSpace?, metallic, roughness, opacity?,
        alphaMode?:"OPAQUE"|"MASK"|"BLEND", alphaCutoff?, doubleSided, unlit?, vertexColors?,
        emissive?:[r,g,b], emissiveIntensity?, transmission?, thickness?, ior?, clearcoat?,
        baseColorTextureId?, normalTextureId?, normalScale?, roughnessTextureId?,
        metalnessTextureId?, occlusionTextureId?, occlusionStrength?, emissiveTextureId? }
Texture { id, bytes?|url?, mimeType?, width?, height?, flipY?, channel?, colorSpace?,
        offset?, scale?, rotation?, magFilter? }
Layer { id, name, visible, count?, color?: [r,g,b,a] }
```

Facts that matter for the design:

* Mesh data is **per-mesh typed arrays, shared by reference**: a node lists `meshIds`; several
  nodes can list the same id (instancing). The renderer caches one `BufferGeometry` per mesh
  object (R3D:3262-3280 `geometryCache`), and the GLB exporter re-uses the Three mesh for a mesh
  id that appears twice (EXP:~1085-1100, `T.has(t) -> e.clone(false)`; shares geometry and
  material so GLTFExporter emits one glTF mesh referenced by two nodes).
* A mesh carries only **one material** or per-range **groups** (material per triangle run); there
  are no "submesh" objects. Triangle `start/count` are in triangles, not indices (TH3:~470
  divides three.js group offsets by 3; cut code P53:100-540 uses the same convention).
* **Layers are a flat name list; nodes point to a layer by `layerId`**. Layer visibility is the
  only visibility filter the UI exposes. Names are the identity: all tools (hide, explode
  filter, export drop) key on `layer.name`, not on id (UI:1292, R3D:2012-2020, P53:1120+).
* `Scene.units` is only a label for display/measure/export scale; vertices are never rescaled at
  load (exception: glTF-family loaders set `units="m"`; 3MF loader reads `<model unit>` and
  scales micron into mm, 73456:~826-846; IFC bakes `scaleToMeters` into the root transform and
  sets `units="m"`, 93149:~226-260).
* Typical `bounds` are always carried and re-derived after every edit (`nK`).

---------------------------------------------------------------------------------------------

## A. Data-model-relevant features

### 1.1 Scene / assembly tree

* **There is no tree panel and no per-node properties panel in the viewer.** The only
  tree-aware UI is the **Explode** panel (UI:3624-3790, `tS`) and the **Layers** panel (UI:4433,
  `t4`). The tree exists in the data (Node nesting, transforms, names) and is used for:
  explode, GLB/STEP/IFC/OBJ/USDZ/3DM export hierarchy, flattening, edit ops, and the renderer's
  `nodeGroups` map (nodeId -> Three Group; R3D:3232-3260).
* Node id rules: loaders generate stable string ids (`"root"`, `"el-<expressID>"`,
  `"layer-node-<n>"`, `"part-<n>"`, STEP: `"root"`/`"<parent>/<index>"`, Three converter:
  the Three `uuid`). Ids only need to be unique inside the scene; ids are re-prefixed
  (`add<N>-`) when two scenes are merged (P53:857-935, "Add another model").
* Node `visible:false` is honoured at render time (R3D:3250) but export dispatch ignores it for
  most formats; hidden **layers** are physically removed before export (P53:1120-1175
  `dropLayers`, called by the export dispatcher, UI:16990-17035, and by
  the room/plan analysers).
* Data model needed: `Node { Id, Name, Visible, LayerId, Matrix4x4? Transform, List<MeshRef>,
  List<Node> Children, bool IsJoint }` + `Scene.Meshes` as dictionary by id. Keep the transform
  *on the node* (not baked), because instancing and later unit/up-axis correction rely on it.

### 1.2 Per-node properties (extras / metadata)

* The viewer has **no node-level property bag at all**. The Scene->Three converter copies only
  `id`(uuid), `name`, `visible`, `matrix`, mesh ids and children (TH3:326-560); three.js
  `userData`/glTF `extras` on nodes are dropped, as are IFC property sets, STEP product
  attributes, etc. IFC's loader keeps the element's *type name* as the layer and builds a node
  name `"<Type> #<expressID>"` (93149:~296-300) and nothing else.
* Model-level information goes in `Scene.metadata` (a loose dictionary, e.g. `generator`,
  `version`, `triangleCount`, `groupCount`, `productCount`, `uniqueGeometryCount`,
  `scaleToMeters`, `upAxis`, `upAxisSource`, `recentered`, `sourceOrigin`, `ascii`,
  `missingMaterials`, `missingParts`, `missingTextures`, `simplified{from,to}`, `blockInstances`,
  `facetStream`, `editMergeCount`...). It is surfaced only as notice chips (UI:15725-15800,
  18842-19420); nothing is exported except the 3MF/OBJ/STEP header comments.
* **Implication for X-Ray.3D**: this is the biggest gap to fill - the task wants part names,
  properties, materials *and* metadata. The data model needs `Node.Properties:
  IReadOnlyDictionary<string, PropertyValue>` (grouped psets: name -> {key -> typed value +
  unit}) and `Scene.Metadata`; the reference viewer is not a model for this, only for names.

### 1.3 Layers

* Source: DXF/DWG layers, 3DM layers (full path `"Parent::Child"`, with colour, visibility, render
  material; 56536:455-480), IFC type names (`type-<n>`, 93149:~262-275), SketchUp tags,
  glTF none, cut results (`Above cut`/`Below cut`, `cut-l<i>`; P53:100-540), PCB boards (Gerber
  layers), embroidery (thread colours) etc.
* **Synthesised "parts" layers**: when a mesh model has no layers, the post-parse step (P68:16213,
  `part:<n>` at 16257) walks down through single-child wrappers (<=4 levels), then if the top
  level has 2..300 children it makes one layer per child, named by the child's trimmed name or
  `"Part N"`, with `count` = meshes beneath it. The UI then titles the panel "Parts"
  (UI:15787 `layersAreParts`, UI:17630). SketchUp titles it "Tags". So for assemblies
  the layers list is effectively a flattened first-level assembly breakdown.
* Layer panel: checkbox list with filter box (>8 items), shift-click range, All/None, per-layer
  `count` (UI:4433-4530); hidden set persisted in the share link (`hiddenLayers`, UI:6305, 16591).
* Data model: `Layer { Id, Name, Visible, Color?, Count }`, a many-to-one `Node.LayerId`. Model
  it as flat list + optional `Path` (3DM-style `A::B`), not as a tree.

### 1.4 Materials / colours

* Material = glTF-like PBR (metallic/roughness), see 0. `color` is RGBA in 0..1; `colorSpace`
  `"linear"` marks values already linear (all three.js-derived materials, `emptyScene`'s
  `c6` helper P53:~62-66 sets everything linear), otherwise sRGB (default). Textures and
  vertex colours are interpreted accordingly (R3D:3149-3211; GLB exporter EXP:~1000-1050).
* Defaults when a source has none: `[0.78,0.78,0.82,1]` (STL/OBJ/STEP/TH3 "Default"), roughness
  0.7-0.85, metallic 0, doubleSided true. The renderer's fallback is `0xcccccc`, roughness .85,
  polygonOffset (R3D:3211-3231). `__default__` is built on demand.
* Material dedupe rules: STEP keys by `"r,g,b"` to 3 decimals (17666:~1135); IFC by RGBA to 3
  decimals (93149:~236); OBJ by MTL name; 3DM one per Rhino material + `(default)`.
* Extra PBR properties supported on import and rendering: `clearcoat`, `transmission`, `ior`,
  `thickness`, `emissiveIntensity`, `unlit`, `alphaMode MASK/BLEND` (render promotes to
  `MeshPhysicalMaterial` when any of those are set; R3D:3149-3200). No sheen/specular/volume
  attenuation/anisotropy in the data model although the stand-alone HTML viewer can display them.
* Per-triangle-run material = `Mesh.groups`; vertex colours = `Mesh.colors` (RGBA float) or
  `colorsRGB8` (point clouds), with `Material.vertexColors=true` and `colorSpace`.
* Colour override modes live in the renderer only (`applyColorMode`, `applyMaterialColors`,
  R3D:2357-2375), not in the data.

### 1.5 Units and unit conversion

* Enum `mm,cm,m,in,ft,unitless` with the factor table to millimetres
  `{mm:1,cm:10,m:1000,in:25.4,ft:304.8,unitless:1}` (EXP/43461:1793-1847 `$6`; duplicated
  in P53:~848 and UI `ag` 10101). Metres table in UI:12981.
* Importer rules (examples): STL none ("mm" default, `lV=new Set(["stl"])` excludes STL from
  unit-guessing, UI:12988); STEP always `"mm"` (OCC/tessellator already emits mm; 17666:~1111);
  IFC `"m"` + `scaleToMeters` on the root matrix; GLB/GLTF `"m"` (GLB:~690-700); 3MF reads the
  `unit` attribute (73456:826-846); FBX uses `UnitScaleFactor/100` when `useFileScale`
  (`59731:1085`); 3DM `modelUnitSystem`.
* User override: Tools -> "Source units (file says: X)" with *Auto - trust file* /mm/cm/m/in/ft
  (UI:1300-1340, state `unitsOverride`, UI:14593). **The override changes only how distances,
  scale bars and plan/room analysis are labelled** (UI:17644-17700, 18031, 20090); it does not
  rescale geometry.
* Display: metric (mm, switches to m at >=1000 mm) vs imperial (feet-inches); format in
  UI `av` 10114-10130; scale bar picks 1/2/5 x 10^n steps (UI:9830-9890).
* Export: STEP/IFC/3MF/AMF scale vertices to mm (`$6(units)`), USDZ writes `metersPerUnit`
  (`US`), DXF writes INSUNITS codes (`EH`), 3DM writes `modelUnitSystem` (EXP:3937-4010),
  **GLB/OBJ/STL/PLY write raw numbers with no unit field** (GLB/glTF is metres by spec: a gap).
* Data model: `enum LengthUnit`, `double ToMeters`, `Scene.Units` + `Scene.UnitsSource`
  (declared | assumed | overridden).

### 1.6 Up-axis normalisation

* Internal convention is **Y-up, right-handed** (three.js). Z-up sources are fixed by a
  **root-node matrix**, not by rewriting vertices: constant
  `[1,0,0,0, 0,0,-1,0, 0,1,0,0, 0,0,0,1]` (column-major; `STL_Z_UP_TO_Y_UP` STL:~140, `f` in OBJ
  35144:~875). `bounds` are re-expressed (`[minx, minz, -maxy]..`), and
  `metadata.upAxis="z"` / `upAxisSource="flat-plate"` records why.
* Heuristics (not metadata) for formats with no declared axis: STL "flat plate" test
  (`z-extent <= 0.25 * min(x,y)` and not produced by this viewer; STL:~135-150); OBJ "stands on
  Z=0" test (min z ~ 0, min y < -5% height, tall in Z, many faces lie on the z-min plane;
  35144:~880-940). PLY reads comments "up axis/vertical axis" (68243:3534; nerfstudio z-up).
  FBX reads `UpAxis` (59731:1082); U3D takes it from PDF views (43953:6574); SOG/3DGS uses
  `axes:"y-down"` (68243:~4100). glTF is Y-up by spec.
* Data model: `Scene.UpAxis` {Y,Z,...}, `Scene.UpAxisSource`, and apply through root transform.

### 1.7 Bounding box / centring

* `bounds` = AABB over every mesh in **world** space (`nK` in P53; per-mesh `bounds` cached in
  local space).
* **Far-coordinate recentering** at parse time (P68:16290-16380): if any axis centre is
  >256x the largest extent away from the origin, the whole model is shifted by the rounded
  offset (rounded to a power of ten of the extent) either by editing vertex arrays in place
  (only if safe: each mesh referenced once, no morph/skin) or by wrapping in a `"<id>~far"`
  child with a translation; `metadata.recentered={offset,rebasedMeshes}` and `sourceOrigin`
  (Y/Z swapped) are recorded. This solves float32 jitter for survey/BIM data.
* Edit ops: "Center on origin", "Drop to floor", "Lay flat (pick a face)" (UI:1450-1520) become
  matrix ops (`{kind:"matrix"}`) merged into the root transform (P53:920 `po`).
* Camera framing uses bounds (R3D:1335-1360 `fitDistanceFor/frameBounds`); `Qj`=size, `dM`=centre
  (P53:~63-70).

### 1.8 Measurement

* Two-click distance only (no angle, area, radius). Data: `Measurement {id, a:[x,y,z],
  b:[x,y,z], distance}` in **scene units**, list `measurements` in UI state (UI:8173, 20092);
  picks come from a BVH raycast `pickPoint/pickSurface` (R3D:1555-1600; BVH in module 59264
  `raycastMesh`) with snapping disabled for splats. `projectToScreen` draws the leader
  (UI:9886-9960). Label = `av(distance, sceneUnits, displayUnits)` (UI:10114).
* Measurements are shown in animation poses: "Measurements apply to the pose at t s".
* Data model: store picked points in **model space** (world, after root transform), plus the unit.

### 1.9 Sections / clipping

* "Edit -> Plane cut" (UI:420-540): axis x|y|z (Y is up), position slider between bounds, keep
  `above|below|split`. Live preview is a translucent plane mesh (R3D:2075-2095 `setCutPreview`);
  there is **no GPU clipping plane**. Apply runs `P53:100-540` (module 19165, `cutScene`): per
  mesh, per triangle classification against `v = 1e-9*diag`, edge-split, vertex/normal/uv/colour
  interpolation, **cap generation** (collect cut segments, chain loops, even-odd nesting,
  earcut triangulation; material `"Cut face"` `[.64,.67,.72,1]`), result *replaces* the scene
  (`root` rebuilt as children per layer or `Above cut`/`Below cut` layers when `split`).
  So a section is a destructive, serialisable op (`c,<axis>,<pos>,<keep>`).
* Edit ops are serialised to a compact string `m<16 floats>;c,x,0.5,above;d<layers>;u,dx,dy,dz;
  s,ratio` for the share link (P53:940-1010 `B`/`F`): matrix, cut, drop-layers, duplicate,
  simplify. This is the only "document" the viewer ever serialises besides the original file.
* Data model: `SectionPlane {Axis|Normal+Offset, Keep}` + a cut routine; C# can do the same
  triangle clip + cap algorithm headlessly.

### 1.10 Explode

* UI: Explode panel (UI:3624+): mode `radial|x|y|z`, `factor` (default .5), `depth` 1..4,
  `stagger`, `duration` 2.5 s, `loop once|loop|pingpong`, `easing smooth`, layer filter
  (P68:14111-14200 `i`, `a=.9` max stagger, `o=4` max depth).
* Parts = nodes at depth d of the tree (`Bc`): descend through single-child wrapper nodes with no
  meshes; a level is explodable if it has >=2 and <=2000 members; name = trimmed node name, else
  the first named descendant along a single-child chain (<=4), else `"Part N"`; mesh-only leaves
  named `"Solid N"` (P68:14165-14200).
* Offsets: per sibling group, from member box centres to the group centre: radial vector
  (min length 0.2 x group diagonal, Fibonacci-sphere fallback for coincident centres) or sorted
  axis spread (`tn`, 14230+). Applied as `group.position = base + offset*ease(t,index)`
  (R3D:2108-2210). It is a pure view effect, but depends on the node tree having sensible nesting.
* Data model: nothing beyond Node tree + names + per-node AABB.

### 1.11 Wireframe / edges / render modes

* Modes (UI:~805-840): `solid`, `wireframe`, `x-ray` (opacity .35), `technical` (white
  surface + dark feature edges, R3D:2496-2603) plus view presets top/bottom/front/back/left/right.
  Feature edges are **extracted at runtime** with a 30 degree crease threshold and a 4 M budget
  (`meshEdgeOverlay(t, s, 30, ..)`, R3D:2375-2410, 2541-2603); a `Mesh.edgeOverlay` flag marks
  loader-supplied line meshes. "Outline creases" is a lighting option (UI:4892).
* Mesh creation for STL welds vertices by position and recomputes crease-shaded normals at 40
  degrees (`weldByPosition`, `shadeWithCreases`; STL:~85, only up to 4 M triangles).

### 1.12 Mesh statistics

* Only counters: `Pw(scene)` triangle count (sum of `indices/3` over non-line/non-point meshes;
  P53:19174), `Ox` point count, `Uf` simplifiable triangle count; shown as the chip "N
  triangles | lines | points | splats | entities | pages" with the unit word chosen by format
  (UI:18842), plus layer count, plus `metadata.simplified` ("simplified to X of Y triangles").
* **No vertex count, no surface area, no volume, no bounding-box dimensions panel, no
  per-part table.** Simplification is meshoptimizer `MeshoptSimplifier` (24487:48-69) with
  device-dependent triangle budgets (`WZ()` low/medium/high; `a.D2` for STEP).
* Data model: easy to add in C# (signed-volume, area per mesh/node/instance, AABB dims, unique
  vs instanced counts); these are not reference-viewer features.

### 1.13 "Model info" / metadata panel

* There is none. Closest things: the toolbar chip row (UI:18800-19420), the
  `<viewer>:model-loaded` event `{fileName, format, triangleCount, layerCount}`
  (UI:17535), the per-format "missing files" banners (`missingParts`, `missingMaterials`,
  `missingTextures`, `missingLevelFiles`...; UI:15725-15780) that ask for companion files, and a
  "Source units" menu. Share-link payload carries camera, hidden layers, explode, animation
  state, edit ops (UI:6290-6330, 16591, 16703).

### 1.14 Other data-model-relevant tools

* **Animation**: clips (`tracks[{nodeId, path: translation|rotation|scale|morph}]`, interpolation
  `linear|step|cubic`), skins/bones (`isJoint`), morph targets with weights. Exported to GLB with
  clips, shared skins per bind matrix, sparse morph accessors (EXP:45587-, 48782 `a`).
  Non-GLB exports bake the current pose (`bakeCurrentPose`, UI:16895-16907).
* **Combine**: "Add another model" merges a second scene under a new root with unit conversion
  and id prefix; "Duplicate (side by side)"; "Remove hidden parts" (P53:857-935).
* **Simplify** (`s,ratio` op), **Mirror X/Y/Z**, **Scale/Rotate/Move** (root matrix).
* **Floor plans / rooms**: slices geometry per storey for DXF/DWG plan export (chunk 29940
  `buildFloorPlans`) and room detection (UI:17640-17720); needs units and up-axis.

---------------------------------------------------------------------------------------------

## B. Export / convert menu

### B.1 Complete list of items (UI:1596-1960)

The Export menu (`label: "Export"`, UI:1597) builds its entries from scene kind. 3D scenes see the
"3D mesh" group (or "3D points" for point clouds, `ec`), the CAD group, Web, Document,
Image; drawings see CAD/Vector/Web. Dispatch is one `switch`-like chain in `lj`, UI:16861-17520.

| # | Menu label | id | Implementation | Chunk ref |
|---|---|---|---|---|
| 1 | STL (.stl) | stl | **custom** binary writer, world-space, 80-byte header, normals from face, no colour | EXP:1509 `r`, UI:17038 |
| 2 | OBJ + MTL (.zip) | obj | **custom** text writer; one `o`/`g` per node/mesh, `usemtl` per mesh, MTL Kd/Ks/d/Ns, optional .prj; flat world-space | EXP:1567-1690 `u` |
| 3 | glTF binary (.glb) | glb | **Three.js GLTFExporter** (lazy from `exporters/GLTFExporter.js`) + custom post-pass | EXP:670-1136 (`h`,`f`,`a`,`d`) |
| 4 | glTF separate (.zip) | gltf-separate | GLB above, then unpacked: `model.gltf` (JSON, pretty) + `model.bin` | EXP:4638 `t2` |
| 5 | PLY (.ply) | ply | custom | EXP:3384 `K` |
| 6 | OFF (.off) | off | custom (triangles only) | EXP:4729 `t8` |
| 7 | Point list (.xyz) | xyz | custom (point clouds) | EXP:3481 `Q` |
| 8 | LAS point cloud (.las) | las | custom LAS writer | EXP:4775 `t9` |
| 9 | PCD point cloud (.pcd) | pcd | custom | EXP:3516 `tt` |
| 10 | USDZ for AR (.usdz) | usdz | **custom** USDA text + hand-built zip (64-byte-aligned stored entries); UsdPreviewSurface, textures PNG/JPG | EXP:3670-3790 `tl` |
| 11 | 3MF print (.3mf) | 3mf | custom XML + zip; one `<object>` per instance, base colours | EXP:3801 `ts` |
| 12 | AMF (.amf) | amf | custom XML | EXP:4689 `t5` |
| 13 | VRML (.wrl) | wrl | custom | EXP:3854 `th` |
| 14 | X3D (.x3d) | x3d | custom | EXP:4200 `tA` |
| 15 | COLLADA (.dae) | dae | custom 1.4.1 | EXP:4088 `tx` |
| 16 | FBX (.fbx) | fbx | custom binary FBX 7400 writer (node records, zlib in code) | EXP:4351 `tk`, 4410-4450 |
| 17 | 3DS (.3ds) | 3ds | custom (chunk writer; 8-char names) | EXP:4570 `t1`, 4558 `t0` |
| 18 | GTA V prop (.ydr, beta) | gtav | custom, separate lazy chunk 85685 (+6913) | UI:17074 |
| 19 | Gaussian splats (.spz, .ply)... | gsplat | opens options dialog; splat export code in its own chunk | UI:16883 `ik` |
| 20 | STEP (.step) | step | **custom** AP214 writer: faceted BREP / shell-based surface model, **keeps assembly** via `PRODUCT_DEFINITION` + `NEXT_ASSEMBLY_USAGE_OCCURRENCE` + `ITEM_DEFINED_TRANSFORMATION`; max 3000 parts, units to mm | EXP:1692-2464 `f`, e.g. 1847, 1865, 2330 |
| 21 | ACIS SAT (.sat) | sat | custom | EXP:2617 `U` |
| 22 | IFC for Revit and BIM (.ifc) | ifc | custom IFC4: Project/Site/Building, `IFCBUILDINGELEMENTPROXY` + `IFCTRIANGULATEDFACESET`, deterministic GUIDs from name+counter, SI units | EXP:2782-2950 `L` |
| 23 | DXF 2D drawing of this view (.dxf) | dxf-view | **custom**: projects visible edges from 3D view; DXF via shared `exportDxfDrawing` | UI:17184, chunk 7060 |
| 24 | DWG 2D drawing of this view (.dwg) | dwg-view | same, then **DXF->DWG via a remote conversion service** (`convertDxfToDwg`, chunk 71439; progress "uploading/converting") | UI:17184, 17451 |
| 25 | DXF three-view sheet | dxf-sheet | custom (first/third angle, 5 views, title block) | chunk 7060 |
| 26 | DWG three-view sheet | dwg-sheet | custom + remote DWG | |
| 27 | DXF floor plans, one per level | dxf-plans | custom (`buildFloorPlans`, chunk 29940) | UI:17160 |
| 28 | DWG floor plans | dwg-plans | custom + remote DWG | |
| 29 | DXF 3D faces (.dxf) | dxf | custom (`SG`, polyface <= 32 767 faces, R12 / AC1015) | UI:17110-17160, EXP `B.SG` |
| 30 | DWG 3D faces (.dwg) | dwg | custom + remote DWG | |
| 31 | Rhino v5 (.3dm) (per-version submenu) | 3dm | **WASM**: `rhino3dm` (`File3dm`, layers, mesh via `createFromThreejsJSON`); if source was .3dm, re-writes original bytes with hidden layers removed (`fS`) | EXP:3937-4070 `tg`/`tv`, loader 43275 |
| 32 | HTML (.html) | html | custom stand-alone page: embeds **GLB base64** + `viewer3d.min.js` + CSP hash | EXP:1148-1360, UI:17261 |
| 33 | PDF snapshot / PDF vector / SVG / EPS | pdf, pdf-vector, svg, eps | canvas snapshot into custom PDF (`dG`); vector via projected visible edges (`computeVisibleEdges`, R3D:1836) -> SVG (`ui`) / EPS (`rE`) / jsPDF (`8141`) | EXP:3259 `Z`, 3559 `te`, 4832 `ee` |
| 34 | PNG / JPG / animated GIF | png, jpg, gif | canvas `renderToImage` (transparent bg option), GIF encoder component | UI:16883-16900 |
| 35 | Word document (.docx) | docx | custom OOXML (document pages only) | UI:16890 |

So ~35 items, of which **only the GLB path uses a three.js exporter**; the page also preloads
`STLExporter` and `OBJExporter` (EXP:5353, TH3:833) but the menu uses the custom writers above.
**Wasm** appears in: rhino3dm (export 3DM + read 3DM), occt-import-js/web-ifc/dwg_core/pln_core/
sw_core/manifold/libarchive/laz-perf (**import side only**), and a remote service for DWG.

### B.2 Common export pipeline (applies to every 3D target)

1. `tY(scene, id, preDropScene)` = **emptiness check** against a capability table `tz`
   (EXP:4471-4493): `triangles/lines/points` supported per format. e.g. STL/OFF/USDZ/3MF/AMF/3DS/
   STEP/SAT/IFC = triangles only; FBX/DAE = triangles+lines; OBJ/GLB/glTF/PLY/WRL/X3D/XYZ/LAS =
   all three. If a model has only unsupported topology the export is refused with a typed notice
   (`hidden | flat | points | lines | empty`; UI:17030-17035, messages `export-empty-*`).
2. Hidden layers are dropped (`dropLayers`, P53:1120) and, for non-GLB, the animation pose is
   baked at the current time.
3. For STL/OBJ/PLY/USDZ/3MF/DAE/FBX/...: `u$(scene)` **flattens** the tree into
   `{mesh, matrix, layerName, nodeName}` and `MD()` transforms positions to **world space**
   (and `NX()` normals via the inverse-transpose, sign-fixed for mirrored matrices;
   P53:1015-1110). Consequently all of those formats lose instancing (every instance duplicates
   its vertices), lose node names (except OBJ `o`, STEP/IFC names, 3DM layer) and lose node
   transforms.
4. Size/budget guards: DXF/STEP simplification to 32 767 faces or a part cap (`GP`),
   HTML export has warn/block thresholds by device tier (26/125 MB low, 63/262 MB medium, 126/419
   MB high; EXP:1215-1235), 3DM `maxSegmentsPerMesh` 200 000, STEP `maxParts` 3000.

### B.3 GLB export in detail (the only "rich" 3D target)

(EXP:669-1136; invoked UI:17034 `hr(O)`)

* The Scene is rebuilt as a Three scene: node -> `Group` (or `Bone`), `name` kept, `matrix`
  applied, meshes added as children, **root named after the viewer + format**; each Material ->
  `MeshStandardMaterial` / `MeshPhysicalMaterial` / `MeshBasicMaterial` (unlit) with
  `userData` carrying the source material id so a second pass can attach textures (EXP:~985-1050).
* Options passed to GLTFExporter: `{binary:true, embedImages:true, onlyVisible:false, animations}`;
  there is **no Draco, no meshopt, no quantisation, no EXT_mesh_gpu_instancing** on export. Instancing
  survives only as "several nodes -> one glTF mesh" (mesh id reuse + GLTFExporter caching).
* Custom post-pass `a` (EXP:680-855): re-reads the exported GLB, then (a) writes textures
  with `KHR_texture_transform` (offset/scale/rotation, flipY compensation) and **`EXT_texture_webp`
  when the source image is WebP** (extensionsRequired), PNG/JPEG otherwise (non-supported images
  re-encoded to PNG via OffscreenCanvas; `d` 856-965); (b) converts morph-target accessors to
  **sparse accessors** when they save space (`< 7.92 B/vertex` rule); (c) re-packs buffer views
  with 4-byte alignment into a fresh GLB. Shared skins per (skeleton, bind matrix) plugin
  `ViewerSharedSkins`.
* Material slots exported: baseColor, metallicRoughness (only if one texture or the same for both),
  normal(+scale), occlusion(+strength), emissive; `KHR_materials_*` come from three's exporter for
  `MeshPhysicalMaterial` (clearcoat, transmission, ior, volume...).
* **Not exported**: node `extras`, layers, units, up-axis info, any metadata. A viewer re-opening this GLB (the
  GLB importer re-reads it through the TH3 converter) gets names and hierarchy, not layers.
  (Layers are partially recoverable because export does not remove layer-less nodes, but the
  `layerName` user data on node groups is not written.)

### B.4 What serialises/caches models? (the "meta.json + zip" question)

* `P68:4034-4140` (`a = ["meta.json","means_l.webp","means_u.webp","quats.webp",
  "scales.webp","sh0.webp"]`) is **not** an internal cache: it is the **SOG v2 Gaussian-splat
  reader** (zip with `meta.json` {version:2, count, means{mins,maxs}, scales/sh0 codebooks,
  shN bands} + quantised WebP images; decoded to splats in `read(i,out)`). Same zip-wrapped-JSON
  idea as 3MF/USDZ, but for splats.
* The viewer never serialises its own Scene. What it does persist/send:
  - IndexedDB `…-viewer-handoff` store (UI:9758): the **original uploaded file blob** under a
    random id (24 h TTL) so a "?local=<id>" link reopens it (not the parsed scene).
  - Share/embed link state string (camera, hidden layers, explode `m:..|f:..`, animation
    `clip,t,playing,speed,loop`, edit-ops string `m..;c..;d..;u..;s..`; P53:940-1010, P68:14300+,
    58168).
  - Worker boundary: parsed scenes are passed with `postMessage` + transfer list of typed array
    buffers (STEP `u` at 17666:~899, IFC worker).
  - HTML export = **GLB in base64 inside a `<script type="application/octet-stream"
    id="isv-model">`** next to `viewer3d.min.js`; the page is created with a CSP whose script hash
    is computed with Web Crypto (EXP:1281-1330). So the reference viewer's *own interchange
    format for the 3D web viewer is GLB*.
* `extract/vendor/html-export/viewer3d.min.js` (663 KB) is a bundled three.js + OrbitControls +
  GLTFLoader viewer: it contains `KHR_draco_mesh_compression`, `KHR_meshopt_compression`/
  `EXT_meshopt_compression`, `KHR_mesh_quantization`, `EXT_mesh_gpu_instancing`,
  `KHR_materials_{unlit,clearcoat,transmission,ior,volume,sheen,specular,iridescence,anisotropy,
  dispersion,emissive_strength}`, `EXT_materials_bump`, `KHR_texture_basisu/transform`,
  `EXT_texture_webp/avif`, `KHR_lights_punctual`, and exposes `userData.gltfExtensions` and
  `userData.name`. It has two UI buttons (fit, grid) and reads layers only for the 2D variant.

### B.5 Per-format options exposed (very few)

| Format | Options |
|---|---|
| 3DM | version submenu (v2..v8; label/default from `DD("rhino").defaultWrite`, UI:17225-17260) |
| DXF/DWG 3D | `dxfVersion` R12 vs AC1015; polyface cap 32 767; `images` sidecar zip for embedded images; trace-images-to-outline option (`traceDrawingImages`, chunk 50424) |
| OBJ | `basename`, `localCoordinates` (geo-referenced models: world coordinates plus `.prj`; `hl`/`Vr` frame) |
| STL | header string |
| STEP | `name`, `maxParts` (3000), units mm |
| IFC | `name`, `timestamp` |
| HTML | title, labels (i18n), `lang`, `sizeGuard` callback |
| PNG/JPG/GIF | transparent background, size (max 16384), GIF frame options dialog |
| GLB | none (animations baked at the current pose for every non-GLB format; GLB keeps clips/skins/morph weights) |

---------------------------------------------------------------------------------------------

## B.6 Recommendation: single best interchange target

**Recommend glTF 2.0 binary (.glb)** as the one format X-Ray.3D converts everything to, with
this profile:

1. Node hierarchy = glTF nodes (names, TRS or matrix) - the same Node tree as above.
2. Meshes: one glTF mesh per unique geometry; instances as multiple nodes referencing the same
   mesh index (native, preserves per-instance names and extras). Add
   **EXT_mesh_gpu_instancing** only as an opt-in for very large identical-part counts (it replaces
   the per-instance nodes, so per-instance names/extras would have to move into an extras table).
3. Compression: **EXT_meshopt_compression + KHR_mesh_quantization** as the default. Meshopt's
   codec is small and straightforward to port to pure C# (vertex/index/filters), decodes
   synchronously in three.js' `MeshoptDecoder` (the reference viewer already loads it, GLB:~800),
   and gives roughly 4-8x over raw. Draco compresses geometry harder but its encoder
   (edgebreaker, predictive coding) is a large native C++ code base, not realistic to port, and
   the decode is async wasm. Offer Draco only if a native encoder is ever bundled separately
   (consistent with the family rule: native code goes in an optional package).
4. Materials: metallic-roughness PBR + `KHR_materials_{emissive_strength,clearcoat,transmission,
   ior,volume,unlit,specular}` and `KHR_texture_transform`; textures PNG/JPEG (WebP via
   `EXT_texture_webp` as the reference viewer does; KTX2/BasisU needs a native encoder).
5. Per-node metadata: write `node.extras` (three.js `GLTFLoader` exposes it as
   `object.userData`): `{ "xr": { id, srcId/path, layer, props: {...} } }` kept small; large
   property sets (IFC psets, STEP attributes) in a sidecar JSON keyed by node index or id,
   referenced from `scene.extras.xr.propertiesUri`, so the GLB stays light and properties
   load lazily on selection.
6. Model-level metadata in `asset.extras` / `scene.extras`: `units` (original), `scaleToMeters`
   baked, `upAxis` original, `format`, `layers[]` (id,name,visible,color,count), `bounds`,
   triangle/vertex/instance stats, `warnings`. Layers have no native glTF concept
   (`KHR_node_visibility` is the standard for visibility; three's loader may not honour it, so also
   emit `extras.layer` and make the viewer apply it).
7. Normalise on write: Y-up right-handed (put the correction on a root node, as the
   reference viewer does), **metres** (glTF's unit; the reference viewer's GLB export skips this,
   a deficiency), triangles/lines/points as modes 4/1/0, colours linear.
8. Lazy assemblies: for huge assemblies, emit a "scene GLB" with the tree plus per-part
   `.glb`/`.bin` and reference them via `node.extras.xr.uri` (same shape as the "glTF
   separate" export, EXP:4638); simple to keep optional.

Why not the others (matching what the reference viewer's own writers show):

| Candidate | Verdict |
|---|---|
| **3MF** | Good for print (units, components = instancing, base colours, metadata namespace), but no PBR/textures beyond the materials extension, no compression beyond zip deflate, no animation; three's `3MFLoader` is limited; the reference viewer's 3MF writer is flat per-instance. Keep as a secondary *export*, not the interchange. |
| **OBJ** (+MTL zip) | No hierarchy beyond `o/g`, no PBR (Kd/Ks/Ns only), no instancing, no units, no metadata, text size. The reference viewer exports it flat in world space. Secondary export only. |
| **STL** | Geometry only (no colour/names/units). Print/CAM export only. |
| **USDZ** | Real scene graph and PBR (UsdPreviewSurface), but uncompressed USDA/USDC zip, weak three.js support (examples `usd` loader is partial), heavy for web. Good as an Apple AR export target (the reference viewer writes it flat with `metersPerUnit`). |
| **The reference viewer's internal Scene** | Not serialisable in any form it ships (never persisted; only the share-link edit string + original blob); but its *shape* is an excellent in-memory model for C#, and its own HTML export proves the best on-the-wire encoding for the web is GLB. |
| **glTF + .bin + textures** | Same content as GLB, worse for delivery; offer as a variant (`gltf-separate`). |

Net: Scene model in C# mirroring section 0 (plus properties/extras that the reference viewer lacks),
one writer to GLB (with meshopt/quantization, KHR_materials_*, node extras), secondary writers
for OBJ/STL/3MF/USDZ/PLY that reuse the same *flatten-to-world* utility (`u$`/`MD`/`NX`
equivalents).

---------------------------------------------------------------------------------------------

## C. How the viewer builds its scene tree

### C.1 Pipeline (P68:16213-16396)

`parse(file)` (P68:16213 `x`) -> `S(file, opts)` (P68:16396) which:

1. detects the format (`qZ`) and checks `Bg[format].implemented`;
2. enforces size limits (`P(format, bytes)` per device tier, ignored for large-file-friendly
   formats: zip/skp/max/wrl/3dm/psd/rage);
3. `switch(format)` dynamic-imports the parser chunk (module-federation style `n.e(...)`),
   formats = glb, stl, obj, skp, step, iges, ies/ldt, jt, ... ~100 cases (P68:16419-17590);
   companions (`opts.companions`: a Map of sibling files the user dropped) are passed to
   parsers that need external files;
4. post-parse **normalisation** on the returned Scene:
   a. `Part N` layers from top-level children when no layers exist (P68:16227-16270);
   b. non-finite `bounds` recomputed with `nK`;
   c. far-coordinate recentering (P68:16290-16385) unless `_keepFarCoordinates`/drawing/rigged;
   d. archives/zip: `Bd(filename)` handles `.zip` inner names and "partial download" notice.

### C.2 Three-based loaders share a converter (TH3 module 97397, 78076:216-830)

Used by GLB, glTF, FBX, DAE, USD, VRML/X3D, PLY (`n(97397)` imports in 52242, 35078, 15601,
72408, 97567, 32297, 62195). `o(THREE, loadedRoot, scene, {animation, animations, imageBytes})`:

* **Node naming**: `{ id: object.uuid, name: object.name || undefined, visible: visible!==false }`
  (TH3:326-333). Unnamed nodes stay unnamed (explode later invents `Part N`); nothing else from
  `userData` survives.
* **Transform**: `updateMatrix()` and store `matrix.elements` only if not identity.
* **Meshes**: every `Mesh` yields `meshes.push({id:"mesh-"+counter, ...})` with copied typed
  arrays (`getX/Y/Z` loops, TH3:~345-420): **no geometry dedupe across instances here** - a glTF
  with 1000 nodes sharing one mesh turns into 1000 mesh copies in the Scene (the GLB exporter
  and post-parse dedupe only act on `positions` identity, so this path loses instancing). Indices
  forced to Uint32 (`h`), unindexed geometry gets a sequential index; non-finite vertices are
  dropped with their triangles (`a`,`s`), groups preserved (start/3, count/3) with material per
  group; `SkinnedMesh` -> skinIndices/skinWeights; morph targets stored as absolute deltas;
  vertex colours -> RGBA Float32, `colorSpace:"linear"`.
* **Lines/Points** -> `topology:"lines"|"points"` meshes (line strips expanded to segment pairs).
* **Materials** (`m`, TH3:~690-760): cached by Three material object -> `mat-N`; `MeshBasicMaterial`
  -> `unlit`; colour copied as linear RGBA (opacity into alpha); metalness/roughness/opacity;
  `alphaTest>0` -> MASK/cutoff, `transparent && depthWrite===false` -> BLEND; maps -> texture ids
  (`f`, 770-810, cached per image, colour vs linear texture maps separately, bytes via
  `imageBytes` callback else re-drawn to a PNG data-URL). Default material (`Default`,
  doubleSided, `[.78,.78,.82]`, roughness .75) when none.
* **Bounds** accumulated through world-space AABB of finite vertices.

### C.3 Format-specific trees (naming / merging / instancing)

| Format | Tree shape | Naming | Instancing / merging |
|---|---|---|---|
| STL (67931) | `root(name "stl") -> [mesh-0]` | none | single mesh, vertices welded, crease normals (40 degrees) when <= 4 M tris; optional Z-up root matrix |
| OBJ (35144) | `root("obj") -> one child per OBJ group/object` | child.name = group/object name from `g`/`o`; fallback in parser; mesh id `mesh-<n>` | **one Mesh per group x material (+ topology)**; MTL parsed into materials named by MTL name; colours per vertex create a `vertexColors` clone of the material; texture bytes from companions (MTL `map_Kd`), `metadata.missingMaterials` for absent files; lean mode >= 256 MB |
| STEP (17666) | recursive from the tessellator's product tree: id `root`, `root/0`, `parent/idx`... | `name = productName` (or undefined); fallback children `part-<i>` named by mesh name if the tree is flat | meshes referenced by index; BREP **face groups** (`brepFaces` first/last triangle) give per-face colour groups; materials deduped by RGB(3 d.p.); a faceted-stream fast path (`g`, `facetStream`) collapses huge files to one mesh |
| IFC (93149) | `root(transform = scaleToMeters) -> element nodes`; element with several placed geometries gets child nodes `el-<id>-<k>` | `"<IfcType minus trailing s> #<expressID>"`; **layer = IFC class name** (`type-<n>`, count) | web-ifc streams `geoms` (unique) + `instances` (placement matrices): `unique geometry -> one Mesh`, each placement is a node transform: **true instancing**; mesh cloned per colour (`f`: `<id>-m<n>`) because colour is per mesh; `elevationOffset` fix; metadata `productCount/instanceCount/uniqueGeometryCount` |
| 3DM (56536) | `root -> layer nodes ("layer-node-<i>", name=full layer path) -> meshes`; unplaced -> orphan node | layer name = `fullPath` (Parent::Child), meshes `mesh-<seq>` | Rhino **block/instance definitions are expanded** (recursive with cycle guard `U` depth, triangle budget) into baked copies per instance (`W(tris, xform, ..)`), linked blocks reported as missing; `metadata.blockInstances`, per-layer materials, curve objects as `lines` meshes |
| GLB/glTF (52242, 35078) | as three scene (C.2) after GLTFLoader (+DRACOLoader, MeshoptDecoder); Odin-variant GLB rewritten to standard GLB first; glTF 1.0 converted; external images from companions via URLModifier | node names from glTF; `metadata.generator/version` | glTF mesh sharing is *expanded* by the converter (see C.2) |
| FBX/DAE/USD/VRML/3DS/PLY | three loader -> C.2 converter | names from files | same; PLY gives point/vertex-colour meshes; `up axis` read from header |
| 3MF (73456) | three `3MFLoader`, with a pre-flatten of multi-file "production extension" archives and slicer metadata (`Slic3r_PE`/`project_settings`); units from `<model unit>` | object names | components/instances flattened by the loader |
| DXF/DWG/SVG/PDF... | `drawing{entities}` + synthetic scene | layer names | 2D branch (no meshes) |
| SOG/PLY splats (68243:4034+) | `topology:"points", splat` meshes | - | per-splat decoder, `axes:"y-down"` |

### C.4 Lazy loading of assembly components

* **There is no lazy loading of components.** The whole model is parsed eagerly inside a worker
  (STEP/IFC: dedicated Web Worker with transfer of buffers; fallback to main thread if `Worker`
  is missing) and handed to the renderer as one Scene. What *is* lazy:
  - the parser code per format (dynamic chunk import, P68:16419+), WASM engines (rhino3dm,
    web-ifc, occt-import-js, dwg_core, ...), and the Three.js examples modules
    (`exporters/GLTFExporter.js`, `loaders/DRACOLoader.js`, `libs/meshopt_decoder.module.js`,
    `3MFLoader.js` - fetched by URL and rewritten to blob imports, `66209`/`EXP:5281-5370`);
  - rendering batches: >=1500 meshes (`s=1500` in R3D:57) are merged into `BatchedMesh`/merged
    geometry buckets keyed by material uuid + attribute signature; <=128-vertex meshes merged
    outright with a 16 M vertex cap, line segments merged per material (R3D:124-330). This is
    a draw-call optimisation only and never changes the Scene;
  - splat renderer chunk (`30928`) only when a splat mesh is present (R3D:1078-1090);
  - **missing external references** are modelled instead of loaded lazily: loaders put lists in
    `metadata.missingParts`/`missingMaterials`/`missingTextures`/`missingLevelFiles`/
    `missingDatasmithFiles`/`missingRenderwareTextures`, the UI asks for the companion files
    (`want` descriptors from module 75228: `Ey`, `Y_`, `NO`, `bW`, `yF`, name match
    ignoring `.prt.N`/`.asm.N` version suffixes) and **re-parses the whole model** with a
    larger `companions` Map (UI:15725-15780). Archives (`ui-archive-*`) let the user pick one of
    several models inside a zip.
* Consequence for X-Ray.3D: external-reference resolution (assembly -> part files, MTL ->
  textures, glTF -> .bin) should be an explicit `IResourceResolver` passed to each reader; the
  reference viewer's "companions" Map is exactly that abstraction.

---------------------------------------------------------------------------------------------

## D. Quick design takeaways for X-Ray.3D

1. Copy the Scene hub: `Scene{Units,UpAxis,Bounds,Root,Meshes,Materials,Textures,Layers,Metadata}`
   with **mesh pool + node `MeshIds` for instancing**, but add `Node.Properties` and keep the
   original source ids/paths (the viewer drops all of that).
2. Keep transforms on nodes; normalise Y-up/units by writing a root matrix and recording
   `Scene.SourceUnits/UpAxis` (the viewer's approach), bake only for flat formats.
3. Treat layers as a flat list plus optional path; synthesise "Part N" layers for assemblies the
   same way (<=300 groups) so "layer filter" works for formats without layers.
4. Provide `Flatten(scene)` -> `(mesh, worldMatrix, layer, nodeName)` (+ world-space
   positions/normals with mirrored-matrix handling) and implement STL/OBJ/PLY/3MF/USDZ/DXF/etc.
   on top of it.
5. Preserve instancing in the importers that have it (IFC, STEP products, glTF, 3DM blocks) -
   the viewer loses it at the three.js converter and at 3DM expansion.
6. Provide capability table per export format (`triangles/lines/points`) and typed "nothing to
   export" reasons (`tz`/`tY`, EXP:4471-4530).
7. Metrics to add: vertex count, unique vs instanced triangles, area/volume, per-node AABB.
8. Target: **GLB + EXT_meshopt_compression + KHR_mesh_quantization + KHR_materials_* + node
   `extras` + sidecar properties JSON**.
