namespace XRay.Ics.Internal;

internal static class DateParsing
{
    /// <summary>Parses the first value of a DATE / DATE-TIME property with its TZID and VALUE parameters.</summary>
    public static IcsDateTime? Parse(IcsProperty property, ZoneResolver resolver, TimeSpan? legacyOffset)
    {
        var text = property.Value;
        var comma = text.IndexOf(',');
        if (comma >= 0)
        {
            text = text[..comma];
        }

        var value = ParseText(text, property.GetParameter("TZID"), resolver, legacyOffset);
        // VALUE=DATE declares a date: a producer that still wrote a time after it gets the date.
        if (value is { IsDate: false } && string.Equals(LastValueParameter(property), "DATE", StringComparison.OrdinalIgnoreCase))
        {
            return new IcsDateTime(value.Value.Value, IcsDateTimeKind.Date);
        }

        return value;
    }

    /// <summary>The last VALUE parameter, which wins when a producer repeats it.</summary>
    internal static string? LastValueParameter(IcsProperty property)
    {
        var values = property.GetParameterValues("VALUE");
        return values.Count > 0 ? values[^1] : null;
    }

    public static IcsDateTime? ParseText(string text, string? tzId, ZoneResolver resolver, TimeSpan? legacyOffset)
    {
        if (!IcsDateTime.TryParseValue(text, out var value, out var kind))
        {
            return null;
        }

        switch (kind)
        {
            case IcsDateTimeKind.Date:
                return new IcsDateTime(value, IcsDateTimeKind.Date);
            case IcsDateTimeKind.Utc:
                return new IcsDateTime(value, IcsDateTimeKind.Utc);
        }

        if (!string.IsNullOrWhiteSpace(tzId))
        {
            var id = tzId.Trim();
            return new IcsDateTime(value, IcsDateTimeKind.Zoned, id, resolver.Resolve(id, value));
        }

        if (legacyOffset is { } offset)
        {
            return new IcsDateTime(value, IcsDateTimeKind.Zoned, "UTC" + IcsDateTime.FormatOffset(offset), offset);
        }

        return new IcsDateTime(value, IcsDateTimeKind.Floating);
    }

    /// <summary>Applies a DATE / DATE-TIME to a property (value and the VALUE / TZID parameters).</summary>
    public static IcsProperty? Write(IcsComponent component, string name, IcsDateTime? value)
    {
        component.Properties.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (value is not { } date)
        {
            return null;
        }

        var property = new IcsProperty(name, date.ToIcsString());
        Decorate(property, date);
        component.Properties.Add(property);
        return property;
    }

    public static void Decorate(IcsProperty property, IcsDateTime date)
    {
        if (date.IsDate)
        {
            property.Parameters.Add(new IcsParameter("VALUE", "DATE"));
        }
        else if (date.Kind == IcsDateTimeKind.Zoned && !string.IsNullOrEmpty(date.TzId))
        {
            property.Parameters.Add(new IcsParameter("TZID", date.TzId));
        }
    }
}
