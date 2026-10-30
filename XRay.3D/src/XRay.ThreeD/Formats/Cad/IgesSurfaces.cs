using System.Numerics;
using XRay.ThreeD.Formats.Step;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// IGES surfaces and B-rep: rational B-spline surfaces (128), planes (108), surfaces of revolution (120), tabulated cylinders (122) and
/// the quadrics (190 plane, 192 cylinder, 194 cone, 196 sphere, 198 torus); trimmed (144) and bounded (143) surfaces with their boundaries
/// (142 curves on a surface, 141 boundaries); faces (510), loops (508), edge and vertex lists (504, 502), shells (514) and manifold solids (186).
/// The surface geometry and the triangulation of a trimmed patch are those of the STEP reader (<see cref="SurfaceFaceTessellator"/>).
/// </summary>
internal static partial class IgesReader
{
    private sealed partial class Builder
    {
        /// <summary>A surface with the rectangle of its parameter domain; <see cref="ParamDefined"/> when the file's parameter-space curves are in that domain (128).</summary>
        private sealed record IgesSurface(Surface Geometry, bool ParamDefined, double U0, double U1, double V0, double V1, bool ClosedU = false, bool ClosedV = false);

        private readonly Dictionary<int, IgesSurface?> _surfaces = new();
        private readonly HashSet<int> _consumedCurves = new();
        private readonly List<string> _surfaceWarnings = new();
        private SurfaceFaceTessellator? _faceTessellator;
        private int _unsupportedSurfaces;
        private int _skippedFaces;

        private SurfaceFaceTessellator Faces => _faceTessellator ??= new SurfaceFaceTessellator(_options.AngularDeflection, _options.Cancellation, _surfaceWarnings);

        private static int Ptr(List<string> f, int index) => (int)Math.Clamp(Real(f, index), 0, int.MaxValue / 2);

        private static V3 Xf(V3 p, Matrix4x4 m) => new(
            p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41, p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42, p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);

        private static V3 XfDir(V3 d, Matrix4x4 m) => new(d.X * m.M11 + d.Y * m.M21 + d.Z * m.M31, d.X * m.M12 + d.Y * m.M22 + d.Z * m.M32, d.X * m.M13 + d.Y * m.M23 + d.Z * m.M33);

        private static bool IsSurfaceType(int type) => type is 108 or 114 or 118 or 120 or 122 or 128 or 190 or 192 or 194 or 196 or 198;

        // ---- drawing ---------------------------------------------------------------------------------------------

        /// <summary>Tessellates every solid, shell, trimmed surface and free-standing surface into meshes under the level nodes; returns the triangle count.</summary>
        private long BuildSurfaces()
        {
            var referencedSurfaces = new HashSet<int>();
            var referencedShells = new HashSet<int>();
            var tick = 0L;
            foreach (var (_, entry) in _byIndex)
            {
                Cancel.Every(_options.Cancellation, tick++);
                if (entry.Type is not (144 or 143 or 510 or 186))
                {
                    continue;
                }

                if (Parameters(entry) is not { } f)
                {
                    continue;
                }

                switch (entry.Type)
                {
                    case 144:
                    case 510:
                        referencedSurfaces.Add(Ptr(f, 1));
                        break;
                    case 143:
                        referencedSurfaces.Add(Ptr(f, 2));
                        break;
                    case 186:
                        referencedShells.Add(Ptr(f, 1));
                        for (var i = 0; i < Count(f, 3) && 4 + 2 * i < f.Count; i++)
                        {
                            referencedShells.Add(Ptr(f, 4 + 2 * i));
                        }

                        break;
                }
            }

            long triangles = 0;
            var freeSurfaces = new Dictionary<int, FaceMeshAssembler>();
            foreach (var (index, entry) in _byIndex.OrderBy(p => p.Key))
            {
                Cancel.Every(_options.Cancellation, tick++, 63);
                if (entry.Blank != 0 || entry.Use == 5)
                {
                    continue;
                }

                switch (entry.Type)
                {
                    case 186 when Parameters(entry) is { } f:
                        {
                            var assembler = NewAssembler();
                            var shellMatrix = TransformOf(entry);
                            AddShell(assembler, Ptr(f, 1), Count(f, 2) != 0, shellMatrix, entry);
                            for (var i = 0; i < Count(f, 3) && 5 + 2 * i < f.Count; i++)
                            {
                                AddShell(assembler, Ptr(f, 4 + 2 * i), Count(f, 5 + 2 * i) != 0, shellMatrix, entry);
                            }

                            triangles += Emit(assembler, entry, string.IsNullOrEmpty(entry.Label) ? "solid" : entry.Label);
                            break;
                        }

                    case 514 when !referencedShells.Contains(index) && (entry.Subordinate & 1) == 0:
                        {
                            var assembler = NewAssembler();
                            AddShell(assembler, index, true, Matrix4x4.Identity, entry);
                            triangles += Emit(assembler, entry, string.IsNullOrEmpty(entry.Label) ? "shell" : entry.Label);
                            break;
                        }

                    case 144 or 143 when (entry.Subordinate & 1) == 0 && Parameters(entry) is { } f:
                        {
                            if (!freeSurfaces.TryGetValue(entry.Level, out var assembler))
                            {
                                freeSurfaces[entry.Level] = assembler = NewAssembler();
                            }

                            AddTrimmedSurface(assembler, entry, f);
                            break;
                        }

                    case 128 or 120 or 122 or 108 when !referencedSurfaces.Contains(index) && (entry.Subordinate & 1) == 0 && entry.Type != 108:
                        {
                            if (!freeSurfaces.TryGetValue(entry.Level, out var assembler))
                            {
                                freeSurfaces[entry.Level] = assembler = NewAssembler();
                            }

                            if (SurfaceOf(index) is { } surface)
                            {
                                var material = ColorMaterial(entry);
                                if (Faces.Tessellate(surface.Geometry, [Rectangle(surface)], true, $"Surface {index}") is { } mesh)
                                {
                                    assembler.Add(mesh, material);
                                }
                            }
                            else
                            {
                                _unsupportedSurfaces++;
                            }

                            break;
                        }

                    case 114 or 118 when (entry.Subordinate & 1) == 0:
                        _unsupportedSurfaces++;
                        break;
                }
            }

            foreach (var (level, assembler) in freeSurfaces.OrderBy(p => p.Key))
            {
                triangles += Emit(assembler, level, "surfaces");
            }

            return triangles;
        }

