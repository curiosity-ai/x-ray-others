namespace XRay.Email.Msg;

internal sealed record PropertyStreamHeaderTopLevel(
    byte[] Reserved0,
    uint NextRecipientId,
    uint NextAttachmentId,
    uint RecipientCount,
    uint AttachmentCount,
    byte[] Reserved1);
