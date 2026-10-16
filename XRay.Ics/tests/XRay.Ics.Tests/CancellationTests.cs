using System.Diagnostics;
using System.Text;

namespace XRay.Ics.Tests;

public sealed class CancellationTests
{
    private static readonly string Small = Tok.Cal("BEGIN:VEVENT", "UID:c", "SUMMARY:x", "DTSTART:20260105T100000Z", "RRULE:FREQ=DAILY;COUNT=3", "ATTACH;ENCODING=BASE64;VALUE=BINARY;FILENAME=a.bin:SGVsbG8=", "END:VEVENT");

    private static CancellationToken Cancelled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    private static string BigCalendar(int events)
    {
        var builder = new StringBuilder(events * 180);
        builder.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//big//EN\r\n");
        for (var i = 0; i < events; i++)
        {
            builder.Append("BEGIN:VEVENT\r\nUID:e").Append(i).Append("@example.com\r\nDTSTART:20260105T100000Z\r\nDTEND:20260105T110000Z\r\nSUMMARY:Event number ").Append(i)
                .Append("\r\nDESCRIPTION:Some description text for event ").Append(i).Append("\r\nORGANIZER;CN=Boss:mailto:boss@example.com\r\nATTENDEE;CN=A:mailto:a@example.com\r\nEND:VEVENT\r\n");
        }

        builder.Append("END:VCALENDAR\r\n");
        return builder.ToString();
    }

