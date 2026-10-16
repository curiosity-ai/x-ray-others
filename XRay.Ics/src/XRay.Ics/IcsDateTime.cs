using System.Globalization;

namespace XRay.Ics;

/// <summary>How an <see cref="IcsDateTime"/> is anchored in time.</summary>
public enum IcsDateTimeKind
{
    /// <summary>A calendar date with no time (<c>VALUE=DATE</c>): an all-day value.</summary>
    Date,

    /// <summary>A local time with no zone: it means the same wall-clock time wherever the reader is.</summary>
    Floating,

    /// <summary>A UTC time (<c>...Z</c>).</summary>
    Utc,

    /// <summary>A time with a <c>TZID</c>. <see cref="IcsDateTime.Offset"/> is set when the zone could be resolved.</summary>
    Zoned,
}

/// <summary>
/// A DATE or DATE-TIME value. <see cref="Value"/> is the wall-clock reading as written (the UTC
/// reading for <see cref="IcsDateTimeKind.Utc"/>); <see cref="Offset"/> is the offset from UTC
/// when it is known.
/// </summary>
public readonly struct IcsDateTime : IEquatable<IcsDateTime>, IComparable<IcsDateTime>
{
    public IcsDateTime(DateTime value, IcsDateTimeKind kind, string? tzId = null, TimeSpan? offset = null)
    {
        Value = DateTime.SpecifyKind(kind == IcsDateTimeKind.Date ? value.Date : value, DateTimeKind.Unspecified);
        Kind = kind;
        TzId = tzId;
        Offset = kind == IcsDateTimeKind.Utc ? TimeSpan.Zero : kind is IcsDateTimeKind.Date or IcsDateTimeKind.Floating ? null : offset;
    }

    public DateTime Value { get; }

    public IcsDateTimeKind Kind { get; }

    /// <summary>The <c>TZID</c> as written, for <see cref="IcsDateTimeKind.Zoned"/> values.</summary>
    public string? TzId { get; }

    /// <summary>The UTC offset of <see cref="Value"/>, or null for dates, floating times and unresolvable zones.</summary>
    public TimeSpan? Offset { get; }

    public bool IsDate => Kind == IcsDateTimeKind.Date;

    public bool IsUtc => Kind == IcsDateTimeKind.Utc;

    public bool IsFloating => Kind == IcsDateTimeKind.Floating;

    /// <summary>The instant, when the offset is known (UTC and resolved zones); null otherwise.</summary>
    public DateTimeOffset? ToDateTimeOffsetOrNull()
    {
        return Offset is { } offset ? Internal.DateTimeMath.ToDateTimeOffset(Value, offset) : null;
    }

    /// <summary>The instant; dates, floating times and unresolved zones are read in <paramref name="floatingZone"/> (default UTC).</summary>
    public DateTimeOffset ToDateTimeOffset(TimeZoneInfo? floatingZone = null)
    {
        if (Offset is { } offset)
        {
            return Internal.DateTimeMath.ToDateTimeOffset(Value, offset);
        }

        var zone = floatingZone ?? TimeZoneInfo.Utc;
        return Internal.DateTimeMath.ToDateTimeOffset(Value, zone.GetUtcOffset(Value));
    }

    /// <summary>The instant expressed in <paramref name="target"/>. A date stays a date at midnight of its own day.</summary>
    public DateTimeOffset ToLocalTime(TimeZoneInfo target, TimeZoneInfo? floatingZone = null)
    {
        var instant = ToDateTimeOffset(floatingZone);
        try
        {
            return TimeZoneInfo.ConvertTime(instant, target);
        }
        catch (ArgumentOutOfRangeException)
        {
            // At the very ends of the range the converted value cannot be represented.
            return instant.ToUniversalTime();
        }
    }

    /// <summary>The same wall-clock moment shifted by a duration (a day is added to the wall clock).</summary>
    public IcsDateTime Add(TimeSpan span)
    {
        return new IcsDateTime(Internal.DateTimeMath.Add(Value, span), Kind, TzId, Offset);
    }

    public bool Equals(IcsDateTime other) => Value == other.Value && Kind == other.Kind && Offset == other.Offset && string.Equals(TzId, other.TzId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is IcsDateTime other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Value, Kind, Offset, TzId);

    public static bool operator ==(IcsDateTime left, IcsDateTime right) => left.Equals(right);

    public static bool operator !=(IcsDateTime left, IcsDateTime right) => !left.Equals(right);

    /// <summary>Orders by instant (floating and unresolved values as UTC).</summary>
    public int CompareTo(IcsDateTime other) => ToDateTimeOffset().UtcDateTime.CompareTo(other.ToDateTimeOffset().UtcDateTime);

    /// <summary>The RFC 5545 form of the value (without TZID).</summary>
    public string ToIcsString()
    {
        return Kind switch
        {
            IcsDateTimeKind.Date => Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            IcsDateTimeKind.Utc => Value.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
            _ => Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture),
        };
    }

    public override string ToString()
    {
        var text = Kind == IcsDateTimeKind.Date ? Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return Kind switch
        {
            IcsDateTimeKind.Utc => text + "Z",
            IcsDateTimeKind.Zoned when Offset is { } offset => text + FormatOffset(offset),
            IcsDateTimeKind.Zoned => text + "[" + TzId + "]",
            _ => text,
        };
    }

    internal static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{offset.Hours:00}:{offset.Minutes:00}");
    }

    /// <summary>Parses <c>YYYYMMDD</c>, <c>YYYYMMDDTHHMMSS[Z]</c> and tolerates ISO 8601 extended punctuation.</summary>
    public static bool TryParseValue(string? text, out DateTime value, out IcsDateTimeKind kind)
    {
        value = default;
        kind = IcsDateTimeKind.Floating;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        Span<char> digits = stackalloc char[16];
        var count = 0;
        var utc = false;
        var hasT = false;
        foreach (var c in span)
        {
            if (char.IsAsciiDigit(c))
            {
                if (count >= 15)
                {
                    return false;
                }

                digits[count++] = c;
            }
            else if (c is 'T' or 't')
            {
                if (hasT)
                {
                    return false;
                }

                hasT = true;
                if (count != 8)
                {
                    return false;
                }
            }
            else if (c is 'Z' or 'z')
            {
                utc = true;
            }
            else if (c is '-' or ':' or '.')
            {
                // ISO 8601 extended punctuation, tolerated.
                if (c == '.' && count >= 14)
                {
                    break;
                }
            }
            else if (c == '+')
            {
                break;
            }
            else
            {
                return false;
            }
        }

        if (count != 8 && count != 12 && count != 14 && count != 6)
        {
            return false;
        }

        if (count == 6 && !hasT)
        {
            return false;
        }

        try
        {
            if (count == 8)
            {
                value = new DateTime(Parse(digits, 0, 4), Parse(digits, 4, 2), Parse(digits, 6, 2), 0, 0, 0, DateTimeKind.Unspecified);
                kind = IcsDateTimeKind.Date;
                return true;
            }

            if (count == 6)
            {
                return false;
            }

            var hour = Parse(digits, 8, 2);
            var minute = Parse(digits, 10, 2);
            var second = count >= 14 ? Parse(digits, 12, 2) : 0;
            if (hour == 24 && minute == 0 && second == 0)
            {
                value = new DateTime(Parse(digits, 0, 4), Parse(digits, 4, 2), Parse(digits, 6, 2), 0, 0, 0, DateTimeKind.Unspecified).AddDays(1);
            }
            else
            {
                if (second == 60)
                {
                    second = 59;
                }

                value = new DateTime(Parse(digits, 0, 4), Parse(digits, 4, 2), Parse(digits, 6, 2), hour, minute, second, DateTimeKind.Unspecified);
            }

            kind = utc ? IcsDateTimeKind.Utc : IcsDateTimeKind.Floating;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static int Parse(ReadOnlySpan<char> digits, int start, int length) => int.Parse(digits.Slice(start, length), NumberStyles.None, CultureInfo.InvariantCulture);
}
