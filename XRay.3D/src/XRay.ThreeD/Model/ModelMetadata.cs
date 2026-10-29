namespace XRay.ThreeD;

/// <summary>What can be learned about a file without (or alongside) decoding its geometry.</summary>
public sealed class ModelMetadata
{
    public FormatInfo Format { get; set; } = FormatInfo.Unknown;

    /// <summary>The kind of document inside the format: "part", "assembly", "drawing", "mesh", "scene"...</summary>
    public string? DocumentKind { get; set; }

    /// <summary>Format revision as the file states it (glTF "2.0", PLY "ascii 1.0", ...).</summary>
    public string? FormatVersion { get; set; }

    /// <summary>The application that wrote the file, with its version when stated.</summary>
    public string? Application { get; set; }

    public string? Title { get; set; }

    public string? Author { get; set; }

    public string? Company { get; set; }

    public DateTimeOffset? Created { get; set; }

    public DateTimeOffset? Modified { get; set; }

    public string? LastModifiedBy { get; set; }

    /// <summary>Unit of the stored coordinates, when the file says. Null means unknown.</summary>
    public LengthUnit? Units { get; set; }

    public UpAxis? UpAxis { get; set; }

    /// <summary>Named parts (leaf components), in document order.</summary>
    public List<string> PartNames { get; } = new();

    /// <summary>Layer names, for formats that have layers.</summary>
    public List<string> LayerNames { get; } = new();

    /// <summary>Drawing sheet names, for formats that have sheets.</summary>
    public List<string> Sheets { get; } = new();

    /// <summary>Configuration / variant names.</summary>
    public List<string> Configurations { get; } = new();

    /// <summary>Feature tree entries (name, type), for parametric CAD formats.</summary>
    public List<ModelFeature> Features { get; } = new();

    /// <summary>Material names used.</summary>
    public List<string> MaterialNames { get; } = new();

    /// <summary>
    /// Files an assembly refers to, reduced to file names. Empty for a part, and for an assembly whose
    /// components are stored inside it. Answers "which files is it missing?".
    /// </summary>
    public List<string> ComponentFiles { get; } = new();

    /// <summary>Document and custom properties (part number, description, revision...).</summary>
    public List<ModelProperty> Properties { get; } = new();

    /// <summary>Geometry counts; null from a metadata-only read of a format whose counts need decoding.</summary>
    public ModelStatistics? Statistics { get; set; }

    public BoundingBox? Bounds { get; set; }

    /// <summary>
    /// When the reader moved the geometry so it sits near zero (float32 vertices cannot hold survey coordinates), the offset that was
    /// subtracted, in the file's own units: add it back to get the stored coordinates. Null when nothing was moved.
    /// </summary>
    public double[]? Origin { get; set; }

    public bool HasPreview { get; set; }

    /// <summary>True when the file holds geometry this library can decode.</summary>
    public bool HasGeometry { get; set; }

    /// <summary>Why there is no geometry, or what was skipped.</summary>
    public List<string> Notes { get; } = new();

    public string? GetProperty(string name) =>
        Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
}

public sealed record ModelProperty(string Name, string Value, string? Group = null);

public sealed record ModelFeature(string Name, string Type);

/// <summary>
/// Geometry counts. <see cref="Vertices"/> and <see cref="Triangles"/> count what is drawn (an instanced part counts
/// once per instance); the Unique counts are what is stored.
/// </summary>
public sealed record ModelStatistics(int Nodes, int Meshes, long Vertices, long Triangles, int Materials, long UniqueVertices = 0, long UniqueTriangles = 0);

public enum LengthUnit
{
    Meter,
    Millimeter,
    Centimeter,
    Kilometer,
    Inch,
    Foot,
    Yard,
    Mile,
    Micrometer,
    Nanometer,
    Decimeter,
    Mil,
    Microinch,
}

public enum UpAxis
{
    Y,
    Z,
}

public static class LengthUnitExtensions
{
    public static double ToMeters(this LengthUnit unit) => unit switch
    {
        LengthUnit.Meter => 1,
        LengthUnit.Millimeter => 0.001,
        LengthUnit.Centimeter => 0.01,
        LengthUnit.Kilometer => 1000,
        LengthUnit.Inch => 0.0254,
        LengthUnit.Foot => 0.3048,
        LengthUnit.Yard => 0.9144,
        LengthUnit.Mile => 1609.344,
        LengthUnit.Micrometer => 1e-6,
        LengthUnit.Nanometer => 1e-9,
        LengthUnit.Decimeter => 0.1,
        LengthUnit.Mil => 2.54e-5,
        LengthUnit.Microinch => 2.54e-8,
        _ => 1,
    };
}
