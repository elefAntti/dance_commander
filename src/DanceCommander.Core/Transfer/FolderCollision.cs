using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Transfer;

public enum FolderCollisionChoice
{
    /// <summary>Keep what is already there; ask about individual files as they clash.</summary>
    Merge,

    /// <summary>The destination folder should end up being exactly the source.</summary>
    Replace,

    Skip,
    Cancel,
}

/// <param name="Lost">
/// The files in the destination that the source does not have, as paths relative to it. These
/// are what the user is actually deciding about: everything else would be overwritten by the copy
/// anyway.
/// </param>
/// <param name="Seen">
/// Every file in the destination as it was when this was worked out, by path: what an answer of
/// "replace" was about. The swap keeps anything there that is new or changed since.
/// </param>
public sealed record FolderCollision(
    FsUri Source, FsUri Destination, IReadOnlySet<string> Lost, IReadOnlyDictionary<string, FsEntry> Seen)
{
    /// <summary>How many files a replace would remove.</summary>
    public int Losses => Lost.Count;

    /// <summary>What "replace" would cost, in the words the prompt should use.</summary>
    public string Cost => Losses switch
    {
        0 => "Nothing there is missing from the source, so nothing would be lost.",
        1 => "1 file there is not in the source and would be deleted.",
        _ => $"{Losses:N0} files there are not in the source and would be deleted.",
    };
}

/// <summary>
/// Works out what a folder dropped onto a folder of the same name would cost.
/// </summary>
/// <remarks>
/// Merging is the default because it is what Explorer, <c>cp -r</c> and every commander do, and
/// because it is the one that cannot destroy anything. Replacing exists for the case macOS built
/// its whole behaviour around: a folder that is really a single artifact — a build output, a
/// deployed application, a virtual environment — where merging two versions leaves stale files
/// behind and produces something that runs but is wrong.
///
/// The choice is offered once per colliding folder, before anything is copied, and it states the
/// cost as a number. "Replace" is the word Explorer uses for one file; reusing it for a folder
/// without saying what disappears is how people lose work.
/// </remarks>
public static class FolderCollisionScanner
{
    /// <summary>
    /// Finds top-level name clashes between the sources and the target directory, counting what a
    /// replace would remove.
    /// </summary>
    /// <param name="linksAsThemselves">
    /// For a move, which carries a folder link as itself: a link picked is no folder to collide,
    /// and a link inside one lands nothing on the paths under its name in the destination, nor
    /// for certain on the name itself (it stays where it is if it can't be made there), so it
    /// keeps nothing. A copy walks through links, and so does this then.
    /// </param>
    public static async Task<IReadOnlyList<FolderCollision>> ScanAsync(
        IProviderRegistry providers,
        IReadOnlyList<FsUri> sources,
        FsUri targetDirectory,
        CancellationToken ct,
        bool linksAsThemselves = false)
    {
        var collisions = new List<FolderCollision>();
        var targetProvider = providers.Resolve(targetDirectory);

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            var sourceProvider = providers.Resolve(source);
            if (await sourceProvider.StatAsync(source, ct).ConfigureAwait(false) is not { IsDirectory: true } picked ||
                (linksAsThemselves && picked.LinkTarget is not null))
                continue;

            // A name that is not one name here (a server's "a:b" on Windows) is left out of the
            // copy with a note, by the transfer; it has no folder of that name to collide with.
            FsUri destination;
            try
            {
                destination = targetDirectory.Child(source.Name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (await targetProvider.StatAsync(destination, ct).ConfigureAwait(false) is not { IsDirectory: true })
                continue;

            // A file there is kept only by a source file landing on its name, and whether two
            // names are one is the target's rule: on a server "Readme.md" and "README.md" are two
            // files, and comparing them as Windows does counted the first as kept when nothing
            // would land on it (review, 2026-10-22). Off Windows the answer may overcount, which
            // only asks more plainly.
            var names = NamesIn(destination);
            var inSource = await RelativePathsAsync(
                sourceProvider, source, names, ct, linksAsThemselves ? LinkCount.Nothing : LinkCount.Through).ConfigureAwait(false);
            var seen = await EntriesAsync(targetProvider, destination, names, ct).ConfigureAwait(false);
            var inDestination = new HashSet<string>(seen.Keys, names);

            inDestination.ExceptWith(inSource);
            collisions.Add(new FolderCollision(source, destination, inDestination, seen));
        }

        return collisions;
    }

    /// <summary>Whether two names in <paramref name="destination"/> are one: Windows' rule there, exact elsewhere.</summary>
    internal static StringComparer NamesIn(FsUri destination) =>
        destination.IsLocal ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>What a folder link inside a tree counts as.</summary>
    internal enum LinkCount
    {
        /// <summary>One entry, by its name: what a folder discarded takes with it.</summary>
        Entry,

        /// <summary>Nothing: a link a move carries keeps nothing in the destination for certain.</summary>
        Nothing,

        /// <summary>The files of the folder it points to, as a copy walks it.</summary>
        Through,
    }

    /// <summary>Every file below a directory, as paths relative to it.</summary>
    internal static async Task<HashSet<string>> RelativePathsAsync(
        IFileSystemProvider provider, FsUri root, StringComparer names, CancellationToken ct, LinkCount links = LinkCount.Entry) =>
        new((await EntriesAsync(provider, root, names, ct, links).ConfigureAwait(false)).Keys, names);

    /// <summary>Every file below a directory as it is now, by path relative to it.</summary>
    internal static async Task<Dictionary<string, FsEntry>> EntriesAsync(
        IFileSystemProvider provider, FsUri root, StringComparer names, CancellationToken ct, LinkCount links = LinkCount.Entry)
    {
        var found = new Dictionary<string, FsEntry>(names);
        await WalkAsync(provider, root, prefix: string.Empty, found, links, ct).ConfigureAwait(false);
        return found;
    }

    private static async Task WalkAsync(
        IFileSystemProvider provider, FsUri directory, string prefix, Dictionary<string, FsEntry> found, LinkCount links, CancellationToken ct)
    {
        await foreach (var entry in provider.ListAsync(directory, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var relative = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";

            // Walked through, a move's link counted the files of the folder it points to as the
            // source's, so the old folder's files at those paths, and at its own name, read as
            // kept by a link that may stay where it is (reviews, 2026-10-22 and 23).
            var isLink = entry.IsDirectory && entry.LinkTarget is not null;
            if (entry.IsDirectory && (!isLink || links == LinkCount.Through))
                await WalkAsync(provider, entry.Uri, relative, found, links, ct).ConfigureAwait(false);
            else if (!isLink || links == LinkCount.Entry)
                found[relative] = entry;
        }
    }
}
