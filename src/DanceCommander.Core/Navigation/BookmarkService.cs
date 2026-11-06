using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;

namespace DanceCommander.Core.Navigation;

/// <summary>
/// A bookmark targets an <see cref="FsUri"/>, so <c>sftp://build01/var/log</c> is bookmarkable in
/// exactly the same way as <c>C:\src</c>. That falls out of the addressing design rather than
/// needing remote-specific support (§6.2).
/// </summary>
public sealed record Bookmark(string Name, string Uri, string? Group = null);

public sealed class BookmarksFile
{
    public List<Bookmark> Bookmarks { get; set; } = [];
}

public interface IBookmarkService
{
    IReadOnlyList<Bookmark> Bookmarks { get; }

    event EventHandler? Changed;

    Task AddAsync(string name, FsUri uri, string? group = null, CancellationToken ct = default);

    Task RemoveAsync(string name, CancellationToken ct = default);

    /// <summary>Gives a bookmark another label; false when the new one is taken by another bookmark.</summary>
    Task<bool> RenameAsync(string name, string newName, CancellationToken ct = default);

    /// <summary>Moves every bookmark of one group into another, which may be new.</summary>
    Task RenameGroupAsync(string group, string newGroup, CancellationToken ct = default);

    /// <summary>Puts these bookmarks in a group; null or blank takes them out of any.</summary>
    Task SetGroupAsync(IReadOnlyList<string> names, string? group, CancellationToken ct = default);

    /// <summary>Resolves a bookmark name typed into the address bar.</summary>
    Bookmark? Find(string name);
}

public sealed class BookmarkService : IBookmarkService
{
    private readonly IConfigStore<BookmarksFile> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How long a first run waits for Explorer before settling for the fixed places alone.</summary>
    private static readonly TimeSpan SeedDeadline = TimeSpan.FromSeconds(2);

    public BookmarkService(IConfigStore<BookmarksFile> store, IRelocationFeed? relocations = null, IQuickAccess? quickAccess = null)
    {
        _store = store;
        _store.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        if (relocations is not null)
            relocations.Relocated += (_, moved) => _ = FollowAsync(moved);

        // Only for a file that is not there. No bookmarks loaded is also what a file that failed
        // to parse, or was locked at startup, or that the user emptied on purpose looks like, and
        // writing the defaults over any of those replaced the user's own list.
        if (_store.Current.Bookmarks.Count == 0 && _store is not IConfigFile { FileExists: true })
        {
            if (quickAccess is null)
                _ = _store.SaveAsync(new BookmarksFile { Bookmarks = Defaults() });
            else
                FirstRun = SeedAsync(quickAccess);
        }
    }

    /// <summary>
    /// On a run that found no bookmarks.json and could ask Explorer: how many bookmarks came from
    /// Quick access, once the list is saved. Null on every other run.
    /// </summary>
    public Task<int>? FirstRun { get; }

    public IReadOnlyList<Bookmark> Bookmarks => _store.Current.Bookmarks;

