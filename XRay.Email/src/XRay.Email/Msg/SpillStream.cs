namespace XRay.Email.Msg;

/// <summary>
/// A write-then-read buffer that stays in memory up to a threshold and then moves to a temporary
/// file that is deleted when the stream is disposed. <see cref="PrepareForReading"/> rewinds it.
/// </summary>
internal sealed class SpillStream(int memoryLimit = 1 << 20) : Stream
{
    private Stream _inner = new MemoryStream();
    private bool _spilled;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!_spilled && _inner.Length + buffer.Length > memoryLimit)
        {
            Spill();
        }

        _inner.Write(buffer);
    }

    public SpillStream PrepareForReading()
    {
        _inner.Position = 0;
        return this;
    }

    private void Spill()
    {
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), "xray-email-" + Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            FileOptions.DeleteOnClose);
        _inner.Position = 0;
        _inner.CopyTo(file);
        _inner.Dispose();
        _inner = file;
        _spilled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
