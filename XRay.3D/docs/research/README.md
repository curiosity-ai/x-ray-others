# Research notes: the reference viewer

Working notes from reviewing the web 3D viewer this library re-implements (called "the reference viewer"
everywhere in this repository; its name is deliberately not used). They record what the viewer does, which
engine parses each format, what survives into its scene model and how large a port of each piece would be.
Chunk names such as `68243-…js` and line numbers refer to its beautified, minified app code and are only
useful to someone holding a copy; the conclusions are what matter.

| File | Content |
|---|---|
| [formats.md](formats.md) | Every input format: parser, metadata, detection order, size limits, feasibility tiers |
| [features-export.md](features-export.md) | Scene model, tools, the 35-item export menu, and the case for glTF/GLB as the browser target |
| [wasm-modules.md](wasm-modules.md) | The WebAssembly parsers: what each is, its API, licence signals, effort to port |

[../solidworks-format.md](../solidworks-format.md) is not a note but a specification: what was learned about the
SolidWorks container and display lists by decompiling the viewer's reader, and how this library decodes them.
