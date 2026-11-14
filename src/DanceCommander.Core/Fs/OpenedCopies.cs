using System.Collections.Concurrent;
using System.Globalization;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Transfer;

namespace DanceCommander.Core.Fs;

/// <summary>A local copy made to be opened.</summary>
/// <param name="Unmarked">
/// It should carry the "from the internet" mark and the disk would not take it (a FAT RAM disk, a
/// redirected temp folder), so Windows would open it without Protected View or SmartScreen.
/// </param>
public sealed record OpenedCopy(string Path, bool Unmarked);

/// <summary>What the drive holding the copies has room for.</summary>
/// <param name="Drive">"C:", as the user is told it.</param>
/// <param name="Limit">The largest file its file system holds, where it has a limit (FAT).</param>
public sealed record CopyRoom(string Drive, long Free, VolumeLimits.Limit? Limit);

/// <summary>A copy saved into since it was made: the user's work.</summary>
/// <param name="From">Where its original is, as the pane shows addresses; null if not recorded.</param>
public sealed record ChangedCopy(string Path, string? From);

/// <summary>What a tidy did.</summary>
/// <param name="Recycled">Copies, and abandoned downloads, sent to the Recycle Bin.</param>
/// <param name="Changed">
/// Changed copies not yet pointed out. Each is found again on every tidy until
/// <see cref="OpenedCopies.MarkTold"/> says the user has seen it.
/// </param>
public sealed record TidiedCopies(int Recycled, IReadOnlyList<ChangedCopy> Changed);

/// <summary>
/// Copies a file that has no path on this machine (on a server, inside an archive) to one that
/// does, so that Enter can hand it to the program Windows opens it with.
/// </summary>
/// <remarks>
/// <para>Each copy gets a folder of its own under <see cref="Root"/>, so it keeps its name (the
/// program shows it in its title bar) and two files of one name never meet. Nothing there is
/// ever written over: the bytes go to <c>name.part</c>, made new, and take the name only when
/// whole, so a download that stops leaves something that says it is incomplete.</para>
/// <para>The copy is read-only. Saving it would change only the copy, never the file it came
/// from, and an editor that says "read-only" on Save says so at the moment it matters; Save As
/// still puts the work somewhere real.</para>
/// <para>Beside it a record (<c>.dc-opened</c>) keeps its size and time as written, so that
/// <see cref="TidyAsync"/> can tell a copy nobody has touched, which goes to the Recycle Bin after
/// <see cref="KeptFor"/>, from one saved into, which is the user's work: said once, and left
/// (DESIGN.md §1.6). The bin rather than deletion, because the original may have gone from the
/// server since, and then this was the last copy.</para>
/// </remarks>
public sealed class OpenedCopies(IProviderRegistry providers, string? root = null)
{
    /// <summary>How long a copy nobody has changed is kept before it goes to the Recycle Bin.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    private const int BufferSize = 81920;
    private const string RecordName = ".dc-opened";

    /// <summary>Made once a changed copy has been pointed out, so it is pointed out only once.</summary>
    private const string ToldName = ".dc-told";

    private readonly ConcurrentDictionary<FsUri, byte> _fetching = new();

    /// <summary>Where the copies go: <c>%TEMP%\DanceCommander\Opened</c>.</summary>
    public string Root { get; } = root ?? Path.Combine(Path.GetTempPath(), "DanceCommander", "Opened");

    /// <summary>
    /// Whether the file comes from a server rather than from inside an archive on this machine,
    /// which is what the pane calls it while it waits: downloading or extracting.
    /// </summary>
    public static bool IsDownload(FsUri file) => !Outermost(file).IsLocal;

