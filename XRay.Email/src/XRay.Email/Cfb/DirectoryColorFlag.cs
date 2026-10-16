namespace XRay.Email.Cfb;

/// <summary>Stores the red-black tree color used by directory sibling links.</summary>
public enum DirectoryColorFlag : byte
{
    /// <summary>Directory entry node is red in the sibling red-black tree.</summary>
    Red = (byte)0,
    /// <summary>Directory entry node is black in the sibling red-black tree.</summary>
    Black = (byte)1
}
