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

    /// <summary>
    /// PR_ATTACH_CONTENT_LOCATION: the URL an HTML body may use to refer to this attachment (the MIME
    /// <c>Content-Location</c> header). Null when there is none.
    /// </summary>
    public string? ContentLocation
    {
        get => Properties.Get(0x3713)?.Value as string;
        set
        {
            Properties.Remove(0x3713);
            if (!string.IsNullOrWhiteSpace(value))
            {
                Properties.Add(0x3713, (ushort)PropertyTypeCode.PtypString, value);
            }
        }
    }

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

    /// <summary>Writes <see cref="Data"/> to <paramref name="destination"/> in blocks, checking the token between them.</summary>
    public void CopyTo(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        for (var offset = 0; offset < Data.Length; offset += 81920)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(Data, offset, Math.Min(81920, Data.Length - offset));
        }
    }
}
