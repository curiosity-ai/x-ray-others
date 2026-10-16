namespace XRay.Email.Cfb;

public sealed class CfbStream : CfbNode
{
    public CfbStream(string name, byte[]? data = null)
        : base(name)
    {
        Data = data ?? [];
    }

    public byte[] Data { get; set; }
}
