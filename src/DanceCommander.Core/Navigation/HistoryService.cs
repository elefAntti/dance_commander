using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Navigation;

public sealed class HistoryFile
{
    /// <summary>Most recent first, as persisted URI strings.</summary>
    public List<string> Recent { get; set; } = [];

    /// <summary>
    /// When each was last visited, by the same strings. Beside the list rather than in it so that
    /// files from before times were kept still read; places visited before then have none.
    /// </summary>
    public Dictionary<string, DateTimeOffset>? Visited { get; set; }
}

/// <summary>A place in the history: when it was last visited if known, and where it stood in the list.</summary>
public sealed record Visit(FsUri Uri, DateTimeOffset? At, int Position);

public interface IHistoryService
{
    IReadOnlyList<FsUri> Recent { get; }

    /// <summary>The same list with when each was last visited: what <c>about:history</c> shows.</summary>
    IReadOnlyList<Visit> Visits { get; }

    /// <summary>Raised after the list changed. On any thread.</summary>
    event EventHandler? Changed;

    void Record(FsUri uri);

    /// <summary>Takes places out of the history; returns them as they were, for putting back.</summary>
    IReadOnlyList<Visit> Forget(IReadOnlyList<FsUri> uris);

    /// <summary>Puts forgotten places back where they stood, unless visited again since.</summary>
    void Restore(IReadOnlyList<Visit> visits);

    /// <summary>Address-bar completions, bookmarks first, then recent locations.</summary>
    IReadOnlyList<Suggestion> Suggest(string text, int max = 8);

    /// <summary>
    /// What the address bar fills in after what was typed, as a browser's does, or null. Starts
    /// with <paramref name="text"/>, ignoring case, and may be exactly as long as it.
    /// </summary>
    string? Complete(string text);
}

/// <param name="Label">
/// The line that identifies the location. A bookmark's own name, which is not derivable from its
/// path — but for a recent location, the path itself: a separate name line there only repeats the
/// last segment of the path printed underneath it.
/// </param>
/// <param name="Detail">Supporting line. Empty when the label already says everything.</param>
public sealed record Suggestion(string Label, string Detail, FsUri Uri, bool IsBookmark);

/// <summary>
/// The global most-recently-visited list behind address-bar completion. Credentials never reach
/// it: a remote URI records scheme, authority and path only (§6.2).
/// </summary>
public sealed class HistoryService : IHistoryService, IDisposable
{
    private const int Capacity = 2000;

    private readonly IConfigStore<HistoryFile> _store;
    private readonly IBookmarkService _bookmarks;
    private readonly Debouncer _save = new(TimeSpan.FromSeconds(3));
    private readonly List<FsUri> _recent;
    private readonly Dictionary<FsUri, DateTimeOffset> _visited = [];
    private readonly Lock _gate = new();

