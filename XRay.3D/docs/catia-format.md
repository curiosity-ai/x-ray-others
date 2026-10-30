# CATIA V5 documents (`.CATPart`, `.CATProduct`, `.CATDrawing`, `.CATShape`, `.cgr`)

What the reader in `src/XRay.ThreeD/Formats/Catia/` does and how it knows. Everything here was checked on real files; where it was not, it says so.

## Sources

| Source | Licence | Used for |
|---|---|---|
| cadmpeg, `docs/formats/catia.md` and the `cadmpeg-codec-catia` crate documentation (github.com/cadmpeg/cadmpeg) | text CC-BY-4.0, code Apache-2.0 | the container (`V5_CFV2` header, `CATIA_V5 CB0001` directory, extents, the nested container), the `FINJPL` segments, the colour row (`A B G R`), the note that the trim packets are indexed triangle lists and that the coordinate table has a control-bit form. **No code was ported**; the reader is written from the descriptions and from the sample files. See `THIRD_PARTY_NOTICES.md`. |
| 75 real files (below) | various; see "Samples" | everything else: the metadata streams, the link records, the catalog, the table header variants, the packet variants, the validation |
| Public searches (the vendor's own file-format pages, forum and converter-vendor pages, GitHub code search for `.cgr` and `.CATPart`) | | found no specification and no other open reader. A `.cgr` file was not found anywhere public, so the standalone graphical-representation file is **unverified** (see "Not done"). |

CATIA V4 `.model` files were not attempted: no public description and no sample was found.

## Container

All integers are big-endian in the container, little-endian in the records inside the streams.

```
0x00  "V5_CFV2\0"
0x08  u32 directory offset          (outer: from the start of the file)
0x0c  u32 directory length          (offset + length = file length)
0x10  ff * 8, 00 * 32, 8 flag bytes (header is 64 bytes; stream bytes follow)
```

The directory starts with `CATIA_V5 CB0001\0` and ends with `CB__END`. A stream descriptor is found by scanning for its extent list: at region offset `i` a u32 count `k` (1 to 64) and `k` extents of 20 bytes
(`physical offset`, `physical length`, `logical length`, `logical offset`, a trailing word); the descriptor header starts at `i - 0x50` and holds the total logical length at +0x0c. A candidate is accepted
only when every extent is inside the file, `logical offset` is the running sum and `logical length = physical length`. The stream name is the UTF-16 run that ends three bytes before the descriptor; it is
preceded by a u16 length (`2 * (characters + 1)`) whose byte can look printable, so such a first character is dropped. A stream is the concatenation of its extents; there is no compression and no cipher.
Several descriptors can share a name (the nested container has three `MainDataStream` entries; only the large one holds geometry).

The **nested container** is a second `V5_CFV2` inside one outer stream (the one declared `CATCGRCont`, the graphical-representation cache). Its header is the same, but the offset at 0x08 is a delta from its own magic
and the physical offsets of its extents are relative to that magic. Its streams are `MAIN`, `Describe`, `Header`, `SceneGraph`, `MainDataStream` and `SurfacicReps`.

Outer streams that matter:

| Stream | Content |
|---|---|
| `CATPropertySets` | typed properties: a u16 name length, the name, a u32 type and a payload; type 0x0e is a string (u32 length, bytes). Found in every file: `LastSaveVersion` (`<Version>5/<Version><Release>21/<Release><ServicePack>0/<ServicePack><BuildDate>04-14-2011.20.00/<BuildDate><HotFix>0/<HotFix>`; the closing tag is the opening tag preceded by `/`), `MinimalVersionToRead` (`CATIAV5R21`), `CATBuildLevel`, `CATPDMPersistence`, `FirstStreamed`. |
| `CATPreview` | four zero bytes and a JPEG (130 x 110 in every real file). |
| `CATDocumentProperty` | `CATOctetArray` values: `0x22`, a u32 count, then every byte as `3b 01 byte`. Decoded, a link record is `04 "File"`, a type (u8 length and ASCII: `CATPart`, `CATProduct`, `STL`, `feat`...), a u16 length and a UTF-8 path. The **first** record is the document's own path when it was saved; the later ones that name a part, product, shape or mesh are the documents it refers to. |
| `CATStorageProperty`, `Data` | the class names of the containers (`CATPrtCont` part, `CATMechProdCont` product, `CATDrwCont` drawing, `CATCGRCont` cache...). A part has `CATPrtCont`; a product `CATMechProdCont` and no `CATPrtCont`; a drawing `CATDrwCont`. |
| `...Cont_...` and `Data` (products) | an identifier catalog: length-prefixed strings where the length byte counts itself. `ASMPRODUCT` is followed by the product name (the part number), `MechanicalPart` by the part's internal name (`Part5`), the names between `_Reps` and `CONNECTOR` are the component and instance names the catalog meets first (a product's list is not complete: a name already seen is not repeated), and `_Nomenclature`, `_Source`... are followed by their value when they have one. This is a heuristic over a string table, not the object graph. |
| `TimeStamp`, `Project`, `GesToler`, `Indexe` | not decoded (`TimeStamp` is seven bytes that did not decode to a date). No author, title or company was present in any of the 75 files; a `CATSummaryInformation` property with such names would be reported as written but this is untested. |

