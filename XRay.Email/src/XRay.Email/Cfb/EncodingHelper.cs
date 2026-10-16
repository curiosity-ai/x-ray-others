using System.Text;

namespace XRay.Email.Cfb;

internal static class EncodingHelper
{
    public static int GetUtf16ByteCount(string value) => Encoding.Unicode.GetByteCount(value);
}
