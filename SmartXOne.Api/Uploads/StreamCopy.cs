namespace SmartXOne.Api.Uploads;

/// <summary>
/// A length-limited, cancellable stream copy. .NET's <see cref="Stream.CopyToAsync(Stream)"/>
/// has no built-in way to cap how much it will copy, so this reads through a fixed-size
/// buffer (the file is never held in memory as a whole) and throws
/// <see cref="AttachmentTooLargeException"/> the moment the limit is crossed, instead of
/// writing an unbounded amount to disk first and checking afterwards.
/// </summary>
public static class StreamCopy
{
    private const int BufferSize = 81920; // matches Stream.CopyToAsync's own default buffer size

    /// <returns>Total bytes copied.</returns>
    /// <exception cref="AttachmentTooLargeException">More than <paramref name="maxBytes"/> were read from <paramref name="source"/>.</exception>
    public static async Task<long> CopyWithLimitAsync(Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new AttachmentTooLargeException(maxBytes);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return total;
    }
}
