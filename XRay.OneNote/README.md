# X-Ray.OneNote

![X-Ray](https://raw.githubusercontent.com/curiosity-ai/x-ray-others/main/assets/logo.png)

X-Ray.OneNote reads Microsoft OneNote section files (`.one`) and notebook table of contents files (`.onetoc2`)
in pure managed C#, whether they were written by OneNote desktop or by OneNote online (OneDrive and SharePoint). It parses the
MS-ONESTORE revision store (as file node lists or as an FSSHTTPB package) and the MS-ONE object model into a document tree of pages, outlines,
formatted rich text, images, tables, attached files, note tags and numbered lists, reconstructs
page history, and exports pages to PDF and HTML. It has no NuGet dependencies and no native code, and does
not need OneNote or COM automation.

It is part of [X-Ray](https://github.com/curiosity-ai/x-ray), a family of .NET 10 libraries for
reading and converting documents.

## Install

```sh
dotnet add package X-Ray.OneNote
```

## Quick start

```csharp
using XRay.OneNote;
using XRay.OneNote.Saving;

// Load a section from a path or a stream.
var document = new Document("Notes.one");
Console.WriteLine(document.DisplayName);

// Walk the pages and their content.
foreach (var page in document.Pages)
{
    Console.WriteLine($"# {page.Title?.TitleText?.Text}  (level {page.Level}, by {page.Author})");

    foreach (var outline in page.GetChildNodes<Outline>())
    {
        foreach (var richText in outline.GetChildNodes<RichText>())
        {
            Console.WriteLine(richText.Text);
        }
    }
}

// Extract all text, including tables and titles.
var text = string.Join("\n", document.GetChildNodes<RichText>().Select(rt => rt.Text));

// Embedded pictures and files carry their bytes.
foreach (var image in document.GetChildNodes<Image>())
{
    File.WriteAllBytes(image.FileName ?? "image.bin", image.Bytes);
}

// Export to PDF.
document.Save("Notes.pdf");
document.Save(Stream.Null, new PdfSaveOptions { PageIndex = 0, PageCount = 1 });
```

## What it reads

| Node | Content |
|---|---|
| `Document` | the section: `DisplayName`, `Color`, `Guid`, pages in section order (subpages included) |
| `Page` | `Title`, `Author`, `CreationTime`, `LastModifiedTime`, `Level`, `InkRecognition` (the text OneNote recognized in handwriting) |
| `Title` | `TitleText`, `TitleDate`, `TitleTime` (each a `RichText`) |
| `Outline` | a note container: offsets and maximum width, holding `OutlineElement`s |
| `OutlineElement` | one paragraph level; nested elements form indented levels; `NumberList` for list items |
| `RichText` | `Text`, `TextRuns` (each with a `TextStyle`: font, size, color, highlight, bold, italic, underline, strikethrough, super/subscript, hyperlink and its `HyperlinkAddress`, language), `ParagraphStyle` (including the named style, `StyleId`: `p`, `h1` to `h6`, `code`, `blockquote`, `cite`), `Alignment`, `Tags` |
| `Image` | `Bytes`, `FileName`, size, `LayoutMaxWidth`/`LayoutMaxHeight`, `IsBackground`, `Alignment`, alternative text, hyperlink, `Tags` |
| `Table`, `TableRow`, `TableCell` | rows of cells holding outline content (a cell's `BackgroundColor`); `Columns` with widths in half-inches and locks; `Tags` |
| `AttachedFile` | `Bytes`, `FileName`, `Tags` |
| `NoteTag` | `Label`, `Icon` (shape), `Status` (open, completed, disabled), colors, creation and completion times |

Traverse with `CompositeNode.GetChildNodes<T>()` (every descendant of a type, in document order),
by enumerating a node's children, or with a `DocumentVisitor` passed to `Accept`.

### Page history

Load with `new LoadOptions { LoadHistory = true }` to reconstruct earlier versions of each page
from the revision store. `Document.GetPageHistory(page)` returns them oldest first, with the
current version in `Current`.

```csharp
var document = new Document("Notes.one", new LoadOptions { LoadHistory = true });
var page = document.Pages.First();
foreach (var version in document.GetPageHistory(page))
{
    Console.WriteLine(version.LastModifiedTime);
}
```

## PDF export

`Document.Save` writes PDF: titles, outlines with list numbering, formatted runs, clickable
hyperlinks, note tag glyphs (ticked when completed), images (PNG, JPEG, GIF and BMP), attached file names and tables
split across pages. Text in Latin-1 uses the PDF standard fonts. Text outside Latin-1 (Cyrillic,
for example) is drawn with a TrueType font that is embedded as a subset; the library looks for one
in the usual Windows, Linux and macOS font directories. To control where fonts come from:

```csharp
var fonts = new FontsSubsystem { SearchSystemFontDirectories = false };
fonts.FontDirectories.Add("/path/to/fonts");   // e.g. DejaVuSans.ttf, LiberationSans-Regular.ttf, arial.ttf
document.Save("Notes.pdf", new PdfSaveOptions { FontsSubsystem = fonts });
```

When no suitable font is found, characters the standard fonts cannot show are written as `?`.

## HTML export

`Document.Save` also writes HTML 5 (UTF-8, no script, deterministic output, stable `on-` class
names, CSS in a `<style>` element). Outlines keep their horizontal offset and width, outline
elements nest with their list markers, runs carry bold, italic, underline, strikethrough, super
and subscript, color, highlight, font and size, links are anchors (only `http`, `https`, `mailto`,
`ftp`, `tel`, `onenote` and `file` targets), tags are inline markers, and tables, pictures
and attached files are written in place. Hidden text stays in the markup with the class
`on-hidden`; set `ShowHiddenText` to show it. Paragraphs in a heading style are `h1` to `h6`, `code` is `pre` and
`blockquote` is `blockquote`; pages carry `data-created` and `data-modified`, table cells their background color.

```csharp
// One HTML document per page, pictures served from your own URLs, each page into your own stream.
document.Save(Stream.Null, new HtmlSaveOptions
{
    ImageSavingCallback = args => args.Url = $"./api/blob/get/{args.PageIndex}-{args.ImageIndex}",
    PageSavingCallback = args => args.OutputStream = File.Create($"page-{args.PageIndex}.html"),
});

// One document, everything embedded (pictures as data: URIs).
document.Save("Notes.html");
```

`ImageSavingArgs` carries the picture bytes, format, size, page and image index; set `Url` to
choose the `src`, `OutputStream` to receive the bytes, or `SkipEmbedding` to leave the picture out.
`FileSavingCallback` does the same for attached files. `HtmlWriter.WritePage` and
`HtmlWriter.WriteDocument` are the lower-level entry points, and `HtmlWriter.StyleSheet` is the CSS
for use with `EmbedCss = false`. Ink and other items the document model does not hold are not
written.

## Sections and notebooks

`Document.Guid` is the section file's identity (the header `guidFile`, or the identity the package's header cell
carries), the value a notebook table of contents lists a section under; `Document.NotebookGuid` is the table of
contents file it belongs to. Pages come in the order the section's page series name them.

`NotebookToc.Load` reads a `.onetoc2` file: its GUID, color, `EnableHistory` and the ordered entries (file name,
GUID, color, ordering id). An entry whose name does not end in `.one` is a folder (a section group). Loaded from a
path, a folder's `Children` are read from the table of contents file in its directory, recursively;
`OneNote_RecycleBin` is never entered. A file name the file refers to more than once appears once, as its last
reference states it.

```csharp
var toc = NotebookToc.Load("MyNotebook/.onetoc2");
foreach (var entry in toc.Entries)
{
    Console.WriteLine($"{entry.OrderingId} {entry.FileName} {(entry.IsFolder ? entry.Children.Count + " entries" : "")}");
}
```

## Cancellation

`Document.Load(stream, options, cancellationToken)`, the `Save` overloads that take a token,
`HtmlWriter` and `NotebookToc.Load` check the token while they parse, lay out and write, and
throw `OperationCanceledException`. `HtmlSaveOptions.CancellationToken` does the same for options
passed around. PDF and HTML are written to the stream as they are produced; a path that was
being written is deleted when the save fails.

## Errors

Damaged or truncated input raises `FileCorruptedException`; a file that is not a OneNote section
raises `UnsupportedFileFormatException`. Both derive from `OneNoteException`, as do
`IncorrectPasswordException` (encrypted sections are not supported) and
`UnsupportedSaveFormatException`.

## Limitations

- Reads OneNote 2010 and later section files (`.one`) and notebook table of contents files (`.onetoc2`), both as
  file node lists and as FSSHTTPB packages (a file that starts with the package, or a header whose package follows
  the transaction log). `.onepkg` is not supported. OneNote 2007 sections and encrypted sections are not
  supported, and the library does not write `.one` files.
- Images and attachments stored outside the section (in a `onefiles` folder next to it) are
  listed with empty `Bytes`.
- Ink strokes, equations and page backgrounds are not modelled (recognized handwriting is on `Page.InkRecognition`).
- The PDF export lays content out in reading order rather than at the positions OneNote shows,
  and does not draw TIFF or other picture formats beyond PNG, JPEG, GIF and BMP (their file name
  is printed instead).

## License

MIT. X-Ray.OneNote is a C# port of an MIT-licensed open-source Python OneNote reader; see
[LICENSE](https://github.com/curiosity-ai/x-ray-others/blob/main/XRay.OneNote/LICENSE) and
[THIRD_PARTY_NOTICES.md](https://github.com/curiosity-ai/x-ray-others/blob/main/XRay.OneNote/THIRD_PARTY_NOTICES.md).
The file formats are described in Microsoft's open specifications
[MS-ONESTORE](https://learn.microsoft.com/openspecs/office_file_formats/ms-onestore/) and
[MS-ONE](https://learn.microsoft.com/openspecs/office_file_formats/ms-one/).
