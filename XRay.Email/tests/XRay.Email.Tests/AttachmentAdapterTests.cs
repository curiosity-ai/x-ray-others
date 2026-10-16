using System.Text;
using XRay.Email.Msg;

namespace XRay.Email.Tests;

/// <summary>GetAttachmentsFlattened: names, types, sizes, inline flags, embedded messages and nesting.</summary>
public sealed class AttachmentAdapterTests
{
    private static IReadOnlyList<MessageAttachment> Flatten(MapiMessage message, AttachmentFlattenOptions? options = null) =>
        message.GetAttachmentsFlattened(options, TestContext.Current.CancellationToken);

    private static byte[] Read(MessageAttachment attachment)
    {
        using var stream = attachment.OpenRead(TestContext.Current.CancellationToken);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static MapiMessage Embedded(string subject, string body = "inner body")
    {
        var inner = MapiMessage.Create(subject, body);
        inner.SenderEmailAddress = "inner@example.com";
        return inner;
    }

    [Fact]
    public void Each_attachment_reports_name_type_size_content_id_and_bytes()
    {
        var message = TestMessages.Basic();
        message.AddAttachment("report.pdf", [1, 2, 3, 4], "application/pdf");
        message.AddAttachment("logo.png", TestMessages.Png, "image/png", "<logo@x>");

        var list = Flatten(message);

        Assert.Equal(["report.pdf", "logo.png"], list.Select(item => item.Name));
        Assert.Equal(["application/pdf", "image/png"], list.Select(item => item.ContentType));
        Assert.Equal([4L, TestMessages.Png.Length], list.Select(item => item.Size));
        Assert.Equal([null, "logo@x"], list.Select(item => item.ContentId));
        Assert.Equal<byte[]>([1, 2, 3, 4], Read(list[0]));
        Assert.All(list, item => Assert.Equal(0, item.Depth));
        Assert.Equal("report.pdf", list[0].Path);
    }

    [Fact]
    public void File_names_are_made_safe_and_unique_case_insensitively()
    {
        var message = TestMessages.Basic();
        message.AddAttachment("a.txt", [1]);
        message.AddAttachment("A.TXT", [2]);
        message.AddAttachment("a.txt", [3]);
        message.AddAttachment("..\\..\\evil/name:with*bad?chars\"<>|.txt", [4]);
        message.AddAttachment("trailing dots...", [5]);
        message.AddAttachment("con\u0001trol.txt", [6]);

        var names = Flatten(message).Select(item => item.Name).ToArray();

        Assert.Equal(["a.txt", "A (2).TXT", "a (3).txt", ".._.._evil_name_with_bad_chars____.txt", "trailing dots", "con_trol.txt"], names);
        Assert.All(names, name => Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c)));
    }

    [Fact]
    public void Missing_names_are_synthesised_from_the_type_or_the_bytes()
    {
        var message = TestMessages.Basic();
        message.AddAttachment("", [1], "application/pdf");
        message.AddAttachment("   ", TestMessages.Png);
        message.AddAttachment("", [1, 2]);

        var list = Flatten(message);

        Assert.Equal(["attachment-1.pdf", "attachment-2.png", "attachment-3"], list.Select(item => item.Name));
        Assert.Equal(["application/pdf", "image/png", "application/octet-stream"], list.Select(item => item.ContentType));
    }

    [Fact]
    public void Content_type_falls_back_to_the_extension()
    {
        var message = TestMessages.Basic();
        message.AddAttachment("sheet.xlsx", [1]);
        message.AddAttachment("page.HTML", [1]);
        message.AddAttachment("noext", [1]);

        Assert.Equal(
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "text/html", "application/octet-stream"],
            Flatten(message).Select(item => item.ContentType));
    }

    [Fact]
    public void Very_long_names_are_shortened_but_keep_their_extension()
    {
        var message = TestMessages.Basic();
        message.AddAttachment(new string('x', 400) + ".docx", [1]);

        var name = Assert.Single(Flatten(message)).Name;

        Assert.True(name.Length <= 120);
        Assert.EndsWith(".docx", name);
    }

    [Fact]
    public void Inline_means_the_body_refers_to_it_or_it_is_hidden()
    {
        var message = TestMessages.WithHtml("<img src=\"cid:used\">");
        message.AddAttachment("used.png", TestMessages.Png, "image/png", "used");
        message.AddAttachment("unused.png", TestMessages.Png, "image/png", "unused");
        message.AddAttachment("plain.txt", [1], "text/plain");
        var hidden = message.AddAttachment("hidden.png", TestMessages.Png, "image/png", "hiddenid");
        hidden.Properties.Add(0x7FFE, (ushort)PropertyTypeCode.PtypBoolean, true);

        var list = Flatten(message);

        Assert.Equal([true, false, false, true], list.Select(item => item.IsInline));
        Assert.Equal([false, false, false, true], list.Select(item => item.IsHidden));
        Assert.Equal(["used.png", "unused.png", "plain.txt", "hidden.png"], Flatten(message, new AttachmentFlattenOptions { IncludeInline = true }).Select(item => item.Name));
        Assert.Equal(["unused.png", "plain.txt"], Flatten(message, new AttachmentFlattenOptions { IncludeInline = false, IncludeHidden = false }).Select(item => item.Name));
        Assert.Equal(["used.png", "unused.png", "plain.txt"], Flatten(message, new AttachmentFlattenOptions { IncludeHidden = false }).Select(item => item.Name));
    }

    [Fact]
    public void Embedded_messages_are_named_from_their_subject_with_the_format_extension()
    {
        var message = MapiMessage.Create("outer");
        message.AddEmbeddedMessageAttachment(Embedded("Re: lunch? <today>"), "ignored.msg");
        message.AddEmbeddedMessageAttachment(Embedded("Re: lunch? <today>"));
        message.AddEmbeddedMessageAttachment(Embedded(""), "from-filename.msg");
        message.AddEmbeddedMessageAttachment(Embedded(""), null);

        var msg = Flatten(message);
        var eml = Flatten(message, new AttachmentFlattenOptions { EmbeddedFormat = EmbeddedMessageFormat.Eml });

        Assert.Equal(["Re_ lunch_ _today_.msg", "Re_ lunch_ _today_ (2).msg", "from-filename.msg", "message.msg"], msg.Select(item => item.Name));
        Assert.Equal(["Re_ lunch_ _today_.eml", "Re_ lunch_ _today_ (2).eml", "from-filename.eml", "message.eml"], eml.Select(item => item.Name));
        Assert.All(msg, item => Assert.Equal("application/vnd.ms-outlook", item.ContentType));
        Assert.All(eml, item => Assert.Equal("message/rfc822", item.ContentType));
        Assert.All(msg, item => Assert.True(item.IsEmbeddedMessage));
    }

    [Fact]
    public void Default_format_follows_the_parent_msg_for_msg_and_eml_for_eml()
    {
        var parent = MapiMessage.Create("outer");
        parent.AddEmbeddedMessageAttachment(Embedded("child"));

        Assert.Equal("child.msg", Assert.Single(Flatten(parent)).Name);
        Assert.Equal("child.msg", Assert.Single(Flatten(TestMessages.ThroughMsg(parent))).Name);

        var viaEml = TestMessages.ThroughEml(parent);
        Assert.Equal(MessageSourceFormat.Eml, viaEml.SourceFormat);
        var entry = Assert.Single(Flatten(viaEml));
        Assert.Equal("child.eml", entry.Name);
        Assert.Equal("message/rfc822", entry.ContentType);
        Assert.Equal("child", MapiMessage.LoadFromEml(Read(entry), TestContext.Current.CancellationToken).Subject);
    }

    [Fact]
    public void Embedded_message_bytes_load_back_in_either_format()
    {
        var message = MapiMessage.Create("outer");
        var inner = Embedded("child", "child body");
        inner.AddAttachment("deep.txt", "deep"u8.ToArray(), "text/plain");
        message.AddEmbeddedMessageAttachment(inner);

        var msgBytes = Read(Assert.Single(Flatten(message)));
        var emlBytes = Read(Assert.Single(Flatten(message, new AttachmentFlattenOptions { EmbeddedFormat = EmbeddedMessageFormat.Eml })));

        var fromMsg = MapiMessage.FromStream(new MemoryStream(msgBytes), TestContext.Current.CancellationToken);
        var fromEml = MapiMessage.LoadFromEml(emlBytes, TestContext.Current.CancellationToken);
        Assert.Equal("child", fromMsg.Subject);
        Assert.Equal("child body", fromMsg.Body);
        Assert.Equal("child", fromEml.Subject);
        Assert.Equal("child body", fromEml.Body?.Trim());
        Assert.Equal("deep.txt", Assert.Single(fromMsg.Attachments).Filename);
        Assert.Equal("deep.txt", Assert.Single(fromEml.Attachments).Filename);
    }

    [Fact]
    public async Task Embedded_message_copy_streams_straight_into_the_destination_and_size_matches()
    {
        var message = MapiMessage.Create("outer");
        var inner = Embedded("big child");
        inner.AddAttachment("data.bin", new byte[2_000_000], "application/octet-stream");
        message.AddEmbeddedMessageAttachment(inner);
        var entry = Assert.Single(Flatten(message));

        var recorder = new RecordingStream();
        entry.CopyTo(recorder, TestContext.Current.CancellationToken);

        Assert.Equal(inner.Save(TestContext.Current.CancellationToken), recorder.ToArray());
        Assert.True(recorder.Writes.Max() <= 4096, "an MSG is written a sector at a time, not assembled in memory");
        Assert.Equal(recorder.ToArray().LongLength, entry.Size);

        using var viaAsync = new MemoryStream();
        await entry.CopyToAsync(viaAsync, TestContext.Current.CancellationToken);
        Assert.Equal(recorder.ToArray(), viaAsync.ToArray());

        using var read = entry.OpenRead(TestContext.Current.CancellationToken);
        Assert.Equal(entry.Size, read.Length);
    }

    [Fact]
    public void Eml_size_is_the_size_of_the_eml_serialisation()
    {
        var message = MapiMessage.Create("outer");
        message.AddEmbeddedMessageAttachment(Embedded("child"));

        var entry = Assert.Single(Flatten(message, new AttachmentFlattenOptions { EmbeddedFormat = EmbeddedMessageFormat.Eml }));

        Assert.Equal(message.Attachments[0].EmbeddedMessage!.SaveToEml(TestContext.Current.CancellationToken).LongLength, entry.Size);
    }

    [Fact]
    public async Task Plain_attachment_copy_is_chunked_and_async_copy_matches()
    {
        var message = TestMessages.Basic();
        var data = new byte[300_000];
        new Random(5).NextBytes(data);
        message.AddAttachment("data.bin", data);
        var entry = Assert.Single(Flatten(message));

        var recorder = new RecordingStream();
        entry.CopyTo(recorder, TestContext.Current.CancellationToken);
        using var viaAsync = new MemoryStream();
        await entry.CopyToAsync(viaAsync, TestContext.Current.CancellationToken);

        Assert.Equal(data, recorder.ToArray());
        Assert.Equal(data, viaAsync.ToArray());
        Assert.True(recorder.Writes.Count > 1);
    }

    [Fact]
    public void Nested_messages_are_flattened_to_the_requested_depth_with_paths()
    {
        var level2 = Embedded("Level 2");
        level2.AddAttachment("inner.txt", "i"u8.ToArray(), "text/plain");
        var level1 = Embedded("Level 1");
        level1.AddEmbeddedMessageAttachment(level2);
        level1.AddAttachment("middle.txt", "m"u8.ToArray(), "text/plain");
        var top = MapiMessage.Create("Top");
        top.AddEmbeddedMessageAttachment(level1);
        top.AddAttachment("outer.txt", "o"u8.ToArray(), "text/plain");

        string[] Paths(int depth) => Flatten(top, new AttachmentFlattenOptions { MaxDepth = depth }).Select(item => item.Path).ToArray();

        Assert.Equal(["Level 1.msg", "outer.txt"], Paths(0));
        Assert.Equal(["Level 1.msg", "Level 1.msg/Level 2.msg", "Level 1.msg/middle.txt", "outer.txt"], Paths(1));
        Assert.Equal(["Level 1.msg", "Level 1.msg/Level 2.msg", "Level 1.msg/Level 2.msg/inner.txt", "Level 1.msg/middle.txt", "outer.txt"], Paths(2));
        Assert.Equal(Paths(2), Paths(99));
        var deep = Flatten(top, new AttachmentFlattenOptions { MaxDepth = 2 });
        Assert.Equal([0, 1, 2, 1, 0], deep.Select(item => item.Depth));
        Assert.Same(level2, deep[2].Owner);
    }

    [Fact]
    public void Names_are_unique_across_the_whole_flattened_listing()
    {
        var inner = Embedded("child");
        inner.AddAttachment("note.txt", [1]);
        var top = MapiMessage.Create("Top");
        top.AddAttachment("note.txt", [2]);
        top.AddEmbeddedMessageAttachment(inner);

        var names = Flatten(top, new AttachmentFlattenOptions { MaxDepth = 1 }).Select(item => item.Name).ToArray();

        Assert.Equal(["note.txt", "child.msg", "note (2).txt"], names);
    }

    [Fact]
    public void Embedded_messages_can_be_left_out_while_their_attachments_are_still_listed()
    {
        var inner = Embedded("child");
        inner.AddAttachment("kept.txt", [1]);
        var top = MapiMessage.Create("Top");
        top.AddEmbeddedMessageAttachment(inner);

        var list = Flatten(top, new AttachmentFlattenOptions { IncludeEmbeddedMessages = false, MaxDepth = 1 });

        Assert.Equal(["kept.txt"], list.Select(item => item.Name));
    }

    [Fact]
    public void Storage_attachments_without_a_readable_payload_read_as_empty()
    {
        var message = MapiMessage.Create("Top");
        message.Attachments.Add(new MapiAttachment { Filename = "ole.bin", AttachMethod = MapiMessage.AttachMethodStorage });

        var entry = Assert.Single(Flatten(message));

        Assert.False(entry.HasContent);
        Assert.Equal(0, entry.Size);
        Assert.Equal(0, entry.OpenRead(TestContext.Current.CancellationToken).Length);
    }

    [Fact]
    public void Fixture_with_nested_messages_lists_every_level()
    {
        var message = MapiMessage.FromFile(TestDocuments.RequireFile("email/xray-email/msg/nested-embedded-messages.msg"), TestContext.Current.CancellationToken);

        var paths = Flatten(message, new AttachmentFlattenOptions { MaxDepth = 5 }).Select(item => item.Path).ToArray();

        Assert.Equal(["Level 1.msg", "Level 1.msg/Level 2.msg", "Level 1.msg/Level 2.msg/inner.txt", "outer.txt"], paths);
    }

    [Fact]
    public void Fixture_eml_with_a_forwarded_message_names_it_as_eml()
    {
        var message = MapiMessage.LoadFromEml(TestDocuments.RequireFile("email/xray-email/nested-rfc822.eml"), TestContext.Current.CancellationToken);

        var list = Flatten(message, new AttachmentFlattenOptions { MaxDepth = 3 });

        Assert.Contains(list, item => item.IsEmbeddedMessage && item.Name.EndsWith(".eml", StringComparison.Ordinal) && item.ContentType == "message/rfc822");
        Assert.All(list, item => Assert.True(item.Size >= 0));
    }

    [Fact]
    public void Names_in_non_latin_scripts_are_kept()
    {
        var message = TestMessages.Basic();
        message.AddAttachment("отчёт 2024.txt", Encoding.UTF8.GetBytes("x"), "text/plain");

        Assert.Equal("отчёт 2024.txt", Assert.Single(Flatten(message)).Name);
    }
}
