using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>Additive Manufacturing File Format (.amf, plain or zipped XML): objects with meshes, volumes with materials and colours, constellations of instances, units.</summary>
internal static class AmfReader
{
    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        XDocument document;
        var span = data.Span;
        Stream stream;
        if (span.Length > 2 && span[0] == 'P' && span[1] == 'K')
        {
            using var zip = new ZipArchive(new MemoryStream(data.ToArray(), writable: false), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".amf", StringComparison.OrdinalIgnoreCase)) ?? throw new ModelFormatException("The zipped AMF has no .amf entry.");
            if (entry.Length > 1L << 30)
            {
                throw new ModelFormatException("The AMF entry is too large.");
            }

            var copy = new MemoryStream();
            using (var inner = entry.Open())
            {
                inner.CopyTo(copy);
            }

            copy.Position = 0;
            stream = copy;
        }
        else
        {
            stream = new MemoryStream(data.ToArray(), writable: false);
        }

        using (stream)
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            document = XDocument.Load(reader);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "amf")
        {
            throw new ModelFormatException("Not an AMF document.");
        }

        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Amf;
        meta.DocumentKind = "mesh";
        meta.UpAxis = UpAxis.Z;
        meta.Units = ((string?)root.Attribute("unit")) switch
        {
            "inch" => LengthUnit.Inch,
            "meter" => LengthUnit.Meter,
            "micron" => LengthUnit.Micrometer,
            _ => LengthUnit.Millimeter,
        };
        if ((string?)root.Attribute("version") is { } version)
        {
            meta.FormatVersion = version;
        }

        foreach (var m in root.Elements().Where(e => e.Name.LocalName == "metadata"))
        {
            var type = (string?)m.Attribute("type") ?? "";
            if (type.Length > 0)
            {
                meta.Properties.Add(new ModelProperty(type, m.Value, "amf"));
            }

            if (type == "name")
            {
                meta.Title = m.Value;
            }
        }

        var materials = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var material in root.Elements().Where(e => e.Name.LocalName == "material"))
        {
            var id = (string?)material.Attribute("id") ?? "";
            var color = material.Elements().FirstOrDefault(e => e.Name.LocalName == "color");
            var name = material.Elements().Where(e => e.Name.LocalName == "metadata" && (string?)e.Attribute("type") == "name").Select(e => e.Value).FirstOrDefault();
            var m = new ModelMaterial { Name = name ?? "material " + id };
            if (color is not null)
            {
                float C(string n) => float.TryParse(color.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1f;
                m.BaseColor = new Vector4(C("r"), C("g"), C("b"), C("a"));
            }

            materials[id] = model.Materials.Count;
            model.Materials.Add(m);
            if (name is not null)
            {
                meta.MaterialNames.Add(name);
            }
        }

        var objects = new Dictionary<string, (MeshData Mesh, string? Name)>(StringComparer.Ordinal);
        long triangles = 0;
        foreach (var obj in root.Elements().Where(e => e.Name.LocalName == "object"))
        {
            options.Cancellation.ThrowIfCancellationRequested();
            var id = (string?)obj.Attribute("id") ?? "";
            var meshElement = obj.Elements().FirstOrDefault(e => e.Name.LocalName == "mesh");
            if (meshElement is null)
            {
                continue;
            }

            var vertices = meshElement.Elements().First(e => e.Name.LocalName == "vertices").Elements().Select(v =>
            {
                var c = v.Elements().First(e => e.Name.LocalName == "coordinates");
                float X(string n) => float.Parse(c.Elements().First(e => e.Name.LocalName == n).Value, CultureInfo.InvariantCulture);
                return new Vector3(X("x"), X("y"), X("z"));
            }).ToList();
            var mesh = new MeshData { Positions = vertices.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray() };
            var indices = new List<uint>();
            foreach (var volume in meshElement.Elements().Where(e => e.Name.LocalName == "volume"))
            {
                var start = indices.Count;
                foreach (var t in volume.Elements().Where(e => e.Name.LocalName == "triangle"))
                {
                    int V(string n) => int.Parse(t.Elements().First(e => e.Name.LocalName == n).Value, CultureInfo.InvariantCulture);
                    int a = V("v1"), b = V("v2"), c = V("v3");
                    if (a < 0 || b < 0 || c < 0 || a >= vertices.Count || b >= vertices.Count || c >= vertices.Count)
                    {
                        throw new ModelFormatException("An AMF triangle refers to a vertex that does not exist.");
                    }

                    indices.AddRange([(uint)a, (uint)b, (uint)c]);
                    if (++triangles > options.MaxTriangles)
                    {
                        throw new ModelFormatException("The AMF has more triangles than the configured limit.");
                    }
                }

                var materialId = (string?)volume.Attribute("materialid");
                mesh.Groups.Add(new MeshGroup(start, indices.Count - start, materialId is not null && materials.TryGetValue(materialId, out var mi) ? mi : -1));
            }

            mesh.Indices = indices.ToArray();
            if (mesh.Groups.Count == 1 && mesh.Groups[0].Material < 0)
            {
                mesh.Groups.Clear();
            }

            var name = obj.Elements().Where(e => e.Name.LocalName == "metadata" && (string?)e.Attribute("type") == "name").Select(e => e.Value).FirstOrDefault();
            mesh.Name = name;
            model.Meshes.Add(mesh);
            objects[id] = (mesh, name);
        }

        var instances = root.Elements().Where(e => e.Name.LocalName == "constellation").SelectMany(c => c.Elements().Where(e => e.Name.LocalName == "instance")).ToList();
        if (instances.Count > 0)
        {
            foreach (var instance in instances)
            {
                var id = (string?)instance.Attribute("objectid") ?? "";
                if (!objects.TryGetValue(id, out var obj))
                {
                    continue;
                }

                float N(string n) => float.TryParse(instance.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
                var transform = Matrix4x4.CreateRotationX(N("rx") * MathF.PI / 180) * Matrix4x4.CreateRotationY(N("ry") * MathF.PI / 180) * Matrix4x4.CreateRotationZ(N("rz") * MathF.PI / 180)
                    * Matrix4x4.CreateTranslation(N("deltax"), N("deltay"), N("deltaz"));
                model.Roots.Add(new SceneNode(obj.Name) { Mesh = obj.Mesh, Transform = transform });
            }
        }
        else
        {
            foreach (var (mesh, name) in objects.Values)
            {
                model.Roots.Add(new SceneNode(name) { Mesh = mesh });
            }
        }

        if (model.Meshes.Count == 0)
        {
            throw new ModelFormatException("The AMF holds no meshes.");
        }

        meta.PartNames.AddRange(model.Meshes.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n))!);
        meta.HasGeometry = true;
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        return model;
    }
}
