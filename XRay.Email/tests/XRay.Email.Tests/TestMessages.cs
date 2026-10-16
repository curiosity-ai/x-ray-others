using System.Text;
using XRay.Email.Msg;

namespace XRay.Email.Tests;

/// <summary>Builders shared by the preview, body fallback and attachment tests.</summary>
internal static class TestMessages
{
    /// <summary>A valid 1x1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgAAACAAFUok9fAAAAAElFTkSuQmCC");

    /// <summary>A valid 1x1 GIF.</summary>
    public static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    public static string PngDataUri => "data:image/png;base64," + Convert.ToBase64String(Png);

    /// <summary>PR_RTF_COMPRESSED in the uncompressed form of MS-OXRTFCP ("MELA", CRC zero).</summary>
    public static byte[] UncompressedRtf(string rtf)
    {
        var raw = Encoding.Latin1.GetBytes(rtf);
        var data = new byte[16 + raw.Length];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), raw.Length + 12);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), raw.Length);
        BitConverter.TryWriteBytes(data.AsSpan(8, 4), 0x414C454Du);
        raw.CopyTo(data, 16);
        return data;
    }

    public static MapiMessage WithRtf(string rtf, string subject = "RTF message")
    {
        var message = MapiMessage.Create(subject);
        message.SetProperty(0x1009, (ushort)PropertyTypeCode.PtypBinary, UncompressedRtf(rtf));
        return message;
    }

    public static MapiMessage Basic(string subject = "Subject", string? body = "Body")
    {
        var message = MapiMessage.Create(subject);
        message.Body = body;
        message.SenderName = "Alice Example";
        message.SenderEmailAddress = "alice@example.com";
        message.SenderAddressType = "SMTP";
        message.AddRecipient("bob@example.com", "Bob Example");
        message.ClientSubmitTime = new DateTime(2024, 3, 5, 14, 40, 0, DateTimeKind.Utc);
        return message;
    }

    public static MapiMessage WithHtml(string html, string subject = "HTML message")
    {
        var message = Basic(subject, null);
        message.HtmlBody = html;
        return message;
    }

    /// <summary>Round-trips through an MSG file, so a test sees what a loaded message looks like.</summary>
    public static MapiMessage ThroughMsg(MapiMessage message) =>
        MapiMessage.FromStream(new MemoryStream(message.Save(TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);

    /// <summary>Round-trips through EML.</summary>
    public static MapiMessage ThroughEml(MapiMessage message) =>
        MapiMessage.LoadFromEml(message.SaveToEml(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

    public static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}

/// <summary>A stream that reports no seeking, as a network or pipe stream does.</summary>
internal sealed class ForwardOnlyStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
}

/// <summary>Records the size of every write and can run a callback on the first one.</summary>
internal sealed class RecordingStream : Stream
{
    private readonly MemoryStream _inner = new();

    public List<int> Writes { get; } = [];

    public Action? OnFirstWrite { get; set; }

    public byte[] ToArray() => _inner.ToArray();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Writes.Count == 0)
        {
            OnFirstWrite?.Invoke();
        }

        Writes.Add(buffer.Length);
        _inner.Write(buffer);
    }
}
