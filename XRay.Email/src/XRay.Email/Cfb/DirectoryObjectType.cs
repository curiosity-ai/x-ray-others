namespace XRay.Email.Cfb;

/// <summary>Classifies the directory entry payload as unallocated, storage, stream, or root storage.</summary>
public enum DirectoryObjectType : byte
{
    /// <summary>Directory entry is unallocated or has an unknown object type.</summary>
    UnknownOrUnallocated = (byte)0,
    /// <summary>Directory entry represents a storage object that can contain child entries.</summary>
    StorageObject = (byte)1,
    /// <summary>Directory entry represents a stream object that stores byte data.</summary>
    StreamObject = (byte)2,
    /// <summary>Directory entry represents the root storage object for the compound file.</summary>
    RootStorageObject = (byte)5
}
