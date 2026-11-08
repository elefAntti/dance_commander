using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DanceCommander.Providers.Local;

/// <summary>
/// What makes a local file itself, whatever it is called: the volume's serial number and the
/// file's index on that volume.
/// </summary>
/// <remarks>
/// Comparing addresses is not enough. A <c>subst</c> drive, a junction or <c>\\localhost\c$</c>
/// give one file two names, and copying it "onto the other one" would write over the source
/// (§1.6). Asked only where a write is about to replace something, so ordinary copies pay
/// nothing.
/// </remarks>
internal readonly record struct FileIdentity(uint VolumeSerial, ulong Index)
{
    /// <summary>The file's or folder's identity, or null when it is not there or cannot be opened to ask.</summary>
    /// <remarks>
    /// Opened for its attributes only, with backup semantics: a folder can't be opened otherwise,
    /// and a move of a folder onto itself under its short name (<c>%TEMP%</c> is
    /// <c>C:\Users\ANTTIV~1\…</c>) then removed its empty subfolders as "emptied" (2026-10-14).
    /// Attributes only also answers for a file another program holds open without sharing, which
    /// asking for Read could not.
    /// </remarks>
    public static FileIdentity? Of(string path)
    {
        using var handle = CreateFileW(
            Extended(path), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        // An index of 0, which some shares give every file, is kept: everything there then reads as
        // the same file, which skips or refuses. Taken as "unknown" it read as different, and a
        // Replace of a file over its own other name went ahead (review, 2026-10-14).
        return GetFileInformationByHandle(handle, out var info)
            ? new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
            : null;
    }

    /// <summary>
    /// The file's identity only where it is its own: the 128-bit file ID where the volume has one
    /// (ReFS's 64-bit index is not promised unique), else a 64-bit index other than 0; null where
    /// there is neither, as on a share that gives every file index 0.
    /// </summary>
    /// <remarks>
    /// For a caller to whom "the same" means "nothing lost" (a Replace's count of what it
    /// deletes), where <see cref="Of"/>'s keeping of index 0 would fail the wrong way. Zero is the
    /// only failure that can be seen: a server whose IDs are not zero but repeat (Samba folding a
    /// 64-bit inode into 32 bits, a FUSE mount with a fixed one) is believed (review, 2026-11-08).
    /// </remarks>
    public static (ulong Volume, ulong Low, ulong High)? UniqueOf(string path)
    {
        using var handle = CreateFileW(
            Extended(path), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        if (GetFileIdInformation(handle, FileIdInfoClass, out var id, (uint)Marshal.SizeOf<FileIdInformation>()))
            return id.Low == 0 && id.High == 0 ? null : (id.VolumeSerialNumber, id.Low, id.High);

        return GetFileInformationByHandle(handle, out var info) && (info.FileIndexHigh | info.FileIndexLow) != 0
            ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, 0UL)
            : null;
    }

    /// <summary>
    /// What moves on every change to the file and that no program puts back: its last entry in the
    /// volume's change journal (the USN), or where there is no journal, NTFS's change time.
    /// </summary>
    /// <remarks>
    /// The change time alone could be held still: VeraCrypt's "preserve timestamps" sets it to -1
    /// while a container is mounted and writes the saved one back at dismount, as any program may
    /// with SetFileInformationByHandle, and the edited container passed for the copy (review,
    /// 2026-10-28). A USN is the journal's, which a file's owner can't set.
    /// </remarks>
    public static long? ChangeStampOf(string path)
    {
        using var handle = CreateFileW(
            Extended(path), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        var record = new byte[1024];
        if (DeviceIoControl(handle, FsctlReadFileUsnData, IntPtr.Zero, 0, record, record.Length, out var got, IntPtr.Zero) && got >= 48)
        {
            // USN_RECORD_V2 (8-byte file references) or V3 (16-byte, ReFS): the USN follows them.
            var major = BitConverter.ToUInt16(record, 4);
            var usn = BitConverter.ToInt64(record, major >= 3 ? 40 : 24);
            if (usn > 0)
                return usn;
        }

        return GetFileInformationByHandleEx(handle, FileBasicInfoClass, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()) && info.ChangeTime != 0
            ? -info.ChangeTime // apart from any USN: a file whose journal came or went reads as changed
            : null;
    }

    /// <summary>
    /// The latest of NTFS's change time, the creation time and the modified time, as a FILETIME, or
    /// null; with <paramref name="alone"/>, the change time only.
    /// </summary>
    /// <remarks>
    /// FAT keeps no change time: Windows gives its modified time in its place, or nothing, and a
    /// pasted file brings that with it from its original. The creation time is when it arrived here.
    /// </remarks>
    public static long? ChangeTimeOf(string path, bool alone = false)
    {
        using var handle = CreateFileW(
            Extended(path), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileBasicInfoClass, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()))
            return null;
        var time = alone ? info.ChangeTime : Math.Max(info.ChangeTime, Math.Max(info.CreationTime, info.LastWriteTime));
        return time > 0 ? time : null;
    }

    private const uint FsctlReadFileUsnData = 0x000900EB;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>A reparse point's tag, as its folder's listing gives it, or null when it can't be read.</summary>
    public static uint? ReparseTagOf(string path)
    {
        using var find = FindFirstFileExW(Extended(path), FindExInfoBasic, out var data, FindExSearchNameMatch, IntPtr.Zero, 0);
        if (find.IsInvalid)
            return null;
        return (data.FileAttributes & 0x400) != 0 ? data.Reserved0 : null;
    }

    // .NET adds this itself for paths past MAX_PATH; a call of our own has to.
    // Not GetFullPath on a path already whole: it takes the trailing dot or space off notes., which
    // the prefix is there to keep (review, 2026-10-28; AUDIT.md §2 kind 45).
    internal static string Extended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        var full = Path.IsPathFullyQualified(path) && !HasDotSegments(path) ? path : Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    // Behind \\?\ nothing is resolved: a . or .. segment, or a /, would be taken as a name.
    private static bool HasDotSegments(string path) =>
        path.Contains('/') || path.Split('\\').Any(name => name is "." or "..");

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    // Pack 4: the times are FILETIMEs, two DWORDs each, so they sit on four-byte boundaries.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;

    // Pack 4, as above: FILETIMEs on four-byte boundaries.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0; // the reparse tag, when FILE_ATTRIBUTE_REPARSE_POINT is set
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }

    private sealed class FindHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern FindHandle FindFirstFileExW(
        string name, int infoLevel, out Win32FindData data, int searchOp, IntPtr filter, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    private const int FileBasicInfoClass = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileBasicInfo information, uint size);

    private const int FileIdInfoClass = 18;

    /// <summary>FILE_ID_INFO: the volume's 64-bit serial and the file's 128-bit ID.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong Low;
        public ulong High;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInformation(SafeFileHandle file, int infoClass, out FileIdInformation information, uint size);
}
