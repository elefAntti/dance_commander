using System.Threading.Channels;
using System.Runtime.CompilerServices;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Matching;
using DanceCommander.Core.Watching;

namespace DanceCommander.Core.Listing;

/// <summary>
/// Decides which subtrees a recursive listing refuses to enter at all (§1.2/3).
/// </summary>
/// <remarks>
/// Pruning is not filtering, and a walk of a real machine makes the difference obvious: flattening
/// a home folder reads a hundred and thirty-five thousand files under <c>AppData</c> in order to
/// display none of them, and flattening a repository spends most of its time inside <c>.git</c>.
/// Filtering answers "should this row be shown"; pruning answers "is there any point going in
/// here", and only the second one can save you the minute.
///
/// The rule is deliberately the one the pane already has rather than a second list of special
/// names: a folder that would be hidden is a folder not worth walking. That makes Ctrl+H the
/// control for both, covers <c>.git</c>, <c>.venv</c> and <c>AppData</c> without naming any of
/// them, and means the app never refuses to look somewhere the user cannot then ask it to look.
/// </remarks>
/// <param name="skipClutter">
/// For a walk of the whole computer (<see cref="EverywhereListingSource"/>): also declines the
/// folders no person keeps anything in, and links to folders, which lead to places walked in
/// their own right or round in a loop. Ctrl+H lifts the first as it lifts hidden folders.
/// </param>
public sealed class PrunePolicy(ListingFilter filter, bool skipClutter = false)
{
    /// <summary>
    /// Folders that are machinery, not anybody's files, and often the biggest thing on a disk:
    /// package stores, caches, temp folders, Windows' component store. Not hidden, so the hidden
    /// rule does not catch them. Only named here, never guessed at: a folder called "bin" may be
    /// what you are looking for.
    /// </summary>
    private static readonly HashSet<string> Clutter = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components", "__pycache__",
        "Temp", "Tmp", "Cache", "Caches", "CachedData", "Code Cache", "GPUCache", "INetCache",
        "Temporary Internet Files",
        "WinSxS", "Installer", "SoftwareDistribution", "assembly", "Prefetch",
        "$Recycle.Bin", "System Volume Information",
    };

    private int _skipped;
    private int _clutter;
    private int _unreadable;

    /// <summary>How many subtrees were declined, for the pane to report.</summary>
    public int Skipped => Volatile.Read(ref _skipped);

    /// <summary>Asked from several walkers at once (<see cref="TreeWalk"/>), hence the interlocked count.</summary>
    public bool ShouldDescend(FsEntry directory)
    {
        if (skipClutter && (directory.Attributes & FsEntryAttributes.Symlink) != 0)
            return false;

        if (filter.ShowHidden)
            return true;

        if (directory.IsHidden)
        {
            Interlocked.Increment(ref _skipped);
            return false;
        }

        if (skipClutter && Clutter.Contains(directory.Name))
        {
            Interlocked.Increment(ref _clutter);
            return false;
        }

        return true;
    }

    /// <summary>A folder the walk could not read and went on without.</summary>
    public void CouldNotRead() => Interlocked.Increment(ref _unreadable);

    /// <summary>What to tell the user. The pane supplies the key, so this does not.</summary>
    public string? Omission
    {
        get
        {
            int hidden = Skipped, clutter = Volatile.Read(ref _clutter), unreadable = Volatile.Read(ref _unreadable);
            var declined = (hidden, clutter) switch
            {
                (0, 0) => null,
                (1, 0) => "1 hidden folder not searched",
                (_, 0) => $"{hidden:N0} hidden folders not searched",
                (0, _) => $"{Folders(clutter)} of caches and packages not searched",
                _ => $"{hidden:N0} hidden and {Folders(clutter)} of caches and packages not searched",
            };
            var unread = unreadable == 0 ? null : $"{Folders(unreadable)} could not be read";
            return declined is null ? unread : unread is null ? declined : $"{declined}, {unread}";
        }
    }

    private static string Folders(int count) => count == 1 ? "1 folder" : $"{count:N0} folders";
}

