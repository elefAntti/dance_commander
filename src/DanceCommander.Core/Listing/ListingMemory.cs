using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Listing;

/// <summary>A folder's rows as a server last gave them, and when.</summary>
public sealed record RememberedListing(IReadOnlyList<FsEntry> Entries, DateTime ListedAt);

/// <summary>
/// The last few dozen listings of remote folders, so Back, Up, or a folder visited a minute ago
/// shows its rows at once while it is listed again (PLAN.md §5, "Remote listings, remembered").
/// </summary>
/// <remarks>
/// Only for providers that claim <see cref="ProviderCapabilities.Remote"/>. A local folder lists in
/// tens of milliseconds and has a watcher; remembering it would only add a way to be wrong. What is
/// recalled is never the truth, only a first draft of it: the pane shows it as unconfirmed and
/// lists the folder again before anything may act on its rows.
///
/// Kept on disk too, when <paramref name="store"/> is given and <paramref name="keep"/> says so
/// (settings.json's remote.keepListings), so a server tab restored at launch has rows at once. A
/// day-old listing is only an older first draft; the same rule covers it.
/// </remarks>
public sealed class ListingMemory : IDisposable
{
    /// <summary>
    /// On disk only the folders people move through, and few enough rows that reading the file is
    /// no noticeable part of a launch: it is read on the way to the first rows (§1.5).
    /// </summary>
    private const int LargestKept = 1_000, MostRowsKept = 5_000;

    private readonly int _capacity;
    private readonly int _largest;
    private readonly IConfigStore<ListingsFile>? _store;
    private readonly Func<bool> _keep;
    private readonly Debouncer _save = new(TimeSpan.FromSeconds(10));

    private readonly Dictionary<FsUri, LinkedListNode<(FsUri Uri, RememberedListing Listing)>> _byUri = [];
    private readonly LinkedList<(FsUri Uri, RememberedListing Listing)> _recency = new(); // most recent first
    private readonly Lock _gate = new();

    public ListingMemory(int capacity = 40, int largest = 20_000, IConfigStore<ListingsFile>? store = null, Func<bool>? keep = null)
    {
        _capacity = capacity;
        _largest = largest;
        _store = store;
        _keep = keep ?? (() => true);

        if (store is null)
            return;
        if (_keep())
            Load(store.Current);
        else if (store.Current.Folders.Count > 0)
            KeepChanged(); // turned off while the application was closed: forget on disk too
    }

    /// <summary>Whether a location is worth remembering: a plain folder on a server.</summary>
    public static bool Covers(FsUri uri, IFileSystemProvider? provider) =>
        !uri.IsView && provider is not null && provider.Capabilities.HasFlag(ProviderCapabilities.Remote);

    public RememberedListing? Recall(FsUri uri)
    {
        lock (_gate)
        {
            if (!_byUri.TryGetValue(uri, out var node))
                return null;

            _recency.Remove(node);
            _recency.AddFirst(node);
            return node.Value.Listing;
        }
    }

    /// <param name="listedAt">
    /// When the server was asked, in UTC. Not when it answered: a listing asked before a change and
    /// answered after it must not look newer than one asked after it.
    /// </param>
    public void Remember(FsUri uri, IReadOnlyList<FsEntry> entries, DateTime listedAt)
    {
        lock (_gate)
            Put(uri, entries, listedAt);
        Changed();
    }

    /// <summary>Drops a listing that turned out wrong: the folder could not be listed again.</summary>
    public void Forget(FsUri uri)
    {
        lock (_gate)
            Remove(uri);
        Changed();
    }

    /// <summary>
    /// The setting changed: on writes what is remembered, off writes an empty file, so turning it
    /// off is also how to make this computer forget the server's names.
    /// </summary>
    public void KeepChanged()
    {
        if (_store is not null)
            _save.Schedule(() => _ = FlushAsync());
    }

    /// <param name="listedAt">When the server was asked, not when it answered: see <see cref="Remember"/>.</param>
    private void Put(FsUri uri, IReadOnlyList<FsEntry> entries, DateTime listedAt)
    {
        // A slow answer to an earlier question arriving after a quicker, later one: it is the older
        // of the two, whatever order they came in. Unless what is held is stamped in the future
        // (the clock set back since): that would hold every new listing off until the clock
        // caught up.
        if (_byUri.TryGetValue(uri, out var had) && had.Value.Listing.ListedAt > listedAt
            && had.Value.Listing.ListedAt <= DateTime.UtcNow)
            return;

        Remove(uri);

        // A folder of tens of thousands of files is a few megabytes held for a moment's gain;
        // it is listed afresh instead, as before.
        if (entries.Count > _largest)
            return;

        _byUri[uri] = _recency.AddFirst((uri, new RememberedListing(entries, listedAt)));
        while (_byUri.Count > _capacity && _recency.Last is { } oldest)
        {
            _recency.RemoveLast();
            _byUri.Remove(oldest.Value.Uri);
        }
    }

