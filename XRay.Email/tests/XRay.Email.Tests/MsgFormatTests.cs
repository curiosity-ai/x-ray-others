using System.Buffers.Binary;
using System.Text;
using XRay.Email.Cfb;
using XRay.Email.Msg;

namespace XRay.Email.Tests;

/// <summary>MS-OXMSG and MS-CFB rules the writer and reader must follow.</summary>
public sealed class MsgFormatTests
{
    private const string EmbeddedPropertyStreamPath = "__attach_version1.0_#00000000/__substg1.0_3701000D/__properties_version1.0";

    private static MapiMessage WithEmbeddedMessage()
    {
        var child = MapiMessage.Create("Inner", "Inner body");
        child.SenderEmailAddress = "inner@example.com";
        child.AddRecipient("bob@example.com", "Bob");
        var parent = MapiMessage.Create("Outer", "Outer body");
        parent.AddEmbeddedMessageAttachment(child, "inner.msg");
        return parent;
    }

    private static byte[] StreamAt(byte[] file, string path)
    {
        using var reader = new CfbReader(file);
        var entry = reader.ResolvePath(path.Split('/'));
        Assert.NotNull(entry);
        return reader.GetStreamData(entry.StreamId);
    }

    [Fact]
    public void Embedded_message_property_stream_has_a_24_byte_header()
    {
        var file = WithEmbeddedMessage().Save();
        Assert.Equal(0, (StreamAt(file, EmbeddedPropertyStreamPath).Length - 24) % 16);
        Assert.Equal(0, (StreamAt(file, "__properties_version1.0").Length - 32) % 16);

        var loaded = MapiMessage.FromStream(new MemoryStream(file), strict: true);
        var embedded = Assert.Single(loaded.Attachments).EmbeddedMessage;
        Assert.NotNull(embedded);
        Assert.Equal("Inner", embedded.Subject);
        Assert.Equal("bob@example.com", Assert.Single(embedded.Recipients).EmailAddress);
    }

    [Fact]
    public void Embedded_message_with_the_older_32_byte_header_still_reads()
    {
        var document = WithEmbeddedMessage().ToMsgDocument();
        var embeddedRoot = document.Root.Storages.Single(item => item.Role == MsgStorageRole.Attachment).Storages.Single();
        var propertyStream = embeddedRoot.FindStream(MsgConstants.PropertyStreamName)!;
        propertyStream.Data = [.. propertyStream.Data.AsSpan(0, 24), .. new byte[8], .. propertyStream.Data.AsSpan(24)];

        var loaded = MapiMessage.FromStream(new MemoryStream(MsgWriter.ToBytes(document)));
        Assert.Equal("Inner", Assert.Single(loaded.Attachments).EmbeddedMessage?.Subject);
    }

    [Fact]
    public void Trailing_bytes_in_a_property_stream_are_reported_not_fatal()
    {
        var document = MapiMessage.Create("Trailing", "Body").ToMsgDocument();
        var propertyStream = document.Root.FindStream(MsgConstants.PropertyStreamName)!;
        propertyStream.Data = [.. propertyStream.Data, 0, 0, 0, 0];
        var file = MsgWriter.ToBytes(document);

        var loaded = MapiMessage.FromStream(new MemoryStream(file));
        Assert.Equal("Trailing", loaded.Subject);
        Assert.Contains(loaded.ValidationIssues, issue => issue.Contains("not divisible by 16", StringComparison.Ordinal));
        Assert.Throws<MsgException>(() => MapiMessage.FromStream(new MemoryStream(file), strict: true));
    }

