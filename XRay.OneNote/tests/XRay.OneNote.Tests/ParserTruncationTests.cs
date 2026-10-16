using System.Buffers.Binary;
using XRay.OneNote.Internal.OneStore;

namespace XRay.OneNote.Tests;

/// <summary>Best-effort reads of damaged structures (ported from the Python parser truncation tests).</summary>
public sealed class ParserTruncationTests
{
    // An object blob header with oid_count = 0 and the no-OSID-stream flag (bit 31) set, so parsing
    // goes straight to the property set.
    private static readonly byte[] BlobHeader = U32(0x80000000);

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    // A property id carrying an inline uint32 value (property type 0x5 in bits 26-30).
    private static uint UInt32Property(uint propertyId) => (0x5u << 26) | propertyId;

    [Fact]
    public void Truncated_guid_is_zero_padded()
    {
        byte[] payload = [0, 0, 0, 0, 0x11, 0x22, 0x33];
        Assert.Equal(new Guid([0x11, 0x22, 0x33, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), Bytes.Guid(payload, 4));
    }

    [Fact]
    public void Full_guid_is_read_unchanged()
    {
        var guidBytes = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[] payload = [0, 0, 0, 0, .. guidBytes];
        Assert.Equal(new Guid(guidBytes), Bytes.Guid(payload, 4));
    }

    [Fact]
    public void Truncated_array_count_ends_the_property_set()
    {
        // One ObjectIDArray (type 0x9) property whose 4-byte count would sit past the end of the buffer.
        byte[] blob = [.. BlobHeader, .. U16(1), .. U32(0x24000001)];
        var properties = PropertySetParser.ParseObjectBlob(blob, []);
        Assert.Equal(0, properties.Count);
        Assert.Empty(properties.Raw);
    }

    [Fact]
    public void Properties_before_a_truncation_are_kept()
    {
        byte[] blob =
        [
            .. BlobHeader, .. U16(2), .. U32(UInt32Property(0x1C01)), .. U32(UInt32Property(0x1C02)),
            .. U32(42), .. U16(7),
        ];
        var properties = PropertySetParser.ParseObjectBlob(blob, []);
        Assert.Equal(1, properties.Count);
        Assert.Equal(42UL, properties.Get("PageWidth").IntegerValue);
        Assert.Equal(["PageWidth"], properties.Raw.Select(p => p.Name));
    }

    [Fact]
    public void Overlong_property_count_is_clamped()
    {
        byte[] blob = [.. BlobHeader, .. U16(1000)];
        var properties = PropertySetParser.ParseObjectBlob(blob, []);
        Assert.Equal(0, properties.Count);
    }

    [Fact]
    public void Too_short_blob_raises_the_format_exception() =>
        Assert.Throws<FileCorruptedException>(() => PropertySetParser.ParseObjectBlob([1, 2], []));

    [Fact]
    public void Duplicate_properties_merge_into_a_list()
    {
        byte[] blob =
        [
            .. BlobHeader, .. U16(2), .. U32(UInt32Property(0x1C01)), .. U32(UInt32Property(0x1C01)),
            .. U32(1), .. U32(2),
        ];
        var properties = PropertySetParser.ParseObjectBlob(blob, []);
        var value = properties.Get("PageWidth");
        Assert.Equal(PropertyKind.List, value.Kind);
        Assert.Equal([1UL, 2UL], value.Items.Select(item => item.IntegerValue));
        Assert.Equal(2, properties.Raw.Count);
    }

    [Fact]
    public void Unnamed_property_ids_are_keyed_by_hex()
    {
        byte[] blob = [.. BlobHeader, .. U16(1), .. U32(UInt32Property(0x1ABC)), .. U32(5)];
        Assert.Equal(5UL, PropertySetParser.ParseObjectBlob(blob, []).Get("0x1abc").IntegerValue);
    }

    [Fact]
    public void Deeply_nested_property_sets_are_bounded()
    {
        // 200,000 property sets each holding the next (type 0x11): without a nesting bound this
        // would overflow the stack, which no exception handler can catch.
        var nested = new List<byte>(BlobHeader);
        for (var level = 0; level < 200_000; level++)
        {
            nested.AddRange(U16(1));
            nested.AddRange(U32((0x11u << 26) | 0x1C01));
        }

        var properties = PropertySetParser.ParseObjectBlob([.. nested], []);
        var depth = 0;
        for (var current = properties; current.Get("PageWidth") is { Kind: PropertyKind.PropertySet } inner; current = inner.SetValue!)
        {
            depth++;
        }

        Assert.InRange(depth, 1, 64);
    }

    [Fact]
    public void Small_files_are_rejected_as_corrupted() =>
        Assert.Throws<FileCorruptedException>(() => new Document(new MemoryStream(new byte[1000])));

    [Fact]
    public void Other_files_are_rejected_as_unsupported()
    {
        var error = Assert.Throws<UnsupportedFileFormatException>(() => new Document(new MemoryStream(new byte[4096])));
        Assert.Equal(FileFormat.Unknown, error.FileFormat);
    }
}
