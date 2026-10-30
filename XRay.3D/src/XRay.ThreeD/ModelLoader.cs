using System.Xml;
using XRay.ThreeD.Formats.Mesh;
using XRay.ThreeD.Formats.SolidWorks;

namespace XRay.ThreeD;

public sealed class LoadOptions
{
    /// <summary>Refuse inputs larger than this many bytes (default 1 GiB).</summary>
    public long MaxInputBytes { get; set; } = 1L << 30;

    /// <summary>Point clouds with more points than this are thinned evenly (default 20 million).</summary>
    public long MaxPoints { get; set; } = 20_000_000;

    /// <summary>B-rep tessellation: the largest distance between the curved surface and its triangles, relative to the model's size (default 0.001).</summary>
    public double LinearDeflection { get; set; } = 0.001;

    /// <summary>B-rep tessellation: the largest angle in radians between neighbouring samples on a curve or surface (default 0.25).</summary>
    public double AngularDeflection { get; set; } = 0.25;

    /// <summary>Refuse models with more triangles than this (default 100 million).</summary>
    public long MaxTriangles { get; set; } = 100_000_000;

    public CancellationToken Cancellation { get; set; }

    /// <summary>
    /// Supplies files a model refers to but does not contain (the .mtl of an OBJ, for example), given the name as the model writes it. Returns
    /// null when there is no such file. ZIP archives set this to look among their own entries.
    /// </summary>
    public Func<string, byte[]?>? ResolveCompanion { get; set; }

    /// <summary>
    /// The unit of the coordinates for formats that do not state one (STL, OBJ, PLY, OFF, 3DS, XYZ, PTS); ignored for formats that do.
    /// Null leaves the unit unknown, as before.
    /// </summary>
    public LengthUnit? AssumedUnits { get; set; }

    /// <summary>The up axis for formats that do not state one (STL, OBJ, PLY, OFF, 3DS, XYZ, PTS); ignored for formats that do.</summary>
    public UpAxis? AssumedUpAxis { get; set; }

    /// <summary>How many archives deep this load is; nested packages stop at three.</summary>
    internal int ArchiveDepth { get; set; }

    internal LoadOptions Clone() => (LoadOptions)MemberwiseClone();
}

/// <summary>
/// Entry point: identify a file, read its metadata, or load it as a <see cref="Model3D"/>.
/// Only <see cref="ModelFormatException"/> (and <see cref="OperationCanceledException"/>) escape a load.
/// </summary>
public static class ModelLoader
{
    public static FormatInfo Detect(ReadOnlyMemory<byte> data, string? fileName = null) => ModelFormats.Detect(data, fileName);

