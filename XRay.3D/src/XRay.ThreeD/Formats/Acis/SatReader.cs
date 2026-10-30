using System.Globalization;
using System.Numerics;
using System.Text;
using XRay.ThreeD.Formats.Step;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Acis;

/// <summary>One record of an ACIS save file: its index, entity type and the tokens after the type name.</summary>
internal sealed class SatRecord
{
    public int Index;
    public string Type = "";
    public string[] Tokens = [];
}

/// <summary>
/// ACIS SAT (text save files, save versions 105 to 700 and the same layout of later ones): bodies, lumps, shells, faces, loops, coedges, edges,
/// vertices, planes, cones and cylinders, spheres, tori, straight and elliptical edge curves, transforms, colours. Faces are tessellated
/// with the machinery the STEP reader uses. Spline surfaces and interpolated curves are not read (the face is flat-filled when its
/// boundary is planar, otherwise skipped, with a warning).
/// </summary>
internal static class SatReader
{
    public static bool Sniff(ReadOnlySpan<byte> head)
    {
        // "400 0 1 0" or "105 4361 1 0": three small integers and a flag on the first line.
        var end = head.IndexOf((byte)'\n');
        var line = Encoding.ASCII.GetString(end < 0 ? head : head[..end]).Trim();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4 && parts.Length <= 5 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version is >= 100 and < 100000
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && (parts[3] == "0" || parts[3] == "1" || parts[3].Length <= 3) && head.Length > 12;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var text = Encoding.Latin1.GetString(data.Span);
        var model = new Model3D();
        var (version, recordCount, header) = ParseHeader(text, model.Metadata);
        var records = ParseRecords(text, header, options.Cancellation);
        return Build(model, records, version, options);
    }

