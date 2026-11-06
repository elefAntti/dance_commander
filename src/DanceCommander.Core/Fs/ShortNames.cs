using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DanceCommander.Core.Fs;

/// <summary>
/// Turns a local path's 8.3 short names (<c>C:\Users\ANTTIV~1</c>) into their long ones.
/// </summary>
/// <remarks>
/// One folder under two names is two addresses, and every check that compares addresses was fooled
/// by it: a hit pasted into its own folder was skipped rather than duplicated, and a folder moved
/// onto itself lost its empty subfolders (2026-10-14). Short names come in from outside, never
/// from a listing: <c>%TEMP%</c> holds one on many machines, and so can a path on the command line,
/// in a drop or on the clipboard. Only a path with a segment shaped like a short name is looked
/// up, and only on a fixed disk: asking about a network path can hang for as long as the server
/// takes to answer, a stick or a disc for as long as it takes to wake, and the address may be
/// being built on the UI thread.
/// </remarks>
internal static partial class ShortNames
{
    /// <param name="path">A normalised local path, <c>C:/a/b</c>.</param>
    /// <returns>The same path with its short names expanded, as far as it exists; otherwise unchanged.</returns>
    public static string Expand(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains('~') || !ShortSegment().IsMatch(path))
            return path;
        if (path.Length < 3 || path[1] != ':' || path[2] != '/')
            return path;
        // A stick or a disc can take seconds to wake, and this runs on the UI thread.
        if (GetDriveTypeW(path[..2] + "\\") is not (DriveFixed or DriveRamDisk))
            return path;

        // The longest part that exists: a name typed for a file not made yet keeps its tail as typed.
        var native = path.Replace('/', '\\');
        for (var at = native; at.Length > 3; at = Path.GetDirectoryName(at) ?? string.Empty)
        {
            var buffer = new char[1024];
            var length = GetLongPathNameW(at, buffer, buffer.Length);
            if (length > 0 && length < buffer.Length)
                return (new string(buffer, 0, length) + native[at.Length..]).Replace('\\', '/');
        }

        return path;
    }

    // NAME~1 or NAME~12.EXT: six characters or fewer, a tilde, digits, an extension of three at most.
    [GeneratedRegex(@"(?:^|/)[^/~]{1,6}~\d{1,6}(?:\.[^/.]{0,3})?(?:/|$)")]
    private static partial Regex ShortSegment();

    private const uint DriveFixed = 3;
    private const uint DriveRamDisk = 6;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLongPathNameW(string shortPath, [Out] char[] longPath, int size);
}
