using System.Numerics;

namespace XRay.ThreeD.Internal;

/// <summary>
/// Reports a file's length scale as <see cref="ModelMetadata.Units"/> when it is one of the named units, and otherwise scales the scene to metres
/// with a wrapper node, so that a unit the enum has no name for (a Blender scene at 0.0125, an FBX at UnitScaleFactor 0.5) still lands at the right size.
/// </summary>
internal static class UnitScaling
{
    /// <summary>The named unit whose size in metres equals <paramref name="metersPerUnit"/> (to within a part in a million), or null.</summary>
    public static LengthUnit? Named(double metersPerUnit)
    {
        foreach (var unit in Enum.GetValues<LengthUnit>())
        {
            if (Math.Abs(unit.ToMeters() - metersPerUnit) <= 1e-6 * metersPerUnit)
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>
    /// Records that one coordinate unit is <paramref name="metersPerUnit"/> metres. A named unit goes into the metadata; any other positive finite size
    /// becomes meters plus a scaling root node (call this after the roots are built, before statistics). A non-positive or non-finite size is ignored.
    /// </summary>
    public static void Apply(Model3D model, double metersPerUnit)
    {
        if (!(metersPerUnit > 0) || !double.IsFinite(metersPerUnit))
        {
            return;
        }

        if (Named(metersPerUnit) is { } named)
        {
            model.Metadata.Units = named;
            return;
        }

        model.Metadata.Units = LengthUnit.Meter;
        if (model.Roots.Count == 0)
        {
            return;
        }

        var wrapper = new SceneNode(null) { Transform = Matrix4x4.CreateScale((float)metersPerUnit) };
        wrapper.Children.AddRange(model.Roots);
        model.Roots.Clear();
        model.Roots.Add(wrapper);
        model.Metadata.Notes.Add("The file's length unit is " + metersPerUnit.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " metres, which is not a named unit; the scene is scaled to metres by its root node.");
    }
}