    public HistoryService(IConfigStore<HistoryFile> store, IBookmarkService bookmarks)
    {
        _store = store;
        _bookmarks = bookmarks;
        _recent = store.Current.Recent
            .Select(text => FsUri.TryParse(text, out var uri, out _) ? uri : null)
            .OfType<FsUri>()
            .ToList();

        foreach (var (text, at) in store.Current.Visited ?? [])
        {
            if (FsUri.TryParse(text, out var uri, out _))
                _visited[uri] = at;
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<Visit> Visits
    {
        get
        {
            lock (_gate)
                return _recent.Select((uri, i) => new Visit(uri, _visited.TryGetValue(uri, out var at) ? at : null, i)).ToList();
        }
    }

    public IReadOnlyList<Visit> Forget(IReadOnlyList<FsUri> uris)
    {
        var gone = new HashSet<FsUri>(uris);
        List<Visit> removed;
        lock (_gate)
        {
            removed = _recent
                .Select((uri, i) => new Visit(uri, _visited.TryGetValue(uri, out var at) ? at : null, i))
                .Where(v => gone.Contains(v.Uri))
                .ToList();
            _recent.RemoveAll(gone.Contains);
            foreach (var uri in gone)
                _visited.Remove(uri);
        }

        if (removed.Count > 0)
            OnChanged();
        return removed;
    }

    public void Restore(IReadOnlyList<Visit> visits)
    {
        lock (_gate)
        {
            // In the order they stood, so each position is counted with the ones before it back.
            foreach (var visit in visits.OrderBy(v => v.Position))
            {
                if (_recent.Contains(visit.Uri))
                    continue;
                _recent.Insert(Math.Min(visit.Position, _recent.Count), visit.Uri);
                if (visit.At is { } at)
                    _visited[visit.Uri] = at;
            }
        }

        OnChanged();
    }

    private void OnChanged()
    {
        _save.Schedule(Flush);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<FsUri> Recent
    {
        get
        {
            lock (_gate)
                return _recent.ToList();
        }
    }

    public void Record(FsUri uri)
    {
        lock (_gate)
        {
            _recent.RemoveAll(existing => existing == uri);
            _recent.Insert(0, uri);
            _visited[uri] = DateTimeOffset.Now;
            if (_recent.Count > Capacity)
            {
                foreach (var dropped in _recent.Skip(Capacity))
                    _visited.Remove(dropped);
                _recent.RemoveRange(Capacity, _recent.Count - Capacity);
            }
        }

        OnChanged();
    }

    public IReadOnlyList<Suggestion> Suggest(string text, int max = 8)
    {
        var term = text.Trim();
        if (term.Length == 0)
            return [];

        // Words, when the text is a search rather than an address (no separator, no colon): every
        // one has to be there, in any order, as in the command palette. "dan work" finds
        // dance_worktree; as one string with its space it found nothing (2026-10-17). An address
        // is matched whole, as typed: "C:\Program Files" is not two words.
        IReadOnlyList<string> words = AddressSearch.QueryIn(term) is not null && !AddressSearch.IsExplicit(term)
            ? term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [term];

        var results = new List<Suggestion>();

        // A bookmark is a name you gave a place so you could jump to it, and you jump by typing the
        // start of that name — the same rule the quick filter follows. So a name that starts with
        // the term comes first, then one containing it, then one that only matched in its path;
        // within each, the order of the file, which is the order you chose.
        var bookmarks = _bookmarks.Bookmarks
            .Select((bookmark, order) => (Bookmark: bookmark, Rank: BookmarkRank(bookmark, words), Order: order))
            .Where(b => b.Rank < 3)
            .OrderBy(b => b.Rank)
            .ThenBy(b => b.Order);

        foreach (var (bookmark, _, _) in bookmarks)
        {
            if (results.Count >= max)
                break;
            if (FsUri.TryParse(bookmark.Uri, out var uri, out _))
                results.Add(new Suggestion(bookmark.Name, uri.Display, uri, IsBookmark: true));
        }

        List<FsUri> recent;
        lock (_gate)
            recent = _recent.ToList();

        // Most-recent-first is the wrong order on its own: it answers "where have I been" when the
        // question being asked is "which of these did I mean". Rank by how well the term matches
        // the folder's own name, then prefer the shorter path — the parent you typed the name of
        // rather than something buried under it — and only then fall back to recency.
        var ranked = recent
            .Where(uri => All(uri.Display, words))
            .Where(uri => results.All(existing => existing.Uri != uri))
            .Select((uri, recency) => (Uri: uri, Rank: Rank(uri, words), Length: uri.Display.Length, Recency: recency))
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.Length)
            .ThenBy(c => c.Recency)
            .Take(Math.Max(0, max - results.Count));

        foreach (var candidate in ranked)
            results.Add(new Suggestion(candidate.Uri.Display, string.Empty, candidate.Uri, IsBookmark: false));

        return results;
    }

    public string? Complete(string text)
    {
        if (text.Trim().Length == 0)
            return null;

        // A bookmark's name first: typing the start of one and pressing Enter already goes there
        // (§6.2), so completing it only shows where Enter is about to take you.
        var bookmarks = _bookmarks.Bookmarks;
        if (bookmarks.FirstOrDefault(b => b.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) is { } named)
            return named.Name;

        List<FsUri> recent;
        lock (_gate)
            recent = _recent.ToList();

        // Then the shortest address that carries on from the text: the folder whose name is being
        // typed rather than something buried under it, which a longer completion would be one
        // careless Enter away from. Only to the end of that name, too: with only
        // C:\src\app\src\Core in the history, a typed C:\src\app\src (a folder in its own right)
        // was completed into Core, and Enter went there instead of where it was told.
        return bookmarks
            .Select(b => FsUri.TryParse(b.Uri, out var uri, out _) ? uri : null)
            .OfType<FsUri>()
            .Concat(recent)
            .Where(uri => uri.Display.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .Select(uri => ToEndOfSegment(uri, text.Length))
            .Where(display => display.Length > text.Length)
            .MinBy(display => display.Length);
    }

    /// <summary>
    /// The address up to the first separator at or after <paramref name="from"/>, past any
    /// <c>sftp://</c> so a scheme is not a segment. A view's address (<c>from:</c>, <c>about:</c>)
    /// is one name, not a path, and is kept whole.
    /// </summary>
    private static string ToEndOfSegment(FsUri uri, int from)
    {
        var address = uri.Display;
        if (uri.IsView || uri.IsAbout)
            return address;

        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            from = Math.Max(from, scheme + 3);
        var separator = address.IndexOfAny(['\\', '/'], from);
        return separator < 0 ? address : address[..separator];
    }

    /// <summary>
    /// Lower is better. What you type is nearly always the beginning of the folder you want, so a
    /// term that starts the last segment beats one merely inside it, which beats one that only
    /// matched somewhere further up the path.
    /// With several words the same, judged on them all: every word in the last segment, the first
    /// of them starting it, is the best.
    /// </summary>
    private static int Rank(FsUri uri, IReadOnlyList<string> words)
    {
        if (!All(uri.Name, words))
            return 2;
        return uri.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    /// <summary>Lower is better; 3 means no match at all. A word may be in the name or the path.</summary>
    private static int BookmarkRank(Bookmark bookmark, IReadOnlyList<string> words)
    {
        if (All(bookmark.Name, words))
            return bookmark.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        return words.All(w => Contains(bookmark.Name, w) || Contains(bookmark.Uri, w)) ? 2 : 3;
    }

    private static bool All(string haystack, IReadOnlyList<string> words) =>
        words.All(word => Contains(haystack, word));

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void Flush() => _ = FlushAsync();

    private Task FlushAsync()
    {
        HistoryFile snapshot;
        lock (_gate)
        {
            snapshot = new HistoryFile
            {
                Recent = _recent.Select(uri => uri.ToString()).ToList(),
                Visited = _visited.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
            };
        }

        return _store.SaveAsync(snapshot);
    }

    /// <summary>
    /// Waits for the last write, as the application shuts down: fired and forgotten, the places
    /// visited in the last few seconds could be lost with the process.
    /// </summary>
    public void Dispose()
    {
        _save.Dispose();
        Debouncer.FlushAtExit(FlushAsync);
    }
}
