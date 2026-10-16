using System.Globalization;
using System.Text;
using XRay.Ics.Internal;

namespace XRay.Ics;

/// <summary>
/// An ATTACH property: either inline data (<c>ENCODING=BASE64</c> or a <c>data:</c> URI) or a
/// reference to a URI. <see cref="Name"/> comes from FILENAME (or X-FILENAME, X-APPLE-FILENAME) and,
/// for a URI, from the last path segment.
/// </summary>
public sealed class CalendarAttachment
{
    private readonly IcsProperty? property;
    private byte[]? cached;

    public CalendarAttachment(string name, byte[] data, string? contentType = null)
    {
        Name = name;
        cached = data ?? throw new ArgumentNullException(nameof(data));
        ContentType = contentType;
    }

    public CalendarAttachment(Uri uri, string? name = null, string? contentType = null)
    {
        Uri = (uri ?? throw new ArgumentNullException(nameof(uri))).ToString();
        Name = name ?? NameFromUri(Uri);
        ContentType = contentType;
    }

    internal CalendarAttachment(IcsProperty property)
    {
        this.property = property;
        ContentType = property.GetParameter("FMTTYPE");
        Name = property.GetParameter("FILENAME") ?? property.GetParameter("X-FILENAME") ?? property.GetParameter("X-APPLE-FILENAME") ?? property.GetParameter("NAME");
        var encoding = property.GetParameter("ENCODING");
        var value = property.GetParameter("VALUE");
        var raw = property.Value.Trim();
        if (string.Equals(encoding, "BASE64", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "BINARY", StringComparison.OrdinalIgnoreCase))
        {
            IsInline = true;
        }
        else if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            IsInline = true;
            IsDataUri = true;
        }
        else
        {
            IsInline = false;
            Uri = raw;
            Name ??= NameFromUri(raw);
        }

        if (long.TryParse(property.GetParameter("SIZE"), NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            DeclaredSize = size;
        }
    }

    /// <summary>The file name, or null when the attachment names none.</summary>
    public string? Name { get; set; }

    /// <summary>The FMTTYPE media type as written, or null. Producers are not reliable about it; derive from <see cref="Name"/> when it matters.</summary>
    public string? ContentType { get; set; }

    /// <summary>The URI for a referenced (not inline) attachment.</summary>
    public string? Uri { get; }

    /// <summary>True when the bytes are in the calendar itself.</summary>
    public bool IsInline { get; private set; } = true;

    /// <summary>The SIZE parameter, when the producer wrote one.</summary>
    public long? DeclaredSize { get; }

    private bool IsDataUri { get; }

    /// <summary>True when <see cref="Open"/> can return content (inline data).</summary>
    public bool HasData => IsInline && (cached is not null || property is not null);

    /// <summary>The decoded size in bytes, or null for a referenced attachment.</summary>
    public long? Size => HasData ? GetBytes().LongLength : null;

    /// <summary>The decoded bytes of an inline attachment (cached after the first call). Empty for a URI reference.</summary>
    public byte[] GetBytes()
    {
        if (cached is not null)
        {
            return cached;
        }

        if (property is null || !IsInline)
        {
            return [];
        }

        using var memory = new MemoryStream();
        WriteTo(memory, CancellationToken.None);
        cached = memory.ToArray();
        return cached;
    }

    /// <summary>A read-only stream over the decoded bytes; empty for a URI reference.</summary>
    public Stream Open() => new MemoryStream(GetBytes(), writable: false);

    /// <summary>Writes the decoded bytes to <paramref name="destination"/> without caching them.</summary>
    public void WriteTo(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (cached is not null)
        {
            destination.Write(cached, 0, cached.Length);
            return;
        }

        if (property is null || !IsInline)
        {
            return;
        }

        if (IsDataUri)
        {
            var bytes = DecodeDataUri(property.Value.Trim());
            destination.Write(bytes, 0, bytes.Length);
            return;
        }

        IcsText.DecodeBase64To(property.Value, destination, cancellationToken);
    }

    internal IcsProperty ToProperty()
    {
        if (property is not null)
        {
            return property.Clone();
        }

        var result = new IcsProperty("ATTACH", Uri ?? string.Empty);
        if (!string.IsNullOrEmpty(ContentType))
        {
            result.Parameters.Add(new IcsParameter("FMTTYPE", ContentType!));
        }

        if (!string.IsNullOrEmpty(Name))
        {
            result.Parameters.Add(new IcsParameter("FILENAME", Name!));
        }

        if (cached is not null)
        {
            result.Parameters.Add(new IcsParameter("ENCODING", "BASE64"));
            result.Parameters.Add(new IcsParameter("VALUE", "BINARY"));
            result.Value = Convert.ToBase64String(cached);
        }

        return result;
    }

    private static string? NameFromUri(string uri)
    {
        var text = uri;
        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var slash = text.LastIndexOf('/');
        var last = slash >= 0 ? text[(slash + 1)..] : string.Empty;
        if (last.Length == 0 || last.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Uri_Unescape(last);
        }
        catch (UriFormatException)
        {
            return last;
        }
    }

    private static string Uri_Unescape(string value) => System.Uri.UnescapeDataString(value);

    private static byte[] DecodeDataUri(string value)
    {
        var comma = value.IndexOf(',');
        if (comma < 0)
        {
            return [];
        }

        var header = value[5..comma];
        var data = value[(comma + 1)..];
        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return IcsText.DecodeBase64(data);
        }

        try
        {
            return Encoding.UTF8.GetBytes(System.Uri.UnescapeDataString(data));
        }
        catch (UriFormatException)
        {
            return Encoding.UTF8.GetBytes(data);
        }
    }
}
