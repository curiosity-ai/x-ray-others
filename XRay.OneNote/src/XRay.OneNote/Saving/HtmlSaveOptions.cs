namespace XRay.OneNote.Saving;

/// <summary>Called for every picture of a page just before it is written; see <see cref="ImageSavingArgs"/>.</summary>
public delegate void ImageSavingCallback(ImageSavingArgs args);

/// <summary>Called for every attached file of a page just before it is written; see <see cref="FileSavingArgs"/>.</summary>
public delegate void FileSavingCallback(FileSavingArgs args);

/// <summary>Called for every HTML document (one per page, or one for the section) before it is written; see <see cref="PageSavingArgs"/>.</summary>
public delegate void PageSavingCallback(PageSavingArgs args);

/// <summary>
/// Options for saving to HTML 5. The output is UTF-8, contains no script, and is deterministic: the
/// same document and options always produce the same bytes. Class names are stable (all start with
/// <c>on-</c>) and the style sheet is the one in <see cref="HtmlWriter.StyleSheet"/>.
/// </summary>
public sealed class HtmlSaveOptions : SaveOptions
{
    private bool _exportFonts;

    /// <summary>Creates HTML save options: one document per page, CSS and pictures embedded.</summary>
    public HtmlSaveOptions()
        : base(SaveFormat.Html)
    {
    }

    /// <summary>
    /// Whether every page becomes its own HTML document (the default) or the whole section one document
    /// with a <c>&lt;article class="on-page"&gt;</c> per page. With one document per page and more than
    /// one page, a <see cref="PageSavingCallback"/> (or a path to save to) says where each goes.
    /// </summary>
    public bool PerPage { get; set; } = true;

    /// <summary>Whether the style sheet is written into a <c>&lt;style&gt;</c> element (the default) or referenced through <see cref="StyleSheetUrl"/>.</summary>
    public bool EmbedCss { get; set; } = true;

    /// <summary>The URL of the style sheet when <see cref="EmbedCss"/> is false.</summary>
    public string StyleSheetUrl { get; set; } = "onenote.css";

    /// <summary>
    /// Whether pictures are written as <c>data:</c> URIs (the default). A URL chosen by the
    /// <see cref="ImageSavingCallback"/> takes precedence. With neither, the <c>src</c> is the generated
    /// <see cref="ImageSavingArgs.SuggestedFileName"/> and the bytes are not exported.
    /// </summary>
    public bool EmbedImages { get; set; } = true;

    /// <summary>Whether attached files are written as <c>data:</c> links. Off by default: attachments can be large.</summary>
    public bool EmbedAttachments { get; set; }

    /// <summary>
    /// Whether runs formatted as hidden text are shown. When false they stay in the markup, with the
    /// class <c>on-hidden</c> that the style sheet hides.
    /// </summary>
    public bool ShowHiddenText { get; set; }

    /// <summary>Font files are never exported; only <c>false</c> is accepted. The property exists so option sets can be carried over.</summary>
    /// <exception cref="NotSupportedException">Set to true.</exception>
    public bool ExportFonts
    {
        get => _exportFonts;
        set => _exportFonts = value ? throw new NotSupportedException("Exporting font files is not supported.") : false;
    }

    /// <summary>Chooses the URL (or the output stream) of each picture. Null embeds or names them according to <see cref="EmbedImages"/>.</summary>
    public ImageSavingCallback? ImageSavingCallback { get; set; }

    /// <summary>Chooses the URL of each attached file, and where its bytes go. Null writes the name without a link unless <see cref="EmbedAttachments"/> is set.</summary>
    public FileSavingCallback? FileSavingCallback { get; set; }

    /// <summary>Supplies the output stream of each HTML document.</summary>
    public PageSavingCallback? PageSavingCallback { get; set; }

    /// <summary>The language written to <c>&lt;html lang&gt;</c>. Null omits the attribute.</summary>
    public string? Language { get; set; }
}

/// <summary>
/// A picture about to be written. Set <see cref="Url"/> to choose what goes into <c>src</c>, and
/// <see cref="OutputStream"/> to receive the picture's bytes; set <see cref="SkipEmbedding"/> to leave the
/// bytes out of the document.
/// </summary>
public sealed class ImageSavingArgs
{
    internal ImageSavingArgs(Image image, byte[] data, string format, string suggestedFileName, int pageIndex, int imageIndex, int globalImageIndex)
    {
        Image = image;
        Data = data;
        Format = format;
        SuggestedFileName = suggestedFileName;
        FileName = string.IsNullOrWhiteSpace(image.FileName) ? suggestedFileName : image.FileName!;
        PageIndex = pageIndex;
        ImageIndex = imageIndex;
        GlobalImageIndex = globalImageIndex;
    }

    /// <summary>The picture node.</summary>
    public Image Image { get; }

    /// <summary>The encoded picture bytes. Do not modify.</summary>
    public byte[] Data { get; }

