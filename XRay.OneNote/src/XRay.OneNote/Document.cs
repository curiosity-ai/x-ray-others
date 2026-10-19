using XRay.OneNote.Internal.Html;
using XRay.OneNote.Internal.MsOne;
using XRay.OneNote.Saving;

namespace XRay.OneNote;

/// <summary>
/// A OneNote section: the root of the document tree. Its children are the <see cref="Page"/>s in
/// section order.
/// </summary>
public sealed class Document : CompositeNode
{
    private readonly Dictionary<Page, PageHistory> _pageHistories = new(ReferenceEqualityComparer.Instance);

    /// <summary>Creates an empty document.</summary>
    public Document()
    {
    }

    /// <summary>Loads a section file (.one) from <paramref name="path"/>.</summary>
    /// <exception cref="FileCorruptedException">The file is truncated or damaged.</exception>
    /// <exception cref="UnsupportedFileFormatException">The file is not a OneNote section.</exception>
    /// <exception cref="IncorrectPasswordException">A password was supplied; encrypted sections are not supported.</exception>
    public Document(string path, LoadOptions? loadOptions = null)
        : this(path, loadOptions, CancellationToken.None)
    {
    }

    /// <summary>Loads a section from the remaining contents of <paramref name="stream"/>.</summary>
    /// <exception cref="FileCorruptedException">The data is truncated or damaged.</exception>
    /// <exception cref="UnsupportedFileFormatException">The data is not a OneNote section.</exception>
    /// <exception cref="IncorrectPasswordException">A password was supplied; encrypted sections are not supported.</exception>
    public Document(Stream stream, LoadOptions? loadOptions = null)
        : this(stream, loadOptions, CancellationToken.None)
    {
    }

    private Document(string path, LoadOptions? loadOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        loadOptions ??= new LoadOptions();
        RejectPassword(loadOptions);
        cancellationToken.ThrowIfCancellationRequested();
        var data = File.ReadAllBytes(path);
        Load(data, Path.GetFileNameWithoutExtension(path), loadOptions, cancellationToken);
    }

