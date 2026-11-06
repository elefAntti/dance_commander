using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DanceCommander.Providers.Local;

/// <summary>
/// Whether another program has a file or folder open so that it can't be moved or removed.
/// </summary>
/// <remarks>
/// Windows doesn't always say so as "in use". A rename over a file open elsewhere is "access
/// denied", as for a permission; the shell's recycle of a folder that a shell window stands in
/// is an error code of its own (0x7C). Both found by the torture run (2026-10-11). Asked only
/// once something has failed, so the walk below costs nothing on the way that works.
/// </remarks>
internal static class OpenElsewhere
{
    /// <summary>Not every entry of a huge selection: past this many, the failure is said as it came.</summary>
    private const int MostLookedAt = 10_000;

    /// <summary>Asked the way a rename or a delete asks: for the right to delete, every kind of sharing offered.</summary>
    public static bool Is(string path)
    {
        var flags = Directory.Exists(path) ? BackupSemantics : 0;
        using var handle = CreateFile(path, Delete, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, flags, IntPtr.Zero);
        return handle.IsInvalid && Marshal.GetLastWin32Error() == ErrorSharingViolation;
    }

    /// <summary>
    /// The <paramref name="paths"/> and what is under them that another program has open: one
    /// held file deep in a folder keeps the whole folder where it is.
    /// </summary>
    public static IReadOnlyList<string> Under(IEnumerable<string> paths)
    {
        var held = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var looked = 0;
        foreach (var path in paths)
        {
            if (++looked > MostLookedAt)
                break;
            if (Is(path))
                held.Add(path);
            if (!Directory.Exists(path))
                continue;

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", options))
                {
                    if (++looked > MostLookedAt)
                        break;
                    if (Is(entry))
                        held.Add(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // What was found so far is still worth saying.
            }
        }

        return held;
    }

    private const uint Delete = 0x00010000;
    private const uint BackupSemantics = 0x02000000;
    private const int ErrorSharingViolation = 32;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
}
