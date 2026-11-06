namespace DanceCommander.Core.Preview;

/// <summary>
/// Recognises a PDF by its leading bytes, for the same reason <see cref="ImageSignature"/> does:
/// a preview should show what a file <em>is</em>, not what its extension claims.
/// </summary>
public static class PdfSignature
{
    /// <summary>
    /// The header is nominally just <c>%PDF-</c>, but the spec allows up to 1024 bytes of junk
    /// before it to tolerate a prepended shebang line or mailer wrapper, so a strict prefix check
    /// would miss files that every real PDF reader still opens.
    /// </summary>
    private const int SearchWindow = 1024;

    public static bool Detect(ReadOnlySpan<byte> bytes) =>
        bytes[..Math.Min(bytes.Length, SearchWindow)].IndexOf("%PDF-"u8) >= 0;
}
