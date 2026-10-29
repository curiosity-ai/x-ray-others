# SolidWorks 2015+ documents as this library reads them

Everything here was derived by decompiling the reference viewer's WebAssembly reader, inspecting the streams
it extracts, and checking each claim against that reader's output on the dataset (`tools/oracle/make_golden.mjs`).
The code is `Formats/SolidWorks/`. Nothing below is an official description of the format.

## Container

A document is a run of ZIP-like entries:

```
sig(4)  version=0x14(2)  flags=6(2)  method=8(2)  time(2) date(2)  crc32(4)  compressedSize(4)  size(4)
nameLength(2)  extraLength(2)  name(nameLength)  extra  data(compressedSize)
```

* The signature differs from file to file (it is not `PK\3\4`); the `14 00 06 00 08 00` after it does not.
* The name has the two nibbles of every byte swapped (`0x34 0xF6` reads as `0x43 0x6F`, "Co").
* `data` is raw deflate. `crc32` is over the inflated bytes.
* The start of the file holds **decoy entries**: well-formed headers with plausible names (`PreviewPNG`,
  `Contents/Config-0-ResolvedFeatures`) whose sizes overrun the next header. And the end holds a
  directory-like tail with other signatures. The reader accepts an entry only when its data inflates to
  exactly `size` bytes and matches `crc32`, and its name is printable.
* Older files (SolidWorks 2014 and before) use an OLE compound file with `__ZLB`-suffixed zlib streams.
  Not implemented (see TODO.md).

## Streams that matter

| Stream | Content |
|---|---|
| `docProps/core.xml` | Dublin Core: last modified by, created, modified. One group **per save, newest first** |
| `docProps/app.xml` | Application, company, `AppVersion` (release `n` is SolidWorks `1992 + n`, so 23 is 2015) |
| `docProps/custom.xml` | Custom properties (part number, description...) as `propertySection`/`property` |
| `docProps/ISolidWorksInformation.xml` | `SW-*` document properties and `SW_Units*` settings |
| `swXmlContents/KeyWords` | Document name, configurations (with `Material` attribute), feature tree entries. May start with a stray byte before the XML |
| `swXmlContents/COMPINSTANCETREE` | `swFile` (referenced files; the first is the document itself, paths are Windows paths) and `swModel` (name, configuration, file reference, bounding box) |
| `swXmlContents/CustomProperties` | Custom properties of drawings |
| `SheetPreviews/SheetNames` | Drawing sheet names: serialised strings (below) |
| `PreviewPNG`, `Preview` | Thumbnail (PNG; a DIB for `Preview`) |
| `Contents/DisplayLists` | The tessellation SolidWorks draws, in **meters** |

Serialised strings in the object streams are MFC-style: `FF FE FF`, a length byte, UTF-16 characters.
The model's own coordinates are meters and the up axis is Y.

## DisplayLists

The stream is a serialised object graph. The reader does not walk it; two record shapes are recognised.

**Face** (a run of triangle strips):

```
u32 4, 8, 2, n         then n u32 strip lengths
u32 12, 100, 2, k      then k x (f32 x, y, z)        positions
u32 12, 100, 2, k      then k x (f32 nx, ny, nz)     normals
```

with the strip lengths summing to `k`. (Two more `4, 8, 2, m` arrays and a few per-face bounding-box doubles follow;
they are not needed for rendering.) A strip of length `L` gives `L - 2` triangles; triangle `i` is
`(i, i+1, i+2)` for even `i` and `(i+1, i, i+2)` for odd `i`, offset by the strip's first vertex. Vertices are
not shared between faces.

**Component instance** (assemblies only): a serialised string containing `@` (`Pin-1@Gear`, instance 1 of part
Pin in assembly Gear) followed by a transform:

```
u8 type
type 0:  3 x f64 translation, f64 scale                      (identity rotation)
type 1:  9 x f64 rotation (row-major), 3 x f64 translation, f64 scale
```

Valid when every rotation entry is within +/-1.000001, the scale is within (1e-6, 1e6) and the translation
within +/-1e6. The placement is `world = x * R * scale + T` with `x` a row vector, which is exactly
`System.Numerics.Matrix4x4`'s convention (rows of R in `M11..M33`, T in `M41..M43`). Checked with a non-symmetric rotation:
the transposed form is wrong.

**Which faces belong to which instance.** Faces that follow an instance record up to the next one belong to it. An
instance followed by no faces repeats the geometry of the last instance that has some (the other three screws of
four). So a part's geometry is stored once, in the frame of the first instance's placement, and every instance
including the first is placed by its own transform. Annotation-region strings with `@` before the first
face are not components and produce no instance. Faces before any instance record are kept as an unnamed root.

The sample assembly decodes to 1131 stored faces / 110,035 vertices in 8 meshes, drawn as 36 instances: 617,027 vertices,
436,251 triangles, identical to the reference reader.

## Not yet decoded

* **Per-face colours.** The reference reader reports a colour per face (`0xRRGGBB`, or none). The material
  records are in the same stream (`satin finish`, `stainless steel`, a type byte, three RGB bytes, then float
  material coefficients), but the face-to-material link is not understood yet. Meshes are written without colours.
* **2024+ saves** keep the mesh in a `FaceTessellations/` store instead of `DisplayLists`.
* **The compound-file envelope** (SolidWorks 2014 and before).
* Drawings: only metadata and sheet names. The sheet image (`Images/Sheet_0`) is not extracted yet.
