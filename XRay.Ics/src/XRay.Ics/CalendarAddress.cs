using System.Globalization;
using XRay.Ics.Internal;

namespace XRay.Ics;

/// <summary>
/// A calendar user: the value of an ORGANIZER or ATTENDEE property with its parameters
/// (CN, ROLE, PARTSTAT, RSVP, CUTYPE, ...). <see cref="Email"/> has the <c>mailto:</c> prefix removed.
/// </summary>
public sealed class CalendarAddress
{
    public CalendarAddress()
    {
    }

    public CalendarAddress(string? email, string? commonName = null)
    {
        Email = Normalize(email);
        Uri = Email is null ? string.Empty : "mailto:" + Email;
        CommonName = commonName;
    }

    /// <summary>The calendar user address as written (<c>mailto:a@b.example</c>, or whatever URI the producer used).</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>The e-mail address: the mailto: value, else the EMAIL parameter some producers add, else a bare address.</summary>
    public string? Email { get; set; }

    /// <summary>The CN parameter: the display name.</summary>
    public string? CommonName { get; set; }

    /// <summary>The e-mail address when there is one, otherwise the raw URI without a mailto: prefix (never null).</summary>
    public string Address => Email ?? StripMailto(Uri);

    /// <summary>The display name when there is one, otherwise the address.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(CommonName) ? Address : CommonName!;

    public AttendeeRole Role { get; set; } = AttendeeRole.RequiredParticipant;

    public ParticipationStatus Status { get; set; } = ParticipationStatus.NeedsAction;

    public bool? Rsvp { get; set; }

    public CalendarUserType UserType { get; set; } = CalendarUserType.Individual;

    public string? SentBy { get; set; }

    public string? Directory { get; set; }

    public List<string> DelegatedTo { get; } = [];

    public List<string> DelegatedFrom { get; } = [];

    public List<string> Members { get; } = [];

    public override string ToString() => string.IsNullOrWhiteSpace(CommonName) ? Address : $"{CommonName} <{Address}>";

    internal static CalendarAddress FromProperty(IcsProperty property)
    {
        var address = new CalendarAddress();
        var value = property.Value.Trim();
        // vCalendar 1.0 and sloppy producers: "Name <a@b.example>" instead of a URI.
        var lt = value.IndexOf('<');
        var gt = value.IndexOf('>');
        string? inlineName = null;
        if (lt >= 0 && gt > lt && !value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            inlineName = value[..lt].Trim().Trim('"');
            value = value[(lt + 1)..gt].Trim();
        }

        address.Uri = value;
        address.Email = Normalize(value) ?? Normalize(property.GetParameter("EMAIL"));
        address.CommonName = property.GetParameter("CN");
        if (string.IsNullOrWhiteSpace(address.CommonName) && !string.IsNullOrWhiteSpace(inlineName))
        {
            address.CommonName = inlineName;
        }

        if (string.IsNullOrWhiteSpace(address.CommonName))
        {
            address.CommonName = null;
        }

        var role = property.GetParameter("ROLE");
        address.Role = role?.ToUpperInvariant() switch
        {
            "OPT-PARTICIPANT" => AttendeeRole.OptionalParticipant,
            "NON-PARTICIPANT" => AttendeeRole.NonParticipant,
            "CHAIR" => AttendeeRole.Chair,
            "OWNER" or "ORGANIZER" => AttendeeRole.Chair,
            _ => AttendeeRole.RequiredParticipant,
        };
        if (string.Equals(property.GetParameter("EXPECT"), "FYI", StringComparison.OrdinalIgnoreCase) && role is null)
        {
            address.Role = AttendeeRole.OptionalParticipant;
        }

        address.Status = (property.GetParameter("PARTSTAT") ?? property.GetParameter("STATUS"))?.ToUpperInvariant() switch
        {
            "ACCEPTED" => ParticipationStatus.Accepted,
            "DECLINED" => ParticipationStatus.Declined,
            "TENTATIVE" => ParticipationStatus.Tentative,
            "DELEGATED" => ParticipationStatus.Delegated,
            "COMPLETED" => ParticipationStatus.Completed,
            "IN-PROCESS" => ParticipationStatus.InProcess,
            _ => ParticipationStatus.NeedsAction,
        };
        var rsvp = property.GetParameter("RSVP");
        if (rsvp is not null)
        {
            address.Rsvp = rsvp.ToUpperInvariant() is "TRUE" or "YES" or "1";
        }

        address.UserType = property.GetParameter("CUTYPE")?.ToUpperInvariant() switch
        {
            null => CalendarUserType.Individual,
            "INDIVIDUAL" => CalendarUserType.Individual,
            "GROUP" => CalendarUserType.Group,
            "RESOURCE" => CalendarUserType.Resource,
            "ROOM" => CalendarUserType.Room,
            _ => CalendarUserType.Unknown,
        };
        var sentBy = property.GetParameter("SENT-BY");
        address.SentBy = sentBy is null ? null : Normalize(sentBy) ?? sentBy;
        address.Directory = property.GetParameter("DIR");
        foreach (var item in property.GetParameterValues("DELEGATED-TO"))
        {
            address.DelegatedTo.Add(StripMailto(item));
        }

        foreach (var item in property.GetParameterValues("DELEGATED-FROM"))
        {
            address.DelegatedFrom.Add(StripMailto(item));
        }

        foreach (var item in property.GetParameterValues("MEMBER"))
        {
            address.Members.Add(StripMailto(item));
        }

        return address;
    }

