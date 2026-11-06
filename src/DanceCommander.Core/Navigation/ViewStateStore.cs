using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;

namespace DanceCommander.Core.Navigation;

/// <summary>What a pane remembers about a location it has shown before.</summary>
/// <param name="Chosen">
/// The order or filter was set here on purpose — a column clicked, hidden files toggled — rather
/// than inherited from wherever the tab came from. Only chosen views outlive a restart.
/// </param>
public sealed record ViewState(
    ListingOrder Order, ListingFilter Filter, string? FocusedName = null, bool Chosen = false);

public interface IViewStateStore
{
    ViewState? Get(FsUri uri);

    void Set(FsUri uri, ViewState state);
}

/// <summary>
/// Per-location view memory (§1.2/5): sort order, filter and which row you were on, restored when
/// you come back. Bounded so that browsing a large tree cannot grow it without limit.
/// </summary>
/// <remarks>
/// In memory, which covers a working session; <see cref="PersistentViewStateStore"/> keeps the
/// chosen part of it across restarts.
/// </remarks>
public sealed class ViewStateStore(int capacity = 500) : IViewStateStore
{
    private readonly Dictionary<FsUri, LinkedListNode<Entry>> _byUri = [];
    private readonly LinkedList<Entry> _recency = new(); // most recent first
    private readonly Lock _gate = new();

    private sealed record Entry(FsUri Uri, ViewState State);

    public ViewState? Get(FsUri uri)
    {
        lock (_gate)
        {
            if (!_byUri.TryGetValue(uri, out var node))
                return null;

            _recency.Remove(node);
            _recency.AddFirst(node);
            return node.Value.State;
        }
    }

    public void Set(FsUri uri, ViewState state)
    {
        lock (_gate)
        {
            if (_byUri.TryGetValue(uri, out var existing))
            {
                _recency.Remove(existing);
                _byUri.Remove(uri);
            }

            var node = _recency.AddFirst(new Entry(uri, state));
            _byUri[uri] = node;

            while (_byUri.Count > capacity && _recency.Last is { } oldest)
            {
                _recency.RemoveLast();
                _byUri.Remove(oldest.Value.Uri);
            }
        }
    }
}

/// <summary>viewstate.json: the views chosen on purpose, most recent first.</summary>
public sealed class ViewStateFile
{
    public List<SavedView> Locations { get; set; } = [];
}

public sealed class SavedView
{
    public string Uri { get; set; } = string.Empty;

    public string Column { get; set; } = Columns.ColumnIds.Name;

    public bool Descending { get; set; }

    public bool DirectoriesFirst { get; set; }

    public bool ShowHidden { get; set; }
}

/// <summary>
/// The per-location memory, kept across restarts for the locations where it was chosen: Downloads
/// sorted newest first stays that way tomorrow (§1.2/5).
/// </summary>
/// <remarks>
/// Only chosen views are written. Every folder passed through inherits the tab's settings and is
/// remembered for the session, but writing all of those would make the file a log of everywhere
/// you have been, growing with every walk; what is worth keeping is the handful of places you set
/// up on purpose. Bounded as well, at the same 500, and written at most every few seconds. The row
/// you were on is not kept: it is session detail, and would rewrite the file on every visit.
/// </remarks>
public sealed class PersistentViewStateStore : IViewStateStore, IDisposable
{
    private const int Capacity = 500;

    private readonly IConfigStore<ViewStateFile> _store;
    private readonly ViewStateStore _session = new(Capacity);
    private readonly Debouncer _save = new(TimeSpan.FromSeconds(3));
    private readonly Lock _gate = new();

    /// <summary>Chosen views, most recent last.</summary>
    private readonly List<(FsUri Uri, ViewState State)> _chosen = [];

    public PersistentViewStateStore(IConfigStore<ViewStateFile> store)
    {
        _store = store;

        // Oldest first, so the session store's recency ends up as it was.
        foreach (var saved in Enumerable.Reverse(store.Current.Locations))
        {
            if (!FsUri.TryParse(saved.Uri, out var uri, out _))
                continue;

            var state = new ViewState(
                new ListingOrder(saved.Column, saved.Descending, saved.DirectoriesFirst),
                new ListingFilter(saved.ShowHidden),
                Chosen: true);
            _session.Set(uri, state);
            _chosen.Add((uri, state));
        }
    }

    public ViewState? Get(FsUri uri) => _session.Get(uri);

    public void Set(FsUri uri, ViewState state)
    {
        _session.Set(uri, state);
        if (!state.Chosen)
            return;

        lock (_gate)
        {
            var existing = _chosen.FindIndex(c => c.Uri == uri);
            if (existing >= 0)
            {
                // A visit that changed nothing worth keeping does not need a write.
                var before = _chosen[existing].State;
                if (before.Order == state.Order && before.Filter == state.Filter)
                    return;
                _chosen.RemoveAt(existing);
            }

            _chosen.Add((uri, state));
            if (_chosen.Count > Capacity)
                _chosen.RemoveRange(0, _chosen.Count - Capacity);
        }

        _save.Schedule(Flush);
    }

    private void Flush() => _ = FlushAsync();

    private Task FlushAsync()
    {
        List<SavedView> snapshot;
        lock (_gate)
        {
            snapshot = Enumerable.Reverse(_chosen)
                .Select(c => new SavedView
                {
                    Uri = c.Uri.ToString(),
                    Column = c.State.Order.ColumnId,
                    Descending = c.State.Order.Descending,
                    DirectoriesFirst = c.State.Order.DirectoriesFirst,
                    ShowHidden = c.State.Filter.ShowHidden,
                })
                .ToList();
        }

        return _store.SaveAsync(new ViewStateFile { Locations = snapshot });
    }

    /// <summary>
    /// Waits for the last write: this runs as the application shuts down, and a save left running
    /// then is a save the process may not live to finish. Safe to block on — the store's writes
    /// never need the calling thread back.
    /// </summary>
    public void Dispose()
    {
        _save.Dispose();
        Debouncer.FlushAtExit(FlushAsync);
    }
}
