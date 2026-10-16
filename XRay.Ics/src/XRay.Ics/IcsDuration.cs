using System.Globalization;
using System.Text;

namespace XRay.Ics;

/// <summary>An RFC 5545 DURATION (<c>P1DT2H30M</c>, <c>-PT15M</c>, <c>P2W</c>).</summary>
public readonly record struct IcsDuration(bool Negative, int Weeks, int Days, int Hours, int Minutes, int Seconds)
{
    /// <summary>The length as a <see cref="TimeSpan"/> (a day is 24 hours).</summary>
    public TimeSpan ToTimeSpan()
    {
        var span = new TimeSpan(Weeks * 7 + Days, Hours, Minutes, Seconds);
        return Negative ? span.Negate() : span;
    }

    public static IcsDuration FromTimeSpan(TimeSpan span)
    {
        var negative = span < TimeSpan.Zero;
        if (negative)
        {
            span = span.Negate();
        }

        return new IcsDuration(negative, 0, span.Days, span.Hours, span.Minutes, span.Seconds);
    }

    public static bool TryParse(string? value, out IcsDuration duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var index = 0;
        var negative = false;
        if (text[index] == '-' || text[index] == '+')
        {
            negative = text[index] == '-';
            index++;
        }

        if (index >= text.Length || char.ToUpperInvariant(text[index]) != 'P')
        {
            return false;
        }

        index++;
        int weeks = 0, days = 0, hours = 0, minutes = 0, seconds = 0;
        var inTime = false;
        var any = false;
        while (index < text.Length)
        {
            var c = char.ToUpperInvariant(text[index]);
            if (c == 'T')
            {
                inTime = true;
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            if (start == index || index >= text.Length || !int.TryParse(text.AsSpan(start, index - start), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > 100_000_000)
            {
                return false;
            }

            var unit = char.ToUpperInvariant(text[index++]);
            switch (unit)
            {
                case 'W':
                    weeks = number;
                    break;
                case 'D':
                    days = number;
                    break;
                case 'H' when inTime:
                    hours = number;
                    break;
                case 'M' when inTime:
                    minutes = number;
                    break;
                case 'S' when inTime:
                    seconds = number;
                    break;
                default:
                    return false;
            }

            any = true;
        }

        if (!any)
        {
            return false;
        }

        try
        {
            _ = new TimeSpan(weeks * 7 + days, hours, minutes, seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        duration = new IcsDuration(negative, weeks, days, hours, minutes, seconds);
        return true;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        if (Negative)
        {
            builder.Append('-');
        }

        builder.Append('P');
        if (Weeks > 0 && Days == 0 && Hours == 0 && Minutes == 0 && Seconds == 0)
        {
            builder.Append(Weeks.ToString(CultureInfo.InvariantCulture)).Append('W');
            return builder.ToString();
        }

        var days = Weeks * 7 + Days;
        if (days > 0)
        {
            builder.Append(days.ToString(CultureInfo.InvariantCulture)).Append('D');
        }

        if (Hours > 0 || Minutes > 0 || Seconds > 0)
        {
            builder.Append('T');
            if (Hours > 0)
            {
                builder.Append(Hours.ToString(CultureInfo.InvariantCulture)).Append('H');
            }

            if (Minutes > 0)
            {
                builder.Append(Minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
            }

            if (Seconds > 0)
            {
                builder.Append(Seconds.ToString(CultureInfo.InvariantCulture)).Append('S');
            }
        }

        if (days == 0 && Hours == 0 && Minutes == 0 && Seconds == 0)
        {
            builder.Append("T0S");
        }

        return builder.ToString();
    }
}
