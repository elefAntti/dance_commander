using System.Text;

namespace DanceCommander.Core.Preview;

public enum LineEndingStyle
{
    /// <summary>No line break in what we read — a one-line file, or a very long first line.</summary>
    None,

    /// <summary>Windows.</summary>
    Crlf,

    /// <summary>Unix.</summary>
    Lf,

    /// <summary>Classic Mac, and usually a mistake.</summary>
    Cr,

    /// <summary>More than one style in the same file, which is worth knowing about.</summary>
    Mixed,
}

/// <param name="Truncated">True when the file is longer than we were willing to read.</param>
public sealed record TextInspection(
    bool IsText,
    string EncodingName,
    LineEndingStyle LineEndings,
    int CrlfCount,
    int LfCount,
    int CrCount,
    bool HasTabs,
    bool Truncated,
    string Text)
{
    /// <summary>
    /// True when the last line has no line break after it.
    /// </summary>
    /// <remarks>
    /// The thing diff writes "\ No newline at end of file" about. It is invisible in every viewer
    /// and it is a real difference between two files, which is exactly the kind of fact this
    /// preview exists to state rather than leave you to infer.
    /// </remarks>
    public bool EndsWithoutNewline =>
        !Truncated && Text.Length > 0 && Text[^1] is not ('\n' or '\r');

    /// <summary>How the line endings should be described in one word.</summary>
    public string LineEndingLabel => LineEndings switch
    {
        LineEndingStyle.Crlf => "CRLF",
        LineEndingStyle.Lf => "LF",
        LineEndingStyle.Cr => "CR",
        LineEndingStyle.Mixed => "mixed line endings",
        _ => "no line breaks",
    };

    /// <summary>The detail behind "mixed", which is the only case where the counts matter.</summary>
    public string? LineEndingDetail => LineEndings == LineEndingStyle.Mixed
        ? $"{CrlfCount:N0} CRLF, {LfCount:N0} LF" + (CrCount > 0 ? $", {CrCount:N0} CR" : "")
        : null;
}

/// <summary>
/// Decides whether bytes are text, in what encoding, and how their lines end (§6.11).
/// </summary>
/// <remarks>
/// Line endings are not trivia in a workflow that crosses Windows and Linux every day: a file that
/// looks identical in two places and behaves differently usually differs here, and "mixed" is the
/// case that costs an afternoon. So it is reported as a fact about the file, next to its encoding,
/// rather than left for the user to infer.
/// </remarks>
public static class TextInspector
{
    /// <summary>A NUL byte in the first block is the oldest and still the best binary test.</summary>
    private const int BinarySniffLength = 8000;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];
    private static ReadOnlySpan<byte> Utf32LeBom => [0xFF, 0xFE, 0x00, 0x00];
    private static ReadOnlySpan<byte> Utf32BeBom => [0x00, 0x00, 0xFE, 0xFF];
    private static ReadOnlySpan<byte> Utf16LeBom => [0xFF, 0xFE];
    private static ReadOnlySpan<byte> Utf16BeBom => [0xFE, 0xFF];

    private static bool Begins(ReadOnlySpan<byte> span, ReadOnlySpan<byte> magic) =>
        span.Length >= magic.Length && span[..magic.Length].SequenceEqual(magic);

    public static TextInspection Inspect(ReadOnlySpan<byte> bytes, bool truncated)
    {
        if (bytes.Length == 0)
            return new TextInspection(true, "empty", LineEndingStyle.None, 0, 0, 0, false, false, string.Empty);

        var (encoding, name, preamble) = DetectEncoding(bytes);
        if (encoding is null)
            return new TextInspection(false, name, LineEndingStyle.None, 0, 0, 0, false, truncated, string.Empty);

        string text;
        try
        {
            text = encoding.GetString(bytes[preamble..]);
        }
        catch (DecoderFallbackException)
        {
            return new TextInspection(false, "not text", LineEndingStyle.None, 0, 0, 0, false, truncated, string.Empty);
        }

        var (style, crlf, lf, cr) = CountLineEndings(text);
        return new TextInspection(
            true, name, style, crlf, lf, cr, text.Contains('\t'), truncated, text);
    }

    /// <returns>Null encoding when the content is not text at all.</returns>
    private static (Encoding? Encoding, string Name, int Preamble) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        // A byte-order mark settles it outright. UTF-32 LE is checked before UTF-16 LE, whose
        // mark is its first two bytes.
        if (Begins(bytes, Utf8Bom))
            return (Encoding.UTF8, "UTF-8 with BOM", 3);
        if (Begins(bytes, Utf32LeBom))
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), "UTF-32 LE", 4);
        if (Begins(bytes, Utf32BeBom))
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), "UTF-32 BE", 4);
        if (Begins(bytes, Utf16LeBom))
            return (Encoding.Unicode, "UTF-16 LE", 2);
        if (Begins(bytes, Utf16BeBom))
            return (Encoding.BigEndianUnicode, "UTF-16 BE", 2);

        if (LooksBinary(bytes))
            return (null, "not text", 0);

        // No mark: valid UTF-8 is overwhelmingly the right guess, and strict decoding is what
        // makes it a test rather than a hope.
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            _ = strict.GetString(bytes);
            return (strict, "UTF-8", 0);
        }
        catch (DecoderFallbackException)
        {
        }

        // Not UTF-8 and not binary: some single-byte codepage. Latin-1 maps every byte to a
        // character, so the preview shows something honest rather than a wall of replacements —
        // but it is labelled a guess, because that is what it is.
        return (Encoding.Latin1, "8-bit text (assumed Latin-1)", 0);
    }

    /// <summary>
    /// NUL bytes mean binary. Nothing else is nearly as reliable: UTF-16 without a mark would look
    /// binary by this test, which is why the marks are checked first.
    /// </summary>
    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var limit = Math.Min(bytes.Length, BinarySniffLength);
        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == 0)
                return true;
        }

        return false;
    }

    private static (LineEndingStyle Style, int Crlf, int Lf, int Cr) CountLineEndings(string text)
    {
        int crlf = 0, lf = 0, cr = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    crlf++;
                    i++; // the pair counts once
                    break;
                case '\r':
                    cr++;
                    break;
                case '\n':
                    lf++;
                    break;
            }
        }

        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var style = kinds switch
        {
            0 => LineEndingStyle.None,
            > 1 => LineEndingStyle.Mixed,
            _ when crlf > 0 => LineEndingStyle.Crlf,
            _ when lf > 0 => LineEndingStyle.Lf,
            _ => LineEndingStyle.Cr,
        };

        return (style, crlf, lf, cr);
    }
}
