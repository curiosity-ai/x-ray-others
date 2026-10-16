using XRay.Email.Cfb;

namespace XRay.Email.Msg;

public static class MsgWriter
{
    public static byte[] ToBytes(MsgDocument document) => ToBytes(document, CancellationToken.None);

    public static byte[] ToBytes(MsgDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return CfbWriter.ToBytes(document.ToCfbDocument(), cancellationToken);
    }

    /// <summary>Writes the MSG container to <paramref name="stream"/> one sector at a time.</summary>
    public static void Write(MsgDocument document, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        CfbWriter.Write(document.ToCfbDocument(), stream, cancellationToken);
    }

    public static void WriteFile(MsgDocument document, string path) => WriteFile(document, path, CancellationToken.None);

    public static void WriteFile(MsgDocument document, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        CfbWriter.WriteFile(document.ToCfbDocument(), path, cancellationToken);
    }
}
