using System.Text;

namespace XRay.Ics.Internal;

/// <summary>Builds the raw component tree from bytes or text. Tolerant: it never throws on damage.</summary>
internal static class IcsReader
{
    internal static readonly string[] CalendarChildren = ["VEVENT", "VTODO", "VJOURNAL", "VFREEBUSY", "VTIMEZONE", "VALARM", "VAVAILABILITY", "AVAILABLE", "VPOLL", "VLOCATION", "VRESOURCE", "PARTICIPANT", "STANDARD", "DAYLIGHT"];

    public static ParseResult Read(byte[] data, IcsParseOptions options, CancellationToken cancellationToken)
    {
        if (data.LongLength > options.MaxInputBytes)
        {
            throw new IcsFormatException($"The input is {data.LongLength} bytes, over the limit of {options.MaxInputBytes}.");
        }

        var text = TextDecoder.Decode(data, out var legacy);
        return ReadText(text, legacy, options, cancellationToken);
    }

    public static ParseResult ReadText(string text, bool legacy, IcsParseOptions options, CancellationToken cancellationToken)
    {
        var fallback = options.FallbackEncoding ?? (legacy ? TextDecoder.Windows1252() : Encoding.UTF8);
        var warnings = new List<string>();
        var root = new IcsComponent("");
        var stack = new List<IcsComponent> { root };
        var reader = new ContentLineReader(text, options.MaxLineLength);
        var skipDepth = 0;
        var componentCount = 0;
        var begins = 0;
        var lines = 0;
        while (reader.TryRead(out var line))
        {
            if ((++lines & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (reader.LastLineOverflowed)
            {
                warnings.Add($"Line {reader.LastLineNumber} exceeds the maximum line length and was skipped.");
                continue;
            }

            var property = ContentLineParser.Parse(line, reader.LastLineNumber, legacy, fallback);
            if (property is null)
            {
                if (warnings.Count < 100)
                {
                    warnings.Add($"Line {reader.LastLineNumber} is not a content line and was skipped.");
                }

                continue;
            }

            if (property.Group is null && property.Name == "BEGIN")
            {
                begins++;
                if (skipDepth > 0 || stack.Count > options.MaxDepth || ++componentCount > options.MaxComponents)
                {
                    skipDepth++;
                    continue;
                }

                var child = new IcsComponent(property.Value.Trim());
                stack[^1].Children.Add(child);
                stack.Add(child);
                continue;
            }

            if (property.Group is null && property.Name == "END")
            {
                if (skipDepth > 0)
                {
                    skipDepth--;
                    continue;
                }

                var name = property.Value.Trim();
                var index = -1;
                for (var i = stack.Count - 1; i > 0; i--)
                {
                    if (string.Equals(stack[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                if (index > 0)
                {
                    stack.RemoveRange(index, stack.Count - index);
                }
                else if (warnings.Count < 100)
                {
                    warnings.Add($"Line {reader.LastLineNumber}: END:{name} has no matching BEGIN.");
                }

                continue;
            }

            if (skipDepth > 0)
            {
                continue;
            }

            var target = stack[^1];
            if (target.Properties.Count >= options.MaxPropertiesPerComponent)
            {
                continue;
            }

            target.Properties.Add(property);
        }

        if (stack.Count > 1)
        {
            warnings.Add($"The input ends inside {stack[^1].Name}; unclosed components were closed.");
        }

        var calendars = new List<IcsComponent>();
        var strays = new List<IcsComponent>();
        foreach (var child in root.Children)
        {
            if (string.Equals(child.Name, "VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                calendars.Add(child);
            }
            else if (child.Name.StartsWith("X-", StringComparison.OrdinalIgnoreCase) || ContainsCalendarComponent(child) || Array.Exists(CalendarChildren, name => string.Equals(name, child.Name, StringComparison.OrdinalIgnoreCase)))
            {
                strays.Add(child);
            }
        }

        if (strays.Count > 0)
        {
            var wrapper = new IcsComponent("VCALENDAR");
            wrapper.Children.AddRange(strays);
            calendars.Add(wrapper);
            warnings.Add("Components found outside a VCALENDAR were wrapped in one.");
        }

        if (calendars.Count == 0)
        {
            throw new IcsFormatException(begins == 0 ? "The input is not an iCalendar or vCalendar stream." : "The input holds no VCALENDAR.");
        }

        return new ParseResult(calendars, warnings);
    }

    private static bool ContainsCalendarComponent(IcsComponent component)
    {
        foreach (var child in component.Children)
        {
            if (Array.Exists(CalendarChildren, name => string.Equals(name, child.Name, StringComparison.OrdinalIgnoreCase)) || ContainsCalendarComponent(child))
            {
                return true;
            }
        }

        return false;
    }

    internal sealed record ParseResult(List<IcsComponent> Calendars, List<string> Warnings);
}