    /// <summary>The picture size in bytes.</summary>
    public long Size => Data.Length;

    /// <summary>The detected format: <c>png</c>, <c>jpeg</c>, <c>gif</c>, <c>bmp</c>, <c>tiff</c>, <c>emf</c>, <c>wmf</c> or <c>bin</c>.</summary>
    public string Format { get; }

    /// <summary>The file extension for <see cref="Format"/>, without the dot (<c>jpg</c> for jpeg).</summary>
    public string Extension => HtmlWriter.ExtensionFor(Format);

    /// <summary>The media type for <see cref="Format"/>.</summary>
    public string MimeType => HtmlWriter.MimeTypeFor(Format);

    /// <summary>The picture's own file name when it has one, else <see cref="SuggestedFileName"/>.</summary>
    public string FileName { get; }

    /// <summary>A generated name, <c>img-&lt;GlobalImageIndex&gt;.&lt;Extension&gt;</c>.</summary>
    public string SuggestedFileName { get; }

    /// <summary>The zero-based index of the page the picture is on, within the document.</summary>
    public int PageIndex { get; }

    /// <summary>The zero-based index of the picture on its page.</summary>
    public int ImageIndex { get; }

    /// <summary>The zero-based index of the picture among all those written by this save.</summary>
    public int GlobalImageIndex { get; }

    /// <summary>What to write into <c>src</c>. Null leaves the decision to <see cref="HtmlSaveOptions.EmbedImages"/>.</summary>
    public string? Url { get; set; }

    /// <summary>Where to write the picture's bytes, when the caller wants them exported. Closed afterwards unless <see cref="KeepStreamOpen"/> is set.</summary>
    public Stream? OutputStream { get; set; }

    /// <summary>Whether <see cref="OutputStream"/> stays open after the bytes are written. False by default.</summary>
    public bool KeepStreamOpen { get; set; }

    /// <summary>
    /// When true and <see cref="Url"/> is not set, the picture is not embedded: the document gets a
    /// placeholder element (<c>on-image-skipped</c>) with the alternative text instead of an <c>&lt;img&gt;</c>.
    /// </summary>
    public bool SkipEmbedding { get; set; }
}

/// <summary>An attached file about to be written.</summary>
public sealed class FileSavingArgs
{
    internal FileSavingArgs(AttachedFile file, string fileName, int pageIndex, int fileIndex)
    {
        File = file;
        FileName = fileName;
        PageIndex = pageIndex;
        FileIndex = fileIndex;
    }

    /// <summary>The attachment node.</summary>
    public AttachedFile File { get; }

    /// <summary>The attachment's file name (a generated one when it has none).</summary>
    public string FileName { get; }

    /// <summary>The attachment size in bytes.</summary>
    public long Size => File.Bytes.Length;

    /// <summary>The zero-based index of the page the attachment is on, within the document.</summary>
    public int PageIndex { get; }

    /// <summary>The zero-based index of the attachment on its page.</summary>
    public int FileIndex { get; }

    /// <summary>The <c>href</c> of the download link. Null writes the name without a link (or a <c>data:</c> link with <see cref="HtmlSaveOptions.EmbedAttachments"/>).</summary>
    public string? Url { get; set; }

    /// <summary>Where to write the attachment's bytes, when the caller wants them exported. Closed afterwards unless <see cref="KeepStreamOpen"/> is set.</summary>
    public Stream? OutputStream { get; set; }

    /// <summary>Whether <see cref="OutputStream"/> stays open after the bytes are written. False by default.</summary>
    public bool KeepStreamOpen { get; set; }
}

/// <summary>An HTML document about to be written. <see cref="OutputStream"/> says where it goes.</summary>
public sealed class PageSavingArgs
{
    internal PageSavingArgs(int pageIndex, Page? page, string? title, int level, Stream? defaultStream)
    {
        PageIndex = pageIndex;
        Page = page;
        Title = title;
        Level = level;
        OutputStream = defaultStream;
    }

    /// <summary>The zero-based index of the page within the document, or -1 for a document holding every page.</summary>
    public int PageIndex { get; }

    /// <summary>The page, or null for a document holding every page.</summary>
    public Page? Page { get; }

    /// <summary>The page title text, or the section name for a document holding every page.</summary>
    public string? Title { get; }

    /// <summary>The page level: 1 for a page, 2 and deeper for subpages; 0 for a document holding every page.</summary>
    public int Level { get; }

    /// <summary>
    /// Where the HTML is written. It starts as the stream or file the save was given, if any; assign a
    /// stream (a temporary file, say) to take the page elsewhere. Closed afterwards unless
    /// <see cref="KeepStreamOpen"/> is set or it is the stream the caller passed to <c>Save</c>.
    /// </summary>
    public Stream? OutputStream { get; set; }

    /// <summary>Whether a stream assigned by the callback stays open after the page is written. False by default.</summary>
    public bool KeepStreamOpen { get; set; }
}
