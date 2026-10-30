using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>Object File Format: OFF, COFF (vertex colours), NOFF (normals) and the 4OFF/nOFF variants are refused. Polygons are fan triangulated; face colours become material groups.</summary>
internal static class OffReader
{
    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var text = Encoding.ASCII.GetString(data.Span);
        var tokens = new List<string>();
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash];
            }

            foreach (var range in line.Split(' '))
            {
                var token = line[range].Trim();
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                }
            }
        }

        if (tokens.Count < 4)
        {
            throw new ModelFormatException("The OFF file is too short.");
        }

        var header = tokens[0].ToUpperInvariant();
        if (!header.EndsWith("OFF", StringComparison.Ordinal) || header.Contains('4') || header.StartsWith('N') && header != "NOFF")
        {
            throw new ModelFormatException($"Unsupported OFF variant '{tokens[0]}'.");
        }

        var colored = header.StartsWith('C');
        var normals = header.StartsWith('N') || header.Contains("NOFF", StringComparison.Ordinal);
        var p = 1;
        var vertexCount = int.Parse(tokens[p++], CultureInfo.InvariantCulture);
        var faceCount = int.Parse(tokens[p++], CultureInfo.InvariantCulture);
        p++; // edge count
        if (vertexCount < 0 || faceCount < 0 || vertexCount > 200_000_000 || faceCount > options.MaxTriangles)
        {
            throw new ModelFormatException("The OFF file declares an impossible size.");
        }

        var perVertex = 3 + (normals ? 3 : 0) + (colored ? 4 : 0);
        if (p + (long)vertexCount * perVertex > tokens.Count)
        {
            throw new ModelFormatException("The OFF vertex list is truncated.");
        }

        var mesh = new MeshData { Positions = new float[vertexCount * 3] };
        if (normals)
        {
            mesh.Normals = new float[vertexCount * 3];
        }

        if (colored)
        {
            mesh.Colors = new float[vertexCount * 4];
        }

        float F(string t) => float.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        for (var v = 0; v < vertexCount; v++)
        {
            for (var a = 0; a < 3; a++)
            {
                mesh.Positions[3 * v + a] = F(tokens[p++]);
            }

            if (normals)
            {
                for (var a = 0; a < 3; a++)
                {
                    mesh.Normals[3 * v + a] = F(tokens[p++]);
                }
            }

            if (colored)
            {
                for (var a = 0; a < 4; a++)
                {
                    var value = F(tokens[p++]);
                    mesh.Colors[4 * v + a] = value > 1 ? value / 255f : value;
                }
            }
        }

        var model = new Model3D();
        var byMaterial = new Dictionary<int, List<uint>>();
        var order = new List<int>();
        var materialIds = new Dictionary<(float, float, float), int>();
        for (var f = 0; f < faceCount; f++)
        {
            if (p >= tokens.Count)
            {
                throw new ModelFormatException("The OFF face list is truncated.");
            }

            var n = int.Parse(tokens[p++], CultureInfo.InvariantCulture);
            if (n < 3 || n > 100_000 || p + n > tokens.Count)
            {
                throw new ModelFormatException("An OFF face is malformed.");
            }

            var ids = new uint[n];
            for (var i = 0; i < n; i++)
            {
                var id = int.Parse(tokens[p++], CultureInfo.InvariantCulture);
                if (id < 0 || id >= vertexCount)
                {
                    throw new ModelFormatException("An OFF face refers to a vertex that does not exist.");
                }

                ids[i] = (uint)id;
            }

            // Optional face colour: three or four numbers after the indices when the next token is not the next face's count. Only
            // read it when the file is a COFF and the remaining tokens leave room for it.
            var material = -1;
            if (colored && p + 3 <= tokens.Count && TryColor(tokens, p, out var color, out var consumed) && LooksLikeFaceColor(tokens, p, consumed, faceCount - f - 1))
            {
                p += consumed;
                if (!materialIds.TryGetValue(color, out material))
                {
                    material = model.Materials.Count;
                    materialIds[color] = material;
                    model.Materials.Add(new ModelMaterial { BaseColor = new System.Numerics.Vector4(color.Item1, color.Item2, color.Item3, 1) });
                }
            }

            if (!byMaterial.TryGetValue(material, out var list))
            {
                byMaterial[material] = list = new List<uint>();
                order.Add(material);
            }

            for (var i = 1; i + 1 < n; i++)
            {
                list.Add(ids[0]);
                list.Add(ids[i]);
                list.Add(ids[i + 1]);
            }
        }

        var indices = new List<uint>();
        foreach (var m in order)
        {
            mesh.Groups.Add(new MeshGroup(indices.Count, byMaterial[m].Count, m));
            indices.AddRange(byMaterial[m]);
        }

        mesh.Indices = indices.ToArray();
        if (mesh.Groups.Count == 1 && mesh.Groups[0].Material < 0)
        {
            mesh.Groups.Clear();
        }

        model.Meshes.Add(mesh);
        model.Roots.Add(new SceneNode(null) { Mesh = mesh });
        model.Metadata.Format = ModelFormats.Off;
        model.Metadata.DocumentKind = "mesh";
        model.Metadata.HasGeometry = true;
        model.Metadata.Statistics = model.ComputeStatistics();
        model.Metadata.Bounds = model.ComputeBounds();
        return model;
    }

    private static bool TryColor(List<string> t, int p, out (float, float, float) color, out int consumed)
    {
        color = default;
        consumed = 0;
        for (var n = 4; n >= 3; n--)
        {
            if (p + n <= t.Count && Enumerable.Range(0, n).All(i => float.TryParse(t[p + i], NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                float V(int i) { var v = float.Parse(t[p + i], CultureInfo.InvariantCulture); return v > 1 ? v / 255f : v; }
                color = (V(0), V(1), V(2));
                consumed = n;
                return true;
            }
        }

        return false;
    }

    /// <summary>A face record that carries a colour has more tokens than faces left would need; decide by counting.</summary>
    private static bool LooksLikeFaceColor(List<string> t, int p, int consumed, int facesLeft)
    {
        // Walk the remaining faces assuming (a) no colour and (b) a colour of the same width; the one that ends exactly at the end wins.
        bool Ends(int colorWidth)
        {
            var q = p + (colorWidth > 0 ? consumed : 0);
            for (var i = 0; i < facesLeft; i++)
            {
                if (q >= t.Count || !int.TryParse(t[q], out var n) || n < 3)
                {
                    return false;
                }

                q += 1 + n + colorWidth;
            }

            return q == t.Count;
        }

        return Ends(consumed) || !Ends(0);
    }
}
