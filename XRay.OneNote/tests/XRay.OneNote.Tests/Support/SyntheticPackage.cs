using System.Buffers.Binary;
using System.Text;

namespace XRay.OneNote.Tests.Support;

/// <summary>
/// Writes a minimal OneNote revision store as an FSSHTTPB package (MS-ONESTORE 2.8, MS-FSSHTTPB 2.2.1): a section
/// with a name and pages, each with a title and one paragraph. It is the inverse of what the reader reads, so the
/// package reader can be tested without a corpus; it is not a file written by OneNote.
/// </summary>
internal sealed class SyntheticPackage
{
    public sealed record PageSpec(string Title, params string[] Paragraphs);

    private static readonly Guid DefaultContext = new("84DEFAB9-AAA3-4A0D-A3A8-520C77AC7073");
    private static readonly Guid HeaderRoot = new("1A5A319C-C26B-41AA-B9C5-9BD8C44E07D4");
    private static readonly Guid HeaderSpace = new("111E4CF3-7FEF-4087-AF6A-B9544ACD334D");
    private static readonly Guid RevisionRoot = new("4A3717F8-1C14-49E7-9526-81D942DE1741");
    private static readonly Guid SectionSchema = new("1F937CB4-B26F-445F-B9F8-17E20160E461");
    private static readonly Guid PackageFormat = new("638DE92F-A6D4-4BC1-9A36-B3FC2511A5B7");

    private readonly List<byte[]> _elements = [];
    private int _counter;

    private readonly record struct ExGuid(Guid Guid, uint N);

    /// <summary>An object's bytes with the object and object space references its property set makes.</summary>
    private sealed record Prop(byte[] Data, List<ExGuid> Objects, List<ExGuid> Spaces);

    private sealed record Cell(ExGuid Space, ExGuid Revision, ExGuid RevisionElement, ExGuid CellElement);

    public static byte[] Build(string sectionName, Guid fileGuid, IReadOnlyList<PageSpec> pages) =>
        new SyntheticPackage().Write(sectionName, fileGuid, pages);

    private ExGuid NewId(uint n = 1) => new(new Guid(++_counter, 0, 0, [9, 9, 9, 9, 9, 9, 9, 9]), n);

    private byte[] Write(string sectionName, Guid fileGuid, IReadOnlyList<PageSpec> pages)
    {
        var cells = new List<(ExGuid Space, Cell Cell)>();

        // Header cell: one object holding the file identity (partition 1 only).
        var headerSpace = new ExGuid(HeaderSpace, 1);
        var headerObject = NewId();
        var headerGroup = AddGroup([(headerObject, 1UL, PropSet(
            [(0x1C001D94u, Bytes(fileGuid.ToByteArray())), (0x1C001D95u, Bytes(Guid.Empty.ToByteArray()))], [], [], false))]);
        var headerCell = AddCell(headerSpace, [headerGroup], headerObject, withRoot: false);

        // Pages.
        var pageSpaces = new List<ExGuid>();
        foreach (var page in pages)
        {
            var space = NewId();
            pageSpaces.Add(space);
            cells.Add((space, AddPageCell(space, page)));
        }

        // Section space: a section node, a page series naming the page spaces, and the section metadata.
        var sectionSpace = NewId();
        var series = NewId();
        var sectionNode = NewId();
        var sectionMeta = NewId();
        var group = AddGroup(
        [
            (sectionNode, 4UL, Jcid(0x0007)),
            (sectionNode, 1UL, PropSet([(0x24001C20u, U32(1))], [series], [], true)),
            (series, 4UL, Jcid(0x0008)),
            (series, 1UL, PropSet([(0x2C001D63u, U32((uint)pageSpaces.Count))], [], pageSpaces, true)),
            (sectionMeta, 4UL, Jcid(0x0031)),
            (sectionMeta, 1UL, PropSet([(0x1C00349Bu, Bytes(Encoding.Unicode.GetBytes(sectionName + "\0")))], [], [], false)),
        ]);
        var sectionCell = AddCell(sectionSpace, [group], sectionNode, withRoot: true);
        cells.Insert(0, (sectionSpace, sectionCell));

        return Package(fileGuid, headerSpace, headerCell, sectionSpace, cells);
    }