    /// <summary>
    /// The fixed places and Explorer's pinned folders (SETTINGS_PLAN.md §8). Off the startup path:
    /// the shell can be slow to answer, and the panes need no bookmarks to list.
    /// </summary>
    private async Task<int> SeedAsync(IQuickAccess quickAccess)
    {
        // No ConfigureAwait(false): Changed is raised where this resumes, and what listens to it
        // updates the window's lists.
        IReadOnlyList<PinnedFolder> pinned = [];
        using (var deadline = new CancellationTokenSource(SeedDeadline))
        {
            try
            {
                pinned = await quickAccess.PinnedAsync(deadline.Token).WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                // Too slow: the fixed places alone, and Import from Explorer is there for later.
            }
        }

        var seeded = QuickAccessBookmarks.FirstRun(pinned, Defaults());
        var fromExplorer = 0;
        await ChangeAsync(
            current =>
            {
                // A bookmarks.json that arrived while Explorer was answering (put back by hand, a
                // restore, a sync) is the user's list, and is not a first run after all. The store
                // has nothing loaded to compare it with, so this is the one check between it and
                // being written over.
                if (_store is IConfigFile { FileExists: true })
                    return null;

                // A bookmark made in the moments before Explorer answered stays, and wins its name.
                var kept = seeded.Where(s => !current.Any(b => string.Equals(b.Name, s.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                fromExplorer = kept.Count(b => b.Group == QuickAccessBookmarks.Group);
                return [.. kept, .. current];
            },
            CancellationToken.None);
        return fromExplorer;
    }

    public event EventHandler? Changed;

    public Task AddAsync(string name, FsUri uri, string? group = null, CancellationToken ct = default) =>
        ChangeAsync(
            current => current
                .Where(b => !string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                .Append(new Bookmark(name, uri.ToString(), string.IsNullOrWhiteSpace(group) ? null : group.Trim()))
                .ToList(),
            ct);

    public Task RemoveAsync(string name, CancellationToken ct = default) =>
        ChangeAsync(current => current.Where(b => !string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)).ToList(), ct);

    public async Task<bool> RenameAsync(string name, string newName, CancellationToken ct = default)
    {
        newName = newName.Trim();
        var renamed = false;
        await ChangeAsync(
            current =>
            {
                var bookmark = current.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
                var other = current.FirstOrDefault(b => string.Equals(b.Name, newName, StringComparison.OrdinalIgnoreCase));
                if (bookmark is null || (other is not null && !ReferenceEquals(other, bookmark)))
                    return null;

                renamed = true;
                return current.Select(b => ReferenceEquals(b, bookmark) ? b with { Name = newName } : b).ToList();
            },
            ct);
        return renamed;
    }

    public Task RenameGroupAsync(string group, string newGroup, CancellationToken ct = default)
    {
        var target = string.IsNullOrWhiteSpace(newGroup) ? null : newGroup.Trim();
        return ChangeAsync(
            current => current
                .Select(b => string.Equals(b.Group, group, StringComparison.OrdinalIgnoreCase) ? b with { Group = target } : b)
                .ToList(),
            ct);
    }

    public Task SetGroupAsync(IReadOnlyList<string> names, string? group, CancellationToken ct = default)
    {
        var target = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        return ChangeAsync(
            current => current
                .Select(b => names.Contains(b.Name, StringComparer.OrdinalIgnoreCase) ? b with { Group = target } : b)
                .ToList(),
            ct);
    }

    /// <summary>Bookmarks follow the moves and renames the application makes, as collections do (§6.19).</summary>
    private Task FollowAsync(IReadOnlyList<Relocation> moved) =>
        ChangeAsync(
            current =>
            {
                var changed = false;
                var bookmarks = current.Select(b =>
                {
                    if (!FsUri.TryParse(b.Uri, out var uri, out _))
                        return b;
                    foreach (var relocation in moved)
                    {
                        if (relocation.Apply(uri) is { } now)
                        {
                            changed = true;
                            return b with { Uri = now.ToString() };
                        }
                    }
                    return b;
                }).ToList();

                return changed ? bookmarks : null;
            },
            CancellationToken.None);

    /// <summary>
    /// Every change to the list, one at a time: read, change, save. Two at once each read the same
    /// list and the second save dropped the first one's bookmark, and the two writes could reach the
    /// disk in the other order (review, 2026-10-14). A change that returns null leaves things as they are.
    /// </summary>
    private async Task ChangeAsync(Func<IReadOnlyList<Bookmark>, List<Bookmark>?> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (change(_store.Current.Bookmarks) is not { } bookmarks)
                return;

            await _store.SaveAsync(new BookmarksFile { Bookmarks = bookmarks }, ct);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Bookmark? Find(string name) =>
        Bookmarks.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    private static List<Bookmark> Defaults()
    {
        List<Bookmark> defaults = [];
        Add(Environment.SpecialFolder.UserProfile, "Home");
        Add(Environment.SpecialFolder.Desktop, "Desktop");
        Add(Environment.SpecialFolder.MyDocuments, "Documents");
        Add(Environment.SpecialFolder.MyComputer, "Downloads", @"%USERPROFILE%\Downloads");
        return defaults;

        void Add(Environment.SpecialFolder folder, string name, string? literal = null)
        {
            var path = literal is null
                ? Environment.GetFolderPath(folder)
                : Environment.ExpandEnvironmentVariables(literal);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                defaults.Add(new Bookmark(name, FsUri.Local(path).ToString()));
        }
    }
}
