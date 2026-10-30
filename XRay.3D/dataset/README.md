# Dataset

Test files for X-Ray.3D, committed so tests never depend on a download. `manifest.json` lists each file with its
format, title, licence, source and sha256.

| Folder | Content | Used by |
|---|---|---|
| `sldprt/`, `sldasm/`, `slddrw/` | SolidWorks parts, an assembly and a drawing (NIST MBE PMI models and others) | SolidWorks reader, golden tests |
| `glb/`, `ply/` | glTF binaries and a gaussian-splat PLY | glTF and PLY readers |
| `khronos/` | `Box.glb`, `Box-Draco.gltf` + `.bin`, CC-BY-4.0, Cesium / Khronos glTF-Sample-Assets | glTF reader |
| `draco/` | Draco meshes (`.drc`) encoded by an independent encoder with the values it decodes to (`.json`), and glTF files using the extension; `tools/fixtures/make_draco_fixtures.py` writes them, CC0 | Draco decoder |
| `synthetic/blend/`, `synthetic/zstd/` | a Blender 5.2 scene saved three ways (`tools/fixtures/make_blend.py`, written with bpy) and Zstandard files from the python `zstandard` package (`tools/fixtures/make_zstd.py`), CC0 | Blender reader, Zstandard decoder |
| `synthetic/` | hand-made STL, OBJ, PLY (three encodings), 3MF, glTF, IFC, IGES, FreeCAD, G-code, Sweet Home 3D, textured (OBJ package, glTF, 3MF) and ZIP package fixtures (and more), CC0; `tools/fixtures/make_fixtures.py` writes them | mesh readers, robustness |
| `step/`, `iges/`, `dxf/`, `fcstd/`, `sh3d/`, `zip/` | third-party files for the readers of these formats (`zip/`: packages, only some hold a model) | readers, detection tests |
| `sat/` | NIST engineering design model saved as ACIS SAT (public domain, US government work) | ACIS reader |
| `dwg/`, `dwf/`, `3dm/`, `scad/`, `pln/`, `unitypackage/`, `b3d/`, `cad/` | formats that are detected but not yet read; targets for later readers | detection tests |

Licences are CC0-1.0, public domain (US government works, Natural Earth), MIT (Adafruit, NVIDIA JetRacer, UND Advanced
Rocketry Club and others) and CC-BY-4.0 for `Box.glb`; each file's entry names its source. Files over about 3.5 MB and
whole gaussian-splat sets were left out. The files are third-party data and are used as they were published, so their
bytes (and sha256) must not be changed.
