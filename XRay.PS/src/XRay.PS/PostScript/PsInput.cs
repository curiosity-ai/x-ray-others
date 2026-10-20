// Not in the original: finds the PostScript program inside a wrapped job.

namespace XRay.PS.PostScript;

/// <summary>Finds the PostScript program inside the two wrappers a PS/EPS file commonly carries,
/// which the original fed to the interpreter as they were (so the job failed or came out blank):
/// the DOS EPS binary header, and a PJL job header around a print-ready file.</summary>
/// <remarks>Deviation from the original. A file in neither wrapper is returned as it is (the same
/// array), so its output does not change.</remarks>
internal static class PsInput
{
    /// <summary>The first four bytes of a DOS EPS binary file (C5 D0 D3 C6).</summary>
    private const uint DosEpsMagic = 0xC6D3D0C5;

    /// <summary>Size of the DOS EPS header: magic, then offset and length of the PostScript, WMF
    /// and TIFF sections as little-endian 32-bit integers, then a 16-bit checksum.</summary>
    private const int DosEpsHeaderLength = 30;

    /// <summary>The PJL Universal Exit Language sequence, <c>ESC %-12345X</c>.</summary>
    private static ReadOnlySpan<byte> Uel => "\u001b%-12345X"u8;

    /// <summary>True when <paramref name="data"/> starts with the DOS EPS binary header.</summary>
    public static bool IsDosEps(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BitConverter.ToUInt32(data[..4]) == DosEpsMagic;

    /// <summary>Returns the PostScript program in <paramref name="data"/>: the PostScript section
    /// of a DOS EPS binary file, the job between a PJL header and the next UEL, or the input itself.</summary>
    /// <exception cref="PsSyntaxError">A DOS EPS header that is truncated or points outside the file.</exception>
    public static byte[] Unwrap(byte[] data)
    {
        if (IsDosEps(data))
            return DosEpsSection(data);
        if (data.AsSpan().StartsWith(Uel))
            return PjlJob(data);
        return data;
    }

    private static byte[] DosEpsSection(byte[] data)
    {
        if (data.Length < DosEpsHeaderLength)
            throw new PsSyntaxError("truncated DOS EPS header");
        uint offset = ReadUInt32(data, 4);
        uint length = ReadUInt32(data, 8);
        if (offset < DosEpsHeaderLength || offset >= (uint)data.Length)
            throw new PsSyntaxError("DOS EPS PostScript section lies outside the file");
        // A section running past the end of the file is read up to the end, as a truncated
        // plain PostScript file would be.
        long available = data.Length - (long)offset;
        int count = (int)Math.Min(length, available);
        if (count == 0)
            throw new PsSyntaxError("DOS EPS PostScript section is empty");
        return data.AsSpan((int)offset, count).ToArray();
    }

    private static byte[] PjlJob(byte[] data)
    {
        // The job starts at the first line that opens with %!, after the @PJL commands; without
        // one, after the line that switches the printer to PostScript.
        int start = -1;
        int enter = -1;
        int pos = Uel.Length;
        while (pos < data.Length)
        {
            int end = LineEnd(data, pos);
            var line = data.AsSpan(pos, end - pos);
            if (line.StartsWith("%!"u8))
            {
                start = pos;
                break;
            }
            if (!line.TrimStart((byte)' ').StartsWith("@PJL"u8) && !line.StartsWith(Uel) && line.Trim(" \t\r\n"u8).Length > 0)
            {
                // Not a PJL command: the program itself, with no %! line.
                start = pos;
                break;
            }
            if (ContainsIgnoreCase(line, "ENTER LANGUAGE"u8))
                enter = SkipLineBreak(data, end);
            pos = SkipLineBreak(data, end);
        }
        if (start < 0)
            start = enter >= 0 ? enter : data.Length;
        int stop = data.AsSpan(start).IndexOf(Uel);
        int finish = stop < 0 ? data.Length : start + stop;
        return data.AsSpan(start, finish - start).ToArray();
    }

    private static uint ReadUInt32(byte[] data, int at) =>
        (uint)(data[at] | data[at + 1] << 8 | data[at + 2] << 16 | data[at + 3] << 24);

    private static int LineEnd(byte[] data, int pos)
    {
        int i = pos;
        while (i < data.Length && data[i] != '\n' && data[i] != '\r')
            i++;
        return i;
    }

    private static int SkipLineBreak(byte[] data, int pos)
    {
        if (pos < data.Length && data[pos] == '\r')
            pos++;
        if (pos < data.Length && data[pos] == '\n')
            pos++;
        return pos;
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && char.ToUpperInvariant((char)haystack[i + j]) == needle[j])
                j++;
            if (j == needle.Length)
                return true;
        }
        return false;
    }
}
