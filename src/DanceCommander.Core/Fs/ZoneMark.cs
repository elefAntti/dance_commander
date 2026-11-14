using System.Text;

namespace DanceCommander.Core.Fs;

/// <summary>
/// The "downloaded from the internet" mark Windows keeps beside a file (its <c>Zone.Identifier</c>
/// stream). Office opens a marked document in Protected View and SmartScreen asks before a marked
/// program runs, so a file this app brings onto the disk from somewhere else carries it, as one
/// Outlook or a browser saved would.
/// </summary>
public static class ZoneMark
{
    private const string Stream = ":Zone.Identifier";

    /// <summary>URLZONE_INTERNET. Restricted sites are 4; both count as "from the internet".</summary>
    private const int Internet = 3;

    /// <summary>Marks <paramref name="path"/> as come from the internet, keeping a mark already there.</summary>
    /// <returns>False when the disk can't hold the mark (FAT, some shares).</returns>
    public static bool MarkFromInternet(string path)
    {
        try
        {
            // A stream of the file's own, new: CreateNew, so a mark already there stays.
            using var mark = new FileStream(path + Stream, FileMode.CreateNew, FileAccess.Write);
            mark.Write(Encoding.ASCII.GetBytes($"[ZoneTransfer]\r\nZoneId={Internet}\r\n"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> carries a mark of the internet or a restricted site. A mark
    /// that is there but can't be read (a browser or a virus scanner still holding the file) counts
    /// as one: "couldn't tell" must not switch the warning off.
    /// </summary>
    public static bool IsFromInternet(string path)
    {
        try
        {
            using var reader = new StreamReader(path + Stream, Encoding.ASCII);
            while (reader.ReadLine() is { } line)
            {
                var equals = line.IndexOf('=');
                if (equals > 0
                    && line[..equals].Trim().Equals("ZoneId", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line[(equals + 1)..].Trim(), out var zone))
                    return zone >= Internet;
            }
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or NotSupportedException)
        {
            // No stream is the usual answer, and a disk without streams (FAT) can't hold a mark.
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