    internal IcsProperty ToProperty(string name, bool attendee)
    {
        var property = new IcsProperty(name, string.IsNullOrEmpty(Uri) ? (Email is null ? string.Empty : "mailto:" + Email) : Uri);
        if (!string.IsNullOrWhiteSpace(CommonName))
        {
            property.Parameters.Add(new IcsParameter("CN", CommonName!));
        }

        if (attendee)
        {
            if (UserType != CalendarUserType.Individual)
            {
                property.Parameters.Add(new IcsParameter("CUTYPE", UserType.ToString().ToUpperInvariant()));
            }

            if (Role != AttendeeRole.RequiredParticipant)
            {
                property.Parameters.Add(new IcsParameter("ROLE", Role switch
                {
                    AttendeeRole.OptionalParticipant => "OPT-PARTICIPANT",
                    AttendeeRole.NonParticipant => "NON-PARTICIPANT",
                    _ => "CHAIR",
                }));
            }

            property.Parameters.Add(new IcsParameter("PARTSTAT", Status switch
            {
                ParticipationStatus.InProcess => "IN-PROCESS",
                _ => Status.ToString().ToUpperInvariant().Replace("NEEDSACTION", "NEEDS-ACTION", StringComparison.Ordinal),
            }));
            if (Rsvp is { } rsvp)
            {
                property.Parameters.Add(new IcsParameter("RSVP", rsvp ? "TRUE" : "FALSE"));
            }
        }

        if (!string.IsNullOrEmpty(SentBy))
        {
            property.Parameters.Add(new IcsParameter("SENT-BY", "mailto:" + SentBy));
        }

        if (!string.IsNullOrEmpty(Directory))
        {
            property.Parameters.Add(new IcsParameter("DIR", Directory!));
        }

        return property;
    }

    internal static string StripMailto(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..];
            var query = text.IndexOf('?');
            if (query >= 0)
            {
                text = text[..query];
            }
        }

        return text.Trim();
    }

    /// <summary>Returns the e-mail address a calendar user address or bare string carries, or null.</summary>
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var stripped = StripMailto(text);
            return stripped.Length == 0 ? null : stripped;
        }

        // A bare address; anything with a URI scheme (urn:, http:, /principals/...) is not one.
        if (text.Contains('@', StringComparison.Ordinal) && !text.Contains(':', StringComparison.Ordinal) && !text.Contains('/', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal))
        {
            return text;
        }

        return null;
    }
}
