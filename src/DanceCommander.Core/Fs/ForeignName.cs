using System.Text;

namespace DanceCommander.Core.Fs;

/// <summary>
/// A file name another program made up, made into one plain name to write under. The name is the
/// program's to choose, but where the file lands is not: "..\x" or "a/b" become names in the
/// target folder, never a way out of it.
/// </summary>
public static class ForeignName
{
    public const string Fallback = "Dropped file";

    /// <summary>Windows' set, which is a superset of what SFTP and WebDAV servers refuse.</summary>
    private static readonly char[] Invalid =
    [
        .. Enumerable.Range(0, 32).Select(c => (char)c),
        '"', '<', '>', '|', ':', '*', '?', '\\', '/',
    ];

    public static string ToFileName(string name)
    {
        // Only the last segment: a name with folders in it doesn't get to choose folders.
        var leaf = name.Replace('/', '\\');
        leaf = leaf[(leaf.LastIndexOf('\\') + 1)..];

        var cleaned = new StringBuilder(leaf.Length);
        // Format characters too: a right-to-left override (U+202E) before "fdp.exe" shows an .exe as a .pdf.
        foreach (var c in leaf)
            cleaned.Append(Array.IndexOf(Invalid, c) >= 0 || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? '_' : c);

        // Windows drops trailing dots and spaces, so "a." would be written as "a", and ".." would be the parent.
        var result = cleaned.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0)
            return Fallback;

        // CON, NUL, COM1 and the rest name devices, with or without an extension.
        if (NamesDevice(result))
            result = "_" + result;

        return result;
    }

    /// <summary>
    /// Whether Windows reads the name as a device: NUL, CON, COM1 and the rest, with or without an
    /// extension. Written to, "NUL" takes whatever is saved there and keeps none of it.
    /// </summary>
    public static bool NamesDevice(string name) => IsDeviceName(name.Split('.')[0].TrimEnd(' '));

    private static bool IsDeviceName(string stem) =>
        stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
        || (stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            // Windows reserves the superscripts too: COM¹ is a port.
            && stem[3] is >= '0' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3');
}
