using System.Text;

namespace XRay.Email.Msg.Mime;

internal sealed class MimeWriter
{
    private const int Base64LineBytes = 57; // 76 base64 characters per line
    private const int Base64BlockLines = 512;

    public byte[] Write(MimeMessageModel message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var memory = new MemoryStream();
        Write(memory, message, cancellationToken);
        return memory.ToArray();
    }

    public void Write(Stream stream, MimeMessageModel message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var headerBytes = SerializeHeaders(message.Headers);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write("\r\n"u8);
        WriteEntityBody(stream, message.RootEntity, cancellationToken);
    }

    private static void WriteEntity(Stream stream, MimeEntity entity, CancellationToken cancellationToken)
    {
        var headers = SerializeHeaders(entity.Headers);
        stream.Write(headers, 0, headers.Length);
        stream.Write("\r\n"u8);
        WriteEntityBody(stream, entity, cancellationToken);
    }

    private static void WriteEntityBody(Stream stream, MimeEntity entity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (entity)
        {
            case MimeMultipart multipart:
                WriteMultipartBody(stream, multipart, cancellationToken);
                break;
            case MimeEmbeddedMessage embedded:
                new MimeWriter().Write(stream, embedded.Message, cancellationToken);
                break;
            case MimePart { DeferEncoding: true } deferred when string.Equals(deferred.TransferEncoding, "base64", StringComparison.Ordinal):
                WriteBase64(stream, deferred.DecodedBody, cancellationToken);
                break;
            case MimePart part:
                // Encode with the transfer encoding the part declares, so headers and body always agree.
                // No line break is appended: RFC 2046 5.1.1 makes the CRLF before a boundary part of the
                // boundary, so adding one here would add a line to the content.
                var body = TransferEncodingEncoder.EncodeBodyAs(part.TransferEncoding, part.DecodedBody);
                stream.Write(body, 0, body.Length);
                break;
        }
    }

    /// <summary>Base64 with 76-character lines, each ended by CRLF (the last included), written block by block.</summary>
    private static void WriteBase64(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        const int blockBytes = Base64LineBytes * Base64BlockLines;
        var line = new char[76];
        var buffer = new byte[(Base64BlockLines * 78)];
        for (var offset = 0; offset < payload.Length; offset += blockBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(blockBytes, payload.Length - offset);
            var written = 0;
            for (var lineOffset = 0; lineOffset < count; lineOffset += Base64LineBytes)
            {
                var lineCount = Math.Min(Base64LineBytes, count - lineOffset);
                Convert.TryToBase64Chars(payload.AsSpan(offset + lineOffset, lineCount), line, out var chars);
                for (var i = 0; i < chars; i++)
                {
                    buffer[written++] = (byte)line[i];
                }

                buffer[written++] = (byte)'\r';
                buffer[written++] = (byte)'\n';
            }

            stream.Write(buffer, 0, written);
        }
    }

    private static void WriteMultipartBody(Stream stream, MimeMultipart multipart, CancellationToken cancellationToken)
    {
        foreach (var child in multipart.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marker = Encoding.ASCII.GetBytes($"--{multipart.Boundary}\r\n");
            stream.Write(marker, 0, marker.Length);
            WriteEntity(stream, child, cancellationToken);
            stream.Write("\r\n"u8);
        }

        var closing = Encoding.ASCII.GetBytes($"--{multipart.Boundary}--\r\n");
        stream.Write(closing, 0, closing.Length);
    }

    private static byte[] SerializeHeaders(IEnumerable<MimeHeader> headers)
    {
        var builder = new StringBuilder();
        foreach (var header in headers)
        {
            // RawValue is the wire form. A value that still carries non-ASCII text (a header added by
            // hand) is sent as RFC 6532 UTF-8 rather than mangled.
            builder.Append(TransferEncodingEncoder.FoldHeader(header.Name, header.RawValue));
            builder.Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
