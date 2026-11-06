using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Transfer;

/// <summary>
/// What a local volume can't hold in one file: FAT32 (most USB sticks and SD cards) stops at
/// 4 GB less a byte, the older FAT at 2 GB.
/// </summary>
/// <remarks>
/// Without this a 5 GB file sent to such a stick stopped the whole job with Windows' "There is not
/// enough space on the disk", on a stick with room to spare (2026-10-14). Nothing was lost, but the
/// cause was hidden and the files after it never tried.
/// </remarks>
public static class VolumeLimits
{
    /// <param name="Format">The file system's name as Windows gives it.</param>
    /// <param name="MaxFile">The largest file it holds, in bytes.</param>
    /// <param name="Said">That size, as the user is told it.</param>
    public sealed record Limit(string Format, long MaxFile, string Said);

    /// <summary>No file at or under this size meets any limit here, so the volume need not be asked.</summary>
    public const long Smallest = int.MaxValue;

    /// <summary>The limit of the volume holding <paramref name="directory"/>, or null when it has none or can't be asked.</summary>
    /// <remarks>Asked only when a file over <see cref="Smallest"/> is in the job, so an ordinary copy never touches the volume for it.</remarks>
    public static Limit? Of(FsUri directory)
    {
        if (!directory.IsLocal || directory.Path.Length < 2 || directory.Path[1] != ':')
            return null;
        try
        {
            return For(new DriveInfo(directory.Path[..2]).DriveFormat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <param name="format">The file system's name as Windows gives it: "NTFS", "FAT32", "exFAT".</param>
    public static Limit? For(string format) => format.ToUpperInvariant() switch
    {
        "FAT32" => new Limit("FAT32", uint.MaxValue, "4 GB"),
        "FAT" => new Limit("FAT", int.MaxValue, "2 GB"),
        _ => null,
    };
}
