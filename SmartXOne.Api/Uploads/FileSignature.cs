namespace SmartXOne.Api.Uploads;

/// <summary>
/// Validates an uploaded attachment by its actual byte content ("magic bytes"),
/// not just the extension on the claimed file name - renaming a file doesn't
/// change what it actually starts with, so sniffing the first few bytes catches
/// a file that's been relabelled to slip past an extension-only check.
/// </summary>
public static class FileSignature
{
    /// <summary>How many leading bytes of the upload are inspected before any content is written to disk.</summary>
    public const int SniffLength = 16;

    private static readonly Dictionary<string, byte[][]> BinarySignatures = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]],
        [".jpg"] = [[0xFF, 0xD8, 0xFF]],
        [".jpeg"] = [[0xFF, 0xD8, 0xFF]],
    };

    private static readonly string[] TextExtensions = [".log", ".txt", ".json", ".csv"];

    public static bool IsAllowedExtension(string extension)
        => BinarySignatures.ContainsKey(extension) || TextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>A human-readable list of accepted extensions, for error messages.</summary>
    public static string AllowedExtensionsDisplay
        => string.Join(", ", BinarySignatures.Keys.Concat(TextExtensions));

    /// <summary>
    /// Checks the sniffed header bytes against what <paramref name="extension"/>
    /// claims to be. Images must match a known magic-byte signature exactly; the
    /// plain-text extensions (log/txt/json/csv) have no fixed signature, so they're
    /// accepted only if the header contains no NUL or other control bytes that
    /// wouldn't appear in real text.
    /// </summary>
    public static bool MatchesContent(string extension, ReadOnlySpan<byte> header)
    {
        if (BinarySignatures.TryGetValue(extension, out var signatures))
        {
            foreach (var signature in signatures)
            {
                if (header.Length >= signature.Length && header[..signature.Length].SequenceEqual(signature))
                {
                    return true;
                }
            }

            return false;
        }

        if (TextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return LooksLikeText(header);
        }

        return false;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        foreach (var b in header)
        {
            // Reject NUL and other control bytes that don't belong in text content;
            // tab (0x09), LF (0x0A) and CR (0x0D) are the only control bytes allowed.
            var isAllowedControl = b is 0x09 or 0x0A or 0x0D;
            if (b < 0x20 && !isAllowedControl)
            {
                return false;
            }
        }

        return true;
    }
}
