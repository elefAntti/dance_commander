using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Navigation;

/// <summary>
/// An address written the Linux way, <c>/home/x</c>, as pasted from a terminal: where it goes
/// depends on where the pane is. On a server, that server's path. Inside a WSL distribution, that
/// distribution's. Anywhere else on this computer, the default distribution's, with
/// <c>/mnt/c/work</c> as <c>C:\work</c>, where WSL puts the drives. Read as a Windows path
/// instead, it was <c>\home\x</c> on whichever drive the application started from, even on a
/// server.
/// </summary>
public static class PosixAddress
{
    /// <summary>
    /// Where <paramref name="typed"/> goes from <paramref name="here"/>, or null when it isn't a
    /// Linux path or nothing here reads it as one: with no WSL distribution it stays a Windows path.
    /// </summary>
    /// <param name="defaultDistribution">The distribution plain <c>wsl</c> opens, or null with none.</param>
    public static FsUri? Resolve(string typed, FsUri? here, Func<string?> defaultDistribution)
    {
        // One slash, then a name: "//server/share" is a Windows share written with slashes, and a
        // backslash means the text is a Windows path whatever it starts with.
        if (!typed.StartsWith('/') || typed.StartsWith("//", StringComparison.Ordinal) || typed.Contains('\\'))
            return null;

        var anchor = here?.Anchor;

        // A server's own path. An archive has no authority, and its own paths start inside it.
        if (anchor is { IsLocal: false, IsArchiveEntry: false, Authority.Length: > 0 })
            return FsUri.Remote(anchor.Scheme, anchor.Authority, typed);

        var distribution = anchor is { IsLocal: true } local && WslPaths.TrySplit(local.ToLocalPath(), out var inside, out _)
            ? inside
            : defaultDistribution();
        if (distribution is null)
            return null;

        if (DrivePath(typed) is { } drive)
            return FsUri.Local(drive);

        return FsUri.Local(@"\\wsl.localhost\" + distribution + typed.Replace('/', '\\'));
    }

    /// <summary>
    /// <c>/mnt/c/work</c> as <c>C:\work</c>. WSL mounts the drives under /mnt unless
    /// <c>automount.root</c> says otherwise; reading that means reading the distribution's
    /// /etc/wsl.conf, which starts it, so only the default is understood here.
    /// </summary>
    private static string? DrivePath(string typed)
    {
        const string mount = "/mnt/";
        if (!typed.StartsWith(mount, StringComparison.Ordinal) || typed.Length < mount.Length + 1)
            return null;

        var letter = typed[mount.Length];
        var rest = typed[(mount.Length + 1)..];
        if (!char.IsAsciiLetter(letter) || (rest.Length > 0 && rest[0] != '/'))
            return null;

        return char.ToUpperInvariant(letter) + @":\" + rest.TrimStart('/').Replace('/', '\\');
    }
}