    private Cell AddPageCell(ExGuid space, PageSpec page)
    {
        var manifest = NewId();
        var pageNode = NewId();
        var title = NewId();
        var titleText = NewId();
        var outline = NewId();
        var items = new List<(ExGuid, ulong, Prop)>();
        void Object(ExGuid id, ushort jcid, Prop props)
        {
            items.Add((id, 4UL, Jcid(jcid)));
            items.Add((id, 1UL, props));
        }

        var elements = new List<ExGuid>();
        var texts = new List<ExGuid>();
        foreach (var paragraph in page.Paragraphs)
        {
            var element = NewId();
            var text = NewId();
            elements.Add(element);
            texts.Add(text);
            Object(text, 0x000E, PropSet([(0x1C001C22u, Bytes(Encoding.Unicode.GetBytes(paragraph)))], [], [], false));
            Object(element, 0x000D, PropSet([(0x24001C1Fu, U32(1))], [text], [], false));
        }

        Object(manifest, 0x0037, PropSet([(0x24001C1Fu, U32(1))], [pageNode], [], false));
        Object(pageNode, 0x000B, PropSet([(0x24001C20u, U32(1)), (0x24001D5Fu, U32(1))], [outline, title], [], false));
        Object(outline, 0x000C, PropSet([(0x24001C20u, U32((uint)elements.Count))], elements, [], false));
        Object(title, 0x002C, PropSet([(0x24001C20u, U32(1))], [titleText], [], false));
        Object(titleText, 0x000E, PropSet([(0x1C001C22u, Bytes(Encoding.Unicode.GetBytes(page.Title)))], [], [], false));

        return AddCell(space, [AddGroup(items)], manifest, withRoot: true);
    }

    // ------------------------------------------------------------------ package structure

    private Cell AddCell(ExGuid space, List<ExGuid> groups, ExGuid root, bool withRoot)
    {
        var revision = NewId();
        var revisionElement = NewId();
        var cellElement = NewId();
        var body = new List<byte>();
        body.AddRange(Start16(0x1A, false, 0));
        body.AddRange(ExGuidBytes(revision));
        body.AddRange(ExGuidBytes(default));
        if (withRoot)
        {
            body.AddRange(Start16(0x0A, false, 0));
            body.AddRange(ExGuidBytes(new ExGuid(RevisionRoot, 1)));
            body.AddRange(ExGuidBytes(root));
        }

        foreach (var group in groups)
        {
            body.AddRange(Start16(0x19, false, 0));
            body.AddRange(ExGuidBytes(group));
        }

        _elements.Add(DataElement(revisionElement, 4, [.. body]));
        var cellBody = new List<byte>();
        cellBody.AddRange(Start16(0x0B, false, 0));
        cellBody.AddRange(ExGuidBytes(revision));
        _elements.Add(DataElement(cellElement, 3, [.. cellBody]));
        return new Cell(space, revision, revisionElement, cellElement);
    }

