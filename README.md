# X-Ray: other formats

[![Docs](https://img.shields.io/badge/docs-xray.curiosity.ai-007ec6)](https://xray.curiosity.ai/)

Part of the [X-Ray](https://github.com/curiosity-ai/x-ray) family: modern, pure C# NuGet packages
for .NET 10 that extract content from documents, render them to PDF and images, and handle
specific file types. This repository holds the packages for formats outside the Office and
content-extraction cores: Photoshop, email, PostScript/XPS and OneNote.

Every package is pure managed code with no native dependencies in its core. Where a feature needs
native code (SkiaSharp, HarfBuzz), it ships as a separate optional package.

## Packages

| Package | Folder | What it does | Licence |
|---|---|---|---|
| `X-Ray.Psd` | [`XRay.Psd/`](XRay.Psd/) | PSD/PSB reader: layer tree, masks, effects, text extraction, SIMD compositing to sRGB, PNG/JPEG output. No dependencies. | MIT |
| `X-Ray.Psd.Skia` | [`XRay.Psd/`](XRay.Psd/) | Optional SkiaSharp interop for X-Ray.Psd (SKBitmap/SKImage, Skia encoders). | MIT |
| `X-Ray.Psd.Text` | [`XRay.Psd/`](XRay.Psd/) | Optional re-rendering of type layers from the text model (SkiaSharp + HarfBuzz). | MIT |
| `X-Ray.Email` | [`XRay.Email/`](XRay.Email/) | Outlook MSG, EML/MIME and CFB: read, write and convert between MSG and EML. No dependencies. | MIT |
| `X-Ray.Ics` | [`XRay.Ics/`](XRay.Ics/) | iCalendar (.ics) and vCalendar: parse, recurrence expansion, extraction of organizer, attendees and description, HTML, text and Markdown renderings, writer. No dependencies. | MIT |
| `X-Ray.PS` | [`XRay.PS/`](XRay.PS/) | PostScript, EPS and XPS: interpret, inspect, edit and convert to PDF and raster images. Core is pure managed. | MIT AND OFL-1.1 |
| `X-Ray.PS.Skia` | [`XRay.PS/`](XRay.PS/) | Optional SkiaSharp raster writer for X-Ray.PS (anti-aliased output, XPS opacity masks). | MIT |
| `X-Ray.OneNote` | [`XRay.OneNote/`](XRay.OneNote/) | OneNote section files (.one): pages, outlines, rich text, images, tables, attachments, tags, page history; PDF export. No dependencies. | MIT |

Install one with `dotnet add package X-Ray.Psd` (and so on), or install the whole family with
`dotnet add package X-Ray`. Package ids are hyphenated; assemblies and namespaces are `XRay.*`.

Each package folder has its own `README.md` (the one packed into the nupkg), `CLAUDE.md`
(development notes), `LICENSE`, solution file, tests and tools.

## Building and testing

Run from the repository root, so `global.json` selects the Microsoft.Testing.Platform runner:

```sh
git submodule update --init --depth 1 test_documents   # fixtures, see below
dotnet build XRay.Others.slnx -c Release
dotnet test --solution XRay.Others.slnx -c Release
```

Each package also builds and tests on its own through `<Package>/<Package>.slnx`.

## Test fixtures

Real-world fixtures live in [curiosity-ai/test_documents](https://github.com/curiosity-ai/test_documents),
the family's shared corpus, checked out here as the `test_documents` submodule: `psd/`, `ps/`,
`xps/`, `onenote/` and `email/`. Tests that need it skip when it is absent. See
[`.claude/skills/test-documents`](.claude/skills/test-documents/SKILL.md).

## Publishing

One Azure DevOps pipeline, [`.devops/build-x-ray-others-nuget.yml`](.devops/build-x-ray-others-nuget.yml),
builds, packs and pushes every package here to nuget.org at one CalVer version (`yy.M.<build>`).
It triggers on `main` for a change under any package folder or to the shared build files, so a
change to one package publishes a new version number for all of them. The [X-Ray umbrella](https://github.com/curiosity-ai/x-ray) picks up the newest published
versions.

## Licence

Each package keeps the licence of the code it was derived from, in its own folder (`LICENSE`).
All packages in this repository are MIT.
