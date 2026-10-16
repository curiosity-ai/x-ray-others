using System.Security.Cryptography;

namespace XRay.Email.Msg.Mime;

/// <summary>
/// Creates multipart boundaries. One factory is shared by a message and every message embedded in
/// it, and each factory carries a random token, so a boundary can never repeat inside the nested
/// structure or match a boundary of a message that is being forwarded.
/// </summary>
internal sealed class MimeBoundaryFactory
{
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private int _counter;

    public string NextBoundary(string prefix = "XRayEmail")
    {
        _counter++;
        return $"----=_{prefix}_{_token}_{_counter:X4}";
    }
}