    private ExGuid AddGroup(IReadOnlyList<(ExGuid Id, ulong Partition, Prop Data)> items)
    {
        var id = NewId();
        var body = new List<byte>();
        body.AddRange(Start16(0x1D, true, 0));
        foreach (var (objectId, partition, data) in items)
        {
            var declaration = new List<byte>();
            declaration.AddRange(ExGuidBytes(objectId));
            declaration.AddRange(Compact(partition));
            declaration.AddRange(Compact((ulong)data.Data.Length));
            declaration.AddRange(Compact(0));
            declaration.AddRange(Compact(0));
            body.AddRange(Start32(0x18, false, declaration.Count));
            body.AddRange(declaration);
        }

        body.Add(End8(0x1D));
        body.AddRange(Start16(0x1E, true, 0));
        foreach (var (_, _, data) in items)
        {
            // Object data: references are carried as arrays, then the bytes as a binary item.
            var payload = new List<byte>();
            payload.AddRange(Compact((ulong)data.Objects.Count));
            foreach (var reference in data.Objects)
            {
                payload.AddRange(ExGuidBytes(reference));
            }

            payload.AddRange(Compact((ulong)data.Spaces.Count));
            foreach (var space in data.Spaces)
            {
                payload.AddRange(ExGuidBytes(new ExGuid(DefaultContext, 1)));
                payload.AddRange(ExGuidBytes(space));
            }

            payload.AddRange(Compact((ulong)data.Data.Length));
            payload.AddRange(data.Data);
            body.AddRange(Start32(0x16, false, payload.Count));
            body.AddRange(payload);
        }

        body.Add(End8(0x1E));
        _elements.Add(DataElement(id, 5, [.. body]));
        return id;
    }

    private static Prop Jcid(ushort index) => new(U32(index | (1u << 17)), [], []);

    private static Prop PropSet(IReadOnlyList<(uint Id, byte[] Data)> properties, List<ExGuid> objects, List<ExGuid> spaces, bool hasSpaces = false)
    {
        _ = hasSpaces;
        var bytes = new List<byte>();

        // Streams: compact ids stand in for the references (their values are not used for packages).
        bytes.AddRange(U32((uint)objects.Count));
        for (var i = 0; i < objects.Count; i++)
        {
            bytes.AddRange(U32(0));
        }

        bytes.AddRange(U32((uint)spaces.Count));
        for (var i = 0; i < spaces.Count; i++)
        {
            bytes.AddRange(U32(0));
        }

        bytes.AddRange(BitConverter.GetBytes((ushort)properties.Count));
        foreach (var (id, _) in properties)
        {
            bytes.AddRange(U32(id));
        }

        foreach (var (_, data) in properties)
        {
            bytes.AddRange(data);
        }

        return new Prop([.. bytes], objects, spaces);
    }

    private static byte[] Bytes(byte[] value) => [.. U32((uint)value.Length), .. value];

    private byte[] Package(Guid fileGuid, ExGuid headerSpace, Cell headerCell, ExGuid sectionSpace, List<(ExGuid Space, Cell Cell)> cells)
    {
        var storageIndexId = NewId();
        var manifestId = NewId();

        var index = new List<byte>();
        void Mapping(ExGuid space, ExGuid contextless, Cell cell)
        {
            _ = contextless;
            index.AddRange(Start16(0x0E, false, 0));
            index.AddRange(ExGuidBytes(new ExGuid(DefaultContext, 1)));
            index.AddRange(ExGuidBytes(space));
            index.AddRange(ExGuidBytes(cell.CellElement));
            index.AddRange(Serial());
            index.AddRange(Start16(0x0D, false, 0));
            index.AddRange(ExGuidBytes(cell.Revision));
            index.AddRange(ExGuidBytes(cell.RevisionElement));
            index.AddRange(Serial());
        }

        Mapping(headerSpace, default, headerCell);
        foreach (var (space, cell) in cells)
        {
            Mapping(space, default, cell);
        }

        var manifest = new List<byte>();
        manifest.AddRange(Start16(0x0C, false, 0));
        manifest.AddRange(SectionSchema.ToByteArray());
        manifest.AddRange(Start16(0x07, false, 0));
        manifest.AddRange(ExGuidBytes(new ExGuid(HeaderRoot, 1)));
        manifest.AddRange(ExGuidBytes(new ExGuid(DefaultContext, 1)));
        manifest.AddRange(ExGuidBytes(new ExGuid(HeaderSpace, 1)));
        manifest.AddRange(Start16(0x07, false, 0));
        manifest.AddRange(ExGuidBytes(new ExGuid(DefaultContext, 2)));
        manifest.AddRange(ExGuidBytes(new ExGuid(DefaultContext, 1)));
        manifest.AddRange(ExGuidBytes(sectionSpace));

        var all = new List<byte[]> { DataElement(storageIndexId, 1, [.. index]), DataElement(manifestId, 2, [.. manifest]) };
        all.AddRange(_elements);

        var file = new List<byte>();
        file.AddRange(new Guid("7B5C52E4-D88C-4DA7-AEB1-5378D02996D3").ToByteArray());
        file.AddRange(fileGuid.ToByteArray());
        file.AddRange(Guid.NewGuid().ToByteArray());
        file.AddRange(PackageFormat.ToByteArray());
        file.AddRange(U32(0));
        var packagingBody = new List<byte>();
        packagingBody.AddRange(ExGuidBytes(storageIndexId));
        packagingBody.AddRange(SectionSchema.ToByteArray());
        file.AddRange(Start32(0x7A, true, packagingBody.Count));
        file.AddRange(packagingBody);
        file.AddRange(Start16(0x15, true, 0));
        file.Add(0);
        foreach (var element in all)
        {
            file.AddRange(element);
        }

        file.Add(End8(0x15));
        file.AddRange(BitConverter.GetBytes((ushort)0x1EB));
        return [.. file];
    }

