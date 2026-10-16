namespace XRay.Email.Cfb;

public sealed record DirectoryEntry(
    uint StreamId,
    string Name,
    ushort DirectoryEntryNameLength,
    DirectoryObjectType ObjectType,
    DirectoryColorFlag ColorFlag,
    uint LeftSiblingId,
    uint RightSiblingId,
    uint ChildId,
    byte[] Clsid,
    uint StateBits,
    ulong CreationTime,
    ulong ModifiedTime,
    uint StartingSectorLocation,
    ulong StreamSize)
{
    public bool IsStorage() => ObjectType is DirectoryObjectType.StorageObject or DirectoryObjectType.RootStorageObject;

    public bool IsStream() => ObjectType == DirectoryObjectType.StreamObject;

    public bool IsRoot() => ObjectType == DirectoryObjectType.RootStorageObject;
}
