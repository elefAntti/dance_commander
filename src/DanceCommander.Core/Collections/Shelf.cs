using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;

namespace DanceCommander.Core.Collections;

/// <summary>
/// Norton's marking, carried across folders (§6.19): files gathered from anywhere while you
/// browse, to be copied, zipped or handed over together. Session only, as marks always were;
/// saving it as a collection is what keeps it.
/// </summary>
public interface IShelf
{
    IReadOnlyList<FsUri> Items { get; }

    /// <summary>Raised after any change. On any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Adds what is not already in; returns how many were new.</summary>
    int Add(IReadOnlyList<FsUri> items);

    void Remove(IReadOnlyList<FsUri> items);

    void Clear();
}

public sealed class Shelf : IShelf
{
    private readonly List<FsUri> _items = [];
    private readonly Lock _gate = new();

    /// <param name="relocations">So that what is in it follows the moves the application makes.</param>
    public Shelf(IRelocationFeed? relocations = null)
    {
        if (relocations is not null)
            relocations.Relocated += (_, moved) => Follow(moved);
    }

    public IReadOnlyList<FsUri> Items
    {
        get
        {
            lock (_gate)
                return _items.ToList();
        }
    }

    public event EventHandler? Changed;

    public int Add(IReadOnlyList<FsUri> items)
    {
        var added = 0;
        lock (_gate)
        {
            foreach (var item in items)
            {
                if (!_items.Contains(item))
                {
                    _items.Add(item);
                    added++;
                }
            }
        }

        if (added > 0)
            Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public void Remove(IReadOnlyList<FsUri> items)
    {
        int removed;
        lock (_gate)
            removed = _items.RemoveAll(items.Contains);

        if (removed > 0)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
            _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Follow(IReadOnlyList<Relocation> moved)
    {
        var changed = false;
        lock (_gate)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                foreach (var relocation in moved)
                {
                    if (relocation.Apply(_items[i]) is { } now)
                    {
                        _items[i] = now;
                        changed = true;
                        break;
                    }
                }
            }
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
