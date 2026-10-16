using System.Diagnostics;
using System.Text;
using XRay.Email.Cfb;
using XRay.Email.Msg;

namespace XRay.Email.Tests;

/// <summary>
/// Every load, save, convert, render and copy operation honours its token: a token cancelled in
/// advance throws OperationCanceledException, one cancelled part-way stops promptly, and the
/// operation is not swallowed into a package exception.
/// </summary>
public sealed class CancellationTests
{
    private static CancellationToken Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    private static MapiMessage BigMessage()
    {
        var message = TestMessages.Basic("Big", "body");
        var data = new byte[4_000_000];
        new Random(3).NextBytes(data);
        message.AddAttachment("data.bin", data, "application/octet-stream");
        return message;
    }

    [Fact]
    public void Operations_given_a_cancelled_token_throw_operation_canceled()
    {
        var token = Cancelled();
        var message = TestMessages.WithHtml("<p>x</p>");
        var rtfMessage = TestMessages.WithRtf("{\\rtf1 x}");
        message.AddEmbeddedMessageAttachment(MapiMessage.Create("child"));
        var msg = message.Save(TestContext.Current.CancellationToken);
        var eml = message.SaveToEml(TestContext.Current.CancellationToken);
        var path = Path.Combine(Path.GetTempPath(), "xray-email-cancel-" + Guid.NewGuid().ToString("N") + ".msg");
        File.WriteAllBytes(path, msg);
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => MapiMessage.FromFile(path, token));
            Assert.ThrowsAny<OperationCanceledException>(() => MapiMessage.FromStream(new MemoryStream(msg), token));
            Assert.ThrowsAny<OperationCanceledException>(() => MapiMessage.FromStream(new ForwardOnlyStream(new MemoryStream(msg)), token));
            Assert.ThrowsAny<OperationCanceledException>(() => MapiMessage.Load(new MemoryStream(msg), token));
            Assert.ThrowsAny<OperationCanceledException>(() => MapiMessage.Load(path, token));
            Assert.ThrowsAny<OperationCanceledException>(() => MsgDocument.FromStream(new MemoryStream(msg), token));
            Assert.ThrowsAny<OperationCanceledException>(() => MsgReader.FromStream(new MemoryStream(msg), token));
            Assert.ThrowsAny<OperationCanceledException>(() => CfbReader.FromStream(new MemoryStream(msg), token));
            Assert.ThrowsAny<OperationCanceledException>(() => CfbReader.FromFile(path, token));
            Assert.ThrowsAny<OperationCanceledException>(() => MapiMessage.LoadFromEml(eml, token));
            Assert.ThrowsAny<OperationCanceledException>(() => MapiMessage.LoadFromEml(new MemoryStream(eml), token));
            Assert.ThrowsAny<OperationCanceledException>(() => MapiMessage.LoadFromEml(new ForwardOnlyStream(new MemoryStream(eml)), token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.Save(token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.Save(new MemoryStream(), token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.Save(path, token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.SaveToEml(token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.SaveToEml(new MemoryStream(), token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.SaveToEml(path, token));
            Assert.ThrowsAny<OperationCanceledException>(() => MsgWriter.ToBytes(message.ToMsgDocument(), token));
            Assert.ThrowsAny<OperationCanceledException>(() => CfbWriter.ToBytes(message.ToMsgDocument().ToCfbDocument(), token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.ToHtml(null, token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.WriteHtml(new MemoryStream(), null, token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.GetBestTextBody(token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.GetBestHtmlBody(token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.GetPreferredBodyKind(token));
            Assert.ThrowsAny<OperationCanceledException>(() => rtfMessage.GetRtfBody(token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.GetAttachmentsFlattened(null, token));
            Assert.ThrowsAny<OperationCanceledException>(() => CompressedRtf.TryDecompress(TestMessages.UncompressedRtf("{\\rtf1}"), token));

            var entry = message.GetAttachmentsFlattened(null, TestContext.Current.CancellationToken)[0];
            Assert.ThrowsAny<OperationCanceledException>(() => entry.OpenRead(token));
            Assert.ThrowsAny<OperationCanceledException>(() => entry.CopyTo(new MemoryStream(), token));
            Assert.ThrowsAny<OperationCanceledException>(() => entry.GetSize(token));
            Assert.ThrowsAny<OperationCanceledException>(() => message.Attachments[0].CopyTo(new MemoryStream(), token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Async_attachment_copy_given_a_cancelled_token_throws()
    {
        var message = TestMessages.Basic();
        message.AddAttachment("a.bin", new byte[100_000]);
        message.AddEmbeddedMessageAttachment(MapiMessage.Create("child"));

        foreach (var entry in message.GetAttachmentsFlattened(null, TestContext.Current.CancellationToken))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => entry.CopyToAsync(new MemoryStream(), Cancelled()));
        }
    }

    [Fact]
    public void Loading_a_large_msg_stops_soon_after_the_token_is_cancelled()
    {
        var bytes = BigMessage().Save(TestContext.Current.CancellationToken);
        using var source = new CancellationTokenSource();
        using var stream = new CancellingReadStream(new MemoryStream(bytes), source, cancelAfterBytes: 200_000);

        Assert.ThrowsAny<OperationCanceledException>(() => MapiMessage.FromStream(stream, source.Token));

        Assert.True(stream.BytesRead < bytes.Length / 2, $"read {stream.BytesRead} of {bytes.Length} bytes after cancelling at 200000");
    }

    [Fact]
    public void Loading_a_large_eml_stops_after_the_token_is_cancelled()
    {
        var eml = BigMessage().SaveToEml(TestContext.Current.CancellationToken);
        using var source = new CancellationTokenSource();
        using var stream = new CancellingReadStream(new MemoryStream(eml), source, cancelAfterBytes: 1);

        Assert.ThrowsAny<OperationCanceledException>(() => MapiMessage.LoadFromEml(stream, source.Token));

        Assert.True(stream.BytesRead <= 2 * (1 << 20));
    }

    [Fact]
    public void Saving_a_large_msg_stops_after_the_token_is_cancelled()
    {
        var message = BigMessage();
        var total = message.Save(TestContext.Current.CancellationToken).Length;
        using var source = new CancellationTokenSource();
        var recorder = new RecordingStream { OnFirstWrite = source.Cancel };

        Assert.ThrowsAny<OperationCanceledException>(() => message.Save(recorder, source.Token));

        Assert.True(recorder.Length < total / 2, $"{recorder.Length} of {total} bytes were written after cancelling");
    }

    [Fact]
    public void Saving_a_large_eml_stops_after_the_token_is_cancelled()
    {
        var message = BigMessage();
        var total = message.SaveToEml(TestContext.Current.CancellationToken).Length;
        using var source = new CancellationTokenSource();
        var recorder = new RecordingStream { OnFirstWrite = source.Cancel };

        Assert.ThrowsAny<OperationCanceledException>(() => message.SaveToEml(recorder, source.Token));

        Assert.True(recorder.Length < total / 2);
    }

    [Fact]
    public void Copying_a_large_attachment_stops_after_the_token_is_cancelled()
    {
        var message = BigMessage();
        var entry = message.GetAttachmentsFlattened(null, TestContext.Current.CancellationToken)[0];
        using var source = new CancellationTokenSource();
        var recorder = new RecordingStream { OnFirstWrite = source.Cancel };

        Assert.ThrowsAny<OperationCanceledException>(() => entry.CopyTo(recorder, source.Token));

        Assert.True(recorder.Length <= 2 * 81920);
    }

    [Fact]
    public void Rendering_a_huge_html_body_stops_promptly_when_cancelled_from_another_thread()
    {
        var message = TestMessages.WithHtml("<html><body>" + string.Concat(Enumerable.Repeat("<p class=\"a\">paragraph <b>text</b></p>", 1_500_000)) + "</body></html>");
        using var source = new CancellationTokenSource();
        source.CancelAfter(30);

        var clock = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => message.ToHtml(null, source.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Deriving_text_from_a_huge_html_body_stops_promptly_when_cancelled()
    {
        var message = TestMessages.WithHtml("<html><body>" + string.Concat(Enumerable.Repeat("<p>paragraph <b>text</b></p>", 1_500_000)) + "</body></html>");
        using var source = new CancellationTokenSource();
        source.CancelAfter(30);

        var clock = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => message.GetBestTextBody(source.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Rendering_a_huge_rtf_body_stops_promptly_when_cancelled()
    {
        var message = TestMessages.WithRtf("{\\rtf1\\ansi " + string.Concat(Enumerable.Repeat("some \\b bold\\b0 text\\par ", 1_500_000)) + "}");
        using var source = new CancellationTokenSource();
        source.CancelAfter(30);

        var clock = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => message.ToHtml(null, source.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void An_uncancelled_token_changes_nothing()
    {
        var message = TestMessages.Basic("Same", "body");
        message.AddAttachment("a.txt", "x"u8.ToArray(), "text/plain");
        using var source = new CancellationTokenSource();

        Assert.Equal(message.Save(TestContext.Current.CancellationToken), message.Save(source.Token));
        Assert.Equal(message.ToHtml(null, TestContext.Current.CancellationToken), message.ToHtml(null, source.Token));
        Assert.Equal(message.GetBestTextBody(TestContext.Current.CancellationToken), message.GetBestTextBody(source.Token));
        Assert.Equal(
            MapiMessage.FromStream(new MemoryStream(message.Save(TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken).Subject,
            MapiMessage.FromStream(new MemoryStream(message.Save(TestContext.Current.CancellationToken)), source.Token).Subject);
    }

    private sealed class CancellingReadStream(Stream inner, CancellationTokenSource source, int cancelAfterBytes) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        private int Count(int read)
        {
            BytesRead += read;
            if (BytesRead >= cancelAfterBytes)
            {
                source.Cancel();
            }

            return read;
        }
    }
}
