using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DanceCommander.Providers.Local;

/// <summary>
/// Folder links, junctions and directory symbolic links, read and made as themselves: what a move
/// carries instead of walking through one (<see cref="Core.Fs.IMovesLinks"/>).
/// </summary>
internal static class DirectoryLinks
{
    /// <summary>A link as the disk holds it: its kind, and the target in both of its forms.</summary>
    internal readonly record struct Link(uint Tag, string Substitute, string Print)
    {
        public bool IsJunction => Tag == JunctionTag;

        public bool IsSymbolic => Tag == SymbolicTag;

        /// <summary>A drive mounted in a folder: a junction to a volume, which only the mount manager makes.</summary>
        public bool IsMountedDrive =>
            IsJunction && Substitute.StartsWith(@"\??\Volume{", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The link at <paramref name="path"/>, or null when there is none or it can't be read.</summary>
    public static Link? Read(string path)
    {
        using var handle = CreateFileW(
            FileIdentity.Extended(path), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        var buffer = new byte[MaximumReparseDataSize];
        if (!DeviceIoControl(handle, GetReparsePoint, null, 0, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;

        var tag = BitConverter.ToUInt32(buffer, 0);
        // A symbolic link's names start after its flags; a junction has none.
        var names = tag switch
        {
            JunctionTag => 16,
            SymbolicTag => 20,
            _ => -1,
        };
        if (names < 0)
            return new Link(tag, string.Empty, string.Empty);

        string Name(int at) => Encoding.Unicode.GetString(
            buffer, names + BitConverter.ToUInt16(buffer, at), BitConverter.ToUInt16(buffer, at + 2));
        return new Link(tag, Name(8), Name(12));
    }

    /// <summary>
    /// Moves the link at <paramref name="source"/> to <paramref name="target"/>, never over
    /// anything there; what it points to stays where it is.
    /// </summary>
    /// <remarks>
    /// On one volume a rename, which moves the link and not its folder. Across volumes the same
    /// kind of link with the same target is made at <paramref name="target"/> first, read back,
    /// and only then is the original removed, and only while it is still the link that was read:
    /// at every step there is a link in one place or both, never in neither. A symbolic link keeps
    /// its text, relative or not, as a rename would.
    /// </remarks>
    public static void Move(string source, string target)
    {
        var link = Movable(source);

        if (string.Equals(Root(source), Root(target), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Move(source, target);
                return;
            }
            catch (IOException ex) when (ex.HResult == NotSameDevice)
            {
                // One letter, two volumes: a folder mounted on the way. Made again, as below.
            }
        }

        Remake(source, target, link);
    }

    /// <summary>The move across volumes: the link made again at the target, then the original removed.</summary>
    internal static void Remake(string source, string target) => Remake(source, target, Movable(source));

    private static Link Movable(string source)
    {
        var link = Read(source) ?? throw new IOException($"{Path.GetFileName(source)} is no longer a link.");
        if (link.IsMountedDrive)
            throw new NotSupportedException($"{Path.GetFileName(source)} is a drive mounted in a folder, which Disk Management moves.");
        if (!link.IsJunction && !link.IsSymbolic)
            throw new NotSupportedException($"{Path.GetFileName(source)} is a kind of link that can't be moved from here.");
        return link;
    }

    private static void Remake(string source, string target, Link link)
    {
        // A link on a share names the server's own drives: made again on this computer, the same
        // text names this computer's, another folder, and the other way round (review, 2026-10-23).
        if (OnShare(source) || OnShare(target))
            throw new NotSupportedException(
                $"{Path.GetFileName(source)} is a link on a network share, whose target is on the server's own drives; it can only be renamed there.");

        var pointsTo = new DirectoryInfo(source).LinkTarget;

        if (link.IsJunction)
            MakeJunction(target, link.Substitute, link.Print);
        else
            MakeSymbolic(target, link);

        if (Read(target) is not { } made || made.Tag != link.Tag || new DirectoryInfo(target).LinkTarget != pointsTo)
            throw new IOException($"The link made at {target} does not point where {Path.GetFileName(source)} does; both are kept.");

        // Changed since it was read (another program replaced it with a folder of its own, say):
        // not ours to remove, and the new one at the target stays beside it.
        if (Read(source) != link)
            throw new IOException($"{Path.GetFileName(source)} changed while it was moved; it is kept, and so is the new link.");

        try
        {
#pragma warning disable RS0030 // §1.6: a folder link, still the one read above, now made again and read back at the target; removing a link without recursing never touches what it points to.
            Directory.Delete(source);
#pragma warning restore RS0030
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"{Path.GetFileName(source)} was made at {target} but could not be removed where it was ({ex.Message.TrimEnd('.')}), so it is in both places now.", ex);
        }
    }

    private static string? Root(string path) => Path.GetPathRoot(Plain(path));

    /// <summary>
    /// The path without a <c>\\?\</c> prefix, which a long path has and a short one not: compared
    /// as they came, two paths on one volume or one share looked like two (review, 2026-10-23).
    /// </summary>
    private static string Plain(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + full[8..];
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full[4..] : full;
    }

    /// <summary>On a network share: a UNC path, or a drive letter mapped to one.</summary>
    private static bool OnShare(string path)
    {
        var plain = Plain(path);
        if (plain.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        return Root(path) is { Length: >= 2 } root && root[1] == ':' && new DriveInfo(root[..2]).DriveType == DriveType.Network;
    }

    private static void MakeJunction(string path, string substitute, string print)
    {
        // Fails if anything is at the name, so nothing there is ever made into a link.
        if (!CreateDirectoryW(FileIdentity.Extended(path), IntPtr.Zero))
            throw new IOException($"Could not make {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        try
        {
            using var handle = CreateFileW(
                FileIdentity.Extended(path), GenericWrite, 0, IntPtr.Zero, OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var data = JunctionData(substitute, print);
            if (!DeviceIoControl(handle, SetReparsePoint, data, data.Length, null, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Win32Exception ex)
        {
            try
            {
#pragma warning disable RS0030 // §1.6: the empty folder made just above, with no link set on it; refused if anything has been put in it since.
                Directory.Delete(path);
#pragma warning restore RS0030
            }
            catch (IOException)
            {
            }

            throw new IOException($"Could not make a junction at {path}: {ex.Message}", ex);
        }
    }

    private static void MakeSymbolic(string path, Link link)
    {
        // Relative text stays relative, as it would through a rename; an absolute target is given
        // in the form people write, which Windows stores again as both names.
        var text = link.Print.Length > 0 ? link.Print
            : link.Substitute.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + link.Substitute[8..]
            : link.Substitute.StartsWith(@"\??\", StringComparison.Ordinal) ? link.Substitute[4..]
            : link.Substitute;
        try
        {
            Directory.CreateSymbolicLink(path, text);
        }
        catch (IOException ex) when (ex.HResult == PrivilegeNotHeld)
        {
            throw new IOException(
                $"Windows lets a symbolic link be made only with Developer Mode on or as an administrator, so {Path.GetFileName(path)} could not be made again here.", ex);
        }
    }

    /// <summary>A mount-point reparse buffer: the substitute name, then the print name, each ending in a NUL.</summary>
    private static byte[] JunctionData(string substitute, string print)
    {
        var sub = Encoding.Unicode.GetBytes(substitute);
        var shown = Encoding.Unicode.GetBytes(print);
        var length = 8 + sub.Length + 2 + shown.Length + 2;

        var data = new byte[8 + length];
        BitConverter.GetBytes(JunctionTag).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)length).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)sub.Length).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)(sub.Length + 2)).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)shown.Length).CopyTo(data, 14);
        sub.CopyTo(data, 16);
        shown.CopyTo(data, 16 + sub.Length + 2);
        return data;
    }

    private const uint JunctionTag = 0xA0000003;
    private const uint SymbolicTag = 0xA000000C;
    private const int MaximumReparseDataSize = 16 * 1024;
    private const uint GetReparsePoint = 0x000900A8;
    private const uint SetReparsePoint = 0x000900A4;
    private const uint FileReadAttributes = 0x80;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int NotSameDevice = unchecked((int)0x80070011);
    private const int PrivilegeNotHeld = unchecked((int)0x80070522);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize,
        out int returned, IntPtr overlapped);
}
