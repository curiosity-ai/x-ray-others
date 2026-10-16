namespace XRay.Email.Msg;

public sealed class MapiAttachment
{
    public static MapiAttachment FromBytes(string filename, byte[] data, string? mimeType = null, string? contentId = null)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(data);
        return new MapiAttachment
        {
            Filename = filename,
            Data = data.ToArray(),
            MimeType = mimeType,
            ContentId = contentId,
        };
    }

    public static MapiAttachment FromStream(string filename, Stream stream, string? mimeType = null, string? contentId = null)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return FromBytes(filename, buffer.ToArray(), mimeType, contentId);
    }

    public string? Filename { get; set; }

    public byte[] Data { get; set; } = [];

    public string? MimeType { get; set; }

    public string? ContentId { get; set; }

    public MapiMessage? EmbeddedMessage { get; set; }

    public MapiPropertyCollection Properties { get; } = new();

    public bool IsEmbeddedMessage => EmbeddedMessage is not null || AttachMethod == MapiMessage.AttachMethodEmbedded;

    public bool IsStorageAttachment => CustomStorage is not null || AttachMethod == MapiMessage.AttachMethodStorage;

    internal int AttachMethod { get; set; } = MapiMessage.AttachMethodByValue;

    internal int? StorageId { get; set; }

    internal MsgStorage? CustomStorage { get; set; }

    internal IList<MsgStream> ExtraStreams { get; } = [];

    internal byte[] HeaderReserved0 { get; set; } = new byte[8];

    public void LoadData(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Data = buffer.ToArray();
    }

    public Stream OpenRead()
    {
        return new MemoryStream(Data, writable: false);
    }
}
