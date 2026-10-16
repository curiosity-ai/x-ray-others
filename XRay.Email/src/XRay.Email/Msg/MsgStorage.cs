namespace XRay.Email.Msg;

public sealed class MsgStorage
{
    public MsgStorage(string name, MsgStorageRole role = MsgStorageRole.Generic)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Role = role;
    }

    public string Name { get; set; }

    public MsgStorageRole Role { get; set; }

    public byte[] Clsid { get; set; } = new byte[16];

    public uint StateBits { get; set; }

    public ulong CreationTime { get; set; }

    public ulong ModifiedTime { get; set; }

    public IList<MsgStream> Streams { get; } = [];

    public IList<MsgStorage> Storages { get; } = [];

    public MsgStream AddStream(MsgStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Streams.Add(stream);
        return stream;
    }

    public MsgStorage AddStorage(MsgStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        Storages.Add(storage);
        return storage;
    }

    public MsgStream? FindStream(string name) => Streams.FirstOrDefault(item => item.Name == name);

    public MsgStorage? FindStorage(string name) => Storages.FirstOrDefault(item => item.Name == name);

    internal string? PropertyHeaderKind { get; set; }

    internal object? PropertyStreamHeader { get; set; }

    internal IReadOnlyList<PropertyEntryFixedLength> FixedLengthProperties { get; set; } = [];

    internal string? PropertyStreamParseError { get; set; }
}
