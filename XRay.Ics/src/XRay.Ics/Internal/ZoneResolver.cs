using System.Collections.Concurrent;
using System.Globalization;

namespace XRay.Ics.Internal;

/// <summary>Resolves a <c>TZID</c> to a UTC offset: the calendar's own VTIMEZONE first, then IANA/Windows names.</summary>
internal sealed class ZoneResolver
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> SystemZones = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, CalendarTimeZone?> lookup;

    public ZoneResolver(Func<string, CalendarTimeZone?> lookup)
    {
        this.lookup = lookup;
    }

    public static ZoneResolver SystemOnly { get; } = new(_ => null);

    public TimeSpan? Resolve(string tzId, DateTime local)
    {
        if (string.IsNullOrWhiteSpace(tzId))
        {
            return null;
        }

        var id = tzId.Trim().Trim('"');
        var custom = lookup(id);
        if (custom is not null && custom.GetUtcOffset(local) is { } fromRules)
        {
            return fromRules;
        }

        if (TryFixedOffset(id, out var fixedOffset))
        {
            return fixedOffset;
        }

        var zone = FindSystemZone(id);
        return zone?.GetUtcOffset(local);
    }

    public static TimeZoneInfo? FindSystemZone(string id)
    {
        return SystemZones.GetOrAdd(id, static key =>
        {
            foreach (var candidate in Candidates(key))
            {
                try
                {
                    if (TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var zone))
                    {
                        return zone;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidTimeZoneException or IOException or UnauthorizedAccessException)
                {
                }
            }

            return null;
        });
    }

    private static IEnumerable<string> Candidates(string id)
    {
        yield return id;
        var windows = WindowsZones.ToIana(id);
        if (windows is not null)
        {
            yield return windows;
        }

        var underscored = id.Replace(' ', '_');
        if (underscored != id)
        {
            yield return underscored;
        }

        // Globally unique ids such as /mozilla.org/20070129_1/Europe/Berlin or /softwarestudio.org/Tzfile/Europe/Berlin.
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            yield return parts[^2] + "/" + parts[^1];
            if (parts.Length >= 4)
            {
                yield return parts[^3] + "/" + parts[^2] + "/" + parts[^1];
            }
        }

        if (parts.Length == 2)
        {
            yield return parts[0] + "/" + parts[1];
        }

        if (parts.Length >= 2)
        {
            yield return parts[^1];
        }
    }

    private static bool TryFixedOffset(string id, out TimeSpan offset)
    {
        offset = default;
        if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase) || id.Equals("GMT", StringComparison.OrdinalIgnoreCase) || id.Equals("Z", StringComparison.OrdinalIgnoreCase) || id.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var span = id.AsSpan();
        if (span.StartsWith("GMT", StringComparison.OrdinalIgnoreCase) || span.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
        {
            var rest = id[3..];
            if (rest.Length > 0 && rest[0] is '+' or '-')
            {
                var sign = rest[0] == '-' ? -1 : 1;
                var digits = rest[1..].Replace(":", string.Empty, StringComparison.Ordinal);
                if (digits.Length is >= 1 and <= 4 && digits.All(char.IsAsciiDigit))
                {
                    var hours = digits.Length <= 2 ? int.Parse(digits, CultureInfo.InvariantCulture) : int.Parse(digits[..^2], CultureInfo.InvariantCulture);
                    var minutes = digits.Length <= 2 ? 0 : int.Parse(digits[^2..], CultureInfo.InvariantCulture);
                    if (hours <= 14 && minutes <= 59)
                    {
                        offset = new TimeSpan(hours, minutes, 0) * sign;
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
