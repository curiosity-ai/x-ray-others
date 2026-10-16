# X-Ray.OneNote

![X-Ray](https://raw.githubusercontent.com/curiosity-ai/x-ray-others/main/assets/logo.png)

X-Ray.OneNote reads Microsoft OneNote section files (`.one`) in pure managed C#. It parses the
MS-ONESTORE revision store and the MS-ONE object model into a document tree of pages, outlines,
formatted rich text, images, tables, attached files, note tags and numbered lists, reconstructs
page history, and exports pages to PDF. It has no NuGet dependencies and no native code, and does
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
| `Document` | the section: `DisplayName`, pages in section order (subpages included) |
| `Page` | `Title`, `Author`, `CreationTime`, `LastModifiedTime`, `Level` |
| `Title` | `TitleText`, `TitleDate`, `TitleTime` (each a `RichText`) |
| `Outline` | a note container: offsets and maximum width, holding `OutlineElement`s |
| `OutlineElement` | one paragraph level; nested elements form indented levels; `NumberList` for list items |
| `RichText` | `Text`, `TextRuns` (each with a `TextStyle`: font, size, color, highlight, bold, italic, underline, strikethrough, super/subscript, hyperlink, language), `ParagraphStyle`, `Alignment`, `Tags` |
| `Image` | `Bytes`, `FileName`, size, `Alignment`, alternative text, hyperlink, `Tags` |
| `Table`, `TableRow`, `TableCell` | rows of cells holding outline content; `Columns` with widths; `Tags` |
| `AttachedFile` | `Bytes`, `FileName`, `Tags` |
| `NoteTag` | `Label`, `Icon` (shape), `Status`, colors, creation and completion times |

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
hyperlinks, note tag glyphs, images (PNG, JPEG, GIF and BMP), attached file names and tables
split across pages. Text in Latin-1 uses the PDF standard fonts. Text outside Latin-1 (Cyrillic,
for example) is drawn with a TrueType font that is embedded as a subset; the library looks for one
in the usual Windows, Linux and macOS font directories. To control where fonts come from:

```csharp
var fonts = new FontsSubsystem { SearchSystemFontDirectories = false };
fonts.FontDirectories.Add("/path/to/fonts");   // e.g. DejaVuSans.ttf, LiberationSans-Regular.ttf, arial.ttf
document.Save("Notes.pdf", new PdfSaveOptions { FontsSubsystem = fonts });
```

When no suitable font is found, characters the standard fonts cannot show are written as `?`.

## Errors

Damaged or truncated input raises `FileCorruptedException`; a file that is not a OneNote section
raises `UnsupportedFileFormatException`. Both derive from `OneNoteException`, as do
`IncorrectPasswordException` (encrypted sections are not supported) and
`UnsupportedSaveFormatException`.

## Limitations

- Reads OneNote 2010 and later section files (`.one`). Notebook table of contents files
  (`.onetoc2`), OneNote 2007 sections, encrypted sections and the OneNote web packaging are not
  supported, and the library does not write `.one` files.
- Images and attachments stored outside the section (in a `onefiles` folder next to it) are
  listed with empty `Bytes`.
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
