using System.Buffers.Binary;
using System.Text;

namespace XRay.OneNote.Tests.Support;

/// <summary>
/// Writes a minimal MS-ONESTORE file of the table of contents type: the inverse of what the parser reads
/// (header, file node list fragments, an object space with one revision, an object group of property
/// sets). It shows that <see cref="NotebookToc"/> reads what this reading of the format describes; it is
/// not a file written by OneNote.
/// </summary>
internal sealed class SyntheticToc
{
    public const uint ColorPropertyId = 0x1CBE;

    private readonly List<byte> _file = new(new byte[1024]);

    public sealed record Entry(string Name, Guid? Guid, uint Order, uint? Color);

    public static byte[] Build(Guid fileGuid, Guid ancestor, uint? notebookColor, bool? history, IReadOnlyList<Entry> entries, bool section = false)
    {
        var builder = new SyntheticToc();
        return builder.Write(fileGuid, ancestor, notebookColor, history, entries, section);
    }

    private static Guid GuidFor(int index) => new(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, (byte)index]);

    private byte[] Write(Guid fileGuid, Guid ancestor, uint? notebookColor, bool? history, IReadOnlyList<Entry> entries, bool section)
    {
        // Objects: 1 = root, 2.. = entries.
        var blobs = new List<(int Id, uint Jcid, long Offset, long Size)>();
        var rootBlob = RootBlob(entries.Count, notebookColor, history);
        blobs.Add((1, 0x00020001u | (1u << 17), Append(rootBlob), rootBlob.Length));
        for (var i = 0; i < entries.Count; i++)
        {
            var blob = EntryBlob(entries[i]);
            blobs.Add((i + 2, 0x00020001u | (1u << 17), Append(blob), blob.Length));
        }

        // Object group list.
        var groupNodes = new List<byte[]>();
        foreach (var (id, _, _, _) in blobs)
        {
            groupNodes.Add(Node(0x024, 0, 0, 0, [.. U32((uint)id), .. GuidFor(id).ToByteArray()]));
        }

        foreach (var (id, jcid, offset, size) in blobs)
        {
            groupNodes.Add(Node(0x0A4, 0, 0, 0, [.. Ref(offset, size), .. U32((uint)((id << 8) | 1)), .. U32(jcid)]));
        }

        var groupList = Fragment(groupNodes);
        var groupAt = Append(groupList);

        // Revision manifest list: start, gid table, root, group reference, end.
        var rootGuid = GuidFor(1);
        var revisionNodes = new List<byte[]>
        {
            Node(0x01B, 0, 0, 0, [.. rootGuid.ToByteArray(), .. U32(7), .. new byte[20]]),
            Node(0x024, 0, 0, 0, [.. U32(1), .. rootGuid.ToByteArray()]),
            Node(0x05A, 0, 0, 0, [.. rootGuid.ToByteArray(), .. U32(1), .. U32(1)]),
            Node(0x0B0, 0, 0, 0, [.. Ref(groupAt, groupList.Length)]),
            Node(0x01C, 0, 0, 0, []),
        };
        var revisionList = Fragment(revisionNodes);
        var revisionAt = Append(revisionList);

        var spaceNodes = new List<byte[]> { Node(0x010, 0, 0, 0, [.. Ref(revisionAt, revisionList.Length), .. new byte[20]]) };
        var spaceList = Fragment(spaceNodes);
        var spaceAt = Append(spaceList);

        var rootNodes = new List<byte[]> { Node(0x008, 0, 0, 0, [.. Ref(spaceAt, spaceList.Length), .. GuidFor(900).ToByteArray(), .. U32(1)]) };
        var rootList = Fragment(rootNodes);
        var rootAt = Append(rootList);

        var data = _file.ToArray();
        var type = section ? new Guid("7b5c52e4-d88c-4da7-aeb1-5378d02996d3") : new Guid("43ff2fa1-efd9-4c76-9ee2-10ea5722765f");
        type.ToByteArray().CopyTo(data, 0);
        fileGuid.ToByteArray().CopyTo(data, 16);
        new Guid("109add3f-911b-49f5-a5d0-1791edc8aed8").ToByteArray().CopyTo(data, 48);
        ancestor.ToByteArray().CopyTo(data, 128);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(172), (ulong)rootAt);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(180), (uint)rootList.Length);
        return data;
    }

    private long Append(byte[] bytes)
    {
        var at = _file.Count;
        _file.AddRange(bytes);
        return at;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Ref(long offset, long size)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)size);
        return bytes;
    }

    private static byte[] Node(int id, int stpFormat, int cbFormat, int baseType, byte[] payload)
    {
        var size = 4 + payload.Length;
        var header = (uint)id | ((uint)size << 10) | ((uint)stpFormat << 23) | ((uint)cbFormat << 25) | ((uint)baseType << 27);
        return [.. U32(header), .. payload];
    }

    private static byte[] Fragment(List<byte[]> nodes)
    {
        var body = nodes.SelectMany(n => n).ToArray();
        var length = 16 + body.Length + 8 + 20;
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 0xA4567AB1F5F7F4C4);
        body.CopyTo(bytes, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(length - 8), 0x8BC215C38233BA4B);
        return bytes;
    }

    // ------------------------------------------------------------------ property sets

    private static uint Id(uint id, int type, bool flag = false) => id | ((uint)type << 26) | (flag ? 1u << 31 : 0);

    private static byte[] Blob(List<uint> oids, List<uint> propertyIds, List<byte[]> values)
    {
        var bytes = new List<byte>();
        bytes.AddRange(U32((uint)oids.Count | (1u << 31)));
        foreach (var oid in oids)
        {
            bytes.AddRange(U32(oid));
        }

        bytes.AddRange(BitConverter.GetBytes((ushort)propertyIds.Count));
        foreach (var id in propertyIds)
        {
            bytes.AddRange(U32(id));
        }

        foreach (var value in values)
        {
            bytes.AddRange(value);
        }

        return [.. bytes];
    }

    private static byte[] RootBlob(int children, uint? color, bool? history)
    {
        var oids = Enumerable.Range(2, children).Select(i => (uint)((i << 8) | 1)).ToList();
        var ids = new List<uint>();
        var values = new List<byte[]>();
        if (color is { } c)
        {
            ids.Add(Id(ColorPropertyId, 5));
            values.Add(U32(c));
        }

        if (history is { } h)
        {
            ids.Add(Id(0x1E1E, 2, h));
        }

        ids.Add(Id(0x1D10, 9));
        values.Add(U32((uint)children));
        return Blob(oids, ids, values);
    }

    private static byte[] EntryBlob(Entry entry)
    {
        var ids = new List<uint>();
        var values = new List<byte[]>();
        if (entry.Guid is { } guid)
        {
            ids.Add(Id(0x1D00, 7));
            values.Add([.. U32(16), .. guid.ToByteArray()]);
        }

        var name = Encoding.Unicode.GetBytes(entry.Name + "\0");
        ids.Add(Id(0x1D01, 7));
        values.Add([.. U32((uint)name.Length), .. name]);
        ids.Add(Id(0x1D02, 5));
        values.Add(U32(entry.Order));
        if (entry.Color is { } color)
        {
            ids.Add(Id(ColorPropertyId, 5));
            values.Add(U32(color));
        }

        return Blob([], ids, values);
    }
}
