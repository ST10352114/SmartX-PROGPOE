namespace SmartXOne.Api.Uploads;

/// <summary>
/// A read-only stream that serves an in-memory prefix first, then forwards
/// every further read to <paramref name="rest"/>.
/// <para>
/// The upload endpoint reads the first few bytes of a multipart section to
/// sniff its content type before deciding whether to accept the file at all.
/// A multipart section's body isn't seekable, so those bytes can't just be
/// rewound - this stream "puts them back" in front of the remaining body, so
/// <see cref="EncryptedAttachmentCodec"/> can read the whole file in its
/// original order as a single stream.
/// </para>
/// </summary>
internal sealed class PrefixedStream(byte[] prefix, int prefixLength, Stream rest) : Stream
{
    private int _prefixPosition;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixPosition < prefixLength)
        {
            var toCopy = Math.Min(buffer.Length, prefixLength - _prefixPosition);
            prefix.AsMemory(_prefixPosition, toCopy).CopyTo(buffer);
            _prefixPosition += toCopy;
            return toCopy;
        }

        return await rest.ReadAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
