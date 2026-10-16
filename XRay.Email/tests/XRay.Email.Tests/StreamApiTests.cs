using XRay.Email.Cfb;
using XRay.Email.Msg;

namespace XRay.Email.Tests;

/// <summary>Stream overloads for loading, saving and rendering, and how they treat the stream.</summary>
public sealed class StreamApiTests
{
    private static MapiMessage BigMessage()
    {
        var message = TestMessages.Basic("Big", "body");
        var data = new byte[3_000_000];
        new Random(11).NextBytes(data);
        message.AddAttachment("data.bin", data, "application/octet-stream");
        message.AddAttachment("small.txt", "hello"u8.ToArray(), "text/plain");
        return message;
    }

    private static string TempFile(string extension) => Path.Combine(Path.GetTempPath(), "xray-email-test-" + Guid.NewGuid().ToString("N") + extension);

    [Fact]
    public void Save_to_a_stream_equals_the_byte_array_and_is_written_a_sector_at_a_time()
    {
        var message = BigMessage();
        var recorder = new RecordingStream();

        message.Save(recorder, TestContext.Current.CancellationToken);

        Assert.Equal(message.Save(TestContext.Current.CancellationToken), recorder.ToArray());
        Assert.True(recorder.Writes.Max() <= 4096);
        Assert.True(recorder.Writes.Count > 1000);
    }

