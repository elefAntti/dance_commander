using System.Runtime.CompilerServices;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Watching;

namespace DanceCommander.Providers.Local;

public sealed class LocalFileSystemProvider : IFileSystemProvider, IFileIdentity, IChangeStamps, ICopiesWithin, IMovesLinks
{
    private static readonly EnumerationOptions Options = new()
    {
        // Show everything, including hidden and system entries; a file manager that hides things
        // is the problem we are solving. Filtering is the user's call, via settings and rules.
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public string Scheme => FsUri.FileScheme;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Delete |
        ProviderCapabilities.Rename | ProviderCapabilities.CreateDirectory |
        ProviderCapabilities.RandomAccessRead | ProviderCapabilities.ExecuteHost |
        ProviderCapabilities.Watch;

    public ColumnSet DefaultColumns => ColumnSet.Standard;

    public async IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield(); // never enumerate on the caller's thread

        var root = new DirectoryInfo(directory.ToLocalPath());
        foreach (var info in root.EnumerateFileSystemInfos("*", Options))
        {
            ct.ThrowIfCancellationRequested();
            yield return ToEntry(directory, info);
        }
    }

    public Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct)
    {
        var local = path.ToLocalPath();
        FileSystemInfo info = Directory.Exists(local) ? new DirectoryInfo(local) : new FileInfo(local);
        return Task.FromResult(info.Exists ? ToEntry(path.Parent ?? path, info) : null);
    }

    public Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(
            file.ToLocalPath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));

    public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) =>
        Task.FromResult<Stream>(LocalPartStream.Open(file.ToLocalPath(), length));

    /// <summary>Any two local files: Windows copies between them keeping what a stream can't (<see cref="LocalCopy"/>).</summary>
    public bool CanCopyWithin(FsUri source, FsUri target) => source.IsLocal && target.IsLocal;

    public Task<long> CopyWithinAsync(
        FsUri source, FsUri target, bool replace, bool durable, Action<long> moved, CancellationToken ct, Func<bool>? mayReplace = null) =>
        LocalCopy.CopyAsync(source.ToLocalPath(), target.ToLocalPath(), replace, durable, moved, ct, mayReplace);

    public Task<bool> IsSameFileAsync(FsUri a, FsUri b, CancellationToken ct) =>
        Task.FromResult(FileIdentity.Of(a.ToLocalPath()) is { } first && FileIdentity.Of(b.ToLocalPath()) == first);

    public Task<bool> IsSurelySameFileAsync(FsUri a, FsUri b, CancellationToken ct) =>
        Task.FromResult(FileIdentity.UniqueOf(a.ToLocalPath()) is { } first && FileIdentity.UniqueOf(b.ToLocalPath()) == first);

    public long? ChangeStamp(FsUri file) => FileIdentity.ChangeStampOf(file.ToLocalPath());

    public DateTimeOffset? ChangedAt(FsUri file) => AsTime(FileIdentity.ChangeTimeOf(file.ToLocalPath()));

    public DateTimeOffset? ClockedAt(FsUri file) => AsTime(FileIdentity.ChangeTimeOf(file.ToLocalPath(), alone: true));

    /// <summary>A FILETIME, or null past what a date holds: any program can set a file's times to year 30000.</summary>
    private static DateTimeOffset? AsTime(long? fileTime)
    {
        try
        {
            return fileTime is { } time ? DateTimeOffset.FromFileTime(time) : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory.ToLocalPath());
        return Task.CompletedTask;
    }

    public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct)
    {
        var local = path.ToLocalPath();
        // §1.6: the provider's delete itself. Every caller of IFileSystemProvider.DeleteAsync is
        // banned and carries its own reason; this is where those reasons are carried out.
#pragma warning disable RS0030 // §1.6: the provider's delete itself (above).
        if (Directory.Exists(local))
        {
            Directory.Delete(local, recursive);
        }
        else
        {
            // Read-only is no bar to a delete its caller has a reason for (a move's source once its
            // copy is checked, the user's confirmed Delete permanently): Windows refuses one, and a
            // move across drives then stopped with every read-only file in both places.
            var readOnly = File.Exists(local) && File.GetAttributes(local).HasFlag(FileAttributes.ReadOnly);
            if (readOnly)
                File.SetAttributes(local, File.GetAttributes(local) & ~FileAttributes.ReadOnly);
            try
            {
                File.Delete(local);
            }
            catch when (readOnly && File.Exists(local))
            {
                // Still there (in use, refused): it keeps the protection it had.
                File.SetAttributes(local, File.GetAttributes(local) | FileAttributes.ReadOnly);
                throw;
            }
        }
#pragma warning restore RS0030
        return Task.CompletedTask;
    }

    public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct)
    {
        var source = from.ToLocalPath();
        var target = to.ToLocalPath();
        if (Directory.Exists(source))
            Directory.Move(source, target);
        else
            File.Move(source, target);
        return Task.CompletedTask;
    }

    public Task MoveLinkAsync(FsUri from, FsUri to, CancellationToken ct)
    {
        DirectoryLinks.Move(from.ToLocalPath(), to.ToLocalPath());
        return Task.CompletedTask;
    }

    public IDirectoryWatcher? Watch(FsUri directory) =>
        new LocalDirectoryWatcher(directory, name => StatChild(directory, name));

    /// <summary>Re-reads one entry by name, for the watcher to fill in a changed row.</summary>
    private static FsEntry? StatChild(FsUri directory, string name)
    {
        var uri = directory.Child(name);
        var local = uri.ToLocalPath();
        try
        {
            FileSystemInfo info = Directory.Exists(local) ? new DirectoryInfo(local) : new FileInfo(local);
            return info.Exists ? ToEntry(directory, info) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FsEntry ToEntry(FsUri directory, FileSystemInfo info)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;

        long size = 0;
        DateTimeOffset? modified = null;
        try
        {
            if (!isDirectory && info is FileInfo file)
                size = file.Length;
            modified = info.LastWriteTime;
        }
        catch (IOException)
        {
            // A file being written or locked still belongs in the listing, just without metadata.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new FsEntry(
            directory.Child(info.Name),
            info.Name,
            isDirectory,
            size,
            modified,
            ToAttributes(info.Attributes),
            LinkTarget(info));
    }

    /// <summary>
    /// Only a reparse point can have a link target, and the attributes the enumeration already
    /// returned say which entries are. Asking every entry opened every file — a CreateFile, a
    /// DeviceIoControl and a CloseHandle each — which was a third of the time it took to list
    /// System32.
    /// </summary>
    /// <remarks>
    /// Null means "not a link", and the move, the copy and the undo walk into anything that says
    /// so. A junction whose target couldn't be read, or a link .NET can't name (WSL's), came back
    /// null as well, and was walked through as a folder (AUDIT.md §2 kind 48). Those are "" now:
    /// a link of unknown target. The reparse tag tells them from a reparse point that is not a
    /// link at all (deduplication, a cloud file): only a tag marked as naming something else is one.
    /// </remarks>
    internal static string? LinkTarget(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            return null;

        // Asked through \\?\ when the name ends in a dot or a space: .NET opens a listed child by its
        // plain path, and notes. answered with the link of notes beside it.
        var exact = FsUri.HasNameWin32Trims(info.FullName)
            ? info is DirectoryInfo ? new DirectoryInfo(FileIdentity.Extended(info.FullName)) : (FileSystemInfo)new FileInfo(FileIdentity.Extended(info.FullName))
            : info;
        try
        {
            if (exact.LinkTarget is { } target)
                return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable, and the tag still says whether it is a link at all: a cloud or HSM folder
            // whose read failed is a folder (review, 2026-10-28).
        }

        return FileIdentity.ReparseTagOf(info.FullName) is { } tag && !IsNameSurrogate(tag) ? null : string.Empty;
    }

    private static bool IsNameSurrogate(uint tag) => (tag & 0x20000000) != 0;

    /// <summary>
    /// FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS and FILE_ATTRIBUTE_PINNED, which .NET has no names for.
    /// </summary>
    /// <remarks>
    /// These are what a cloud file looks like from here. Windows disguises cloud placeholders from
    /// ordinary processes, this one included (placeholder compatibility mode 1): no reparse point
    /// and no Offline, which is also why OneDrive files never came out as links. These two flags
    /// are left visible precisely so a program can tell what reading the file would cost.
    /// </remarks>
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
    private const FileAttributes Pinned = (FileAttributes)0x80000;

    internal static FsEntryAttributes ToAttributes(FileAttributes attributes)
    {
        var result = FsEntryAttributes.None;
        if (attributes.HasFlag(FileAttributes.Hidden)) result |= FsEntryAttributes.Hidden;
        if (attributes.HasFlag(FileAttributes.System)) result |= FsEntryAttributes.System;
        if (attributes.HasFlag(FileAttributes.ReadOnly)) result |= FsEntryAttributes.ReadOnly;
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) result |= FsEntryAttributes.Symlink;
        if (attributes.HasFlag(FileAttributes.Compressed)) result |= FsEntryAttributes.Compressed;
        if (attributes.HasFlag(FileAttributes.Encrypted)) result |= FsEntryAttributes.Encrypted;
        // Offline too: older sync engines and HSM storage mark online-only data that way, and
        // reading it costs the same.
        if ((attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0) result |= FsEntryAttributes.CloudOnly;
        if (attributes.HasFlag(Pinned)) result |= FsEntryAttributes.AlwaysKept;
        return result;
    }
}
