using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DanceCommander.Core.Collections;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Navigation;
using DanceCommander.Core.Watching;

namespace DanceCommander.Providers.Local;

/// <summary>
/// The application's own places (ARCHITECTURE.md §6.19): <c>about:computer</c>, the drives and the
/// known folders — where Up from a drive root goes — the collections, the bookmarks and the history.
/// </summary>
/// <remarks>
/// The rows are the things themselves: a drive, a folder, a collection's files. So Enter opens
/// them and a drop onto one lands in it. Which commands apply is decided by the page
/// (<see cref="AboutPages"/>), not by this provider, which holds nothing of its own.
/// </remarks>
/// <param name="collections">Absent in tests of the computer page alone.</param>
/// <param name="bookmarks">Likewise.</param>
/// <param name="history">Likewise.</param>
/// <param name="shelf">Likewise.</param>
/// <param name="providers">
/// For reading a collection's members wherever they live. A factory, because the registry is made
/// of the providers, this one among them.
/// </param>
public sealed class AboutProvider(
    ICollectionService? collections = null,
    Func<IProviderRegistry>? providers = null,
    IBookmarkService? bookmarks = null,
    IHistoryService? history = null,
    IShelf? shelf = null) : IFileSystemProvider, ILocationColumns
{
    /// <summary>
    /// How long one drive may take to say how big it is. A disconnected network drive can take
    /// tens of seconds; the page does not wait for it, it says so.
    /// </summary>
    private static readonly TimeSpan DrivePatience = TimeSpan.FromSeconds(2);

    /// <summary>How long one member of a collection may take to answer: an SFTP host may be down.</summary>
    private static readonly TimeSpan MemberPatience = TimeSpan.FromSeconds(5);

    public string Scheme => FsUri.AboutScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.None;

    public ColumnSet DefaultColumns => ComputerColumns;

    /// <summary>The computer page's columns: what a drive or a known folder is, and how full.</summary>
    public static ColumnSet ComputerColumns { get; } = new(
        [
            new ColumnDefinition(ColumnIds.Name, "Name", ColumnKind.Name, 150),
            new ColumnDefinition(DescriptionColumn, "Description", ColumnKind.Text, 260, Flexible: true),
            new ColumnDefinition(TypeColumn, "Type", ColumnKind.Text, 130),
            new ColumnDefinition(FreeColumn, "Free", ColumnKind.Text, 90, ColumnAlignment.Right),
            new ColumnDefinition(ColumnIds.Size, "Size", ColumnKind.Size, 90, ColumnAlignment.Right),
        ],
        // Drives, then folders, then pages: their types read "Drive…", "Folder", "Page".
        DefaultSortColumnId: TypeColumn);

    /// <summary>The list of collections: what each is called and how much is in it.</summary>
    public static ColumnSet CollectionListColumns { get; } = new(
        [
            new ColumnDefinition(ColumnIds.Name, "Name", ColumnKind.Name, 260, Flexible: true),
            new ColumnDefinition(CountColumn, "Items", ColumnKind.Text, 90, ColumnAlignment.Right),
        ]);

    /// <summary>A collection's members: files from anywhere, so where each lives is a column.</summary>
    public static ColumnSet CollectionColumns { get; } = new(
        [
            new ColumnDefinition(ColumnIds.Name, "Name", ColumnKind.Name, 190),
            new ColumnDefinition(ColumnIds.RelativePath, "Where", ColumnKind.Path, 260, Flexible: true),
            new ColumnDefinition(ColumnIds.Note, "Note", ColumnKind.Text, 100),
            new ColumnDefinition(ColumnIds.Size, "Size", ColumnKind.Size, 78, ColumnAlignment.Right),
            new ColumnDefinition(ColumnIds.Modified, "Modified", ColumnKind.Date, 148),
        ]);

    /// <summary>Bookmarks: the label, and the place it goes to.</summary>
    public static ColumnSet BookmarkColumns { get; } = new(
        [
            new ColumnDefinition(ColumnIds.Name, "Name", ColumnKind.Name, 200),
            new ColumnDefinition(DescriptionColumn, "Location", ColumnKind.Path, 320, Flexible: true),
        ]);

    /// <summary>The history: where, and when last — newest first.</summary>
    public static ColumnSet HistoryColumns { get; } = new(
        [
            new ColumnDefinition(ColumnIds.Name, "Name", ColumnKind.Name, 200),
            new ColumnDefinition(DescriptionColumn, "Location", ColumnKind.Path, 320, Flexible: true),
            new ColumnDefinition(ColumnIds.Modified, "Visited", ColumnKind.Date, 148),
        ],
        DefaultSortColumnId: ColumnIds.Modified,
        DefaultDescending: true);

    /// <summary>About the application: what each row is, and how big the files are.</summary>
    public static ColumnSet AppColumns { get; } = new(
        [
            new ColumnDefinition(ColumnIds.Name, "Name", ColumnKind.Name, 200),
            new ColumnDefinition(DescriptionColumn, "Description", ColumnKind.Text, 380, Flexible: true),
            new ColumnDefinition(ColumnIds.Size, "Size", ColumnKind.Size, 90, ColumnAlignment.Right),
        ]);

    public ColumnSet ColumnsFor(FsUri location) =>
        location == AboutPages.App ? AppColumns
        : location == AboutPages.History ? HistoryColumns
        : AboutPages.IsBookmarks(location) ? BookmarkColumns
        : location == AboutPages.Collections ? CollectionListColumns
        : AboutPages.HoldsFiles(location) ? CollectionColumns
        : ComputerColumns;

    public const string CountColumn = "count";
    public const string DescriptionColumn = "description";
    public const string TypeColumn = "type";
    public const string FreeColumn = "free";

    public IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, CancellationToken ct)
    {
        if (directory == FsUri.Computer)
            return ListComputerAsync(ct);
        if (directory == AboutPages.App)
            return ListApp();
        if (directory == AboutPages.Collections && collections is not null)
            return ListCollections();
        if (AboutPages.IsBookmarks(directory) && bookmarks is not null)
            return ListBookmarks(AboutPages.BookmarkGroupName(directory));
        if (directory == AboutPages.History && history is not null)
            return ListHistory();
        if (directory == AboutPages.Shelf && shelf is not null)
            return ListFilesAsync(shelf.Items, ct);
        if (AboutPages.CollectionName(directory) is { } name)
        {
            return collections?.Find(name) is not null
                ? ListMembersAsync(name, ct)
                : throw new DirectoryNotFoundException($"There is no collection called {name}.");
        }

        throw new DirectoryNotFoundException($"There is no page called {directory.Display}.");
    }

    private async IAsyncEnumerable<FsEntry> ListComputerAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // All drives at once, each with its own deadline: one unreachable share must not hold up
        // the others, nor the page.
        var drives = await Task.WhenAll(DriveInfo.GetDrives().Select(d => DescribeAsync(d, ct))).ConfigureAwait(false);
        foreach (var drive in drives)
            yield return drive;

        foreach (var (name, path) in KnownFolders())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(path))
                continue;

            yield return new FsEntry(FsUri.Local(path), name, IsDirectory: true, 0, null)
            {
                Extra = new Dictionary<string, string>
                {
                    [DescriptionColumn] = path,
                    [TypeColumn] = "Folder",
                },
            };
        }

        if (shelf is not null)
        {
            var count = shelf.Items.Count;
            yield return new FsEntry(AboutPages.Shelf, "Shelf", IsDirectory: true, 0, null)
            {
                Extra = new Dictionary<string, string>
                {
                    [DescriptionColumn] = count switch
                    {
                        0 => "Files marked across folders, this session (Ctrl+B): none yet",
                        1 => "Files marked across folders, this session (Ctrl+B): 1",
                        _ => $"Files marked across folders, this session (Ctrl+B): {count}",
                    },
                    [TypeColumn] = "Page",
                },
            };
        }

        if (history is not null)
        {
            yield return new FsEntry(AboutPages.History, "History", IsDirectory: true, 0, null)
            {
                Extra = new Dictionary<string, string>
                {
                    [DescriptionColumn] = "Where you have been, newest first",
                    [TypeColumn] = "Page",
                },
            };
        }

        if (bookmarks is not null)
        {
            var count = bookmarks.Bookmarks.Count;
            yield return new FsEntry(AboutPages.Bookmarks, "Bookmarks", IsDirectory: true, 0, null)
            {
                Extra = new Dictionary<string, string>
                {
                    [DescriptionColumn] = count == 1 ? "Places you go to: 1 bookmark" : $"Places you go to: {count} bookmarks",
                    [TypeColumn] = "Page",
                },
            };
        }

        yield return new FsEntry(AboutPages.App, "About Dance Commander", IsDirectory: true, 0, null)
        {
            Extra = new Dictionary<string, string>
            {
                [DescriptionColumn] = $"Version {Version().Number}, the licences, where the settings and logs are",
                [TypeColumn] = "Page",
            },
        };

        if (collections is not null)
        {
            var count = collections.Collections.Count;
            yield return new FsEntry(AboutPages.Collections, "Collections", IsDirectory: true, 0, null)
            {
                Extra = new Dictionary<string, string>
                {
                    [DescriptionColumn] = count switch
                    {
                        0 => "Files gathered to act on together: none yet",
                        1 => "Files gathered to act on together: 1 collection",
                        _ => $"Files gathered to act on together: {count} collections",
                    },
                    [TypeColumn] = "Page",
                },
            };
        }
    }

    /// <summary>
    /// About the application, as the things themselves: the program's folder, its licence and the
    /// notices a published build must carry (README), and the folders a user goes looking for when
    /// something needs fixing by hand. Enter opens each; the version is said on the program's row.
    /// </summary>
