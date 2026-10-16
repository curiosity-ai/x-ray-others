using System.Globalization;
using XRay.Ics.Internal;

namespace XRay.Ics;

/// <summary>
/// The typed view shared by VEVENT, VTODO and VJOURNAL. The view reads and writes the underlying
/// <see cref="Raw"/> component, so properties it has no accessor for (X- properties above all)
/// are never lost.
/// </summary>
public abstract class CalendarComponent
{
    private protected CalendarComponent(IcsComponent raw)
    {
        Raw = raw ?? throw new ArgumentNullException(nameof(raw));
    }

    public IcsComponent Raw { get; }

    /// <summary>The calendar this component belongs to, or null while it is not added to one.</summary>
    public Calendar? Calendar { get; internal set; }

    private ZoneResolver Resolver => Calendar?.Resolver ?? ZoneResolver.SystemOnly;

    private TimeSpan? LegacyOffset => Calendar?.LegacyUtcOffset;

    public string? Uid
    {
        get => Raw.Get("UID")?.TextValue.Trim();
        set => Raw.Set("UID", value is null ? null : IcsText.Escape(value));
    }

    public string? Summary
    {
        get => Raw.Get("SUMMARY")?.TextValue;
        set => Raw.Set("SUMMARY", value is null ? null : IcsText.Escape(value));
    }

    public string? Description
    {
        get => Raw.Get("DESCRIPTION")?.TextValue;
        set => Raw.Set("DESCRIPTION", value is null ? null : IcsText.Escape(value));
    }

    /// <summary>
    /// The HTML description some producers add beside the plain one: Outlook's <c>X-ALT-DESC;FMTTYPE=text/html</c>.
    /// </summary>
    public string? HtmlDescription
    {
        get
        {
            foreach (var property in Raw.GetAll("X-ALT-DESC"))
            {
                var type = property.GetParameter("FMTTYPE");
                if (type is null || type.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    return property.TextValue;
                }
            }

            return null;
        }
    }

    public string? Comment => Raw.Get("COMMENT")?.TextValue;

    public string? Url
    {
        get => Raw.Get("URL")?.Value.Trim();
        set => Raw.Set("URL", value);
    }

