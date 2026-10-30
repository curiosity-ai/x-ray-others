using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.PointCloud;

using XRay.ThreeD.Internal;

/// <summary>Text point clouds: .xyz (x y z [r g b | nx ny nz]) and .pts (a count line, then x y z intensity r g b). Delimiters are space, tab, comma or semicolon.</summary>
internal static class XyzReader
{
    public static Model3D Read(ReadOnlyMemory<byte> data, FormatInfo format, LoadOptions options)
    {
        var text = Encoding.UTF8.GetString(data.Span);
        var xyz = new List<double>();
        var colors = new List<float>();
        var colorCount = 0;
        var first = true;
        var tick16 = 0L;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            Cancel.Every(options.Cancellation, tick16++);
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '/')
            {
                continue;
            }

            var tokens = line.ToString().Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (first)
            {
                first = false;
                if (tokens.Length == 1 && long.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    continue; // PTS point count
                }
            }

            if (tokens.Length < 3 || !TryParse(tokens[0], out var x) || !TryParse(tokens[1], out var y) || !TryParse(tokens[2], out var z))
            {
                if (xyz.Count == 0)
                {
                    continue; // a header or label line
                }

                throw new ModelFormatException("A point line does not start with three numbers.");
            }

            xyz.Add(x);
            xyz.Add(y);
            xyz.Add(z);
            // Colours: 6 columns = r g b; PTS 7 columns = intensity r g b; 4 columns = intensity (greyscale).
            float r = -1, g = 0, b = 0;
            if (tokens.Length >= 7 && format.Id == "pts")
            {
                r = Channel(tokens[4]);
                g = Channel(tokens[5]);
                b = Channel(tokens[6]);
            }
            else if (tokens.Length >= 6 && format.Id == "xyz" && tokens.Skip(3).Take(3).All(t => TryParse(t, out var c) && c >= 0 && c <= 255 && c == Math.Floor(c)))
            {
                r = Channel(tokens[3]);
                g = Channel(tokens[4]);
                b = Channel(tokens[5]);
            }
            else if (tokens.Length == 4 && TryParse(tokens[3], out var intensity))
            {
                r = g = b = (float)Math.Clamp(intensity > 1 ? intensity / 255.0 : intensity, 0, 1);
            }

            if (r >= 0)
            {
                colorCount++;
            }

            colors.AddRange([Math.Max(r, 0), g, b, 1f]);
            if (xyz.Count / 3 > options.MaxPoints * 2 + 100_000_000)
            {
                throw new ModelFormatException("The point file is larger than the configured limit.");
            }
        }

        if (xyz.Count == 0)
        {
            throw new ModelFormatException("The file holds no points.");
        }

        var positions = xyz.ToArray();
        var model = PointCloudCommon.Build(format, positions, colorCount > 0 ? colors.ToArray() : PointCloudCommon.HeightRamp(positions), options);
        return model;
    }

    private static float Channel(string token) => TryParse(token, out var v) ? (float)Math.Clamp(v > 1 ? v / 255.0 : v, 0, 1) : 0;

    private static bool TryParse(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
