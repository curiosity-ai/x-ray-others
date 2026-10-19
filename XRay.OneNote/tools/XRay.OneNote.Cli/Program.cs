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
//   onenotetool toc <file.onetoc2>           notebook table of contents (nested section groups are read from disk)
//   onenotetool store <file> [--props]       the revision store: object spaces, revisions, objects (diagnostics)
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
            Console.WriteLine($"Guid: {document.Guid}  Notebook: {document.NotebookGuid}  Created: {document.CreationTime:u}  Color: {document.Color}");
            foreach (var page in document.Pages)
            {
                Console.WriteLine($"Page (level {page.Level}): {page.Title?.TitleText?.Text ?? "<untitled>"}");
                if (page.InkRecognition is { } ink)
                {
                    Console.WriteLine($"  Ink: {ink.Text.Replace("\n", " / ")}");
                }
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

        case "store":
        {
            // Diagnostics: the revision store as the parser sees it (object spaces, revisions, objects by type).
            var bytes = File.ReadAllBytes(input);
            if (XRay.OneNote.Internal.Fsshttpb.FsshttpbPackage.IsPackagingAt(bytes, 0))
            {
                var package = XRay.OneNote.Internal.Fsshttpb.FsshttpbPackage.Parse(bytes, 0, default);
                Console.WriteLine($"package schema {package.CellSchema} elements {package.Elements.Count}");
                foreach (var group in package.Elements.Values.GroupBy(e => e.GetType().Name))
                {
                    Console.WriteLine($"  {group.Key} x{group.Count()}");
                }

                foreach (var manifest in package.StorageManifests)
                {
                    foreach (var (root, cell) in manifest.Roots)
                    {
                        Console.WriteLine($"  root {root} -> {cell}");
                    }
                }

                foreach (var index in package.StorageIndexes)
                {
                    foreach (var (cell, mapping) in index.CellMappings)
                    {
                        Console.WriteLine($"  cell {cell} -> {mapping.Mapping}");
                    }

                    Console.WriteLine($"  revision mappings {index.RevisionMappings.Count}");
                }
            }

            XRay.OneNote.Internal.OneStore.OneStoreFile store;
            try
            {
                store = XRay.OneNote.Internal.OneStore.OneStoreParser.Parse(bytes, null);
            }
            catch (UnsupportedFileFormatException)
            {
                store = XRay.OneNote.Internal.OneStore.OneStoreParser.ParseToc(bytes, null);
            }

            Console.WriteLine($"file {store.FileGuid} ancestor {store.AncestorGuid} spaces {store.ObjectSpaces.Count} filedata {store.FileDataStore.Count}");
            foreach (var space in store.ObjectSpaces)
            {
                Console.WriteLine($"space {space.Gosid} revisions {space.Revisions.Count} objects {space.Objects.Count} name {space.SectionDisplayName}");
                foreach (var group in space.Objects.Values.GroupBy(o => o.JcidName).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    Console.WriteLine($"    {group.Key} x{group.Count()}");
                }

                if (args.Contains("--props"))
                {
                    foreach (var obj in space.Objects.Values)
                    {
                        Console.WriteLine($"    obj {obj.Oid} {obj.JcidName}");
                        foreach (var (name, value) in obj.Properties.Raw)
                        {
                            Console.WriteLine($"        {name} {Describe(value)}");
                        }
                    }
                }

                foreach (var revision in space.Revisions)
                {
                    Console.WriteLine($"  rev {revision.Rid} dep {revision.RidDependent} ctx {revision.ContextId} roots {string.Join(",", revision.RootRoles.Select(r => r.Key + "=" + r.Value))} objects {revision.Objects.Count}");
                }
            }

            return 0;
        }

        case "html":
            new Document(input).Save(args.Length > 2 ? args[2] : Path.ChangeExtension(input, ".html"), new HtmlSaveOptions { PerPage = false });
            return 0;
        case "toc":
        {
            var toc = NotebookToc.Load(input);
            Console.WriteLine($"Notebook {toc.Guid} {toc.DisplayName} color={toc.Color} history={toc.EnableHistory}");
            PrintEntries(toc.Entries, 1);
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

static string Describe(XRay.OneNote.Internal.OneStore.PropertyValue value) => value.Kind switch
{
    XRay.OneNote.Internal.OneStore.PropertyKind.Bytes => "bytes[" + value.BytesValue.Length + "] " + Convert.ToHexString(value.BytesValue.AsSpan(0, Math.Min(24, value.BytesValue.Length))),
    XRay.OneNote.Internal.OneStore.PropertyKind.Integer => "int " + value.IntegerValue,
    XRay.OneNote.Internal.OneStore.PropertyKind.Bool => "bool " + value.BoolValue,
    XRay.OneNote.Internal.OneStore.PropertyKind.List => "list[" + string.Join(", ", value.Items.Select(Describe)) + "]",
    XRay.OneNote.Internal.OneStore.PropertyKind.PropertySet => "set{" + string.Join("; ", value.SetValue!.Raw.Select(r => r.Name + "=" + Describe(r.Value))) + "}",
    _ => value.Kind + (value.Kind is XRay.OneNote.Internal.OneStore.PropertyKind.None ? "" : " " + value.ObjectIdValue),
};

static void PrintEntries(IReadOnlyList<NotebookTocEntry> entries, int depth)
{
    foreach (var entry in entries)
    {
        Console.WriteLine($"{new string(' ', depth * 2)}{entry.OrderingId,4} {(entry.IsFolder ? "dir " : "sect")} {entry.Guid} {entry.FileName} color={entry.Color}");
        PrintEntries(entry.Children, depth + 1);
    }
}
