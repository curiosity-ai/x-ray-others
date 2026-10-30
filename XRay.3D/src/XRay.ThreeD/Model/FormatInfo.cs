namespace XRay.ThreeD;

/// <summary>How far this library gets with a format.</summary>
public enum FormatSupport
{
    /// <summary>Recognised by name or content only.</summary>
    Identified,

    /// <summary>Metadata can be read; geometry cannot be decoded.</summary>
    Metadata,

    /// <summary>Metadata and geometry.</summary>
    Full,
}

/// <summary>A file format the library knows about.</summary>
public sealed record FormatInfo(string Id, string Name, string Category, string[] Extensions, FormatSupport Support)
{
    public static FormatInfo Unknown { get; } = new("unknown", "Unknown", "unknown", [], FormatSupport.Identified);

    public bool IsUnknown => Id == "unknown";
}