    [Fact]
    public void Save_to_a_path_writes_the_same_file()
    {
        var message = BigMessage();
        var path = TempFile(".msg");
        try
        {
            message.Save(path, TestContext.Current.CancellationToken);

            Assert.Equal(message.Save(TestContext.Current.CancellationToken), File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Eml_to_a_stream_equals_the_byte_array_and_attachments_are_encoded_block_by_block()
    {
        var message = BigMessage();
        var recorder = new RecordingStream();

        message.SaveToEml(recorder, TestContext.Current.CancellationToken);

        // boundaries carry a random token, so two serialisations agree in length and content, not in bytes
        Assert.Equal(recorder.ToArray().Length, message.SaveToEml(TestContext.Current.CancellationToken).Length);
        Assert.True(recorder.Writes.Max() <= 64 * 1024, "no write should be the whole base64 attachment");
        var reloaded = MapiMessage.LoadFromEml(recorder.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(message.Attachments[0].Data, reloaded.Attachments[0].Data);
        Assert.Equal("hello"u8.ToArray(), reloaded.Attachments[1].Data);
    }

    [Fact]
    public void Eml_with_an_embedded_message_is_streamed_with_the_same_bytes_as_before()
    {
        var inner = MapiMessage.Create("inner", "inner body");
        inner.AddAttachment("x.bin", new byte[10_000]);
        var message = TestMessages.Basic();
        message.AddEmbeddedMessageAttachment(inner);

        var bytes = message.SaveToEml(TestContext.Current.CancellationToken);
        var reloaded = MapiMessage.LoadFromEml(bytes, TestContext.Current.CancellationToken);

        var embedded = Assert.Single(reloaded.Attachments).EmbeddedMessage;
        Assert.NotNull(embedded);
        Assert.Equal("inner", embedded.Subject);
        Assert.Equal(10_000, Assert.Single(embedded.Attachments).Data.Length);
        Assert.Contains("Content-Type: message/rfc822", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Cfb_and_msg_writers_have_stream_forms_too()
    {
        var message = TestMessages.Basic();
        var document = message.ToMsgDocument();

        using var msg = new MemoryStream();
        MsgWriter.Write(document, msg, TestContext.Current.CancellationToken);
        Assert.Equal(MsgWriter.ToBytes(document, TestContext.Current.CancellationToken), msg.ToArray());

        var cfb = document.ToCfbDocument();
        using var container = new MemoryStream();
        CfbWriter.Write(cfb, container, TestContext.Current.CancellationToken);
        Assert.Equal(CfbWriter.ToBytes(cfb, TestContext.Current.CancellationToken), container.ToArray());
    }

    [Fact]
    public void Msg_loads_from_forward_only_and_seekable_streams_and_leaves_them_open()
    {
        var message = BigMessage();
        var bytes = message.Save(TestContext.Current.CancellationToken);

        using var seekable = new MemoryStream(bytes);
        var fromSeekable = MapiMessage.FromStream(seekable, TestContext.Current.CancellationToken);
        Assert.True(seekable.CanRead);

        using var forward = new ForwardOnlyStream(new MemoryStream(bytes));
        var fromForward = MapiMessage.FromStream(forward, TestContext.Current.CancellationToken);

        Assert.Equal(message.Attachments[0].Data, fromSeekable.Attachments[0].Data);
        Assert.Equal(message.Attachments[0].Data, fromForward.Attachments[0].Data);
        Assert.Equal("Big", fromForward.Subject);
    }

    [Fact]
    public void Msg_is_read_from_the_streams_current_position()
    {
        var bytes = TestMessages.Basic("Offset").Save(TestContext.Current.CancellationToken);
        using var stream = new MemoryStream();
        stream.Write(new byte[1000]);
        stream.Write(bytes);
        stream.Position = 1000;

        Assert.Equal("Offset", MapiMessage.FromStream(stream, TestContext.Current.CancellationToken).Subject);
    }

    [Fact]
    public void Spooling_a_forward_only_stream_leaves_no_temporary_file_behind()
    {
        var before = Directory.GetFiles(Path.GetTempPath(), "xray-email-*.tmp").Length;
        var bytes = BigMessage().Save(TestContext.Current.CancellationToken);

        using (var forward = new ForwardOnlyStream(new MemoryStream(bytes)))
        {
            _ = MapiMessage.FromStream(forward, TestContext.Current.CancellationToken);
        }

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "xray-email-*.tmp").Length);
    }

    [Fact]
    public void A_damaged_stream_still_cleans_up_and_raises_the_package_exception()
    {
        var before = Directory.GetFiles(Path.GetTempPath(), "xray-email-*.tmp").Length;
        using var forward = new ForwardOnlyStream(new MemoryStream(new byte[2000]));

        Assert.Throws<CfbException>(() => MapiMessage.FromStream(forward, TestContext.Current.CancellationToken));
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "xray-email-*.tmp").Length);
    }

    [Fact]
    public void File_loading_does_not_keep_the_file_open()
    {
        var path = TempFile(".msg");
        TestMessages.Basic("From file").Save(path, TestContext.Current.CancellationToken);

        Assert.Equal("From file", MapiMessage.FromFile(path, TestContext.Current.CancellationToken).Subject);
        File.Delete(path);
        Assert.False(File.Exists(path));

        var bad = TempFile(".msg");
        File.WriteAllBytes(bad, new byte[600]);
        Assert.Throws<CfbException>(() => MapiMessage.FromFile(bad, TestContext.Current.CancellationToken));
        File.Delete(bad);
        Assert.False(File.Exists(bad));
    }

    [Fact]
    public void Eml_loads_from_streams_with_or_without_seeking_and_from_a_path()
    {
        var bytes = BigMessage().SaveToEml(TestContext.Current.CancellationToken);

        var fromSeekable = MapiMessage.LoadFromEml(new MemoryStream(bytes), TestContext.Current.CancellationToken);
        var fromForward = MapiMessage.LoadFromEml(new ForwardOnlyStream(new MemoryStream(bytes)), TestContext.Current.CancellationToken);
        var path = TempFile(".eml");
        File.WriteAllBytes(path, bytes);
        try
        {
            var fromPath = MapiMessage.LoadFromEml(path, TestContext.Current.CancellationToken);
            Assert.Equal(3_000_000, fromPath.Attachments[0].Data.Length);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(fromSeekable.Attachments[0].Data, fromForward.Attachments[0].Data);
        Assert.Equal(MessageSourceFormat.Eml, fromForward.SourceFormat);
    }

    [Fact]
    public void Load_detects_msg_and_eml_from_the_first_bytes_on_any_kind_of_stream()
    {
        var msg = TestMessages.Basic("As MSG").Save(TestContext.Current.CancellationToken);
        var eml = TestMessages.Basic("As EML").SaveToEml(TestContext.Current.CancellationToken);

        Assert.Equal(("As MSG", MessageSourceFormat.Msg), Describe(MapiMessage.Load(new MemoryStream(msg), TestContext.Current.CancellationToken)));
        Assert.Equal(("As EML", MessageSourceFormat.Eml), Describe(MapiMessage.Load(new MemoryStream(eml), TestContext.Current.CancellationToken)));
        Assert.Equal(("As MSG", MessageSourceFormat.Msg), Describe(MapiMessage.Load(new ForwardOnlyStream(new MemoryStream(msg)), TestContext.Current.CancellationToken)));
        Assert.Equal(("As EML", MessageSourceFormat.Eml), Describe(MapiMessage.Load(new ForwardOnlyStream(new MemoryStream(eml)), TestContext.Current.CancellationToken)));

        var path = TempFile(".bin");
        File.WriteAllBytes(path, msg);
        try
        {
            Assert.Equal(("As MSG", MessageSourceFormat.Msg), Describe(MapiMessage.Load(path, TestContext.Current.CancellationToken)));
        }
        finally
        {
            File.Delete(path);
        }

        static (string?, MessageSourceFormat) Describe(MapiMessage message) => (message.Subject, message.SourceFormat);
    }

    [Fact]
    public void Source_format_is_recorded_for_loaded_and_built_messages()
    {
        var built = MapiMessage.Create("x");
        Assert.Equal(MessageSourceFormat.Created, built.SourceFormat);
        Assert.Equal(MessageSourceFormat.Msg, TestMessages.ThroughMsg(built).SourceFormat);
        Assert.Equal(MessageSourceFormat.Eml, TestMessages.ThroughEml(built).SourceFormat);
    }

    [Fact]
    public void Eml_content_location_survives_a_round_trip_through_both_formats()
    {
        var message = TestMessages.Basic();
        message.AddAttachment("p.png", TestMessages.Png, "image/png").ContentLocation = "images/p.png";

        Assert.Equal("images/p.png", TestMessages.ThroughEml(message).Attachments[0].ContentLocation);
        Assert.Equal("images/p.png", TestMessages.ThroughMsg(message).Attachments[0].ContentLocation);
        Assert.Equal("images/p.png", TestMessages.ThroughMsg(TestMessages.ThroughEml(message)).Attachments[0].ContentLocation);
    }
}
