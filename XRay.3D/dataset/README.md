# Dataset

Test files for X-Ray.3D, committed so tests never depend on a download. `manifest.json` lists each file with its
format, title, licence, source and sha256.

| Folder | Content | Used by |
|---|---|---|
| `sldprt/`, `sldasm/`, `slddrw/` | SolidWorks parts, an assembly and a drawing (NIST MBE PMI models and others) | SolidWorks reader, golden tests |
| `glb/`, `ply/` | glTF binaries and a gaussian-splat PLY | glTF and PLY readers |
| `khronos/` | `Box.glb`, CC-BY-4.0, Cesium / Khronos glTF-Sample-Assets | glTF reader |
| `synthetic/` | hand-made STL, OBJ, PLY (three encodings), 3MF, glTF, IFC, IGES, FreeCAD, G-code, Sweet Home 3D, textured (OBJ package, glTF, 3MF) and ZIP package fixtures (and more), CC0; `tools/fixtures/make_fixtures.py` writes them | mesh readers, robustness |
| `step/`, `iges/`, `dxf/`, `fcstd/`, `sh3d/`, `zip/` | third-party files for the readers of these formats (`zip/`: packages, only some hold a model) | readers, detection tests |
| `dwg/`, `dwf/`, `3dm/`, `scad/`, `pln/`, `uasset/`, `unitypackage/`, `b3d/`, `cad/`, `rlf/`, `psu/` | formats that are detected but not yet read; targets for later readers | detection tests |

Licences are CC0-1.0, public domain (US government works, Natural Earth), MIT (Adafruit, NVIDIA JetRacer, UND Advanced
Rocketry Club and others) and CC-BY-4.0 for `Box.glb`; each file's entry names its source. Files over about 3.5 MB and
whole gaussian-splat sets were left out. The files are third-party data and are used as they were published, so their
bytes (and sha256) must not be changed.
