using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>
/// Autodesk 3D Studio (.3ds): named objects with vertices, triangle faces, per-face materials and (when present) texture coordinates
/// (ignored), material diffuse colours, the master scale. Vertices are stored in world space, so the objects are siblings; the
/// keyframe hierarchy is read for object names only.
/// </summary>
internal static class Max3dsReader
{
    private const ushort Main = 0x4D4D;
    private const ushort Editor = 0x3D3D;
    private const ushort MasterScale = 0x0100;
    private const ushort Object = 0x4000;
    private const ushort TriMesh = 0x4100;
    private const ushort VertexList = 0x4110;
    private const ushort FaceList = 0x4120;
    private const ushort FaceMaterial = 0x4130;
    private const ushort MeshMatrix = 0x4160;
    private const ushort Material = 0xAFFF;
    private const ushort MaterialName = 0xA000;
    private const ushort MaterialDiffuse = 0xA020;
    private const ushort ColorFloat = 0x0010;
    private const ushort ColorByte = 0x0011;
    private const ushort ColorByteGamma = 0x0012;
    private const ushort ColorFloatGamma = 0x0013;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var s = data.ToArray();
        if (s.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan()) != Main)
        {
            throw new ModelFormatException("Not a 3DS file.");
        }

        var model = new Model3D();
        var materials = new Dictionary<string, int>(StringComparer.Ordinal);
        var meshes = new List<(string Name, MeshData Mesh, List<(string Material, List<int> Faces)> Assignments)>();
        double scale = 1;
        var end = (int)Math.Min(s.Length, BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(2)));
        long triangles = 0;

        void Walk(int position, int limit, ushort parent, Action<ushort, int, int>? visit)
        {
            while (position + 6 <= limit)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                var id = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(position));
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan((position + 2)));
                if (length < 6 || position + length > limit)
                {
                    throw new ModelFormatException("A 3DS chunk runs past its parent.");
                }

                visit?.Invoke(id, position + 6, position + length);
                position += length;
            }
        }

        Walk(6, end, 0, (id, body, chunkEnd) =>
        {
            if (id != Editor)
            {
                return;
            }

            Walk(body, chunkEnd, Editor, (eid, ebody, eend) =>
            {
                switch (eid)
                {
                    case MasterScale:
                        if (ebody + 4 <= eend)
                        {
                            scale = BinaryPrimitives.ReadSingleLittleEndian(s.AsSpan(ebody));
                        }

                        break;
                    case Material:
                        ReadMaterial(s, ebody, eend, model, materials);
                        break;
                    case Object:
                        {
                            var nameEnd = ebody;
                            while (nameEnd < eend && s[nameEnd] != 0)
                            {
                                nameEnd++;
                            }

                            var name = Encoding.Latin1.GetString(s.AsSpan(ebody, nameEnd - ebody));
                            Walk(nameEnd + 1, eend, Object, (oid, obody, oend) =>
                            {
                                if (oid != TriMesh)
                                {
                                    return;
                                }

                                var mesh = new MeshData { Name = name };
                                var assignments = new List<(string, List<int>)>();
                                Matrix4x4? local = null;
                                Walk(obody, oend, TriMesh, (mid, mbody, mend) =>
                                {
                                    switch (mid)
                                    {
                                        case VertexList:
                                            {
                                                var count = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(mbody));
                                                if (mbody + 2 + count * 12 > mend)
                                                {
                                                    throw new ModelFormatException("A 3DS vertex list is truncated.");
                                                }

                                                mesh.Positions = new float[count * 3];
                                                for (var i = 0; i < count * 3; i++)
                                                {
                                                    mesh.Positions[i] = BinaryPrimitives.ReadSingleLittleEndian(s.AsSpan((mbody + 2 + 4 * i)));
                                                }

                                                break;
                                            }

                                        case FaceList:
                                            {
                                                var count = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(mbody));
                                                if (mbody + 2 + count * 8 > mend)
                                                {
                                                    throw new ModelFormatException("A 3DS face list is truncated.");
                                                }

                                                triangles += count;
                                                if (triangles > options.MaxTriangles)
                                                {
                                                    throw new ModelFormatException("The 3DS has more triangles than the configured limit.");
                                                }

                                                mesh.Indices = new uint[count * 3];
                                                for (var i = 0; i < count; i++)
                                                {
                                                    for (var k = 0; k < 3; k++)
                                                    {
                                                        mesh.Indices[3 * i + k] = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan((mbody + 2 + 8 * i + 2 * k)));
                                                    }
                                                }

                                                // Sub-chunks after the faces (material assignment, smoothing groups).
                                                Walk(mbody + 2 + count * 8, mend, FaceList, (fid, fbody, fend) =>
                                                {
                                                    if (fid != FaceMaterial)
                                                    {
                                                        return;
                                                    }

                                                    var matEnd = fbody;
                                                    while (matEnd < fend && s[matEnd] != 0)
                                                    {
                                                        matEnd++;
                                                    }

                                                    var material = Encoding.Latin1.GetString(s.AsSpan(fbody, matEnd - fbody));
                                                    var n = matEnd + 1 + 2 <= fend ? BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan((matEnd + 1))) : 0;
                                                    var faces = new List<int>();
                                                    for (var i = 0; i < n && matEnd + 3 + 2 * i + 2 <= fend; i++)
                                                    {
                                                        faces.Add(BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan((matEnd + 3 + 2 * i))));
                                                    }

                                                    assignments.Add((material, faces));
                                                });
                                                break;
                                            }

                                        case MeshMatrix:
                                            if (mbody + 48 <= mend)
                                            {
                                                var f = new float[12];
                                                for (var i = 0; i < 12; i++)
                                                {
                                                    f[i] = BinaryPrimitives.ReadSingleLittleEndian(s.AsSpan((mbody + 4 * i)));
                                                }

                                                local = new Matrix4x4(f[0], f[1], f[2], 0, f[3], f[4], f[5], 0, f[6], f[7], f[8], 0, f[9], f[10], f[11], 1);
                                            }

                                            break;
                                    }
                                });
                                _ = local;
                                if (mesh.Positions.Length > 0)
                                {
                                    meshes.Add((name, mesh, assignments));
                                }
                            });
                            break;
                        }
                }
            });
        });

        if (meshes.Count == 0)
        {
            throw new ModelFormatException("The 3DS file holds no meshes.");
        }

        foreach (var (name, mesh, assignments) in meshes)
        {
            if (mesh.Indices.Any(i => i >= mesh.VertexCount))
            {
                throw new ModelFormatException("A 3DS face refers to a vertex that does not exist.");
            }

            if (assignments.Count > 0)
            {
                // Reorder faces so each material is contiguous.
                var faceMaterial = new int[mesh.TriangleCount];
                Array.Fill(faceMaterial, -1);
                foreach (var (material, faces) in assignments)
                {
                    var index = materials.GetValueOrDefault(material, -1);
                    foreach (var f in faces.Where(f => f < faceMaterial.Length))
                    {
                        faceMaterial[f] = index;
                    }
                }

                var order = Enumerable.Range(0, faceMaterial.Length).OrderBy(f => faceMaterial[f]).ToArray();
                var indices = new uint[mesh.Indices.Length];
                var groupStart = 0;
                for (var k = 0; k < order.Length; k++)
                {
                    Array.Copy(mesh.Indices, order[k] * 3, indices, k * 3, 3);
                    if (k + 1 == order.Length || faceMaterial[order[k + 1]] != faceMaterial[order[k]])
                    {
                        mesh.Groups.Add(new MeshGroup(groupStart * 3, (k + 1 - groupStart) * 3, faceMaterial[order[k]]));
                        groupStart = k + 1;
                    }
                }

                mesh.Indices = indices;
            }

            model.Meshes.Add(mesh);
            model.Roots.Add(new SceneNode(name) { Mesh = mesh });
        }

        var meta = model.Metadata;
        meta.Format = ModelFormats.Max3ds;
        meta.DocumentKind = "scene";
        meta.UpAxis = UpAxis.Z;
        meta.PartNames.AddRange(meshes.Select(m => m.Name));
        meta.MaterialNames.AddRange(materials.OrderBy(m => m.Value).Select(m => m.Key));
        if (Math.Abs(scale - 1) > 1e-6 && scale > 0)
        {
            meta.Properties.Add(new ModelProperty("Master scale", scale.ToString(System.Globalization.CultureInfo.InvariantCulture), "3ds"));
        }

        meta.HasGeometry = true;
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        return model;
    }

    private static void ReadMaterial(ReadOnlySpan<byte> s, int position, int limit, Model3D model, Dictionary<string, int> materials)
    {
        var name = "";
        var color = new Vector4(0.8f, 0.8f, 0.8f, 1f);
        while (position + 6 <= limit)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(s[position..]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[(position + 2)..]);
            if (length < 6 || position + length > limit)
            {
                throw new ModelFormatException("A 3DS material chunk runs past its parent.");
            }

            var body = position + 6;
            var end = position + length;
            if (id == MaterialName)
            {
                var n = body;
                while (n < end && s[n] != 0)
                {
                    n++;
                }

                name = Encoding.Latin1.GetString(s.Slice(body, n - body));
            }
            else if (id == MaterialDiffuse && body + 6 <= end)
            {
                var cid = BinaryPrimitives.ReadUInt16LittleEndian(s[body..]);
                var cb = body + 6;
                if (cid is ColorFloat or ColorFloatGamma && cb + 12 <= end)
                {
                    color = new Vector4(BinaryPrimitives.ReadSingleLittleEndian(s[cb..]), BinaryPrimitives.ReadSingleLittleEndian(s[(cb + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(s[(cb + 8)..]), 1f);
                }
                else if (cid is ColorByte or ColorByteGamma && cb + 3 <= end)
                {
                    color = new Vector4(s[cb] / 255f, s[cb + 1] / 255f, s[cb + 2] / 255f, 1f);
                }
            }

            position = end;
        }

        materials[name] = model.Materials.Count;
        model.Materials.Add(new ModelMaterial { Name = name, BaseColor = color });
    }
}