    [Fact]
    public void Named_property_mapping_storage_has_its_three_streams()
    {
        using var reader = new CfbReader(MapiMessage.Create("x", "y").Save());
        var nameId = reader.ResolvePath([MsgConstants.NamedPropertyMappingStorageName]);
        Assert.NotNull(nameId);
        Assert.Equal(["__substg1.0_00020102", "__substg1.0_00030102", "__substg1.0_00040102"], reader.IterChildren(nameId.StreamId).Select(item => item.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FromMsgDocument_reads_a_document_built_in_memory()
    {
        var message = MapiMessage.Create("In memory", "Body");
        message.AddRecipient("bob@example.com", "Bob");
        message.AddAttachment("a.txt", "a"u8.ToArray());
        var copy = MapiMessage.FromMsgDocument(message.ToMsgDocument());
        Assert.Equal("In memory", copy.Subject);
        Assert.Equal("bob@example.com", Assert.Single(copy.Recipients).EmailAddress);
        Assert.Equal("a.txt", Assert.Single(copy.Attachments).Filename);
    }

    [Fact]
    public void Binary_html_body_is_decoded_with_the_internet_code_page()
    {
        var message = MapiMessage.Create("Html", "Body");
        message.SetProperty((ushort)CommonMessagePropertyId.InternetCodepage, (ushort)PropertyTypeCode.PtypInteger32, 1252);
        message.SetProperty((ushort)CommonMessagePropertyId.BodyHtml, (ushort)PropertyTypeCode.PtypBinary, Encoding.GetEncoding(1252).GetBytes("<p>café “quoted”</p>"));
        var loaded = MapiMessage.FromStream(new MemoryStream(message.Save()));
        Assert.Equal("<p>café “quoted”</p>", loaded.HtmlBody);

        loaded.HtmlBody = "<p>replaced</p>";
        Assert.Equal("<p>replaced</p>", MapiMessage.FromStream(new MemoryStream(loaded.Save())).HtmlBody);
        Assert.Single(loaded.IterPropertyKeys(), key => key.PropertyId == (ushort)CommonMessagePropertyId.BodyHtml);
    }

    [Fact]
    public void Setting_a_string_replaces_a_loaded_string8_value()
    {
        var ansi = MapiMessage.Create("Old subject", unicodeStrings: false);
        var loaded = MapiMessage.FromStream(new MemoryStream(ansi.Save()));
        loaded.UnicodeStrings = true;
        loaded.Subject = "New subject";
        Assert.Equal("New subject", loaded.Subject);
        Assert.Equal("New subject", MapiMessage.FromStream(new MemoryStream(loaded.Save())).Subject);
    }

    [Fact]
    public void String8_values_use_the_message_code_page()
    {
        var message = MapiMessage.Create(unicodeStrings: false);
        message.SetProperty(0x3FFD, (ushort)PropertyTypeCode.PtypInteger32, 1251);
        message.Subject = "Привет";
        var file = message.Save();
        var subjectStream = StreamAt(file, "__substg1.0_0037001E");
        Assert.Equal(Encoding.GetEncoding(1251).GetBytes("Привет"), subjectStream);
        Assert.Equal("Привет", MapiMessage.FromStream(new MemoryStream(file)).Subject);
    }

    [Fact]
    public void Multi_valued_properties_are_written_and_read()
    {
        var message = MapiMessage.Create("Multi", "Body");
        message.SetProperty(0x9000, (ushort)PropertyTypeCode.PtypMultipleString, new[] { "alpha", "βήτα" });
        message.SetProperty(0x9001, (ushort)PropertyTypeCode.PtypMultipleBinary, new[] { new byte[] { 1, 2 }, new byte[] { 3 } });
        var loaded = MapiMessage.FromStream(new MemoryStream(message.Save()));
        Assert.Equal(new[] { "alpha", "βήτα" }, loaded.GetPropertyValue(0x9000, (ushort)PropertyTypeCode.PtypMultipleString));
        var blobs = Assert.IsType<byte[][]>(loaded.GetPropertyValue(0x9001, (ushort)PropertyTypeCode.PtypMultipleBinary));
        Assert.Equal([new byte[] { 1, 2 }, new byte[] { 3 }], blobs);
    }

    [Fact]
    public void Exchange_addresses_resolve_to_smtp()
    {
        var message = MapiMessage.Create("EX", "Body");
        message.SenderAddressType = "EX";
        message.SenderEmailAddress = "/O=EXAMPLE/OU=EXCHANGE/CN=RECIPIENTS/CN=ALICE";
        message.SetProperty(0x5D01, (ushort)PropertyTypeCode.PtypString, "alice@example.com");
        var recipient = message.AddRecipient("/O=EXAMPLE/OU=EXCHANGE/CN=RECIPIENTS/CN=BOB", "Bob");
        recipient.AddressType = "EX";
        recipient.Properties.Add(0x39FE, (ushort)PropertyTypeCode.PtypString, "bob@example.com");

        var loaded = MapiMessage.FromStream(new MemoryStream(message.Save()));
        Assert.Equal("alice@example.com", loaded.SenderSmtpAddress);
        Assert.Equal("bob@example.com", Assert.Single(loaded.Recipients).SmtpAddress);

        var eml = Encoding.ASCII.GetString(loaded.SaveToEml());
        Assert.Contains("From: alice@example.com", eml);
        Assert.Contains("To: Bob <bob@example.com>", eml);
    }

    [Fact]
    public void Compressed_rtf_body_is_decompressed()
    {
        // MS-OXRTFCP 3.1.1, and a run-heavy body compressed by an independent implementation.
        var message = MapiMessage.Create("Rtf", "Body");
        message.SetProperty(0x1009, (ushort)PropertyTypeCode.PtypBinary, Convert.FromHexString(
            "2d0000002b0000004c5a4675f1c5c7a703000a007263706731323542320af32068656c090020627705b06c647d0a800fa0"));
        Assert.Equal("{\\rtf1\\ansi\\ansicpg1252\\pard hello world}\r\n", message.RtfBody);

        message.SetProperty(0x1009, (ushort)PropertyTypeCode.PtypBinary, Convert.FromHexString(
            "74000000cf0200004c5a46759aa7cd8f8b000a01032009707065610540ff0e1f0e4f0e0f0e3f0e6f0e2f0e5f0e1fff0e4f0e0f0e3f0e6f0e2f0e5f0e1f0e4fff0e0f0e3f0e6f0e2f0e5f0e1f0e4f0e0fff0e3f0e6f0e2f0e5f0e1f0e4f0e0f0e3fff0e6f0e2f0e5f0e1f0e4f0e0f0e3f0e6f050e2b7d39e0"));
        Assert.Equal("{\\rtf1\\ansi\\deff0 " + string.Concat(Enumerable.Repeat("repeat repeat repeat repeat repeat ", 20)) + "}", message.RtfBody);

        message.SetProperty(0x1009, (ushort)PropertyTypeCode.PtypBinary, new byte[] { 1, 2, 3 });
        Assert.Null(message.RtfBody);
    }

    [Fact]
    public void Cfb_version_4_round_trips()
    {
        var message = MapiMessage.Create("v4", "Body");
        message.MajorVersion = 4;
        message.AddAttachment("big.bin", new byte[100_000]);
        var file = message.Save();
        using (var reader = new CfbReader(file))
        {
            Assert.Equal(4, reader.MajorVersion);
            Assert.Equal(4096, reader.SectorSize);
        }

        Assert.Equal(100_000, Assert.Single(MapiMessage.FromStream(new MemoryStream(file)).Attachments).Data.Length);
    }

    [Fact]
    public void Damaged_directory_entries_raise_cfb_exceptions()
    {
        var file = MapiMessage.Create("x", "y").Save();
        var directoryOffset = (int)((BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x30, 4)) + 1) * 512);

        // A name length past the 64-byte name field.
        var longName = file.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(longName.AsSpan(directoryOffset + 128 + 64, 2), 0xFFFF);
        using (var reader = new CfbReader(longName))
        {
            Assert.True(reader.DirectoryEntries[1].Name.Length <= 31);
        }

        // The root's first child pointing back at the root: a cycle.
        var cyclic = file.ToArray();
        var rootChild = BinaryPrimitives.ReadUInt32LittleEndian(cyclic.AsSpan(directoryOffset + 76, 4));
        var childEntry = directoryOffset + ((int)rootChild * 128);
        cyclic[childEntry + 66] = 1; // make it a storage
        BinaryPrimitives.WriteUInt32LittleEndian(cyclic.AsSpan(childEntry + 76, 4), 0);
        var exception = Record.Exception(() => MapiMessage.FromStream(new MemoryStream(cyclic)));
        Assert.True(exception is CfbException or MsgException, $"unexpected {exception?.GetType().Name}: {exception?.Message}");
        using var cyclicReader = new CfbReader(cyclic);
        Assert.Throws<CfbException>(() => cyclicReader.IterTree().ToArray());
    }
}