    /// <summary>Like <see cref="Detect(ReadOnlyMemory{byte}, string?)"/>, stopping with <see cref="OperationCanceledException"/> when the options' token is cancelled (archives are opened to look inside).</summary>
    public static FormatInfo Detect(ReadOnlyMemory<byte> data, string? fileName, LoadOptions options) => ModelFormats.Detect(data, fileName, options.Cancellation);

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, string? fileName = null, LoadOptions? options = null)
    {
        return Guard(data, fileName, options, (format, d, o) => format.Id switch
        {
            "solidworks" => SolidWorksReader.ReadMetadata(d, o.Cancellation),
            "las" or "laz" => Formats.PointCloud.LasReader.ReadMetadata(d),
            "step" => Formats.Step.StepReader.ReadMetadata(d, o),
            "ifc" => Formats.Bim.IfcReader.ReadMetadata(d, o),
            "iges" => Formats.Cad.IgesReader.ReadMetadata(d, o),
            "dwg" => Formats.Cad.DwgReader.ReadMetadata(d, o),
            _ => ApplyHints(format, Dispatch(format, d, o), o).Metadata,
        });
    }

    /// <summary>
    /// The preview image saved in the file, without loading its geometry, for formats whose geometry is not decoded (DWG) as well as those that
    /// are; null when the file has none.
    /// </summary>
    public static PreviewImage? ReadPreview(ReadOnlyMemory<byte> data, string? fileName = null, LoadOptions? options = null)
    {
        return Guard(data, fileName, options, (format, d, o) => format.Id switch
        {
            "dwg" => Formats.Cad.DwgReader.ReadPreview(d, o),
            _ => format.Support == FormatSupport.Full ? Dispatch(format, d, o).Preview : null,
        });
    }

    public static Model3D Load(ReadOnlyMemory<byte> data, string? fileName = null, LoadOptions? options = null)
    {
        return Guard(data, fileName, options, (format, d, o) => Sanitize(ApplyHints(format, Dispatch(format, d, o), o)));
    }

    private static readonly HashSet<string> UnstatedUnits = new(StringComparer.Ordinal) { "stl", "obj", "ply", "off", "3ds", "xyz", "pts" };

    /// <summary>The caller's unit and up-axis hints, for the formats whose files do not carry them.</summary>
    private static Model3D ApplyHints(FormatInfo format, Model3D model, LoadOptions options)
    {
        if (UnstatedUnits.Contains(format.Id))
        {
            if (options.AssumedUnits is { } units)
            {
                model.Metadata.Units = units;
            }

            if (options.AssumedUpAxis is { } up)
            {
                model.Metadata.UpAxis = up;
            }
        }

        return model;
    }

    /// <summary>
    /// Replaces non-finite coordinates (NaN, infinity) with zero and drops indices that point outside their mesh, so that
    /// anything a reader returns can be written to glTF, which forbids both.
    /// </summary>
    private static Model3D Sanitize(Model3D model)
    {
        var repaired = false;
        foreach (var mesh in model.Meshes)
        {
            repaired |= ReplaceNonFinite(mesh.Positions) | ReplaceNonFinite(mesh.Normals) | ReplaceNonFinite(mesh.Colors);
            repaired |= ReplaceNonFinite(mesh.TexCoords);
            if (mesh.TexCoords.Length != mesh.VertexCount * 2)
            {
                mesh.TexCoords = [];
            }

            if (mesh.Normals.Length > 0 && !NormaliseNormals(mesh))
            {
                mesh.Normals = [];
                model.Warnings.Add("A mesh had zero-length normals; they were dropped so the viewer computes its own.");
            }

            if (mesh.Indices.Any(i => i >= (uint)mesh.VertexCount))
            {
                throw new ModelFormatException("A mesh has indices outside its vertex range.");
            }
        }

        var texts = model.Texts.RemoveAll(t => !float.IsFinite(t.Position.X) || !float.IsFinite(t.Position.Y) || !float.IsFinite(t.Position.Z) || !float.IsFinite(t.Height) || !float.IsFinite(t.Rotation));
        var fills = model.Fills.RemoveAll(f => f.Loops.Any(l => l.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))));
        repaired |= texts + fills > 0;

        foreach (var node in model.Nodes())
        {
            var m = node.Transform;
            if (!IsFinite(m))
            {
                node.Transform = System.Numerics.Matrix4x4.Identity;
                repaired = true;
            }
        }

        foreach (var material in model.Materials)
        {
            if (material.BaseColorTexture is { } texture)
            {
                if (texture.Texture < 0 || texture.Texture >= model.Textures.Count)
                {
                    material.BaseColorTexture = null;
                }
                else if (!float.IsFinite(texture.Offset.X) || !float.IsFinite(texture.Offset.Y) || !float.IsFinite(texture.Scale.X) || !float.IsFinite(texture.Scale.Y) || !float.IsFinite(texture.Rotation))
                {
                    texture.Offset = System.Numerics.Vector2.Zero;
                    texture.Scale = System.Numerics.Vector2.One;
                    texture.Rotation = 0;
                    repaired = true;
                }
            }

            var c = material.BaseColor;
            if (!float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z) || !float.IsFinite(c.W) || !float.IsFinite(material.Metallic) || !float.IsFinite(material.Roughness))
            {
                material.BaseColor = new System.Numerics.Vector4(0.8f, 0.8f, 0.8f, 1f);
                material.Metallic = 0;
                material.Roughness = 0.6f;
                repaired = true;
            }
        }

        if (repaired)
        {
            model.Warnings.Add("Some numbers in the file were not finite (coordinates, transforms or colours) and were replaced by neutral values.");
            model.Metadata.Bounds = model.ComputeBounds();
        }

        return model;
    }

    /// <summary>glTF requires unit-length normals. Returns false when a normal has no direction (and the set cannot be used).</summary>
    private static bool NormaliseNormals(MeshData mesh)
    {
        var n = mesh.Normals;
        if (n.Length != mesh.Positions.Length)
        {
            return false;
        }

        for (var i = 0; i + 2 < n.Length; i += 3)
        {
            var length = MathF.Sqrt(n[i] * n[i] + n[i + 1] * n[i + 1] + n[i + 2] * n[i + 2]);
            if (!(length > 1e-12f))
            {
                return false;
            }

            if (MathF.Abs(length - 1f) > 1e-5f)
            {
                n[i] /= length;
                n[i + 1] /= length;
                n[i + 2] /= length;
            }
        }

        return true;
    }

    private static bool IsFinite(System.Numerics.Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
        && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    private static bool ReplaceNonFinite(float[] values)
    {
        var changed = false;
        for (var i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]))
            {
                values[i] = 0;
                changed = true;
            }
        }

        return changed;
    }

    public static ModelMetadata ReadMetadata(string path, LoadOptions? options = null) =>
        ReadMetadata(File.ReadAllBytes(path), Path.GetFileName(path), options);

    public static Model3D Load(string path, LoadOptions? options = null) =>
        Load(File.ReadAllBytes(path), Path.GetFileName(path), options);

    /// <summary>
    /// Loads a drawing (a model with <see cref="Model3D.Is2D"/>) and writes it as SVG. Three-dimensional content is refused unless
    /// <see cref="SvgWriteOptions.ProjectFlat"/> asks for its top view: GLB is the output for 3D.
    /// </summary>
    public static byte[] ConvertToSvg(ReadOnlyMemory<byte> data, string? fileName = null, SvgWriteOptions? svg = null, LoadOptions? options = null)
    {
        var model = Load(data, fileName, options);
        svg = svg?.Clone() ?? new SvgWriteOptions();
        if (!svg.Cancellation.CanBeCanceled && options is not null)
        {
            svg.Cancellation = options.Cancellation;
        }

        return System.Text.Encoding.UTF8.GetBytes(SvgWriter.ToString(model, svg));
    }

    /// <summary>Load and write binary glTF in one step.</summary>
    public static byte[] ConvertToGlb(ReadOnlyMemory<byte> data, string? fileName = null, GlbWriteOptions? glb = null, LoadOptions? options = null)
    {
        var model = Load(data, fileName, options);
        if (glb is null && IsGlb(data.Span))
        {
            // A GLB read without trouble is already what this would write: hand it back untouched (the load above validated it).
            return data.ToArray();
        }

        glb = glb?.Clone() ?? new GlbWriteOptions();
        if (!glb.Cancellation.CanBeCanceled && options is not null)
        {
            glb.Cancellation = options.Cancellation;
        }

        return GlbWriter.ToArray(model, glb);
    }

    private static bool IsGlb(ReadOnlySpan<byte> data) => data.Length >= 12 && data[0] == (byte)'g' && data[1] == (byte)'l' && data[2] == (byte)'T' && data[3] == (byte)'F';

    private static Model3D Dispatch(FormatInfo format, ReadOnlyMemory<byte> data, LoadOptions options) => format.Id switch
    {
        "solidworks" => SolidWorksReader.Read(data, options),
        "stl" => StlReader.Read(data, options),
        "obj" => ObjReader.Read(data, options),
        "ply" => PlyReader.Read(data, options),
        "gltf" => GltfReader.Read(data, options),
        "3mf" => ThreeMfReader.Read(data, options),
        "dxf" => Formats.Cad.DxfReader.Read(data, options),
        "3ds" => Max3dsReader.Read(data, options),
        "collada" => ColladaReader.Read(data, options),
        "fbx" => FbxReader.Read(data, options),
        "usd" => UsdaReader.Read(data, options),
        "dwg" => Formats.Cad.DwgReader.Read(data, options),
        "step" => Formats.Step.StepReader.Read(data, options),
        "ifc" => Formats.Bim.IfcReader.Read(data, options),
        "archive" => Formats.Packages.ArchiveReader.Read(data, options),
        "iges" => Formats.Cad.IgesReader.Read(data, options),
        "freecad" => Formats.Cad.FreecadReader.Read(data, options),
        "gcode" => Formats.Toolpath.GcodeReader.Read(data, options),
        "sh3d" => Formats.Bim.Sh3dReader.Read(data, options),
        "x3d" => X3dReader.ReadXml(data, options),
        "vrml" => X3dReader.ReadVrml(data, options),
        "off" => OffReader.Read(data, options),
        "amf" => AmfReader.Read(data, options),
        "las" or "laz" => Formats.PointCloud.LasReader.Read(data, options),
        "pcd" => Formats.PointCloud.PcdReader.Read(data, options),
        "xyz" => Formats.PointCloud.XyzReader.Read(data, ModelFormats.Xyz, options),
        "pts" => Formats.PointCloud.XyzReader.Read(data, ModelFormats.Pts, options),
        _ => throw new UnsupportedModelFormatException(
            format,
            format.IsUnknown
                ? "The file is not in a format this library recognises."
                : $"{format.Name} files are recognised but not supported yet."),
    };

    private static T Guard<T>(ReadOnlyMemory<byte> data, string? fileName, LoadOptions? options, Func<FormatInfo, ReadOnlyMemory<byte>, LoadOptions, T> body)
    {
        options ??= new LoadOptions();
        options.Cancellation.ThrowIfCancellationRequested();
        if (data.Length > options.MaxInputBytes)
        {
            throw new ModelFormatException($"The file is larger than the {options.MaxInputBytes} byte limit.");
        }

        var format = ModelFormats.Detect(data, fileName, options.Cancellation);
        try
        {
            return body(format, data, options);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or InvalidOperationException
            or OverflowException or FormatException or XmlException or System.Text.Json.JsonException or KeyNotFoundException
            or NullReferenceException or OutOfMemoryException or IOException or NotSupportedException)
        {
            throw new ModelFormatException($"The {format.Name} file is damaged or uses a form this library cannot read: {ex.Message}", ex);
        }
    }
}
