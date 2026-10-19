# x-ray-others

Part of the X-Ray family of pure C# .NET 10 NuGet packages (Curiosity GmbH). The family's root
repository, umbrella package, shared skills and documentation site are in
[curiosity-ai/x-ray](https://github.com/curiosity-ai/x-ray); read its `CLAUDE.md` for the family
conventions. This repository holds the packages for formats outside x-ray-content (extraction)
and x-ray-office (Office rendering).

## Layout

Each package is a self-contained folder. Read that folder's `CLAUDE.md` before working in it;
the rules there (provenance, reference implementations, parity checks) are specific to it.

| Folder | Packages | Origin |
|---|---|---|
| `XRay.Psd/` | `X-Ray.Psd`, `X-Ray.Psd.Skia`, `X-Ray.Psd.Text` | Moved from theolivenbaum/Patchy (C# port of a C++/Qt editor; the C++ reference stays in that repository's `.reference/`) |
| `XRay.Email/` | `X-Ray.Email` | Derived from an MIT-licensed open-source C# MSG/EML toolkit |
| `XRay.Ics/` | `X-Ray.Ics` | Written from RFC 5545 for this family; checked against Python `icalendar` and `dateutil` on 434 fixtures |
| `XRay.PS/` | `X-Ray.PS` | Ported from an MIT-licensed open-source Python PostScript/XPS library |
| `XRay.OneNote/` | `X-Ray.OneNote` | Ported from an MIT-licensed open-source Python OneNote reader |
| `.devops/` | `build-x-ray-others-nuget.yml`, the one pipeline that publishes every package | |
| `assets/logo.png` | the family icon, packed into every package | |
| `test_documents/` | git submodule: curiosity-ai/test_documents | |
| `XRay.Others.slnx` | every project in the repository | |

Root build files are shared: `Directory.Build.props` (net10.0, nullable, implicit usings,
Curiosity GmbH metadata, symbols), `Directory.Build.targets` (packs the icon for any packable
project), `global.json` (SDK and the Microsoft.Testing.Platform runner). Each package folder has a
`Directory.Build.props` that imports the root one. A package folder that adds a
`Directory.Build.targets` must import the root one too, or its packages lose their icon.

## Commands

Run from the repository root so `global.json` applies:

```sh
git submodule update --init --depth 1 test_documents
dotnet build XRay.Others.slnx -c Release
dotnet test --solution XRay.Others.slnx -c Release
dotnet test --solution XRay.Psd/XRay.Psd.slnx -c Release      # one package
dotnet pack XRay.Email/src/XRay.Email -c Release -o artifacts  # one package
```

## Rules

- **Naming.** Package ids are hyphenated (`X-Ray.Psd`); assemblies and namespaces are not
  (`XRay.Psd`), because a hyphen is not valid in a C# identifier.
- **Pure managed core.** No native code or P/Invoke in a core package. Native dependencies
  (SkiaSharp, HarfBuzz) go in a separate optional package, as `X-Ray.Psd.Skia`/`.Text` do.
- **Licences.** Each package folder keeps the `LICENSE` of the code it came from, verbatim. Do not
  edit a copyright line.
- **No vendor names.** XRay.Email, XRay.PS and XRay.OneNote derive from projects published under
  a commercial vendor's name. That name appears only in their verbatim `LICENSE` files. Do not
  write it anywhere else: code, comments, docs, package metadata, commit messages.
- **Tests.** xUnit v3 on Microsoft.Testing.Platform (VSTest-style projects do not run under
  this `global.json`). Fixtures come from `test_documents/` through each test project's corpus
  resolver (`XRAY_TEST_DOCUMENTS`, else the nearest `test_documents` directory above the test
  binaries). With no corpus, corpus tests skip; with a corpus but a missing file, they fail. See
  `.claude/skills/test-documents`.
- **Untrusted input.** Every reader takes hostile files: bounds-checked reads, bounded recursion,
  and only the package's own exception types escaping a load. Each package has truncation and
  mutation tests guarding this; keep them passing and extend them with new parsers.
- **Zero build warnings.** Fix the cause; scope any suppression to one line.

## Publishing

`.devops/build-x-ray-others-nuget.yml` is the single pipeline for the repository, following the
family's shape: CalVer `yy.M.<buildId % 65536>`, windows-latest, .NET 10 SDK, restore/build/pack
with the version passed in, push (`.nupkg` and `.snupkg`) through the `nuget-curiosity-org`
service connection, one step per package (`.devops/templates/push-nuget.yml`) so one failed push does not
stop the others and a re-run skips what is already published. It triggers on `main` for a change under any package folder or to the shared
build files and publishes all packages at one version. Adding a package to this repository means:
a new folder following the layout above, its projects added to the variables, restore, build and
`packList` and push list of that pipeline (plus a path filter for its folder), a row in `README.md`, its projects in
`XRay.Others.slnx`, and a `Members.props` entry plus docs section in curiosity-ai/x-ray (see the
`add-family-member` skill there).