        private FaceMeshAssembler NewAssembler() => new(_options.MaxTriangles, "IGES");

        private long Emit(FaceMeshAssembler assembler, DirectoryEntry entry, string name) => Emit(assembler, entry.Level, name);

        private long Emit(FaceMeshAssembler assembler, int level, string name)
        {
            var mesh = assembler.ToMesh(name);
            if (mesh is null)
            {
                return 0;
            }

            _model.Meshes.Add(mesh);
            var layer = LayerOf(level);
            LevelNode(level).Children.Add(new SceneNode(name) { Mesh = mesh, Layer = layer });
            return mesh.TriangleCount;
        }

        /// <summary>The material of the first of the entries that states a colour; -1 when none does.</summary>
        private int ColorMaterial(params DirectoryEntry?[] chain)
        {
            foreach (var entry in chain)
            {
                if (entry is not null && entry.Color != 0)
                {
                    return MaterialFor(ColorOf(entry));
                }
            }

            return -1;
        }

        // ---- B-rep -----------------------------------------------------------------------------------------------

        private void AddShell(FaceMeshAssembler assembler, int shellPointer, bool outward, Matrix4x4 matrix, DirectoryEntry solid)
        {
            if (!_byIndex.TryGetValue(shellPointer, out var shell) || shell.Type != 514 || Parameters(shell) is not { } f)
            {
                return;
            }

            var count = Count(f, 1);
            for (var i = 0; i < count && 3 + 2 * i < f.Count; i++)
            {
                Cancel.Every(_options.Cancellation, i, 63);
                if (!_byIndex.TryGetValue(Ptr(f, 2 + 2 * i), out var face) || face.Type != 510 || face.Blank != 0)
                {
                    continue;
                }

                // The face's own flag says whether the surface normal points out of the shell; the shell's says whether the shell points out of the solid.
                var sameSense = (Count(f, 3 + 2 * i) != 0) == outward;
                AddFace(assembler, face, sameSense, matrix, solid, shell);
            }
        }

        private void AddFace(FaceMeshAssembler assembler, DirectoryEntry face, bool sameSense, Matrix4x4 matrix, params DirectoryEntry[] owners)
        {
            if (Parameters(face) is not { } f)
            {
                return;
            }

            var surfaceEntry = _byIndex.GetValueOrDefault(Ptr(f, 1));
            var surface = SurfaceOf(Ptr(f, 1));
            if (surface is null)
            {
                _skippedFaces++;
                if (surfaceEntry is null || !IsSurfaceType(surfaceEntry.Type) || surfaceEntry.Type is 114 or 118)
                {
                    _unsupportedSurfaces++;
                }

                return;
            }

            var loops = new List<FaceLoop>();
            var count = Count(f, 2);
            var outerStated = Count(f, 3) != 0;
            if (!outerStated && surface.ParamDefined)
            {
                loops.Add(Rectangle(surface)); // no outer loop: the face reaches the surface's own boundary
            }

            for (var i = 0; i < count && 4 + i < f.Count; i++)
            {
                if (LoopOf508(Ptr(f, 4 + i), surface) is { } loop)
                {
                    loops.Add(loop);
                }
            }

            if (loops.Count == 0)
            {
                _skippedFaces++;
                return;
            }

            if (Faces.Tessellate(surface.Geometry, loops, sameSense, $"Face {face.Pointer}") is not { } mesh)
            {
                _skippedFaces++;
                return;
            }

            var material = ColorMaterial([face, surfaceEntry, .. Enumerable.Reverse(owners)]);
            assembler.Add(mesh, material, matrix.IsIdentity ? null : matrix);
        }

        private FaceLoop? LoopOf508(int pointer, IgesSurface surface)
        {
            if (!_byIndex.TryGetValue(pointer, out var entry) || entry.Type != 508 || Parameters(entry) is not { } f)
            {
                return null;
            }

            var pieces = new List<List<(double U, double V, V3 P)>>();
            var infos = new List<(bool Forward, (V3 Start, V3 End)? Ends, bool ModelSpace)>();
            var n = Count(f, 1);
            var p = 2;
            for (var i = 0; i < n && p + 4 < f.Count; i++)
            {
                Cancel.Every(_options.Cancellation, i, 63);
                var type = Count(f, p);
                var edgeList = Ptr(f, p + 1);
                var forward = Count(f, p + 3) != 0;
                var index = Count(f, p + 2);
                var k = Count(f, p + 4);
                var first = p + 6;
                p += 5 + 2 * k;
                if (type != 0)
                {
                    continue; // a vertex-only edge has no extent
                }

                List<(double U, double V, V3 P)>? piece = null;
                if (surface.ParamDefined && k > 0)
                {
                    var curves = new List<List<V3>>(k);
                    for (var j = 0; j < k && first + 2 * j < f.Count; j++)
                    {
                        if (CurvePoints(Ptr(f, first + 2 * j)) is { } c)
                        {
                            curves.Add(c);
                        }
                    }

                    if (JoinPieces(curves) is { } joined)
                    {
                        piece = FromParameters(joined, surface);
                    }
                }

                var usedModelSpace = false;
                if (piece is null && EdgePoints3D(edgeList, index) is { } edge)
                {
                    piece = FromModelSpace(edge, surface);
                    usedModelSpace = true;
                }

                if (piece is null || piece.Count < 2)
                {
                    continue;
                }

                infos.Add((forward, EdgeEnds(edgeList, index), usedModelSpace));
                pieces.Add(piece);
            }

            return AssembleLoop(pieces, surface, infos!.Select(i => i.ModelSpace).ToList(), infos);
        }

