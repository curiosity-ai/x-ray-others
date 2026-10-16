using XRay.OneNote.Internal.Html;

namespace XRay.OneNote.Saving;

/// <summary>
/// The lower-level HTML API: writes a page or a whole document to a stream without going through
/// <see cref="Document.Save(string, SaveOptions)"/>, and exposes the style sheet the output refers to.
/// </summary>
public static class HtmlWriter
{
    /// <summary>The style sheet embedded by default; write it yourself when <see cref="HtmlSaveOptions.EmbedCss"/> is false.</summary>
    public static string StyleSheet => HtmlStyle.Css;

    /// <summary>Writes <paramref name="page"/> as one complete HTML document.</summary>
    /// <param name="page">The page.</param>
    /// <param name="output">Receives UTF-8 HTML. Left open.</param>
    /// <param name="options">Options; <see cref="HtmlSaveOptions.PerPage"/> and the page selection are ignored.</param>
    /// <param name="pageIndex">The page's index in its document, reported to the image callback.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static void WritePage(Page page, Stream output, HtmlSaveOptions? options = null, int pageIndex = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(output);
        var effective = options ?? new HtmlSaveOptions();
        var token = Combine(effective, cancellationToken, out var linked);
        using (linked)
        {
            new HtmlRenderer(effective, token).SavePage(page, pageIndex, output);
        }
    }

    /// <summary>Writes <paramref name="document"/> as one HTML document holding every selected page.</summary>
    /// <param name="document">The document.</param>
    /// <param name="output">Receives UTF-8 HTML. Left open.</param>
    /// <param name="options">Options; <see cref="HtmlSaveOptions.PerPage"/> is ignored.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static void WriteDocument(Document document, Stream output, HtmlSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        var effective = options ?? new HtmlSaveOptions();
        var token = Combine(effective, cancellationToken, out var linked);
        using (linked)
        {
            var single = new HtmlSaveOptions
            {
                PerPage = false,
                EmbedCss = effective.EmbedCss,
                StyleSheetUrl = effective.StyleSheetUrl,
                EmbedImages = effective.EmbedImages,
                EmbedAttachments = effective.EmbedAttachments,
                ShowHiddenText = effective.ShowHiddenText,
                ImageSavingCallback = effective.ImageSavingCallback,
                FileSavingCallback = effective.FileSavingCallback,
                Language = effective.Language,
                PageIndex = effective.PageIndex,
                PageCount = effective.PageCount,
            };
            new HtmlRenderer(single, token).SaveDocument(document, output, null);
        }
    }

    internal static CancellationToken Combine(SaveOptions options, CancellationToken explicitToken, out IDisposable? linked)
    {
        var fromOptions = options.CancellationToken;
        if (!fromOptions.CanBeCanceled)
        {
            linked = null;
            return explicitToken;
        }

        if (!explicitToken.CanBeCanceled || explicitToken == fromOptions)
        {
            linked = null;
            return fromOptions;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(fromOptions, explicitToken);
        linked = source;
        return source.Token;
    }

    internal static string ExtensionFor(string format) => ImageFormats.Extension(format);

    internal static string MimeTypeFor(string format) => ImageFormats.MimeType(format);
}
