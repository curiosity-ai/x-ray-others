namespace XRay.Email.Msg;

/// <summary>Where a <see cref="MapiMessage"/> came from.</summary>
public enum MessageSourceFormat
{
    /// <summary>Built in code, not loaded.</summary>
    Created = 0,

    /// <summary>Loaded from an Outlook MSG (a compound file), or embedded in one.</summary>
    Msg = 1,

    /// <summary>Loaded from an EML (RFC 5322 / MIME), or embedded in one.</summary>
    Eml = 2,
}
