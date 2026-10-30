using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.Game;

using XRay.ThreeD.Internal;

/// <summary>
/// Unreal Engine packages (<c>.uasset</c>, <c>.umap</c>, magic <c>0x9E2A83C1</c>): the package summary (legacy and engine versions, flags, table offsets,
/// engine version that saved it, thumbnail table), the name table, imports (class, package, outer, name), exports (class, super, outer, name, flags,
/// serial range), the tagged property list of each export (simple values read, the rest skipped by their stored size), and the saved asset thumbnail.
/// Both UE4 summaries (legacy version -5 and below) and UE5 ones (-8, with the extra UE5 file version) are walked; record sizes of the import and export
/// tables are taken from the gaps between the table offsets rather than from the version, so a record layout that grew in some engine version still
/// parses. Geometry is not decoded: static meshes keep their vertices in bulk data whose layout depends on the engine version and on whether the
/// package was cooked, so a mesh export is reported with its size and the model has no geometry.
/// </summary>
internal static class UassetReader
{
    public const uint Magic = 0x9E2A83C1;
    private const int MaxTable = 2_000_000;
    private const int MaxString = 1 << 16;

    public static bool Sniff(ReadOnlySpan<byte> d) => d.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(d) == Magic;

    internal sealed class Package
    {
        public int Legacy, Ue3, Ue4, Licensee, Ue5;
        public int TotalHeaderSize;
        public string Folder = "";
        public uint Flags;
        public int NameCount, NameOffset, ExportCount, ExportOffset, ImportCount, ImportOffset, DependsOffset;
        public int ThumbnailOffset = -1;
        public int AssetRegistryOffset;
        public long BulkDataStart;
        public Guid PackageGuid;
        public string SavedBy = "";
        public string Branch = "";
        public List<string> Names = new();
        public List<Import> Imports = new();
        public List<Export> Exports = new();
        public bool FilterEditorOnly => (Flags & 0x80000000u) != 0;
    }

    internal sealed record Import(string ClassPackage, string ClassName, int Outer, string Name);

    internal sealed record Export(int Class, int Super, int Outer, string Name, uint ObjectFlags, long Size, long Offset);

    private ref struct Cursor
    {
        public ReadOnlySpan<byte> D;
        public int P;

        public Cursor(ReadOnlySpan<byte> d, int p)
        {
            D = d;
            P = p;
        }

        public int I32()
        {
            if (P < 0 || P + 4 > D.Length)
            {
                throw new ModelFormatException("The package is truncated.");
            }

            var v = BinaryPrimitives.ReadInt32LittleEndian(D[P..]);
            P += 4;
            return v;
        }

        public uint U32() => (uint)I32();

        public long I64()
        {
            if (P < 0 || P + 8 > D.Length)
            {
                throw new ModelFormatException("The package is truncated.");
            }

            var v = BinaryPrimitives.ReadInt64LittleEndian(D[P..]);
            P += 8;
            return v;
        }

        public ushort U16()
        {
            if (P < 0 || P + 2 > D.Length)
            {
                throw new ModelFormatException("The package is truncated.");
            }

            var v = BinaryPrimitives.ReadUInt16LittleEndian(D[P..]);
            P += 2;
            return v;
        }

        public byte U8()
        {
            if (P < 0 || P >= D.Length)
            {
                throw new ModelFormatException("The package is truncated.");
            }

            return D[P++];
        }

        public Guid Guid()
        {
            if (P < 0 || P + 16 > D.Length)
            {
                throw new ModelFormatException("The package is truncated.");
            }

            var g = new Guid(D.Slice(P, 16));
            P += 16;
            return g;
        }

        public string Str()
        {
            var n = I32();
            if (n == 0)
            {
                return "";
            }

            if (n > 0)
            {
                if (n > MaxString || P + n > D.Length)
                {
                    throw new ModelFormatException("A package string is out of range.");
                }

                var s = Encoding.Latin1.GetString(D.Slice(P, n - 1));
                P += n;
                return s;
            }

            var chars = -(long)n;
            if (chars > MaxString || P + chars * 2 > D.Length)
            {
                throw new ModelFormatException("A package string is out of range.");
            }

            var u = Encoding.Unicode.GetString(D.Slice(P, (int)(chars - 1) * 2));
            P += (int)chars * 2;
            return u;
        }
    }

