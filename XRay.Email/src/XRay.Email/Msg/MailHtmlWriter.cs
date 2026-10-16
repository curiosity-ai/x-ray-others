using System.Globalization;
using System.Text;
using XRay.Email.Msg.Html;

namespace XRay.Email.Msg;

/// <summary>
/// Renders a message as one self-contained HTML document laid out the way Outlook displays it: a
/// header block (From, Sent, To, Cc, Bcc, Subject, Attachments) above the body. The body is the
/// HTML body, else the RTF body (the original HTML when it is encapsulated, otherwise a rendering of
/// the RTF), else the plain text. The output is UTF-8, declares it, and is deterministic: nothing in it
/// depends on the time of rendering or the machine.
/// </summary>
/// <remarks>
/// S/MIME (signed or encrypted messages, <c>smime.p7m</c>) and TNEF (<c>winmail.dat</c>) are not
/// interpreted: such a message renders with whatever body it carries and the opaque part listed as an
/// attachment.
/// </remarks>
public static class MailHtmlWriter
{
    private const string Css =
        ".xray-mail{font-family:Calibri,'Segoe UI',Arial,sans-serif;font-size:11pt;color:#000}" +
        ".xray-mail-header{font-size:10pt;border-bottom:1px solid #c8c8c8;padding:0 0 8px;margin:0 0 12px}" +
        ".xray-mail-header table{border-collapse:collapse}" +
        ".xray-mail-header th{text-align:left;font-weight:bold;vertical-align:top;padding:1px 14px 1px 0;white-space:nowrap}" +
        ".xray-mail-header td{padding:1px 0;vertical-align:top}" +
        ".xray-mail-header .xray-mail-subject td{font-weight:bold;font-size:12pt}" +
        ".xray-mail-text{white-space:pre-wrap;word-wrap:break-word}";

    /// <summary>The message as an HTML string.</summary>
    public static string ToHtml(MapiMessage message, MailHtmlOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(message, writer, options, cancellationToken);
        return writer.ToString();
    }

