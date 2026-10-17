namespace SmartXOne.Api.Uploads;

/// <summary>
/// Resolves the AES-256 key used to encrypt/decrypt attachments, once, at
/// start-up, and hands it out to the upload/download endpoints as a singleton.
/// <para>
/// The key is read from configuration key <c>Encryption:KeyBase64</c>, which in
/// a real deployment comes from an environment variable or a secret store - it
/// is never hard-coded. For local development, set it with:
/// <c>dotnet user-secrets set "Encryption:KeyBase64" "&lt;base64 32-byte key&gt;"</c>
/// (see the README's "Encryption key setup" section for the exact command and
/// how to generate a key).
/// </para>
/// <para>
/// If nothing is configured, a fixed, clearly-labelled development-only key is
/// used instead of failing to start - acceptable for coursework running
/// entirely on localhost with no real secrets in play, but called out loudly
/// here and in the README so it's never mistaken for something safe to deploy.
/// </para>
/// </summary>
public sealed class AttachmentEncryptionKeyProvider
{
    private const int KeySizeBytes = 32; // AES-256

    // NOT A SECRET. Fixed only so a fresh clone with no configured key still
    // runs locally. Replace via "Encryption:KeyBase64" for anything that
    // matters - see the README.
    private static readonly byte[] DevelopmentOnlyKey =
    [
        0x53, 0x6D, 0x61, 0x72, 0x74, 0x2D, 0x58, 0x20, 0x50, 0x6F, 0x45, 0x20, 0x64, 0x65, 0x76, 0x20,
        0x6B, 0x65, 0x79, 0x20, 0x2D, 0x20, 0x44, 0x4F, 0x20, 0x4E, 0x4F, 0x54, 0x20, 0x55, 0x53, 0x45,
    ];

    public byte[] Key { get; }

    public AttachmentEncryptionKeyProvider(IConfiguration configuration, ILogger<AttachmentEncryptionKeyProvider> logger)
    {
        var configured = configuration["Encryption:KeyBase64"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            logger.LogWarning(
                "Encryption:KeyBase64 is not configured - falling back to the built-in development-only " +
                "key. This is fine for local coursework use, never for anything deployed. See the README's " +
                "'Encryption key setup' section.");
            Key = DevelopmentOnlyKey;
            return;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(configured);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Encryption:KeyBase64 is not valid Base64.", ex);
        }

        if (decoded.Length != KeySizeBytes)
        {
            throw new InvalidOperationException(
                $"Encryption:KeyBase64 must decode to exactly {KeySizeBytes} bytes (AES-256); got {decoded.Length}.");
        }

        Key = decoded;
    }
}
