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
