namespace XRay.Email.Cfb;

public abstract class CfbNode
{
    protected CfbNode(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    public string Name { get; set; }

    public byte[] Clsid { get; set; } = new byte[16];

    public uint StateBits { get; set; }

    public ulong CreationTime { get; set; }

    public ulong ModifiedTime { get; set; }
}
