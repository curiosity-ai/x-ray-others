namespace XRay.Email.Msg.Mime;

internal sealed class MimeMultipart : MimeEntity
{
    public MimeMultipart(IEnumerable<MimeHeader> headers, string boundary, IEnumerable<MimeEntity> children)
        : base(headers)
    {
        Boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        Children = children?.ToArray() ?? throw new ArgumentNullException(nameof(children));
    }

    public string Boundary { get; }

    public IReadOnlyList<MimeEntity> Children { get; }
}
