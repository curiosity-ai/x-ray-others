namespace XRay.Email.Cfb;

/// <summary>Special FAT marker values reserved for sector allocation metadata.</summary>
public enum SectorMarker : uint
{
    /// <summary>0xFFFFFFFC marker identifying a sector that belongs to the DIFAT chain.</summary>
    DIFSECT = 4294967292u,
    /// <summary>0xFFFFFFFD marker identifying a sector that belongs to the FAT itself.</summary>
    FATSECT = 4294967293u,
    /// <summary>0xFFFFFFFE marker indicating the final sector in a chain.</summary>
    ENDOFCHAIN = 4294967294u,
    /// <summary>0xFFFFFFFF marker indicating an unallocated sector.</summary>
    FREESECT = 4294967295u
}
