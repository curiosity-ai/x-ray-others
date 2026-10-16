namespace XRay.Email.Msg.Mime;

/// <summary>A message/rfc822 part written straight from its model, without serialising it first.</summary>
internal sealed class MimeEmbeddedMessage(IEnumerable<MimeHeader> headers, MimeMessageModel message) : MimeEntity(headers)
{
    public MimeMessageModel Message { get; } = message;
}
