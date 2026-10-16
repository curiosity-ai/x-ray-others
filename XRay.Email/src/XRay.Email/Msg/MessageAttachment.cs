using System.Globalization;
using System.Text.RegularExpressions;

namespace XRay.Email.Msg;

/// <summary>How an embedded message is serialised when it is read out as an attachment.</summary>
public enum EmbeddedMessageFormat
{
    /// <summary>MSG for a message loaded from MSG (or built in code), EML for one loaded from EML.</summary>
    Auto = 0,

    /// <summary>An Outlook MSG file.</summary>
    Msg = 1,

    /// <summary>An RFC 5322 / MIME message.</summary>
    Eml = 2,
}

/// <summary>Options for <see cref="MapiMessage.GetAttachmentsFlattened"/>.</summary>
public sealed class AttachmentFlattenOptions
{
    /// <summary>
    /// 0 lists only this message's attachments (an embedded message is one entry). Each further level
    /// also lists the attachments inside embedded messages, depth first, right after the embedded
    /// message's own entry. Capped at 32.
    /// </summary>
    public int MaxDepth { get; init; }

    /// <summary>The serialisation of embedded messages.</summary>
    public EmbeddedMessageFormat EmbeddedFormat { get; init; } = EmbeddedMessageFormat.Auto;

    /// <summary>Include attachments the body displays inline (images referenced by <c>cid:</c>). They are flagged <see cref="MessageAttachment.IsInline"/>.</summary>
    public bool IncludeInline { get; init; } = true;

    /// <summary>Include attachments marked hidden (PR_ATTACHMENT_HIDDEN). They are flagged <see cref="MessageAttachment.IsHidden"/>.</summary>
    public bool IncludeHidden { get; init; } = true;

    /// <summary>Include embedded messages as entries of their own.</summary>
    public bool IncludeEmbeddedMessages { get; init; } = true;
}

/// <summary>
/// One attachment as a consumer wants it: a unique file name, a content type, a size, a content id,
/// and bytes that can be read or copied. Embedded messages are serialised on demand.
/// </summary>
public sealed class MessageAttachment
{
    private readonly EmbeddedMessageFormat _format;
    private long? _size;

    internal MessageAttachment(MapiMessage owner, MapiAttachment source, string name, string contentType, bool isInline, bool isHidden, int depth, string path, EmbeddedMessageFormat format)
    {
        Owner = owner;
        Source = source;
        Name = name;
        ContentType = contentType;
        IsInline = isInline;
        IsHidden = isHidden;
        Depth = depth;
        Path = path;
        _format = format;
    }

    /// <summary>A file-system-safe name, unique within the listing it came from. For an embedded message: the subject plus .msg or .eml.</summary>
    public string Name { get; }

    /// <summary>The declared media type, else one derived from the file name; application/vnd.ms-outlook or message/rfc822 for an embedded message.</summary>
    public string ContentType { get; }

    /// <summary>The Content-ID without angle brackets, or null.</summary>
    public string? ContentId => string.IsNullOrWhiteSpace(Source.ContentId) ? null : Source.ContentId.Trim().Trim('<', '>');

    /// <summary>
    /// True when the message body displays the attachment: its content id is referenced by a <c>cid:</c>
    /// URL in the HTML body, or the attachment is hidden or flagged as an MHTML reference.
    /// </summary>
    public bool IsInline { get; }

    /// <summary>PR_ATTACHMENT_HIDDEN.</summary>
    public bool IsHidden { get; }

    /// <summary>True for a message attached to a message.</summary>
    public bool IsEmbeddedMessage => Source.EmbeddedMessage is not null;

    /// <summary>The embedded message, or null.</summary>
    public MapiMessage? EmbeddedMessage => Source.EmbeddedMessage;

    /// <summary>0 for the message the listing was taken from, 1 for what is inside its embedded messages, and so on.</summary>
    public int Depth { get; }

    /// <summary>The names of the embedded messages leading here, then this name, joined by '/'.</summary>
    public string Path { get; }

    /// <summary>The message this attachment belongs to (an embedded message at depth above 0).</summary>
    public MapiMessage Owner { get; }

    /// <summary>The underlying attachment.</summary>
    public MapiAttachment Source { get; }

    /// <summary>
    /// True when bytes can be read. False for an OLE or other storage attachment the library keeps
    /// only as a storage tree; those read as empty.
    /// </summary>
    public bool HasContent => IsEmbeddedMessage || !Source.IsStorageAttachment;

    /// <summary>
    /// The size in bytes. For an embedded message this is the size of its serialisation, computed
    /// once on first use by writing it to a counting sink.
    /// </summary>
    public long Size => GetSize(CancellationToken.None);

    /// <summary>See <see cref="Size"/>.</summary>
    public long GetSize(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_size is { } known)
        {
            return known;
        }

        if (Source.EmbeddedMessage is null)
        {
            _size = HasContent ? Source.Data.LongLength : 0;
            return _size.Value;
        }

