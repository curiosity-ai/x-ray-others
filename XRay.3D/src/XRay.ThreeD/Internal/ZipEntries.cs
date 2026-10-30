using System.IO.Compression;

namespace XRay.ThreeD.Internal;

/// <summary>Bounded reading of ZIP packages: an entry's declared size is never trusted.</summary>
internal static class ZipEntries
{
    public const int MaxEntries = 100_000;

    public static ZipArchive Open(ReadOnlyMemory<byte> data)
    {
        try
        {
            var zip = new ZipArchive(new MemoryStream(data.ToArray(), writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries)
            {
                zip.Dispose();
                throw new ModelFormatException("The ZIP package has too many entries.");
            }

            return zip;
        }
        catch (InvalidDataException ex)
        {
            throw new ModelFormatException("The ZIP package is damaged: " + ex.Message, ex);
        }
    }

    /// <summary>Inflates one entry, refusing more than <paramref name="limit"/> bytes however large it claims to be.</summary>
    public static byte[] Read(ZipArchiveEntry entry, long limit, CancellationToken cancel = default)
    {
        if (entry.Length > limit)
        {
            throw new ModelFormatException($"The ZIP entry '{entry.FullName}' is larger than the {limit} byte limit.");
        }

        using var stream = entry.Open();

        // The declared size is only a hint for the first allocation; it is never trusted beyond a small cap.
        var output = new MemoryStream((int)Math.Clamp(entry.Length, 0, 1 << 20));
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (output.Length + read > limit)
                {
                    throw new ModelFormatException($"The ZIP entry '{entry.FullName}' is larger than the {limit} byte limit.");
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return output.ToArray();
    }

    public static ZipArchiveEntry? Find(ZipArchive zip, string name) =>
        zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
}
