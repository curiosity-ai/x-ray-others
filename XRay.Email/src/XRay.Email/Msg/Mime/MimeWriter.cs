using System.Text;

namespace XRay.Email.Msg.Mime;

internal sealed class MimeWriter
{
    public byte[] Write(MimeMessageModel message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var memory = new MemoryStream();
        Write(memory, message);
        return memory.ToArray();
    }

    public void Write(Stream stream, MimeMessageModel message)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var headerBytes = SerializeHeaders(message.Headers);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write("\r\n"u8);
        var bodyBytes = SerializeEntityBody(message.RootEntity);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
    }

    private static byte[] SerializeEntity(MimeEntity entity)
    {
        using var memory = new MemoryStream();
        var headers = SerializeHeaders(entity.Headers);
        memory.Write(headers, 0, headers.Length);
        memory.Write("\r\n"u8);
        var body = SerializeEntityBody(entity);
        memory.Write(body, 0, body.Length);
        return memory.ToArray();
    }

    private static byte[] SerializeEntityBody(MimeEntity entity)
    {
        return entity switch
        {
            MimeMultipart multipart => SerializeMultipartBody(multipart),
            MimePart part => SerializePartBody(part),
            _ => Array.Empty<byte>(),
        };
    }

    private static byte[] SerializePartBody(MimePart part)
    {
        // Encode with the transfer encoding the part declares, so headers and body always agree.
        // No line break is appended: RFC 2046 5.1.1 makes the CRLF before a boundary part of the
        // boundary, so adding one here would add a line to the content.
        return TransferEncodingEncoder.EncodeBodyAs(part.TransferEncoding, part.DecodedBody);
    }

    private static byte[] SerializeMultipartBody(MimeMultipart multipart)
    {
        using var memory = new MemoryStream();
        foreach (var child in multipart.Children)
        {
            var marker = Encoding.ASCII.GetBytes($"--{multipart.Boundary}\r\n");
            memory.Write(marker, 0, marker.Length);
            var serialized = SerializeEntity(child);
            memory.Write(serialized, 0, serialized.Length);
            memory.Write("\r\n"u8);
        }

        var closing = Encoding.ASCII.GetBytes($"--{multipart.Boundary}--\r\n");
        memory.Write(closing, 0, closing.Length);
        return memory.ToArray();
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
