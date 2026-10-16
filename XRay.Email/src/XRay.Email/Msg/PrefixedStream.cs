namespace XRay.Email.Msg;

/// <summary>A forward-only stream that replays bytes already read from a non-seekable source before continuing with it.</summary>
internal sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
{
    private int _prefixPosition;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_prefixPosition < prefix.Length)
        {
            var count = Math.Min(buffer.Length, prefix.Length - _prefixPosition);
            prefix.AsSpan(_prefixPosition, count).CopyTo(buffer);
            _prefixPosition += count;
            return count;
        }

        return inner.Read(buffer);
    }
}
