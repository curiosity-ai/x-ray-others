using System.Text;

namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// The code page of a DWG before AC1021 (which stores UTF-16): the 16-bit number at offset 0x13 of the file indexes a table of Windows and ISO code pages,
/// in the order the reader of the MIT-licensed ACadSharp lists them (see THIRD_PARTY_NOTICES.md). Text that is not valid in the page, or a page the runtime does
/// not know, is read as Latin-1.
/// </summary>
internal static class DwgCodePages
{
    /// <summary>Windows code page numbers by DWG code page id (0 is undefined).</summary>
    private static readonly int[] Table =
    [
        0, 20127, 28591, 28592, 28593, 28594, 28595, 28596, 28597, 28598, 28599, 437, 850, 852, 855, 857, 860, 861, 863, 864, 865, 869, 932, 10000, 950, 949, 1361,
        866, 1250, 1251, 1252, 936, 1253, 1254, 1255, 1256, 1257, 874, 932, 936, 949, 950, 1361, 1200, 1258,
    ];

    public static Encoding For(int id)
    {
        if (id <= 0 || id >= Table.Length || Table[id] == 1200)
        {
            return Encoding.Latin1;
        }

        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(Table[id]) ?? Encoding.Latin1;
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    }
}
