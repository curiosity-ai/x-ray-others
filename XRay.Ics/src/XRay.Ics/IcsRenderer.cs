using System.Globalization;
using System.Text;

namespace XRay.Ics;

/// <summary>
/// Renders calendars for people and for search indexes: an Outlook-style appointment page (HTML),
/// plain text and Markdown. Output is deterministic for a given input and options.
/// </summary>
public static class IcsRenderer
{
    private const string Css = "body{font-family:Segoe UI,Calibri,Arial,sans-serif;font-size:14px;color:#222;margin:16px}"
        + ".appointment{max-width:760px;margin:0 0 28px 0}"
        + "h1.subject{font-size:20px;font-weight:600;margin:0 0 12px 0;padding-bottom:6px;border-bottom:1px solid #ccc}"
        + "table.details{border-collapse:collapse}"
        + "table.details th{text-align:left;vertical-align:top;font-weight:600;padding:3px 16px 3px 0;white-space:nowrap}"
        + "table.details td{vertical-align:top;padding:3px 0}"
        + "div.description{margin-top:14px;padding-top:10px;border-top:1px solid #ddd;white-space:pre-wrap}"
        + "h2.attachments{font-size:14px;margin:14px 0 4px 0}"
        + "ul.attachments{margin:0;padding-left:20px}"
        + ".note{color:#a00;font-weight:600}";

