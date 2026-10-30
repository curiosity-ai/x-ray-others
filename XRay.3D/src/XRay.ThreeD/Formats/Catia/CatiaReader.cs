using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Catia;

using XRay.ThreeD.Internal;

/// <summary>
/// CATIA V5 documents (<c>.CATPart</c>, <c>.CATProduct</c>, <c>.CATDrawing</c>, <c>.CATShape</c>, <c>.cgr</c>), read from the format description in docs/catia-format.md (container and
/// B-rep notes from the public cadmpeg descriptions, CC-BY-4.0; the rest from the sample files). Metadata (save version, product and part names, referenced documents, preview image) is
/// read from the container's property and catalog streams. Geometry is the display tessellation the part carries in its graphical-representation cache; the exact B-rep, the feature tree,
/// instance positions of assemblies and parameter values are not decoded.
/// </summary>
internal static class CatiaReader
{
    private const long StreamLimit = 1L << 29;

    private static readonly string[] ContainerNames =
    [
        "CATPrtCont", "CATProdCont", "CATMechProdCont", "CATFeatCont", "CATDrwCont", "CATStdCont", "CATCGRCont", "CATMaterialCont", "CATBRepModeContainer", "CATSeeBodyCont",
        "CGMGeom", "CATMFBRP", "CATCtorProxyCont", "CATSelSetsCont", "CATXDocLinkCont", "CATCameraContainer", "CATTTRSCont", "CameraStartupContainer", "CATCompoundCont",
    ];

    public static bool Sniff(ReadOnlySpan<byte> head) => CatiaContainer.Sniff(head);

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var token = options.Cancellation;
        var container = Open(data, token);
        var model = new Model3D();
        var info = ReadInfo(container, model.Metadata, options);

        long vertices = 0;
        var bodies = new List<CatiaTessellation.Body>();
        foreach (var stream in container.Streams)
        {
            token.ThrowIfCancellationRequested();
            if (stream.Name != "MainDataStream" || stream.Length < 64 || container.Read(stream, StreamLimit) is not { } bytes)
            {
                continue;
            }

            foreach (var body in CatiaTessellation.Decode(bytes, options))
            {
                bodies.Add(body);
                vertices += body.Positions.Length / 3;
            }
        }

        var meta = model.Metadata;
        if (bodies.Count == 0)
        {
            meta.HasGeometry = false;
            throw new UnsupportedModelFormatException(ModelFormats.Catia, NoGeometry(meta));
        }

        var root = new SceneNode(info.ProductName ?? meta.Title ?? "CATIA part");
        var index = 0;
        foreach (var body in bodies)
        {
            token.ThrowIfCancellationRequested();
            var mesh = BuildMesh(body, model, bodies.Count == 1 ? root.Name : "Body " + (++index).ToString(CultureInfo.InvariantCulture), options);
            model.Meshes.Add(mesh);
            if (bodies.Count == 1)
            {
                root.Mesh = mesh;
            }
            else
            {
                root.Children.Add(new SceneNode(mesh.Name) { Mesh = mesh });
            }
        }