/// <summary>
/// Everything below a directory as one flat list (§1.2/3) — Total Commander's branch view.
/// </summary>
/// <remarks>
/// The rows are ordinary files that happen to live in different folders, so everything the pane
/// already does works on them: sort by date to see what changed in a repository in the last hour,
/// select across the whole tree, copy the selection somewhere, press F3. The only thing the pane
/// needs that it did not before is a column saying where each row actually lives.
/// </remarks>
public sealed class FlatListingSource(IProviderRegistry providers, FsUri uri, ListingFilter filter)
    : IListingSource
{
    /// <summary>A tree this deep is a mistake or a loop; stopping beats hanging.</summary>
    private const int MaxDepth = 64;

    private readonly FsUri _root = uri.Inner
        ?? throw new InvalidOperationException("A flat view must say what it is a view of.");

    private readonly PrunePolicy _prune = new(filter);

    public FsUri Uri { get; } = uri;

    public string Title => $"flat: {_root.Name}";

    /// <summary>Where each row lives, since the name alone no longer says.</summary>
    public ColumnSet Columns { get; } = ColumnSet.OfScatteredFiles;

    public bool SupportsNavigation => true;

    public string? Omission => _prune.Omission;

    public bool RereadWhenFilterChanges => true;

    /// <summary>Nothing to watch: a tree has as many directories as it has, and any of them may change.</summary>
    public IDirectoryWatcher? Watch() => null;

    // Folders themselves are not listed: a flat view is about the files in the tree, and the
    // folders are what the extra column already says.
    public IAsyncEnumerable<FsEntry> ListAsync(CancellationToken ct) =>
        TreeWalk.WalkAsync(
            providers, _root, _prune, MaxDepth,
            (entry, prefix) => entry.IsDirectory ? null : WithFolder(entry, prefix),
            ct,
            keepGoing: true);

    /// <param name="relevance">A search's key for the row (<see cref="Relevance"/>), if it ranks them.</param>
    internal static FsEntry WithFolder(FsEntry entry, string prefix, string? relevance = null)
    {
        var extra = entry.Extra is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(entry.Extra);

        extra[ColumnIds.RelativePath] = prefix.Length == 0 ? "." : prefix;
        if (relevance is not null)
            extra[ColumnIds.Relevance + ColumnIds.SortKeySuffix] = relevance;
        return entry with { Extra = extra };
    }
}

/// <summary>
/// A search whose results are a location: browsable, sortable, and acted on like any other pane.
/// </summary>
/// <remarks>
/// Because the result set is a location it can be bookmarked and returned to, which is the point
/// of doing it this way rather than as a dialog with a results list — "every TODO in this repo"
/// becomes a place you can keep.
/// </remarks>
public sealed class SearchListingSource : IListingSource
{
    private const int MaxDepth = 64;

    private readonly IProviderRegistry _providers;
    private readonly FsUri _root;
    private readonly ListingFilter _filter;
    private readonly NameMatcher _matcher;
    private readonly PrunePolicy _prune;
    private readonly DateTimeOffset _now = DateTimeOffset.Now;
    private int _inHiddenFolders;

    public SearchListingSource(IProviderRegistry providers, FsUri uri, ListingFilter filter)
    {
        _providers = providers;
        _root = uri.Inner ?? throw new InvalidOperationException("A search must say where it is searching.");
        _filter = filter;
        _prune = new PrunePolicy(filter);
        Uri = uri;

        // The same rule the quick filter uses, so that what you learn typing in a pane is what
        // you know when you search from one.
        _matcher = NameQuery.Compile(uri.Parameter);
    }

    public FsUri Uri { get; }

    public string Title => $"search: {Uri.Parameter}";

    public ColumnSet Columns { get; } = ColumnSet.OfSearch;

    public bool SupportsNavigation => true;

    public string? Omission => Volatile.Read(ref _inHiddenFolders) switch
    {
        0 => _prune.Omission,
        1 => "1 found in a hidden folder not shown",
        var count => $"{count:N0} found in hidden folders not shown",
    };

    public bool RereadWhenFilterChanges => true;

    public IDirectoryWatcher? Watch() => null;

    // A matching folder is itself a result — people search for directories too — and is still
    // descended into, because what is inside may match as well.
    public async IAsyncEnumerable<FsEntry> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // A server that can search answers in one request what a walk asks of it once per folder.
        if (_providers.Resolve(_root) is ISearchingProvider server
            && await server.SearchAsync(_root, NameQuery.Terms(Uri.Parameter), ct).ConfigureAwait(false) is { } found
            && FromServer(found) is { } rows)
        {
            foreach (var row in rows)
                yield return row;

            yield break;
        }

        await foreach (var row in TreeWalk.WalkAsync(
                               _providers, _root, _prune, MaxDepth,
                               (entry, prefix) => _matcher.Matches(entry) ? Row(entry, prefix) : null,
                               ct,
                               keepGoing: true)
                           .ConfigureAwait(false))
        {
            yield return row;
        }
    }

    /// <summary>
    /// A find as a row: the folder it is in, and how it ranks. As in the address bar's search, but
    /// one folder is one place, so the place drops out of the key: what a person made before what a
    /// build left, then how well the name matches, how recently it changed, and how deep it lies.
    /// </summary>
    private FsEntry Row(FsEntry entry, string prefix) =>
        FlatListingSource.WithFolder(
            entry, prefix, Relevance.Key(likelihood: 0, _matcher.Rank(entry.Name), entry, prefix, _now));

    /// <summary>
    /// A server's finds as the walk would have given them: by our rule rather than the server's,
    /// and not from inside a hidden folder the walk would not have entered. Null when the answer
    /// cannot be the folder's, and the walk has to do it.
    /// </summary>
    /// <remarks>
    /// <para>A find outside the root means the server and we disagree about where the folder is (a
    /// proxy that rewrites paths, a user name typed in another form), and its finds inside the root
    /// may then be missing too. Dropping the strays would show a short answer as a whole one.</para>
    ///
    /// <para>A row whose address does not end in the name it shows is dropped: a name with a
    /// backslash, a <c>..</c> or an escaped slash in it reads as a path, and Del on the row would
    /// act on the file or folder that path leads to rather than the one on screen.</para>
    ///
    /// <para>The server looks inside hidden folders, so those finds are dropped here and counted, as
    /// the walk counts the folders it declined: Ctrl+H brings them back either way.</para>
    /// </remarks>
    private List<FsEntry>? FromServer(IReadOnlyList<FsEntry> found)
    {
        var under = _root.Path.TrimEnd('/') + "/";
        if (found.Any(entry => !entry.Uri.Path.StartsWith(under, StringComparison.Ordinal)))
            return null;

        var rows = new List<FsEntry>();
        foreach (var entry in found)
        {
            if (!_matcher.Matches(entry) || entry.Uri.Name != entry.Name)
                continue;

            var cut = entry.Uri.Path.LastIndexOf('/');
            var prefix = cut < under.Length ? string.Empty : entry.Uri.Path[under.Length..cut];

            if (!_filter.ShowHidden && prefix.Split('/').Any(folder => folder.StartsWith('.')))
            {
                Interlocked.Increment(ref _inHiddenFolders);
                continue;
            }

            rows.Add(Row(entry, prefix));
        }

        return rows;
    }
}

