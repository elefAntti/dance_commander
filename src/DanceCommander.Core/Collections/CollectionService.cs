using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;

namespace DanceCommander.Core.Collections;

/// <summary>
/// A named set of references (§6.19): files gathered to act on together. The members are
/// addresses, so anything the application can list can be in one — local, SFTP, inside an archive.
/// </summary>
public sealed record Collection(string Name, List<string> Items);

public sealed class CollectionsFile
{
    public List<Collection> Collections { get; set; } = [];
}

public interface ICollectionService
{
    IReadOnlyList<Collection> Collections { get; }

    /// <summary>Raised after any change, ours or a hand edit of collections.json. On any thread.</summary>
    event EventHandler? Changed;

    Collection? Find(string name);

    /// <summary>The members that parse as addresses; a hand-edited file may have others.</summary>
    IReadOnlyList<FsUri> Members(string name);

    /// <summary>The collection last added to, which the next "Add to collection" proposes.</summary>
    string? LastUsed { get; }

    Task CreateAsync(string name, CancellationToken ct = default);

    Task DeleteAsync(IReadOnlyList<string> names, CancellationToken ct = default);

    /// <summary>Adds members, creating the collection when there is none of that name. Returns how many were new.</summary>
    Task<int> AddAsync(string name, IReadOnlyList<FsUri> items, CancellationToken ct = default);

    /// <summary>Takes members out of the collection. The files are not touched.</summary>
    Task RemoveAsync(string name, IReadOnlyList<FsUri> items, CancellationToken ct = default);
}

public sealed class CollectionService : ICollectionService
{
    private readonly IConfigStore<CollectionsFile> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CollectionService(IConfigStore<CollectionsFile> store, IRelocationFeed? relocations = null)
    {
        _store = store;
        _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        if (relocations is not null)
            relocations.Relocated += (_, moved) => _ = FollowAsync(moved);
    }

    public IReadOnlyList<Collection> Collections => _store.Current.Collections;

    public event EventHandler? Changed;

    public string? LastUsed { get; private set; }

    public Collection? Find(string name) =>
        Collections.FirstOrDefault(c => Same(c.Name, name));

    public IReadOnlyList<FsUri> Members(string name) =>
        Find(name)?.Items.Select(Parse).OfType<FsUri>().ToList() ?? [];

    public Task CreateAsync(string name, CancellationToken ct = default) =>
        ChangeAsync(list =>
        {
            if (list.Any(c => Same(c.Name, name)))
                return false;
            list.Add(new Collection(name.Trim(), []));
            return true;
        }, ct);

    public Task DeleteAsync(IReadOnlyList<string> names, CancellationToken ct = default) =>
        ChangeAsync(list => list.RemoveAll(c => names.Any(n => Same(c.Name, n))) > 0, ct);

    public async Task<int> AddAsync(string name, IReadOnlyList<FsUri> items, CancellationToken ct = default)
    {
        var added = 0;
        await ChangeAsync(list =>
        {
            var index = list.FindIndex(c => Same(c.Name, name));
            var members = index < 0 ? [] : list[index].Items;
            var present = new HashSet<FsUri>(members.Select(Parse).OfType<FsUri>());

            foreach (var item in items)
            {
                if (present.Add(item))
                {
                    members.Add(item.ToString());
                    added++;
                }
            }

            if (index < 0)
                list.Add(new Collection(name.Trim(), members));
            return index < 0 || added > 0;
        }, ct).ConfigureAwait(false);

        LastUsed = Find(name)?.Name ?? name.Trim();
        return added;
    }

    public Task RemoveAsync(string name, IReadOnlyList<FsUri> items, CancellationToken ct = default) =>
        ChangeAsync(list =>
        {
            var collection = list.Find(c => Same(c.Name, name));
            if (collection is null)
                return false;

            var gone = new HashSet<FsUri>(items);
            return collection.Items.RemoveAll(m => Parse(m) is { } uri && gone.Contains(uri)) > 0;
        }, ct);

    /// <summary>
    /// Rewrites the members the application just moved or renamed. A member inside a moved folder
    /// moves with it; a member moved away and back ends where it started.
    /// </summary>
    private Task FollowAsync(IReadOnlyList<Relocation> moved) =>
        ChangeAsync(list =>
        {
            var changed = false;
            foreach (var collection in list)
            {
                for (var i = 0; i < collection.Items.Count; i++)
                {
                    if (Parse(collection.Items[i]) is not { } uri)
                        continue;
                    foreach (var relocation in moved)
                    {
                        if (relocation.Apply(uri) is { } now)
                        {
                            collection.Items[i] = now.ToString();
                            changed = true;
                            break;
                        }
                    }
                }
            }
            return changed;
        }, CancellationToken.None);

    /// <summary>
    /// One change at a time, each made to a copy of the latest list and saved only when it changed
    /// something — so a reader never sees a list half-way through an edit.
    /// </summary>
    private async Task ChangeAsync(Func<List<Collection>, bool> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = _store.Current.Collections.Select(c => c with { Items = [.. c.Items] }).ToList();
            if (!change(list))
                return;
            await _store.SaveAsync(new CollectionsFile { Collections = list }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static FsUri? Parse(string item) => FsUri.TryParse(item, out var uri, out _) ? uri : null;

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
