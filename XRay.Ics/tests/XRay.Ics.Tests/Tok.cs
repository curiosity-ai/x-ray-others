namespace XRay.Ics.Tests;

internal static class Tok
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Wraps component lines in a VCALENDAR with CRLF line ends.</summary>
    public static string Cal(params string[] lines) => string.Join("\r\n", ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//test//EN", .. lines, "END:VCALENDAR"]) + "\r\n";

    public static Calendar Load(string text) => Calendar.Parse(text, null, Ct);

    public static Calendar Load(byte[] data) => Calendar.Load(data, null, Ct);

    public static CalendarEvent OneEvent(params string[] lines) => Load(Cal(["BEGIN:VEVENT", "UID:t@example.com", .. lines, "END:VEVENT"])).Events.Single();
}
