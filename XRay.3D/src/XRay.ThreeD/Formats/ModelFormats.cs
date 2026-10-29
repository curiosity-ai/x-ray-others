using System.Text;
using XRay.ThreeD.Formats.SolidWorks;

namespace XRay.ThreeD;

/// <summary>The formats the library knows, and detection from a file name and the first bytes of the file.</summary>
public static class ModelFormats
{
    public static FormatInfo SolidWorks { get; } = new("solidworks", "SolidWorks", "cad", [".sldprt", ".sldasm", ".slddrw"], FormatSupport.Full);

    public static FormatInfo Stl { get; } = new("stl", "STL", "mesh", [".stl"], FormatSupport.Full);

    public static FormatInfo Obj { get; } = new("obj", "Wavefront OBJ", "mesh", [".obj"], FormatSupport.Full);

    public static FormatInfo Ply { get; } = new("ply", "Polygon File Format (PLY)", "mesh", [".ply"], FormatSupport.Full);

    public static FormatInfo Gltf { get; } = new("gltf", "glTF 2.0", "scene", [".gltf", ".glb"], FormatSupport.Full);

    public static FormatInfo ThreeMf { get; } = new("3mf", "3D Manufacturing Format", "mesh", [".3mf"], FormatSupport.Full);

    /// <summary>Formats that are recognised but not yet read. See TODO.md for the order they are planned in.</summary>
    private static readonly FormatInfo[] Identified =
    [
        new("step", "STEP (ISO 10303)", "cad", [".step", ".stp", ".p21"], FormatSupport.Identified),
        new("iges", "IGES", "cad", [".iges", ".igs"], FormatSupport.Identified),
        new("sat", "ACIS SAT", "cad", [".sat", ".sab"], FormatSupport.Identified),
        new("parasolid", "Parasolid", "cad", [".x_t", ".x_b", ".xmt_txt", ".xmt_bin"], FormatSupport.Identified),
        new("jt", "JT", "cad", [".jt"], FormatSupport.Identified),
        new("dxf", "AutoCAD DXF", "cad", [".dxf"], FormatSupport.Identified),
        new("dwg", "AutoCAD DWG", "cad", [".dwg"], FormatSupport.Identified),
        new("ifc", "IFC (BIM)", "bim", [".ifc", ".ifcxml", ".ifczip"], FormatSupport.Identified),
        new("rhino", "Rhino 3DM", "cad", [".3dm"], FormatSupport.Identified),
        new("fbx", "Autodesk FBX", "scene", [".fbx"], FormatSupport.Identified),
        new("collada", "COLLADA", "scene", [".dae"], FormatSupport.Identified),
        new("usd", "Universal Scene Description", "scene", [".usd", ".usda", ".usdc", ".usdz"], FormatSupport.Identified),
        new("3ds", "3D Studio", "scene", [".3ds"], FormatSupport.Identified),
        new("vrml", "VRML / X3D", "scene", [".wrl", ".vrml", ".x3d", ".x3dv"], FormatSupport.Identified),
        new("off", "Object File Format", "mesh", [".off"], FormatSupport.Identified),
        new("sketchup", "SketchUp", "cad", [".skp"], FormatSupport.Identified),
        new("revit", "Revit", "bim", [".rvt", ".rfa", ".rft"], FormatSupport.Identified),
        new("inventor", "Autodesk Inventor", "cad", [".ipt", ".iam", ".idw"], FormatSupport.Identified),
        new("catia", "CATIA", "cad", [".catpart", ".catproduct"], FormatSupport.Identified),
        new("creo", "PTC Creo / Pro/E", "cad", [".prt", ".asm", ".drw"], FormatSupport.Identified),
        new("nx", "Siemens NX", "cad", [".prt"], FormatSupport.Identified),
        new("solidedge", "Solid Edge", "cad", [".par", ".psm", ".asm"], FormatSupport.Identified),
        new("fusion", "Fusion 360", "cad", [".f3d", ".f3z"], FormatSupport.Identified),
        new("freecad", "FreeCAD", "cad", [".fcstd"], FormatSupport.Identified),
        new("openscad", "OpenSCAD", "cad", [".scad"], FormatSupport.Identified),
        new("blender", "Blender", "scene", [".blend"], FormatSupport.Identified),
        new("archicad", "Archicad", "bim", [".pln", ".pla", ".gsm"], FormatSupport.Identified),
        new("sh3d", "Sweet Home 3D", "bim", [".sh3d"], FormatSupport.Identified),
        new("pointcloud", "Point cloud (LAS/LAZ/E57/PCD/PTS)", "pointcloud", [".las", ".laz", ".e57", ".pcd", ".pts", ".xyz"], FormatSupport.Identified),
        new("dwf", "Autodesk DWF", "cad", [".dwf", ".dwfx"], FormatSupport.Identified),
        new("b3d", "Blitz3D / Bazis", "scene", [".b3d"], FormatSupport.Identified),
        new("gcode", "G-code", "toolpath", [".gcode", ".gco", ".nc"], FormatSupport.Identified),
        new("unity", "Unity package", "scene", [".unitypackage"], FormatSupport.Identified),
        new("unreal", "Unreal asset", "scene", [".uasset", ".umap"], FormatSupport.Identified),
    ];

