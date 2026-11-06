namespace DanceCommander.Core.Preview;

/// <summary>
/// Recognises images by their leading bytes, for the same reason archives are (§6.10): a preview
/// should show what a file <em>is</em>, and a screenshot saved as ".txt" is still a screenshot.
/// </summary>
public static class ImageSignature
{
    /// <summary>The format's usual name, or null when these bytes are not an image we can show.</summary>
    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (StartsWith(bytes, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return "PNG";
        if (StartsWith(bytes, [0xFF, 0xD8, 0xFF]))
            return "JPEG";
        if (StartsWith(bytes, "GIF87a"u8) || StartsWith(bytes, "GIF89a"u8))
            return "GIF";
        if (StartsWith(bytes, "BM"u8))
            return "BMP";

        // RIFF....WEBP
        if (StartsWith(bytes, "RIFF"u8) && bytes.Length >= 12 && bytes[8..12].SequenceEqual("WEBP"u8))
            return "WebP";

        if (StartsWith(bytes, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(bytes, [0x4D, 0x4D, 0x00, 0x2A]))
            return "TIFF";

        // An icon and a cursor share a header and differ only in the type field.
        if (StartsWith(bytes, [0x00, 0x00, 0x01, 0x00]))
            return "ICO";

        return null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> span, ReadOnlySpan<byte> magic) =>
        span.Length >= magic.Length && span[..magic.Length].SequenceEqual(magic);
}