    /// <summary>The room on the drive the copies go to; null when it can't be told (a share).</summary>
    public CopyRoom? Room()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(Root));
            if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
            var drive = new DriveInfo(root);
            return new CopyRoom(drive.Name.TrimEnd('\\'), drive.AvailableFreeSpace, VolumeLimits.For(drive.DriveFormat));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies <paramref name="file"/> to a new local path and returns it; null when a copy of the
    /// same file is already on its way, which will be opened when it arrives.
    /// </summary>
    /// <param name="moved">Told the bytes copied so far, as they go.</param>
    public async Task<OpenedCopy?> FetchAsync(FsEntry file, Action<long>? moved, CancellationToken ct)
    {
        if (!_fetching.TryAdd(file.Uri, 0))
            return null;
        try
        {
            return await CopyAsync(file, moved, ct).ConfigureAwait(false);
        }
        finally
        {
            _fetching.TryRemove(file.Uri, out _);
        }
    }

    private async Task<OpenedCopy> CopyAsync(FsEntry file, Action<long>? moved, CancellationToken ct)
    {
        var provider = providers.Resolve(file.Uri);
        var folder = NewFolder();
        var name = ForeignName.ToFileName(file.Name);
        // A file named like the record beside it would be taken for it, and the tidy would then
        // remove the copy as bookkeeping (review, 2026-11-14).
        if (IsOurs(name))
            name = "_" + name;
        var final = Path.Combine(folder, name);
        var part = Path.Combine(folder, TemporaryName.Beside(name, ".part"));

        long done = 0;
        await using (var source = await provider.OpenReadAsync(file.Uri, ct).ConfigureAwait(false))
        await using (var target = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                moved?.Invoke(done);
            }
        }

        // A stream can end early without saying so (a dropped connection, a damaged archive), and
        // a cut copy opened as whole makes the original look broken. As the transfer does, a short
        // count is checked against a fresh look: the file may only have shrunk since it was listed.
        if (!file.SizeUnknown && done < file.Size)
        {
            var now = await provider.StatAsync(file.Uri, ct).ConfigureAwait(false);
            if (now is null || done < now.Size)
                throw new IOException($"The copy ended after {done:N0} of {(now ?? file).Size:N0} bytes.");
        }

        // Marked while it is still the .part, so no moment has an unmarked download at the real
        // name; the stream goes with the rename. Explorer passes a zip's mark on to what it
        // extracts; a file from a server always gets one.
        var unmarked = false;
        if (IsDownload(file.Uri) || ZoneMark.IsFromInternet(Outermost(file.Uri).ToLocalPath()))
            unmarked = !ZoneMark.MarkFromInternet(part);

        // Two arguments: never over anything. The folder is new, so nothing should be there.
        File.Move(part, final);

        // Its own date, not today's, so a program that shows it (or a later copy) tells the truth.
        // A date Windows can't hold (before 1601, a server's "no date") leaves today's.
        if (file.Modified is { } modified && modified.UtcDateTime >= DateTime.FromFileTimeUtc(0))
            File.SetLastWriteTimeUtc(final, modified.UtcDateTime);
        File.SetAttributes(final, File.GetAttributes(final) | FileAttributes.ReadOnly);
        Record.Write(folder, name, final, file.Uri.Display);
        return new OpenedCopy(final, unmarked);
    }

    /// <summary>
    /// Sends copies nobody has changed, opened more than <see cref="KeptFor"/> before
    /// <paramref name="now"/>, to the Recycle Bin, and abandoned downloads with them; returns the
    /// changed copies not pointed out before. Anything it can't be sure of is left as it is: a
    /// folder without a record, a copy a program has open, a disk with no bin, a copy too large
    /// for the bin (Windows would destroy it instead, after a box of its own).
    /// </summary>
    public async Task<TidiedCopies> TidyAsync(IRecycleBin bin, DateTimeOffset now, CancellationToken ct)
    {
        var recycled = 0;
        var changed = new List<ChangedCopy>();
        // A link, at the root or in it, leads to folders that are not ours: the tidy would have
        // judged whatever is there by our rules (review, 2026-11-14).
        if (!Directory.Exists(Root) || IsLink(Root))
            return new TidiedCopies(0, changed);

        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (IsLink(folder))
                    continue;
                if (Record.Read(folder) is { } record)
                {
                    if (now - record.Opened < KeptFor)
                        continue;
                    switch (await TidyCopyAsync(folder, record, bin, ct).ConfigureAwait(false))
                    {
                        case Outcome.Recycled:
                            recycled++;
                            break;
                        case Outcome.ChangedNews:
                            changed.Add(new ChangedCopy(Path.Combine(folder, record.Name), record.From));
                            break;
                    }
                }
                else
                {
                    recycled += await TidyAbandonedAsync(folder, bin, now, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                           or InvalidOperationException
                                           || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // Something there is busy, not ours to touch, or the shell gave up on it; the next
                // launch looks again. InvalidOperationException covers the bin's own refusals.
            }
        }

        return new TidiedCopies(recycled, changed);
    }

    /// <summary>
    /// Records that the user has been shown <paramref name="copy"/> as changed, so it is not said
    /// again. Called once the notice is answered, not when it is put up: a window closed before
    /// it was read would otherwise never say it.
    /// </summary>
    public static void MarkTold(string copy)
    {
        try
        {
            var told = Path.Combine(Path.GetDirectoryName(copy)!, ToldName);
            using (new FileStream(told, FileMode.CreateNew, FileAccess.Write))
                File.SetAttributes(told, FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Already marked, or the folder has gone: either way it needn't be said again.
        }
    }

    private static bool IsOurs(string name) =>
        name.Equals(RecordName, StringComparison.OrdinalIgnoreCase) || name.Equals(ToldName, StringComparison.OrdinalIgnoreCase);

    private static bool IsLink(string folder) => new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint);

    private enum Outcome
    {
        Left,
        Recycled,
        ChangedNews,
    }

    private static async Task<Outcome> TidyCopyAsync(string folder, Record record, IRecycleBin bin, CancellationToken ct)
    {
        var copy = new FileInfo(Path.Combine(folder, record.Name));
        if (!copy.Exists)
        {
            // Moved or removed by the user: only the record is left, which is ours.
            RemoveOurs(folder);
            return Outcome.Left;
        }

        // Read-only on a FAT disk, a change to or from summer time moves the time by an hour; the
        // copy is then taken as changed and kept, which errs the safe way.
        if (copy.Length != record.Size || copy.LastWriteTimeUtc.Ticks != record.Written)
            return File.Exists(Path.Combine(folder, ToldName)) ? Outcome.Left : Outcome.ChangedNews;

        var uri = FsUri.Local(copy.FullName);
        if (!bin.CanRecycle(uri) || !FitsInBin(bin, uri, copy.Length) || InUse(copy.FullName))
            return Outcome.Left;

        await bin.RecycleAsync([uri], ct).ConfigureAwait(false);
        if (File.Exists(copy.FullName))
            return Outcome.Left;
        RemoveOurs(folder);
        return Outcome.Recycled;
    }

    /// <summary>
    /// A folder of nothing but <c>.part</c> files (or nothing at all) that has not changed in
    /// <see cref="KeptFor"/>: a download that stopped. Anything else in it, and it is left.
    /// </summary>
    private static async Task<int> TidyAbandonedAsync(string folder, IRecycleBin bin, DateTimeOffset now, CancellationToken ct)
    {
        var files = new DirectoryInfo(folder).GetFiles();
        if (files.Any(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || now - f.LastWriteTimeUtc < KeptFor)
            || (files.Length == 0 && now - Directory.GetCreationTimeUtc(folder) < KeptFor))
            return 0;

        var parts = files.Select(f => FsUri.Local(f.FullName)).ToList();
        if (parts.Count > 0)
        {
            if (parts.Any(p => !bin.CanRecycle(p)) || files.Any(f => !FitsInBin(bin, FsUri.Local(f.FullName), f.Length) || InUse(f.FullName)))
                return 0;
            await bin.RecycleAsync(parts, ct).ConfigureAwait(false);
        }

        RemoveOurs(folder);
        return parts.Count(p => !File.Exists(p.ToLocalPath()));
    }

    /// <summary>
    /// Whether the bin takes a file this size; Windows destroys one it won't, after a box of its
    /// own. A bin nobody has set up has no capacity written down, and Windows' default for it, 5%
    /// of the drive, is assumed (review, 2026-11-14: an unknown capacity let any size through).
    /// </summary>
    private static bool FitsInBin(IRecycleBin bin, FsUri file, long size)
    {
        if (bin.CapacityOf(file) is { } most)
            return size <= most;
        try
        {
            return size <= new DriveInfo(Path.GetPathRoot(file.ToLocalPath())!).TotalSize / 20;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether some program has the file open, which the bin would ask about in a box.</summary>
    private static bool InUse(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>The record and the told mark, then the folder if nothing else is in it.</summary>
    private static void RemoveOurs(string folder)
    {
        foreach (var ours in new[] { RecordName, ToldName })
        {
            var path = Path.Combine(folder, ours);
            if (!File.Exists(path))
                continue;
            File.SetAttributes(path, FileAttributes.Normal);
#pragma warning disable RS0030 // §1.6: the app's own bookkeeping beside a copy, a few lines it wrote itself; never the copy
            File.Delete(path);
#pragma warning restore RS0030
        }

        if (Directory.EnumerateFileSystemEntries(folder).Any())
            return;
#pragma warning disable RS0030 // §1.6: not recursive, so it removes the folder only when it is empty
        Directory.Delete(folder, recursive: false);
#pragma warning restore RS0030
    }

    /// <summary>A folder under <see cref="Root"/> that did not exist before.</summary>
    private string NewFolder()
    {
        while (true)
        {
            var folder = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
            if (Directory.Exists(folder))
                continue;
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    /// <summary>The file an archive entry is in, through any number of archives; else the file itself.</summary>
    private static FsUri Outermost(FsUri file)
    {
        while (file is { IsArchiveEntry: true, Inner: { } container })
            file = container;
        return file;
    }

    /// <summary>
    /// What a copy was when it was made: its name, size and write time as they landed (read back,
    /// so a disk that rounds times agrees with itself), when it was opened, and where its original
    /// is, which is what someone told the copy was changed would act on.
    /// </summary>
    private sealed record Record(string Name, long Size, long Written, DateTimeOffset Opened, string? From)
    {
        public static void Write(string folder, string name, string copy, string from)
        {
            var path = Path.Combine(folder, RecordName);
            try
            {
                var info = new FileInfo(copy);
                using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write)))
                {
                    writer.WriteLine(name);
                    writer.WriteLine(info.Length.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine(DateTimeOffset.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture));
                    // A server's name may hold a line break; the record is one line per field.
                    writer.WriteLine(string.Concat(from.Select(c => char.IsControl(c) ? '?' : c)));
                }
                File.SetAttributes(path, FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without a record the copy is never tidied, which loses nothing; the open goes on.
            }
        }

        /// <summary>The folder's record, or null when there is none or it doesn't read as one.</summary>
        public static Record? Read(string folder)
        {
            var path = Path.Combine(folder, RecordName);
            if (!File.Exists(path))
                return null;
            var lines = File.ReadAllLines(path);
            if (lines.Length < 4
                || lines[0].Length == 0 || lines[0] != ForeignName.ToFileName(lines[0]) || IsOurs(lines[0])
                || !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || !long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var written)
                || !long.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out var opened)
                || opened > DateTimeOffset.MaxValue.UtcTicks)
                return null;
            return new Record(lines[0], size, written, new DateTimeOffset(opened, TimeSpan.Zero), lines.Length > 4 && lines[4].Length > 0 ? lines[4] : null);
        }
    }
}