    /// <summary>A ZIP package that is not a 3D format this library knows. Entries are not searched yet.</summary>
    public static FormatInfo Archive { get; } = new("archive", "ZIP archive", "archive", [".zip"], FormatSupport.Identified);

    private static readonly FormatInfo[] Supported = [SolidWorks, Stl, Obj, Ply, Gltf, ThreeMf];

    public static IEnumerable<FormatInfo> All => Supported.Concat(Identified).Append(Archive);

    public static FormatInfo? FromExtension(string? fileNameOrExtension)
    {
        if (string.IsNullOrEmpty(fileNameOrExtension))
        {
            return null;
        }

        var extension = Path.GetExtension(fileNameOrExtension);
        if (extension.Length == 0 && fileNameOrExtension[0] == '.')
        {
            extension = fileNameOrExtension;
        }

        return All.FirstOrDefault(f => f.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Content first (magic bytes), then the file name. Returns <see cref="FormatInfo.Unknown"/> when neither says.</summary>
    public static FormatInfo Detect(ReadOnlyMemory<byte> data, string? fileName = null, CancellationToken cancellation = default)
    {
        var byExtension = FromExtension(fileName);
        var head = data.Span[..Math.Min(data.Length, 512)];

        if (head.Length >= 4 && head[0] == 'g' && head[1] == 'l' && head[2] == 'T' && head[3] == 'F')
        {
            return Gltf;
        }

        if (StartsWith(head, "ply"))
        {
            return Ply;
        }

        if (StartsWith(head, "Kaydara FBX Binary"))
        {
            return Find("fbx");
        }

        if (StartsWith(head, "3D Geometry File Format"))
        {
            return Find("rhino");
        }

        if (StartsWith(head, "AC10"))
        {
            return Find("dwg");
        }

        if (StartsWith(head, "ISO-10303-21"))
        {
            return Contains(data.Span, "FILE_SCHEMA", "IFC") ? Find("ifc") : Find("step");
        }

        if (head.Length >= 2 && head[0] == 'P' && head[1] == 'K')
        {
            return DetectZip(data) ?? (byExtension is { Id: not ("solidworks" or "3mf") } zipExtension ? zipExtension : Archive);
        }

        if (byExtension?.Id == "solidworks" || (byExtension is null && SolidWorksReader.Sniff(data, cancellation)))
        {
            return SolidWorksReader.Sniff(data, cancellation) ? SolidWorks : byExtension ?? SolidWorks;
        }

        if (byExtension is not null)
        {
            return byExtension;
        }

        if (LooksLikeStl(data.Span))
        {
            return Stl;
        }

        return FormatInfo.Unknown;
    }

    internal static bool LooksLikeStl(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 84)
        {
            var triangles = BitConverter.ToUInt32(data.Slice(80, 4));
            if (84L + 50L * triangles == data.Length)
            {
                return true;
            }
        }

        var head = data[..Math.Min(data.Length, 256)];
        return StartsWith(head.TrimStart((byte)' '), "solid") && Contains(head, "facet", "");
    }

    /// <summary>Classifies a ZIP package by the names inside it.</summary>
    private static FormatInfo? DetectZip(ReadOnlyMemory<byte> data)
    {
        try
        {
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(data.ToArray(), writable: false), System.IO.Compression.ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            if (names.Any(n => n.EndsWith(".model", StringComparison.OrdinalIgnoreCase) && n.StartsWith("3D/", StringComparison.OrdinalIgnoreCase)))
            {
                return ThreeMf;
            }

            if (names.Contains("Document.xml", StringComparer.Ordinal))
            {
                return Find("freecad");
            }

            if (names.Any(n => n.EndsWith(".home", StringComparison.OrdinalIgnoreCase) || n == "Home"))
            {
                return Find("sh3d");
            }

            if (names.Contains("model.gltf", StringComparer.OrdinalIgnoreCase) || names.Any(n => n.EndsWith(".usdc", StringComparison.OrdinalIgnoreCase)))
            {
                return Find("usd");
            }

            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static FormatInfo Find(string id) => Identified.First(f => f.Id == id);

    private static bool StartsWith(ReadOnlySpan<byte> span, string text) =>
        span.Length >= text.Length && span[..text.Length].SequenceEqual(Encoding.ASCII.GetBytes(text));

    private static bool Contains(ReadOnlySpan<byte> span, string first, string second)
    {
        var window = span[..Math.Min(span.Length, 8192)];
        var text = Encoding.ASCII.GetString(window);
        return text.Contains(first, StringComparison.Ordinal) && text.Contains(second, StringComparison.Ordinal);
    }
}