## The display tessellation (what is converted)

A part stores, in `MainDataStream` of the nested container, the triangulation CATIA draws with. The same bytes are what cadmpeg describes as the "trim records" of its standard layout. Found by structure, never by position:

1. **Coordinate table.** The marker `ff ff 02 00 01`, a count `n` and a second count (a byte, or `ff` and a u32), four flag bytes, then `ceil(n/4)` control bytes, a scalar count `s` (same form) and `s` float32 scalars.
   Each control byte holds four 2-bit codes, lowest first: `0` read x y z, `1` repeat the row above, `2` read z only, `3` read y and z (the other coordinates repeat). The scalars are consumed exactly:
   the sum of the codes' needs equals `s` (the check that tells a coordinate table from the other tables that begin with the same marker: 86- and 84-entry tables of something else sit in front of it in most files).
   When the fourth flag byte is not zero (`01 00 01 02`: three of the 75 files, all from one author) other arrays sit between the header and the control bits; the table is then found by the same consistency check.
2. **Face packets.** After the table, per face: `01 kind`, then (by the low bits of `kind`: 1, 2, 4) the number of independent triangles `A`, strips `B` and fans `C` (each a byte, or `ff` and a u32), `ff` and a u32 `N`, a 12-byte face normal when bit 3 is set, one length per strip and fan, then `N` handles (A triples, the strips, the fans).
   `N = 3A + sum(lengths)`. Handles are big-endian and **one to three bytes wide**: the smallest width that holds `n - 1` (one byte up to 256 vertices, two up to 65536), which fits all 75 files. The lengths are one count each, or for two-byte handles sometimes one u16 each (a packet that does not parse the first way is tried the second).
   Strips alternate winding, fans pivot on the first handle. A handle at or above `n` ends the candidate. Packets are not always back to back (in a few files each is followed by its colour row and a short record), so every offset after the table is tried and the valid packets kept in order.
