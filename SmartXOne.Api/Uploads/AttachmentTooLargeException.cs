namespace SmartXOne.Api.Uploads;

/// <summary>
/// Thrown mid-stream by <see cref="StreamCopy.CopyWithLimitAsync"/> the moment an
/// upload exceeds its allowed size, so the caller can stop writing and delete the
/// partial file instead of accepting an oversized attachment.
/// </summary>
public sealed class AttachmentTooLargeException(long maxBytes) : Exception($"Attachment exceeds the maximum allowed size of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}
