using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Watching;

namespace DanceCommander.Core.Listing;

/// <summary>
/// Whatever fills a pane with rows. A directory on a provider is only the first implementation:
/// search results, a flattened tree, a saved collection and the transfer queue are all listing
/// sources behind their own URI scheme (§1.2/3), and the pane cannot tell them apart.
/// </summary>
public interface IListingSource
{
    FsUri Uri { get; }

    /// <summary>Short label for the pane header and tab.</summary>
    string Title { get; }

    ColumnSet Columns { get; }

    /// <summary>True when rows can be opened as containers (a search result list still can).</summary>
    bool SupportsNavigation { get; }

    IAsyncEnumerable<FsEntry> ListAsync(CancellationToken ct);

    IDirectoryWatcher? Watch();

    /// <summary>
    /// What this listing left out on the filter's behalf, in words, or null when it left out
    /// nothing. The pane reports it alongside its own hidden count.
    /// </summary>
    /// <remarks>
    /// A listing that declines to walk part of the tree has to say so, and cannot say it as a
    /// count of rows: the filter can report "40 hidden" because it saw all forty, while a walk
    /// that never opened the folder knows only that it did not go in. That is the thing the user
    /// needs told, and it is the same thing Ctrl+H undoes, which is why the pane says both under
    /// one mention of the key rather than naming it twice.
    /// </remarks>
    string? Omission => null;

    /// <summary>
    /// True when the rows depend on the filter, so changing it means listing again rather than
    /// re-filtering what is already loaded.
    /// </summary>
    /// <remarks>
    /// A directory listing reads everything and hides some of it, so Ctrl+H is instant and local.
    /// A pruned walk never read the hidden part at all; re-filtering rows that were never
    /// collected would show nothing new and look like the key had stopped working.
    /// </remarks>
    bool RereadWhenFilterChanges => false;

    /// <summary>
    /// What answered instead of the listing's own way of reading, when something did: "Everything"
    /// for a search it answered rather than a walk. Known once the rows are coming; for the log.
    /// </summary>
    string? AnsweredBy => null;
}

public interface IListingSourceFactory
{
    IListingSource Create(FsUri uri, ListingFilter filter);

    /// <param name="walk">
    /// A search without the index: asked for when the index found nothing, which may be the index
    /// not having it yet rather than the disk not having it.
    /// </param>
    IListingSource Create(FsUri uri, ListingFilter filter, bool walk) => Create(uri, filter);
}

/// <summary>A plain directory listing from a provider.</summary>
public sealed class DirectoryListingSource(IFileSystemProvider provider, FsUri uri) : IListingSource
{
    public FsUri Uri { get; } = uri;

    public string Title => Uri.Name;

    public ColumnSet Columns => provider is ILocationColumns pages ? pages.ColumnsFor(Uri) : provider.DefaultColumns;

    public bool SupportsNavigation => true;

    public IAsyncEnumerable<FsEntry> ListAsync(CancellationToken ct) => provider.ListAsync(Uri, ct);

    public IDirectoryWatcher? Watch() => provider.Watch(Uri);
}

/// <param name="index">The disks' search index, when there is one to ask (Everything).</param>
public sealed class ListingSourceFactory(
    IProviderRegistry providers,
    ICommandListingCatalog? listings = null,
    ILocalSearchIndex? index = null) : IListingSourceFactory
{
    /// <summary>
    /// A directory on a provider is only the first kind of listing (§1.2/3). A view computes its
    /// rows from another location instead, and the pane cannot tell the difference.
    /// </summary>
    public IListingSource Create(FsUri uri, ListingFilter filter) => Create(uri, filter, walk: false);

    public IListingSource Create(FsUri uri, ListingFilter filter, bool walk) => Create(uri, filter, walk ? null : index);

    private IListingSource Create(FsUri uri, ListingFilter filter, ILocalSearchIndex? index) => uri.Scheme switch
    {
        "flat" => new FlatListingSource(providers, uri, filter),
        "search" => new SearchListingSource(providers, uri, filter, index),
        FsUri.EverywhereScheme => new EverywhereListingSource(providers, uri, filter, index: index),
        "from" => FromCommand(uri),
        _ => new DirectoryListingSource(providers.Resolve(uri), uri),
    };

    /// <summary>
    /// A listing named by a <c>from:</c> address. An unknown name is a location that cannot be
    /// built, which is what happens to a bookmark kept after its entry was renamed or removed.
    /// </summary>
    private IListingSource FromCommand(FsUri uri)
    {
        if (listings?.Find(uri.Parameter) is not { } listing)
            throw new InvalidOperationException($"There is no listing called '{uri.Parameter}'.");

        // A script location is a provider (§6.16); its rows are its own, served level by level.
        if (listing.IsScript)
            return new ScriptListingSource(providers.Resolve(uri), uri, listing);

        return new CommandListingSource(providers, uri, listing);
    }
}