        model.Roots.Add(root);
        model.Preview = info.Preview;
        meta.HasGeometry = true;
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        meta.Notes.Add("The geometry is the display tessellation stored with the part; the exact B-rep is not decoded.");
        return model;
    }

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var container = Open(data, options.Cancellation);
        var meta = new ModelMetadata();
        ReadInfo(container, meta, options);
        var count = 0;
        foreach (var stream in container.Streams)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            if (stream.Name != "MainDataStream" || stream.Length < 64 || container.Read(stream, StreamLimit) is not { } bytes)
            {
                continue;
            }

            count += CatiaTessellation.Decode(bytes, options).Count;
        }

        meta.HasGeometry = count > 0;
        if (count == 0)
        {
            meta.Notes.Add(NoGeometry(meta));
        }

        return meta;
    }

    public static PreviewImage? ReadPreview(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var container = Open(data, options.Cancellation);
        return FindPreview(container, options.Cancellation)?.Image;
    }

    private static string NoGeometry(ModelMetadata meta) => meta.DocumentKind switch
    {
        "assembly" => "A CATIA product stores its structure only; the geometry is in the referenced part files, and the instance positions are not decoded.",
        "drawing" => "A CATIA drawing holds no 3D geometry; the preview image is available.",
        _ => "No display tessellation was found; the exact B-rep that such a document stores is not decoded.",
    };

    private static CatiaContainer Open(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (data.Length > 0 && !CatiaContainer.Sniff(data.Span))
        {
            throw new ModelFormatException("Not a CATIA V5 file: the V5_CFV2 header is missing.");
        }

        return CatiaContainer.TryOpen(data, token) ?? throw new ModelFormatException("The CATIA V5 container is damaged: its stream directory could not be read.");
    }

    private sealed record Info(string? ProductName, PreviewImage? Preview);

    private sealed record Preview(PreviewImage Image, int Width, int Height);

    private static Info ReadInfo(CatiaContainer container, ModelMetadata meta, LoadOptions options)
    {
        var token = options.Cancellation;
        meta.Format = ModelFormats.Catia;
        meta.Units = LengthUnit.Millimeter;
        meta.UpAxis = UpAxis.Z;
        var classes = new HashSet<string>(StringComparer.Ordinal);
        string? productName = null;
        string? partName = null;
        string? original = null;
        var components = new List<string>();
        var instances = new List<string>();
        var attributes = new List<(string Name, string Value)>();
        var streams = 0;
        foreach (var stream in container.Streams)
        {
            token.ThrowIfCancellationRequested();
            if (stream.Nested)
            {
                continue;
            }

            streams++;
            if (stream.Length > StreamLimit || stream.Length == 0)
            {
                continue;
            }

            var name = stream.Name;
            var isProperties = name == "CATPropertySets";
            var isDocument = name == "CATDocumentProperty";
            var isStorage = name == "CATStorageProperty";
            var isCatalog = name == "Data" || name.Contains("Cont_", StringComparison.Ordinal);
            if (!isProperties && !isDocument && !isStorage && !isCatalog)
            {
                continue;
            }

            if (container.Read(stream, StreamLimit) is not { } bytes)
            {
                continue;
            }

            if (isProperties)
            {
                ReadPropertySet(bytes, meta, token);
            }
            else if (isDocument)
            {
                ReadLinks(bytes, ref original, components, token);
            }
            else
            {
                foreach (var cls in ContainerNames)
                {
                    if (bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(cls)) >= 0)
                    {
                        classes.Add(cls);
                    }
                }

                if (isCatalog)
                {
                    ReadCatalog(bytes, ref productName, ref partName, instances, attributes, token);
                }
            }
        }

        meta.DocumentKind = classes.Contains("CATPrtCont") ? "part" : classes.Contains("CATDrwCont") ? "drawing" : classes.Contains("CATMechProdCont") || classes.Contains("CATProdCont") ? "assembly" : "part";
        meta.Title = productName;
        if (productName is not null)
        {
            meta.Properties.Add(new ModelProperty("Product name", productName, "catalog"));
            if (meta.DocumentKind == "part")
            {
                meta.PartNames.Add(productName);
            }
        }

        if (partName is not null)
        {
            meta.Properties.Add(new ModelProperty("Part feature", partName, "catalog"));
        }

        foreach (var (attribute, value) in attributes)
        {
            meta.Properties.Add(new ModelProperty(attribute, value, "catalog"));
        }

        if (meta.DocumentKind == "assembly")
        {
            foreach (var instance in instances.Distinct(StringComparer.Ordinal))
            {
                meta.PartNames.Add(instance);
            }
        }

        if (original is not null)
        {
            meta.Properties.Add(new ModelProperty("Original path", original, "links"));
        }

        foreach (var file in components.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            meta.ComponentFiles.Add(file);
        }

        if (classes.Count > 0)
        {
            meta.Properties.Add(new ModelProperty("Containers", string.Join(", ", classes.Order(StringComparer.Ordinal)), "container"));
        }

        meta.Properties.Add(new ModelProperty("Streams", streams.ToString(CultureInfo.InvariantCulture), "container"));
        var preview = FindPreview(container, token);
        meta.HasPreview = preview is not null;
        if (preview is not null)
        {
            meta.Properties.Add(new ModelProperty("Preview size", preview.Width.ToString(CultureInfo.InvariantCulture) + " x " + preview.Height.ToString(CultureInfo.InvariantCulture), "preview"));
        }

        return new Info(productName, preview?.Image);
    }

    private static void ReadPropertySet(byte[] d, ModelMetadata meta, CancellationToken token)
    {
        // Entries are a u16 name length, the name, a u32 type and a payload; type 0x0e is a string (u32 length and bytes).
        for (var p = 0; p + 14 < d.Length; p++)
        {
            Cancel.Every(token, p);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p));
            if (length is < 3 or > 64 || p + 2 + length + 8 > d.Length || !IsIdentifier(d.AsSpan(p + 2, length)))
            {
                continue;
            }

            var at = p + 2 + length;
            if (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) != 0x0e)
            {
                continue;
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 4));
            if (size == 0 || size > 4096 || at + 8 + size > d.Length)
            {
                continue;
            }

            var name = Encoding.ASCII.GetString(d, p + 2, length);
            var value = Encoding.UTF8.GetString(d, at + 8, (int)size).TrimEnd('\0');
            if (value.Any(char.IsControl))
            {
                continue;
            }

            Apply(meta, name, value);
            p = at + 8 + (int)size - 1;
        }
    }

    private static void Apply(ModelMetadata meta, string name, string value)
    {
        switch (name)
        {
            case "LastSaveVersion":
                var version = Tag(value, "Version");
                var release = Tag(value, "Release");
                var servicePack = Tag(value, "ServicePack");
                var hotFix = Tag(value, "HotFix");
                var build = Tag(value, "BuildDate");
                if (version is not null && release is not null)
                {
                    meta.FormatVersion = "V" + version + "R" + release;
                    meta.Application = "CATIA " + meta.FormatVersion + (servicePack is { Length: > 0 } and not "0" ? " SP" + servicePack : "");
                }

                meta.Properties.Add(new ModelProperty(name, "V" + version + "R" + release + " SP" + (servicePack ?? "0") + " HF" + (hotFix ?? "0") + (build is null ? "" : " built " + build), "summary"));
                break;
            case "FirstStreamed":
                break;
            case "Title" when meta.Title is null:
                meta.Title = value;
                break;
            case "Author":
                meta.Author = value;
                meta.Properties.Add(new ModelProperty(name, value, "summary"));
                break;
            default:
                meta.Properties.Add(new ModelProperty(name, value, "summary"));
                break;
        }
    }

    /// <summary>The value of <c>&lt;Name&gt;value/&lt;Name&gt;</c>.</summary>
    private static string? Tag(string text, string name)
    {
        var open = "<" + name + ">";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        var end = text.IndexOf("/" + open, start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }

    private static bool IsIdentifier(ReadOnlySpan<byte> s)
    {
        if (!(s[0] is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!(c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_'))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly string[] DocumentTypes = ["CATPart", "CATProduct", "CATShape", "CATDrawing", "CATMaterial", "cgr", "STL", "stp", "step", "igs", "iges", "3dxml", "model", "wrl", "dxf", "dwg"];

    /// <summary>
    /// The document links: entries <c>04 "File" &lt;type&gt; &lt;u16 length&gt; &lt;path&gt;</c>. The first entry is the document's own path; the later ones that name another document
    /// (a part, a product, a mesh) are the files it refers to.
    /// </summary>
    private static void ReadLinks(byte[] stream, ref string? original, List<string> components, CancellationToken token)
    {
        // The links live in CATOctetArray values: 0x22, a u32 count, then every byte of the array as <c>3b 01 byte</c>.
        var first = true;
        for (var i = 0; i + 5 < stream.Length; i++)
        {
            Cancel.Every(token, i);
            if (stream[i] != 0x22)
            {
                continue;
            }

            var count = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(i + 1));
            if (count is < 8 or > (1 << 20) || i + 5 + 3L * count > stream.Length || stream[i + 5] != 0x3b || stream[i + 6] != 0x01)
            {
                continue;
            }

            var array = new byte[count];
            var ok = true;
            for (var k = 0; k < count; k++)
            {
                var at = i + 5 + 3 * k;
                if (stream[at] != 0x3b || stream[at + 1] != 0x01)
                {
                    ok = false;
                    break;
                }

                array[k] = stream[at + 2];
            }

            if (ok)
            {
                ReadLinkArray(array, ref original, components, ref first, token);
                i += 5 + 3 * (int)count - 1;
            }
        }
    }

    private static void ReadLinkArray(byte[] d, ref string? original, List<string> components, ref bool first, CancellationToken token)
    {
        ReadOnlySpan<byte> marker = [0x04, (byte)'F', (byte)'i', (byte)'l', (byte)'e'];
        var span = d.AsSpan();
        var from = 0;
        while (from < span.Length - 8)
        {
            token.ThrowIfCancellationRequested();
            var at = span[from..].IndexOf(marker);
            if (at < 0)
            {
                break;
            }

            var p = from + at + marker.Length;
            from = p;
            if (p >= span.Length)
            {
                break;
            }

            var typeLength = span[p++];
            if (typeLength is 0 or > 16 || p + typeLength + 2 > span.Length)
            {
                continue;
            }

            var type = Encoding.ASCII.GetString(span.Slice(p, typeLength));
            p += typeLength;
            var length = BinaryPrimitives.ReadUInt16LittleEndian(span[p..]);
            p += 2;
            if (length == 0 || length > 1024 || p + length > span.Length)
            {
                continue;
            }

            var path = Encoding.UTF8.GetString(span.Slice(p, length));
            if (path.Any(char.IsControl) || !DocumentTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (first)
            {
                original = path;
                first = false;
                continue;
            }

            components.Add(path[(path.LastIndexOfAny(['\\', '/']) + 1)..]);
        }
    }

    private static readonly string[] ValueAttributes = ["_PartNumber", "_Nomenclature", "_Revision", "_Source"];

    /// <summary>
    /// The identifier catalogs at the start of a container stream: length-prefixed strings (the length counts the byte itself). <c>ASMPRODUCT</c> is followed by the product's
    /// name, <c>MechanicalPart</c> by the part's, and the names between <c>_Reps</c> and <c>CONNECTOR</c> are the component and instance names the catalog meets first.
    /// </summary>
    private static void ReadCatalog(byte[] d, ref string? product, ref string? part, List<string> instances, List<(string, string)> attributes, CancellationToken token)
    {
        var span = d.AsSpan();
        var at = Find(span, "ASMPRODUCT");
        if (at >= 0)
        {
            var names = Strings(span, at, 80);
            if (names.Count > 1 && product is null)
            {
                product = names[1];
            }

            for (var i = 0; i + 1 < names.Count; i++)
            {
                if (Array.IndexOf(ValueAttributes, names[i]) >= 0 && !names[i + 1].StartsWith('_') && names[i + 1] is not ("PRDBAGREP" or "PRDREP" or "CONNECTOR" or "3DIC") && !attributes.Any(a => a.Item1 == names[i]))
                {
                    attributes.Add((names[i].TrimStart('_'), names[i + 1]));
                }
            }

            var reps = names.IndexOf("_Reps");
            if (reps >= 0)
            {
                for (var i = reps + 1; i < names.Count && names[i] != "CONNECTOR" && names[i] != "_ShapeConnector"; i++)
                {
                    if (names[i] is not ("_InstanceName" or "_Position" or "_ComponentActivationState" or "PRDREP" or "ASMGEOMETRY" or "Shape 1") && !names[i].StartsWith('_') && IsInstanceName(names[i]))
                    {
                        instances.Add(names[i]);
                    }
                }
            }
        }

        var mechanical = Find(span, "MechanicalPart");
        if (mechanical >= 0 && part is null)
        {
            var names = Strings(span, mechanical, 3);
            if (names.Count > 1)
            {
                part = names[1];
            }
        }
    }

    private static bool IsInstanceName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 && name[(dot + 1)..].All(char.IsAsciiDigit);
    }

    private static int Find(ReadOnlySpan<byte> d, string text)
    {
        var needle = new byte[text.Length + 1];
        needle[0] = (byte)(text.Length + 1);
        Encoding.ASCII.GetBytes(text, 0, text.Length, needle, 1);
        return d.IndexOf(needle);
    }

    private static List<string> Strings(ReadOnlySpan<byte> d, int at, int max)
    {
        var list = new List<string>();
        while (list.Count < max && at < d.Length)
        {
            var length = d[at];
            if (length < 2 || at + length > d.Length)
            {
                break;
            }

            var text = d.Slice(at + 1, length - 1);
            var ok = true;
            foreach (var c in text)
            {
                if (c is < 0x20 or >= 0x7f)
                {
                    ok = false;
                    break;
                }
            }

            if (!ok)
            {
                break;
            }

            list.Add(Encoding.ASCII.GetString(text));
            at += length;
        }

        return list;
    }

    private static Preview? FindPreview(CatiaContainer container, CancellationToken token)
    {
        foreach (var stream in container.Named("CATPreview", nested: false))
        {
            token.ThrowIfCancellationRequested();
            if (container.Read(stream, 1L << 26) is not { } bytes)
            {
                continue;
            }

            var soi = bytes.AsSpan().IndexOf<byte>([0xff, 0xd8, 0xff]);
            if (soi < 0)
            {
                continue;
            }

            if (JpegLength(bytes.AsSpan(soi), out var width, out var height) is var length and > 0)
            {
                return new Preview(new PreviewImage("image/jpeg", bytes.AsSpan(soi, length).ToArray()), width, height);
            }
        }

        return null;
    }

    /// <summary>The length of the JPEG that starts at the span (to its end-of-image marker), with its pixel size; 0 when it is not a complete baseline or progressive image.</summary>
    internal static int JpegLength(ReadOnlySpan<byte> s, out int width, out int height)
    {
        width = height = 0;
        var p = 2;
        while (p + 2 <= s.Length)
        {
            if (s[p] != 0xff)
            {
                return 0;
            }

            var marker = s[p + 1];
            if (marker == 0xff)
            {
                p++;
                continue;
            }

            if (marker == 0xd9)
            {
                return p + 2;
            }

            if (marker is 0x01 or (>= 0xd0 and <= 0xd7))
            {
                p += 2;
                continue;
            }

            if (p + 4 > s.Length)
            {
                return 0;
            }

            var length = (s[p + 2] << 8) | s[p + 3];
            if (length < 2 || p + 2 + length > s.Length)
            {
                return 0;
            }

            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc) && length >= 8)
            {
                height = (s[p + 5] << 8) | s[p + 6];
                width = (s[p + 7] << 8) | s[p + 8];
            }

            p += 2 + length;
            if (marker == 0xda)
            {
                // Entropy-coded data runs to the next marker that is not a stuffed zero or a restart.
                while (p + 1 < s.Length)
                {
                    if (s[p] == 0xff && s[p + 1] != 0 && !(s[p + 1] is >= 0xd0 and <= 0xd7))
                    {
                        break;
                    }

                    p++;
                }
            }
        }

        return 0;
    }

    private static MeshData BuildMesh(CatiaTessellation.Body body, Model3D model, string? name, LoadOptions options)
    {
        var token = options.Cancellation;
        var faces = body.Faces;
        var sourceCount = body.Positions.Length / 3;
        var totalIndices = body.Indices.Length;

        // Faces share vertices along their edges; give every face its own copies so that curved faces are smooth and edges between faces stay sharp.
        var stamp = new int[sourceCount];
        var local = new uint[sourceCount];
        var positions = new List<float>(Math.Min(totalIndices, 1 << 20) * 3);
        var indices = new uint[totalIndices];
        var meshFaces = new MeshFace[faces];
        var mesh = new MeshData { Name = name };
        var materials = new Dictionary<uint, int>();
        var lastMaterial = int.MinValue;
        var groupStart = 0;
        for (var f = 0; f < faces; f++)
        {
            Cancel.Every(token, f);
            var start = body.FaceStart[f];
            var end = body.FaceStart[f + 1];
            for (var i = start; i < end; i++)
            {
                var v = (int)body.Indices[i];
                if (stamp[v] != f + 1)
                {
                    stamp[v] = f + 1;
                    local[v] = (uint)(positions.Count / 3);
                    positions.Add(body.Positions[3 * v]);
                    positions.Add(body.Positions[3 * v + 1]);
                    positions.Add(body.Positions[3 * v + 2]);
                }

                indices[i] = local[v];
            }

            meshFaces[f] = new MeshFace(start, end - start);
            var material = -1;
            if (body.FaceColors.Length == faces)
            {
                var color = body.FaceColors[f];
                if (!materials.TryGetValue(color, out material))
                {
                    material = model.Materials.Count;
                    materials[color] = material;
                    model.Materials.Add(new ModelMaterial
                    {
                        Name = "#" + (color >> 8).ToString("X6", CultureInfo.InvariantCulture),
                        BaseColor = new Vector4((color >> 24) / 255f, ((color >> 16) & 0xff) / 255f, ((color >> 8) & 0xff) / 255f, (color & 0xff) / 255f),
                    });
                }
            }

            if (material != lastMaterial)
            {
                if (f > 0)
                {
                    mesh.Groups.Add(new MeshGroup(groupStart, start - groupStart, lastMaterial));
                }

                groupStart = start;
                lastMaterial = material;
            }
        }

        if (faces > 0)
        {
            mesh.Groups.Add(new MeshGroup(groupStart, totalIndices - groupStart, lastMaterial));
        }

        var p = positions.ToArray();
        var normals = new float[p.Length];
        for (var i = 0; i + 2 < totalIndices; i += 3)
        {
            var a = (int)indices[i] * 3;
            var b = (int)indices[i + 1] * 3;
            var c = (int)indices[i + 2] * 3;
            var e1 = new Vector3(p[b] - p[a], p[b + 1] - p[a + 1], p[b + 2] - p[a + 2]);
            var e2 = new Vector3(p[c] - p[a], p[c + 1] - p[a + 1], p[c + 2] - p[a + 2]);
            var n = Vector3.Cross(e1, e2);
            foreach (var corner in (ReadOnlySpan<int>)[a, b, c])
            {
                normals[corner] += n.X;
                normals[corner + 1] += n.Y;
                normals[corner + 2] += n.Z;
            }
        }

        for (var i = 0; i + 2 < normals.Length; i += 3)
        {
            var n = new Vector3(normals[i], normals[i + 1], normals[i + 2]);
            var length = n.Length();
            if (length > 0 && float.IsFinite(length))
            {
                n /= length;
            }
            else
            {
                n = Vector3.UnitZ;
            }

            normals[i] = n.X;
            normals[i + 1] = n.Y;
            normals[i + 2] = n.Z;
        }

        mesh.Positions = p;
        mesh.Normals = normals;
        mesh.Indices = indices;
        mesh.Faces = meshFaces;
        return mesh;
    }
}
