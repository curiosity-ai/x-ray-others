namespace XRay.Email.Cfb;

public sealed class CfbDocument
{
    public CfbDocument()
        : this(new CfbStorage(CfbConstants.RootEntryName))
    {
    }

    public CfbDocument(CfbStorage root, ushort majorVersion = 3, ushort minorVersion = 0x003E, uint transactionSignatureNumber = 0)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        TransactionSignatureNumber = transactionSignatureNumber;
    }

    public CfbStorage Root { get; }

    public ushort MajorVersion { get; set; }

    public ushort MinorVersion { get; set; }

    public uint TransactionSignatureNumber { get; set; }

    public static CfbDocument FromReader(CfbReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new CfbDocument(
            BuildStorage(reader, CfbConstants.RootStreamId),
            reader.Header.MajorVersion,
            reader.Header.MinorVersion,
            reader.Header.TransactionSignatureNumber);
    }

    public static CfbDocument FromFile(string path)
    {
        using var reader = CfbReader.FromFile(path);
        return FromReader(reader);
    }

    public static CfbDocument FromStream(Stream stream)
    {
        using var reader = CfbReader.FromStream(stream);
        return FromReader(reader);
    }

    private static CfbStorage BuildStorage(CfbReader reader, uint streamId)
    {
        var entry = reader.GetEntry(streamId);
        var storage = new CfbStorage(entry.Name)
        {
            Clsid = entry.Clsid.ToArray(),
            StateBits = entry.StateBits,
            CreationTime = entry.CreationTime,
            ModifiedTime = entry.ModifiedTime,
        };

        foreach (var child in reader.IterChildren(streamId))
        {
            if (child.IsStorage())
            {
                storage.Children.Add(BuildStorage(reader, child.StreamId));
                continue;
            }

            if (child.IsStream())
            {
                storage.Children.Add(new CfbStream(child.Name, reader.GetStreamData(child.StreamId))
                {
                    Clsid = child.Clsid.ToArray(),
                    StateBits = child.StateBits,
                    CreationTime = child.CreationTime,
                    ModifiedTime = child.ModifiedTime,
                });
            }
        }

        return storage;
    }
}