3. **Face colours.** `?? 04 04 ff` and the bytes A, B, G, R (alpha first). The rows either follow the last packet back to back or follow each packet; a colour per face is used only when the rows account for every face. (`ff ff d2 d2` is CATIA's default grey-blue, 210/210/255; `ff 00 ff ff` is yellow; a plexiglass plate has alpha `0x91`.)
4. **Orientation.** The strip parity draws some closed solids inside out; the mesh is flipped when its enclosed volume is negative.
5. A table is accepted as a tessellation only when the packets use at least three quarters of its vertices.

A part can hold several tables (bodies, sheets); each becomes a body (a node `Body n`). Coordinates are millimetres in the part's own axes, Z up, as stored; nothing is scaled.
Faces keep their own vertex copies so curved faces are smooth and edges between faces stay sharp; `MeshData.Faces` holds each CATIA face's index range.

## Products: instances and placements

A product's `Data` stream holds the structure; it is read by `Formats/Catia/CatiaProduct.cs`. Checked on the 12 products of one public repository (all components present), on the products of two more, and on the committed ones.

* **Token table.** The stream opens with a directory, then the catalog (`CATCatalogManager`, `catalogLinks`, `ASMPRODUCT`, ...): length-prefixed strings (the length counts its own byte; UTF-8 allowed), `01`/`21` separators, and `00` + u32 length blocks (constraint descriptions) that take a token slot and no name. Every name used later is a token: catalog index + `0x35` as one byte, or `32` + u32 (the wide form; its numbering drifts from the catalog in large products, so a name in this form is not trusted).
* **Objects.** `7c 06 len` header: flags, two class tokens, `ea`, a u32 object index, references (`80` + u32), and `7c 07 len` that opens the body; the body is a field list (field token, value, `fe`).
* **Instance** = object of class (`ASMPRODUCT`, reference name) whose body has an `_InstanceName` field; the name is the token before the field's `fe`. The reference name is the one the reference had when it was instanced (it can be stale: a renamed part matches only through the file's own part number or a later instance name).
* **Placement.** The `3DIC` object (class `PRDBAGREP`, `3DIC`) that follows the instance carries a `_Position` field: `8d 81` and twelve numbers in a compact form: `e6` + 8 bytes (little-endian double), `e7` = 1, `e8` = 0, `e9` = -1. The first nine are the three axis vectors (images of x, y, z), the last three the origin, so a row-vector matrix (`world = local * placement`, System.Numerics) takes them as stored. An instance with no `_Position` is at the origin of its product (CATIA stores nothing for an identity that was never moved). Rotations come out orthonormal with determinant +1 in all 400+ placements checked.
* **Row or column?** Settled with the piston-cylinder assembly (the only asymmetric rotation among assemblies whose parts mate): read as rows, the connecting rod, cylinder and piston touch (minimum vertex distance 0.6 and 0.9 mm); read as columns the rod and cylinder are 45 mm apart. A gearbox of meshing gears was consistent (0 interpenetration as rows, a little as columns) but not decisive. The order also matches the CATIA automation `GetComponents` description and the 3DXML row-vector convention. `CatiaProductTests.The_stored_axes_are_rows_so_that_mating_parts_touch` keeps it.
* **Component files.** An instance finds its file among the product's links (`ComponentFiles`, found through `LoadOptions.ResolveCompanion` by bare name) by, in order: the file's own part number (the name in its catalog, read from the file) equal to the reference name, then to the instance name; the file name without extension equal to either; instances of one reference share the first file found for it. Otherwise a warning names the instance. Nested products load recursively (five levels), each distinct file once, shared by every instance.
* **Not found / not decoded.** Positions the constraint solver would recompute are not needed: the stored result is read. Instances whose class uses the wide token form can fail to resolve; the constraint records (`CtorToProd:...!Geometry:<hex>`) that tie faces together are not decoded.

## Not done

