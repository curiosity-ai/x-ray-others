using System.Text;
using System.Globalization;

namespace XRay.Email.Msg.Mime;

internal static class EmlMessageMapper
{
    public static MapiMessage ToMapiMessage(MimeMessageModel model, CancellationToken cancellationToken = default) => ToMapiMessage(model, 0, cancellationToken);

    private static MapiMessage ToMapiMessage(MimeMessageModel model, int depth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var message = new MapiMessage { SourceFormat = MessageSourceFormat.Eml };
        message.Subject = model.Subject;
        var sender = model.From.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(sender.Address))
        {
            sender = model.Sender.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(sender.Address))
        {
            message.SenderAddressType = "SMTP";
            message.SenderEmailAddress = sender.Address;
            message.SenderName = sender.DisplayName;
        }

        AddRecipients(message, model.To, MapiMessage.RecipientTypeTo);
        AddRecipients(message, model.Cc, MapiMessage.RecipientTypeCc);
        AddRecipients(message, model.Bcc, MapiMessage.RecipientTypeBcc);

        if (!string.IsNullOrWhiteSpace(model.MessageId))
        {
            message.InternetMessageId = model.MessageId;
        }

        if (model.Date is { } date)
        {
            message.MessageDeliveryTime = date.UtcDateTime;
        }

        if (!string.IsNullOrWhiteSpace(model.RawHeaderText))
        {
            message.TransportMessageHeaders = model.RawHeaderText.TrimEnd('\r', '\n') + "\r\n";
        }