    public static string ToHtml(Calendar calendar, IcsRenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        options ??= new IcsRenderOptions();
        var views = Views(calendar, options, defaultAll: false, cancellationToken);
        var title = views.Count > 0 ? views[0].Title : calendar.Name ?? "Calendar";
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(Encode(title)).Append("</title>\n<style>").Append(Css).Append("</style>\n</head>\n<body>\n");
        foreach (var view in views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            html.Append("<div class=\"appointment\">\n<h1 class=\"subject\">").Append(Encode(view.Title)).Append("</h1>\n");
            if (view.Note is not null)
            {
                html.Append("<div class=\"note\">").Append(Encode(view.Note)).Append("</div>\n");
            }

            html.Append("<table class=\"details\">\n");
            foreach (var (label, lines) in view.Rows)
            {
                html.Append("<tr><th>").Append(Encode(label)).Append("</th><td>");
                for (var i = 0; i < lines.Count; i++)
                {
                    if (i > 0)
                    {
                        html.Append("<br>");
                    }

                    html.Append(Encode(lines[i]));
                }

                html.Append("</td></tr>\n");
            }

            html.Append("</table>\n");
            if (!string.IsNullOrEmpty(view.Description))
            {
                html.Append("<div class=\"description\">").Append(EncodeMultiline(view.Description)).Append("</div>\n");
            }

            if (view.Attachments.Count > 0)
            {
                html.Append("<h2 class=\"attachments\">Attachments</h2>\n<ul class=\"attachments\">\n");
                foreach (var attachment in view.Attachments)
                {
                    html.Append("<li>").Append(Encode(attachment)).Append("</li>\n");
                }

                html.Append("</ul>\n");
            }

            html.Append("</div>\n");
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    public static string ToPlainText(Calendar calendar, IcsRenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        options ??= new IcsRenderOptions();
        var text = new StringBuilder();
        var first = true;
        foreach (var view in Views(calendar, options, defaultAll: true, cancellationToken))
        {
            if (!first)
            {
                text.Append('\n');
            }

            first = false;
            text.Append(view.Title).Append('\n');
            if (view.Note is not null)
            {
                text.Append(view.Note).Append('\n');
            }

            foreach (var (label, lines) in view.Rows)
            {
                text.Append(label).Append(": ").Append(string.Join("; ", lines)).Append('\n');
            }

            if (!string.IsNullOrEmpty(view.Description))
            {
                text.Append('\n').Append(view.Description.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
            }

            if (view.Attachments.Count > 0)
            {
                text.Append("\nAttachments: ").Append(string.Join(", ", view.Attachments)).Append('\n');
            }
        }

        return text.ToString();
    }

    public static string ToMarkdown(Calendar calendar, IcsRenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        options ??= new IcsRenderOptions();
        var markdown = new StringBuilder();
        var first = true;
        foreach (var view in Views(calendar, options, defaultAll: true, cancellationToken))
        {
            if (!first)
            {
                markdown.Append('\n');
            }

            first = false;
            markdown.Append("# ").Append(EscapeMarkdown(view.Title)).Append("\n\n");
            if (view.Note is not null)
            {
                markdown.Append("**").Append(EscapeMarkdown(view.Note)).Append("**\n\n");
            }

            foreach (var (label, lines) in view.Rows)
            {
                markdown.Append("- **").Append(label).Append(":** ").Append(string.Join("; ", lines.Select(EscapeMarkdown))).Append('\n');
            }

            if (!string.IsNullOrEmpty(view.Description))
            {
                markdown.Append('\n');
                foreach (var line in view.Description.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                {
                    markdown.Append(EscapeMarkdown(line)).Append("  \n");
                }
            }

            if (view.Attachments.Count > 0)
            {
                markdown.Append("\n## Attachments\n\n");
                foreach (var attachment in view.Attachments)
                {
                    markdown.Append("- ").Append(EscapeMarkdown(attachment)).Append('\n');
                }
            }
        }

        return markdown.ToString();
    }

    /// <summary>Escapes the markup characters only: the page is UTF-8, so other characters stay as they are.</summary>
    private static string Encode(string value)
    {
        if (value.AsSpan().IndexOfAny("&<>\"'") < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&#39;");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string EncodeMultiline(string value)
    {
        var encoded = Encode(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        return encoded.Replace("\n", "<br>\n", StringComparison.Ordinal);
    }

    private static string EscapeMarkdown(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or '*' or '_' or '[' or ']' or '<' or '>' or '`' or '|')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static List<View> Views(Calendar calendar, IcsRenderOptions options, bool defaultAll, CancellationToken cancellationToken)
    {
        var components = new List<CalendarComponent>();
        components.AddRange(calendar.Events);
        components.AddRange(calendar.Todos);
        components.AddRange(calendar.Journals);
        var all = options.AllComponents ?? defaultAll;
        if (!all && components.Count > 1)
        {
            components = [calendar.Primary!];
        }

        var views = new List<View>(components.Count);
        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            views.Add(Build(component, options));
        }

        return views;
    }

    private static View Build(CalendarComponent component, IcsRenderOptions options)
    {
        var culture = options.Culture;
        var view = new View { Title = string.IsNullOrWhiteSpace(component.Summary) ? "(no subject)" : component.Summary! };
        if (component.Status == CalendarStatus.Cancelled || component.Method == CalendarMethod.Cancel)
        {
            view.Note = "This item has been cancelled.";
        }
        else if (component.Status == CalendarStatus.Tentative)
        {
            view.Note = "Tentative.";
        }

        var start = component.Start;
        switch (component)
        {
            case CalendarEvent anEvent:
            {
                if (start is { } begin)
                {
                    var lines = new List<string> { FormatWhen(begin, anEvent.End, options) };
                    lines.AddRange(RecurrenceLines(component, begin));
                    view.Rows.Add(("When", lines));
                }

                AddIfAny(view, "Where", anEvent.Location);
                break;
            }

            case CalendarTodo todo:
            {
                if (start is { } begin)
                {
                    view.Rows.Add(("Start", [FormatWhen(begin, null, options)]));
                }

                if (todo.Due is { } due)
                {
                    view.Rows.Add(("Due", [FormatWhen(due, null, options)]));
                }

                if (todo.Completed is { } completed)
                {
                    view.Rows.Add(("Completed", [FormatWhen(completed, null, options)]));
                }
                else if (todo.PercentComplete is { } percent)
                {
                    view.Rows.Add(("Progress", [percent.ToString(culture) + "%"]));
                }

                AddIfAny(view, "Where", todo.Location);
                break;
            }

            default:
                if (start is { } date)
                {
                    view.Rows.Add(("Date", [FormatWhen(date, null, options)]));
                }

                break;
        }

        if (component.Organizer is { } organizer)
        {
            view.Rows.Add(("Organizer", [Person(organizer)]));
        }

        var required = component.RequiredAttendees.Concat(component.Chairs).Concat(component.NonParticipants).ToList();
        if (required.Count > 0)
        {
            view.Rows.Add(("Required", [.. required.Select(Person)]));
        }

        var optional = component.OptionalAttendees;
        if (optional.Count > 0)
        {
            view.Rows.Add(("Optional", [.. optional.Select(Person)]));
        }

        var resources = component.Resources;
        if (resources.Count > 0)
        {
            view.Rows.Add(("Resources", [.. resources.Select(Person)]));
        }

        var categories = component.Categories;
        if (categories.Count > 0)
        {
            view.Rows.Add(("Categories", [string.Join(", ", categories)]));
        }

        view.Description = component.Description?.TrimEnd('\r', '\n', ' ', '\t');
        if (options.IncludeAttachments)
        {
            foreach (var attachment in component.Attachments)
            {
                var name = attachment.Name ?? attachment.Uri ?? "(unnamed)";
                if (attachment.HasData)
                {
                    name += " (" + FormatSize(attachment.Size ?? 0, culture) + ")";
                }

                view.Attachments.Add(name);
            }
        }

        return view;
    }

    private static void AddIfAny(View view, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            view.Rows.Add((label, [value!.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ')]));
        }
    }

    private static string Person(CalendarAddress address)
    {
        var name = address.CommonName;
        var mail = address.Address;
        if (string.IsNullOrWhiteSpace(name))
        {
            return mail;
        }

        return string.IsNullOrWhiteSpace(mail) || string.Equals(name, mail, StringComparison.OrdinalIgnoreCase) ? name! : $"{name} <{mail}>";
    }

    private static string FormatSize(long bytes, CultureInfo culture)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(culture) + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024d).ToString("0.#", culture) + " KB";
        }

        return (bytes / 1024d / 1024d).ToString("0.#", culture) + " MB";
    }

    private static IEnumerable<string> RecurrenceLines(CalendarComponent component, IcsDateTime start)
    {
        if (!component.IsRecurring)
        {
            yield break;
        }

        foreach (var rule in component.RecurrenceRules)
        {
            yield return "Recurrence: " + rule.Describe(start);
        }

        var extra = component.RecurrenceDates.Count;
        if (extra > 0)
        {
            yield return "Additional dates: " + extra.ToString(CultureInfo.InvariantCulture) + ".";
        }

        var excluded = component.ExceptionDates.Count;
        if (excluded > 0)
        {
            yield return "Excluded dates: " + excluded.ToString(CultureInfo.InvariantCulture) + ".";
        }
    }

    private static string FormatWhen(IcsDateTime start, IcsDateTime? end, IcsRenderOptions options)
    {
        var culture = options.Culture;
        const string DateFormat = "dddd, d MMMM yyyy";
        if (start.IsDate)
        {
            var last = start.Value.Date;
            if (end is { } stop && stop.IsDate && stop.Value.Date > Internal.DateTimeMath.AddDays(start.Value.Date, 1))
            {
                last = Internal.DateTimeMath.AddDays(stop.Value.Date, -1);
            }

            return last == start.Value.Date
                ? start.Value.ToString(DateFormat, culture) + " (all day)"
                : start.Value.ToString(DateFormat, culture) + " - " + last.ToString(DateFormat, culture) + " (all day)";
        }

        var (startWall, startLabel) = Resolve(start, options);
        var builder = new StringBuilder();
        builder.Append(startWall.ToString(DateFormat, culture)).Append(' ').Append(startWall.ToString("HH:mm", culture));
        if (end is { } finish && finish != start)
        {
            if (finish.IsDate)
            {
                builder.Append(" - ").Append(finish.Value.ToString(DateFormat, culture));
            }
            else
            {
                var (endWall, endLabel) = Resolve(finish, options);
                if (endWall.Date == startWall.Date)
                {
                    builder.Append(" - ").Append(endWall.ToString("HH:mm", culture));
                }
                else
                {
                    builder.Append(" - ").Append(endWall.ToString(DateFormat, culture)).Append(' ').Append(endWall.ToString("HH:mm", culture));
                }

                startLabel = endLabel.Length > 0 && endLabel != startLabel && startLabel.Length > 0 ? startLabel + " / " + endLabel : startLabel.Length > 0 ? startLabel : endLabel;
            }
        }

        if (startLabel.Length > 0)
        {
            builder.Append(' ').Append(startLabel);
        }

        return builder.ToString();
    }

    private static (DateTime Wall, string Label) Resolve(IcsDateTime value, IcsRenderOptions options)
    {
        if (options.DisplayTimeZone is { } zone)
        {
            var local = value.ToLocalTime(zone, options.FloatingTimeZone);
            return (local.DateTime, "(" + OffsetLabel(local.Offset) + ", " + zone.Id + ")");
        }

        switch (value.Kind)
        {
            case IcsDateTimeKind.Utc:
                return (value.Value, "(UTC)");
            case IcsDateTimeKind.Zoned when value.Offset is { } offset:
                var isFixed = value.TzId is not null && value.TzId.StartsWith("UTC", StringComparison.Ordinal);
                return (value.Value, isFixed || value.TzId is null ? "(" + OffsetLabel(offset) + ")" : "(" + OffsetLabel(offset) + ", " + value.TzId + ")");
            case IcsDateTimeKind.Zoned:
                return (value.Value, "(" + value.TzId + ")");
            default:
                return (value.Value, string.Empty);
        }
    }

    private static string OffsetLabel(TimeSpan offset)
    {
        return offset == TimeSpan.Zero ? "UTC" : "UTC" + IcsDateTime.FormatOffset(offset);
    }

    private sealed class View
    {
        public string Title { get; init; } = string.Empty;

        public string? Note { get; set; }

        public List<(string Label, List<string> Lines)> Rows { get; } = [];

        public string? Description { get; set; }

        public List<string> Attachments { get; } = [];
    }
}
