using System.Xml;
using XRay.ThreeD.Formats.Mesh;
using XRay.ThreeD.Formats.SolidWorks;

namespace XRay.ThreeD;

public sealed class LoadOptions
{
    /// <summary>Refuse inputs larger than this many bytes (default 1 GiB).</summary>
    public long MaxInputBytes { get; set; } = 1L << 30;

    /// <summary>Refuse models with more triangles than this (default 100 million).</summary>
    public long MaxTriangles { get; set; } = 100_000_000;

    public CancellationToken Cancellation { get; set; }
}

/// <summary>
/// Entry point: identify a file, read its metadata, or load it as a <see cref="Model3D"/>.
/// Only <see cref="ModelFormatException"/> (and <see cref="OperationCanceledException"/>) escape a load.
/// </summary>
public static class ModelLoader
{
    public static FormatInfo Detect(ReadOnlyMemory<byte> data, string? fileName = null) => ModelFormats.Detect(data, fileName);

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, string? fileName = null, LoadOptions? options = null)
    {
        return Guard(data, fileName, options, (format, d, o) => format.Id switch
        {
            "solidworks" => SolidWorksReader.ReadMetadata(d, o.Cancellation),
            _ => Dispatch(format, d, o).Metadata,
        });
    }

    public static Model3D Load(ReadOnlyMemory<byte> data, string? fileName = null, LoadOptions? options = null)
    {
        return Guard(data, fileName, options, (format, d, o) => Sanitize(Dispatch(format, d, o)));
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

        if (repaired)
        {
            model.Warnings.Add("Some coordinates were not finite numbers and were set to zero.");
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

    /// <summary>Load and write binary glTF in one step.</summary>
    public static byte[] ConvertToGlb(ReadOnlyMemory<byte> data, string? fileName = null, GlbWriteOptions? glb = null, LoadOptions? options = null) =>
        GlbWriter.ToArray(Load(data, fileName, options), glb);

    private static Model3D Dispatch(FormatInfo format, ReadOnlyMemory<byte> data, LoadOptions options) => format.Id switch
    {
        "solidworks" => SolidWorksReader.Read(data, options.Cancellation),
        "stl" => StlReader.Read(data, options),
        "obj" => ObjReader.Read(data, options),
        "ply" => PlyReader.Read(data, options),
        "gltf" => GltfReader.Read(data, options),
        "3mf" => ThreeMfReader.Read(data, options),
        "dxf" => Formats.Cad.DxfReader.Read(data, options),
        _ => throw new UnsupportedModelFormatException(
            format,
            format.IsUnknown
                ? "The file is not in a format this library recognises."
                : $"{format.Name} files are recognised but not supported yet."),
    };

    private static T Guard<T>(ReadOnlyMemory<byte> data, string? fileName, LoadOptions? options, Func<FormatInfo, ReadOnlyMemory<byte>, LoadOptions, T> body)
    {
        options ??= new LoadOptions();
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
            or NullReferenceException or OutOfMemoryException or EndOfStreamException or NotSupportedException)
        {
            throw new ModelFormatException($"The {format.Name} file is damaged or uses a form this library cannot read: {ex.Message}", ex);
        }
    }
}
