using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Navigation;

/// <summary>
/// Per-tab back/forward, exactly as a browser does it: visiting somewhere new after going back
/// discards the forward entries.
/// </summary>
public sealed class NavigationHistory(int capacity = 100)
{
    private readonly List<FsUri> _entries = [];
    private int _index = -1;

    public FsUri? Current => _index >= 0 ? _entries[_index] : null;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    public IReadOnlyList<FsUri> Entries => _entries;

    public void Visit(FsUri uri)
    {
        if (Equals(Current, uri))
            return;

        if (CanGoForward)
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);

        _entries.Add(uri);
        _index = _entries.Count - 1;

        if (_entries.Count > capacity)
        {
            _entries.RemoveAt(0);
            _index--;
        }
    }

    /// <summary>Puts back a history saved from an earlier session, standing at <paramref name="position"/>.</summary>
    public void Restore(IReadOnlyList<FsUri> entries, int position)
    {
        _entries.Clear();
        _entries.AddRange(entries.TakeLast(capacity));
        var dropped = entries.Count - _entries.Count;
        _index = _entries.Count == 0 ? -1 : Math.Clamp(position - dropped, 0, _entries.Count - 1);
    }

    /// <summary>Where in <see cref="Entries"/> the tab stands; -1 before it has been anywhere.</summary>
    public int Position => _index;

    public FsUri? Back() => CanGoBack ? _entries[--_index] : null;

    public FsUri? Forward() => CanGoForward ? _entries[++_index] : null;

    /// <summary>Stands at an entry picked from a history menu, as that many Backs or Forwards would; null if there is none.</summary>
    public FsUri? GoTo(int index)
    {
        if (index < 0 || index >= _entries.Count)
            return null;

        _index = index;
        return _entries[index];
    }
}
