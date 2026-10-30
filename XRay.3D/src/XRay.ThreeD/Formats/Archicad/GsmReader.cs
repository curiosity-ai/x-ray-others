using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Archicad;

using XRay.ThreeD.Internal;

/// <summary>
/// An Archicad library part (<c>.gsm</c>), the GDL object container. Layout as found in the files: a header of 0x30 bytes (<c>WW</c> and the section
/// count), a table entry <c>HEAD</c> (offset, size) at 0x30 pointing to the directory of 16-byte entries (tag stored reversed, offset, size, zero), the
/// two GUIDs of the object, and sections that each start with a 16-byte block header (tag reversed, <c>WW</c>, version, size, zero). Scripts
/// (<c>3DSC</c> 3D, <c>2DSC</c> 2D, <c>1DSC</c> master, <c>PRSC</c> parameter, <c>UISC</c>, <c>VLSC</c> value lists, <c>FMSC</c>/<c>BMSC</c> migration)
/// are UTF-8 text, <c>PARD</c> the parameter list (a 72-byte record with a double default and a name each), <c>GIFF</c> the preview image (MIME type, a zero,
/// the image), <c>CPYR</c> author and licence as UTF-16 fields. The 3D geometry comes from running the master and 3D scripts through
/// <see cref="GdlInterpreter"/>.
/// </summary>
internal static class GsmReader
{
    private const int DirectoryOffset = 0x30;

    public static bool Sniff(ReadOnlySpan<byte> d) =>
        d.Length >= 0x40 && d[0] == (byte)'W' && d[1] == (byte)'W' && TryDirectory(d, out _);

    private readonly record struct Section(string Tag, int Offset, int Size);

