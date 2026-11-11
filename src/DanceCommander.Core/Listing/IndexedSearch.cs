using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Matching;

namespace DanceCommander.Core.Listing;

/// <summary>A path an index holds, as a Win32 path, and whether the index says it is a folder.</summary>
public readonly record struct IndexedPath(string Path, bool IsFolder);

/// <summary>
/// An index of the names on this machine's disks that can propose what a search would find, far
/// faster than walking for it: Everything (voidtools), when it is running.
/// </summary>
public interface ILocalSearchIndex
{
    /// <summary>Who answers, for the log.</summary>
    string Name { get; }

    /// <summary>
    /// Every path under <paramref name="root"/> whose name may match all of <paramref name="terms"/>
    /// (a <see cref="Matching.NameQuery"/>'s), or null when the index cannot answer for that folder
    /// or would propose more than <paramref name="limit"/>, and the caller walks instead. Its idea of
    /// a match may be looser than ours, never stricter.
    /// </summary>
    Task<IReadOnlyList<IndexedPath>?> CandidatesAsync(
        FsUri root, IReadOnlyList<string> terms, int limit, CancellationToken ct);
}

/// <summary>
/// A search answered from an index rather than a walk, giving the rows the walk would have given.
/// </summary>
/// <remarks>
/// <para>The index only proposes. Each path it offers is placed under the root name by name, the
/// address built with <see cref="FsUri.Child"/> rather than parsed from its text, and each folder
/// above it is judged as the walk would judge it on the way down: by <see cref="PrunePolicy"/>, the
/// fences, the depth limit, and whether it can be listed at all. Then the path itself is stat-ed
/// through the provider. So a row is what the disk says now, not what the index remembered: what has
/// gone since is dropped, a name ending in a dot is the file with that name (AUDIT.md §2 kind 45),
/// and a folder the user cannot open hides what is in it, as it does from the walk, though
/// Everything, reading the volume as a service, lists it.</para>
///
/// <para>An answer with a path outside the root or a name no folder can hold is not used at all:
/// the index and we disagree about the folder, and what it found inside may be wrong too.</para>
/// </remarks>
public static class IndexedSearch
{
    /// <summary>
    /// More than this and stat-ing them costs what the walk does: a one-letter search of a whole
    /// disk is a million names.
    /// </summary>
    public const int Limit = 20_000;

    /// <summary>Stat-ed this many at a time, as <see cref="TreeWalk"/> lists local folders.</summary>
    private const int Parallelism = 8;

