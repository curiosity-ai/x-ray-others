# Third-party notices

## meshoptimizer

`src/XRay.ThreeD/Internal/MeshoptEncoder.cs` is a C# port of the vertex, index and filter *encoders* of
[meshoptimizer](https://github.com/zeux/meshoptimizer) (`vertexcodec.cpp`, `indexcodec.cpp`, the octahedral filter), producing the
bitstreams of the glTF extension `EXT_meshopt_compression`. The index codec is in turn based on work by Fabian Giesen
(simple lossless index buffer compression, 2013) and Conor Stokes (vertex cache optimised index buffer compression, 2014).

```
MIT License

Copyright (c) 2016-2026 Arseny Kapoulkine

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Draco bitstream specification

`src/XRay.ThreeD/Internal/DracoDecoder*.cs` decodes `KHR_draco_mesh_compression` data. It was written from the public
[Draco bitstream specification](https://google.github.io/draco/spec/) (Apache License 2.0, the Draco authors); no source code of the
Draco library was copied or ported. The decoder is checked against the output of an independent implementation (the DracoPy
bindings of Draco, used only by `tools/fixtures/make_draco_fixtures.py` to produce the fixtures under `dataset/draco/`).

## laz-rs (layered LAZ decoding)

`src/XRay.ThreeD/Formats/PointCloud/LazLayered.cs` (the LAS 1.4 point formats 6 to 10 of LAZ: POINT14, RGB14, RGBNIR14, WAVEPACKET14 and BYTE14,
versions 3 and 4) is a C# port of the decompressor of [laz-rs](https://github.com/tmontaigu/laz-rs) (Apache License 2.0, copyright
2019 to 2023 Thomas Montaigu), which is itself a port of the LASzip reference implementation (copyright 2007 to 2015 Martin Isenburg,
rapidlasso). The context tables (`NumberReturnMap6`, `NumberReturnLevel8`), the model sizes, the GPS time sequence logic and the per-layer
context handling follow that source; the range decoder, the symbol and bit models and the integer decoder are this repository's own (see
`LazDecoder.cs`, written from the public LASzip description). The Apache License 2.0 text is at
<https://www.apache.org/licenses/LICENSE-2.0>; the port is checked byte for byte against the C++ LASzip (through laspy's `laszip` back end) and
against lazrs on the fixtures written by `tools/fixtures/make_las14_fixtures.py`.

## openskp (SketchUp 2021+ record layout)

`src/XRay.ThreeD/Formats/SketchUp/SketchUpVff.cs` reads the `model.dat` of SketchUp 2021 and later, and `SketchUpLegacy.cs` the MFC object stream of 2013 to 2020 (class layouts, slot-base probes and the burned-slot correction of the package's `legacy` reader, re-implemented in C#). The record layout (the tag values of definitions,
vertices, edges, faces, loops, coedges, instances, layers and materials, the container set, the 13-double instance matrix) was taken from the
reading code of [openskp](https://github.com/iamahsanmehmood/openskp) (npm package `openskp` 1.4.0, MIT licence), re-implemented in C# with this
repository's own tree, triangulation and scene code, and checked against independent counts on 24 real files (`SketchUpGeometryTests`). The licence:

```
MIT License

Copyright (c) 2024-2026 Ahsan Mehmood

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The Python port of openskp is LGPL-licensed and was not used.

## ACadSharp (DWG AC1021 literal and match decoder)

`src/XRay.ThreeD/Formats/Cad/DwgLz21.cs` decodes the LZ77 variant of AutoCAD 2007 (AC1021) file headers and pages. The opcode layout and the
per-length literal copy table (pieces of 1 to 16 bytes, reordered) were taken from `DwgLZ77AC21Decompressor` of
[ACadSharp](https://github.com/DomCR/ACadSharp) (MIT licence), re-implemented here with bounds checks on every read and write. The page and section
map layout of `DwgContainer.OpenR2007` (Reed-Solomon block interleaving, the 0x110 byte header table, section descriptors) follows that project's
reader. Checked against the AC1021 example drawing and its DXF twin (`DwgReleaseTests`). The licence:

```
MIT License

Copyright (c) 2021 Albert Domenech

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