    private Document(Stream stream, LoadOptions? loadOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        loadOptions ??= new LoadOptions();
        RejectPassword(loadOptions);
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer, 81920);
        cancellationToken.ThrowIfCancellationRequested();
        Load(buffer.ToArray(), null, loadOptions, cancellationToken);
    }

    /// <summary>
    /// Loads a section from the remaining contents of <paramref name="stream"/>, checking
    /// <paramref name="cancellationToken"/> while it parses.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    /// <exception cref="FileCorruptedException">The data is truncated or damaged.</exception>
    /// <exception cref="UnsupportedFileFormatException">The data is not a OneNote section.</exception>
    public static Document Load(Stream stream, LoadOptions? loadOptions = null, CancellationToken cancellationToken = default) =>
        new(stream, loadOptions, cancellationToken);

    /// <summary>Loads a section file (.one) from <paramref name="path"/>, checking <paramref name="cancellationToken"/> while it parses.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    /// <exception cref="FileCorruptedException">The file is truncated or damaged.</exception>
    /// <exception cref="UnsupportedFileFormatException">The file is not a OneNote section.</exception>
    public static Document Load(string path, LoadOptions? loadOptions = null, CancellationToken cancellationToken = default) =>
        new(path, loadOptions, cancellationToken);

    /// <summary>
    /// The section name: the name stored in the section, else the file name without extension, else
    /// the first page title.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The section's identity: the <c>guidFile</c> of its file header. A notebook table of contents
    /// (<see cref="NotebookToc"/>) lists every section under this GUID (its <c>FileIdentityGuid</c>), so it is what
    /// matches a section to a notebook entry. <see cref="System.Guid.Empty"/> for a document that was not loaded from
    /// a file. Copies of a file share it.
    /// </summary>
    public Guid Guid { get; set; }

    /// <summary>
    /// The GUID of the notebook's table of contents file (the header's <c>guidAncestor</c>), which equals
    /// <see cref="NotebookToc.Guid"/> of the .onetoc2 the section belongs to; empty when the section names none.
    /// </summary>
    public Guid NotebookGuid { get; set; }

    /// <summary>The <c>NotebookManagementEntityGuid</c> of the section node, when it has one.</summary>
    public Guid? EntityGuid { get; set; }

    /// <summary>When the section was created (UTC): the topology creation time of its section node, when stored.</summary>
    public DateTime? CreationTime { get; set; }

    /// <summary>The section's color as a COLORREF (0x00BBGGRR), when it has one (its section metadata's color).</summary>
    public uint? Color { get; set; }

    /// <summary>The format the document was read from.</summary>
    public FileFormat FileFormat => FileFormat.OneNote2010;

    /// <summary>The pages of the document, in order.</summary>
    public IEnumerable<Page> Pages => this.OfType<Page>();

    /// <summary>
    /// The history of <paramref name="page"/>: earlier versions, oldest first, and the current version.
    /// Earlier versions are only present when the document was loaded with <see cref="LoadOptions.LoadHistory"/>.
    /// </summary>
    public PageHistory GetPageHistory(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return _pageHistories.TryGetValue(page, out var history) ? history : new PageHistory(page);
    }

    /// <summary>Saves the document to <paramref name="path"/>; the format follows the extension (.pdf, .html or .htm).</summary>
    public void Save(string path) => Save(path, CancellationToken.None);

    /// <summary>Saves the document to <paramref name="path"/>; the format follows the extension (.pdf, .html or .htm).</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; no partial file is left behind.</exception>
    public void Save(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            Save(path, new PdfSaveOptions(), cancellationToken);
        }
        else if (string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".htm", StringComparison.OrdinalIgnoreCase))
        {
            Save(path, new HtmlSaveOptions { PerPage = false }, cancellationToken);
        }
        else
        {
            throw new UnsupportedSaveFormatException("Only .pdf and .html file targets are supported for save operations");
        }
    }

    /// <summary>Saves the document to <paramref name="path"/> in <paramref name="format"/>.</summary>
    public void Save(string path, SaveFormat format) => Save(path, OptionsFor(format), CancellationToken.None);

    /// <summary>Saves the document to <paramref name="path"/> in <paramref name="format"/>.</summary>
    public void Save(string path, SaveFormat format, CancellationToken cancellationToken) => Save(path, OptionsFor(format), cancellationToken);

    /// <summary>Saves the document to <paramref name="path"/> with <paramref name="options"/>.</summary>
    public void Save(string path, SaveOptions options) => Save(path, options, CancellationToken.None);

    /// <summary>
    /// Saves the document to <paramref name="path"/> with <paramref name="options"/>. For HTML with one
    /// document per page and several pages, the pages go to <c>name_000.html</c>, <c>name_001.html</c>, ... beside
    /// <paramref name="path"/> unless a <see cref="HtmlSaveOptions.PageSavingCallback"/> says otherwise.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; no partial file is left behind.</exception>
    public void Save(string path, SaveOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        var created = new List<string>();
        try
        {
            if (options is HtmlSaveOptions html)
            {
                SaveHtmlToPath(path, html, created, cancellationToken);
            }
            else
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                created.Add(path);
                SaveToStream(stream, options, cancellationToken);
            }
        }
        catch
        {
            foreach (var file in created)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Best effort: the original failure is the one to report.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            throw;
        }
    }

    /// <summary>Saves the document to <paramref name="stream"/> in <paramref name="format"/> (PDF by default).</summary>
    public void Save(Stream stream, SaveFormat format = SaveFormat.Pdf) => Save(stream, OptionsFor(format), CancellationToken.None);

    /// <summary>Saves the document to <paramref name="stream"/> in <paramref name="format"/>.</summary>
    public void Save(Stream stream, SaveFormat format, CancellationToken cancellationToken) => Save(stream, OptionsFor(format), cancellationToken);

    /// <summary>Saves the document to <paramref name="stream"/> with <paramref name="options"/>.</summary>
    public void Save(Stream stream, SaveOptions options) => Save(stream, options, CancellationToken.None);

    /// <summary>
    /// Saves the document to <paramref name="stream"/> with <paramref name="options"/>, which is left open. PDF is
    /// written straight to the stream. HTML with one document per page and several pages needs a
    /// <see cref="HtmlSaveOptions.PageSavingCallback"/> that supplies each page's stream.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled; the stream may hold partial output.</exception>
    public void Save(Stream stream, SaveOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        SaveToStream(stream, options, cancellationToken);
    }

    private protected override void AcceptCore(DocumentVisitor visitor)
    {
        visitor.VisitDocumentStart(this);
        AcceptChildren(visitor);
        visitor.VisitDocumentEnd(this);
    }

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new Document { DisplayName = DisplayName, CreationTime = CreationTime, Guid = Guid, NotebookGuid = NotebookGuid, EntityGuid = EntityGuid, Color = Color };
        context.Register(this, copy);
        CloneChildrenInto(copy, context);
        return copy;
    }

    private static SaveOptions OptionsFor(SaveFormat format) => format switch
    {
        SaveFormat.Pdf => new PdfSaveOptions(),
        SaveFormat.Html => new HtmlSaveOptions { PerPage = false },
        _ => throw new UnsupportedSaveFormatException("Only PDF and HTML save are supported"),
    };

    private static void RejectPassword(LoadOptions loadOptions)
    {
        if (!string.IsNullOrEmpty(loadOptions.DocumentPassword))
        {
            throw new IncorrectPasswordException("Password-protected documents are not supported");
        }
    }

    private void SaveToStream(Stream stream, SaveOptions options, CancellationToken cancellationToken)
    {
        switch (options)
        {
            case PdfSaveOptions pdf:
            {
                var token = HtmlWriter.Combine(pdf, cancellationToken, out var linked);
                using (linked)
                {
                    Internal.Pdf.PdfExporter.Export(this, pdf, stream, token);
                }

                break;
            }

            case HtmlSaveOptions html:
            {
                var token = HtmlWriter.Combine(html, cancellationToken, out var linked);
                using (linked)
                {
                    new HtmlRenderer(html, token).SaveDocument(this, stream, null);
                }

                break;
            }

            default:
                throw new UnsupportedSaveFormatException("Only PDF and HTML save are supported");
        }
    }

    private void SaveHtmlToPath(string path, HtmlSaveOptions options, List<string> created, CancellationToken cancellationToken)
    {
        var token = HtmlWriter.Combine(options, cancellationToken, out var linked);
        using (linked)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            Stream CreateFile(string file)
            {
                var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
                created.Add(file);
                return stream;
            }

            Stream StreamFor(int index, int count) => count <= 1
                ? CreateFile(path)
                : CreateFile(Path.Combine(directory, name + "_" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + extension));

            new HtmlRenderer(options, token).SaveDocument(this, null, options.PerPage ? StreamFor : (_, _) => CreateFile(path));
            if (!options.EmbedCss && options.PageSavingCallback is null && IsPlainRelativeFileName(options.StyleSheetUrl))
            {
                var cssPath = Path.Combine(directory, options.StyleSheetUrl);
                File.WriteAllText(cssPath, HtmlWriter.StyleSheet + "\n", new System.Text.UTF8Encoding(false));
                created.Add(cssPath);
            }
        }
    }

    private static bool IsPlainRelativeFileName(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url == Path.GetFileName(url) && url is not ("." or "..") && !url.Contains(':', StringComparison.Ordinal);

    private void Load(byte[] data, string? displayName, LoadOptions loadOptions, CancellationToken cancellationToken)
    {
        var loaded = OneNoteLoader.Load(data, displayName, loadOptions.LoadHistory, cancellationToken);
        DisplayName = loaded.DisplayName;
        Guid = loaded.FileGuid;
        NotebookGuid = loaded.AncestorGuid;
        EntityGuid = loaded.EntityGuid;
        CreationTime = loaded.CreationTime;
        Color = loaded.Color;
        for (var index = 0; index < loaded.Pages.Count; index++)
        {
            var page = loaded.Pages[index];
            AppendChildLast(page);
            var ordered = index < loaded.PageHistories.Count && loaded.PageHistories[index].Count > 0
                ? loaded.PageHistories[index]
                : [page];
            var history = new PageHistory(ordered[^1]);
            history.AddRange(ordered.Take(ordered.Count - 1));
            _pageHistories[page] = history;
        }
    }
}
