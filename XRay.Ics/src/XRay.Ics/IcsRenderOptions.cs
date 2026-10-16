using System.Globalization;

namespace XRay.Ics;

/// <summary>Options for the HTML, plain text and Markdown renderings.</summary>
public sealed class IcsRenderOptions
{
    /// <summary>
    /// When set, timed values are converted to this zone and labelled with its offset ("apply local
    /// time zone"). When null, each value is shown as written, in its own zone.
    /// </summary>
    public TimeZoneInfo? DisplayTimeZone { get; set; }

    /// <summary>The zone floating times and unresolved zones are read in when converting to <see cref="DisplayTimeZone"/>. Default UTC.</summary>
    public TimeZoneInfo? FloatingTimeZone { get; set; }

    /// <summary>
    /// Render every event, to-do and journal entry instead of only the first. Null picks the default
    /// of the rendering: the first only for HTML (one appointment view), all for plain text and Markdown.
    /// </summary>
    public bool? AllComponents { get; set; }

    /// <summary>List attachments (name and size). Default true.</summary>
    public bool IncludeAttachments { get; set; } = true;

    /// <summary>Culture for day and month names. Default invariant (English), which keeps output deterministic.</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;
}
