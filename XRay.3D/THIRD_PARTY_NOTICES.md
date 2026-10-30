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

## ACadSharp (DWG AC1021 pages and the ordered header variables)

`src/XRay.ThreeD/Formats/Cad/DwgLz21.cs` decodes the LZ77 variant of AutoCAD 2007 (AC1021) file headers and pages. The opcode layout and the
per-length literal copy table (pieces of 1 to 16 bytes, reordered) were taken from `DwgLZ77AC21Decompressor` of
[ACadSharp](https://github.com/DomCR/ACadSharp) (MIT licence), re-implemented here with bounds checks on every read and write. The page and section
map layout of `DwgContainer.OpenR2007` (Reed-Solomon block interleaving, the 0x110 byte header table, section descriptors) follows that project's
reader. `DwgHeaderVariables.cs` is a mechanical conversion of the header reader of the same project (the order of the ~270 header variables and their per-release
conditions), over this repository's bit reader. Checked against the AC1021 example drawing and its DXF twin and, for the header variables, against the twin of
every release from AC1015 to AC1032 (`DwgReleaseTests`). The licence:

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

## cadmpeg (CATIA V5 container description)

`src/XRay.ThreeD/Formats/Catia/` reads the CATIA V5 container from the public format description of [cadmpeg](https://github.com/cadmpeg/cadmpeg)
(`docs/formats/catia.md`, released under CC-BY-4.0, copyright the cadmpeg project; the `cadmpeg-codec-catia` crate is Apache-2.0). The outer and nested
`V5_CFV2` headers, the `CATIA_V5 CB0001` stream directory and its extent lists, the `FINJPL` and `CATCGRCont` notes, the indexed-triangle packets with big-endian
handles and the colour row layout are taken from that description and re-checked on real files; the coordinate-table encoding, the handle widths, the packet
variants, the metadata streams and the link records were derived from the sample files. No source code of cadmpeg was copied or ported. The description is
licensed under <https://creativecommons.org/licenses/by/4.0/>.

## CATIA sample files in the dataset

`dataset/catia/` holds seven CATIA V5 files that were published under the MIT licence and are redistributed unchanged: five from
[hssnadr/HTBX_01Ressources](https://github.com/hssnadr/HTBX_01Ressources) (copyright (c) 2020 Adrien Husson) and two from
[Dictor/HaneulMyo](https://github.com/Dictor/HaneulMyo) (copyright (c) 2019 Dictor). The MIT licence text is in the first section of this file
(meshoptimizer) with the corresponding copyright lines above.


## FerroJT (JT segment layout and codecs)

`src/XRay.ThreeD/Formats/Jt/` reads JT files. The element layouts of the logical scene graph, the integer codecs (bitlength, Huffman, arithmetic, chopper), the dual-mesh topology decoder, the vertex and unit-normal quantisation and the segment envelopes were taken from the reading code of [FerroJT](https://github.com/jriegel/FerroJT) (MIT licence), re-implemented in C# with this repository's own scene and mesh model and checked against the JSON that the JT Open reference toolkit wrote for five sample files (`JtTests`). The licence:

```
MIT License

Copyright (c) 2024 Maksym Pavlenko

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

The `.xz` decoder (`Internal/XzDecoder.cs`) is written from the public xz and LZMA2 format descriptions and checked against liblzma.
## Parasolid XT format reference and parasolid-kit

The node types and field lists of `Formats/Parasolid/ParasolidSchema.cs` were written from the struct listings of the publicly distributed Parasolid XT
Format Reference (Siemens, April 2008). [parasolid-kit](https://github.com/monozukuri-ai/parasolid-kit) (MIT licence, copyright 2026 parasolid-kit
contributors) was read to cross-check the field lists against real exports (it confirms, for instance, that the ellipse lists its sense before its centre,
that an intersection curve lists its two surfaces before its chart, and that B-surface control grids run with v fastest).

Taken from parasolid-kit and ported (data and format knowledge, no code): the binary encoding (header framing, big-endian widths, the positive-integer pointer encoding, null sentinels) and the layout of the embedded schema section of a transmit file (the stream header
with the schema key and the highest node type, and per node type at first use: 255 for an unchanged base layout, or a field count followed by the copy, delete, insert,
append and end operations, or a complete declaration for a type the base lacks; the field encoding: name, pointer class, element count, type code and
transmission flag) and the per-revision edit sequences of the plain schemas 15003 to 33103 (the BODY, REGION, INTERSECTION, LIMIT, LIST and POINTER block edits in
`Formats/Parasolid/ParasolidLayouts.cs`). The edit sequence of schema 35102 and the part block (type 176) were read from real local sample files. The licence:

```
MIT License

Copyright (c) 2026 parasolid-kit contributors

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