        var counter = new CountingStream();
        Serialise(counter, cancellationToken);
        _size = counter.Count;
        return _size.Value;
    }

    /// <summary>
    /// Opens the content for reading. A plain attachment is a read-only view of its bytes. An embedded
    /// message is serialised first, into memory up to 1 MiB and into a temporary file (deleted on
    /// dispose) beyond that.
    /// </summary>
    public Stream OpenRead(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Source.EmbeddedMessage is null)
        {
            return HasContent ? Source.OpenRead() : new MemoryStream([], writable: false);
        }

        var spill = new SpillStream();
        try
        {
            Serialise(spill, cancellationToken);
            return spill.PrepareForReading();
        }
        catch
        {
            spill.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes the content to <paramref name="destination"/>. An embedded message is serialised
    /// straight into it, sector by sector (MSG) or part by part (EML), with no intermediate copy.
    /// </summary>
    public void CopyTo(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (Source.EmbeddedMessage is not null)
        {
            Serialise(destination, cancellationToken);
            return;
        }

        if (HasContent)
        {
            Source.CopyTo(destination, cancellationToken);
        }
    }

    /// <summary>
    /// Asynchronous copy. A plain attachment is written with asynchronous writes. An embedded message
    /// is serialised (synchronously, on the thread pool) into a spill buffer first and then copied with
    /// asynchronous writes, so it is safe for destinations that forbid synchronous I/O.
    /// </summary>
    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (Source.EmbeddedMessage is not null)
        {
            using var spill = new SpillStream();
            await Task.Run(() => Serialise(spill, cancellationToken), cancellationToken).ConfigureAwait(false);
            spill.PrepareForReading();
            await spill.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!HasContent)
        {
            return;
        }

        var data = Source.Data;
        for (var offset = 0; offset < data.Length; offset += 81920)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(data.AsMemory(offset, Math.Min(81920, data.Length - offset)), cancellationToken).ConfigureAwait(false);
        }
    }

    private void Serialise(Stream destination, CancellationToken cancellationToken)
    {
        var embedded = Source.EmbeddedMessage!;
        if (_format == EmbeddedMessageFormat.Eml)
        {
            embedded.SaveToEml(destination, cancellationToken);
        }
        else
        {
            embedded.Save(destination, cancellationToken);
        }
    }

    private sealed class CountingStream : Stream
    {
        public long Count { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Path} ({ContentType})");
}

public sealed partial class MapiMessage
{
    private const int MaxFlattenDepth = 32;
    private const int MaxFileNameLength = 120;
    private const ushort AttachmentHiddenPropertyId = 0x7FFE;
    private const ushort AttachFlagsPropertyId = 0x3714;
    private const int AttachFlagMhtmlReference = 0x4;

    /// <summary>
    /// Lists the attachments the way a consumer that stores them as files wants them: a safe unique
    /// name, content type, size, content id, inline flag and readable bytes for each one. Embedded
    /// messages are named from their subject (<c>Re_ lunch.msg</c>) and serialised on demand as MSG or
    /// EML (see <see cref="AttachmentFlattenOptions.EmbeddedFormat"/>); with
    /// <see cref="AttachmentFlattenOptions.MaxDepth"/> above 0 the attachments inside them are listed
    /// too. Names are made unique across the whole listing, case-insensitively (<c>a.png</c>,
    /// <c>a (2).png</c>).
    /// </summary>
    public IReadOnlyList<MessageAttachment> GetAttachmentsFlattened(AttachmentFlattenOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new AttachmentFlattenOptions();
        var result = new List<MessageAttachment>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxDepth = Math.Clamp(options.MaxDepth, 0, MaxFlattenDepth);
        var format = options.EmbeddedFormat != EmbeddedMessageFormat.Auto
            ? options.EmbeddedFormat
            : SourceFormat == MessageSourceFormat.Eml ? EmbeddedMessageFormat.Eml : EmbeddedMessageFormat.Msg;
        FlattenInto(this, 0, string.Empty, options, maxDepth, format, used, result, cancellationToken);
        return result;
    }

