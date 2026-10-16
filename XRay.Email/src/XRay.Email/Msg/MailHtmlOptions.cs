using System.Globalization;

namespace XRay.Email.Msg;

/// <summary>Options for <see cref="MailHtmlWriter"/> and <see cref="MapiMessage.ToHtml(MailHtmlOptions?, CancellationToken)"/>.</summary>
public sealed class MailHtmlOptions
{
    /// <summary>
    /// Remove what would run or navigate when the page is opened: <c>script</c> elements, event
    /// handler attributes (<c>onclick</c> and the like), <c>javascript:</c> and other scripting URLs,
    /// <c>iframe</c>, <c>object</c>, <c>embed</c>, <c>applet</c>, <c>base</c> and meta refresh, and CSS
    /// <c>expression()</c>, <c>behavior</c> and <c>@import</c>. On by default; the page is meant to be
    /// shown, not to act.
    /// </summary>
    public bool SanitizeHtml { get; init; } = true;

    /// <summary>
    /// Replace <c>cid:</c> image references (and references matching an attachment's content
    /// location) with <c>data:</c> URIs of the attachment, so the page needs nothing else. On by default.
    /// </summary>
    public bool EmbedInlineImages { get; init; } = true;

    /// <summary>Largest single image embedded as a data URI; a larger one keeps its <c>cid:</c> reference. Default 10 MiB.</summary>
    public int MaxInlineImageBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Write the From/Sent/To/Cc/Bcc/Subject/Attachments block above the body. On by default.</summary>
    public bool IncludeHeader { get; init; } = true;

    /// <summary>List attachments the body already shows inline (embedded images) in the header too. Off by default.</summary>
    public bool ListInlineAttachments { get; init; }

    /// <summary>Turn http, https and www addresses in a plain text body into links. Off by default.</summary>
    public bool LinkifyPlainText { get; init; }

    /// <summary>
    /// Write a complete document (doctype, head with the charset and styles, body). When false the
    /// result is a fragment: a style element, the header and the body, for placing inside another page.
    /// </summary>
    public bool FullDocument { get; init; } = true;

    /// <summary>Format of the Sent line. Default <c>dddd, MMMM d, yyyy h:mm tt</c>, as Outlook writes it.</summary>
    public string DateFormat { get; init; } = "dddd, MMMM d, yyyy h:mm tt";

    /// <summary>The zone the Sent time is shown in. Default UTC, so output does not depend on the machine.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>Append the zone to the Sent line: <c>UTC</c>, or an offset such as <c>UTC+02:00</c>. On by default.</summary>
    public bool ShowTimeZone { get; init; } = true;

    /// <summary>Culture for the Sent line and attachment sizes. Default invariant (English month and day names).</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;
}
