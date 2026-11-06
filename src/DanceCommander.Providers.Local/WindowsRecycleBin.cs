using System.Runtime.InteropServices;
using System.IO.Enumeration;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;

namespace DanceCommander.Providers.Local;

/// <summary>
/// Deletes through the shell's IFileOperation (<see cref="ShellRecycle"/>), so entries land in the
/// Recycle Bin and the user keeps a way back, through our undo only. Only local paths qualify;
/// anything else has to be deleted outright, and the caller is expected to say so plainly first.
/// </summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    /// <summary>
    /// Only a fixed disk under a drive letter of its own has a Recycle Bin. A network share, a
    /// mapped drive, a USB stick or a subst drive has none, and the shell's "recycle" there deletes
    /// outright: asking only whether the address was a file: one sent those to their end with no
    /// question, and a log line promising Ctrl+Z would bring them back.
    /// </summary>
    public bool CanRecycle(FsUri uri) => uri.IsLocal && CanRecycle(uri.Path.Replace('/', '\\'), depth: 0);

    /// <remarks>
    /// The drive letter alone does not say where a file is: a junction under C:, or a volume
    /// mounted on a folder there, can lead to a share or a stick. So every folder on the way is
    /// looked at, and one that leads elsewhere is followed; one that leads to a volume with no
    /// drive letter counts as having no bin, since there is no telling.
    /// </remarks>
    private bool CanRecycle(string path, int depth)
    {
        if (depth > 8 || path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
            return false; // a UNC path, or links in a circle

        if (!IsFixedDrive(path[..2]))
            return false;

        // The folder holding it, not the entry itself: deleting a junction recycles the link.
        for (var at = Path.GetDirectoryName(path); at is not null && at.Length > 3; at = Path.GetDirectoryName(at))
        {
            if (LinkOf(at) is { } link)
                return CanRecycle(link, depth + 1);
        }

        return true;
    }

    /// <summary>Where a folder leads, if it is a junction, a link or a mount point; else null.</summary>
    private string? LinkOf(string directory)
    {
        var now = DateTime.UtcNow;
        lock (_links)
        {
            if (_links.TryGetValue(directory, out var known) && now - known.At < TimeSpan.FromSeconds(10))
                return known.Target;
        }

        string? target = null;
        try
        {
            var info = new DirectoryInfo(directory);
            // No link target is a reparse point of another kind (OneDrive's, deduplication's):
            // still an ordinary folder on this disk.
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is { Length: > 0 } link)
            {
                // A mount point names a volume, not a path: "\\?\Volume{…}\", nothing to follow.
                target = Path.IsPathRooted(link) && link.Length > 1 && link[1] == ':'
                    ? link
                    : Path.IsPathRooted(link) ? @"\\unknown" : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(directory)!, link));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable: taken for an ordinary folder, so what is under it still goes to the bin,
            // through the shell, with Windows' own question as the backstop. Taken for a link to
            // somewhere unknown, it was "no Recycle Bin here", and a folder Replace under it then
            // deleted the old folder for good (review, 2026-10-28; AUDIT.md §2 kind 57).
        }

        lock (_links)
            _links[directory] = (target, now);
        return target;
    }

    /// <summary>A few seconds' memory of which folders are links, since Del asks about every row.</summary>
    private readonly Dictionary<string, (string? Target, DateTime At)> _links = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsFixedDrive(string drive)
    {
        if (IsSubst(drive))
            return false;

        try
        {
            return new DriveInfo(drive).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A drive letter made by <c>subst</c>, which Windows names <c>\??\C:\somewhere</c>.</summary>
    private static bool IsSubst(string drive)
    {
        var target = new char[1024];
        return QueryDosDevice(drive, target, target.Length) > 0 &&
               new string(target).StartsWith(@"\??\", StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, char[] targetPath, int max);

    /// <remarks>
    /// Windows has no call for "would the bin take this". It keeps each disk's bin settings in the
    /// registry, by volume: <c>NukeOnDelete</c> is "Don't move files to the Recycle Bin", and
    /// <c>MaxCapacity</c> its size in MB. The bin makes room by dropping its oldest items, so only
    /// an item larger than the whole bin is destroyed instead. Undocumented, though the same since
    /// Vista; a disk with no settings there (a bin never used or set) is not judged at all, and
    /// Windows' own box stays the backstop.
    /// </remarks>
    public Task<IReadOnlyList<WontFit>> WontFitAsync(IReadOnlyList<FsEntry> entries, CancellationToken ct) =>
        Task.Run(() => WontFit(entries, ct), ct);

    private IReadOnlyList<WontFit> WontFit(IReadOnlyList<FsEntry> entries, CancellationToken ct)
    {
        var found = new List<WontFit>();
        var bins = new Dictionary<string, BinSettings?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = entry.Uri.ToLocalPath();

            // Not judged, so Windows' box stays the backstop: a link, which goes to the bin as the
            // link and not what it leads to, so its target's size says nothing (review,
            // 2026-10-17); and anything under a junction, whose bin is the far disk's, not the
            // one its path names.
            if (IsLink(path) || UnderALink(path))
                continue;
            if (VolumeOf(path) is not { } volume)
                continue;
            if (!bins.TryGetValue(volume.Guid, out var bin))
                bins[volume.Guid] = bin = SettingsOf(volume.Guid);
            if (bin is null)
                continue;

            // A long path comes as \\?\C:\…, and so does its root.
            var where = volume.Root.Replace(@"\\?\", "", StringComparison.Ordinal).TrimEnd('\\');
            if (bin.Off)
                found.Add(new WontFit(entry, $"The Recycle Bin on {where} is turned off in its settings."));
            else if (!entry.IsDirectory && LengthOf(path) > bin.MaxBytes)
                found.Add(new WontFit(entry, $"{entry.Name} is larger than the whole Recycle Bin on {where} ({AboutProvider.Bytes(bin.MaxBytes)})."));
        }

        return found;
    }

    private static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) && new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // can't tell: not judged
        }
    }

    private bool UnderALink(string path)
    {
        for (var at = Path.GetDirectoryName(path); at is not null && Path.GetPathRoot(at) != at; at = Path.GetDirectoryName(at))
        {
            if (LinkOf(at) is not null)
                return true;
        }
        return false;
    }

    private sealed record BinSettings(bool Off, long MaxBytes);

    /// <summary>The size of the bin <paramref name="path"/> would go to, as its settings say; null if they don't.</summary>
    internal static long? BinSizeOf(string path) =>
        VolumeOf(path) is { } volume && SettingsOf(volume.Guid) is { } bin ? bin.MaxBytes : null;

    public long? CapacityOf(FsUri uri)
    {
        if (!uri.IsLocal)
            return 0;
        var path = uri.ToLocalPath();
        if (UnderALink(path) || VolumeOf(path) is not { } volume || SettingsOf(volume.Guid) is not { } bin)
            return null;
        return bin.Off ? 0 : bin.MaxBytes;
    }

    private const string BinSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\";

    private static BinSettings? SettingsOf(string volumeGuid)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BinSettingsKey + volumeGuid);
            if (key?.GetValue("MaxCapacity") is not int megabytes)
                return null;
            return new BinSettings(key.GetValue("NukeOnDelete") is int nuke && nuke != 0, (long)(uint)megabytes * 1024 * 1024);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The volume a path is on, as its GUID ("{…}") and the folder it is mounted at.</summary>
    internal static (string Guid, string Root)? VolumeOf(string path)
    {
        var root = new char[1024];
        var name = new char[64];
        if (!GetVolumePathName(path, root, root.Length))
            return null;
        var mount = new string(root).TrimEnd('\0');
        if (!GetVolumeNameForVolumeMountPoint(mount, name, name.Length))
            return null;

        // "\\?\Volume{…}\"
        var volume = new string(name).TrimEnd('\0');
        var open = volume.IndexOf('{');
        var close = volume.IndexOf('}');
        return open >= 0 && close > open ? (volume[open..(close + 1)], mount) : null;
    }

    /// <remarks>
    /// A file only, whose size is known at once. A folder is not walked: one of 85,000 files took
    /// Del 12 s to start, with nothing on screen (2026-10-17). One too large for the bin is still
    /// kept: Windows, unable to recycle it whole, deletes it file by file, and
    /// <see cref="ShellRecycle"/> refuses the first such delete (the torture run, 2026-10-17).
    /// </remarks>
    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0; // can't tell: Windows' own box is still there
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, char[] volumePathName, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, char[] volumeName, int length);

    /// <summary>
    /// Undoes a delete by reading the Recycle Bin's own records rather than its shell view.
    /// </summary>
    /// <remarks>
    /// Each deleted item is a pair in <c>X:\$Recycle.Bin\&lt;user SID&gt;</c>: <c>$R…</c> is the
    /// file or folder itself, and <c>$I…</c> records where it came from and when. Putting one back
    /// is moving the <c>$R</c> to the recorded path and removing its <c>$I</c>, which is what
    /// Explorer's Restore does too. The shell's view of the bin names items for display, extension
    /// hidden or not, which is no way to find the one that was <c>C:\work\a.txt</c>.
    /// </remarks>
    // Off the UI thread, which is where Ctrl+Z starts it: a full bin is thousands of records to read.
    public Task RestoreAsync(IReadOnlyList<FsUri> paths, DateTimeOffset deletedSince, CancellationToken ct) =>
        Task.Run(() => Restore(paths, deletedSince, DateTimeOffset.MaxValue, ct), ct);

    public Task RestoreAsync(IReadOnlyList<FsUri> paths, DateTimeOffset deletedSince, DateTimeOffset deletedBefore, CancellationToken ct) =>
        Task.Run(() => Restore(paths, deletedSince, deletedBefore, ct), ct);

    private static void Restore(IReadOnlyList<FsUri> paths, DateTimeOffset deletedSince, DateTimeOffset deletedBefore, CancellationToken ct)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Could not tell whose Recycle Bin to look in.");
        // A second before the start, as before. None after the end: the end was read once the
        // recycle had returned, by the same clock the bin stamps with, so anything stamped later
        // is someone else's (review, 2026-10-15: a second's slack let a later one win).
        var since = deletedSince.ToUniversalTime().AddSeconds(-1);
        var before = deletedBefore == DateTimeOffset.MaxValue ? deletedBefore : deletedBefore.ToUniversalTime();

        foreach (var original in paths.Select(p => p.ToLocalPath()))
        {
            ct.ThrowIfCancellationRequested();
            var bin = Path.Combine(Path.GetPathRoot(original) ?? string.Empty, "$Recycle.Bin", sid);
            // Either form: the shell keeps a long path's \\?\ in its record (the torture run's
            // long paths, 2026-10-18), and ours is written plain, as for any other.
            var plain = original.StartsWith(PrefixLong, StringComparison.Ordinal) ? original[PrefixLong.Length..] : original;

            // Only the records written about then are opened, by their own file time: a bin of
            // 20,000 items made every undo read all 20,000 (2026-10-17). The slack is generous,
            // two hours for FAT's local times across a change of summer time; the record's own
            // time below is what decides.
            var record = Directory.Exists(bin)
                ? new DirectoryInfo(bin).EnumerateFiles("$I*")
                    .Where(info => info.LastWriteTimeUtc >= since.UtcDateTime.AddHours(-2) &&
                                   (before == DateTimeOffset.MaxValue || info.LastWriteTimeUtc <= before.UtcDateTime.AddHours(2)))
                    .Select(info => (Info: info.FullName, Entry: ReadRecord(info.FullName)))
                    .Where(r => r.Entry is { } e && e.Deleted >= since && e.Deleted <= before &&
                                (string.Equals(e.Path, plain, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(e.Path, original, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(r => r.Entry!.Value.Deleted)
                    .FirstOrDefault()
                : default;

            // "Something else has that name" was said of the very file, put back from outside
            // (2026-10-17). Which it is can't be told from here, so both facts and no guess.
            if (record.Info is null && (File.Exists(original) || Directory.Exists(original)))
                throw new IOException($"{Path.GetFileName(original)} is not in the Recycle Bin from this delete, and something by that name is in its place now.");
            if (record.Info is null)
                throw new IOException($"{Path.GetFileName(original)} is no longer in the Recycle Bin.");
            if (File.Exists(original) || Directory.Exists(original))
                throw new IOException($"{Path.GetFileName(original)} cannot be put back: something else has that name now.");

            var content = Path.Combine(bin, "$R" + Path.GetFileName(record.Info)[2..]);
            if (Directory.Exists(content))
                Directory.Move(content, original);
            else
                File.Move(content, original);
#pragma warning disable RS0030 // §1.6: the bin's $I record of an item just put back; the item itself is in place.
            File.Delete(record.Info);
#pragma warning restore RS0030
        }
    }

    /// <summary>Folders with fewer files go through the shell, which keeps the bin's own accounts.</summary>
    internal const int DirectFrom = 1_000;

    /// <summary>
    /// Sends a large folder to the bin the way <see cref="Restore"/> takes one out, in reverse: a
    /// <c>$I</c> record of where it was, then the folder renamed beside it as its <c>$R</c>. The
    /// shell walks every file first, so 85,000 of them took 28 s, and an undo after it as long
    /// (2026-10-17); this is a rename and one listing to total the size. Explorer's bin shows it
    /// and restores it as one of its own.
    /// </summary>
    /// <returns>False, with nothing changed, for anything left to the shell.</returns>
    /// <remarks>
    /// The record first: stopped between the two, there is a record of a folder still in its
    /// place, which nothing reads as gone. The other way round, the folder would sit in the bin
    /// with nothing saying where from, invisible in Explorer's view of it. What the shell judges
    /// is left to it: a link, a disk with the bin turned off or no settings, a folder larger
    /// than the bin, one it can't read all of, one held open (the rename fails, and the shell's
    /// error says who has it). Windows drops the bin's oldest items when it overflows; it counts
    /// this one from its record the next time it adds one of its own.
    /// </remarks>
    private bool RecycleDirectly(FsUri uri, CancellationToken ct)
    {
        var path = uri.ToLocalPath();
        var folder = new DirectoryInfo(path);
        if (!folder.Exists || folder.Attributes.HasFlag(FileAttributes.ReparsePoint) || UnderALink(path))
            return false;
        if (VolumeOf(path) is not { } volume || SettingsOf(volume.Guid) is not { Off: false } settings)
            return false;
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        var bin = sid is null ? null : Path.Combine(volume.Root, "$Recycle.Bin", sid);
        if (bin is null || !Directory.Exists(bin))
            return false; // never made: the shell makes it as Windows wants it

        // A cloud folder (OneDrive, Dropbox…) goes through the shell and so through its sync
        // provider: renamed out of the sync root behind its back, what it does with files that
        // are only online is not ours to find out (review, 2026-10-18).
        if (InOrHoldsASyncRoot(path) || (folder.Attributes & Cloud) != 0)
            return false;

        long size = 0;
        var files = 0;
        try
        {
            // Links inside are not walked into: the folder takes the link, not what it leads to,
            // and .NET's own recursion would follow a junction anywhere, round in a circle too
            // (review, 2026-10-18).
            var walk = new FileSystemEnumerable<(long Length, FileAttributes Attributes, bool IsDirectory)>(
                path,
                (ref FileSystemEntry entry) => (entry.Length, entry.Attributes, entry.IsDirectory),
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
            {
                ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
            };
            foreach (var entry in walk)
            {
                if ((entry.Attributes & Cloud) != 0)
                    return false;
                if (entry.IsDirectory)
                    continue;
                if ((++files & 1023) == 0)
                    ct.ThrowIfCancellationRequested();
                size += entry.Length;
                if (size > settings.MaxBytes)
                    return false; // the shell's to refuse, file by file
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (files < DirectFrom)
            return false;
        ct.ThrowIfCancellationRequested();

        // As Windows names them: six letters or digits, and the item's own extension.
        var extension = folder.Extension;
        string record, content;
        do
        {
            var name = string.Concat(Enumerable.Range(0, 6).Select(_ => NameChars[Random.Shared.Next(NameChars.Length)]));
            record = Path.Combine(bin, "$I" + name + extension);
            content = Path.Combine(bin, "$R" + name + extension);
        }
        while (Path.Exists(record) || Path.Exists(content));

        // Where it came from as the shell writes it: a plain path, and \\?\ only past MAX_PATH,
        // where the shell's own records keep it (the torture run's long paths, 2026-10-18).
        var plain = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        var original = plain.Length < 260 ? plain : @"\\?\" + plain;
        var written = false;
        try
        {
            // On the disk before the rename: power lost just after it, the folder would
            // otherwise sit in the bin beside a record of zeros, shown nowhere (review, 2026-10-18).
            WriteRecord(record, original, size, DateTimeOffset.UtcNow, () => written = true);
            Directory.Move(path, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // CreateNew: a record that was there already is not ours, and stays. Nor ours, if
            // the folder went all the same: then it is what says where the folder came from.
            if (written && Directory.Exists(path) && !Path.Exists(content))
            {
#pragma warning disable RS0030 // §1.6: the record just written for a folder that stayed where it was; it describes nothing.
                File.Delete(record);
#pragma warning restore RS0030
            }
            return false;
        }

        // Explorer windows on the folder above see it go.
        SHChangeNotify(ShcneRmdir, ShcnfPath, original, IntPtr.Zero);
        return true;
    }

    private const string NameChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private const string PrefixLong = @"\\?\";

    /// <summary>A version 2 record (see <see cref="ReadRecord"/>), made new: never over another.</summary>
    internal static void WriteRecord(string file, string original, long size, DateTimeOffset deleted, Action? created = null)
    {
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        created?.Invoke();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.Unicode);
        writer.Write(2L);
        writer.Write(size);
        writer.Write(deleted.ToFileTime());
        writer.Write(original.Length + 1); // with its terminating null, as Windows counts it
        writer.Write(original.ToCharArray());
        writer.Write('\0');
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS, _RECALL_ON_OPEN, _PINNED, _UNPINNED and Offline: a
    /// cloud file or folder, as seen from here.
    /// </summary>
    private const FileAttributes Cloud = (FileAttributes)(0x400000 | 0x40000 | 0x80000 | 0x100000) | FileAttributes.Offline;

    /// <summary>
    /// Whether <paramref name="path"/> is in a folder a sync provider keeps (OneDrive, Dropbox…),
    /// or holds one, as each registers it with Windows for this user. Asked each time, so one set
    /// up while the app runs counts too (review, 2026-10-18).
    /// </summary>
    internal static bool InOrHoldsASyncRoot(string path)
    {
        var plain = (path.StartsWith(PrefixLong, StringComparison.Ordinal) ? path[PrefixLong.Length..] : path).TrimEnd('\\');
        return SyncRoots().Select(root => root.TrimEnd('\\')).Any(root =>
            string.Equals(plain, root, StringComparison.OrdinalIgnoreCase) ||
            plain.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(plain + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> SyncRoots()
    {
        var roots = new List<string>();
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        try
        {
            using var manager = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            foreach (var name in manager?.GetSubKeyNames() ?? [])
            {
                using var users = manager!.OpenSubKey(name + @"\UserSyncRoots");
                if (sid is not null && users?.GetValue(sid) is string root && root.Length > 0)
                    roots.Add(root);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        // The environment's own say too, should the registry not.
        foreach (var variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root)
                roots.Add(root);
        }
        return roots;
    }

    private const int ShcneRmdir = 0x10;
    private const uint ShcnfPath = 0x5;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

    /// <summary>
    /// A <c>$I</c> record: version (1 before Windows 10, 2 since), size, deletion time as a
    /// FILETIME, then the original path, fixed at 260 characters in version 1 and length-prefixed
    /// in version 2. Null for anything that does not read as one.
    /// </summary>
    internal static (string Path, DateTimeOffset Deleted)? ReadRecord(string file)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(file), System.Text.Encoding.Unicode);
            var version = reader.ReadInt64();
            reader.ReadInt64(); // size
            var deleted = DateTimeOffset.FromFileTime(reader.ReadInt64());
            var length = version >= 2 ? reader.ReadInt32() : 260;
            var path = new string(reader.ReadChars(length)).TrimEnd('\0');
            return (path, deleted.ToUniversalTime());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public async Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct)
    {
        if (paths.Count == 0)
            return;

        // Checked again here rather than trusted: past this line the shell decides, and it
        // decides by deleting.
        if (paths.Any(p => !CanRecycle(p)))
            throw new NotSupportedException("Only files on a fixed local disk can go to the Recycle Bin.");

        // The shell reads paths without \\?\, and so takes notes. for the folder notes beside it;
        // a record of one in the bin would be put back there too (AUDIT.md §2 kind 45).
        if (paths.FirstOrDefault(p => FsUri.HasNameWin32Trims(p.Path)) is { } trimmed)
            throw new IOException(
                $"The Recycle Bin can't take “{trimmed.Name}”: Windows would read the name without its last dot or space, " +
                "which is another file. It was left where it is; rename it, or delete it permanently.");

        // A large folder by our own hand, everything else through the shell. Stopped part way,
        // the caller looks at what is still in place, as after the shell.
        var rest = new List<FsUri>();
        foreach (var path in paths)
        {
            if (!await Task.Run(() => RecycleDirectly(path, ct), ct).ConfigureAwait(false))
                rest.Add(path);
        }
        if (rest.Count == 0)
            return;
        paths = rest;

        // The shell rejects the \\?\ prefix, so these have to be plain paths.
        var outcome = await ShellRecycle.RecycleAsync(paths.Select(p => p.Path.Replace('/', '\\')).ToList()).ConfigureAwait(false);

        if (outcome.Refused)
            throw new IOException(
                $"Windows would have deleted {NameOf(outcome.Failed)} outright rather than recycle it, so it was left where it is.");

        // Said after the fact, so the record doesn't promise a Ctrl+Z that has nothing to bring
        // back: the caller looks at what is still in place and records the rest as it went.
        if (outcome.NotRecycled is { Length: > 0 } outright && !Path.Exists(outright))
            throw new IOException($"Windows deleted {NameOf(outright)} outright: it did not go to the Recycle Bin.");
        if (outcome.Result < 0)
        {
            // A folder another program stands in, or holds a file in, can come back as access
            // denied or as a shell error of its own (a sharing violation on the way into the bin)
            // rather than "in use": looked for, it gets the in-use question and its Retry, and the
            // program is looked for where it holds, not at the folder above. Only for those: after
            // any other error, a file that happens to be open somewhere isn't its cause, and naming
            // it hid the real one behind a Retry that could never work (review, 2026-10-11).
            var sharing = outcome.Result is ErrorSharingViolation or ErrorLockViolation or ShellSharingViolation or ShellSharingViolationDest;
            if (sharing || outcome.Result is ErrorAccessDenied or ShellAccessDenied or ShellInvalidFiles or ShellDirectoryNotEmpty)
            {
                var held = OpenElsewhere.Under(paths.Select(p => p.ToLocalPath()));
                if (held.Count > 0)
                    throw new InUseException(held, "Another program has it open, so it cannot go to the Recycle Bin yet.");
                if (sharing)
                    throw new InUseException(
                        paths.Select(p => p.ToLocalPath()).ToList(),
                        "Another program has it open, so it cannot go to the Recycle Bin yet.");
            }

            if (outcome.Result is not (UserCancelled or ErrorCancelled))
                throw new IOException(Explain(outcome.Result));
        }
        if (outcome.Aborted || outcome.Result is UserCancelled or ErrorCancelled)
            throw new OperationCanceledException(ct);
    }

    private static string NameOf(string? path) => string.IsNullOrEmpty(path) ? "it" : Path.GetFileName(path);

    private const int ErrorAccessDenied = unchecked((int)0x80070005);
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    // The copy engine's own codes (sherrors.h), which Windows has no message for.
    private const int UserCancelled = unchecked((int)0x80270000);
    private const int ShellInvalidFiles = unchecked((int)0x8027001B);
    private const int ShellAccessDenied = unchecked((int)0x80270021);
    private const int ShellSharingViolation = unchecked((int)0x80270027);

    /// <summary>The destination's: the move into the bin, what a folder a shell window stands in gets.</summary>
    private const int ShellSharingViolationDest = unchecked((int)0x80270028);
    private const int ShellDirectoryNotEmpty = unchecked((int)0x8027003D);

    /// <summary>
    /// In words (§1.3). A Win32 error has Windows' message; the copy engine's own codes have none,
    /// so the few a recycle meets are said here and the rest keep their number, since it is the
    /// one thing that can be looked up.
    /// </summary>
    private static string Explain(int result) =>
        (result & 0xFFFF0000) == 0x80070000
            ? $"Windows could not move it to the Recycle Bin: {new System.ComponentModel.Win32Exception(result & 0xFFFF).Message}"
            : result switch
            {
                unchecked((int)0x80270023) => "Windows could not move it to the Recycle Bin: it is no longer there.",
                unchecked((int)0x80270036) or unchecked((int)0x8027003A) =>
                    "Windows could not move it to the Recycle Bin: this disk's Recycle Bin is turned off or missing.",
                unchecked((int)0x80270037) => "Windows could not move it to the Recycle Bin: it is too large for it.",
                unchecked((int)0x80270038) => "Windows could not move it to the Recycle Bin: its path is too deep for it.",
                _ => $"Windows could not move it to the Recycle Bin (shell error 0x{result:X8}).",
            };
}
