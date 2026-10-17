using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SmartXOne.Api.Uploads;

/// <summary>
/// Encrypts and decrypts attachment files with AES-256-GCM, streaming through a
/// fixed-size buffer in both directions so a file is never held in memory as a
/// whole. The plaintext is also SHA-256 hashed as it streams through
/// <see cref="EncryptToFileAsync"/>, producing an integrity fingerprint that gets
/// stored in the attachment's metadata.
/// <para>
/// Code Attribution
/// Purpose: AesGcm's span-based Encrypt/Decrypt overloads, and IncrementalHash
///          for computing a SHA-256 digest incrementally as data streams through.
/// Author: Microsoft
/// Title: AesGcm Class; IncrementalHash Class (System.Security.Cryptography)
/// Year: 2026
/// URL: https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm,
///      https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.incrementalhash
/// Accessed: 17 October 2026
/// </para>
/// <para>
/// GCM itself only encrypts one buffer at a time, so the chunked file envelope
/// below (magic header, per-chunk nonce derivation, length-prefixed ciphertext
/// frames) is this project's own design on top of those two APIs, built to let a
/// file far larger than available memory be encrypted/decrypted a chunk at a time.
/// </para>
/// </summary>
public static class EncryptedAttachmentCodec
{
    private const int ChunkSizePlaintext = 64 * 1024;
    private const int NonceSizeBytes = 12; // AesGcm's standard/recommended nonce size
    private const int TagSizeBytes = 16;   // 128-bit authentication tag

    private static readonly byte[] Magic = "SXE1"u8.ToArray();

    public sealed record EncryptResult(long PlaintextLength, string Sha256Hex);

