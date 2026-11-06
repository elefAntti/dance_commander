namespace DanceCommander.Core.Fs;

/// <summary>
/// Our own names beside a file: <c>name.part</c>, <c>name.dc-old-…</c>, <c>name.dc-incoming-…</c>.
/// </summary>
/// <remarks>
/// A name can be 255 characters long, and the suffix goes on top: a file whose name was already
/// that long couldn't be copied or replaced at all, as its <c>.part</c> name was refused (the
/// torture run, 2026-10-11). So the name is shortened until the whole fits. The suffix is what
/// says the file is ours, and it stays whole; the name in front is only a hint to whoever finds a
/// leftover, and its start serves as well.
/// </remarks>
public static class TemporaryName
{
    /// <summary>
    /// The most a name may have: 255 bytes of UTF-8 on Linux's file systems. A name within it is
    /// within NTFS's 255 UTF-16 units too, since no character takes more units than bytes.
    /// </summary>
    public const int MostBytes = 255;

    /// <summary><paramref name="name"/> with <paramref name="suffix"/> after it, the name shortened as needed.</summary>
    /// <remarks>
    /// Never the name itself: a long name that already ends in the suffix (a browser's long
    /// <c>.part</c>) shortened to exactly that, and the "temporary" copy was written at the real
    /// name, a truncated file wearing it if anything stopped part way (review, 2026-10-11).
    /// </remarks>
    public static string Beside(string name, string suffix)
    {
        var room = MostBytes - System.Text.Encoding.UTF8.GetByteCount(suffix);
        var made = Shortened(name, room) + suffix;
        return string.Equals(made, name, StringComparison.OrdinalIgnoreCase)
            ? Shortened(name, room - 1) + suffix
            : made;
    }

    /// <summary>The same for the last part of a path with '/' between its parts, as a server's is.</summary>
    public static string BesideInPath(string path, string suffix)
    {
        var slash = path.LastIndexOf('/');
        return path[..(slash + 1)] + Beside(path[(slash + 1)..], suffix);
    }

    /// <summary>The start of <paramref name="name"/> that fits in so many UTF-8 bytes, never
    /// cutting a character in two.</summary>
    public static string Shortened(string name, int maxBytes)
    {
        var length = 0;
        var bytes = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }

        return name[..length];
    }
}