    private static bool TryDirectory(ReadOnlySpan<byte> d, out List<Section> sections)
    {
        sections = new List<Section>();
        if (d.Length < DirectoryOffset + 12 || !d.Slice(DirectoryOffset, 4).SequenceEqual("DAEH"u8))
        {
            return false;
        }

        var offset = BinaryPrimitives.ReadUInt32LittleEndian(d[(DirectoryOffset + 4)..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(d[(DirectoryOffset + 8)..]);
        if (size == 0 || size % 16 != 0 || size > 16 * 4096 || offset > (uint)d.Length || size > (uint)d.Length - offset)
        {
            return false;
        }

        for (var p = (int)offset; p < offset + size; p += 16)
        {
            var o = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 4)..]);
            var s = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 8)..]);
            if (s < 16 || o > (uint)d.Length || s > (uint)d.Length - o)
            {
                return false;
            }

            var tag = new string([(char)d[p + 3], (char)d[p + 2], (char)d[p + 1], (char)d[p]]);
            if (!d.Slice((int)o, 4).SequenceEqual(d.Slice(p, 4)))
            {
                return false;
            }

            sections.Add(new Section(tag, (int)o, (int)s));
        }

        return true;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var token = options.Cancellation;
        var d = data.Span;
        if (!Sniff(d) || !TryDirectory(d, out var sections))
        {
            throw new UnsupportedModelFormatException(ModelFormats.Archicad, "Archicad project files (.pln, .pla) are a proprietary container this reader does not open; library parts (.gsm) are read.");
        }

        var model = new Model3D();
        var md = model.Metadata;
        md.Format = ModelFormats.Archicad;
        md.DocumentKind = "object";
        md.Units = LengthUnit.Meter;
        md.UpAxis = UpAxis.Z;
        md.FormatVersion = "GSM " + d[2].ToString("X2", CultureInfo.InvariantCulture);
        md.Application = "Archicad GDL library part";
        if (d.Length >= 0x58 + 16)
        {
            md.Properties.Add(new ModelProperty("GUID", new Guid(d.Slice(0x48, 16)).ToString("D"), "object"));
            md.Properties.Add(new ModelProperty("Revision GUID", new Guid(d.Slice(0x58, 16)).ToString("D"), "object"));
        }

        foreach (var (tag, label) in new[] { ("1DSC", "Master script"), ("3DSC", "3D script"), ("2DSC", "2D script"), ("PRSC", "Parameter script"), ("UISC", "UI script"), ("VLSC", "Value lists script"), ("CTXT", "Comment") })
        {
            if (Script(d, sections, tag) is { } text)
            {
                md.Properties.Add(new ModelProperty(label, text.Length > 262144 ? text[..262144] : text, "gdl"));
            }
        }

        var copyright = Payload(d, sections, "CPYR");
        if (copyright.Length > 6)
        {
            var fields = Utf16Fields(copyright);
            string[] names = ["Author", "License", "License version", "License note"];
            for (var i = 0; i < fields.Count && i < names.Length; i++)
            {
                if (fields[i].Length > 0)
                {
                    md.Properties.Add(new ModelProperty(names[i], fields[i], "copyright"));
                }
            }

            md.Author = fields.Count > 0 && fields[0].Length > 0 ? fields[0] : null;
        }

        // Preview: a MIME type, a zero, then the image.
        var giff = Payload(d, sections, "GIFF");
        var zero = giff.IndexOf((byte)0);
        if (zero is > 0 and < 64 && giff.Length > zero + 8)
        {
            md.HasPreview = true;
            model.Preview = new PreviewImage(Encoding.ASCII.GetString(giff[..zero]), giff[(zero + 1)..].ToArray());
        }

        var parameters = ReadParameters(Payload(d, sections, "PARD"));
        foreach (var (name, value) in parameters)
        {
            md.Features.Add(new ModelFeature(name, "parameter"));
            md.Properties.Add(new ModelProperty(name, value.ToString("R", CultureInfo.InvariantCulture), "parameters"));
        }

        foreach (var s in sections)
        {
            if (s.Tag is not ("HEAD" or "3DSC" or "2DSC" or "1DSC" or "PRSC" or "UISC" or "VLSC" or "FMSC" or "BMSC" or "PARD" or "GIFF" or "CPYR" or "KWRD" or "CTXT" or "ANCS" or "MCRS"))
            {
                md.Notes.Add("Section " + s.Tag + " (" + s.Size.ToString(CultureInfo.InvariantCulture) + " bytes) is not read.");
            }
        }

        var interpreter = new GdlInterpreter(token, options.MaxTriangles);
        foreach (var (name, value) in parameters)
        {
            interpreter.SetNumber(name, value);
        }

        try
        {
            if (Script(d, sections, "1DSC") is { } master)
            {
                interpreter.Run(master);
            }

            if (Script(d, sections, "3DSC") is { } script)
            {
                interpreter.Run(script);
            }
        }
        catch (ModelFormatException)
        {
            throw;
        }

        foreach (var skipped in interpreter.Skipped.Order(StringComparer.OrdinalIgnoreCase).Take(16))
        {
            model.Warnings.Add("GDL statement not interpreted: " + skipped);
        }

        var node = new SceneNode(md.Title ?? "GDL object");
        var materialMap = new Dictionary<int, int>();
        foreach (var (slot, bucket) in interpreter.Geometry)
        {
            if (bucket.Indices.Count == 0)
            {
                continue;
            }

            if (slot >= 0)
            {
                var def = interpreter.Materials[slot];
                materialMap[slot] = model.Materials.Count;
                model.Materials.Add(new ModelMaterial { Name = def.Name, BaseColor = new Vector4(def.Rgb, 1f - def.Transparency), Roughness = 0.6f });
            }
        }

        var positions = new List<float>();
        var normals = new List<float>();
        var indices = new List<uint>();
        var mesh = new MeshData { Name = md.Title };
        foreach (var (slot, bucket) in interpreter.Geometry)
        {
            if (bucket.Indices.Count == 0)
            {
                continue;
            }

            var baseIndex = (uint)(positions.Count / 3);
            mesh.Groups.Add(new MeshGroup(indices.Count, bucket.Indices.Count, slot >= 0 ? materialMap[slot] : -1));
            positions.AddRange(bucket.Positions);
            normals.AddRange(bucket.Normals);
            foreach (var i in bucket.Indices)
            {
                indices.Add(i + baseIndex);
            }
        }

        if (indices.Count > 0)
        {
            mesh.Positions = [.. positions];
            mesh.Normals = [.. normals];
            mesh.Indices = [.. indices];
            model.Meshes.Add(mesh);
            node.Mesh = mesh;
            model.Roots.Add(node);
            md.HasGeometry = true;
            md.Statistics = model.ComputeStatistics();
            md.Bounds = model.ComputeBounds();
        }
        else
        {
            model.Warnings.Add("The GDL 3D script draws no geometry this reader understands.");
        }

        return model;
    }


    private static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> d, List<Section> sections, string tag)
    {
        foreach (var s in sections)
        {
            if (s.Tag == tag)
            {
                return d.Slice(s.Offset + 16, s.Size - 16);
            }
        }

        return default;
    }

    private static string? Script(ReadOnlySpan<byte> d, List<Section> sections, string tag)
    {
        var p = Payload(d, sections, tag);
        if (p.Length == 0)
        {
            return null;
        }

        if (p.Length >= 3 && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF)
        {
            p = p[3..];
        }

        var text = Encoding.UTF8.GetString(p);
        return text.Length == 0 ? null : text;
    }

    /// <summary>The numeric parameters (double default and name per 72-byte record after the header) of a PARD section.</summary>
    private static List<(string Name, double Value)> ReadParameters(ReadOnlySpan<byte> pard)
    {
        var result = new List<(string, double)>();

        // The payload (after the block header) starts with a header of 81 bytes; records of 72 bytes follow, each holding the default as a double
        // and then the name as zero-padded ASCII (32 bytes) and ending in the flags of the next record. The list ends at the first empty name.
        for (var pos = 81; pos + 40 <= pard.Length && result.Count < 512; pos += 72)
        {
            var nameBytes = pard.Slice(pos + 8, 32);
            var end = nameBytes.IndexOf((byte)0);
            if (end <= 0)
            {
                break;
            }

            var name = Encoding.ASCII.GetString(nameBytes[..end]);
            if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                break;
            }

            var value = BitConverter.ToDouble(pard.Slice(pos, 8));
            result.Add((name, double.IsFinite(value) ? value : 0));
        }

        return result;
    }

    /// <summary>Length-prefixed UTF-16 fields (<c>u16 present, u32 length, text</c>) after a 4-byte header.</summary>
    private static List<string> Utf16Fields(ReadOnlySpan<byte> p)
    {
        var fields = new List<string>();
        var pos = 2;
        while (pos + 6 <= p.Length && fields.Count < 16)
        {
            pos += 2;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(p[pos..]);
            pos += 4;
            if (length > (p.Length - pos) / 2)
            {
                break;
            }

            fields.Add(Encoding.Unicode.GetString(p.Slice(pos, (int)length * 2)));
            pos += (int)length * 2;
        }

        return fields;
    }
}