    private static void FlattenInto(MapiMessage message, int depth, string pathPrefix, AttachmentFlattenOptions options, int maxDepth, EmbeddedMessageFormat format, HashSet<string> used, List<MessageAttachment> result, CancellationToken cancellationToken)
    {
        var referenced = message.ReferencedContentIds();
        var index = 0;
        foreach (var attachment in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            var hidden = message.IsAttachmentHidden(attachment);
            var contentId = string.IsNullOrWhiteSpace(attachment.ContentId) ? null : attachment.ContentId.Trim().Trim('<', '>');
            var flags = attachment.Properties.Get(AttachFlagsPropertyId)?.Value is int attachFlags ? attachFlags : 0;
            var location = attachment.ContentLocation;
            var inline = (contentId is not null && (referenced.Contains(contentId) || hidden || (flags & AttachFlagMhtmlReference) != 0))
                || (location is not null && message.HtmlBody is { } bodyHtml && bodyHtml.Contains(location, StringComparison.Ordinal));
            var embedded = attachment.EmbeddedMessage;

            if ((embedded is not null && !options.IncludeEmbeddedMessages) || (inline && !options.IncludeInline) || (hidden && !options.IncludeHidden))
            {
                if (embedded is not null && depth < maxDepth)
                {
                    FlattenInto(embedded, depth + 1, pathPrefix + Describe(embedded, attachment, index) + "/", options, maxDepth, format, used, result, cancellationToken);
                }

                continue;
            }

            string name;
            string contentType;
            if (embedded is not null)
            {
                var extension = format == EmbeddedMessageFormat.Eml ? ".eml" : ".msg";
                name = Unique(used, SanitizeFileName(string.IsNullOrWhiteSpace(embedded.Subject) ? StripExtension(attachment.Filename) : embedded.Subject, extension) ?? "message" + extension);
                contentType = format == EmbeddedMessageFormat.Eml ? "message/rfc822" : "application/vnd.ms-outlook";
            }
            else
            {
                var declared = string.IsNullOrWhiteSpace(attachment.MimeType) ? null : attachment.MimeType.Trim();
                var safe = SanitizeFileName(attachment.Filename, null);
                if (safe is null)
                {
                    var extension = MimeTypes.ExtensionFor(declared) ?? ImageExtension(attachment.Data) ?? string.Empty;
                    safe = "attachment-" + index.ToString(CultureInfo.InvariantCulture) + extension;
                }

                name = Unique(used, safe);
                contentType = declared ?? MimeTypes.SniffImage(attachment.Data) ?? MimeTypes.FromFileName(name);
            }

            result.Add(new MessageAttachment(message, attachment, name, contentType, inline, hidden, depth, pathPrefix + name, format));
            if (embedded is not null && depth < maxDepth)
            {
                FlattenInto(embedded, depth + 1, pathPrefix + name + "/", options, maxDepth, format, used, result, cancellationToken);
            }
        }
    }

    private static string Describe(MapiMessage embedded, MapiAttachment attachment, int index) =>
        SanitizeFileName(string.IsNullOrWhiteSpace(embedded.Subject) ? StripExtension(attachment.Filename) : embedded.Subject, null) ?? "message-" + index.ToString(CultureInfo.InvariantCulture);

    private static string? ImageExtension(byte[] data) => MimeTypes.ExtensionFor(MimeTypes.SniffImage(data));

    private static string? StripExtension(string? name) => string.IsNullOrEmpty(name) ? null : System.IO.Path.GetFileNameWithoutExtension(name);

    private bool IsAttachmentHidden(MapiAttachment attachment) => attachment.Properties.Get(AttachmentHiddenPropertyId)?.Value switch
    {
        bool value => value,
        int value => value != 0,
        _ => false,
    };

    /// <summary>Content ids that the HTML body refers to as <c>cid:</c> URLs.</summary>
    internal HashSet<string> ReferencedContentIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (HtmlBody is { Length: > 0 } html)
        {
            foreach (Match match in ContentIdReference().Matches(html))
            {
                var id = match.Groups[1].Value;
                ids.Add(id);
                try
                {
                    ids.Add(Uri.UnescapeDataString(id));
                }
                catch (UriFormatException)
                {
                    // keep the raw form only
                }
            }
        }

        return ids;
    }

    [GeneratedRegex(@"cid:([^""'\s>)\\]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContentIdReference();

    /// <summary>
    /// A name that is safe as a file name on every platform: separators, control characters and
    /// <c>&lt;&gt;:"|?*</c> become '_', trailing dots and spaces go, the length is capped keeping the
    /// extension. <paramref name="forcedExtension"/> replaces the extension (and is appended when
    /// there is none). Null when nothing usable remains.
    /// </summary>
    internal static string? SanitizeFileName(string? name, string? forcedExtension)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            builder.Append(char.IsControl(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
        }

        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(ch => ch is '_' or '.' or ' '))
        {
            return null;
        }

        var extension = forcedExtension ?? System.IO.Path.GetExtension(cleaned);
        var stem = forcedExtension is not null
            ? (cleaned.EndsWith(forcedExtension, StringComparison.OrdinalIgnoreCase) ? cleaned[..^forcedExtension.Length] : cleaned)
            : (extension.Length > 0 ? cleaned[..^extension.Length] : cleaned);
        if (extension.Length > 16)
        {
            stem = cleaned;
            extension = string.Empty;
        }

        var budget = Math.Max(1, MaxFileNameLength - extension.Length);
        if (stem.Length > budget)
        {
            stem = stem[..budget].TrimEnd('.', ' ');
        }

        return stem.Length == 0 ? null : stem + extension;
    }

    private static string Unique(HashSet<string> used, string name)
    {
        if (used.Add(name))
        {
            return name;
        }

        var extension = System.IO.Path.GetExtension(name);
        var stem = extension.Length > 0 ? name[..^extension.Length] : name;
        for (var counter = 2; ; counter++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} ({counter}){extension}");
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
