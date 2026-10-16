namespace XRay.Ics;

/// <summary>
/// What a consumer that indexes appointments needs from an .ics file: the primary component's
/// created date, organizer, attendees, subject, body and attachments, and its renderings.
/// The primary component is the first event, else the first to-do, else the first journal entry.
/// </summary>
public sealed class IcsExtractResult
{
    private readonly IcsRenderOptions renderDefaults;

    internal IcsExtractResult(Calendar calendar, IcsRenderOptions renderDefaults)
    {
        Calendar = calendar;
        this.renderDefaults = renderDefaults;
        Component = calendar.Primary;
        if (Component is null)
        {
            return;
        }

        var attendees = Component.Attendees;
        Organizer = Component.Organizer;
        Attendees = [.. attendees.Where(a => a.Role != AttendeeRole.OptionalParticipant && a.UserType is not (CalendarUserType.Resource or CalendarUserType.Room))];
        OptionalAttendees = [.. attendees.Where(a => a.Role == AttendeeRole.OptionalParticipant && a.UserType is not (CalendarUserType.Resource or CalendarUserType.Room))];
        Resources = Component.Resources;
        Summary = Component.Summary;
        Description = Component.Description;
        HtmlDescription = Component.HtmlDescription;
        Attachments = Component.Attachments;
        var created = Component.Created ?? Component.Stamp;
        CreatedDate = created?.ToDateTimeOffset();
        Start = Component.Start;
        if (Component is CalendarEvent anEvent)
        {
            Location = anEvent.Location;
            End = anEvent.End;
        }
        else if (Component is CalendarTodo todo)
        {
            Location = todo.Location;
            End = todo.Due;
        }
    }

    /// <summary>The whole calendar, for anything the primary component does not cover (more events, to-dos, time zones).</summary>
    public Calendar Calendar { get; }

    /// <summary>The primary component, or null when the calendar holds no event, to-do or journal entry.</summary>
    public CalendarComponent? Component { get; }

    /// <summary>CREATED, else DTSTAMP (floating times read as UTC); null when neither exists.</summary>
    public DateTimeOffset? CreatedDate { get; }

    public CalendarAddress? Organizer { get; }

    /// <summary>Required attendees, chairs and non-participants in file order (everyone who is neither optional nor a resource).</summary>
    public IReadOnlyList<CalendarAddress> Attendees { get; } = [];

    public IReadOnlyList<CalendarAddress> OptionalAttendees { get; } = [];

    /// <summary>Attendees of type RESOURCE or ROOM.</summary>
    public IReadOnlyList<CalendarAddress> Resources { get; } = [];

    public string? Summary { get; }

    public string? Description { get; }

    public string? HtmlDescription { get; }

    public string? Location { get; }

    public IcsDateTime? Start { get; }

    public IcsDateTime? End { get; }

    public CalendarMethod Method => Calendar.Method;

    /// <summary>Every ATTACH of the primary component. Inline ones have <see cref="CalendarAttachment.HasData"/>; filter on <see cref="CalendarAttachment.Name"/> as the consumer does.</summary>
    public IReadOnlyList<CalendarAttachment> Attachments { get; } = [];

    /// <summary>An Outlook-style appointment page: self-contained HTML, UTF-8, no script. Only the primary component unless <see cref="IcsRenderOptions.AllComponents"/> says otherwise.</summary>
    public string ToHtml(IcsRenderOptions? options = null, CancellationToken cancellationToken = default) => IcsRenderer.ToHtml(Calendar, options ?? renderDefaults, cancellationToken);

    public string ToPlainText(IcsRenderOptions? options = null, CancellationToken cancellationToken = default) => IcsRenderer.ToPlainText(Calendar, options ?? renderDefaults, cancellationToken);

    public string ToMarkdown(IcsRenderOptions? options = null, CancellationToken cancellationToken = default) => IcsRenderer.ToMarkdown(Calendar, options ?? renderDefaults, cancellationToken);

    /// <summary>The HTML page as UTF-8 bytes without a BOM.</summary>
    public byte[] ToHtmlBytes(IcsRenderOptions? options = null, CancellationToken cancellationToken = default) => new System.Text.UTF8Encoding(false).GetBytes(ToHtml(options, cancellationToken));

    /// <summary>Writes the HTML page to a stream as UTF-8 without a BOM.</summary>
    public void ToHtml(Stream destination, IcsRenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new StreamWriter(destination, new System.Text.UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        writer.Write(ToHtml(options, cancellationToken));
    }
}