    /// <summary>Writes the message as UTF-8 (no byte order mark) to <paramref name="stream"/>; the stream is left open.</summary>
    public static void Write(MapiMessage message, Stream stream, MailHtmlOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384, leaveOpen: true) { NewLine = "\n" };
        Write(message, writer, options, cancellationToken);
        writer.Flush();
    }

    /// <summary>Writes the message to <paramref name="writer"/>. Lines end with a line feed.</summary>
    public static void Write(MapiMessage message, TextWriter writer, MailHtmlOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(writer);
        options ??= new MailHtmlOptions();
        cancellationToken.ThrowIfCancellationRequested();

        var body = message.ResolveBody(cancellationToken);
        string bodyHtml;
        IReadOnlyList<string> styles = [];
        string? bodyStyle = null;
        switch (body.Kind)
        {
            case MessageBodyKind.Html or MessageBodyKind.Rtf:
                var processor = new HtmlBodyProcessor
                {
                    Sanitize = options.SanitizeHtml,
                    ExtractBody = true,
                    ResolveReference = options.EmbedInlineImages ? new InlineImageResolver(message, options).Resolve : null,
                };
                var processed = processor.Process(body.Html!, cancellationToken);
                bodyHtml = processed.Body;
                styles = processed.Styles;
                bodyStyle = processed.BodyStyle;
                break;
            case MessageBodyKind.PlainText:
                bodyHtml = "<div class=\"xray-mail-text\">" + HtmlText.PlainToHtml(body.Text!, options.LinkifyPlainText) + "</div>";
                break;
            default:
                bodyHtml = string.Empty;
                break;
        }

        var subject = DisplaySubject(message);
        if (options.FullDocument)
        {
            writer.Write("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>");
            writer.Write(HtmlText.Escape(string.IsNullOrWhiteSpace(subject) ? "(No subject)" : subject));
            writer.Write("</title>\n");
        }

        writer.Write("<style>");
        writer.Write(Css);
        writer.Write("</style>\n");
        foreach (var style in styles)
        {
            writer.Write("<style>");
            writer.Write(style);
            writer.Write("</style>\n");
        }

        if (options.FullDocument)
        {
            writer.Write("</head>\n<body>\n");
        }

        writer.Write("<div class=\"xray-mail\">\n");
        if (options.IncludeHeader)
        {
            WriteHeader(message, subject, options, writer, cancellationToken);
        }

        writer.Write("<div class=\"xray-mail-body\"");
        if (!string.IsNullOrEmpty(bodyStyle))
        {
            writer.Write(" style=\"");
            writer.Write(HtmlBodyProcessor.EscapeAttribute(bodyStyle));
            writer.Write('"');
        }

        writer.Write(">\n");
        writer.Write(bodyHtml);
        writer.Write("\n</div>\n</div>\n");
        if (options.FullDocument)
        {
            writer.Write("</body>\n</html>\n");
        }
    }

    private static void WriteHeader(MapiMessage message, string subject, MailHtmlOptions options, TextWriter writer, CancellationToken cancellationToken)
    {
        writer.Write("<div class=\"xray-mail-header\">\n<table>\n");
        Row(writer, "From:", FormatAddress(message.SenderName, message.SenderSmtpAddress));
        if (message.SentTime is { } sent)
        {
            Row(writer, "Sent:", HtmlText.Escape(FormatDate(sent, options)));
        }

        Row(writer, "To:", Recipients(message, MapiMessage.RecipientTypeTo, CommonMessagePropertyId.DisplayTo));
        Row(writer, "Cc:", Recipients(message, MapiMessage.RecipientTypeCc, CommonMessagePropertyId.DisplayCc));
        Row(writer, "Bcc:", Recipients(message, MapiMessage.RecipientTypeBcc, CommonMessagePropertyId.DisplayBcc));
        writer.Write("<tr class=\"xray-mail-subject\"><th>Subject:</th><td>");
        writer.Write(HtmlText.Escape(subject));
        writer.Write("</td></tr>\n");

        var listing = message.GetAttachmentsFlattened(
            new AttachmentFlattenOptions
            {
                IncludeInline = options.ListInlineAttachments || !options.EmbedInlineImages,
                IncludeHidden = false,
            },
            cancellationToken);
        if (listing.Count > 0)
        {
            var builder = new StringBuilder();
            foreach (var attachment in listing)
            {
                if (builder.Length > 0)
                {
                    builder.Append("; ");
                }

                builder.Append("<span class=\"xray-mail-attachment\">").Append(HtmlText.Escape(attachment.Name)).Append(" (").Append(HtmlText.Escape(FormatSize(attachment.GetSize(cancellationToken), options.Culture))).Append(")</span>");
            }

            Row(writer, "Attachments:", builder.ToString());
        }

        writer.Write("</table>\n</div>\n");
    }

    private static void Row(TextWriter writer, string label, string? htmlValue)
    {
        if (string.IsNullOrEmpty(htmlValue))
        {
            return;
        }

        writer.Write("<tr><th>");
        writer.Write(label);
        writer.Write("</th><td>");
        writer.Write(htmlValue);
        writer.Write("</td></tr>\n");
    }

    private static string DisplaySubject(MapiMessage message)
    {
        var subject = message.Subject ?? string.Empty;
        // PR_SUBJECT may begin with U+0001 and one more control character that marks a stripped prefix.
        if (subject.Length >= 2 && subject[0] == '\u0001')
        {
            subject = subject[2..];
        }

        return subject;
    }

    private static string? Recipients(MapiMessage message, int type, CommonMessagePropertyId displayProperty)
    {
        var parts = new List<string>();
        foreach (var recipient in message.Recipients)
        {
            if (recipient.RecipientType != type)
            {
                continue;
            }

            if (FormatAddress(recipient.DisplayName, recipient.SmtpAddress ?? recipient.EmailAddress) is { Length: > 0 } formatted)
            {
                parts.Add(formatted);
            }
        }

        if (parts.Count > 0)
        {
            return string.Join("; ", parts);
        }

        // An MSG that kept no recipient table still has the display-form line Outlook builds it from.
        return message.GetPropertyString((ushort)displayProperty) is { Length: > 0 } display ? HtmlText.Escape(display) : null;
    }

    /// <summary>"Name &lt;address&gt;" escaped for HTML, or just the one that exists.</summary>
    private static string? FormatAddress(string? name, string? address)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        if (name is null && address is null)
        {
            return null;
        }

        if (name is null || string.Equals(name, address, StringComparison.OrdinalIgnoreCase))
        {
            return HtmlText.Escape(address!);
        }

        return address is null ? HtmlText.Escape(name) : HtmlText.Escape(name) + " &lt;" + HtmlText.Escape(address) + "&gt;";
    }

    private static string FormatDate(DateTime utc, MailHtmlOptions options)
    {
        var instant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(instant, options.TimeZone);
        var text = local.ToString(options.DateFormat, options.Culture);
        if (!options.ShowTimeZone)
        {
            return text;
        }

        var offset = options.TimeZone.GetUtcOffset(instant);
        return offset == TimeSpan.Zero
            ? text + " UTC"
            : text + " UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hh\\:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatSize(long bytes, CultureInfo culture)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(culture) + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return Math.Max(1, (bytes + 512) / 1024).ToString(culture) + " KB";
        }

        return (bytes / 1048576.0).ToString("0.#", culture) + " MB";
    }

    /// <summary>Maps <c>cid:</c> references and attachment content locations to data URIs.</summary>
    private sealed class InlineImageResolver
    {
        private readonly Dictionary<string, MapiAttachment> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MapiAttachment> _byLocation = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<MapiAttachment, string?> _cache = [];
        private readonly int _maxBytes;

        public InlineImageResolver(MapiMessage message, MailHtmlOptions options)
        {
            _maxBytes = options.MaxInlineImageBytes;
            foreach (var attachment in message.Attachments)
            {
                if (attachment.EmbeddedMessage is not null || attachment.IsStorageAttachment)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(attachment.ContentId))
                {
                    _byId.TryAdd(attachment.ContentId.Trim().Trim('<', '>'), attachment);
                }

                if (attachment.ContentLocation is { Length: > 0 } location)
                {
                    _byLocation.TryAdd(location.Trim(), attachment);
                }
            }
        }

        public string? Resolve(string reference)
        {
            MapiAttachment? attachment = null;
            if (reference.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                var id = reference[4..].Trim().Trim('<', '>');
                if (!_byId.TryGetValue(id, out attachment))
                {
                    try
                    {
                        _byId.TryGetValue(Uri.UnescapeDataString(id), out attachment);
                    }
                    catch (UriFormatException)
                    {
                        // unresolved
                    }
                }
            }
            else if (_byLocation.Count > 0)
            {
                _byLocation.TryGetValue(reference, out attachment);
            }

            return attachment is null ? null : DataUri(attachment);
        }

        private string? DataUri(MapiAttachment attachment)
        {
            if (_cache.TryGetValue(attachment, out var cached))
            {
                return cached;
            }

            string? uri = null;
            var data = attachment.Data;
            if (data.Length > 0 && data.Length <= _maxBytes)
            {
                var type = MimeTypes.SniffImage(data);
                if (type is null)
                {
                    var declared = attachment.MimeType?.Split(';')[0].Trim();
                    type = declared is not null && declared.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                        ? declared.ToLowerInvariant()
                        : MimeTypes.FromFileName(attachment.Filename) is { } byName && byName.StartsWith("image/", StringComparison.Ordinal) ? byName : null;
                }

                if (type is not null)
                {
                    uri = "data:" + type + ";base64," + Convert.ToBase64String(data);
                }
            }

            _cache[attachment] = uri;
            return uri;
        }
    }
}

public sealed partial class MapiMessage
{
    /// <summary>
    /// The message as a self-contained, Outlook-style HTML document: header block, then the body
    /// (HTML, else RTF, else plain text), with <c>cid:</c> images inlined and scripting removed by
    /// default. See <see cref="MailHtmlWriter"/> and <see cref="MailHtmlOptions"/>.
    /// </summary>
    public string ToHtml(MailHtmlOptions? options = null, CancellationToken cancellationToken = default) =>
        MailHtmlWriter.ToHtml(this, options, cancellationToken);

    /// <summary>Writes <see cref="ToHtml(MailHtmlOptions?, CancellationToken)"/> to <paramref name="stream"/> as UTF-8 without buffering the whole document.</summary>
    public void WriteHtml(Stream stream, MailHtmlOptions? options = null, CancellationToken cancellationToken = default) =>
        MailHtmlWriter.Write(this, stream, options, cancellationToken);
}