/// <summary>
/// Walks a tree for the flat and search views, several directories at a time on a local disk.
/// </summary>
/// <remarks>
/// <para>One directory at a time, a walk of C:\Windows spent most of its half minute waiting: every
/// directory is an open and a close, and on a machine with an antivirus filter the close alone is
/// a measurable wait, paid tens of thousands of times in a row. Those waits overlap well, so a
/// local walk lists up to eight directories at once. A remote one stays at one, to be gentle on a
/// single SFTP or WebDAV connection.</para>
///
/// <para>Each directory's rows are handed on together, so the order found is still grouped by
/// folder — which is what makes a streaming walk readable before it is sorted — only the folders
/// no longer arrive depth first.</para>
/// </remarks>
public static class TreeWalk
{
    public static async IAsyncEnumerable<FsEntry> WalkAsync(
        IProviderRegistry providers,
        FsUri root,
        PrunePolicy prune,
        int maxDepth,
        Func<FsEntry, string, FsEntry?> select,
        [EnumeratorCancellation] CancellationToken ct,
        IReadOnlySet<FsUri>? fenced = null,
        bool keepGoing = false)
    {
        // fenced: folders not to go into, because another walk covers them; their own row is
        // still offered. keepGoing: a folder that cannot be read is counted and passed over, for
        // a walk so wide that one unreadable corner must not end it.
        // Only a folder on a local disk: a flattened script location would otherwise start eight
        // scripts at once against somebody's GitLab.
        var parallelism = root.IsLocal ? 8 : 1;
        var work = Channel.CreateUnbounded<(FsUri Directory, string Prefix, int Depth)>();
        var found = Channel.CreateBounded<List<FsEntry>>(new BoundedChannelOptions(64) { SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Directories queued or being listed. The walk is over when it reaches zero, not when the
        // queue is momentarily empty -- a directory being listed may be about to add ten more.
        var pending = 1;
        work.Writer.TryWrite((root, string.Empty, 0));

        async Task WalkerAsync()
        {
            await foreach (var (directory, prefix, depth) in work.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                try
                {
                    // Collected before handing on, so this directory's handle or connection is done
                    // with before its subdirectories are opened.
                    var entries = new List<FsEntry>();
                    await foreach (var entry in providers.Resolve(directory).ListAsync(directory, stop.Token).ConfigureAwait(false))
                        entries.Add(entry);

                    var rows = new List<FsEntry>();
                    foreach (var entry in entries)
                    {
                        if (select(entry, prefix) is { } row)
                            rows.Add(row);

                        if (entry.IsDirectory && depth < maxDepth
                            && (fenced is null || !fenced.Contains(entry.Uri)) && prune.ShouldDescend(entry))
                        {
                            Interlocked.Increment(ref pending);
                            work.Writer.TryWrite((entry.Uri, prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}", depth + 1));
                        }
                    }

                    if (rows.Count > 0)
                        await found.Writer.WriteAsync(rows, stop.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (keepGoing && e is IOException or UnauthorizedAccessException)
                {
                    prune.CouldNotRead();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // One unreadable directory ends the walk with its error, as it did before;
                    // the others are told to stop rather than left to finish a walk nobody reads.
                    found.Writer.TryComplete(e);
                    stop.Cancel();
                    return;
                }
                finally
                {
                    if (Interlocked.Decrement(ref pending) == 0)
                        work.Writer.TryComplete();
                }
            }
        }

        var walkers = Enumerable.Range(0, parallelism).Select(_ => Task.Run(WalkerAsync, CancellationToken.None)).ToArray();
        _ = Task.WhenAll(walkers).ContinueWith(_ => found.Writer.TryComplete(), TaskScheduler.Default);

        try
        {
            await foreach (var rows in found.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (var row in rows)
                    yield return row;
            }
        }
        finally
        {
            stop.Cancel();
        }
    }
}