    private static byte[] DataElement(ExGuid id, ulong type, byte[] body)
    {
        var head = new List<byte>();
        head.AddRange(ExGuidBytes(id));
        head.AddRange(Serial());
        head.AddRange(Compact(type));
        var bytes = new List<byte>();
        bytes.AddRange(Start16(0x01, true, head.Count));
        bytes.AddRange(head);
        bytes.AddRange(body);
        bytes.Add(End8(0x01));
        return [.. bytes];
    }

    // ------------------------------------------------------------------ primitives

    private static byte[] Serial()
    {
        var bytes = new List<byte> { 0x80 };
        bytes.AddRange(Guid.Parse("00000000-0000-0000-0000-0000000000AA").ToByteArray());
        bytes.AddRange(BitConverter.GetBytes(1UL));
        return [.. bytes];
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Start16(uint type, bool compound, int length) =>
        BitConverter.GetBytes((ushort)((compound ? 4u : 0u) | (type << 3) | ((uint)length << 9)));

    private static byte[] Start32(uint type, bool compound, int length) =>
        U32(2u | (compound ? 4u : 0u) | (type << 3) | ((uint)length << 17));

    private static byte End8(uint type) => (byte)(1 | (type << 2));

    private static byte[] Compact(ulong value)
    {
        if (value == 0)
        {
            return [0];
        }

        if (value < 0x80)
        {
            return [(byte)((value << 1) | 1)];
        }

        if (value < 0x4000)
        {
            return BitConverter.GetBytes((ushort)((value << 2) | 2));
        }

        if (value < 0x200000)
        {
            var raw = (uint)((value << 3) | 4);
            return [(byte)raw, (byte)(raw >> 8), (byte)(raw >> 16)];
        }

        var wide = (value << 4) | 8;
        return [(byte)wide, (byte)(wide >> 8), (byte)(wide >> 16), (byte)(wide >> 24)];
    }

    private static byte[] ExGuidBytes(ExGuid value)
    {
        if (value.Guid == Guid.Empty && value.N == 0)
        {
            return [0];
        }

        var bytes = new List<byte>();
        if (value.N < 32)
        {
            bytes.Add((byte)(4 | (value.N << 3)));
        }
        else if (value.N < 1024)
        {
            bytes.AddRange(BitConverter.GetBytes((ushort)(32 | (value.N << 6))));
        }
        else if (value.N < 0x20000)
        {
            var raw = 64u | (value.N << 7);
            bytes.AddRange([(byte)raw, (byte)(raw >> 8), (byte)(raw >> 16)]);
        }
        else
        {
            bytes.Add(0x80);
            bytes.AddRange(U32(value.N));
        }

        bytes.AddRange(value.Guid.ToByteArray());
        return [.. bytes];
    }
}