    /// <summary>
    /// Reads <paramref name="plaintextSource"/> to completion, writing an
    /// encrypted envelope to <paramref name="destinationPath"/> one chunk at a
    /// time, and returns the plaintext's total length and SHA-256 hash.
    /// </summary>
    /// <param name="maxPlaintextBytes">
    /// If the source produces more than this many bytes, encryption stops, the
    /// partial output file is deleted, and <see cref="AttachmentTooLargeException"/>
    /// is thrown.
    /// </param>
    public static async Task<EncryptResult> EncryptToFileAsync(
        Stream plaintextSource, string destinationPath, byte[] key, long maxPlaintextBytes, CancellationToken ct)
    {
        var baseNonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var aes = new AesGcm(key, TagSizeBytes);

        var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        try
        {
            await using (destination)
            {
                await destination.WriteAsync(Magic, ct);
                await destination.WriteAsync(baseNonce, ct);

                var plaintextBuffer = new byte[ChunkSizePlaintext];
                var ciphertextBuffer = new byte[ChunkSizePlaintext];
                var tagBuffer = new byte[TagSizeBytes];
                var lengthPrefix = new byte[4];

                long totalPlaintext = 0;
                uint chunkIndex = 0;

                int read;
                while ((read = await plaintextSource.ReadAtLeastAsync(plaintextBuffer, plaintextBuffer.Length, throwOnEndOfStream: false, ct)) > 0)
                {
                    totalPlaintext += read;
                    if (totalPlaintext > maxPlaintextBytes)
                    {
                        throw new AttachmentTooLargeException(maxPlaintextBytes);
                    }

                    hash.AppendData(plaintextBuffer.AsSpan(0, read));

                    var nonce = DeriveChunkNonce(baseNonce, chunkIndex);
                    aes.Encrypt(nonce, plaintextBuffer.AsSpan(0, read), ciphertextBuffer.AsSpan(0, read), tagBuffer);

                    BinaryPrimitives.WriteUInt32BigEndian(lengthPrefix, (uint)read);
                    await destination.WriteAsync(lengthPrefix, ct);
                    await destination.WriteAsync(tagBuffer, ct);
                    await destination.WriteAsync(ciphertextBuffer.AsMemory(0, read), ct);

                    chunkIndex++;
                }

                var digest = hash.GetHashAndReset();
                return new EncryptResult(totalPlaintext, Convert.ToHexString(digest).ToLowerInvariant());
            }
        }
        catch (Exception ex) when (ex is AttachmentTooLargeException or InvalidDataException)
        {
            // AttachmentTooLargeException is our own check above; InvalidDataException
            // is MultipartReader's own BodyLengthLimit tripping first when reading
            // from a multipart section (its limit counts multipart framing overhead
            // too, so it can fire a little before our own byte-for-byte count does).
            // Either way, don't leave a partial encrypted file behind.
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    /// <summary>
    /// Reads the encrypted envelope at <paramref name="sourcePath"/> one chunk at
    /// a time, decrypting and writing each chunk's plaintext to
    /// <paramref name="destination"/> as soon as it's verified.
    /// </summary>
    /// <exception cref="CryptographicException">
    /// A chunk's authentication tag didn't match - the file was tampered with,
    /// corrupted, or the wrong key was used. Thrown before that chunk's plaintext
    /// is written, so no unverified bytes ever reach <paramref name="destination"/>.
    /// </exception>
    public static async Task DecryptToStreamAsync(string sourcePath, Stream destination, byte[] key, CancellationToken ct)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var magic = new byte[Magic.Length];
        await source.ReadExactlyAsync(magic, ct);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a recognised encrypted attachment file.");
        }

        var baseNonce = new byte[NonceSizeBytes];
        await source.ReadExactlyAsync(baseNonce, ct);

        using var aes = new AesGcm(key, TagSizeBytes);
        var lengthPrefix = new byte[4];
        var tagBuffer = new byte[TagSizeBytes];
        var ciphertextBuffer = new byte[ChunkSizePlaintext];
        var plaintextBuffer = new byte[ChunkSizePlaintext];
        uint chunkIndex = 0;

        while (true)
        {
            var prefixRead = await source.ReadAtLeastAsync(lengthPrefix, lengthPrefix.Length, throwOnEndOfStream: false, ct);
            if (prefixRead == 0)
            {
                break; // clean end of file between chunks
            }

            if (prefixRead != lengthPrefix.Length)
            {
                throw new InvalidDataException("Encrypted attachment file is truncated.");
            }

            var chunkLength = (int)BinaryPrimitives.ReadUInt32BigEndian(lengthPrefix);
            if (chunkLength is <= 0 or > ChunkSizePlaintext)
            {
                throw new InvalidDataException("Encrypted attachment file is corrupt (invalid chunk length).");
            }

            await source.ReadExactlyAsync(tagBuffer, ct);
            await source.ReadExactlyAsync(ciphertextBuffer.AsMemory(0, chunkLength), ct);

            var nonce = DeriveChunkNonce(baseNonce, chunkIndex);

            // Throws CryptographicException if the tag doesn't match - i.e. this
            // chunk was tampered with or corrupted. That's the real-time integrity
            // check while streaming; EncryptResult.Sha256Hex (stored in the
            // attachment's metadata) is the separate end-to-end plaintext
            // fingerprint a caller can compare against once the whole download
            // has completed.
            aes.Decrypt(nonce, ciphertextBuffer.AsSpan(0, chunkLength), tagBuffer, plaintextBuffer.AsSpan(0, chunkLength));

            await destination.WriteAsync(plaintextBuffer.AsMemory(0, chunkLength), ct);
            chunkIndex++;
        }
    }

    /// <summary>
    /// Combines the file's random base nonce with a per-chunk counter so every
    /// chunk gets a distinct nonce without storing one per chunk - reusing a GCM
    /// nonce with the same key is what breaks its security guarantees, so this
    /// is the one invariant the whole scheme depends on.
    /// </summary>
    private static byte[] DeriveChunkNonce(byte[] baseNonce, uint chunkIndex)
    {
        var nonce = (byte[])baseNonce.Clone();
        var indexBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(indexBytes, chunkIndex);

        for (var i = 0; i < 4; i++)
        {
            nonce[NonceSizeBytes - 4 + i] ^= indexBytes[i];
        }

        return nonce;
    }
}
