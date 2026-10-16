namespace XRay.Email.Msg;

public sealed class MsgStream
{
    public MsgStream(string name, byte[]? data = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Data = data ?? [];
    }

    public string Name { get; set; }

    public byte[] Data { get; set; }

    public byte[] Clsid { get; set; } = new byte[16];

    public uint StateBits { get; set; }

    public ulong CreationTime { get; set; }

    public ulong ModifiedTime { get; set; }
}
