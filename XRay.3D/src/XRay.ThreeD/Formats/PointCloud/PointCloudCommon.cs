namespace XRay.ThreeD.Formats.PointCloud;

internal static class PointCloudCommon
{
    /// <summary>
    /// Builds the model of a point cloud: positions (as doubles, in the file's units) are moved so their bounds centre is at the origin
    /// (recorded in the metadata), thinned evenly to <see cref="LoadOptions.MaxPoints"/>, and stored as float32 with optional colours.
    /// </summary>
    public static Model3D Build(FormatInfo format, double[] positions, float[]? colors, LoadOptions options, string kind = "pointcloud")
    {
        var count = positions.Length / 3;
        if (count == 0)
        {
            throw new ModelFormatException("The file holds no points.");
        }

        var stride = options.MaxPoints > 0 && count > options.MaxPoints ? (int)Math.Ceiling(count / (double)options.MaxPoints) : 1;
        double[] min = [double.MaxValue, double.MaxValue, double.MaxValue];
        double[] max = [double.MinValue, double.MinValue, double.MinValue];
        for (var i = 0; i < count; i++)
        {
            for (var a = 0; a < 3; a++)
            {
                var v = positions[3 * i + a];
                if (!double.IsFinite(v))
                {
                    continue;
                }

                min[a] = Math.Min(min[a], v);
                max[a] = Math.Max(max[a], v);
            }
        }

        if (min[0] > max[0])
        {
            throw new ModelFormatException("The points have no finite coordinates.");
        }

        var origin = new[] { (min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2 };
        var kept = (count + stride - 1) / stride;
        var mesh = new MeshData { Positions = new float[kept * 3] };
        if (colors is not null)
        {
            mesh.Colors = new float[kept * 4];
        }

        var o = 0;
        for (var i = 0; i < count; i += stride)
        {
            for (var a = 0; a < 3; a++)
            {
                var v = positions[3 * i + a];
                mesh.Positions[3 * o + a] = double.IsFinite(v) ? (float)(v - origin[a]) : 0f;
            }

            if (colors is not null)
            {
                Array.Copy(colors, 4 * i, mesh.Colors, 4 * o, 4);
            }

            o++;
        }

        var model = new Model3D();
        model.Metadata.Format = format;
        model.Metadata.DocumentKind = kind;
        model.Meshes.Add(mesh);
        model.Roots.Add(new SceneNode(null) { Mesh = mesh });
        model.Metadata.Origin = origin;
        model.Metadata.HasGeometry = true;
        model.Metadata.Properties.Add(new ModelProperty("points", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "cloud"));
        if (stride > 1)
        {
            model.Metadata.Notes.Add($"The cloud was thinned to every {stride}. point ({kept:N0} of {count:N0}).");
            model.Warnings.Add(model.Metadata.Notes[^1]);
        }

        model.Metadata.Statistics = model.ComputeStatistics();
        model.Metadata.Bounds = model.ComputeBounds();
        return model;
    }

    /// <summary>A colour ramp for clouds without colours: height from blue through green to red.</summary>
    public static float[] HeightRamp(double[] positions, int axis = 2)
    {
        var count = positions.Length / 3;
        var min = double.MaxValue;
        var max = double.MinValue;
        for (var i = 0; i < count; i++)
        {
            var v = positions[3 * i + axis];
            if (double.IsFinite(v))
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        }

        var colors = new float[count * 4];
        var range = max > min ? max - min : 1;
        for (var i = 0; i < count; i++)
        {
            var t = (float)Math.Clamp((positions[3 * i + axis] - min) / range, 0, 1);
            colors[4 * i] = Math.Clamp(1.5f - MathF.Abs(4 * t - 3), 0, 1);
            colors[4 * i + 1] = Math.Clamp(1.5f - MathF.Abs(4 * t - 2), 0, 1);
            colors[4 * i + 2] = Math.Clamp(1.5f - MathF.Abs(4 * t - 1), 0, 1);
            colors[4 * i + 3] = 1;
        }

        return colors;
    }
}
