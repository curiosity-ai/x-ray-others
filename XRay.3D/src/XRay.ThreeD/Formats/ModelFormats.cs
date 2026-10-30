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

    public static FormatInfo Las { get; } = new("las", "LAS point cloud", "pointcloud", [".las"], FormatSupport.Full);

    public static FormatInfo Dwg { get; } = new("dwg", "AutoCAD DWG", "cad", [".dwg"], FormatSupport.Metadata);

    public static FormatInfo Laz { get; } = new("laz", "LAZ (compressed LAS)", "pointcloud", [".laz"], FormatSupport.Full);

    public static FormatInfo Pcd { get; } = new("pcd", "Point Cloud Library PCD", "pointcloud", [".pcd"], FormatSupport.Full);

    public static FormatInfo Xyz { get; } = new("xyz", "XYZ text point cloud", "pointcloud", [".xyz"], FormatSupport.Full);

    public static FormatInfo Pts { get; } = new("pts", "PTS text point cloud", "pointcloud", [".pts"], FormatSupport.Full);

    public static FormatInfo Off { get; } = new("off", "Object File Format (OFF)", "mesh", [".off"], FormatSupport.Full);

    public static FormatInfo Amf { get; } = new("amf", "Additive Manufacturing File (AMF)", "mesh", [".amf"], FormatSupport.Full);

    public static FormatInfo Max3ds { get; } = new("3ds", "3D Studio (3DS)", "scene", [".3ds"], FormatSupport.Full);

    public static FormatInfo Collada { get; } = new("collada", "COLLADA", "scene", [".dae"], FormatSupport.Full);

    public static FormatInfo X3d { get; } = new("x3d", "X3D (XML)", "scene", [".x3d"], FormatSupport.Full);

    public static FormatInfo X3dv { get; } = new("x3dv", "X3D classic encoding", "scene", [".x3dv"], FormatSupport.Full);

    public static FormatInfo Vrml { get; } = new("vrml", "VRML97", "scene", [".wrl", ".vrml"], FormatSupport.Full);

    public static FormatInfo Fbx { get; } = new("fbx", "Autodesk FBX", "scene", [".fbx"], FormatSupport.Full);

    public static FormatInfo Usd { get; } = new("usd", "Universal Scene Description (USDA text, USDZ)", "scene", [".usda", ".usd", ".usdz", ".usdc"], FormatSupport.Full);

    public static FormatInfo Step { get; } = new("step", "STEP (ISO 10303-21)", "cad", [".step", ".stp", ".p21"], FormatSupport.Full);

    public static FormatInfo Ifc { get; } = new("ifc", "IFC (BIM)", "bim", [".ifc", ".ifczip"], FormatSupport.Full);

    public static FormatInfo Iges { get; } = new("iges", "IGES", "cad", [".iges", ".igs"], FormatSupport.Full);

    public static FormatInfo FreeCad { get; } = new("freecad", "FreeCAD", "cad", [".fcstd"], FormatSupport.Full);

    public static FormatInfo Gcode { get; } = new("gcode", "G-code", "toolpath", [".gcode", ".gco", ".nc", ".ngc"], FormatSupport.Full);

    public static FormatInfo Sh3d { get; } = new("sh3d", "Sweet Home 3D", "bim", [".sh3d"], FormatSupport.Full);

    public static FormatInfo B3d { get; } = new("b3d", "Blitz3D model (and the Bazis container)", "scene", [".b3d"], FormatSupport.Full);

    public static FormatInfo Unity { get; } = new("unity", "Unity package", "scene", [".unitypackage"], FormatSupport.Full);

    public static FormatInfo Dxf { get; } = new("dxf", "AutoCAD DXF", "cad", [".dxf"], FormatSupport.Full);

    public static FormatInfo OpenScad { get; } = new("openscad", "OpenSCAD", "cad", [".scad"], FormatSupport.Full);

    public static FormatInfo Unreal { get; } = new("unreal", "Unreal asset", "scene", [".uasset", ".umap"], FormatSupport.Full);

    public static FormatInfo E57 { get; } = new("e57", "ASTM E57", "pointcloud", [".e57"], FormatSupport.Full);

    public static FormatInfo Psu { get; } = new("psu", "PlayStation 2 save", "scene", [".psu"], FormatSupport.Full);

    public static FormatInfo Rlf { get; } = new("rlf", "Relief height map", "mesh", [".rlf"], FormatSupport.Metadata);

    public static FormatInfo Blender { get; } = new("blender", "Blender", "scene", [".blend"], FormatSupport.Full);

    public static FormatInfo Rhino { get; } = new("rhino", "Rhino 3DM", "cad", [".3dm"], FormatSupport.Full);

    public static FormatInfo Acis { get; } = new("sat", "ACIS SAT", "cad", [".sat", ".sab"], FormatSupport.Full);

    public static FormatInfo Dwf { get; } = new("dwf", "Autodesk DWF", "cad", [".dwf", ".dwfx"], FormatSupport.Full);

    public static FormatInfo Parasolid { get; } = new("parasolid", "Parasolid", "cad", [".x_t", ".x_b", ".xmt_txt", ".xmt_bin"], FormatSupport.Metadata);

    public static FormatInfo Archicad { get; } = new("archicad", "Archicad", "bim", [".pln", ".pla", ".gsm"], FormatSupport.Full);

    public static FormatInfo SketchUp { get; } = new("sketchup", "SketchUp", "cad", [".skp"], FormatSupport.Metadata);

    public static FormatInfo ThreeMf { get; } = new("3mf", "3D Manufacturing Format", "mesh", [".3mf"], FormatSupport.Full);

    /// <summary>Formats that are recognised but not yet read. See TODO.md for the order they are planned in.</summary>
    private static readonly FormatInfo[] Identified =
    [
        new("jt", "JT", "cad", [".jt"], FormatSupport.Identified),
        new("ifcxml", "IFC XML", "bim", [".ifcxml"], FormatSupport.Identified),
        new("revit", "Revit", "bim", [".rvt", ".rfa", ".rft"], FormatSupport.Metadata),
        new("inventor", "Autodesk Inventor", "cad", [".ipt", ".iam", ".idw"], FormatSupport.Metadata),
        new("catia", "CATIA", "cad", [".catpart", ".catproduct"], FormatSupport.Identified),
        new("creo", "PTC Creo / Pro/E", "cad", [".prt", ".asm", ".drw"], FormatSupport.Identified),
        new("nx", "Siemens NX", "cad", [".prt"], FormatSupport.Identified),
        new("solidedge", "Solid Edge", "cad", [".par", ".psm", ".asm"], FormatSupport.Identified),
        new("fusion", "Fusion 360", "cad", [".f3d", ".f3z"], FormatSupport.Identified),
    ];

    /// <summary>A ZIP package that is not a 3D format this library knows; loading it loads the primary model inside (see <c>ArchiveReader</c>).</summary>
    public static FormatInfo Archive { get; } = new("archive", "ZIP archive", "archive", [".zip"], FormatSupport.Full);

    private static readonly FormatInfo[] Supported = [SolidWorks, Stl, Obj, Ply, Gltf, ThreeMf, Dxf, Las, Laz, Pcd, Xyz, Pts, Off, Amf, Max3ds, Collada, X3d, Vrml, Fbx, Usd, Step, Ifc, Iges, FreeCad, Gcode, Sh3d, B3d, X3dv, Unity, OpenScad, Psu, Rlf, Unreal, E57, Blender, Archicad, SketchUp, Acis, Dwf, Parasolid];

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

        if (Formats.Parasolid.ParasolidReader.Sniff(head))
        {
            return Parasolid;
        }

        if (Formats.Dwf.DwfReader.Sniff(head))
        {
            return Dwf;
        }

        if (Formats.Acis.SabReader.Sniff(head))
        {
            return Acis;
        }

        if (Formats.Acis.SatReader.Sniff(head) && (byExtension?.Id == "sat" || byExtension is null))
        {
            return Acis;
        }

        if (StartsWith(head, "AutoCAD Binary DXF") || LooksLikeDxf(head))
        {
            return Dxf;
        }

        if (Formats.Cad.IgesReader.Sniff(head))
        {
            return Iges;
        }

        if (StartsWith(head, "LASF"))
        {
            return (head.Length > 104 && (head[104] & 0x80) != 0) || byExtension?.Id == "laz" ? Laz : Las;
        }

        if (StartsWith(head, "# .PCD") || StartsWith(head, "VERSION .7") || StartsWith(head, "VERSION 0.7"))
        {
            return Pcd;
        }

        if (StartsWith(head, "OFF") || StartsWith(head, "COFF") || StartsWith(head, "NOFF"))
        {
            return Off;
        }

        if (head.Length >= 6 && head[0] == 0x4D && head[1] == 0x4D && byExtension?.Id == "3ds")
        {
            return Max3ds;
        }

        if (StartsWith(head, "#usda") || StartsWith(head, "PXR-USDC"))
        {
            return Usd;
        }

        if (StartsWith(head, "ply"))
        {
            return Ply;
        }

        if (StartsWith(head, "Kaydara FBX Binary") || (head.Length > 0 && head[0] == ';' && Contains(head, "FBX", "")))
        {
            return Fbx;
        }

        if (Formats.Mesh.B3dReader.IsBlitz(head) || Formats.Mesh.B3dReader.IsBazis(head))
        {
            return B3d;
        }

        if (Formats.PointCloud.E57Reader.Sniff(head))
        {
            return E57;
        }

        if (Formats.Game.UassetReader.Sniff(head))
        {
            return Unreal;
        }

        if (Formats.Saves.PsuReader.Sniff(head))
        {
            return Psu;
        }

        if (Formats.Relief.RlfReader.Sniff(head))
        {
            return Rlf;
        }

        if (Formats.Blender.BlendFile.Sniff(head) || (byExtension?.Id == "blender" && Formats.Blender.BlendFile.IsCompressed(head)))
        {
            return Blender;
        }

        if (Formats.SketchUp.SketchUpReader.Sniff(head))
        {
            return SketchUp;
        }

        if (head.Length > 0x34 && head[0] == (byte)'W' && head[1] == (byte)'W' && Formats.Archicad.GsmReader.Sniff(data.Span))
        {
            return Archicad;
        }

        if (StartsWith(head, "3D Geometry File Format"))
        {
            return Rhino;
        }

        if (StartsWith(head, "AC10"))
        {
            return Dwg;
        }

        if (StartsWith(head, "ISO-10303-21"))
        {
            return Contains(data.Span, "FILE_SCHEMA", "IFC") ? Ifc : Step;
        }

        if (head.Length >= 2 && head[0] == 'P' && head[1] == 'K')
        {
            return DetectZip(data) ?? (byExtension is { Id: not ("solidworks" or "3mf") } zipExtension ? zipExtension : Archive);
        }

        if (StartsWith(head, "#VRML V2.0") || StartsWith(head, "#VRML V1.0"))
        {
            return Vrml;
        }

        if (StartsWith(head, "#X3D"))
        {
            return X3dv;
        }

        if (head.Length > 0 && head[0] == '<' && Contains(head, "<X3D", ""))
        {
            return X3d;
        }

        if (head.Length > 0 && head[0] == '<' && Contains(head, "<COLLADA", ""))
        {
            return Collada;
        }

        if (byExtension?.Id == "amf" && head.Length > 0 && head[0] == '<')
        {
            return Amf;
        }

        if (byExtension?.Id == "solidworks" || (byExtension is null && SolidWorksReader.Sniff(data, cancellation)))
        {
            return SolidWorksReader.Sniff(data, cancellation) ? SolidWorks : byExtension ?? SolidWorks;
        }

        if (byExtension is { Id: "gcode" })
        {
            // ".nc" is also NetCDF: only text counts as G-code.
            return Formats.Toolpath.GcodeReader.IsText(head) ? Gcode : FormatInfo.Unknown;
        }

        if (byExtension is not null)
        {
            return byExtension;
        }

        if (LooksLikeStl(data.Span))
        {
            return Stl;
        }

        if (Formats.Toolpath.GcodeReader.LooksLikeGcode(data.Span[..Math.Min(data.Length, 4096)]))
        {
            return Gcode;
        }

        return FormatInfo.Unknown;
    }

    private static bool LooksLikeDxf(ReadOnlySpan<byte> head)
    {
        var text = Encoding.ASCII.GetString(head).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        return text.StartsWith("0", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(text, @"^0\s*\r?\n\s*SECTION\b", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
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

            if (names.Any(n => n.EndsWith(".amf", StringComparison.OrdinalIgnoreCase)) && names.Count <= 3)
            {
                return Amf;
            }

            if (names.Count <= 3 && names.Any(n => n.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase)))
            {
                return Ifc;
            }

            if (names.Contains("manifest.xml", StringComparer.OrdinalIgnoreCase) && (names.Any(n => n.EndsWith(".w2d", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".w3d", StringComparison.OrdinalIgnoreCase)) || names.Any(n => n.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase))))
            {
                return Dwf;
            }

            if (names.Contains("Document.xml", StringComparer.Ordinal))
            {
                return FreeCad;
            }

            if (names.Any(n => n.EndsWith(".home", StringComparison.OrdinalIgnoreCase) || n == "Home" || n == "Home.xml"))
            {
                return Sh3d;
            }

            if (names.Any(n => n.EndsWith(".usdc", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".usda", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".usd", StringComparison.OrdinalIgnoreCase)))
            {
                return Usd;
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