        private FaceLoop? AssembleLoop(List<List<(double U, double V, V3 P)>> pieces, IgesSurface surface, List<bool> projected, List<(bool Forward, (V3 Start, V3 End)? Ends, bool ModelSpace)>? infos = null)
        {
            if (pieces.Count == 0)
            {
                return null;
            }

            // The edges follow each other round the loop but are stored in their own directions (and the orientation flags of the
            // files disagree about whose direction they mean), so each piece is turned round when its far end is the one that continues the path.
            // The edges follow each other round the loop but are stored in their own directions, so each piece is turned round when its far
            // end is the one that continues the path (ties, as between the two ends of a seam, go to the closer parameters). A closed edge
            // has no ends to tell its direction by: its flag does, relative to the way its curve runs. When no edge is closed, the direction
            // of the loop as a whole comes from an open edge, which runs from its start vertex to its end vertex (against that when flagged so).
            double Gap((double U, double V, V3 P) a, (double U, double V, V3 P) b) =>
                (a.P - b.P).Length + (surface.ParamDefined ? 1e-9 * (Math.Abs(a.U - b.U) + Math.Abs(a.V - b.V)) : 0);
            var hasInfo = infos is not null && infos.Count == pieces.Count;
            var anchored = false;
            if (hasInfo)
            {
                for (var k = 0; k < pieces.Count; k++)
                {
                    if (infos![k].ModelSpace && infos[k].Ends is { } e && (e.Start - e.End).Length < 1e-9 * (1 + e.Start.Length))
                    {
                        anchored = true;
                        if (!infos[k].Forward)
                        {
                            pieces[k].Reverse();
                        }
                    }
                }
            }

            bool IsAnchored(int k) => hasInfo && infos![k].ModelSpace && infos[k].Ends is { } e && (e.Start - e.End).Length < 1e-9 * (1 + e.Start.Length);
            if (pieces.Count > 1 && !IsAnchored(0))
            {
                var next = pieces[1];
                double Reach((double U, double V, V3 P) a) => Math.Min(Gap(a, next[0]), Gap(a, next[^1]));
                if (Reach(pieces[0][0]) < Reach(pieces[0][^1]))
                {
                    pieces[0].Reverse();
                }
            }

            for (var k = 1; k < pieces.Count; k++)
            {
                var tail = pieces[k - 1][^1];
                if (!IsAnchored(k) && Gap(tail, pieces[k][0]) > Gap(tail, pieces[k][^1]))
                {
                    pieces[k].Reverse();
                }
            }

            if (hasInfo && !anchored)
            {
                var open = infos!.FindIndex(i => i.Ends is { } e && (e.Start - e.End).Length >= 1e-9 * (1 + e.Start.Length));
                if (open >= 0)
                {
                    var wanted = infos[open].Forward ? infos[open].Ends!.Value.Start : infos[open].Ends!.Value.End;
                    if ((pieces[open][0].P - wanted).Length > (pieces[open][^1].P - wanted).Length)
                    {
                        pieces.Reverse();
                        foreach (var piece in pieces)
                        {
                            piece.Reverse();
                        }
                    }
                }
            }

            if (surface.ParamDefined && (surface.ClosedU || surface.ClosedV))
            {
                AlignAcrossSeams(pieces, projected, surface);
            }

            bool Same((double U, double V, V3 P) a, (double U, double V, V3 P) b) =>
                (a.P - b.P).Length < 1e-9 * (1 + b.P.Length) && (!surface.ParamDefined || Math.Abs(a.U - b.U) + Math.Abs(a.V - b.V) < 1e-9 * (1 + Math.Abs(b.U) + Math.Abs(b.V)));
            var all = new List<(double U, double V, V3 P)>();
            for (var k = 0; k < pieces.Count; k++)
            {
                foreach (var point in pieces[k])
                {
                    if (all.Count > 0 && Same(all[^1], point))
                    {
                        continue;
                    }

                    all.Add(point);
                }
            }

            while (all.Count > 1 && Same(all[0], all[^1]))
            {
                all.RemoveAt(all.Count - 1);
            }

            if (all.Count < 3)
            {
                return null;
            }

            var points = new List<V3>(all.Count);
            foreach (var point in all)
            {
                points.Add(point.P);
            }

            if (!surface.ParamDefined)
            {
                return new FaceLoop(points);
            }

            var uv = new List<(double U, double V)>(all.Count);
            foreach (var point in all)
            {
                uv.Add((point.U, point.V));
            }

            return new FaceLoop(points, uv);
        }

        /// <summary>The sample points of an edge-list entry between its vertices (cut out of the whole curve by the nearest samples).</summary>
        private List<V3>? EdgePoints3D(int listPointer, int index)
        {
            if (!_byIndex.TryGetValue(listPointer, out var list) || list.Type != 504 || Parameters(list) is not { } f || index < 1 || index > Count(f, 1))
            {
                return null;
            }

            var b = 2 + 5 * (index - 1);
            if (b + 4 >= f.Count)
            {
                return null;
            }

            var curve = CurvePoints(Ptr(f, b));
            var start = Vertex(Ptr(f, b + 1), Count(f, b + 2));
            var end = Vertex(Ptr(f, b + 3), Count(f, b + 4));
            if (curve is null || start is null || end is null)
            {
                return null;
            }

            var s = start.Value;
            var t = end.Value;
            var scale = 1 + s.Length;
            if ((s - t).Length < 1e-9 * scale)
            {
                // A closed edge: the whole curve, from the vertex round to the vertex.
                var whole = new List<V3>(curve);
                var first = Nearest(curve, s);
                if (first > 0 && first < curve.Count - 1)
                {
                    whole.Clear();
                    whole.AddRange(curve.Skip(first));
                    whole.AddRange(curve.Take(first + 1).Skip(1));
                }

                whole[0] = s;
                whole[^1] = s;
                return whole;
            }

            var i0 = Nearest(curve, s);
            var i1 = Nearest(curve, t);
            List<V3> piece;
            if (i0 < i1)
            {
                piece = curve.GetRange(i0, i1 - i0 + 1);
            }
            else if (i0 > i1)
            {
                var closedShape = (curve[0] - curve[^1]).Length < 1e-9 * scale;
                if (closedShape)
                {
                    piece = curve.GetRange(i0, curve.Count - i0);
                    piece.AddRange(curve.Take(i1 + 1).Skip(1));
                }
                else
                {
                    piece = curve.GetRange(i1, i0 - i1 + 1);
                    piece.Reverse();
                }
            }
            else
            {
                piece = [s, t];
            }

            piece[0] = s;
            piece[^1] = t;
            return piece;
        }

