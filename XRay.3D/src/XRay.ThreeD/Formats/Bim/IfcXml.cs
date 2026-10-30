using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Bim;

/// <summary>
/// ifcXML (the XML encoding of IFC: <c>.ifcxml</c>) turned back into the STEP physical file text the IFC reader already reads, so both encodings share
/// the model builder and the geometry. The XML is a tree: simple attributes are XML attributes (lists space separated), entity-valued attributes are
/// child elements named after the attribute (the entity is the element itself, typed by <c>xsi:type</c>, or one level down under the name of its class for
/// selects), references are <c>href</c> to an <c>id</c>, and inverse attributes nest the relating entity under the element they mirror
/// (<c>IsDecomposedBy</c> holding an <c>IfcRelAggregates</c>), whose forward attribute is then implied. Attribute order, kinds, inverses, defined types
/// and selects come from tables of the EXPRESS schema (IFC2X3, IFC4, IFC4X3), embedded as compressed resources.
/// </summary>
internal static class IfcXml
{
    private const int MaxDepth = 96;
    private const int MaxEntities = 8_000_000;

    public static bool Sniff(ReadOnlySpan<byte> data)
    {
        var head = data[..Math.Min(data.Length, 4096)];
        var i = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            i = 3;
        }

        while (i < head.Length && head[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i < head.Length && head[i] == '<' && Encoding.ASCII.GetString(head).Contains("ifcXML", StringComparison.OrdinalIgnoreCase);
    }

    // ---- schema tables -------------------------------------------------------------------------------------------------

    internal sealed record Attr(string Name, bool Derived, int Depth, char Kind, string Target);

    internal sealed class EntityDef
    {
        public string Name = "";
        public string Super = "";
        public Attr[] Attrs = [];
        public Dictionary<string, (string Entity, string Attr)> Inverse = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class Schema
    {
        public string Name = "";
        public Dictionary<string, EntityDef> Entities = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (int Depth, char Kind)> Defined = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Enums = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> Selects = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, Schema> Cache = new(StringComparer.Ordinal);

    internal static Schema LoadSchema(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            using var resource = typeof(IfcXml).Assembly.GetManifestResourceStream("XRay.ThreeD.Schemas." + name.ToLowerInvariant() + ".txt")
                ?? throw new UnsupportedModelFormatException(ModelFormats.Ifc, "The ifcXML schema " + name + " is not available.");
            using var gzip = new GZipStream(resource, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.ASCII);
            var schema = new Schema { Name = name };
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var f = line.Split('|');
                switch (f[0])
                {
                    case "E":
                    {
                        var def = new EntityDef { Name = f[1], Super = f[2] };
                        def.Attrs = f[3].Length == 0 ? [] : f[3].Split(',').Select(a =>
                        {
                            var p = a.Split(':');
                            return new Attr(p[0], p[1] == "d", int.Parse(p[2], CultureInfo.InvariantCulture), p[3][0], p.Length > 4 ? p[4] : "");
                        }).ToArray();
                        if (f.Length > 4 && f[4].Length > 0)
                        {
                            foreach (var inverse in f[4].Split(','))
                            {
                                var p = inverse.Split(':');
                                var target = p[1].Split('.');
                                def.Inverse[p[0]] = (target[0], target[1]);
                            }
                        }

                        schema.Entities[f[1]] = def;
                        break;
                    }

                    case "D":
                        schema.Defined[f[1]] = (int.Parse(f[2], CultureInfo.InvariantCulture), f[3][0]);
                        break;
                    case "N":
                        schema.Enums.Add(f[1]);
                        break;
                    case "T":
                        schema.Selects[f[1]] = f[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
                        break;
                }
            }

            Cache[name] = schema;
            return schema;
        }
    }

    // ---- conversion ---------------------------------------------------------------------------------------------------------

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static string ToStep(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data.ToArray(), writable: false), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1L << 30 });
            document = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new ModelFormatException("The ifcXML is damaged: " + e.Message);
        }

        var root = document.Root ?? throw new ModelFormatException("The ifcXML is empty.");
        var ns = root.Name.NamespaceName;
        var schemaName = ns.Contains("IFC2x3", StringComparison.OrdinalIgnoreCase) ? "IFC2X3" : ns.Contains("IFC4x3", StringComparison.OrdinalIgnoreCase) || ns.Contains("IFC4_3", StringComparison.OrdinalIgnoreCase) ? "IFC4X3_ADD2" : "IFC4";
        var converter = new Converter(LoadSchema(schemaName), options);
        var container = root.Elements().FirstOrDefault(e => e.Name.LocalName == "uos") ?? root;
        foreach (var child in container.Elements())
        {
            if (child.Name.LocalName is "header" or "uos")
            {
                continue;
            }

            if (converter.Schema.Entities.ContainsKey(child.Name.LocalName) || child.Attribute(Xsi + "type") is not null)
            {
                converter.Root(child);
            }
        }

        return converter.Finish();
    }

    private sealed class Converter
    {
        private readonly LoadOptions _options;
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _lines = new();
        private readonly HashSet<XElement> _done = new(ReferenceEqualityComparer.Instance);
        private int _next;
        private long _tick;

        public Converter(Schema schema, LoadOptions options)
        {
            Schema = schema;
            _options = options;
        }

        public Schema Schema { get; }

        private int IdOf(string xmlId)
        {
            if (!_ids.TryGetValue(xmlId, out var id))
            {
                _ids[xmlId] = id = ++_next;
            }

            return id;
        }

        public void Root(XElement e) => Entity(e, null, null, 0);

        public string Finish()
        {
            var sb = new StringBuilder();
            sb.Append("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('ViewDefinition [ifcXML]'),'2;1');\nFILE_NAME('model.ifcxml','',(''),(''),'','','');\nFILE_SCHEMA(('")
                .Append(Schema.Name == "IFC4X3_ADD2" ? "IFC4X3" : Schema.Name).Append("'));\nENDSEC;\nDATA;\n");
            foreach (var id in _lines.Keys.Order())
            {
                sb.Append('#').Append(id.ToString(CultureInfo.InvariantCulture)).Append('=').Append(_lines[id]).Append(";\n");
            }

            sb.Append("ENDSEC;\nEND-ISO-10303-21;\n");
            return sb.ToString();
        }

        private EntityDef? DefOf(XElement e, string? declared)
        {
            if (Schema.Entities.TryGetValue(e.Name.LocalName, out var byName))
            {
                return byName;
            }

            if (e.Attribute(Xsi + "type")?.Value is { Length: > 0 } type)
            {
                var local = type.Contains(':') ? type[(type.IndexOf(':') + 1)..] : type;
                if (Schema.Entities.TryGetValue(local, out var byType))
                {
                    return byType;
                }
            }

            return declared is { Length: > 0 } && Schema.Entities.TryGetValue(declared, out var byDeclared) ? byDeclared : null;
        }

        private static string? HrefOf(XElement e) => (string?)e.Attribute("href") ?? (string?)e.Attribute("ref");

        /// <summary>Emits an entity element and returns its STEP reference, or "$" when the element cannot be read as an entity.</summary>
        private string Entity(XElement e, string? declared, (int Parent, string Attr)? link, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ModelFormatException("The ifcXML is nested too deeply.");
            }

            Cancel.Every(_options.Cancellation, _tick++);
            if (HrefOf(e) is { } href)
            {
                return "#" + IdOf(href).ToString(CultureInfo.InvariantCulture);
            }

            var def = DefOf(e, declared);
            if (def is null)
            {
                return "$";
            }

            var id = e.Attribute("id") is { } idAttr ? IdOf(idAttr.Value) : ++_next;
            if (!_done.Add(e))
            {
                return "#" + id.ToString(CultureInfo.InvariantCulture);
            }

            if (_lines.Count > MaxEntities)
            {
                throw new ModelFormatException("The ifcXML has too many entities.");
            }

            var values = new string[def.Attrs.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var a = def.Attrs[i];
                if (a.Derived)
                {
                    values[i] = "*";
                    continue;
                }

                string? value = null;
                if (e.Attribute(a.Name) is { } xmlAttribute)
                {
                    value = Scalar(a, xmlAttribute.Value);
                }
                else if (e.Elements().FirstOrDefault(c => c.Name.LocalName == a.Name) is { } child)
                {
                    value = Complex(a, child, depth + 1);
                }

                if (value is null && link is { } l && string.Equals(a.Name, l.Attr, StringComparison.OrdinalIgnoreCase))
                {
                    var reference = "#" + l.Parent.ToString(CultureInfo.InvariantCulture);
                    value = a.Depth > 0 ? "(" + reference + ")" : reference;
                }

                values[i] = value ?? "$";
            }

            _lines[id] = def.Name + "(" + string.Join(",", values) + ")";

            // Inverse attributes nest the relating entities under this one.
            foreach (var child in e.Elements())
            {
                if (def.Inverse.TryGetValue(child.Name.LocalName, out var inverse) && !def.Attrs.Any(a => a.Name == child.Name.LocalName))
                {
                    foreach (var related in child.Elements())
                    {
                        Entity(related, inverse.Entity, (id, inverse.Attr), depth + 1);
                    }
                }
            }

            return "#" + id.ToString(CultureInfo.InvariantCulture);
        }

        private string Complex(Attr a, XElement e, int depth)
        {
            switch (a.Kind)
            {
                case 'X':
                    if (a.Depth == 0)
                    {
                        return EntityValue(e, a.Target, depth);
                    }

                    if (HrefOf(e) is not null)
                    {
                        return "(" + Entity(e, a.Target, null, depth) + ")";
                    }

                    return "(" + string.Join(",", e.Elements().Select(c => Entity(c, a.Target, null, depth + 1)).Where(r => r != "$")) + ")";
                case 'T':
                    if (a.Depth == 0)
                    {
                        return SelectValue(e, a.Target, depth);
                    }

                    return "(" + string.Join(",", e.Elements().Select(c => SelectItem(c, a.Target, depth + 1)).Where(r => r != "$")) + ")";
                default:
                {
                    var items = e.Elements().Any() ? e.Elements().Select(c => c.Value.Trim()).ToList() : null;
                    return Scalar(a, items is null ? e.Value : string.Join(" ", items));
                }
            }
        }

        /// <summary>An entity-valued attribute element: a reference, the entity one level down (a select wrapper), or the element itself.</summary>
        private string EntityValue(XElement e, string target, int depth)
        {
            if (HrefOf(e) is not null)
            {
                return Entity(e, target, null, depth);
            }

            var inner = e.Elements().FirstOrDefault(c => Schema.Entities.ContainsKey(c.Name.LocalName));
            return inner is not null ? Entity(inner, target, null, depth + 1) : Entity(e, target, null, depth);
        }

        private string SelectItem(XElement c, string select, int depth)
        {
            if (HrefOf(c) is not null || Schema.Entities.ContainsKey(c.Name.LocalName))
            {
                return Entity(c, null, null, depth);
            }

            return Typed(c, c.Name.LocalName) ?? Entity(c, null, null, depth);
        }

        private string SelectValue(XElement e, string select, int depth)
        {
            if (HrefOf(e) is not null)
            {
                return Entity(e, null, null, depth);
            }

            if (e.Attribute(Xsi + "type") is not null && DefOf(e, null) is not null)
            {
                return Entity(e, null, null, depth);
            }

            foreach (var c in e.Elements())
            {
                if (Schema.Entities.ContainsKey(c.Name.LocalName))
                {
                    return Entity(c, null, null, depth + 1);
                }

                if (Typed(c, c.Name.LocalName) is { } typed)
                {
                    return typed;
                }
            }

            return "$";
        }

        /// <summary>A value of a defined type inside a select: IFCLABEL('text').</summary>
        private string? Typed(XElement c, string typeName)
        {
            var name = typeName.EndsWith("-wrapper", StringComparison.Ordinal) ? typeName[..^8] : typeName;
            if (!Schema.Defined.TryGetValue(name, out var d))
            {
                return null;
            }

            var attr = new Attr("", false, d.Depth, d.Kind, "");
            return name.ToUpperInvariant() + "(" + Scalar(attr, c.Value) + ")";
        }

        private string Scalar(Attr a, string text)
        {
            text = text.Trim();
            if (a.Depth > 0)
            {
                var parts = a.Kind == 'S' ? [text] : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var inner = string.Join(",", parts.Select(p => One(a.Kind, p)));
                for (var d = 1; d < a.Depth; d++)
                {
                    inner = "(" + inner + ")";
                }

                return "(" + inner + ")";
            }

            return One(a.Kind, text);
        }

        private static string One(char kind, string text)
        {
            if (text.Length == 0)
            {
                return "$";
            }

            switch (kind)
            {
                case 'S':
                    return Quote(text);
                case 'I':
                    return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i.ToString(CultureInfo.InvariantCulture) : "$";
                case 'R':
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) || !double.IsFinite(r))
                    {
                        return "$";
                    }

                    var s = r.ToString("R", CultureInfo.InvariantCulture);
                    return s.Contains('.') || s.Contains('E') ? s : s + ".";
                case 'B':
                    return text.ToLowerInvariant() switch { "true" or "1" => ".T.", "false" or "0" => ".F.", _ => ".U." };
                case 'E':
                    return "." + text.ToUpperInvariant() + ".";
                default:
                    return "$";
            }
        }

        private static string Quote(string text)
        {
            var sb = new StringBuilder("'");
            foreach (var c in text)
            {
                if (c == '\'')
                {
                    sb.Append("''");
                }
                else if (c == '\\')
                {
                    sb.Append("\\\\");
                }
                else if (c < 0x20 || c > 0x7E)
                {
                    sb.Append("\\X2\\").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append("\\X0\\");
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.Append('\'').ToString();
        }
    }
}