* The **exact B-rep** (surfaces, curves, trims, in the same stream and in the object streams) is not decoded. A part saved without the cache would hold none of the above; none of the 65 parts here was.
* A `.CATProduct` loads when its component files are supplied (see "Products"); without them `Load` says so and `ComponentFiles` lists what is needed.
* The **feature tree, parameters, mass properties, layers and materials** are names in the catalog; their values are not read.
* **Drawings** (`.CATDrawing`) have a preview and metadata only.
* **`.cgr`**: the graphical-representation file is believed to be the same container with the tessellation at the top level (the part's cache is a nested container of that kind), and the reader looks for a `MainDataStream` in any container, but no `.cgr` file could be obtained, so this is **untested**.
* **`.CATShape`**: read like a part; no file was available.
* CATIA V4 `.model`: not attempted.

## The exact B-rep (what was learned, nothing decoded)

Looked at on `HTBX_EntretoiseTendeurs.CATPart` (a 22 mm x 70 mm spacer; its twin STL is in the oracle list below). The part's object containers (`Cont_<id>_<n>`, 1 to 40 KB each) hold, besides the feature tree, raw little-endian doubles for the geometry: in the `CGMGeom` container (16 KB here) a record carries `35.0`, `1.0, 1.0`, `11.0` and `70.0` as plain 8-byte doubles (a cylinder of radius 11 and length 70 with its frame and an angular range as `-35 .. 35`); the feature container (`CATFeatCont`, 20 KB) repeats the same values in the compact form of the product stream (`e6` + double), as the sketch and pad parameters. A record is introduced by class and object tokens in the same `7c`-block grammar as the product stream, with a different catalog. No class table, no face/edge topology and no surface-to-face mapping was identified, so analytic faces (planes, cylinders) are **not** decoded: the tessellation remains the only geometry. Next step if someone continues: take a part made of one primitive (the spacer), list its `CGMGeom` records by class token, and match each record's doubles to the radius, length and frame above.

## External oracles (twins exported from the same model)

Searching the public repositories that hold CATIA files (the six already used; GitHub code search for `.CATPart`, `.CATProduct` finds no others than one more product, `Robbbo-T_OLD`) found five parts with a twin exported from the same model, compared in `CatiaProductTests.Part_tessellations_match_their_exports_from_the_same_model` (numbers in `Corpus/catia-oracle-golden.json`; the files are not redistributable):

| CATPart | twin | result |
|---|---|---|
| `HTBX_EntretoiseTendeurs` | CATIA STL export | 3240 triangles on both sides, bounds identical, volume 14073.547 against 14073.542 mm3, area equal |
| `bolloon holder` | STL | 240 triangles, bounds identical, volume 23482.879 against 23482.878 |
| `Hitbox_ControlBoardCase` | CATIA STL export (finer: 4296 triangles against 3036) | bounds identical to 0.001 mm, volume -0.7 %, area -2.5 % (a coarser tessellation of curved faces inscribes) |
| `Hitbox_PowerBoardCase` | CATIA STL export (2852 against 1396) | bounds identical, volume -0.9 %, area -2.9 % |
| `Telemetry CUAV Air Link 4G` | STEP, tessellated by this package | bounds identical, volume +0.06 %, area +0.01 % |

So the tessellation is CATIA's own display mesh, decoded without loss: for the first two the triangle counts, bounds and volumes match to rounding. Not a pair: `nxholder/Part2.CATPart` and `nxholder_Part2-rk3588lbc4.STEP/STL` (a different revision; heights differ). Not usable for placements: `HitBoxV0.31-Powaa.step` is an EDA export of the power board, not of `POWER_BOARD.CATProduct`; the URDF meshes of `SO_SPIDAR300` are not tied to `Antdroid.CATProduct` by any file in the repository. No 3DXML, OBJ or 3MF twin was found. Instance placements therefore rest on the physical checks in "Products".

## Other formats searched again

`.cgr`: GitHub code search finds 706 files, all unrelated (graph files of a geometry library, a build log); web searches found no downloadable sample. `.CATShape`: no GitHub result, no sample on the web. CATIA V4 `.model`: nothing found. The reader's assumptions for `.cgr` (top-level `MainDataStream`) and `.CATShape` (same as a part) stay unverified.

## Samples and validation

75 files, R18 (2007) to R32 SP1 (2022), 65 parts, 9 products, 1 drawing. They came from public GitHub repositories (found through GitHub code search on the extensions) and are kept only under `/tmp/claude-0/samples/catia/` (not committed), except the seven MIT-licensed ones that are in `dataset/catia/` with their licence in `dataset/manifest.json`:
Adrien Husson's HTBX_01Ressources (MIT, 2020: a game-controller model; R21), Dictor's HaneulMyo (MIT, 2019: a robot hand; R24). The others (Motor-V6, SO_SPIDAR300, holydrone, t8data and more) state no licence and are not redistributed.

Five parts have an exported twin (see "External oracles"); for the rest the checks that stand in for an oracle are:

* **Closed, consistently oriented surfaces.** For every tessellation of the 65 parts, welding vertices by position, every directed edge has its reverse (0 open edges) except three files: a sheet body in `Radiator` (open by construction) and two files with 6 and 8 unmatched edges. A misread handle width, strip parity or table would not close.
* **Every handle is below the vertex count and the faces use every vertex**, in all 65 (for the flagged-layout files too).
* **A part and its mirror image** (`Femur_lateral` is the table type of the flagged layout; `Symmetry_of_Femur_lateral` is its mirror in the usual layout) enclose the same volume (5828.5 mm3) and have mirrored bounds, although they are decoded by different paths.
* **Plausible sizes and names**: `Weight` is a disc of 320 mm diameter and 30 mm thickness with holes (1.96e6 mm3); `Case_Logo_Yellow` is yellow; the two gears `Spur_Gear15` and `Spur_Gear21` (same model, saved twice) give identical counts and volume.
* The golden file (`tests/.../Corpus/catia-golden.json`) records names, counts and bounds of what the reader returns for all 75 files; it catches regressions, it does not prove the numbers.