        private (V3 Start, V3 End)? EdgeEnds(int listPointer, int index)
        {
            if (!_byIndex.TryGetValue(listPointer, out var list) || list.Type != 504 || Parameters(list) is not { } f || index < 1 || index > Count(f, 1))
            {
                return null;
            }

            var b = 2 + 5 * (index - 1);
            return b + 4 < f.Count && Vertex(Ptr(f, b + 1), Count(f, b + 2)) is { } s && Vertex(Ptr(f, b + 3), Count(f, b + 4)) is { } t ? (s, t) : null;
        }

        private static int Nearest(List<V3> points, V3 p)
        {
            var best = 0;
            var distance = double.MaxValue;
            for (var i = 0; i < points.Count; i++)
            {
                var d = (points[i] - p).Length;
                if (d < distance)
                {
                    distance = d;
                    best = i;
                }
            }

            return best;
        }

        private V3? Vertex(int listPointer, int index)
        {
            if (!_byIndex.TryGetValue(listPointer, out var list) || list.Type != 502 || Parameters(list) is not { } f || index < 1 || index > Count(f, 1) || 3 * index + 1 >= f.Count)
            {
                return null;
            }

            return new V3(Real(f, 3 * index - 1), Real(f, 3 * index), Real(f, 3 * index + 1));
        }

        // ---- trimmed and bounded surfaces ------------------------------------------------------------------------

        private void AddTrimmedSurface(FaceMeshAssembler assembler, DirectoryEntry entry, List<string> f)
        {
            var surfacePointer = entry.Type == 144 ? Ptr(f, 1) : Ptr(f, 2);
            var surfaceEntry = _byIndex.GetValueOrDefault(surfacePointer);
            var surface = SurfaceOf(surfacePointer);
            if (surface is null)
            {
                _unsupportedSurfaces++;
                return;
            }

            var loops = new List<FaceLoop>();
            if (entry.Type == 144)
            {
                var outerIsBoundary = Count(f, 2) == 0;
                var inner = Count(f, 3);
                if (outerIsBoundary)
                {
                    loops.Add(Rectangle(surface));
                }
                else if (LoopOf142(Ptr(f, 4), surface) is { } outer)
                {
                    loops.Add(outer);
                }

                for (var i = 0; i < inner && 5 + i < f.Count; i++)
                {
                    if (LoopOf142(Ptr(f, 5 + i), surface) is { } hole)
                    {
                        loops.Add(hole);
                    }
                }
            }
            else
            {
                var count = Count(f, 3);
                for (var i = 0; i < count && 4 + i < f.Count; i++)
                {
                    if (LoopOf141(Ptr(f, 4 + i), surface) is { } loop)
                    {
                        loops.Add(loop);
                    }
                }

                if (loops.Count == 0)
                {
                    loops.Add(Rectangle(surface));
                }
                else
                {
                    // Nothing says which boundary is the outer one: the widest is.
                    var widest = 0;
                    for (var i = 1; i < loops.Count; i++)
                    {
                        if (Extent(loops[i]) > Extent(loops[widest]))
                        {
                            widest = i;
                        }
                    }

                    (loops[0], loops[widest]) = (loops[widest], loops[0]);
                }
            }

            if (loops.Count == 0)
            {
                return;
            }

            if (Faces.Tessellate(surface.Geometry, loops, true, $"Trimmed surface {entry.Pointer}") is { } mesh)
            {
                assembler.Add(mesh, ColorMaterial(entry, surfaceEntry), TransformOf(entry) is { IsIdentity: false } m ? m : null);
            }
            else
            {
                _skippedFaces++;
            }
        }

