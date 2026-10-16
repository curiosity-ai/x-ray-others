namespace XRay.Ics;

/// <summary>
/// Reads an .ics file and returns the fields a document indexer needs; the counterpart of loading an
/// appointment, reading its organizer, attendees, subject, body and attachments, and rendering its HTML.
/// </summary>
public static class IcsExtractor
{
    public static IcsExtractResult Extract(Stream stream, IcsExtractOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new IcsExtractOptions();
        var calendar = Calendar.Load(stream, options.Parse, cancellationToken);
        return new IcsExtractResult(calendar, options.Render);
    }

    public static IcsExtractResult Extract(byte[] data, IcsExtractOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= new IcsExtractOptions();
        var calendar = Calendar.Load(data, options.Parse, cancellationToken);
        return new IcsExtractResult(calendar, options.Render);
    }

    public static IcsExtractResult Extract(string text, IcsExtractOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new IcsExtractOptions();
        var calendar = Calendar.Parse(text, options.Parse, cancellationToken);
        return new IcsExtractResult(calendar, options.Render);
    }

    public static IcsExtractResult ExtractFile(string path, IcsExtractOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new IcsExtractOptions();
        var calendar = Calendar.LoadFile(path, options.Parse, cancellationToken);
        return new IcsExtractResult(calendar, options.Render);
    }
}