    public static Package ReadPackage(ReadOnlySpan<byte> d, LoadOptions options)
    {
        var c = new Cursor(d, 0);
        if (c.U32() != Magic)
        {
            throw new ModelFormatException("The file is not an Unreal package.");
        }

        var pk = new Package { Legacy = c.I32() };
        if (pk.Legacy >= 0 || pk.Legacy < -9)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Unreal, "This Unreal package predates the versioned format (legacy version " + pk.Legacy + ").");
        }

        if (pk.Legacy != -4)
        {
            pk.Ue3 = c.I32();
        }

        pk.Ue4 = c.I32();
        if (pk.Legacy <= -8)
        {
            pk.Ue5 = c.I32();
        }

        pk.Licensee = c.I32();
        if (pk.Legacy <= -2)
        {
            var count = c.I32();
            if (count is < 0 or > 4096)
            {
                throw new ModelFormatException("The custom version table is out of range.");
            }

            for (var i = 0; i < count; i++)
            {
                if (pk.Legacy == -2)
                {
                    c.I32();
                    c.I32();
                }
                else
                {
                    c.Guid();
                    c.I32();
                    if (pk.Legacy > -6)
                    {
                        c.Str();
                    }
                }
            }
        }

        if (pk.Ue4 == 0 && pk.Ue3 == 0 && pk.Licensee == 0 && pk.Legacy > -3)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Unreal, "The package is unversioned.");
        }

        pk.TotalHeaderSize = c.I32();
        if (pk.Ue5 >= 1010)
        {
            // UE 5.3 and later: the package-name table moved into the summary (an offset and a count that precede the names).
            c.I32();
        }

        pk.Folder = c.Str();
        pk.Flags = c.U32();
        pk.NameCount = c.I32();
        pk.NameOffset = c.I32();
        if (pk.Ue5 >= 1008)
        {
            c.I32();
            c.I32();
        }

        if (!pk.FilterEditorOnly && pk.Ue4 >= 516)
        {
            c.Str();
        }

        if (pk.Ue4 >= 459)
        {
            c.I32();
            c.I32();
        }

        pk.ExportCount = c.I32();
        pk.ExportOffset = c.I32();
        pk.ImportCount = c.I32();
        pk.ImportOffset = c.I32();
        pk.DependsOffset = c.I32();
        if (pk.Ue4 >= 384)
        {
            c.I32();
            c.I32();
        }

        if (pk.Ue4 >= 510)
        {
            c.I32();
        }

        pk.ThumbnailOffset = c.I32();
        pk.PackageGuid = c.Guid();
        var generations = c.I32();
        if (generations is < 0 or > 4096)
        {
            throw new ModelFormatException("The generation table is out of range.");
        }

        for (var i = 0; i < generations; i++)
        {
            c.I32();
            c.I32();
        }

        if (pk.Ue4 >= 336)
        {
            var (major, minor, patch, changelist, branch) = EngineVersion(ref c);
            pk.SavedBy = $"{major}.{minor}.{patch}-{changelist & 0x7FFFFFFF}";
            pk.Branch = branch;
        }
        else
        {
            c.I32();
        }

        try
        {
            ReadSummaryTail(ref c, pk);
        }
        catch (ModelFormatException)
        {
            // The tail only locates bulk data; a layout this reader does not know leaves that unknown.
        }

        CheckTables(d, pk, options);
        pk.Names = ReadNames(d, pk, options);
        pk.Imports = ReadImports(d, pk, options);
        pk.Exports = ReadExports(d, pk, options);
        return pk;
    }

    private static void ReadSummaryTail(ref Cursor c, Package pk)
    {
        if (pk.Ue5 > 0)
        {
            return;
        }

        if (pk.Ue4 >= 444)
        {
            EngineVersion(ref c);
        }

        c.U32();
        var chunks = c.I32();
        if (chunks is < 0 or > 4096)
        {
            return;
        }

        c.P += chunks * 16;
        c.U32();
        var extra = c.I32();
        if (extra is < 0 or > 4096)
        {
            return;
        }

        for (var i = 0; i < extra; i++)
        {
            c.Str();
        }

        if (pk.Legacy > -7)
        {
            c.I32();
        }

        pk.AssetRegistryOffset = c.I32();
        pk.BulkDataStart = c.I64();
    }

    private static (int, int, int, uint, string) EngineVersion(ref Cursor c)
    {
        int major = c.U16(), minor = c.U16(), patch = c.U16();
        var changelist = c.U32();
        return (major, minor, patch, changelist, c.Str());
    }

    private static void CheckTables(ReadOnlySpan<byte> d, Package pk, LoadOptions options)
    {
        if (pk.NameCount is < 0 or > MaxTable || pk.ImportCount is < 0 or > MaxTable || pk.ExportCount is < 0 or > MaxTable
            || pk.NameOffset < 0 || pk.NameOffset > d.Length || pk.ImportOffset < 0 || pk.ImportOffset > d.Length || pk.ExportOffset < 0 || pk.ExportOffset > d.Length)
        {
            throw new ModelFormatException("A package table is out of range.");
        }
    }

    private static List<string> ReadNames(ReadOnlySpan<byte> d, Package pk, LoadOptions options)
    {
        var list = new List<string>(Math.Min(pk.NameCount, 1 << 16));
        var c = new Cursor(d, pk.NameOffset);
        for (var i = 0; i < pk.NameCount; i++)
        {
            Cancel.Every(options.Cancellation, i);
            list.Add(c.Str());
            if (pk.Ue4 >= 504)
            {
                c.U16();
                c.U16();
            }
        }

        return list;
    }

    private static string Name(Package pk, int index, int number)
    {
        var name = index >= 0 && index < pk.Names.Count ? pk.Names[index] : "<name " + index.ToString(CultureInfo.InvariantCulture) + ">";
        return number > 0 ? name + "_" + (number - 1).ToString(CultureInfo.InvariantCulture) : name;
    }

    private static int NextOffsetAfter(Package pk, int start, long fileLength)
    {
        var best = (long)(pk.TotalHeaderSize > start ? pk.TotalHeaderSize : fileLength);
        foreach (var o in new[] { pk.NameOffset, pk.ImportOffset, pk.ExportOffset, pk.DependsOffset, pk.ThumbnailOffset, pk.AssetRegistryOffset })
        {
            if (o > start && o < best)
            {
                best = o;
            }
        }

        return (int)best;
    }

    private static List<Import> ReadImports(ReadOnlySpan<byte> d, Package pk, LoadOptions options)
    {
        var list = new List<Import>(Math.Min(pk.ImportCount, 1 << 16));
        var c = new Cursor(d, pk.ImportOffset);
        var span = NextOffsetAfter(pk, pk.ImportOffset, d.Length) - pk.ImportOffset;
        var stride = pk.ImportCount > 0 && span > 0 ? span / pk.ImportCount : 28;
        if (stride < 28)
        {
            stride = 28;
        }

        for (var i = 0; i < pk.ImportCount; i++)
        {
            Cancel.Every(options.Cancellation, i);
            c.P = pk.ImportOffset + i * stride;
            var pkgIdx = c.I32();
            var pkgNum = c.I32();
            var clsIdx = c.I32();
            var clsNum = c.I32();
            var outer = c.I32();
            var nameIdx = c.I32();
            var nameNum = c.I32();
            list.Add(new Import(Name(pk, pkgIdx, pkgNum), Name(pk, clsIdx, clsNum), outer, Name(pk, nameIdx, nameNum)));
        }

        return list;
    }

    private static List<Export> ReadExports(ReadOnlySpan<byte> d, Package pk, LoadOptions options)
    {
        var list = new List<Export>(Math.Min(pk.ExportCount, 1 << 16));
        if (pk.ExportCount == 0)
        {
            return list;
        }

        var span = NextOffsetAfter(pk, pk.ExportOffset, d.Length) - pk.ExportOffset;
        var stride = span / pk.ExportCount;
        // 32-bit serial sizes (older engines) make a 68-byte record; 64-bit sizes and the template index grow it to 72 and beyond.
        var wide = pk.Ue4 >= 507 || stride >= 72;
        var template = pk.Ue4 >= 508;
        if (stride < 40)
        {
            throw new ModelFormatException("The export table is inconsistent.");
        }

        for (var i = 0; i < pk.ExportCount; i++)
        {
            Cancel.Every(options.Cancellation, i);
            var c = new Cursor(d, pk.ExportOffset + i * stride);
            var cls = c.I32();
            var super = c.I32();
            if (template)
            {
                c.I32();
            }

            var outer = c.I32();
            var nameIdx = c.I32();
            var nameNum = c.I32();
            var flags = c.U32();
            long size, offset;
            if (wide)
            {
                size = c.I64();
                offset = c.I64();
            }
            else
            {
                size = c.I32();
                offset = c.I32();
            }

            list.Add(new Export(cls, super, outer, Name(pk, nameIdx, nameNum), flags, size, offset));
        }

        return list;
    }

    /// <summary>The object an index names: positive is an export (1-based), negative an import (-1-based), zero none.</summary>
    internal static string Resolve(Package pk, int index) =>
        index > 0 && index <= pk.Exports.Count ? pk.Exports[index - 1].Name
        : index < 0 && -index <= pk.Imports.Count ? pk.Imports[-index - 1].Name
        : "";

    internal static string ClassOf(Package pk, Export e) =>
        e.Class < 0 && -e.Class <= pk.Imports.Count ? pk.Imports[-e.Class - 1].Name : e.Class > 0 ? Resolve(pk, e.Class) : "Class";

    private static string PathOf(Package pk, int index, int depth = 0)
    {
        if (index == 0 || depth > 32)
        {
            return "";
        }

        var outer = index > 0 && index <= pk.Exports.Count ? pk.Exports[index - 1].Outer : index < 0 && -index <= pk.Imports.Count ? pk.Imports[-index - 1].Outer : 0;
        var parent = PathOf(pk, outer, depth + 1);
        var name = Resolve(pk, index);
        return parent.Length == 0 ? name : parent + "." + name;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options, bool requireGeometry = false)
    {
        var d = data.Span;
        var pk = ReadPackage(d, options);
        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Unreal;
        meta.DocumentKind = "scene";
        meta.FormatVersion = pk.Legacy <= -8 ? $"UE5 file version {pk.Ue5} (UE4 {pk.Ue4})" : $"UE4 file version {pk.Ue4}";
        meta.Application = "Unreal Engine " + (pk.SavedBy.Length > 0 ? pk.SavedBy : EngineName(pk.Ue4));
        meta.Title = pk.Folder.Length > 0 && pk.Folder != "None" ? pk.Folder : null;
        AddProp(meta, "Package flags", "0x" + pk.Flags.ToString("X8", CultureInfo.InvariantCulture));
        AddProp(meta, "Cooked", (pk.Flags & 0x00000200u) != 0 ? "yes" : "no");
        AddProp(meta, "Editor data", pk.FilterEditorOnly ? "stripped" : "present");
        AddProp(meta, "Package GUID", pk.PackageGuid.ToString());
        AddProp(meta, "Branch", pk.Branch);
        AddProp(meta, "Names", pk.Names.Count.ToString(CultureInfo.InvariantCulture));
        AddProp(meta, "Imports", pk.Imports.Count.ToString(CultureInfo.InvariantCulture));
        AddProp(meta, "Exports", pk.Exports.Count.ToString(CultureInfo.InvariantCulture));

        var classCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var import in pk.Imports)
        {
            if (import.ClassName == "Package" && !meta.ComponentFiles.Contains(import.Name, StringComparer.Ordinal))
            {
                meta.ComponentFiles.Add(import.Name);
            }
        }

        var tick = 0L;
        for (var i = 0; i < pk.Exports.Count; i++)
        {
            Cancel.Every(options.Cancellation, tick++);
            var e = pk.Exports[i];
            var cls = ClassOf(pk, e);
            classCounts[cls] = classCounts.GetValueOrDefault(cls) + 1;
            var node = new SceneNode(e.Name);
            node.Properties["class"] = cls;
            node.Properties["path"] = PathOf(pk, i + 1);
            node.Properties["serialSize"] = e.Size.ToString(CultureInfo.InvariantCulture);
            if (e.Super != 0)
            {
                node.Properties["super"] = Resolve(pk, e.Super);
            }

            ReadProperties(d, pk, e, node, options);
            if (cls == "StaticMesh" && RawMeshes.TryRead(d, pk, e, options) is { } raw)
            {
                AddMesh(model, pk, node, raw);
            }

            if (e.Outer == 0)
            {
                model.Roots.Add(node);
                meta.PartNames.Add(e.Name);
            }
        }

        foreach (var (cls, count) in classCounts)
        {
            AddProp(meta, cls, count.ToString(CultureInfo.InvariantCulture), "export classes");
        }

        if (pk.ThumbnailOffset > 0 && ReadThumbnail(d, pk) is { } thumb)
        {
            model.Preview = thumb;
            meta.HasPreview = true;
        }

        foreach (var (cls, count) in classCounts)
        {
            if (cls == "StaticMesh")
            {
                meta.Notes.Add($"{count} StaticMesh export(s); geometry is read from the editor's raw mesh bulk data when the package holds it (not from cooked render data).");
            }
        }

        meta.HasGeometry = model.Meshes.Count > 0;
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        meta.Units = LengthUnit.Centimeter;
        meta.UpAxis = UpAxis.Z;
        if (model.Meshes.Count == 0)
        {
            meta.Notes.Add("No mesh geometry was found: cooked render data and later engine layouts are not decoded.");
            if (requireGeometry)
            {
                throw new UnsupportedModelFormatException(ModelFormats.Unreal, "The package holds no editor raw mesh (cooked render data is not decoded); use ReadMetadata for its tables and properties.");
            }
        }
        return model;
    }

    private static void AddMesh(Model3D model, Package pk, SceneNode node, RawMeshes.Raw raw)
    {
        var faces = raw.FaceMaterial.Length;
        var wedges = faces * 3;
        var materialIds = raw.FaceMaterial.Distinct().Order().ToArray();
        var importedMaterials = pk.Imports.Where(i => i.ClassName.StartsWith("Material", StringComparison.Ordinal)).Select(i => i.Name).ToList();
        var positions = new float[wedges * 3];
        var normals = raw.Normals.Length == wedges * 3 ? new float[wedges * 3] : [];
        var uvs = raw.Uvs.Length == wedges * 2 ? new float[wedges * 2] : [];
        var indices = new uint[wedges];
        var mesh = new MeshData { Name = node.Name };
        var cursor = 0;
        foreach (var material in materialIds)
        {
            var start = cursor;
            for (var f = 0; f < faces; f++)
            {
                if (raw.FaceMaterial[f] != material)
                {
                    continue;
                }

                // Unreal is left-handed (x forward, y right, z up) with clockwise front faces: mirroring y gives a right-handed mesh whose front faces are counter-clockwise.
                for (var k = 0; k < 3; k++)
                {
                    var src = f * 3 + k;
                    var v = (int)raw.Wedges[src];
                    var o = cursor * 3;
                    positions[o] = raw.Positions[v * 3];
                    positions[o + 1] = -raw.Positions[v * 3 + 1];
                    positions[o + 2] = raw.Positions[v * 3 + 2];
                    if (normals.Length > 0)
                    {
                        normals[o] = raw.Normals[src * 3];
                        normals[o + 1] = -raw.Normals[src * 3 + 1];
                        normals[o + 2] = raw.Normals[src * 3 + 2];
                    }

                    if (uvs.Length > 0)
                    {
                        uvs[cursor * 2] = raw.Uvs[src * 2];
                        uvs[cursor * 2 + 1] = raw.Uvs[src * 2 + 1];
                    }

                    indices[cursor] = (uint)cursor;
                    cursor++;
                }
            }

            var name = materialIds.Length == 1 && importedMaterials.Count == 1 ? importedMaterials[0] : "Material " + material.ToString(CultureInfo.InvariantCulture);
            model.Materials.Add(new ModelMaterial { Name = name, Roughness = 0.7f });
            mesh.Groups.Add(new MeshGroup(start, cursor - start, model.Materials.Count - 1));
        }

        mesh.Positions = positions;
        mesh.Normals = normals;
        mesh.TexCoords = uvs;
        mesh.Indices = indices;
        model.Meshes.Add(mesh);
        node.Mesh = mesh;
        node.Properties["vertices"] = raw.Positions.Length / 3 + " (" + wedges + " wedges)";
        node.Properties["triangles"] = faces.ToString(CultureInfo.InvariantCulture);
    }

    private static string EngineName(int ue4) => ue4 switch
    {
        >= 522 => "4.2x",
        >= 500 => "4.1x",
        >= 451 => "4.8",
        _ => "4.x (file version " + ue4.ToString(CultureInfo.InvariantCulture) + ")",
    };

    private static void AddProp(ModelMetadata meta, string name, string value, string? group = null)
    {
        if (value.Length > 0)
        {
            meta.Properties.Add(new ModelProperty(name, value, group));
        }
    }

    private static PreviewImage? ReadThumbnail(ReadOnlySpan<byte> d, Package pk)
    {
        try
        {
            var c = new Cursor(d, pk.ThumbnailOffset);
            var count = c.I32();
            if (count is <= 0 or > 4096)
            {
                return null;
            }

            var offset = -1;
            for (var i = 0; i < count; i++)
            {
                c.Str();
                c.Str();
                var o = c.I32();
                if (offset < 0)
                {
                    offset = o;
                }
            }

            if (offset <= 0 || offset + 12 > d.Length)
            {
                return null;
            }

            c = new Cursor(d, offset);
            var w = c.I32();
            var h = c.I32();
            var size = c.I32();
            if (w <= 0 || h <= 0 || size <= 8 || c.P + size > d.Length)
            {
                return null;
            }

            var body = d.Slice(c.P, size);
            if (body[0] == 0x89 && body[1] == 'P')
            {
                return new PreviewImage("image/png", body.ToArray());
            }

            if (body[0] == 0xFF && body[1] == 0xD8)
            {
                return new PreviewImage("image/jpeg", body.ToArray());
            }
        }
        catch (ModelFormatException)
        {
        }

        return null;
    }

    private static void ReadProperties(ReadOnlySpan<byte> d, Package pk, Export e, SceneNode node, LoadOptions options)
    {
        if (e.Offset <= 0 || e.Size <= 0 || e.Offset + e.Size > d.Length || e.Size > int.MaxValue)
        {
            return;
        }

        var end = (int)(e.Offset + e.Size);
        var c = new Cursor(d[..end], (int)e.Offset);
        try
        {
            // Only the objects serialised as plain property lists start with one; a stack-frame or component prefix would derail it, and then the list stops.
            for (var guard = 0; guard < 4096; guard++)
            {
                Cancel.Every(options.Cancellation, guard);
                var nameIdx = c.I32();
                var nameNum = c.I32();
                var name = Name(pk, nameIdx, nameNum);
                if (name == "None")
                {
                    return;
                }

                if (nameIdx < 0 || nameIdx >= pk.Names.Count)
                {
                    return;
                }

                var typeIdx = c.I32();
                var typeNum = c.I32();
                var type = Name(pk, typeIdx, typeNum);
                var size = c.I32();
                var arrayIndex = c.I32();
                if (size < 0 || typeIdx < 0 || typeIdx >= pk.Names.Count)
                {
                    return;
                }

                string? extra = null;
                var boolValue = false;
                switch (type)
                {
                    case "StructProperty":
                        extra = Name(pk, c.I32(), c.I32());
                        if (pk.Ue4 >= 441)
                        {
                            c.Guid();
                        }

                        break;
                    case "BoolProperty":
                        boolValue = c.U8() != 0;
                        break;
                    case "ByteProperty":
                    case "EnumProperty":
                        extra = Name(pk, c.I32(), c.I32());
                        break;
                    case "ArrayProperty":
                    case "SetProperty":
                        if (pk.Ue4 >= 282)
                        {
                            extra = Name(pk, c.I32(), c.I32());
                        }

                        break;
                    case "MapProperty":
                        extra = Name(pk, c.I32(), c.I32()) + "," + Name(pk, c.I32(), c.I32());
                        break;
                }

                if (pk.Ue4 >= 282 && c.U8() != 0)
                {
                    c.Guid();
                }

                var valueStart = c.P;
                if (valueStart + size > end)
                {
                    return;
                }

                string value;
                switch (type)
                {
                    case "IntProperty": value = size == 4 ? BinaryPrimitives.ReadInt32LittleEndian(d[valueStart..]).ToString(CultureInfo.InvariantCulture) : ""; break;
                    case "UInt32Property": value = size == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(d[valueStart..]).ToString(CultureInfo.InvariantCulture) : ""; break;
                    case "Int64Property": value = size == 8 ? BinaryPrimitives.ReadInt64LittleEndian(d[valueStart..]).ToString(CultureInfo.InvariantCulture) : ""; break;
                    case "FloatProperty": value = size == 4 ? BinaryPrimitives.ReadSingleLittleEndian(d[valueStart..]).ToString("R", CultureInfo.InvariantCulture) : ""; break;
                    case "DoubleProperty": value = size == 8 ? BinaryPrimitives.ReadDoubleLittleEndian(d[valueStart..]).ToString("R", CultureInfo.InvariantCulture) : ""; break;
                    case "BoolProperty": value = boolValue ? "true" : "false"; break;
                    case "NameProperty": value = size == 8 ? Name(pk, BinaryPrimitives.ReadInt32LittleEndian(d[valueStart..]), BinaryPrimitives.ReadInt32LittleEndian(d[(valueStart + 4)..])) : ""; break;
                    case "StrProperty":
                    {
                        var s = new Cursor(d[..(valueStart + size)], valueStart);
                        value = s.Str();
                        break;
                    }

                    case "ObjectProperty": value = size == 4 ? Resolve(pk, BinaryPrimitives.ReadInt32LittleEndian(d[valueStart..])) : ""; break;
                    case "ByteProperty": value = size == 1 ? d[valueStart].ToString(CultureInfo.InvariantCulture) : size == 8 ? Name(pk, BinaryPrimitives.ReadInt32LittleEndian(d[valueStart..]), BinaryPrimitives.ReadInt32LittleEndian(d[(valueStart + 4)..])) : ""; break;
                    case "EnumProperty": value = size == 8 ? Name(pk, BinaryPrimitives.ReadInt32LittleEndian(d[valueStart..]), BinaryPrimitives.ReadInt32LittleEndian(d[(valueStart + 4)..])) : ""; break;
                    default: value = "(" + type + (extra is null ? "" : " " + extra) + ", " + size.ToString(CultureInfo.InvariantCulture) + " bytes)"; break;
                }

                var key = arrayIndex > 0 ? name + "[" + arrayIndex.ToString(CultureInfo.InvariantCulture) + "]" : name;
                if (value.Length > 0 && node.Properties.Count < 512)
                {
                    node.Properties["prop." + key] = value;
                }

                c.P = valueStart + size;
            }
        }
        catch (ModelFormatException)
        {
        }
    }
}
