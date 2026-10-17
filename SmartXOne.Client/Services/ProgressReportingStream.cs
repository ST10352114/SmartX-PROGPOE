namespace SmartXOne.Client.Services;

/// <summary>
/// Wraps the browser file stream used for an upload and reports cumulative
/// bytes read as <see cref="HttpClient"/> consumes it, so the upload progress
/// bar reflects bytes actually sent rather than a simulated/fake animation.
/// </summary>
internal sealed class ProgressReportingStream(Stream inner, Action<long> onBytesRead) : Stream
{
    private long _bytesRead;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
        {
            _bytesRead += read;
            onBytesRead(_bytesRead);
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
