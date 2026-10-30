using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Saves;

using XRay.ThreeD.Internal;

/// <summary>
/// A PlayStation 2 save exported as <c>.psu</c>: a flat list of 512-byte directory records (status, size, creation and modification time, name),
/// each file's data following its record padded to 1024 bytes. The save's <c>icon.sys</c> gives the title (Shift-JIS, full-width Latin is
/// decoded), the three icon file names and the background and lighting; the icon files (<c>.ico</c>/<c>.icn</c>, magic <c>0x00010000</c>) are
/// small triangle meshes: per vertex a position, normal, texture coordinate and colour for each of the animation shapes (the first is
/// drawn), consecutive vertices forming triangles, followed by an animation header and a 128 x 128 RGB555 texture (raw, or run-length coded).
/// Positions, normals and coordinates are 4.12 fixed point with y pointing down; the mesh is flipped to y up and the winding is chosen so the
/// enclosed volume is positive.
/// </summary>
internal static class PsuReader
{
    private const int Record = 512;
    private const int MaxEntries = 4096;

    public static bool Sniff(ReadOnlySpan<byte> d) =>
        d.Length >= 2 * Record && d[0] == 0x27 && d[1] == 0x84 && (d[2] | d[3]) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(d[4..]) is > 0 and <= MaxEntries
        && d[64] != 0 && d[Record + 0] is 0x27 or 0x97;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var d = data.Span;
        if (d.Length < Record || BinaryPrimitives.ReadUInt16LittleEndian(d) != 0x8427)
        {
            throw new ModelFormatException("The file is not a PlayStation 2 save (.psu).");
        }

        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Psu;
        meta.DocumentKind = "scene";
        meta.Application = "PlayStation 2 memory card save";
        var count = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
        if (count == 0 || count > MaxEntries)
        {
            throw new ModelFormatException("The PSU entry count is out of range.");
        }

