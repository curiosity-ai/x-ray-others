# XRay.OneNote: C# OneNote (.one) reader

Package `X-Ray.OneNote` (assembly and namespace `XRay.OneNote`). Reads OneNote 2010+ section
files: the MS-ONESTORE revision store, the MS-ONE object model, a public document tree, page
history, PDF and HTML export, and (best effort) notebook table of contents files. Pure managed, no NuGet dependencies.

The code is ported from an MIT-licensed open-source Python OneNote reader (see `LICENSE`). Its
checkout, when present, is a sibling under `/home/user` (`ls -d /home/user/*Note-FOSS-for-Python`);
it is the behavioral reference: read it, never edit it. Its `src/<pkg>/note/` holds the code,
`tests/` the original tests, `language-agnostic-plan/` format notes, and the root has the
[MS-ONESTORE](https://learn.microsoft.com/openspecs/office_file_formats/ms-onestore/) and
[MS-ONE](https://learn.microsoft.com/openspecs/office_file_formats/ms-one/) PDFs (not copied here).

Never write the upstream vendor's name anywhere in this folder (code, docs, comments, metadata,
goldens); `LICENSE` is the only file allowed to contain it. `grep -rIi <vendor> XRay.OneNote` must
match only `LICENSE`. Scripts that import the reference discover its package name at run time.

## Layout

| Path | What |
|---|---|
| `src/XRay.OneNote/*.cs` | Public DOM: `Document`, `Page`, `Title`, `Outline`, `OutlineElement`, `RichText`, `TextRun`, `TextStyle`, `ParagraphStyle`, `Image`, `AttachedFile`, `Table*`, `NumberList`, `NoteTag`, `PageHistory`, `DocumentVisitor`, `LoadOptions`, enums, exceptions |
| `src/XRay.OneNote/NotebookToc.cs` | `NotebookToc` (.onetoc2 reader, best effort) and `NotebookTocEntry` |
| `src/XRay.OneNote/Saving/` | `SaveOptions` (page range, `CancellationToken`), `PdfSaveOptions`, `FontsSubsystem`, `HtmlSaveOptions` + `ImageSavingArgs`/`FileSavingArgs`/`PageSavingArgs` and their callbacks, `HtmlWriter` (low-level entry points, `StyleSheet`) |
| `src/XRay.OneNote/Internal/Html/` | `HtmlRenderer` (the writer; `ImageFormats` sniffing) and `HtmlStyle` (the shared CSS; class names are output contract) |
| `src/XRay.OneNote/Internal/TagShapes.cs` | Note tag shape groups shared by the PDF and HTML writers |
| `src/XRay.OneNote/Internal/OneStore/` | MS-ONESTORE: `OneStoreParser` (header, file node lists, object spaces, revision manifests, object groups, file data store), `PropertySetParser`, `RevisionChains`, `TextDecoding`, value types, property/JCID names |
| `src/XRay.OneNote/Internal/MsOne/` | `OneNoteLoader`: object space and revision selection, logical graph, page/outline/text/image/table/tag builders, page history |
| `src/XRay.OneNote/Internal/Pdf/` | `PdfExporter` (layout), `Flowables` (paragraphs, tables with row splitting), `PdfCanvas` (`IPdfCanvas` + PDF file writer), `PdfFonts` (standard fonts, `FontResolver`, embedded TrueType), `TrueTypeFont` (parse, subset), `ImageDecoder` (PNG, JPEG pass-through, GIF, BMP), `StandardFontMetrics.g.cs` |
| `src/XRay.OneNote/Internal/Text/PyText.cs` | White space rules of the reference (`str.strip`/`isspace`) |
| `tests/XRay.OneNote.Tests/` | xUnit v3. `Goldens/dom`, `Goldens/pdf` (from the Python reader), `Goldens/images.json` (from Pillow), `Fonts/` (Liberation Sans, OFL), `Support/` (corpus locator, JSON diff, PDF inspector, recording canvas) |
| `tools/Shared/DomJson.cs` | The parity DOM dump (public API only), linked into tests and the CLI |
| `tools/XRay.OneNote.Cli/` | `onenotetool info|text|dump|pdf|html|toc|pdf-dir` |
| `tools/regenerate_goldens.py` | Regenerates `Goldens/dom` and `Goldens/pdf` from the Python checkout; `--compare-pdfs DIR` checks C# PDFs with pypdf |
| `tools/generate_standard_font_metrics.py` | Regenerates the standard font width tables |

Fixtures: `test_documents/onenote/*.one` (12 files, MIT, from the Python test suite). `test_documents/onenote/html-golden/` holds this package's own HTML output for them (not third-party reference output); regenerate with `XRAY_ONENOTE_UPDATE_GOLDENS=1 dotnet test ... --filter-class "*HtmlWriterTests"` and review the diff by eye. The golden test skips when the folder is absent, so reference output can be dropped there under the same names. Tests find
the corpus through `XRAY_TEST_DOCUMENTS` or by walking up to a `test_documents` directory; absent
corpus skips, a missing file fails.

## Commands

```sh
dotnet build XRay.OneNote.slnx -c Release                 # must report 0 warnings
dotnet test --project tests/XRay.OneNote.Tests -c Release
XRAY_ONENOTE_MUTATIONS=2000 dotnet test --project tests/XRay.OneNote.Tests -c Release -- --filter-class "*RobustnessTests"
dotnet pack src/XRay.OneNote -c Release -o artifacts
dotnet run --project tools/XRay.OneNote.Cli -c Release -- dump <file.one> --history

# Parity against the Python reader (venv with `pip install reportlab pypdf`)
python3 tools/regenerate_goldens.py --source <python checkout>            # rewrite goldens
dotnet run --project tools/XRay.OneNote.Cli -c Release -- pdf-dir ../../test_documents/onenote /tmp/pdf
python3 tools/regenerate_goldens.py --source <python checkout> --compare-pdfs /tmp/pdf
```

## Porting map

| Python (`src/<pkg>/note/`) | C# |
|---|---|
| `_internal/onestore/parser.py` | `Internal/OneStore/OneStoreParser.cs`, `PropertySetParser.cs`, `OneStoreTypes.cs`, `Names.cs` |
| `_internal/ms_one/loader.py`, `entities/` | `Internal/MsOne/OneNoteLoader.cs` |
| `model.py` | `Node.cs`, `Nodes.cs`, `Page.cs`, `RichText.cs`, `Styles.cs`, `Document.cs` |
| `enums.py`, `exceptions.py` | `Enums.cs`, `Exceptions.cs` (the reference's base error class became `OneNoteException`) |
| `saving/options.py` | `Saving/SaveOptions.cs` |
| `saving/pdf_writer.py` (ReportLab canvas + platypus) | `Internal/Pdf/*` (own PDF writer; same layout rules) |
| `tests/test_*.py` | `tests/XRay.OneNote.Tests/*Tests.cs` (same cases, ported; fake canvas became `RecordingCanvas`) |

## HTML, identity, TOC, cancellation

- **HTML**: one `HtmlRenderer`; no indentation inside paragraphs (they are `white-space: pre-wrap`);
  offsets and sizes are half-inches, written as px at 48 per half-inch; outlines keep document order
  (no reordering by position); tags are glyph spans (`on-tag on-tag-<kind> on-tag-<state>`); list
  markers come from `PdfExporter.ResolveListMarkerText`, hyperlink field codes are handled as in the
  PDF writer. Output must stay deterministic (no clock, culture or hash order) because the goldens pin it.
  Merged cells, header rows and ink are not modelled, so they are not written.
- **`Document.Guid`** is the header `guidFile`; MS-ONE says a TOC section entry's `FileIdentityGuid`
  equals it. `NotebookGuid` is the header `guidAncestor`; `EntityGuid` the section node's
  `NotebookManagementEntityGuid`; `CreationTime` the section node's topology time stamp.
- **`NotebookToc`** is best effort: MS-ONE does not publish the TOC property ids, so entries are
  recognised by property type (object-reference list, 16-byte GUID, UTF-16 string, integers) and
  the one known id 0x1CBE (color). Unverified against a OneNote-written file; the tests use
  `Support/SyntheticToc` (the parser's inverse). The root of a TOC is jcid 0x00020001.
- **Cancellation**: every loop that walks file nodes, revisions, object groups, the logical graph,
  pages, outlines, rows or image rows checks the token (`ThrowIfCancellationRequested`); PDF is
  written to the stream by `PdfCanvas.Save(Stream)` (the trailer hash is incremental).

## Rules

- Parity first. The loader keeps the reference's selection and ordering logic, including its
  quirks; check `DomParityTests` after any change under `Internal/`. Python truthiness and
  `isinstance(x, int)` checks map to `PropertyValue.IsTruthy` / `AsInteger()` (a bool counts as an
  integer). Python dict order maps to `OrderedDictionary`; stable sorts to `OrderBy`/`ThenBy`.
- Untrusted input: every read of file data is bounds-checked (`Bytes.*`). Malformed input loads
  best-effort (property sets stop at the damage, file node lists stop at a bad node) or throws
  `FileCorruptedException`/`UnsupportedFileFormatException`. Any other exception escaping
  `Document`'s constructors or `Save` is a bug; `RobustnessTests` guards this. Cycles and
  pathological sizes are bounded (fragment chains, revision chains, logical graph depth and node
  budget, array and GID range counts).
- No NuGet dependencies, no native code. Zero warnings; fix the cause.
- Public API mirrors the reference's .NET-style names. Read-only members are `init`/get-only;
  collections are get-only live lists. New internals stay `internal`.

## Deliberate differences from the reference

- `Accept` passes the visitor through tables, rows and cells (the reference stops at a table).
- PDF: a long wrapped paragraph breaks pages at any line (the reference only at newlines, so text
  ran off the page); a table taller than a page is drawn instead of looping; an undecodable image
  in a table is skipped instead of raising; list markers, attachment names in tables and the
  empty-document name pick a TrueType font when they need one (the reference used standard fonts).
- PDF output is this package's own writer, not ReportLab: page count, text, links and images
  match, layout is near-identical, and pypdf groups our text into fuller lines.
- Text run boundaries index UTF-16 code units (the reference uses code points); identical for
  BMP text. FILETIMEs convert exactly (the reference rounds through float microseconds).
- Colors are `uint?` COLORREF values; integers out of `int` range read as null; non-text values
  of text properties decode to null except integers and booleans.
- Dropped: licensing stubs, `DetectLayoutChanges`, no-op placeholders (`ImageCompression`,
  `JpegQuality`, `PageSettings`, `PageSplittingAlgorithm`, `Page.Margin`/`SizeType`/
  `PageLayoutSize`/`PageContentRevisionSummary`). `FontsSubsystem` is now a real option (font
  directories); the reference's font environment variable is `XRAY_ONENOTE_PDF_USE_SYSTEM_FONTS`.

## Validation status

- DOM parity: all 12 fixtures equal the Python dumps field for field, page history included.
- PDF parity: all 12 match the Python manifests (page count, links, image count, text without
  white space); 2 also match white space exactly.
- Image decoders match Pillow on PNG (all color types, 1-16 bit, Adam7, tRNS), GIF, BMP.

## Gaps

- OneNote 2007, encrypted sections, MS-FSSHTTPB (web) packaging, `.onepkg` (CAB) listing; writing `.one`. `.onetoc2` is best effort (see above).
- Files referenced outside the section (`<file>` references into `onefiles/`) have empty bytes.
- Ink, equations, page backgrounds and outline positions are not modeled; the PDF lays outlines
  out in reading order at a fixed origin, as the reference does.
- PDF images: TIFF, EMF/WMF and other formats print the file name instead.
