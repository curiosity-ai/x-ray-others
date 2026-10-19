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
| `src/XRay.OneNote/NotebookToc.cs` | `NotebookToc` (.onetoc2 reader, nested section groups) and `NotebookTocEntry` |
| `src/XRay.OneNote/Saving/` | `SaveOptions` (page range, `CancellationToken`), `PdfSaveOptions`, `FontsSubsystem`, `HtmlSaveOptions` + `ImageSavingArgs`/`FileSavingArgs`/`PageSavingArgs` and their callbacks, `HtmlWriter` (low-level entry points, `StyleSheet`) |
| `src/XRay.OneNote/Internal/Html/` | `HtmlRenderer` (the writer; `ImageFormats` sniffing) and `HtmlStyle` (the shared CSS; class names are output contract) |
| `src/XRay.OneNote/Internal/TagShapes.cs` | Note tag shape groups shared by the PDF and HTML writers |
| `src/XRay.OneNote/Internal/OneStore/` | MS-ONESTORE: `OneStoreParser` (header, transaction log, file node lists, object spaces, revision manifests, object groups, file data store, container detection), `PropertySetParser`, `RevisionChains`, `TextDecoding`, value types, property/JCID names |
| `src/XRay.OneNote/Internal/Fsshttpb/` | MS-FSSHTTPB packages (MS-ONESTORE 2.7, 2.8): `FsshttpbReader` (compact integers, extended GUIDs, stream object headers), `FsshttpbPackage` (data elements), `FsshttpbStore` (builds the same `OneStoreFile` the file-node-list reader does) |
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
python3 tools/regenerate_goldens.py --source <python checkout>            # rewrite goldens (pure Python output)
# ...then put back the fields this package corrects (see "Golden policy" below):
XRAY_ONENOTE_UPDATE_GOLDENS=1 dotnet test --project tests/XRay.OneNote.Tests -c Release -- --filter-class "*DomParityTests"
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
- **`NotebookToc`**: the root object (jcid 0x00020001, a TocContainer) is the default-content root of the data-root
  object space; `0x1CF6` lists child TocContainers, each with the file name (`0x1D6B`, UTF-16, `^M` for `+` and
  `^J` for `,`), order (`0x1CB9`), color (`0x1CBE`, R,G,B and inverted alpha, 0xFFFFFFFF undefined) and identity
  (`0x1D94`, equal to the section's `Document.Guid`); the root also has `EnableHistory` (`0x1E1E`). Entries are
  deduplicated by file name keeping the last reference, then ordered by (order, position). These ids are not in the
  specs this package was built from; they were established from files OneNote wrote and cross-checked with an
  independent parser (see Validation status). `NotebookToc.Load(path)` recurses into section group directories
  (`.onetoc2`, else the first `*.onetoc2`) and never enters `OneNote_RecycleBin`.
- **Containers**: `OneStoreParser.ParseCore` picks the reader. A file whose bytes 48..63 are the package format GUID
  `638DE92F-...` is an FSSHTTPB package (`Internal/Fsshttpb`); a revision store header whose
  `guidLegacyFileVersion` is nil and whose package starts right after the transaction log (the OneDrive download
  layout) is read as that embedded package; anything else is a file-node-list store. A package feeds the same
  `OneStoreFile` (one object space per cell, one revision per revision manifest, objects with decoded property sets),
  so `OneNoteLoader` is shared. References in a package come from the object data's arrays, matched by position to
  the compact ids in the property set streams: object ids to the extended GUID array, object spaces and contexts to
  the cell id array (a cell of the object's own space is a context, any other an object space).
- **Pages** come from the section node's page series (in child order) and each series' `ChildGraphSpaceElementNodes`
  object space references (ObjectSpaceID properties decode into `PropertyKind.ObjectSpaceId`); only when the section
  names no usable page space does the loader fall back to scoring object spaces and ordering by page metadata.
- **Transaction log** (MS-ONESTORE 2.3.3): `ReadCommittedCounts` reads how many nodes the committed transactions
  (`cTransactionsInLog`) put in each file node list and `ParseFileNodeList` stops there (a chunk terminator does not
  count). An unreadable log cuts nothing. Revision selection still uses the scoring/`GetLatestRevisionIndex` logic of
  the reference: it was not replaced by pure role/context rules because nothing showed those to be better on the 12
  corpus fixtures and 32 third-party samples. `.onetoc2` file-node-list files declare objects with 0x02D/0x02E and
  revise them with 0x041/0x042 directly in the revision manifest, and their Entry2/Entry3 global id table nodes copy
  from the dependency revision's table; all handled. 0x084 (ObjectInfoDependencyOverrides, reference counts only) is
  skipped.
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
- Table column widths: `TableColumnWidths`/`TableColumnsLocked` carry a one-byte count (the reference reads it as part
  of the first float), so widths are real half-inch values and the PDF writer uses them proportionally.
- Note tags are built from the definition plus the tag's own `NoteTagState` (created, completed, `ActionItemStatus`
  bits); a tag that cannot be checked is open and has no completion time. The reference reports a 1980 completion.
- Page creation time falls back to the page metadata's topology time stamp; for packages (not section files) an empty
  title object is filled from the page metadata's cached title.
- `TextStyle.HyperlinkAddress` is filled from `HYPERLINK "address"` field codes (also when OneNote online splits the
  code over two runs); the field code stays in `RichText.Text` (offsets match the reference) and the writers hide it.
- `ParagraphStyle.StyleId`, `Image.LayoutMaxWidth/LayoutMaxHeight/IsBackground`, `TableCell.BackgroundColor`,
  `Document.Color` and `Page.InkRecognition` are additions; U+FFFC placeholders are dropped from paragraphs that carry
  inline object data. `DomParityTests.DeliberateDifference` names the golden fields these change.

## Golden policy

`Goldens/dom/*.json` started as the Python reader's dumps. Five groups of fields were wrong or empty in the
reference and are corrected here, so their golden values are this package's: table column widths, tag status
and created/completed times, page creation time, and hyperlink addresses written as field codes. They were
cross-checked against an independent parser (used only as an oracle) on the 12 fixtures and on its own 32
sample files. Everything else in each golden is untouched Python output, which is what keeps the parity claim
meaningful. `DeliberateDifference` in `DomParityTests` is the list of those paths and is used only to
re-derive: after `tools/regenerate_goldens.py` rewrites the goldens from Python, run the test with
`XRAY_ONENOTE_UPDATE_GOLDENS=1` to put the five groups back, then review `git diff`. The comparison itself
ignores nothing.

## Validation status

- DOM parity: all 12 fixtures equal their goldens field for field, page history included. The goldens are the
  Python dumps with five fields re-derived (see "Golden policy").
- PDF parity: all 12 match the Python manifests (page count, links, image count, text without
  white space); 2 also match white space exactly.
- Image decoders match Pillow on PNG (all color types, 1-16 bit, Adam7, tRNS), GIF, BMP.
- Third-party samples (optional, never copied here; `XRAY_ONENOTE_RS_SAMPLES` points at the folder): 32 section files
  (25 FSSHTTPB packages, 7 file-node-list files) and 12 table of contents files from the test suite of an independent
  MPL-2.0 Rust parser, used only as a behavioural oracle. All 32 sections load with the same identity GUID, section
  name, page count, page order, page levels and section color as that parser; hyperlink addresses, handwriting
  recognition text, paragraph style ids and image bytes match; remaining differences are things this model does not hold
  (ink strokes, outline text normalization). `RustSampleExpectations` records what the oracle reported; the tests skip
  without the folder. FSSHTTPB robustness: prefix and mutation tests on the samples plus `SyntheticPackage`, a writer of
  minimal packages for corpus-free tests.

## Gaps

- OneNote 2007, encrypted sections, `.onepkg` (CAB) listing; writing `.one`. Data element fragments (MS-FSSHTTPB
  2.2.1.12.7) are skipped, not reassembled; no sample uses them.
- Files referenced outside the section (`<file>` references into `onefiles/`) have empty bytes.
- Ink strokes, equations and page backgrounds are not modeled (recognized handwriting is `Page.InkRecognition`);
  outline positions are kept, but the PDF lays outlines out in reading order at a fixed origin, as the reference does.
- A page the reference titles from its first text (no title anywhere) stays untitled; for section files an empty title
  object stays empty.
- PDF images: TIFF, EMF/WMF and other formats print the file name instead; PDF does not draw cell backgrounds.
