namespace XRay.Email.Cfb;

public sealed record Header(
    byte[] HeaderSignature,
    byte[] HeaderClsid,
    ushort MinorVersion,
    ushort MajorVersion,
    ushort ByteOrder,
    ushort SectorShift,
    ushort MiniSectorShift,
    byte[] Reserved,
    uint NumberOfDirectorySectors,
    uint NumberOfFatSectors,
    uint FirstDirectorySectorLocation,
    uint TransactionSignatureNumber,
    uint MiniStreamCutoffSize,
    uint FirstMiniFatSectorLocation,
    uint NumberOfMiniFatSectors,
    uint FirstDifatSectorLocation,
    uint NumberOfDifatSectors,
    uint[] Difat)
{
    public int SectorSize => 1 << SectorShift;

    public int MiniSectorSize => 1 << MiniSectorShift;
}