    [Fact]
    public void Pre_cancelled_tokens_stop_every_entry_point()
    {
        var token = Cancelled();
        var bytes = Encoding.UTF8.GetBytes(Small);
        var calendar = Calendar.Parse(Small, null, Tok.Ct);
        var path = Path.Combine(Path.GetTempPath(), "xray-ics-" + Guid.NewGuid().ToString("N") + ".ics");
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.Throws<OperationCanceledException>(() => Calendar.Load(bytes, null, token));
            Assert.Throws<OperationCanceledException>(() => Calendar.Load(new MemoryStream(bytes), null, token));
            Assert.Throws<OperationCanceledException>(() => Calendar.Parse(Small, null, token));
            Assert.Throws<OperationCanceledException>(() => Calendar.LoadFile(path, null, token));
            Assert.Throws<OperationCanceledException>(() => Calendar.LoadAll(new MemoryStream(bytes), null, token));
            Assert.Throws<OperationCanceledException>(() => calendar.Save(new MemoryStream(), token));
            Assert.Throws<OperationCanceledException>(() => calendar.ToBytes(token));
            Assert.Throws<OperationCanceledException>(() => calendar.ToIcsString(token));
            Assert.Throws<OperationCanceledException>(() => calendar.Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(50), 10, null, token));
            Assert.Throws<OperationCanceledException>(() => calendar.Events[0].Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(50), 10, null, token).ToList());
            Assert.Throws<OperationCanceledException>(() => RecurrenceRule.Parse("FREQ=DAILY").Enumerate(new DateTime(2026, 1, 1), null, token).ToList());
            Assert.Throws<OperationCanceledException>(() => IcsRenderer.ToHtml(calendar, null, token));
            Assert.Throws<OperationCanceledException>(() => IcsRenderer.ToPlainText(calendar, null, token));
            Assert.Throws<OperationCanceledException>(() => IcsRenderer.ToMarkdown(calendar, null, token));
            Assert.Throws<OperationCanceledException>(() => IcsExtractor.Extract(bytes, null, token));
            Assert.Throws<OperationCanceledException>(() => IcsExtractor.Extract(new MemoryStream(bytes), null, token));
            Assert.Throws<OperationCanceledException>(() => IcsExtractor.Extract(Small, null, token));
            Assert.Throws<OperationCanceledException>(() => IcsExtractor.ExtractFile(path, null, token));
            var result = IcsExtractor.Extract(Small, null, Tok.Ct);
            Assert.Throws<OperationCanceledException>(() => result.ToHtml(null, token));
            Assert.Throws<OperationCanceledException>(() => result.ToHtml(new MemoryStream(), null, token));
            Assert.Throws<OperationCanceledException>(() => result.ToPlainText(null, token));
            Assert.Throws<OperationCanceledException>(() => result.ToMarkdown(null, token));
            Assert.Throws<OperationCanceledException>(() => result.Attachments[0].WriteTo(new MemoryStream(), token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Results_with_a_live_token_equal_the_defaults()
    {
        using var source = new CancellationTokenSource();
        var plain = IcsExtractor.Extract(Small, null, Tok.Ct);
        var withToken = IcsExtractor.Extract(Small, null, source.Token);
        Assert.Equal(plain.ToHtml(null, Tok.Ct), withToken.ToHtml(null, source.Token));
        Assert.Equal(plain.ToPlainText(null, Tok.Ct), withToken.ToPlainText(null, source.Token));
        Assert.Equal(plain.ToMarkdown(null, Tok.Ct), withToken.ToMarkdown(null, source.Token));
        Assert.Equal(plain.Calendar.ToBytes(Tok.Ct), withToken.Calendar.ToBytes(source.Token));
        Assert.Equal(plain.Calendar.Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(60), 1000, null, Tok.Ct).Select(o => o.Start), withToken.Calendar.Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(60), 1000, null, source.Token).Select(o => o.Start));
    }

    [Fact]
    public void Cancelling_a_large_parse_returns_promptly()
    {
        var text = BigCalendar(150_000);
        using var source = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        source.CancelAfter(30);
        Assert.ThrowsAny<OperationCanceledException>(() => Calendar.Parse(text, null, source.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
        // The same input still loads with a live token.
        Assert.Equal(150_000, Calendar.Parse(text, null, Tok.Ct).Events.Count);
    }

    [Fact]
    public void Cancelling_a_stream_read_returns_promptly()
    {
        var bytes = Encoding.UTF8.GetBytes(BigCalendar(100_000));
        using var source = new CancellationTokenSource();
        using var stream = new CancelOnReadStream(new MemoryStream(bytes), source, 200_000);
        Assert.ThrowsAny<OperationCanceledException>(() => Calendar.Load(stream, null, source.Token));
    }

    [Fact]
    public void Cancelling_a_save_returns_promptly()
    {
        var calendar = Calendar.Parse(BigCalendar(100_000), null, Tok.Ct);
        using var source = new CancellationTokenSource();
        using var sink = new CancelOnWriteStream(source, 100_000);
        Assert.ThrowsAny<OperationCanceledException>(() => calendar.Save(sink, source.Token));
        Assert.True(sink.Written < 200_000_000);
    }

    [Fact]
    public void Cancelling_an_endless_expansion_returns_promptly()
    {
        // Each rule never matches, so each expansion would walk a million periods; twenty of them take far longer than the delay.
        var events = Enumerable.Range(0, 20).SelectMany(i => new[] { "BEGIN:VEVENT", "UID:s" + i, "DTSTART:20260105T100000Z", "RRULE:FREQ=SECONDLY;BYMONTH=2;BYMONTHDAY=30", "END:VEVENT" });
        var calendar = Tok.Load(Tok.Cal([.. events]));
        using var source = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        source.CancelAfter(20);
        Assert.ThrowsAny<OperationCanceledException>(() => calendar.Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(500), 100, null, source.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
        Assert.ThrowsAny<OperationCanceledException>(() => calendar.Events[0].Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(500), 100, null, source.Token).ToList());
    }

    [Fact]
    public void Cancelling_a_large_render_returns_promptly()
    {
        var calendar = Calendar.Parse(BigCalendar(100_000), null, Tok.Ct);
        using var source = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        source.CancelAfter(5);
        Assert.ThrowsAny<OperationCanceledException>(() => IcsRenderer.ToHtml(calendar, new IcsRenderOptions { AllComponents = true }, source.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
    }

    [Fact]
    public void Cancelling_a_large_calendar_expansion_returns_promptly()
    {
        var calendar = Calendar.Parse(BigCalendar(100_000), null, Tok.Ct);
        using var source = new CancellationTokenSource();
        source.CancelAfter(5);
        Assert.ThrowsAny<OperationCanceledException>(() => calendar.Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100), int.MaxValue, null, source.Token));
    }

    private sealed class CancelOnWriteStream(CancellationTokenSource source, long threshold) : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Written;

        public override long Position
        {
            get => Written;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Written += count;
            if (Written > threshold)
            {
                source.Cancel();
            }
        }
    }

    private sealed class CancelOnReadStream(Stream inner, CancellationTokenSource source, long threshold) : Stream
    {
        private long read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            read += n;
            if (read > threshold)
            {
                source.Cancel();
            }

            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