    public int? Sequence => int.TryParse(Raw.GetValue("SEQUENCE")?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public IcsDateTime? Created
    {
        get => GetDate("CREATED");
        set => DateParsing.Write(Raw, "CREATED", value);
    }

    public IcsDateTime? LastModified
    {
        get => GetDate("LAST-MODIFIED");
        set => DateParsing.Write(Raw, "LAST-MODIFIED", value);
    }

    /// <summary>DTSTAMP.</summary>
    public IcsDateTime? Stamp
    {
        get => GetDate("DTSTAMP");
        set => DateParsing.Write(Raw, "DTSTAMP", value);
    }

    /// <summary>DTSTART with its TZID resolved (VTIMEZONE rules, then IANA and Windows names).</summary>
    public IcsDateTime? Start
    {
        get => GetDate("DTSTART");
        set => DateParsing.Write(Raw, "DTSTART", value);
    }

    public IcsDateTime? RecurrenceId => GetDate("RECURRENCE-ID");

    public CalendarStatus Status => Raw.GetValue("STATUS")?.Trim().ToUpperInvariant() switch
    {
        "TENTATIVE" => CalendarStatus.Tentative,
        "CONFIRMED" => CalendarStatus.Confirmed,
        "CANCELLED" or "CANCELED" => CalendarStatus.Cancelled,
        "NEEDS-ACTION" => CalendarStatus.NeedsAction,
        "COMPLETED" => CalendarStatus.Completed,
        "IN-PROCESS" => CalendarStatus.InProcess,
        "DRAFT" => CalendarStatus.Draft,
        "FINAL" => CalendarStatus.Final,
        _ => CalendarStatus.None,
    };

    public CalendarClassification Classification => Raw.GetValue("CLASS")?.Trim().ToUpperInvariant() switch
    {
        "PUBLIC" => CalendarClassification.Public,
        "PRIVATE" => CalendarClassification.Private,
        "CONFIDENTIAL" => CalendarClassification.Confidential,
        _ => CalendarClassification.None,
    };

    /// <summary>The iTIP METHOD of the calendar this component is in.</summary>
    public CalendarMethod Method => Calendar?.Method ?? CalendarMethod.None;

    public CalendarAddress? Organizer
    {
        get
        {
            var property = Raw.Get("ORGANIZER");
            return property is null ? null : CalendarAddress.FromProperty(property);
        }

        set
        {
            Raw.Properties.RemoveAll(p => p.Name == "ORGANIZER");
            if (value is not null)
            {
                Raw.Properties.Add(value.ToProperty("ORGANIZER", attendee: false));
            }
        }
    }

    /// <summary>Every ATTENDEE, in file order.</summary>
    public IReadOnlyList<CalendarAddress> Attendees => [.. Raw.GetAll("ATTENDEE").Select(CalendarAddress.FromProperty)];

    /// <summary>Attendees whose role is required (the default), excluding chairs, resources and rooms.</summary>
    public IReadOnlyList<CalendarAddress> RequiredAttendees => [.. Attendees.Where(a => a.Role == AttendeeRole.RequiredParticipant && !IsResource(a))];

    public IReadOnlyList<CalendarAddress> OptionalAttendees => [.. Attendees.Where(a => a.Role == AttendeeRole.OptionalParticipant && !IsResource(a))];

    public IReadOnlyList<CalendarAddress> NonParticipants => [.. Attendees.Where(a => a.Role == AttendeeRole.NonParticipant && !IsResource(a))];

    public IReadOnlyList<CalendarAddress> Chairs => [.. Attendees.Where(a => a.Role == AttendeeRole.Chair && !IsResource(a))];

    /// <summary>Attendees of type RESOURCE or ROOM.</summary>
    public IReadOnlyList<CalendarAddress> Resources => [.. Attendees.Where(IsResource)];

    public void AddAttendee(CalendarAddress attendee)
    {
        ArgumentNullException.ThrowIfNull(attendee);
        Raw.Properties.Add(attendee.ToProperty("ATTENDEE", attendee: true));
    }

    public IReadOnlyList<string> Categories => [.. Raw.GetAll("CATEGORIES").SelectMany(p => p.TextValues).Select(c => c.Trim()).Where(c => c.Length > 0)];

    public IReadOnlyList<CalendarAttachment> Attachments => [.. Raw.GetAll("ATTACH").Select(p => new CalendarAttachment(p))];

    public void AddAttachment(CalendarAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        Raw.Properties.Add(attachment.ToProperty());
    }

    /// <summary>The RRULE properties (several are legal but rare). vCalendar 1.0 rules are converted.</summary>
    public IReadOnlyList<RecurrenceRule> RecurrenceRules
    {
        get
        {
            var rules = new List<RecurrenceRule>();
            foreach (var property in Raw.GetAll("RRULE"))
            {
                if (property.Value.Contains("FREQ=", StringComparison.OrdinalIgnoreCase))
                {
                    if (RecurrenceRule.TryParse(property.Value, out var rule))
                    {
                        rules.Add(rule);
                    }
                }
                else if (RecurrenceRule.TryParseVCalendar1(property.Value, out var legacy))
                {
                    rules.Add(legacy);
                }
            }

            return rules;
        }
    }

    public IReadOnlyList<IcsDateTime> RecurrenceDates => ReadDates("RDATE");

    public IReadOnlyList<IcsDateTime> ExceptionDates => ReadDates("EXDATE");

    public bool IsRecurring => Raw.GetAll("RRULE").Any(p => !string.IsNullOrWhiteSpace(p.Value)) || Raw.GetAll("RDATE").Any(p => !string.IsNullOrWhiteSpace(p.Value));

    /// <summary>Replaces the RRULE.</summary>
    public void SetRecurrence(RecurrenceRule? rule) => Raw.Set("RRULE", rule?.ToString());

    /// <summary>A named property parsed as a date (for X- properties and the less common ones).</summary>
    public IcsDateTime? GetDate(string name)
    {
        var property = Raw.Get(name);
        return property is null ? null : DateParsing.Parse(property, Resolver, LegacyOffset);
    }

    private static bool IsResource(CalendarAddress address) => address.UserType is CalendarUserType.Resource or CalendarUserType.Room;

    private List<IcsDateTime> ReadDates(string name)
    {
        var result = new List<IcsDateTime>();
        foreach (var property in Raw.GetAll(name))
        {
            var tzid = property.GetParameter("TZID");
            foreach (var item in property.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                // A PERIOD (start/end or start/duration) contributes its start.
                var slash = item.IndexOf('/');
                var text = slash >= 0 ? item[..slash] : item;
                if (DateParsing.ParseText(text, tzid, Resolver, LegacyOffset) is { } date)
                {
                    result.Add(date.IsDate || !string.Equals(property.GetParameter("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase) ? date : new IcsDateTime(date.Value, IcsDateTimeKind.Date));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The start of every instance (DTSTART, RRULE and RDATE instances, minus EXDATE), ascending
    /// and lazy. Without a start there are none.
    /// </summary>
    internal IEnumerable<IcsDateTime> EnumerateStarts(CancellationToken cancellationToken)
    {
        if (Start is not { } start)
        {
            yield break;
        }

        var resolver = Resolver;
        var exWalls = new HashSet<DateTime>();
        var exDates = new HashSet<DateTime>();
        var exInstants = new HashSet<long>();
        foreach (var ex in ExceptionDates)
        {
            if (ex.IsDate)
            {
                exDates.Add(ex.Value.Date);
            }
            else if (ex.Offset is { } offset && start.Offset is not null)
            {
                exInstants.Add(DateTimeMath.UtcTicks(ex.Value, offset));
            }
            else
            {
                exWalls.Add(ex.Value);
            }
        }

        var sources = new List<IEnumerator<DateTime>> { new[] { start.Value }.AsEnumerable().GetEnumerator() };
        foreach (var rule in RecurrenceRules)
        {
            sources.Add(rule.Enumerate(start.Value, UntilWall(rule, start, resolver), cancellationToken).GetEnumerator());
        }

        var rdates = RecurrenceDates.Select(d => d.Value).Distinct().Order().ToList();
        if (rdates.Count > 0)
        {
            sources.Add(rdates.GetEnumerator());
        }

        try
        {
            var heads = new DateTime?[sources.Count];
            for (var i = 0; i < sources.Count; i++)
            {
                heads[i] = sources[i].MoveNext() ? sources[i].Current : null;
            }

            DateTime? last = null;
            while (true)
            {
                var best = -1;
                for (var i = 0; i < heads.Length; i++)
                {
                    if (heads[i] is { } head && (best < 0 || head < heads[best]!.Value))
                    {
                        best = i;
                    }
                }

                if (best < 0)
                {
                    yield break;
                }

                var wall = heads[best]!.Value;
                heads[best] = sources[best].MoveNext() ? sources[best].Current : null;
                if (last == wall)
                {
                    continue;
                }

                last = wall;
                cancellationToken.ThrowIfCancellationRequested();
                var instance = Make(start, wall, resolver);
                if (exWalls.Contains(wall) || exDates.Contains(wall.Date) || (exInstants.Count > 0 && instance.Offset is { } offset && exInstants.Contains(DateTimeMath.UtcTicks(wall, offset))))
                {
                    continue;
                }

                yield return instance;
            }
        }
        finally
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
        }
    }

    internal IcsDateTime Make(IcsDateTime template, DateTime wall, ZoneResolver? resolver = null)
    {
        resolver ??= Resolver;
        return template.Kind switch
        {
            IcsDateTimeKind.Date => new IcsDateTime(wall, IcsDateTimeKind.Date),
            IcsDateTimeKind.Utc => new IcsDateTime(wall, IcsDateTimeKind.Utc),
            IcsDateTimeKind.Floating => new IcsDateTime(wall, IcsDateTimeKind.Floating),
            _ when template.TzId is not null && template.Offset is not null => new IcsDateTime(wall, IcsDateTimeKind.Zoned, template.TzId, FixedOrResolved(template, wall, resolver)),
            _ => new IcsDateTime(wall, IcsDateTimeKind.Zoned, template.TzId, null),
        };
    }

    private static TimeSpan? FixedOrResolved(IcsDateTime template, DateTime wall, ZoneResolver resolver)
    {
        // A legacy vCalendar 1.0 offset ("UTC+01:00") has no zone behind it: keep the fixed offset.
        if (template.TzId!.StartsWith("UTC+", StringComparison.Ordinal) || template.TzId.StartsWith("UTC-", StringComparison.Ordinal))
        {
            return template.Offset;
        }

        return resolver.Resolve(template.TzId, wall) ?? template.Offset;
    }

    private static DateTime? UntilWall(RecurrenceRule rule, IcsDateTime start, ZoneResolver resolver)
    {
        if (rule.Until is not { } until)
        {
            return null;
        }

        if (until.IsDate)
        {
            return DateTimeMath.Add(DateTimeMath.AddDays(until.Value.Date, 1), TimeSpan.FromTicks(-1));
        }

        if (until.IsUtc && start.Kind == IcsDateTimeKind.Zoned && start.TzId is not null && start.Offset is { } startOffset)
        {
            var first = DateTimeMath.Add(until.Value, startOffset);
            var offset = resolver.Resolve(start.TzId, first) ?? startOffset;
            return DateTimeMath.Add(until.Value, offset);
        }

        if (until.IsUtc && start.IsDate)
        {
            return until.Value;
        }

        return until.Value;
    }
}