        ExtractContent(model.RootEntity, message, depth, cancellationToken);
        return message;
    }

    public static MimeMessageModel ToMimeMessage(MapiMessage message, CancellationToken cancellationToken = default) => ToMimeMessage(message, new MimeBoundaryFactory(), cancellationToken);

    private static MimeMessageModel ToMimeMessage(MapiMessage message, MimeBoundaryFactory boundaryFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var rootEntity = BuildRootEntity(message, boundaryFactory, cancellationToken);
        var headers = new List<MimeHeader>();
        AddHeader(headers, "MIME-Version", "1.0");
        if (!string.IsNullOrWhiteSpace(message.Subject))
        {
            headers.Add(new MimeHeader("Subject", message.Subject, TransferEncodingEncoder.EncodeUnstructured(message.Subject)));
        }

        var sender = CreateSender(message);
        if (sender is { } senderMailbox)
        {
            AddHeader(headers, "From", AddressParser.FormatSingle(senderMailbox));
        }

        AddHeader(headers, "To", FormatRecipients(message, MapiMessage.RecipientTypeTo));
        AddHeader(headers, "Cc", FormatRecipients(message, MapiMessage.RecipientTypeCc));
        AddHeader(headers, "Bcc", FormatRecipients(message, MapiMessage.RecipientTypeBcc));
        if (message.MessageDeliveryTime is { } deliveryTime)
        {
            AddHeader(headers, "Date", deliveryTime.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture));
        }

        AddHeader(headers, "Message-ID", message.InternetMessageId);
        foreach (var header in rootEntity.Headers)
        {
            headers.Add(header);
        }

        return new MimeMessageModel(headers, rootEntity);
    }

    private static void ExtractContent(MimeEntity entity, MapiMessage message, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (entity)
        {
            case MimeMultipart multipart:
                foreach (var child in multipart.Children)
                {
                    ExtractContent(child, message, depth, cancellationToken);
                }

                break;

            case MimePart part:
                if (string.Equals(part.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase) && !part.IsAttachment && string.IsNullOrEmpty(message.Body))
                {
                    message.Body = part.GetText();
                    break;
                }

                if (string.Equals(part.MediaType, "text/html", StringComparison.OrdinalIgnoreCase) && !part.IsAttachment && string.IsNullOrEmpty(message.HtmlBody))
                {
                    message.HtmlBody = part.GetText();
                    break;
                }

                if (string.Equals(part.MediaType, "message/rfc822", StringComparison.OrdinalIgnoreCase) && depth < MimeReader.MaxDepth)
                {
                    try
                    {
                        var embedded = ToMapiMessage(new MimeReader(cancellationToken).Read(part.DecodedBody), depth + 1, cancellationToken);
                        message.Attachments.Add(new MapiAttachment
                        {
                            Filename = part.FileName,
                            MimeType = part.ContentType,
                            ContentId = part.ContentId,
                            AttachMethod = MapiMessage.AttachMethodEmbedded,
                            EmbeddedMessage = embedded,
                        });
                        break;
                    }
                    catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException or MsgException)
                    {
                        // Not a parseable message: keep it as an ordinary attachment below.
                    }
                }

                var attachment = new MapiAttachment
                {
                    Filename = part.FileName,
                    MimeType = part.ContentType,
                    ContentId = part.ContentId,
                    Data = part.DecodedBody,
                };
                if (HeaderParser.StripComments(part.GetHeaderValue("content-location") ?? string.Empty).Trim() is { Length: > 0 } location)
                {
                    attachment.ContentLocation = location;
                }

                message.Attachments.Add(attachment);
                break;
        }
    }

    private static MimeEntity BuildRootEntity(MapiMessage message, MimeBoundaryFactory boundaryFactory, CancellationToken cancellationToken)
    {
        var bodyEntity = BuildBodyEntity(message, boundaryFactory);
        var attachmentEntities = message.Attachments.Select(attachment => BuildAttachmentPart(attachment, boundaryFactory, cancellationToken)).ToArray();
        if (attachmentEntities.Length == 0)
        {
            return bodyEntity;
        }

        var boundary = boundaryFactory.NextBoundary("mixed");
        var headers = new[]
        {
            new MimeHeader("Content-Type", $"multipart/mixed; boundary=\"{boundary}\""),
        };

        var children = new List<MimeEntity> { bodyEntity };
        children.AddRange(attachmentEntities);
        return new MimeMultipart(headers, boundary, children);
    }

    private static MimeEntity BuildBodyEntity(MapiMessage message, MimeBoundaryFactory boundaryFactory)
    {
        var hasText = !string.IsNullOrEmpty(message.Body);
        var hasHtml = !string.IsNullOrEmpty(message.HtmlBody);
        if (hasText && hasHtml)
        {
            var boundary = boundaryFactory.NextBoundary("alternative");
            var headers = new[]
            {
                new MimeHeader("Content-Type", $"multipart/alternative; boundary=\"{boundary}\""),
            };
            return new MimeMultipart(headers, boundary, new[]
            {
                BuildTextPart("text/plain", message.Body!),
                BuildTextPart("text/html", message.HtmlBody!),
            });
        }

        if (hasHtml)
        {
            return BuildTextPart("text/html", message.HtmlBody!);
        }

        return BuildTextPart("text/plain", message.Body ?? string.Empty);
    }

    private static MimePart BuildTextPart(string mediaType, string text)
    {
        var decodedBody = CharsetDecoder.EncodeText(text, "utf-8");
        var (transferEncoding, encodedBody) = TransferEncodingEncoder.EncodeBody(mediaType, decodedBody);
        var headers = new[]
        {
            new MimeHeader("Content-Type", $"{mediaType}; charset=utf-8"),
            new MimeHeader("Content-Transfer-Encoding", transferEncoding),
        };
        return new MimePart(headers, encodedBody, decodedBody);
    }

    private static MimeEntity BuildAttachmentPart(MapiAttachment attachment, MimeBoundaryFactory boundaryFactory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mediaType = attachment.MimeType ?? GuessMimeType(attachment.Filename);
        MimeMessageModel? embedded = null;
        byte[] decodedBody = [];
        string transferEncoding;
        if (attachment.EmbeddedMessage is not null)
        {
            embedded = ToMimeMessage(attachment.EmbeddedMessage, boundaryFactory, cancellationToken);
            mediaType = "message/rfc822";
            // RFC 2046 5.2.1: never encoded; 7bit only if every byte is. A throw-away pass decides
            // without holding the serialised message.
            var probe = new SevenBitProbe();
            new MimeWriter().Write(probe, embedded, cancellationToken);
            transferEncoding = probe.IsEmpty ? "7bit" : probe.IsSevenBit ? "7bit" : "8bit";
        }
        else
        {
            decodedBody = attachment.Data;
            // Attachments keep their exact bytes: base64 even for text, whose 7bit and quoted-printable
            // forms would canonicalise line breaks.
            transferEncoding = decodedBody.Length == 0 ? "7bit" : "base64";
        }
        var disposition = !string.IsNullOrEmpty(attachment.ContentId) ? "inline" : "attachment";
        var filename = attachment.Filename;
        var headers = new List<MimeHeader>
        {
            // Content-Type carries the name as quoted encoded words, which older clients read;
            // Content-Disposition carries it in RFC 2231 form, which is the standard.
            WireHeader("Content-Type", string.IsNullOrEmpty(filename) ? mediaType : $"{mediaType}; {FormatNameParameter(filename)}"),
            new("Content-Transfer-Encoding", transferEncoding),
            WireHeader("Content-Disposition", string.IsNullOrEmpty(filename) ? disposition : $"{disposition}; {TransferEncodingEncoder.FormatParameter("filename", filename)}"),
        };

        if (!string.IsNullOrEmpty(attachment.ContentId))
        {
            headers.Add(new MimeHeader("Content-ID", $"<{attachment.ContentId}>"));
        }

        if (attachment.ContentLocation is { Length: > 0 } contentLocation && contentLocation.All(ch => ch is > ' ' and <= '~'))
        {
            headers.Add(new MimeHeader("Content-Location", contentLocation));
        }

        if (embedded is not null)
        {
            return new MimeEmbeddedMessage(headers, embedded);
        }

        return decodedBody.Length == 0 ? new MimePart(headers, [], []) : MimePart.Deferred(headers, decodedBody);
    }

    private sealed class SevenBitProbe : Stream
    {
        public bool IsSevenBit { get; private set; } = true;

        public bool IsEmpty { get; private set; } = true;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > 0)
            {
                IsEmpty = false;
            }

            if (!IsSevenBit)
            {
                return;
            }

            foreach (var b in buffer)
            {
                if (b is 9 or 10 or 13)
                {
                    continue;
                }

                if (b < 32 || b > 126)
                {
                    IsSevenBit = false;
                    return;
                }
            }
        }
    }

    private static MimeHeader WireHeader(string name, string rawValue) => MimeHeader.FromWire(name, rawValue);

    private static string FormatNameParameter(string filename)
    {
        if (filename.All(ch => ch is >= ' ' and <= '~'))
        {
            return TransferEncodingEncoder.FormatParameter("name", filename);
        }

        return $"name=\"{TransferEncodingEncoder.EncodeWords(filename)}\"";
    }

    private static void AddRecipients(MapiMessage message, IEnumerable<MimeMailbox> mailboxes, int recipientType)
    {
        foreach (var mailbox in mailboxes)
        {
            if (string.IsNullOrWhiteSpace(mailbox.Address))
            {
                continue;
            }

            message.AddRecipient(mailbox.Address, mailbox.DisplayName, recipientType);
        }
    }

    private static string? FormatRecipients(MapiMessage message, int recipientType)
    {
        var recipients = message.Recipients
            .Where(recipient => recipient.RecipientType == recipientType && !string.IsNullOrWhiteSpace(recipient.EmailAddress))
            .Select(recipient => new MimeMailbox(recipient.DisplayName, recipient.SmtpAddress ?? recipient.EmailAddress!))
            .ToArray();
        return recipients.Length == 0 ? null : AddressParser.FormatList(recipients);
    }

    private static MimeMailbox? CreateSender(MapiMessage message)
    {
        var address = message.SenderSmtpAddress ?? message.SenderEmailAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        return new MimeMailbox(message.SenderName, address);
    }

    private static void AddHeader(ICollection<MimeHeader> headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers.Add(MimeHeader.FromWire(name, value));
        }
    }

    private static string GuessMimeType(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return "application/octet-stream";
        }

        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".eml" => "message/rfc822",
            _ => "application/octet-stream",
        };
    }
}