        var offset = 0L;
        var icons = new List<(string Name, int Start, int Length)>();
        string? iconSys = null;
        for (var i = 0; i < count; i++)
        {
            Cancel.Every(options.Cancellation, i);
            if (offset + Record > d.Length)
            {
                throw new ModelFormatException("The PSU directory is truncated.");
            }

            var rec = d.Slice((int)offset, Record);
            var status = BinaryPrimitives.ReadUInt16LittleEndian(rec);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(rec[4..]);
            var name = ReadName(rec.Slice(64, 448));
            offset += Record;
            if (i == 0)
            {
                meta.Title = name;
                meta.Created = ReadTime(rec.Slice(8, 8));
                meta.Modified = ReadTime(rec.Slice(24, 8));
                meta.Properties.Add(new ModelProperty("Save directory", name));
                meta.Properties.Add(new ModelProperty("Entries", size.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            if ((status & 0x10) == 0 || size == 0)
            {
                continue;
            }

            if (offset + size > d.Length)
            {
                throw new ModelFormatException("A PSU file runs past the end of the data.");
            }

            var start = (int)offset;
            meta.ComponentFiles.Add(name);
            meta.Properties.Add(new ModelProperty("File " + name, size.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes"));
            if (name.Equals("icon.sys", StringComparison.OrdinalIgnoreCase))
            {
                ReadIconSys(d.Slice(start, (int)size), meta, ref iconSys);
            }
            else if (size > 20 && BinaryPrimitives.ReadUInt32LittleEndian(d[start..]) == 0x00010000)
            {
                icons.Add((name, start, (int)size));
            }

            offset += ((long)size + 1023) & ~1023L;
        }

        foreach (var icon in icons)
        {
            ReadIcon(d.Slice(icon.Start, icon.Length), icon.Name, model, options);
        }

        if (model.Meshes.Count == 0)
        {
            meta.Notes.Add("The save holds no icon model.");
        }

        meta.HasGeometry = model.Meshes.Count > 0;
        meta.UpAxis = UpAxis.Y;
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        return model;
    }

    private static string ReadName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }

    private static DateTimeOffset? ReadTime(ReadOnlySpan<byte> t)
    {
        // reserved, seconds, minutes, hours, day, month, year (u16); the console's clock is Japan standard time.
        try
        {
            var year = BinaryPrimitives.ReadUInt16LittleEndian(t[6..]);
            if (year is < 1980 or > 2100)
            {
                return null;
            }

            return new DateTimeOffset(year, t[5], t[4], t[3], t[2], t[1], TimeSpan.FromHours(9));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void ReadIconSys(ReadOnlySpan<byte> d, ModelMetadata meta, ref string? title)
    {
        if (d.Length < 0x1C4 || d[0] != 'P' || d[1] != 'S' || d[2] != '2' || d[3] != 'D')
        {
            return;
        }

        var lineBreak = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]);
        var raw = d.Slice(0xC0, 68);
        var end = raw.IndexOf((byte)0);
        raw = end < 0 ? raw : raw[..end];
        var first = DecodeShiftJis(raw[..Math.Min(raw.Length, lineBreak)]);
        var second = DecodeShiftJis(raw[Math.Min(raw.Length, lineBreak)..]);
        title = (first + " " + second).Trim();
        meta.Properties.Add(new ModelProperty("Icon title", title));
        meta.Properties.Add(new ModelProperty("Icon title line 1", first));
        meta.Properties.Add(new ModelProperty("Icon title line 2", second));
        meta.Properties.Add(new ModelProperty("List icon", ReadName(d.Slice(0x104, 64))));
        meta.Properties.Add(new ModelProperty("Copy icon", ReadName(d.Slice(0x144, 64))));
        meta.Properties.Add(new ModelProperty("Delete icon", ReadName(d.Slice(0x184, 64))));
        if (!string.IsNullOrWhiteSpace(title))
        {
            meta.Title = title;
        }
    }

    /// <summary>Full-width Latin letters, digits, punctuation and spaces of Shift-JIS (lead byte 0x81 and 0x82); other pairs become '?'. Single bytes are ASCII.</summary>
    internal static string DecodeShiftJis(ReadOnlySpan<byte> s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var b = s[i];
            if (b < 0x80 || (b >= 0xA1 && b <= 0xDF))
            {
                sb.Append(b < 0x80 ? (char)b : (char)(0xFF61 + b - 0xA1));
                continue;
            }

            if (i + 1 >= s.Length)
            {
                break;
            }

            var t = s[++i];
            if (b == 0x81 && t == 0x40)
            {
                sb.Append(' ');
            }
            else if (b == 0x82 && t >= 0x4F && t <= 0x58)
            {
                sb.Append((char)('0' + t - 0x4F));
            }
            else if (b == 0x82 && t >= 0x60 && t <= 0x79)
            {
                sb.Append((char)('A' + t - 0x60));
            }
            else if (b == 0x82 && t >= 0x81 && t <= 0x9A)
            {
                sb.Append((char)('a' + t - 0x81));
            }
            else
            {
                sb.Append('?');
            }
        }

        return sb.ToString();
    }

    private static void ReadIcon(ReadOnlySpan<byte> d, string name, Model3D model, LoadOptions options)
    {
        if (d.Length < 20)
        {
            throw new ModelFormatException("The PSU icon is truncated.");
        }

        var shapes = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
        var textureType = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]);
        var vertices = BinaryPrimitives.ReadUInt32LittleEndian(d[16..]);
        if (shapes is 0 or > 64 || vertices is 0 or > 1 << 20 || vertices % 3 != 0)
        {
            throw new ModelFormatException("The PSU icon header is out of range.");
        }

        var stride = 8L * shapes + 16;
        if (20 + stride * vertices > d.Length)
        {
            throw new ModelFormatException("The PSU icon vertex data is truncated.");
        }

        var n = (int)vertices;
        var positions = new float[n * 3];
        var normals = new float[n * 3];
        var uvs = new float[n * 2];
        var colors = new float[n * 4];
        var p = 20;
        for (var i = 0; i < n; i++)
        {
            Cancel.Every(options.Cancellation, i);
            var x = BinaryPrimitives.ReadInt16LittleEndian(d[p..]);
            var y = BinaryPrimitives.ReadInt16LittleEndian(d[(p + 2)..]);
            var z = BinaryPrimitives.ReadInt16LittleEndian(d[(p + 4)..]);
            p += 8 * (int)shapes;
            var nx = BinaryPrimitives.ReadInt16LittleEndian(d[p..]);
            var ny = BinaryPrimitives.ReadInt16LittleEndian(d[(p + 2)..]);
            var nz = BinaryPrimitives.ReadInt16LittleEndian(d[(p + 4)..]);
            var u = BinaryPrimitives.ReadInt16LittleEndian(d[(p + 8)..]);
            var v = BinaryPrimitives.ReadInt16LittleEndian(d[(p + 10)..]);
            positions[i * 3] = x / 4096f;
            positions[i * 3 + 1] = -y / 4096f;
            positions[i * 3 + 2] = z / 4096f;
            normals[i * 3] = nx / 4096f;
            normals[i * 3 + 1] = -ny / 4096f;
            normals[i * 3 + 2] = nz / 4096f;
            uvs[i * 2] = u / 4096f;
            uvs[i * 2 + 1] = v / 4096f;
            for (var c = 0; c < 3; c++)
            {
                colors[i * 4 + c] = Math.Min(1f, d[p + 12 + c] / 128f);
            }

            colors[i * 4 + 3] = 1f;
            p += 16;
        }

        // Animation header (id, frame length, speed, play offset, frame count) and the frames' shape keys.
        var textureAt = -1;
        var animationNote = "";
        if (p + 20 <= d.Length && BinaryPrimitives.ReadUInt32LittleEndian(d[p..]) == 1)
        {
            var frames = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 16)..]);
            animationNote = $"{frames} animation frame(s), {shapes} shape(s)";
            var q = p + 20;
            for (var f = 0; f < frames && f < 4096; f++)
            {
                if (q + 8 > d.Length)
                {
                    q = -1;
                    break;
                }

                var keys = BinaryPrimitives.ReadUInt32LittleEndian(d[(q + 4)..]);
                if (keys > 1 << 16 || q + 8L + 8L * keys > d.Length)
                {
                    q = -1;
                    break;
                }

                q += 8 + 8 * (int)keys;
            }

            textureAt = q;
        }

        var volume = 0.0;
        for (var i = 0; i < n; i += 3)
        {
            var a = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
            var b = new Vector3(positions[i * 3 + 3], positions[i * 3 + 4], positions[i * 3 + 5]);
            var c = new Vector3(positions[i * 3 + 6], positions[i * 3 + 7], positions[i * 3 + 8]);
            volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
        }

        var flip = volume < 0;
        var indices = new uint[n];
        for (var i = 0; i < n; i += 3)
        {
            indices[i] = (uint)i;
            indices[i + 1] = (uint)(flip ? i + 2 : i + 1);
            indices[i + 2] = (uint)(flip ? i + 1 : i + 2);
        }

        var material = new ModelMaterial { Name = Path.GetFileNameWithoutExtension(name), BaseColor = Vector4.One, Roughness = 0.9f };
        if ((textureType & 4) != 0 && textureAt >= 0 && textureAt <= d.Length)
        {
            var pixels = ReadTexture(d[textureAt..], (textureType & 8) != 0, options);
            if (pixels is not null)
            {
                var rgba = new byte[128 * 128 * 4];
                for (var i = 0; i < 128 * 128; i++)
                {
                    var c = BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(i * 2));
                    rgba[i * 4] = (byte)(((c & 31) * 255 + 15) / 31);
                    rgba[i * 4 + 1] = (byte)((((c >> 5) & 31) * 255 + 15) / 31);
                    rgba[i * 4 + 2] = (byte)((((c >> 10) & 31) * 255 + 15) / 31);
                    rgba[i * 4 + 3] = 255;
                }

                model.Textures.Add(new ModelTexture { Name = name, Data = TextureImages.EncodePng(rgba, 128, 128, options.Cancellation), WrapU = TextureWrap.Repeat, WrapV = TextureWrap.Repeat });
                material.BaseColorTexture = new ModelTextureRef(model.Textures.Count - 1);
            }
        }

        model.Materials.Add(material);
        var mesh = new MeshData { Name = Path.GetFileNameWithoutExtension(name), Positions = positions, Normals = normals, TexCoords = uvs, Colors = colors, Indices = indices };
        mesh.Groups.Add(new MeshGroup(0, n, model.Materials.Count - 1));
        model.Meshes.Add(mesh);
        var node = new SceneNode(mesh.Name) { Mesh = mesh };
        node.Properties["file"] = name;
        node.Properties["vertices"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (animationNote.Length > 0)
        {
            node.Properties["animation"] = animationNote;
        }

        model.Roots.Add(node);
        model.Metadata.PartNames.Add(mesh.Name!);
    }

    private static byte[]? ReadTexture(ReadOnlySpan<byte> d, bool compressed, LoadOptions options)
    {
        const int Size = 128 * 128 * 2;
        if (!compressed)
        {
            return d.Length >= Size ? d[..Size].ToArray() : null;
        }

        // A u32 byte count, then 16-bit codes: 0xFFxx copies (0x100 - xx) literal words, any other code repeats the next word that many times.
        if (d.Length < 4)
        {
            return null;
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(d);
        var end = (int)Math.Min((long)d.Length, 4L + total);
        var output = new byte[Size];
        var o = 0;
        var p = 4;
        long tick = 0;
        while (p + 2 <= end && o < Size)
        {
            Cancel.Every(options.Cancellation, tick++);
            var code = BinaryPrimitives.ReadUInt16LittleEndian(d[p..]);
            p += 2;
            if ((code & 0xFF00) == 0xFF00)
            {
                var run = 0x100 - (code & 0xFF);
                for (var i = 0; i < run && p + 2 <= end && o < Size; i++)
                {
                    output[o++] = d[p++];
                    output[o++] = d[p++];
                }
            }
            else
            {
                if (p + 2 > end)
                {
                    break;
                }

                var lo = d[p];
                var hi = d[p + 1];
                p += 2;
                for (var i = 0; i < code && o < Size; i++)
                {
                    output[o++] = lo;
                    if (o < Size)
                    {
                        output[o++] = hi;
                    }
                }
            }
        }

        return o == Size ? output : null;
    }
}