        private static double Extent(FaceLoop loop)
        {
            if (loop.Uv is { } uv && uv.Count > 0)
            {
                return (uv.Max(p => p.U) - uv.Min(p => p.U)) * (uv.Max(p => p.V) - uv.Min(p => p.V));
            }

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var p in loop.Points)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z);
                maxZ = Math.Max(maxZ, p.Z);
            }

            return (maxX - minX) + (maxY - minY) + (maxZ - minZ);
        }

        /// <summary>A 142 curve on a surface: the parameter-space curve when the surface has a parameter domain the file's curves share, else the model-space curve.</summary>
        private FaceLoop? LoopOf142(int pointer, IgesSurface surface)
        {
            if (!_byIndex.TryGetValue(pointer, out var entry) || entry.Type != 142 || Parameters(entry) is not { } f)
            {
                return null;
            }

            int parameterCurve = Ptr(f, 3), modelCurve = Ptr(f, 4), preferred = Count(f, 5);
            return LoopOfCurve(parameterCurve, modelCurve, preferred != 2, surface);
        }

        private FaceLoop? LoopOf141(int pointer, IgesSurface surface)
        {
            if (!_byIndex.TryGetValue(pointer, out var entry) || entry.Type != 141 || Parameters(entry) is not { } f)
            {
                return null;
            }

            var n = Count(f, 4);
            var p = 5;
            var pieces = new List<List<(double U, double V, V3 P)>>();
            var projected = new List<bool>();
            for (var i = 0; i < n && p + 2 < f.Count; i++)
            {
                Cancel.Every(_options.Cancellation, i, 63);
                var modelCurve = Ptr(f, p);
                var k = Count(f, p + 2);
                var first = p + 3;
                p += 3 + k;
                List<(double U, double V, V3 P)>? piece = null;
                if (surface.ParamDefined && k > 0)
                {
                    var curves = new List<List<V3>>(k);
                    for (var j = 0; j < k && first + j < f.Count; j++)
                    {
                        if (CurvePoints(Ptr(f, first + j)) is { } c)
                        {
                            curves.Add(c);
                        }
                    }

                    if (JoinPieces(curves) is { } joined)
                    {
                        piece = FromParameters(joined, surface);
                    }
                }

                var fromModel = false;
                if (piece is null && CurvePoints(modelCurve) is { } model)
                {
                    piece = FromModelSpace(model, surface);
                    fromModel = true;
                }

                if (piece is null)
                {
                    continue;
                }

                projected.Add(fromModel);
                pieces.Add(piece);
            }

            return AssembleLoop(pieces, surface, projected);
        }

        private FaceLoop? LoopOfCurve(int parameterCurve, int modelCurve, bool allowParameters, IgesSurface surface)
        {
            List<(double U, double V, V3 P)>? piece = null;
            var fromModel = false;
            if (allowParameters && surface.ParamDefined && parameterCurve != 0 && CurvePoints(parameterCurve) is { } uv)
            {
                piece = FromParameters(uv, surface);
            }

            if (piece is null && modelCurve != 0 && CurvePoints(modelCurve) is { } model)
            {
                piece = FromModelSpace(model, surface);
                fromModel = true;
            }

            return piece is null ? null : AssembleLoop([piece], surface, [fromModel]);
        }

        // ---- curves and boundaries -------------------------------------------------------------------------------

        /// <summary>Points of a curve entity (110, 100, 106, 126, and composites of them, 102), placed by the entity's transformation.</summary>
        private List<V3>? CurvePoints(int pointer, int depth = 0)
        {
            if (depth > 8 || !_byIndex.TryGetValue(pointer, out var entry) || Parameters(entry) is not { } f)
            {
                return null;
            }

            var points = entry.Type switch
            {
                110 => Line(f),
                100 => Arc(f),
                106 => Copious(entry, f),
                126 => Nurbs(f),
                102 => Composite(f, depth),
                _ => null,
            };
            if (points is null || points.Count < 2)
            {
                return null;
            }

            _consumedCurves.Add(pointer);
            if (TransformOf(entry) is { IsIdentity: false } m)
            {
                for (var i = 0; i < points.Count; i++)
                {
                    points[i] = Xf(points[i], m);
                }
            }

            return points;
        }

        /// <summary>
        /// Every curve a boundary refers to (142 and 141 curves on surfaces, 504 edge curves, 508 parameter-space curves) is part of a face, not a
        /// drawing of its own, whether or not the face used it (parameter-space curves of a surface without a parameter domain are not read).
        /// </summary>
        private void MarkBoundaryCurvesConsumed()
        {
            var tick = 0L;
            foreach (var (_, entry) in _byIndex)
            {
                Cancel.Every(_options.Cancellation, tick++);
                if (entry.Type is not (141 or 142 or 504 or 508) || Parameters(entry) is not { } f)
                {
                    continue;
                }

                switch (entry.Type)
                {
                    case 142:
                        MarkConsumed(Ptr(f, 3), 0);
                        MarkConsumed(Ptr(f, 4), 0);
                        break;
                    case 141:
                        {
                            var p = 5;
                            for (var i = 0; i < Count(f, 4) && p + 2 < f.Count; i++)
                            {
                                MarkConsumed(Ptr(f, p), 0);
                                var k = Count(f, p + 2);
                                for (var j = 0; j < k && p + 3 + j < f.Count; j++)
                                {
                                    MarkConsumed(Ptr(f, p + 3 + j), 0);
                                }

                                p += 3 + k;
                            }

                            break;
                        }

                    case 504:
                        for (var i = 0; i < Count(f, 1) && 2 + 5 * i < f.Count; i++)
                        {
                            MarkConsumed(Ptr(f, 2 + 5 * i), 0);
                        }

                        break;
                    case 508:
                        {
                            var p = 2;
                            for (var i = 0; i < Count(f, 1) && p + 4 < f.Count; i++)
                            {
                                var k = Count(f, p + 4);
                                for (var j = 0; j < k && p + 6 + 2 * j < f.Count; j++)
                                {
                                    MarkConsumed(Ptr(f, p + 6 + 2 * j), 0);
                                }

                                p += 5 + 2 * k;
                            }

                            break;
                        }
                }
            }
        }

        private void MarkConsumed(int pointer, int depth)
        {
            if (depth > 8 || !_byIndex.TryGetValue(pointer, out var entry))
            {
                return;
            }

            _consumedCurves.Add(pointer);
            if (entry.Type == 102 && Parameters(entry) is { } f)
            {
                for (var i = 0; i < Count(f, 1) && 2 + i < f.Count; i++)
                {
                    MarkConsumed(Ptr(f, 2 + i), depth + 1);
                }
            }
        }

        private List<V3>? Composite(List<string> f, int depth)
        {
            var n = Count(f, 1);
            var pieces = new List<List<V3>>();
            for (var i = 0; i < n && 2 + i < f.Count; i++)
            {
                Cancel.Every(_options.Cancellation, i, 63);
                if (CurvePoints(Ptr(f, 2 + i), depth + 1) is { } piece)
                {
                    pieces.Add(piece);
                }
            }

            return JoinPieces(pieces);
        }

        /// <summary>Joins curve pieces end to start, turning a piece round when its far end is the one that continues the path.</summary>
        private static List<V3>? JoinPieces(List<List<V3>> pieces)
        {
            if (pieces.Count == 0)
            {
                return null;
            }

            var result = new List<V3>(pieces[0]);
            if (pieces.Count > 1)
            {
                var next = pieces[1];
                double Gap(V3 a) => Math.Min((a - next[0]).Length, (a - next[^1]).Length);
                if (Gap(result[0]) < Gap(result[^1]))
                {
                    result.Reverse();
                }
            }

            for (var k = 1; k < pieces.Count; k++)
            {
                var piece = pieces[k];
                var tail = result[^1];
                var turn = (tail - piece[0]).Length > (tail - piece[^1]).Length;
                for (var i = 0; i < piece.Count; i++)
                {
                    var point = piece[turn ? piece.Count - 1 - i : i];
                    if (i == 0 && (tail - point).Length < 1e-9 * (1 + tail.Length))
                    {
                        continue;
                    }

                    result.Add(point);
                }
            }

            return result;
        }

        /// <summary>A parameter-space polyline (x = u, y = v), refined where it is straight across a surface that curves, with the surface points.</summary>
        private List<(double U, double V, V3 P)> FromParameters(List<V3> curve, IgesSurface surface)
        {
            var (stepU, stepV) = surface.Geometry.InteriorStep(_options.AngularDeflection);
            var result = new List<(double U, double V, V3 P)>(curve.Count);
            for (var i = 0; i < curve.Count; i++)
            {
                var (u, v) = (curve[i].X, curve[i].Y);
                if (i > 0)
                {
                    var (pu, pv) = (curve[i - 1].X, curve[i - 1].Y);
                    var pieces = 1;
                    if (stepU > 0)
                    {
                        pieces = Math.Max(pieces, (int)Math.Ceiling(Math.Abs(u - pu) / stepU));
                    }

                    if (stepV > 0)
                    {
                        pieces = Math.Max(pieces, (int)Math.Ceiling(Math.Abs(v - pv) / stepV));
                    }

                    pieces = Math.Min(pieces, 64);
                    for (var k = 1; k < pieces; k++)
                    {
                        var (iu, iv) = (pu + (u - pu) * k / pieces, pv + (v - pv) * k / pieces);
                        result.Add((iu, iv, surface.Geometry.At(iu, iv)));
                    }
                }

                result.Add((u, v, surface.Geometry.At(u, v)));
            }

            return result;
        }

        /// <summary>Model-space points, with their parameters found on the surface when it has a parameter domain (continued past the seam of a closed surface).</summary>
        private static List<(double U, double V, V3 P)> FromModelSpace(List<V3> curve, IgesSurface surface)
        {
            var result = new List<(double U, double V, V3 P)>(curve.Count);
            (double U, double V)? previous = null;
            double offsetU = 0, offsetV = 0;
            var periodU = surface.U1 - surface.U0;
            var periodV = surface.V1 - surface.V0;
            foreach (var p in curve)
            {
                if (surface.ParamDefined)
                {
                    var uv = surface.Geometry.Project(p, previous);
                    previous = uv;
                    var (u, v) = (uv.U + offsetU, uv.V + offsetV);
                    if (result.Count > 0)
                    {
                        var last = result[^1];
                        if (surface.ClosedU && u - last.U > periodU / 2)
                        {
                            offsetU -= periodU;
                            u -= periodU;
                        }
                        else if (surface.ClosedU && u - last.U < -periodU / 2)
                        {
                            offsetU += periodU;
                            u += periodU;
                        }

                        if (surface.ClosedV && v - last.V > periodV / 2)
                        {
                            offsetV -= periodV;
                            v -= periodV;
                        }
                        else if (surface.ClosedV && v - last.V < -periodV / 2)
                        {
                            offsetV += periodV;
                            v += periodV;
                        }
                    }

                    result.Add((u, v, p));
                }
                else
                {
                    result.Add((0, 0, p));
                }
            }

            return result;
        }

        /// <summary>
        /// Moves each piece of a loop by whole periods so that it starts where the one before it ended, and the loop as a whole into the domain:
        /// projecting onto a closed surface cannot tell the two sides of the seam apart.
        /// </summary>
        private static void AlignAcrossSeams(List<List<(double U, double V, V3 P)>> pieces, List<bool> projected, IgesSurface surface)
        {
            var periodU = surface.U1 - surface.U0;
            var periodV = surface.V1 - surface.V0;
            for (var k = 1; k < pieces.Count; k++)
            {
                if (!projected[k])
                {
                    continue; // parameter-space curves of the file already say where they are
                }

                var end = pieces[k - 1][^1];
                var piece = pieces[k];
                var du = surface.ClosedU && periodU > 0 ? Math.Round((end.U - piece[0].U) / periodU) * periodU : 0;
                var dv = surface.ClosedV && periodV > 0 ? Math.Round((end.V - piece[0].V) / periodV) * periodV : 0;
                if (du != 0 || dv != 0)
                {
                    for (var i = 0; i < piece.Count; i++)
                    {
                        piece[i] = (piece[i].U + du, piece[i].V + dv, piece[i].P);
                    }
                }
            }

            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var piece in pieces)
            {
                foreach (var point in piece)
                {
                    minU = Math.Min(minU, point.U);
                    maxU = Math.Max(maxU, point.U);
                    minV = Math.Min(minV, point.V);
                    maxV = Math.Max(maxV, point.V);
                }
            }

            if (projected.Contains(false))
            {
                return; // the file's own curves fix the loop's place; only a loop made wholly of projected edges is free to float
            }

            var shiftU = surface.ClosedU && periodU > 0 ? -Math.Round(((minU + maxU) / 2 - (surface.U0 + surface.U1) / 2) / periodU) * periodU : 0;
            var shiftV = surface.ClosedV && periodV > 0 ? -Math.Round(((minV + maxV) / 2 - (surface.V0 + surface.V1) / 2) / periodV) * periodV : 0;
            if (shiftU != 0 || shiftV != 0)
            {
                foreach (var piece in pieces)
                {
                    for (var i = 0; i < piece.Count; i++)
                    {
                        piece[i] = (piece[i].U + shiftU, piece[i].V + shiftV, piece[i].P);
                    }
                }
            }
        }

        /// <summary>The boundary of the parameter domain, counter-clockwise in (u, v).</summary>
        private FaceLoop Rectangle(IgesSurface surface)
        {
            var corners = new List<V3>
            {
                new(surface.U0, surface.V0, 0), new(surface.U1, surface.V0, 0), new(surface.U1, surface.V1, 0), new(surface.U0, surface.V1, 0), new(surface.U0, surface.V0, 0),
            };
            var refined = FromParameters(corners, surface);
            var points = new List<V3>(refined.Count);
            var uv = new List<(double U, double V)>(refined.Count);
            for (var i = 0; i < refined.Count - 1; i++)
            {
                if (i > 0 && (refined[i].P - points[^1]).Length < 1e-12 * (1 + points[^1].Length))
                {
                    continue; // a collapsed side
                }

                points.Add(refined[i].P);
                uv.Add((refined[i].U, refined[i].V));
            }

            return new FaceLoop(points, uv);
        }

        // ---- surfaces --------------------------------------------------------------------------------------------

        private IgesSurface? SurfaceOf(int pointer)
        {
            if (_surfaces.TryGetValue(pointer, out var cached))
            {
                return cached;
            }

            _surfaces[pointer] = null; // guards against cycles
            IgesSurface? result = null;
            if (_byIndex.TryGetValue(pointer, out var entry) && Parameters(entry) is { } f)
            {
                var m = TransformOf(entry);
                result = entry.Type switch
                {
                    128 => SplineSurfaceOf(f, m),
                    108 => PlaneOf(f, m),
                    190 => Quadric(f, m, 190),
                    192 => Quadric(f, m, 192),
                    194 => Quadric(f, m, 194),
                    196 => Quadric(f, m, 196),
                    198 => Quadric(f, m, 198),
                    120 => RevolutionOf(f, m),
                    122 => TabulatedOf(f, m),
                    _ => null,
                };
            }

            _surfaces[pointer] = result;
            return result;
        }

        private static IgesSurface Infinite(Surface surface) => new(surface, false, 0, 0, 0, 0);

        private IgesSurface? SplineSurfaceOf(List<string> f, Matrix4x4 matrix)
        {
            var k1 = Count(f, 1);
            var k2 = Count(f, 2);
            int m1 = Count(f, 3), m2 = Count(f, 4);
            if (m1 < 1 || m2 < 1 || m1 > 64 || m2 > 64 || k1 < m1 || k2 < m2 || k1 > 100_000 || k2 > 100_000)
            {
                return null;
            }

            int nu = k1 + 1, nv = k2 + 1;
            int knotsU = nu + m1 + 1, knotsV = nv + m2 + 1;
            long needed = 10L + knotsU + knotsV + (long)nu * nv * 4 + 4;
            if (needed > f.Count + 4 || (long)nu * nv > 4_000_000)
            {
                return null;
            }

            var o = 10;
            var ku = new double[knotsU];
            for (var i = 0; i < knotsU; i++)
            {
                ku[i] = Real(f, o + i);
                if (i > 0 && ku[i] < ku[i - 1])
                {
                    return null;
                }
            }

            o += knotsU;
            var kv = new double[knotsV];
            for (var i = 0; i < knotsV; i++)
            {
                kv[i] = Real(f, o + i);
                if (i > 0 && kv[i] < kv[i - 1])
                {
                    return null;
                }
            }

            o += knotsV;
            var weights = new double[nu, nv];
            for (var j = 0; j < nv; j++)
            {
                for (var i = 0; i < nu; i++)
                {
                    var w = Real(f, o + j * nu + i);
                    weights[i, j] = w > 0 ? w : 1;
                }
            }

            o += nu * nv;
            var control = new V3[nu, nv];
            for (var j = 0; j < nv; j++)
            {
                for (var i = 0; i < nu; i++)
                {
                    var b = o + 3 * (j * nu + i);
                    var p = new V3(Real(f, b), Real(f, b + 1), Real(f, b + 2));
                    control[i, j] = matrix.IsIdentity ? p : Xf(p, matrix);
                }
            }

            o += 3 * nu * nv;
            var surface = new SplineSurface { DegreeU = m1, DegreeV = m2, Control = control, Weights = weights, KnotsU = ku, KnotsV = kv };
            var (ru0, ru1) = surface.RangeU;
            var (rv0, rv1) = surface.RangeV;
            double u0 = Math.Max(Real(f, o), ru0), u1 = Math.Min(Real(f, o + 1), ru1), v0 = Math.Max(Real(f, o + 2), rv0), v1 = Math.Min(Real(f, o + 3), rv1);
            if (!(u1 > u0))
            {
                (u0, u1) = (ru0, ru1);
            }

            if (!(v1 > v0))
            {
                (v0, v1) = (rv0, rv1);
            }

            // A surface closed in a direction (a tube, a ball): its first and last rows of control points coincide, and boundaries that run round it
            // have parameters that continue past the end of the domain rather than start again.
            var closedU = (u1 - u0) >= (ru1 - ru0) * (1 - 1e-9);
            var closedV = (v1 - v0) >= (rv1 - rv0) * (1 - 1e-9);
            for (var j = 0; j < nv && closedU; j++)
            {
                closedU = (control[0, j] - control[nu - 1, j]).Length < 1e-9 * (1 + control[0, j].Length) && Math.Abs(weights[0, j] - weights[nu - 1, j]) < 1e-9;
            }

            for (var i = 0; i < nu && closedV; i++)
            {
                closedV = (control[i, 0] - control[i, nv - 1]).Length < 1e-9 * (1 + control[i, 0].Length) && Math.Abs(weights[i, 0] - weights[i, nv - 1]) < 1e-9;
            }

            return new IgesSurface(surface, true, u0, u1, v0, v1, closedU, closedV);
        }

        private static IgesSurface? PlaneOf(List<string> f, Matrix4x4 matrix)
        {
            var normal = new V3(Real(f, 1), Real(f, 2), Real(f, 3));
            var length = normal.Length;
            if (length < 1e-12)
            {
                return null;
            }

            var origin = normal * (Real(f, 4) / (length * length));
            if (!matrix.IsIdentity)
            {
                origin = Xf(origin, matrix);
                normal = XfDir(normal, matrix);
            }

            return Infinite(new PlaneSurface { F = Frame.From(origin, normal.Normalized(), null) });
        }

        private V3 PointEntity(int pointer, Matrix4x4 matrix)
        {
            var p = V3.Zero;
            if (_byIndex.TryGetValue(pointer, out var e) && e.Type == 116 && Parameters(e) is { } f)
            {
                p = new V3(Real(f, 1), Real(f, 2), Real(f, 3));
            }

            return matrix.IsIdentity ? p : Xf(p, matrix);
        }

        private V3? DirectionEntity(int pointer, Matrix4x4 matrix)
        {
            if (pointer == 0 || !_byIndex.TryGetValue(pointer, out var e) || e.Type != 123 || Parameters(e) is not { } f)
            {
                return null;
            }

            var d = new V3(Real(f, 1), Real(f, 2), Real(f, 3));
            if (!matrix.IsIdentity)
            {
                d = XfDir(d, matrix);
            }

            return d.Length > 1e-12 ? d.Normalized() : null;
        }

        private IgesSurface? Quadric(List<string> f, Matrix4x4 matrix, int type)
        {
            var origin = PointEntity(Ptr(f, 1), matrix);
            switch (type)
            {
                case 190:
                    return Infinite(new PlaneSurface { F = Frame.From(origin, DirectionEntity(Ptr(f, 2), matrix), DirectionEntity(Ptr(f, 3), matrix)) });
                case 192:
                    return Real(f, 3) > 0 ? Infinite(new CylinderSurface { F = Frame.From(origin, DirectionEntity(Ptr(f, 2), matrix), DirectionEntity(Ptr(f, 4), matrix)), R = Real(f, 3) * Scale(matrix) }) : null;
                case 194:
                    {
                        var angle = Real(f, 4) * Math.PI / 180; // the semi-angle is in degrees
                        return Real(f, 3) >= 0 && angle > 0 && angle < Math.PI / 2
                            ? Infinite(new ConeSurface { F = Frame.From(origin, DirectionEntity(Ptr(f, 2), matrix), DirectionEntity(Ptr(f, 5), matrix)), R = Real(f, 3) * Scale(matrix), Angle = angle })
                            : null;
                    }

                case 196:
                    return Real(f, 2) > 0 ? Infinite(new SphereSurface { F = Frame.From(origin, DirectionEntity(Ptr(f, 3), matrix), DirectionEntity(Ptr(f, 4), matrix)), R = Real(f, 2) * Scale(matrix) }) : null;
                default:
                    return Real(f, 3) > 0 && Real(f, 4) > 0
                        ? Infinite(new TorusSurface { F = Frame.From(origin, DirectionEntity(Ptr(f, 2), matrix), DirectionEntity(Ptr(f, 5), matrix)), Major = Real(f, 3) * Scale(matrix), Minor = Real(f, 4) * Scale(matrix) })
                        : null;
            }
        }

        private static double Scale(Matrix4x4 m) => m.IsIdentity ? 1 : Math.Max(Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13), 1e-12);

        private IgesSurface? RevolutionOf(List<string> f, Matrix4x4 matrix)
        {
            // L: the axis line, C: the generatrix, then the start and terminate angles.
            var axis = CurvePoints(Ptr(f, 1));
            var profile = CurvePoints(Ptr(f, 2));
            if (axis is null || profile is null || (axis[^1] - axis[0]).Length < 1e-12)
            {
                return null;
            }

            double a0 = Real(f, 3), a1 = Real(f, 4);
            if (!(a1 > a0))
            {
                a1 = a0 + 2 * Math.PI;
            }

            if (!matrix.IsIdentity)
            {
                for (var i = 0; i < profile.Count; i++)
                {
                    profile[i] = Xf(profile[i], matrix);
                }

                axis = [Xf(axis[0], matrix), Xf(axis[^1], matrix)];
            }

            var surface = new RevolutionSurface { Profile = new PolylineCurve { Points = profile }, AxisOrigin = axis[0], AxisDirection = (axis[^1] - axis[0]).Normalized() };
            return new IgesSurface(surface, false, a0, a1, 0, profile.Count - 1);
        }

        private IgesSurface? TabulatedOf(List<string> f, Matrix4x4 matrix)
        {
            var directrix = CurvePoints(Ptr(f, 1));
            if (directrix is null)
            {
                return null;
            }

            var end = new V3(Real(f, 2), Real(f, 3), Real(f, 4));
            if (!matrix.IsIdentity)
            {
                for (var i = 0; i < directrix.Count; i++)
                {
                    directrix[i] = Xf(directrix[i], matrix);
                }

                end = Xf(end, matrix);
            }

            var direction = end - directrix[0];
            var length = direction.Length;
            if (length < 1e-12)
            {
                return null;
            }

            var surface = new ExtrusionSurface { Profile = new PolylineCurve { Points = directrix }, Direction = direction * (1 / length) };
            return new IgesSurface(surface, false, 0, directrix.Count - 1, 0, length);
        }
    }
}
