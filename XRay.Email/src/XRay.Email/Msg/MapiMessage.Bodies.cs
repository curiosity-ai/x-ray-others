using XRay.Email.Msg.Html;

namespace XRay.Email.Msg;

/// <summary>Which stored body a message's text or HTML is taken from.</summary>
public enum MessageBodyKind
{
    /// <summary>The message has no body with content.</summary>
    None = 0,

    /// <summary>The plain text body (PR_BODY, or the first text/plain part of an EML).</summary>
    PlainText = 1,

    /// <summary>The HTML body (PR_HTML, or the first text/html part of an EML).</summary>
    Html = 2,

    /// <summary>The RTF body (PR_RTF_COMPRESSED), used when there is no HTML body.</summary>
    Rtf = 3,
}

public sealed partial class MapiMessage
{
    /// <summary>
    /// Which body the preview and the "best body" methods use: HTML, else RTF, else plain text. An RTF
    /// that only wraps plain text yields to the plain text body, which keeps its exact whitespace.
    /// </summary>
    public MessageBodyKind GetPreferredBodyKind(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ResolveBody(cancellationToken).Kind;
    }

    /// <summary>
    /// The decompressed RTF body (PR_RTF_COMPRESSED, MS-OXRTFCP), or null. Same as <see cref="RtfBody"/>
    /// with cancellation for a large body.
    /// </summary>
    public string? GetRtfBody(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Properties.Get(RtfCompressedPropertyId, (ushort)PropertyTypeCode.PtypBinary)?.Value is not byte[] compressed)
        {
            return null;
        }

        return CompressedRtf.TryDecompress(compressed, cancellationToken) is { } rtf ? System.Text.Encoding.Latin1.GetString(rtf) : null;
    }

    /// <summary>
    /// The body as plain text (CRLF line breaks). The plain text body when there is one; otherwise it
    /// is derived from the HTML body (tags removed, character references decoded, paragraphs and
    /// line breaks kept, list items bulleted, table cells tab-separated), otherwise from the RTF body.
    /// Null when the message has no body.
    /// </summary>
    public string? GetBestTextBody(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(Body))
        {
            return Body;
        }

        var html = HtmlBody;
        if (!string.IsNullOrWhiteSpace(html))
        {
            var text = HtmlText.ToText(html, cancellationToken);
            if (text.Length > 0)
            {
                return text;
            }
        }

        var rtf = GetRtfBody(cancellationToken);
        if (!string.IsNullOrWhiteSpace(rtf))
        {
            var text = RtfConverter.IsEncapsulatedHtml(rtf)
                ? HtmlText.ToText(RtfConverter.DeencapsulateHtml(rtf, cancellationToken), cancellationToken)
                : RtfConverter.ToText(rtf, cancellationToken);
            if (text.Length > 0)
            {
                return text;
            }
        }

        return Body;
    }

    /// <summary>
    /// The body as HTML, unmodified: the HTML body as stored; else the HTML recovered from an
    /// encapsulated-HTML RTF (MS-OXRTFEX) or rendered from a plain RTF; else the plain text body in a
    /// whitespace-preserving <c>pre</c> element. Null when the message has no body. Nothing is
    /// sanitised here; <see cref="ToHtml(MailHtmlOptions?, CancellationToken)"/> is the safe form.
    /// </summary>
    public string? GetBestHtmlBody(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = ResolveBody(cancellationToken);
        return body.Kind switch
        {
            MessageBodyKind.Html or MessageBodyKind.Rtf => body.Html,
            MessageBodyKind.PlainText => "<pre style=\"white-space:pre-wrap;word-wrap:break-word;margin:0\">" + HtmlText.Escape(body.Text!) + "</pre>",
            _ => null,
        };
    }

    internal readonly record struct ResolvedBody(MessageBodyKind Kind, string? Html, string? Text);

    internal ResolvedBody ResolveBody(CancellationToken cancellationToken)
    {
        var html = HtmlBody;
        if (!string.IsNullOrWhiteSpace(html))
        {
            return new ResolvedBody(MessageBodyKind.Html, html, null);
        }

        var rtf = GetRtfBody(cancellationToken);
        if (!string.IsNullOrWhiteSpace(rtf))
        {
            if (RtfConverter.IsEncapsulatedHtml(rtf))
            {
                var recovered = RtfConverter.DeencapsulateHtml(rtf, cancellationToken);
                if (HasVisibleContent(recovered, cancellationToken))
                {
                    return new ResolvedBody(MessageBodyKind.Rtf, recovered, null);
                }
            }
            else if (RtfConverter.IsEncapsulatedText(rtf) && !string.IsNullOrWhiteSpace(Body))
            {
                return new ResolvedBody(MessageBodyKind.PlainText, null, Body);
            }
            else
            {
                var rendered = RtfConverter.ToHtml(rtf, cancellationToken);
                if (HasVisibleContent(rendered, cancellationToken))
                {
                    return new ResolvedBody(MessageBodyKind.Rtf, rendered, null);
                }
            }
        }

        var text = Body;
        return !string.IsNullOrWhiteSpace(text)
            ? new ResolvedBody(MessageBodyKind.PlainText, null, text)
            : new ResolvedBody(MessageBodyKind.None, null, null);
    }

    private static bool HasVisibleContent(string html, CancellationToken cancellationToken) =>
        html.Contains("<img", StringComparison.OrdinalIgnoreCase) || HtmlText.ToText(html, cancellationToken).Length > 0;
}
