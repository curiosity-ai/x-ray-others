namespace XRay.Ics;

/// <summary>
/// Raised when the input is not an iCalendar or vCalendar stream at all (no calendar component
/// can be found), or when it exceeds one of the configured <see cref="IcsParseOptions"/> limits.
/// Damaged calendars that still contain components are read leniently and never raise this.
/// </summary>
public sealed class IcsFormatException : Exception
{
    public IcsFormatException()
    {
    }

    public IcsFormatException(string message)
        : base(message)
    {
    }

    public IcsFormatException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
