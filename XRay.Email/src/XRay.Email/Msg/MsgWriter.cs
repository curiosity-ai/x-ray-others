using XRay.Email.Cfb;

namespace XRay.Email.Msg;

public static class MsgWriter
{
    public static byte[] ToBytes(MsgDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return CfbWriter.ToBytes(document.ToCfbDocument());
    }

    public static void WriteFile(MsgDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, ToBytes(document));
    }
}
