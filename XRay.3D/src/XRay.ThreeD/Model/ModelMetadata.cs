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

    /// <summary>Drawing sheets with their paper size in meters, when the file states it (parallel to <see cref="Sheets"/> where both are known).</summary>
    public List<DrawingSheet> DrawingSheets { get; } = new();

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
public sealed record ModelStatistics(int Nodes, int Meshes, long Vertices, long Triangles, int Materials, long UniqueVertices = 0, long UniqueTriangles = 0)
{
    /// <summary>Surface area of everything drawn (instances counted per use, under their full transform: a non-uniform scale is exact), in the model's units squared. Zero when not computed.</summary>
    public double SurfaceArea { get; init; }

    /// <summary>
    /// Volume enclosed by the triangles of the closed, outward-facing meshes (instances counted per use), in the model's units cubed; open meshes
    /// and inside-out ones add nothing (see <see cref="VolumeIsComplete"/>); a mirrored instance (negative determinant) is drawn with
    /// its winding read the other way, as glTF specifies, and counts as positive too. Zero when not computed.
    /// </summary>
    public double Volume { get; init; }

    /// <summary>The signed sum over every mesh's triangles, open ones included (what <see cref="Volume"/> was before it counted closed meshes only). For an open surface it depends on where the origin is; use it to compare with another tool's triangle sums, not as a volume.</summary>
    public double SignedVolume { get; init; }

    /// <summary>Unique triangle meshes that are closed and outward-facing: the ones <see cref="Volume"/> counts.</summary>
    public int ClosedMeshes { get; init; }

    /// <summary>
    /// Unique triangle meshes that do not enclose space (a sheet, a surface with holes, adjacent triangles wound against each other, separate unwelded faces).
    /// Their area is counted, their volume is not: the volume of an open mesh is not a quantity of the model. See <see cref="MeshTopology"/> and <see cref="MeshData.AnalyzeTopology()"/>.
    /// </summary>
    public int OpenMeshes { get; init; }

    /// <summary>Unique closed triangle meshes whose triangles face inward; left out of <see cref="Volume"/>.</summary>
    public int InsideOutMeshes { get; init; }

    /// <summary>Edges used by one triangle only, over the unique meshes (welded by position).</summary>
    public int OpenEdges { get; init; }

    /// <summary>Edges used by more than two triangles, over the unique meshes.</summary>
    public int NonManifoldEdges { get; init; }

    /// <summary>Edges whose triangles are wound against each other, over the unique meshes.</summary>
    public int InconsistentEdges { get; init; }

    /// <summary>True when every triangle mesh encloses space, so <see cref="Volume"/> is the volume of the whole model; false when some mesh is open or inside out (<see cref="OpenMeshes"/>) or there is no triangle mesh.</summary>
    public bool VolumeIsComplete => ClosedMeshes > 0 && OpenMeshes == 0 && InsideOutMeshes == 0;
}

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

/// <summary>A drawing sheet: its name and paper size in meters (width is the horizontal side as the sheet is shown).</summary>
public sealed record DrawingSheet(string Name, double WidthMeters, double HeightMeters);
