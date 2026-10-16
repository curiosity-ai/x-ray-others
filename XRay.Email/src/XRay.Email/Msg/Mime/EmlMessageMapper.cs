using System.Text;
using System.Globalization;

namespace XRay.Email.Msg.Mime;

internal static class EmlMessageMapper
{
    public static MapiMessage ToMapiMessage(MimeMessageModel model) => ToMapiMessage(model, 0);

    private static MapiMessage ToMapiMessage(MimeMessageModel model, int depth)
    {
        ArgumentNullException.ThrowIfNull(model);

        var message = new MapiMessage();
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

        ExtractContent(model.RootEntity, message, depth);
        return message;
    }

    public static MimeMessageModel ToMimeMessage(MapiMessage message) => ToMimeMessage(message, new MimeBoundaryFactory());

    private static MimeMessageModel ToMimeMessage(MapiMessage message, MimeBoundaryFactory boundaryFactory)
    {
        ArgumentNullException.ThrowIfNull(message);

        var rootEntity = BuildRootEntity(message, boundaryFactory);
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

    private static void ExtractContent(MimeEntity entity, MapiMessage message, int depth)
    {
        switch (entity)
        {
            case MimeMultipart multipart:
                foreach (var child in multipart.Children)
                {
                    ExtractContent(child, message, depth);
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
                        var embedded = ToMapiMessage(new MimeReader().Read(part.DecodedBody), depth + 1);
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

                message.Attachments.Add(new MapiAttachment
                {
                    Filename = part.FileName,
                    MimeType = part.ContentType,
                    ContentId = part.ContentId,
                    Data = part.DecodedBody.ToArray(),
                });
                break;
        }
    }

    private static MimeEntity BuildRootEntity(MapiMessage message, MimeBoundaryFactory boundaryFactory)
    {
        var bodyEntity = BuildBodyEntity(message, boundaryFactory);
        var attachmentEntities = message.Attachments.Select(attachment => BuildAttachmentPart(attachment, boundaryFactory)).ToArray();
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

    private static MimePart BuildAttachmentPart(MapiAttachment attachment, MimeBoundaryFactory boundaryFactory)
    {
        byte[] decodedBody;
        var mediaType = attachment.MimeType ?? GuessMimeType(attachment.Filename);
        if (attachment.EmbeddedMessage is not null)
        {
            decodedBody = new MimeWriter().Write(ToMimeMessage(attachment.EmbeddedMessage, boundaryFactory));
            mediaType = "message/rfc822";
        }
        else
        {
            decodedBody = attachment.Data.ToArray();
        }

        // Attachments keep their exact bytes: base64 even for text, whose 7bit and quoted-printable
        // forms would canonicalise line breaks. Embedded messages stay unencoded (RFC 2046 5.2.1).
        var (transferEncoding, encodedBody) = TransferEncodingEncoder.EncodeBody(
            mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? "application/octet-stream" : mediaType,
            decodedBody);
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

        return new MimePart(headers, encodedBody, decodedBody);
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