#pragma warning disable CS1998 // a handful of rows made on the spot
    private static async IAsyncEnumerable<FsEntry> ListApp()
#pragma warning restore CS1998
    {
        var program = AppContext.BaseDirectory;
        var (number, commit) = Version();
        var built = commit is null ? $"Version {number}" : $"Version {number}, commit {commit}";
        yield return Place(program, "Dance Commander", $"{built}, on .NET {Environment.Version}. The program's folder");

        foreach (var (file, description) in new[]
        {
            ("README.md", "How to use it: the keys, the addresses, servers, settings and tools"),
            ("LICENSE.txt", "Dance Commander's licence: MIT"),
            ("THIRD-PARTY-NOTICES.txt", "The licences of the software it is built from"),
        })
        {
            var path = Path.Combine(program, file);
            var info = new FileInfo(path);
            yield return new FsEntry(FsUri.Local(path), file, IsDirectory: false, info.Exists ? info.Length : 0,
                info.Exists ? info.LastWriteTimeUtc : null)
            {
                Extra = new Dictionary<string, string>
                {
                    [DescriptionColumn] = info.Exists ? description : $"{description} (missing from this build)",
                },
            };
        }

        // As App makes them: settings roam with the user, the logs stay on this machine.
        yield return Place(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DanceCommander"),
            "Settings", "settings.json, tools.json, the keys, bookmarks and collections");
        yield return Place(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanceCommander", "logs"),
            "Logs", "Daily log files, the last seven kept");

        static FsEntry Place(string path, string name, string description) =>
            new(FsUri.Local(path), name, IsDirectory: true, 0, null)
            {
                Extra = new Dictionary<string, string> { [DescriptionColumn] = description },
            };
    }

    /// <summary>
    /// The version the build was given, and the commit it was built from, which the SDK appends to
    /// the informational version from git ("1.0.0+9223a0b…").
    /// </summary>
    private static (string Number, string? Commit) Version()
    {
        var informational = (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(AboutProvider).Assembly)
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0
            ? (informational, null)
            : (informational[..plus], informational[(plus + 1)..] is { Length: > 7 } sha ? sha[..7] : informational[(plus + 1)..]);
    }

