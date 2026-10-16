using System.Globalization;

namespace XRay.Ics;

/// <summary>The GEO property: latitude and longitude in decimal degrees.</summary>
public readonly record struct GeoLocation(double Latitude, double Longitude)
{
    public static bool TryParse(string? value, out GeoLocation geo)
    {
        geo = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(';', ',');
        if (parts.Length != 2
            || !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
            || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return false;
        }

        geo = new GeoLocation(latitude, longitude);
        return true;
    }

    public string ToIcsString() => string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.######};{Longitude:0.######}");
}