    private void Remove(FsUri uri)
    {
        if (_byUri.Remove(uri, out var node))
            _recency.Remove(node);
    }

    private void Changed()
    {
        if (_store is not null && _keep())
            _save.Schedule(() => _ = FlushAsync());
    }

    private void Load(ListingsFile file)
    {
        lock (_gate)
        {
            // Oldest first, so the most recent ends up first as it was.
            foreach (var folder in Enumerable.Reverse(file.Folders))
            {
                if (Restore(folder) is { } listing)
                    Put(listing.Uri, listing.Entries, listing.ListedAt);
            }
        }
    }

    /// <summary>
    /// A saved folder back as rows, or null for one that does not read cleanly. A row's address is
    /// made from its folder and its name, never read from the file: a hand-edited or damaged file
    /// must not give a row another place's address (another user's, by case), which is what Del
    /// would act on. A name that is not one name, or that the address would not keep as it is,
    /// drops the folder.
    /// </summary>
    private static (FsUri Uri, IReadOnlyList<FsEntry> Entries, DateTime ListedAt)? Restore(SavedListing saved)
    {
        if (!FsUri.TryParse(saved.Uri, out var folder, out _) || folder.ToString() != saved.Uri)
            return null;

        // Listed in the future: a clock set back since, or a file from elsewhere. Its age means
        // nothing, so the listing is not kept.
        if (saved.ListedAt > DateTimeOffset.UtcNow)
            return null;

        var entries = new List<FsEntry>(saved.Entries.Count);
        foreach (var row in saved.Entries)
        {
            FsUri uri;
            try
            {
                uri = folder.Child(row.Name);
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (uri.Name != row.Name)
                return null;
            entries.Add(new FsEntry(uri, row.Name, row.IsDirectory, row.Size, row.Modified, row.Attributes,
                row.LinkTarget, row.Extra));
        }

        return (folder, entries, saved.ListedAt.UtcDateTime);
    }

    private Task FlushAsync()
    {
        if (_store is null)
            return Task.CompletedTask;

        var file = new ListingsFile();
        if (_keep())
        {
            lock (_gate)
            {
                var rows = 0;
                foreach (var (uri, listing) in _recency)
                {
                    if (listing.Entries.Count > LargestKept)
                        continue;
                    if ((rows += listing.Entries.Count) > MostRowsKept)
                        break;
                    file.Folders.Add(new SavedListing
                    {
                        Uri = uri.ToString(),
                        ListedAt = new DateTimeOffset(DateTime.SpecifyKind(listing.ListedAt, DateTimeKind.Utc)),
                        Entries = listing.Entries.Select(e => new SavedEntry
                        {
                            Name = e.Name,
                            IsDirectory = e.IsDirectory,
                            Size = e.Size,
                            Modified = e.Modified,
                            Attributes = e.Attributes,
                            LinkTarget = e.LinkTarget,
                            Extra = e.Extra?.ToDictionary(),
                        }).ToList(),
                    });
                }
            }
        }

        return _store.SaveAsync(file);
    }

    /// <summary>
    /// Waits for the last write, as the application shuts down. Safe to block on: the store's
    /// writes never need the calling thread back.
    /// </summary>
    public void Dispose()
    {
        _save.Dispose();
        if (_store is not null)
            Debouncer.FlushAtExit(FlushAsync);
    }
}

/// <summary>listings.json, in %LOCALAPPDATA%: remembered server folders, most recent first.</summary>
public sealed class ListingsFile
{
    public List<SavedListing> Folders { get; set; } = [];
}

public sealed class SavedListing
{
    public string Uri { get; set; } = string.Empty;

    /// <summary>With its offset, so reading it back cannot shift it by the local time zone.</summary>
    public DateTimeOffset ListedAt { get; set; }

    public List<SavedEntry> Entries { get; set; } = [];
}

/// <summary>One row. No address of its own: it is its folder's, plus the name (ListingMemory.Restore).</summary>
public sealed class SavedEntry
{
    public string Name { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Size { get; set; }

    public DateTimeOffset? Modified { get; set; }

    public FsEntryAttributes Attributes { get; set; }

    public string? LinkTarget { get; set; }

    public Dictionary<string, string>? Extra { get; set; }
}