#pragma warning disable CS1998 // an async iterator over a list in memory
    private async IAsyncEnumerable<FsEntry> ListCollections()
#pragma warning restore CS1998
    {
        foreach (var collection in collections!.Collections)
        {
            yield return new FsEntry(AboutPages.Collection(collection.Name), collection.Name, IsDirectory: true, 0, null)
            {
                Extra = new Dictionary<string, string>
                {
                    [CountColumn] = collection.Items.Count.ToString("N0", CultureInfo.CurrentCulture),
                },
            };
        }
    }

    /// <summary>
    /// Bookmarks as records that point somewhere: the row is labelled with the bookmark's name and
    /// its address is the place, so Enter goes there; the link target makes the row say so with
    /// the link glyph. The top level has the ungrouped ones and a folder for each group.
    /// </summary>
#pragma warning disable CS1998 // an async iterator over a list in memory
    private async IAsyncEnumerable<FsEntry> ListBookmarks(string? group)
#pragma warning restore CS1998
    {
        var all = bookmarks!.Bookmarks;
        if (group is null)
        {
            foreach (var name in all.Select(b => b.Group).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var count = all.Count(b => string.Equals(b.Group, name, StringComparison.OrdinalIgnoreCase));
                yield return new FsEntry(AboutPages.BookmarkGroup(name), name, IsDirectory: true, 0, null)
                {
                    Extra = new Dictionary<string, string>
                    {
                        [DescriptionColumn] = count == 1 ? "1 bookmark" : $"{count} bookmarks",
                    },
                };
            }
        }

        foreach (var bookmark in all.Where(b => string.Equals(b.Group, group, StringComparison.OrdinalIgnoreCase)))
        {
            // Hand-edited entries that do not parse are left out here as in the popup.
            if (!FsUri.TryParse(bookmark.Uri, out var uri, out _))
                continue;

            yield return new FsEntry(uri, bookmark.Name, IsDirectory: true, 0, null, LinkTarget: uri.Display)
            {
                Extra = new Dictionary<string, string> { [DescriptionColumn] = uri.Display },
            };
        }
    }

    /// <summary>
    /// Where you have been: a row per place, its address the place itself, with when it was last
    /// visited as the date the listing sorts by. Places from before times were kept have none, and
    /// sort after the rest.
    /// </summary>
