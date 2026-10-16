namespace XRay.Email.Cfb;

public static class CfbConstants
{
    /// <summary>Byte order marker value that identifies little-endian encoding for integer fields in CFB structures.</summary>
    public const uint ByteOrderLittleEndian = 65534u;

    /// <summary>Maximum regular sector number (0xFFFFFFFA).</summary>
    public const uint MAXREGSECT = 4294967290u;

    /// <summary>Maximum regular stream ID (0xFFFFFFFA).</summary>
    public const uint MAXREGSID = 4294967290u;

    /// <summary>Maximum stream size allocated from the mini FAT and mini stream.</summary>
    public const uint MiniStreamCutoffSize = 4096u;

    /// <summary>Terminator or empty pointer (0xFFFFFFFF).</summary>
    public const uint NOSTREAM = 4294967295u;

    /// <summary>Required UTF-16 null-terminated name for the root storage directory entry.</summary>
    public const string RootEntryName = "Root Entry";

    /// <summary>Stream ID of the root directory entry in the first directory sector.</summary>
    public const uint RootStreamId = 0u;

}
