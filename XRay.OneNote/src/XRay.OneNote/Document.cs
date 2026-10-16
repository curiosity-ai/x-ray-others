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
    {
        ArgumentNullException.ThrowIfNull(path);
        loadOptions ??= new LoadOptions();
        RejectPassword(loadOptions);
        var data = File.ReadAllBytes(path);
        Load(data, Path.GetFileNameWithoutExtension(path), loadOptions);
    }

    /// <summary>Loads a section from the remaining contents of <paramref name="stream"/>.</summary>
    /// <exception cref="FileCorruptedException">The data is truncated or damaged.</exception>
    /// <exception cref="UnsupportedFileFormatException">The data is not a OneNote section.</exception>
    /// <exception cref="IncorrectPasswordException">A password was supplied; encrypted sections are not supported.</exception>
    public Document(Stream stream, LoadOptions? loadOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        loadOptions ??= new LoadOptions();
        RejectPassword(loadOptions);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Load(buffer.ToArray(), null, loadOptions);
    }

    /// <summary>
    /// The section name: the name stored in the section, else the file name without extension, else
    /// the first page title.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>When the section was created, when known.</summary>
    public DateTime? CreationTime { get; set; }

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

    /// <summary>Saves the document to <paramref name="path"/>; the format follows the extension (.pdf).</summary>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedSaveFormatException("Only .pdf file targets are supported for save operations");
        }

        Save(path, new PdfSaveOptions());
    }

    /// <summary>Saves the document to <paramref name="path"/> in <paramref name="format"/>.</summary>
    public void Save(string path, SaveFormat format) => Save(path, OptionsFor(format));

    /// <summary>Saves the document to <paramref name="path"/> with <paramref name="options"/>.</summary>
    public void Save(string path, SaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, Render(options));
    }

    /// <summary>Saves the document to <paramref name="stream"/> in <paramref name="format"/> (PDF by default).</summary>
    public void Save(Stream stream, SaveFormat format = SaveFormat.Pdf) => Save(stream, OptionsFor(format));

    /// <summary>Saves the document to <paramref name="stream"/> with <paramref name="options"/>.</summary>
    public void Save(Stream stream, SaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(Render(options));
    }

    private protected override void AcceptCore(DocumentVisitor visitor)
    {
        visitor.VisitDocumentStart(this);
        AcceptChildren(visitor);
        visitor.VisitDocumentEnd(this);
    }

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new Document { DisplayName = DisplayName, CreationTime = CreationTime };
        context.Register(this, copy);
        CloneChildrenInto(copy, context);
        return copy;
    }

    private static SaveOptions OptionsFor(SaveFormat format) => format switch
    {
        SaveFormat.Pdf => new PdfSaveOptions(),
        _ => throw new UnsupportedSaveFormatException("Only PDF save is supported"),
    };

    private static void RejectPassword(LoadOptions loadOptions)
    {
        if (!string.IsNullOrEmpty(loadOptions.DocumentPassword))
        {
            throw new IncorrectPasswordException("Password-protected documents are not supported");
        }
    }

    private byte[] Render(SaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SaveFormat != SaveFormat.Pdf || options is not PdfSaveOptions pdfOptions)
        {
            throw new UnsupportedSaveFormatException("Only PDF save is supported");
        }

        return Internal.Pdf.PdfExporter.Export(this, pdfOptions);
    }

    private void Load(byte[] data, string? displayName, LoadOptions loadOptions)
    {
        var loaded = OneNoteLoader.Load(data, displayName, loadOptions.LoadHistory);
        DisplayName = loaded.DisplayName;
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