#pragma warning disable CS1998 // an async iterator over a list in memory
    private async IAsyncEnumerable<FsEntry> ListHistory()
#pragma warning restore CS1998
    {
        foreach (var visit in history!.Visits)
        {
            yield return new FsEntry(visit.Uri, visit.Uri.Name, IsDirectory: true, 0, visit.At)
            {
                Extra = new Dictionary<string, string> { [DescriptionColumn] = visit.Uri.Display },
            };
        }
    }

    /// <summary>
    /// The members as the files themselves, read where they live — all at once, each with its own
    /// deadline. One that is gone is still listed, saying so, rather than silently dropped: it was
    /// put there on purpose, and the way to take it out is Del.
    /// </summary>
    private IAsyncEnumerable<FsEntry> ListMembersAsync(string name, CancellationToken ct) =>
        ListFilesAsync(collections!.Members(name), ct);

    /// <summary>Files from anywhere, as themselves: a collection's members, or the shelf.</summary>
    private async IAsyncEnumerable<FsEntry> ListFilesAsync(IReadOnlyList<FsUri> items, [EnumeratorCancellation] CancellationToken ct)
    {
        var registry = providers?.Invoke();
        var rows = await Task.WhenAll(items.Select(uri => DescribeAsync(uri, registry, ct))).ConfigureAwait(false);
        foreach (var row in rows)
            yield return row;
    }

    private static async Task<FsEntry> DescribeAsync(FsUri uri, IProviderRegistry? registry, CancellationToken ct)
    {
        string note;
        try
        {
            if (registry is not null && registry.TryResolve(uri, out var provider)
                && await provider.StatAsync(uri, ct).WaitAsync(MemberPatience, ct).ConfigureAwait(false) is { } found)
            {
                var extra = found.Extra is null ? new Dictionary<string, string>() : new Dictionary<string, string>(found.Extra);
                extra[ColumnIds.RelativePath] = (uri.Parent ?? uri).Display;
                return found with { Uri = uri, Extra = extra };
            }
            note = "not found";
        }
        catch (TimeoutException)
        {
            note = "not responding";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            note = "unavailable";
        }

        return new FsEntry(uri, uri.Name, IsDirectory: false, 0, null)
        {
            Extra = new Dictionary<string, string>
            {
                [ColumnIds.RelativePath] = (uri.Parent ?? uri).Display,
                [ColumnIds.Note] = note,
            },
        };
    }

    private static async Task<FsEntry> DescribeAsync(DriveInfo drive, CancellationToken ct)
    {
        var root = drive.Name; // "C:\"
        var letter = root.TrimEnd('\\');
        var type = drive.DriveType switch
        {
            DriveType.Removable => "Drive (removable)",
            DriveType.Network => "Drive (network)",
            DriveType.CDRom => "Drive (optical)",
            DriveType.Ram => "Drive (RAM)",
            _ => "Drive",
        };

        var extra = new Dictionary<string, string> { [TypeColumn] = type };
        long total = 0;

        try
        {
            var (label, size, free) = await Task.Run(() =>
                drive.IsReady
                    ? (drive.VolumeLabel, drive.TotalSize, drive.AvailableFreeSpace)
                    : ((string?)null, 0L, -1L), ct).WaitAsync(DrivePatience, ct).ConfigureAwait(false);

            if (free < 0)
            {
                extra[DescriptionColumn] = "not ready";
            }
            else
            {
                extra[DescriptionColumn] = string.IsNullOrWhiteSpace(label) ? DefaultLabel(drive.DriveType) : label;
                extra[FreeColumn] = Bytes(free);
                total = size;
            }
        }
        catch (TimeoutException)
        {
            extra[DescriptionColumn] = "not responding";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            extra[DescriptionColumn] = "unavailable";
        }

        return new FsEntry(FsUri.Local(root), letter, IsDirectory: true, total, null) { Extra = extra };
    }

    private static string DefaultLabel(DriveType type) => type switch
    {
        DriveType.Removable => "Removable disk",
        DriveType.Network => "Network drive",
        DriveType.CDRom => "Optical drive",
        _ => "Local disk",
    };

    /// <summary>As the size column writes it: one decimal, KB with a capital K.</summary>
    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{value.ToString("N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private static IEnumerable<(string Name, string Path)> KnownFolders()
    {
        yield return ("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        yield return ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        yield return ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (KnownFolder(DownloadsFolderId) is { } downloads)
            yield return ("Downloads", downloads);
        yield return ("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        yield return ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        yield return ("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
    }

    /// <summary>Downloads has no <see cref="Environment.SpecialFolder"/>; the known-folder API has it.</summary>
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var path) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct)
    {
        var exists = path == FsUri.Computer
            || path == AboutPages.App
            || (path == AboutPages.Shelf && shelf is not null)
            || (path == AboutPages.History && history is not null)
            || (path == AboutPages.Bookmarks && bookmarks is not null)
            || (AboutPages.BookmarkGroupName(path) is { } group
                && bookmarks?.Bookmarks.Any(b => string.Equals(b.Group, group, StringComparison.OrdinalIgnoreCase)) == true)
            || (path == AboutPages.Collections && collections is not null)
            || (AboutPages.CollectionName(path) is { } name && collections?.Find(name) is not null);
        return Task.FromResult<FsEntry?>(exists ? new FsEntry(path, path.Name, IsDirectory: true, 0, null) : null);
    }

    public Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct) => throw NotAPlace();

    public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) => throw NotAPlace();

    public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct) => throw NotAPlace();

    public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct) => throw NotAPlace();

    public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct) => throw NotAPlace();

    /// <summary>
    /// The collection pages follow the collections: Del in one pane, an add from the other, a hand
    /// edit of collections.json. Drives come and go rarely enough that Ctrl+R answers for them.
    /// </summary>
    public IDirectoryWatcher? Watch(FsUri directory) =>
        shelf is not null && directory == AboutPages.Shelf
            ? new ChangeWatcher(handler => shelf.Changed += handler, handler => shelf.Changed -= handler)
        : collections is not null && (directory == AboutPages.Collections || AboutPages.CollectionName(directory) is not null)
            ? new ChangeWatcher(handler => collections.Changed += handler, handler => collections.Changed -= handler)
        : bookmarks is not null && AboutPages.IsBookmarks(directory)
            ? new ChangeWatcher(handler => bookmarks.Changed += handler, handler => bookmarks.Changed -= handler)
        : history is not null && directory == AboutPages.History
            ? new ChangeWatcher(handler => history.Changed += handler, handler => history.Changed -= handler)
        : null;

    private static NotSupportedException NotAPlace() =>
        new("The application's own pages list things that live elsewhere; they hold nothing of their own.");

    /// <summary>Any change to what a page shows re-lists it: the lists are short, and a re-list is exact.</summary>
    private sealed class ChangeWatcher(Action<EventHandler> subscribe, Action<EventHandler> unsubscribe) : IDirectoryWatcher
    {
        public event EventHandler<DirectoryChangeBatch>? Changed { add { } remove { } }

        public event EventHandler? ResyncRequired;

        public event EventHandler<FsUri?>? Moved { add { } remove { } }

        public void Start() => subscribe(OnChanged);

        public void Dispose() => unsubscribe(OnChanged);

        private void OnChanged(object? sender, EventArgs e) => ResyncRequired?.Invoke(this, EventArgs.Empty);
    }
}