    private static readonly char[] NotInAName = ['/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// The rows for a search of <paramref name="root"/>, or null when the index cannot answer and
    /// the caller walks. The arguments are <see cref="TreeWalk.WalkAsync"/>'s, so either can answer.
    /// </summary>
    public static async Task<IAsyncEnumerable<FsEntry>?> TryAsync(
        ILocalSearchIndex index,
        IProviderRegistry providers,
        FsUri root,
        IReadOnlyList<string> terms,
        PrunePolicy prune,
        int maxDepth,
        Func<FsEntry, string, FsEntry?> select,
        CancellationToken ct,
        IReadOnlySet<FsUri>? fenced = null)
    {
        if (!root.IsLocal || root.IsView)
            return null;

        if (await index.CandidatesAsync(root, terms, Limit, ct).ConfigureAwait(false) is not { } candidates)
            return null;

        if (Place(root, candidates) is not { } placed)
            return null;

        var matcher = NameQuery.Compile(string.Join(' ', terms));
        return RowsAsync(providers.Resolve(root), root, placed, matcher, prune, maxDepth, select, fenced, ct);
    }

    /// <summary>Each candidate's names below the root, or null when one is not under it as a path can be.</summary>
    private static List<(string[] Names, bool IsFolder)>? Place(FsUri root, IReadOnlyList<IndexedPath> candidates)
    {
        var under = root.Path.Replace('/', '\\').TrimEnd('\\') + "\\";
        var placed = new List<(string[] Names, bool IsFolder)>(candidates.Count);
        // By case: in a folder made case-sensitive (WSL's) Docs and docs are two (AUDIT.md §2 kind 78).
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var path = candidate.Path;
            if (path.Length == under.Length - 1 && under.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                continue; // the root itself: not one of its own finds

            if (!path.StartsWith(under, StringComparison.OrdinalIgnoreCase))
                return null;

            var names = path[under.Length..].Split('\\');
            if (names.Any(name => name.Length == 0 || name is "." or ".." || name.IndexOfAny(NotInAName) >= 0
                    || name.Any(c => c < ' ')))
                return null;

            if (seen.Add(path))
                placed.Add((names, candidate.IsFolder));
        }

        return placed;
    }

    /// <summary>What the walk would have done at a folder on the way down to a find.</summary>
    private enum Fate
    {
        /// <summary>Gone into: what is below may be a row.</summary>
        Walked,

        /// <summary>Never reached, or nothing there to list: what is below is no row and not counted.</summary>
        Unseen,

        /// <summary>Declined as hidden: what is below is counted as found there.</summary>
        Hidden,

        /// <summary>Declined as clutter: likewise.</summary>
        Clutter,
    }

    private static async IAsyncEnumerable<FsEntry> RowsAsync(
        IFileSystemProvider provider,
        FsUri root,
        List<(string[] Names, bool IsFolder)> placed,
        NameMatcher matcher,
        PrunePolicy prune,
        int maxDepth,
        Func<FsEntry, string, FsEntry?> select,
        IReadOnlySet<FsUri>? fenced,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Keyed by the path as written, not by FsUri, whose local equality ignores case: Docs and
        // docs in a case-sensitive folder are judged each on its own.
        var folders = new ConcurrentDictionary<string, Lazy<Task<Fate>>>(StringComparer.Ordinal);

        // The folder at depth: the root is 0 and never pruned, only listed; a folder at depth d was
        // listed in one at d - 1, and is gone into only while d - 1 is under the walk's limit.
        Task<Fate> FolderAsync(FsUri folder, int depth, string[] names) =>
            folders.GetOrAdd(folder.Path, _ => new Lazy<Task<Fate>>(() => JudgeAsync(folder, depth, names))).Value;

        async Task<Fate> JudgeAsync(FsUri folder, int depth, string[] names)
        {
            if (depth > 0)
            {
                var above = await FolderAsync(folder.Parent!, depth - 1, names).ConfigureAwait(false);
                if (above != Fate.Walked)
                    return above;

                if (depth - 1 >= maxDepth || fenced?.Contains(folder) == true)
                    return Fate.Unseen;

                if (await StatAsync(provider, folder, ct).ConfigureAwait(false) is not { IsDirectory: true } entry)
                    return Fate.Unseen;

                switch (prune.Declines(entry))
                {
                    case PrunePolicy.Decline.Hidden:
                        return Fate.Hidden;
                    case PrunePolicy.Decline.Clutter:
                        return Fate.Clutter;
                    case PrunePolicy.Decline.Link:
                        return Fate.Unseen;
                }
            }

            return Listable(folder, prune) ? Fate.Walked : Fate.Unseen;
        }

        var rows = Channel.CreateBounded<FsEntry>(new BoundedChannelOptions(1024) { SingleReader = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var producing = Parallel.ForEachAsync(
            placed,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = stop.Token },
            async (candidate, token) =>
            {
                var names = candidate.Names;
                var uri = root;
                foreach (var name in names)
                    uri = uri.Child(name);

                var fate = await FolderAsync(uri.Parent!, names.Length - 1, names).ConfigureAwait(false);
                if (fate is Fate.Hidden or Fate.Clutter)
                {
                    // Counted only if it is a find by our rule too: the index's may be looser.
                    if (matcher.Matches(new FsEntry(uri, names[^1], candidate.IsFolder, 0, null)))
                        prune.FoundInside(fate == Fate.Hidden ? PrunePolicy.Decline.Hidden : PrunePolicy.Decline.Clutter);
                    return;
                }

                if (fate == Fate.Unseen)
                    return;

                if (await StatAsync(provider, uri, token).ConfigureAwait(false) is not { } entry
                    || entry.Name != names[^1] || entry.Uri != uri)
                    return;

                if (select(entry, string.Join('/', names[..^1])) is { } row)
                    await rows.Writer.WriteAsync(row, token).ConfigureAwait(false);
            });
        _ = producing.ContinueWith(task => rows.Writer.TryComplete(task.Exception?.InnerException), TaskScheduler.Default);

        try
        {
            await foreach (var row in rows.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return row;
        }
        finally
        {
            stop.Cancel();
        }
    }

    private static async Task<FsEntry?> StatAsync(IFileSystemProvider provider, FsUri uri, CancellationToken ct)
    {
        try
        {
            return await provider.StatAsync(uri, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the walk would see what is in the folder. The local provider lists a folder the user
    /// may not open as empty, saying nothing, and any other failure is a folder that could not be
    /// read, which the walk counts.
    /// </summary>
    private static bool Listable(FsUri folder, PrunePolicy prune)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(
                folder.ToLocalPath(), "*", new EnumerationOptions { AttributesToSkip = 0 }).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Gone since it was stat-ed is no folder the walk would have met either.
            return false;
        }
        catch (IOException)
        {
            prune.CouldNotRead();
            return false;
        }
    }
}
