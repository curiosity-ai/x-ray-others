using System.Buffers.Binary;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// A compact structural check of a GLB against the rules of the glTF 2.0 specification that writers get wrong:
/// chunk layout, accessor and buffer-view bounds, index ranges, unit normals, min/max, node and material references.
/// (The full Khronos validator was also run over the same outputs during development.)
/// </summary>
internal static class GlbValidation
{
    public static void Validate(byte[] glb, bool allowCompressed = false)
    {
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb));
        Assert.Equal((uint)glb.Length, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)));
        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        Assert.Equal(0, jsonLength % 4);
        var json = JsonDocument.Parse(glb.AsMemory(20, jsonLength)).RootElement;
        var binOffset = 20 + jsonLength + 8;
        var bin = 20 + jsonLength < glb.Length ? glb.AsSpan(binOffset) : [];

        var views = json.TryGetProperty("bufferViews", out var v) ? v.EnumerateArray().ToList() : [];
        var accessors = json.TryGetProperty("accessors", out var a) ? a.EnumerateArray().ToList() : [];
        foreach (var view in views.Where(v => !(allowCompressed && v.TryGetProperty("extensions", out _))))
        {
            Assert.InRange(view.GetProperty("byteOffset").GetInt32() + view.GetProperty("byteLength").GetInt32(), 0, bin.Length);
            Assert.Equal(0, view.GetProperty("byteOffset").GetInt32() % 4);
        }

        foreach (var accessor in accessors)
        {
            var view = views[accessor.GetProperty("bufferView").GetInt32()];
            Assert.True(view.TryGetProperty("byteLength", out _));
            var componentType = accessor.GetProperty("componentType").GetInt32();
            var size = componentType is 5126 or 5125 ? 4 : componentType is 5123 ? 2 : 1;
            var width = accessor.GetProperty("type").GetString() switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => throw new InvalidDataException() };
            var count = accessor.GetProperty("count").GetInt32();
            Assert.True(count > 0);
            Assert.True(count * size * width <= view.GetProperty("byteLength").GetInt32());
        }

        var nodes = json.TryGetProperty("nodes", out var n) ? n.GetArrayLength() : 0;
        var meshes = json.TryGetProperty("meshes", out var m) ? m.EnumerateArray().ToList() : [];
        var materials = json.TryGetProperty("materials", out var mat) ? mat.GetArrayLength() : 0;
        if (json.TryGetProperty("nodes", out var nodeArray))
        {
            foreach (var node in nodeArray.EnumerateArray())
            {
                if (node.TryGetProperty("mesh", out var meshRef))
                {
                    Assert.InRange(meshRef.GetInt32(), 0, meshes.Count - 1);
                }

                if (node.TryGetProperty("children", out var children))
                {
                    foreach (var child in children.EnumerateArray())
                    {
                        Assert.InRange(child.GetInt32(), 0, nodes - 1);
                    }
                }
            }
        }

        foreach (var mesh in meshes)
        {
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                var attributes = primitive.GetProperty("attributes");
                var position = accessors[attributes.GetProperty("POSITION").GetInt32()];
                var vertexCount = position.GetProperty("count").GetInt32();
                Assert.True(position.TryGetProperty("min", out _) && position.TryGetProperty("max", out _), "POSITION needs min/max");
                if (primitive.TryGetProperty("material", out var materialRef))
                {
                    Assert.InRange(materialRef.GetInt32(), 0, materials - 1);
                }

                var mode = primitive.TryGetProperty("mode", out var modeValue) ? modeValue.GetInt32() : 4;
                if (primitive.TryGetProperty("indices", out var indexRef) && !(allowCompressed && views[accessors[indexRef.GetInt32()].GetProperty("bufferView").GetInt32()].TryGetProperty("extensions", out _)))
                {
                    var accessor = accessors[indexRef.GetInt32()];
                    var view = views[accessor.GetProperty("bufferView").GetInt32()];
                    var offset = view.GetProperty("byteOffset").GetInt32();
                    var count = accessor.GetProperty("count").GetInt32();
                    Assert.Equal(0, count % (mode == 1 ? 2 : mode == 4 ? 3 : 1));
                    var wide = accessor.GetProperty("componentType").GetInt32() == 5125;
                    for (var i = 0; i < count; i++)
                    {
                        var index = wide ? BinaryPrimitives.ReadUInt32LittleEndian(bin.Slice(offset + 4 * i, 4)) : BinaryPrimitives.ReadUInt16LittleEndian(bin.Slice(offset + 2 * i, 2));
                        Assert.True(index < vertexCount, "index out of range");
                    }
                }

                if (attributes.TryGetProperty("NORMAL", out var normalRef) && accessors[normalRef.GetInt32()].GetProperty("componentType").GetInt32() == 5126)
                {
                    var accessor = accessors[normalRef.GetInt32()];
                    var offset = views[accessor.GetProperty("bufferView").GetInt32()].GetProperty("byteOffset").GetInt32();
                    Assert.Equal(vertexCount, accessor.GetProperty("count").GetInt32());
                    for (var i = 0; i < vertexCount; i++)
                    {
                        var x = BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(offset + 12 * i, 4));
                        var y = BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(offset + 12 * i + 4, 4));
                        var z = BinaryPrimitives.ReadSingleLittleEndian(bin.Slice(offset + 12 * i + 8, 4));
                        Assert.InRange(Math.Sqrt(x * x + y * y + z * z), 0.999, 1.001);
                    }
                }
            }
        }
    }
}
