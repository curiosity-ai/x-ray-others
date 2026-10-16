using System.Text.Encodings.Web;
using System.Text.Json;
using XRay.OneNote;
using XRay.OneNote.Saving;
using XRay.OneNote.Tools;

// onenotetool: inspection and manual checks for XRay.OneNote.
//   onenotetool info <file.one>              section name and page titles
//   onenotetool text <file.one>              every paragraph, one per line
//   onenotetool dump <file.one> [--history]  the DOM as JSON (the parity schema of tools/Shared/DomJson.cs)
//   onenotetool pdf <file.one> <out.pdf>     PDF export
//   onenotetool html <file.one> <out.html>   HTML export (one document, everything embedded)
//   onenotetool toc <file.onetoc2>           notebook table of contents (best effort)
//   onenotetool pdf-dir <dir> <out-dir>      PDF export of every .one in a directory (for regenerate_goldens.py --compare-pdfs)
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: onenotetool info|text|dump|pdf|html|toc|pdf-dir <file.one|dir> [out] [--history]");
    return 2;
}

var command = args[0];
var input = args[1];
try
{
    switch (command)
    {
        case "info":
        {
            var document = new Document(input);
            Console.WriteLine($"Display name: {document.DisplayName}");
            Console.WriteLine($"Guid: {document.Guid}  Notebook: {document.NotebookGuid}  Created: {document.CreationTime:u}");
            foreach (var page in document.Pages)
            {
                Console.WriteLine($"Page (level {page.Level}): {page.Title?.TitleText?.Text ?? "<untitled>"}");
            }

            return 0;
        }

        case "text":
            Console.WriteLine(DomJson.PlainText(new Document(input)));
            return 0;
        case "dump":
        {
            var history = args.Contains("--history") ? new Document(input, new LoadOptions { LoadHistory = true }) : null;
            var json = DomJson.DumpDocument(Path.GetFileName(input), new Document(input), history);
            Console.WriteLine(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }

        case "html":
            new Document(input).Save(args.Length > 2 ? args[2] : Path.ChangeExtension(input, ".html"), new HtmlSaveOptions { PerPage = false });
            return 0;
        case "toc":
        {
            var toc = NotebookToc.Load(input);
            Console.WriteLine($"Notebook {toc.Guid} {toc.DisplayName}");
            foreach (var entry in toc.Entries)
            {
                Console.WriteLine($"{entry.OrderingId,4} {(entry.IsFolder ? "dir " : "sect")} {entry.Guid} {entry.FileName}");
            }

            return 0;
        }

        case "pdf":
            new Document(input).Save(args.Length > 2 ? args[2] : Path.ChangeExtension(input, ".pdf"), new PdfSaveOptions());
            return 0;
        case "pdf-dir":
        {
            var output = args.Length > 2 ? args[2] : "test-output/pdf";
            Directory.CreateDirectory(output);
            foreach (var file in Directory.EnumerateFiles(input, "*.one").Order(StringComparer.Ordinal))
            {
                new Document(file).Save(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".pdf"), new PdfSaveOptions());
                Console.WriteLine($"wrote {Path.GetFileNameWithoutExtension(file)}.pdf");
            }

            return 0;
        }

        default:
            Console.Error.WriteLine($"unknown command {command}");
            return 2;
    }
}
catch (OneNoteException exception)
{
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    return 1;
}
