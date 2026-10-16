namespace XRay.Email.Msg;

public sealed class MapiProperty
{
    public MapiProperty(ushort propertyId, ushort propertyType, object? value, uint flags = MapiMessage.DefaultPropertyFlags)
    {
        PropertyId = propertyId;
        PropertyType = propertyType;
        Value = value;
        Flags = flags;
    }

    public ushort PropertyId { get; }

    public ushort PropertyType { get; }

    public object? Value { get; set; }

    public uint Flags { get; set; }

    public uint PropertyTag => ((uint)PropertyId << 16) | PropertyType;

    internal byte[]? RawInlineValue { get; set; }

    internal uint? RawSizeValue { get; set; }

    internal uint? RawReservedValue { get; set; }

    internal byte[]? RawDirectStream { get; set; }

    internal IReadOnlyList<(int Index, byte[] Payload)> RawIndexedStreams { get; set; } = [];

    internal bool PreserveRaw { get; set; }
}
