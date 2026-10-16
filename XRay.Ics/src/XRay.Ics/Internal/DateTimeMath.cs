namespace XRay.Ics.Internal;

/// <summary>Date arithmetic that clamps at the ends of the representable range instead of throwing.</summary>
internal static class DateTimeMath
{
    public static DateTime Add(DateTime value, TimeSpan span)
    {
        var ticks = value.Ticks + span.Ticks;
        if (span.Ticks > 0 && ticks < value.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return new DateTime(DateTime.MaxValue.Ticks, DateTimeKind.Unspecified);
        }

        if (span.Ticks < 0 && ticks > value.Ticks || ticks < 0)
        {
            return new DateTime(0, DateTimeKind.Unspecified);
        }

        return new DateTime(ticks, DateTimeKind.Unspecified);
    }

    public static DateTime AddDays(DateTime value, int days) => Add(value, TimeSpan.FromDays(days));

    /// <summary>The UTC ticks of a wall-clock reading at an offset; never throws.</summary>
    public static long UtcTicks(DateTime wall, TimeSpan offset) => wall.Ticks - offset.Ticks;

    public static DateTimeOffset ToDateTimeOffset(DateTime wall, TimeSpan offset)
    {
        var utc = wall.Ticks - offset.Ticks;
        if (utc < 0)
        {
            return DateTimeOffset.MinValue;
        }

        if (utc > DateTime.MaxValue.Ticks)
        {
            return DateTimeOffset.MaxValue;
        }

        try
        {
            return new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }
    }

    public static DateTimeOffset AddDays(DateTimeOffset value, int days)
    {
        var ticks = days * TimeSpan.TicksPerDay;
        if (days > 0 && value.UtcTicks + ticks > DateTime.MaxValue.Ticks)
        {
            return DateTimeOffset.MaxValue;
        }

        if (days < 0 && value.UtcTicks + ticks < 0)
        {
            return DateTimeOffset.MinValue;
        }

        return value.AddTicks(ticks);
    }
}