    private static (int Version, int Records, int BodyStart) ParseHeader(string text, ModelMetadata meta)
    {
        var position = 0;
        string Line()
        {
            var end = text.IndexOf('\n', position);
            var line = (end < 0 ? text[position..] : text[position..end]).TrimEnd('\r');
            position = end < 0 ? text.Length : end + 1;
            return line;
        }

        var first = Line().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length < 4 || !int.TryParse(first[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new ModelFormatException("The ACIS header is unreadable.");
        }

        meta.Format = ModelFormats.Acis;
        meta.DocumentKind = "model";
        meta.FormatVersion = "ACIS save version " + version.ToString(CultureInfo.InvariantCulture) + " (SAT)";
        _ = int.TryParse(first[1], NumberStyles.None, CultureInfo.InvariantCulture, out var records);
        if (version >= 200)
        {
            // Product line: three counted strings (producer, product, date), with or without a leading @.
            var product = Line();
            var strings = CountedStrings(product);
            if (strings.Count > 0)
            {
                meta.Application = string.Join(" ", strings.Take(2));
            }

            if (strings.Count > 2)
            {
                meta.Properties.Add(new ModelProperty("Saved", strings[2]));
            }

            var units = Line().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (units.Length >= 1 && double.TryParse(units[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
            {
                meta.Units = scale switch
                {
                    1 => LengthUnit.Millimeter,
                    10 => LengthUnit.Centimeter,
                    1000 => LengthUnit.Meter,
                    25.4 => LengthUnit.Inch,
                    304.8 => LengthUnit.Foot,
                    _ => null,
                };
                meta.Properties.Add(new ModelProperty("Millimetres per model unit", scale.ToString("R", CultureInfo.InvariantCulture)));
            }

            if (units.Length >= 3)
            {
                meta.Properties.Add(new ModelProperty("Resolution", units[1] + " / " + units[2]));
            }
        }

        meta.UpAxis = UpAxis.Z;
        return (version, records, position);
    }

    private static List<string> CountedStrings(string line)
    {
        var list = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            if (i < line.Length && line[i] == '@')
            {
                i++;
            }

            var start = i;
            while (i < line.Length && char.IsDigit(line[i]))
            {
                i++;
            }

            if (i == start || !int.TryParse(line.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > line.Length - i)
            {
                break;
            }

            i++; // the separating space
            list.Add(line.Substring(Math.Min(i, line.Length), Math.Min(length, Math.Max(line.Length - i, 0))));
            i += length;
        }

        return list;
    }

    private static List<SatRecord> ParseRecords(string text, int start, CancellationToken cancel)
    {
        var list = new List<SatRecord>();
        var position = start;
        long tick = 0;
        var tokens = new List<string>();
        while (position < text.Length)
        {
            Cancel.Every(cancel, tick++);
            tokens.Clear();
            // Tokens up to the terminating '#'. Counted strings (@N text) may contain spaces and '#'.
            var done = false;
            while (position < text.Length && !done)
            {
                while (position < text.Length && (text[position] is ' ' or '\r' or '\n' or '\t'))
                {
                    position++;
                }

                if (position >= text.Length)
                {
                    break;
                }

                if (text[position] == '#')
                {
                    position++;
                    done = true;
                    break;
                }

                var tokenStart = position;
                while (position < text.Length && !(text[position] is ' ' or '\r' or '\n' or '\t'))
                {
                    position++;
                }

                var token = text[tokenStart..position];
                if (token.Length > 1 && token[0] == '@' && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length >= 0 && position + 1 + length <= text.Length)
                {
                    tokens.Add("@" + text.Substring(position + 1, length));
                    position += 1 + length;
                    continue;
                }

                if (token == "End-of-ACIS-data" || token == "End-of-ASM-data")
                {
                    position = text.Length;
                    break;
                }

                tokens.Add(token);
                if (tokens.Count > 1_000_000)
                {
                    throw new ModelFormatException("An ACIS record is impossibly long.");
                }
            }

            if (tokens.Count == 0)
            {
                continue;
            }

            var record = new SatRecord { Index = list.Count };
            var first = tokens[0];
            var from = 1;
            if (first.Length > 1 && first[0] == '-' && int.TryParse(first.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var explicitIndex) && tokens.Count > 1)
            {
                record.Index = explicitIndex;
                first = tokens[1];
                from = 2;
            }

            record.Type = first;
            record.Tokens = tokens.Skip(from).ToArray();
            list.Add(record);
            if (list.Count > 20_000_000)
            {
                throw new ModelFormatException("The ACIS file has too many records.");
            }
        }

        return list;
    }

    // ------------------------------------------------------------------------------------------------------------

    private sealed class Ctx
    {
        public required Dictionary<int, SatRecord> ById;
        public int Extra;
        public double Linear;
        public double Angular;
        public Dictionary<int, V3> Points = new();
    }

    private static int Ptr(SatRecord r, int i) =>
        i < r.Tokens.Length && r.Tokens[i].Length > 1 && r.Tokens[i][0] == '$' && int.TryParse(r.Tokens[i].AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : -1;

    private static double Num(SatRecord r, int i)
    {
        if (i >= r.Tokens.Length || !double.TryParse(r.Tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
        {
            throw new ModelFormatException($"An ACIS {r.Type} record is missing a number.");
        }

        return v;
    }

    private static V3 Vec(SatRecord r, int i) => new(Num(r, i), Num(r, i + 1), Num(r, i + 2));

    private static bool Reversed(SatRecord r, int i) => i < r.Tokens.Length && r.Tokens[i] is "reversed" or "1";

    private static SatRecord? Get(Ctx c, int pointer, string type) =>
        pointer >= 0 && c.ById.TryGetValue(pointer, out var r) && r.Type == type ? r : null;

    internal static Model3D Build(Model3D model, List<SatRecord> records, int version, LoadOptions options)
    {
        var cancel = options.Cancellation;
        var ctx = new Ctx { ById = new Dictionary<int, SatRecord>(records.Count), Extra = version >= 700 ? 2 : 0 };
        foreach (var r in records)
        {
            ctx.ById[r.Index] = r;
        }

        // Pointers after the attribute pointer begin at Extra + 1 in every entity (the attribute pointer is token 0).
        int F(SatRecord r, int k) => ctx.Extra + 1 + k;
        var meta = model.Metadata;

        // Extent for the sampling tolerance, and the vertex points.
        double min = double.MaxValue, max = double.MinValue;
        foreach (var r in records)
        {
            if (r.Type == "point" && r.Tokens.Length >= 3 + ctx.Extra + 1)
            {
                var p = Vec(r, F(r, 0));
                ctx.Points[r.Index] = p;
                min = Math.Min(min, Math.Min(p.X, Math.Min(p.Y, p.Z)));
                max = Math.Max(max, Math.Max(p.X, Math.Max(p.Y, p.Z)));
            }
        }

        var extent = max > min ? max - min : 1;
        ctx.Linear = Math.Max(options.LinearDeflection, 1e-6) * extent;
        ctx.Angular = Math.Max(options.AngularDeflection, 0.02);

        // Colours: rgb_color attributes name their owner in the pointer before the three components.
        var colours = new Dictionary<int, Vector4>();
        foreach (var r in records)
        {
            if (r.Type.StartsWith("rgb_color", StringComparison.Ordinal) && r.Tokens.Length >= 4)
            {
                var n = r.Tokens.Length;
                var owner = Ptr(r, n - 4);
                if (owner >= 0 && double.TryParse(r.Tokens[n - 3], NumberStyles.Float, CultureInfo.InvariantCulture, out var cr) && double.TryParse(r.Tokens[n - 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cg) && double.TryParse(r.Tokens[n - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cb))
                {
                    colours[owner] = new Vector4((float)Math.Clamp(cr, 0, 1), (float)Math.Clamp(cg, 0, 1), (float)Math.Clamp(cb, 0, 1), 1);
                }
            }
        }

        var materialIds = new Dictionary<Vector4, int>();
        int Material(Vector4 colour)
        {
            if (!materialIds.TryGetValue(colour, out var id))
            {
                id = model.Materials.Count;
                model.Materials.Add(new ModelMaterial { BaseColor = colour });
                materialIds[colour] = id;
            }

            return id;
        }

        var skippedSurfaces = new Dictionary<string, int>(StringComparer.Ordinal);
        var tessellator = new SurfaceFaceTessellator(ctx.Angular, cancel, model.Warnings);
        var bodyCount = 0;
        foreach (var body in records.Where(r => r.Type == "body"))
        {
            Cancel.Every(cancel, bodyCount++, 0);
            var assembler = new FaceMeshAssembler(options.MaxTriangles, "ACIS");
            var bodyColour = colours.TryGetValue(body.Index, out var bc) ? (Vector4?)bc : null;
            var transform = Matrix4x4.Identity;
            if (Get(ctx, Ptr(body, F(body, 2)), "transform") is { } tr)
            {
                transform = ReadTransform(tr, version >= 700 ? 2 : 1);
            }

            var lumpPtr = Ptr(body, F(body, 0));
            var faces = 0;
            for (var lump = Get(ctx, lumpPtr, "lump"); lump is not null; lump = Get(ctx, Ptr(lump, F(lump, 0)), "lump"))
            {
                for (var shell = Get(ctx, Ptr(lump, F(lump, 1)), "shell"); shell is not null; shell = Get(ctx, Ptr(shell, F(shell, 0)), "shell"))
                {
                    var guard = 0;
                    for (var face = Get(ctx, Ptr(shell, F(shell, 2)), "face"); face is not null && guard++ < 10_000_000; face = Get(ctx, Ptr(face, F(face, 0)), "face"))
                    {
                        Cancel.Every(cancel, faces++);
                        var mesh = TessellateFace(ctx, face, F(face, 0), tessellator, skippedSurfaces, model.Warnings);
                        if (mesh is null)
                        {
                            continue;
                        }

                        var colour = colours.TryGetValue(face.Index, out var fc) ? fc : bodyColour;
                        assembler.Add(mesh, colour is { } c ? Material(c) : -1);
                    }
                }
            }

            if (assembler.ToMesh("body " + (bodyCount).ToString(CultureInfo.InvariantCulture)) is { } result)
            {
                model.Meshes.Add(result);
                model.Roots.Add(new SceneNode("Body " + bodyCount.ToString(CultureInfo.InvariantCulture)) { Mesh = result, Transform = transform });
            }
        }

        foreach (var (what, count) in skippedSurfaces.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            model.Warnings.Add($"{count} face(s) on {what} were not tessellated.");
        }

        meta.HasGeometry = model.Meshes.Count > 0;
        meta.Properties.Add(new ModelProperty("Bodies", records.Count(r => r.Type == "body").ToString(CultureInfo.InvariantCulture)));
        meta.Properties.Add(new ModelProperty("Faces", records.Count(r => r.Type == "face").ToString(CultureInfo.InvariantCulture)));
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        return model;
    }

    private static Matrix4x4 ReadTransform(SatRecord r, int at)
    {
        // Nine numbers of the 3x3 part (rows), the translation, the scale and three flags. ACIS transforms row vectors: p' = scale * (p * M) + T.
        var m = new double[9];
        for (var i = 0; i < 9; i++)
        {
            m[i] = Num(r, at + i);
        }

        var t = Vec(r, at + 9);
        var s = Num(r, at + 12);
        return new Matrix4x4(
            (float)(m[0] * s), (float)(m[1] * s), (float)(m[2] * s), 0,
            (float)(m[3] * s), (float)(m[4] * s), (float)(m[5] * s), 0,
            (float)(m[6] * s), (float)(m[7] * s), (float)(m[8] * s), 0,
            (float)t.X, (float)t.Y, (float)t.Z, 1);
    }

    // ------------------------------------------------------------------------------------------------------------

    private static FaceMesh? TessellateFace(Ctx c, SatRecord face, int at, SurfaceFaceTessellator tessellator, Dictionary<string, int> skipped, List<string> warnings)
    {
        var forward = !Reversed(face, at + 5);
        var surfaceRecord = c.ById.GetValueOrDefault(Ptr(face, at + 4));
        var label = "ACIS face " + face.Index.ToString(CultureInfo.InvariantCulture);
        var loops = new List<FaceLoop>();
        var guard = 0;
        for (var loop = Get(c, Ptr(face, at + 1), "loop"); loop is not null && guard++ < 100_000; loop = Get(c, Ptr(loop, at), "loop"))
        {
            if (LoopPoints(c, loop, at) is { Count: >= 3 } points)
            {
                loops.Add(new FaceLoop(points));
            }
        }

        Surface? surface = null;
        var sameSense = forward;
        if (surfaceRecord is not null)
        {
            surface = MakeSurface(c, surfaceRecord, at, out var flip);
            if (flip)
            {
                sameSense = !sameSense;
            }

            if (surface is null)
            {
                skipped[surfaceRecord.Type] = skipped.GetValueOrDefault(surfaceRecord.Type) + 1;
                if (loops.Count == 0)
                {
                    return null;
                }
            }
        }

        if (loops.Count == 0)
        {
            // A whole sphere or torus has no boundary: mesh the entire surface.
            return surface is SphereSurface or TorusSurface ? WholeSurface(surface, sameSense, c) : null;
        }

        // Without a usable surface a flat boundary is meshed as a plane (the face's winding decides which way it faces).
        return tessellator.Tessellate(surface, loops, sameSense, label, approximatedWarning: surface is null);
    }

    private static FaceMesh WholeSurface(Surface surface, bool sameSense, Ctx c)
    {
        var mesh = new FaceMesh();
        var nu = Math.Clamp((int)Math.Ceiling(2 * Math.PI / c.Angular), 8, 256);
        var nv = surface is SphereSurface ? Math.Clamp(nu / 2, 4, 128) : nu;
        var (v0, v1) = surface is SphereSurface ? (-Math.PI / 2, Math.PI / 2) : (0.0, 2 * Math.PI);
        for (var j = 0; j <= nv; j++)
        {
            for (var i = 0; i <= nu; i++)
            {
                var u = 2 * Math.PI * (i % nu) / nu;
                var v = v0 + (v1 - v0) * j / nv;
                var n = surface.Normal(u, v);
                mesh.Positions.Add(surface.At(u, v));
                mesh.Normals.Add(sameSense ? n : -n);
            }
        }

        for (var j = 0; j < nv; j++)
        {
            for (var i = 0; i < nu; i++)
            {
                var a = j * (nu + 1) + i;
                var b = a + 1;
                var d = a + nu + 1;
                var e = d + 1;
                // The sphere's v grows toward the pole, so (u, v) is right-handed with the outward normal.
                if (sameSense)
                {
                    mesh.Indices.AddRange([a, b, e, a, e, d]);
                }
                else
                {
                    mesh.Indices.AddRange([a, e, b, a, d, e]);
                }
            }
        }

        return mesh;
    }

    private static Surface? MakeSurface(Ctx c, SatRecord r, int at, out bool flip)
    {
        flip = false;
        // The record's own fields start after the attribute pointer and the version-dependent extras: for a surface that is `at - 1 + 1`.
        var k = c.Extra + 1;
        switch (r.Type)
        {
            case "plane-surface":
                return new PlaneSurface { F = Frame.From(Vec(r, k), Vec(r, k + 3), Vec(r, k + 6)) };
            case "cone-surface":
            {
                var centre = Vec(r, k);
                var axis = Vec(r, k + 3);
                var major = Vec(r, k + 6);
                var ratio = Num(r, k + 9);
                var next = k + 10;
                while (next < r.Tokens.Length && !double.TryParse(r.Tokens[next], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    next++; // the parameter range of newer versions: "I I" or their binary booleans
                }

                double sin = 0, cos = 1;
                if (next + 1 < r.Tokens.Length && double.TryParse(r.Tokens[next], NumberStyles.Float, CultureInfo.InvariantCulture, out sin) && double.TryParse(r.Tokens[next + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out cos))
                {
                }
                else
                {
                    return null;
                }

                var radius = major.Length;
                if (Math.Abs(ratio - 1) > 1e-6 || radius < 1e-12)
                {
                    return null; // an elliptical cone
                }

                var frame = Frame.From(centre, axis, major);
                flip = cos < 0;
                if (Math.Abs(sin) < 1e-9)
                {
                    return new CylinderSurface { F = frame, R = radius };
                }

                return new ConeSurface { F = frame, R = radius, Angle = Math.Atan2(sin, Math.Abs(cos)) };
            }

            case "sphere-surface":
            {
                var centre = Vec(r, k);
                var radius = Num(r, k + 3);
                V3? uDir = k + 8 < r.Tokens.Length && Ptr(r, k + 4) == -1 && double.TryParse(r.Tokens[k + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? Vec(r, k + 4) : null;
                V3? pole = uDir is not null && k + 10 < r.Tokens.Length && double.TryParse(r.Tokens[k + 7], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? Vec(r, k + 7) : null;
                return new SphereSurface { F = Frame.From(centre, pole ?? new V3(0, 0, 1), uDir), R = radius };
            }

            case "torus-surface":
            {
                var centre = Vec(r, k);
                var axis = Vec(r, k + 3);
                var major = Num(r, k + 6);
                var minor = Num(r, k + 7);
                V3? uDir = k + 10 < r.Tokens.Length && double.TryParse(r.Tokens[k + 8], NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? Vec(r, k + 8) : null;
                return new TorusSurface { F = Frame.From(centre, axis, uDir), Major = major, Minor = minor };
            }
        }

        return null;
    }

    private static Curve? MakeCurve(Ctx c, SatRecord r)
    {
        var k = c.Extra + 1;
        try
        {
            switch (r.Type)
            {
                case "straight-curve":
                {
                    var d = Vec(r, k + 3);
                    return d.Length < 1e-12 ? null : new LineCurve { P = Vec(r, k), D = d.Normalized() };
                }

                case "ellipse-curve":
                {
                    var major = Vec(r, k + 6);
                    var ratio = Num(r, k + 9);
                    var radius = major.Length;
                    if (radius < 1e-12 || ratio <= 0)
                    {
                        return null;
                    }

                    return new EllipseCurve { F = Frame.From(Vec(r, k), Vec(r, k + 3), major), A = radius, B = radius * ratio };
                }
            }
        }
        catch (ModelFormatException)
        {
        }

        return null;
    }

    /// <summary>The boundary of a loop as 3D points, walking its coedges through the next pointers (the last point of each edge is the first of the next).</summary>
    private static List<V3>? LoopPoints(Ctx c, SatRecord loop, int at)
    {
        var first = Get(c, Ptr(loop, at + 1), "coedge");
        if (first is null)
        {
            return null;
        }

        var points = new List<V3>();
        var coedge = first;
        var guard = 0;
        do
        {
            var edge = Get(c, Ptr(coedge, at + 3), "edge");
            if (edge is null)
            {
                return null;
            }

            var edgePoints = EdgePoints(c, edge, at);
            if (edgePoints is null)
            {
                return null;
            }

            // The coedge walks the edge backwards when its sense is reversed (versions with a numeric sense write 0 for forward).
            if (Reversed(coedge, at + 4))
            {
                edgePoints = edgePoints.AsEnumerable().Reverse().ToList();
            }

            for (var i = 0; i + 1 < edgePoints.Count; i++)
            {
                points.Add(edgePoints[i]);
            }

            if (edgePoints.Count == 1)
            {
                points.Add(edgePoints[0]);
            }

            coedge = Get(c, Ptr(coedge, at), "coedge");
        }
        while (coedge is not null && !ReferenceEquals(coedge, first) && guard++ < 1_000_000);

        return points;
    }

    private static V3? Vertex(Ctx c, int pointer)
    {
        var v = Get(c, pointer, "vertex");
        if (v is null)
        {
            return null;
        }

        // The point is the last pointer of the record (binary saves carry an extra integer before it).
        var pointRecord = -1;
        for (var i = v.Tokens.Length - 1; i >= 0; i--)
        {
            if (v.Tokens[i].StartsWith('$'))
            {
                pointRecord = Ptr(v, i);
                break;
            }
        }

        return c.Points.TryGetValue(pointRecord, out var p) ? p : null;
    }

    /// <summary>The points of an edge from its start vertex to its end vertex, sampled along its curve.</summary>
    private static List<V3>? EdgePoints(Ctx c, SatRecord edge, int at)
    {
        // Two layouts: start, end, coedge, curve, sense (older saves); or start, start parameter, end, end parameter, coedge, curve, sense.
        var withParameters = at + 1 < edge.Tokens.Length && edge.Tokens.Length > at + 1 && !edge.Tokens[at + 1].StartsWith('$');
        var startPtr = Ptr(edge, at);
        var endPtr = Ptr(edge, withParameters ? at + 2 : at + 1);
        var curvePtr = Ptr(edge, withParameters ? at + 5 : at + 3);
        var senseAt = withParameters ? at + 6 : at + 4;
        var v0 = Vertex(c, startPtr);
        var v1 = Vertex(c, endPtr);
        if (v0 is null || v1 is null)
        {
            return null;
        }

        var curveRecord = c.ById.GetValueOrDefault(curvePtr);
        var curve = curveRecord is null ? null : MakeCurve(c, curveRecord);
        if (curve is null || curve is LineCurve)
        {
            return [v0.Value, v1.Value];
        }

        var reversed = Reversed(edge, senseAt);
        double t0, t1;
        if (withParameters)
        {
            t0 = Num(edge, at + 1);
            t1 = Num(edge, at + 3);
        }
        else
        {
            var ta = curve.Project(v0.Value);
            var tb = curve.Project(v1.Value);
            if (!reversed)
            {
                while (tb <= ta + 1e-9)
                {
                    tb += 2 * Math.PI;
                }
            }
            else
            {
                while (tb >= ta - 1e-9)
                {
                    tb -= 2 * Math.PI;
                }
            }

            t0 = ta;
            t1 = tb;
        }

        var points = curve.Sample(t0, t1, Math.Max(c.Linear, 1e-9), c.Angular);
        // Whatever convention the file used for the direction, the edge runs from its start vertex.
        if (points.Count > 1 && (points[0] - v0.Value).Length > (points[^1] - v0.Value).Length + 1e-9 && (v0.Value - v1.Value).Length > 1e-9)
        {
            points.Reverse();
        }

        points[0] = v0.Value;
        points[^1] = v1.Value;
        return points;
    }
}
