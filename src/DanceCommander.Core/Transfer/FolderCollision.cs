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

            // A file there is kept only by a source file landing on its name. The same name is
            // kept; any other only where the destination itself says the source's name is that
            // file (SameFileUnderAnotherNameAsync).
            var inSource = await RelativePathsAsync(
                sourceProvider, source, ct, linksAsThemselves ? LinkCount.Nothing : LinkCount.Through).ConfigureAwait(false);
            var seen = await EntriesAsync(targetProvider, destination, ct).ConfigureAwait(false);
            var inDestination = new HashSet<string>(seen.Keys, StringComparer.Ordinal);

            inDestination.ExceptWith(inSource);
            await SameFileUnderAnotherNameAsync(targetProvider, destination, inSource, seen, inDestination, ct).ConfigureAwait(false);
            collisions.Add(new FolderCollision(source, destination, inDestination, seen));
        }

        return collisions;
    }

    /// <summary>
    /// Takes out of <paramref name="lost"/> each file a source name lands on under another case,
    /// as the destination's volume judges it: "README.md" over "Readme.md" in a Windows folder.
    /// </summary>
    /// <remarks>
    /// Whether two names are one is the volume's rule, and no comparer here knows it. On a server
    /// "Readme.md" and "README.md" are two files (review, 2026-10-22); a Windows folder can be made
    /// case-sensitive, as WSL does to its own; and NTFS folds case by a table of its own, which
    /// takes Georgian ა and Ა for two names where .NET takes them for one (AUDIT.md §2 kind 78).
    /// Judged by any comparer, a file Replace deletes was counted as kept. So the destination is
    /// asked whether the source's name is that file, by identity. .NET's ignoring of case only
    /// picks which files to ask about; a pair it misses, or a provider that can't tell, leaves the
    /// file counted as lost, which only asks more plainly.
    /// Only <see cref="IFileIdentity.IsSurelySameFileAsync"/> is asked: an identity that can't tell
    /// files apart (index 0 on some shares) answers "the same" for everything, and counted the
    /// file Replace deletes as kept (review, 2026-11-07).
    ///
    /// Asked as little as it can be, as this is all before the question shows (§1.5); asked for
    /// each name, "sub" over "Sub" took 4 s for 20,000 files below it, and 5,000 photos named .JPG
    /// over .jpg 1.6 s (reviews, 2026-11-07). A folder is resolved once, and a file of the same
    /// name in it is then that file by any rule. And a folder is asked once whether it folds ASCII
    /// case, as an ordinary Windows folder does and one WSL made doesn't: where it does, a name
    /// differing only in ASCII case from the one name like it there is that name. Only the rest,
    /// Georgian's two cases for one, is asked name by name.
    /// </remarks>
    private static async Task SameFileUnderAnotherNameAsync(
        IFileSystemProvider provider,
        FsUri destination,
        IReadOnlySet<string> inSource,
        IReadOnlyDictionary<string, FsEntry> seen,
        HashSet<string> lost,
        CancellationToken ct)
    {
        if (provider is not IFileIdentity identity || lost.Count == 0)
            return;

        // The destination's files and folders by the folder they are in, as its files' paths name
        // them, each found by name regardless of case in one lookup.
        var files = new Dictionary<string, Names>(StringComparer.Ordinal);
        var folders = new Dictionary<string, Names>(StringComparer.Ordinal);
        foreach (var path in seen.Keys)
        {
            In(files, ParentOf(path)).Add(NameOf(path));
            for (var folder = ParentOf(path); folder.Length > 0; folder = ParentOf(folder))
            {
                if (!In(folders, ParentOf(folder)).Add(NameOf(folder)))
                    break; // its parents are in already
            }
        }

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal) { [string.Empty] = string.Empty };
        var folds = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var path in inSource)
        {
            if (seen.ContainsKey(path))
                continue;

            if (await ResolveAsync(ParentOf(path)).ConfigureAwait(false) is not { } folder)
                continue;

            var name = NameOf(path);
            if (lost.Remove(Join(folder, name)))
                continue;

            if (files.TryGetValue(folder, out var there) && !there.Contains(name) &&
                await LandsOnAsync(folder, name, there).ConfigureAwait(false) is { } landed)
            {
                lost.Remove(Join(folder, landed));
            }
        }

        // The destination's folder a source folder lands in, as the destination lists it, or null
        // where it lands in none it has (or the volume can't say which).
        async Task<string?> ResolveAsync(string sourceFolder)
        {
            if (resolved.TryGetValue(sourceFolder, out var known))
                return known;

            string? found = null;
            if (await ResolveAsync(ParentOf(sourceFolder)).ConfigureAwait(false) is { } parent &&
                folders.TryGetValue(parent, out var children))
            {
                var name = NameOf(sourceFolder);
                if (children.Contains(name))
                    found = Join(parent, name);
                else if (await LandsOnAsync(parent, name, children).ConfigureAwait(false) is { } landed)
                    found = Join(parent, landed);
            }

            resolved[sourceFolder] = found;
            return found;
        }

        // Which of the names listed in a folder another name there is, as the volume says, if any.
        async Task<string?> LandsOnAsync(string folder, string name, Names listed)
        {
            var like = listed.OthersLike(name);
            if (like.Count == 0)
                return null;
            if (like.Count == 1 && DiffersInAsciiCaseOnly(name, like[0]) && await FoldsAsciiCaseAsync(folder).ConfigureAwait(false))
                return like[0];

            foreach (var other in like)
            {
                if (await SurelySameAsync(Join(folder, name), Join(folder, other)).ConfigureAwait(false))
                    return other;
            }

            return null;
        }

        // Whether a folder takes a name in another ASCII case for the same name: asked of one name
        // it lists, under that name with its case turned around. Two names it lists that differ
        // only so already say it doesn't.
        async Task<bool> FoldsAsciiCaseAsync(string folder)
        {
            if (folds.TryGetValue(folder, out var known))
                return known;

            var listed = (files.GetValueOrDefault(folder)?.All ?? []).Concat(folders.GetValueOrDefault(folder)?.All ?? []);
            var probe = listed.FirstOrDefault(n => n.Any(char.IsAsciiLetter));
            var answer = false;
            if (probe is not null)
            {
                var turned = TurnAsciiCase(probe);
                var both = files.GetValueOrDefault(folder)?.Contains(turned) == true || folders.GetValueOrDefault(folder)?.Contains(turned) == true;
                answer = !both && await SurelySameAsync(Join(folder, turned), Join(folder, probe)).ConfigureAwait(false);
            }

            folds[folder] = answer;
            return answer;
        }

        async Task<bool> SurelySameAsync(string landing, string there)
        {
            ct.ThrowIfCancellationRequested();
            return At(destination, landing) is { } a && At(destination, there) is { } b &&
                await identity.IsSurelySameFileAsync(a, b, ct).ConfigureAwait(false);
        }
    }

    private static Names In(Dictionary<string, Names> byFolder, string folder) =>
        byFolder.TryGetValue(folder, out var names) ? names : byFolder[folder] = new Names();

    private static bool DiffersInAsciiCaseOnly(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && !(char.IsAsciiLetter(a[i]) && char.IsAsciiLetter(b[i]) && (a[i] | 0x20) == (b[i] | 0x20)))
                return false;
        }

        return true;
    }

    private static string TurnAsciiCase(string name) =>
        string.Create(name.Length, name, static (span, from) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = char.IsAsciiLetter(from[i]) ? (char)(from[i] ^ 0x20) : from[i];
        });

    /// <summary>The names in one folder, exactly and by name regardless of case.</summary>
    private sealed class Names
    {
        private readonly HashSet<string> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _byCase = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> All => _exact;

        public bool Add(string name)
        {
            if (!_exact.Add(name))
                return false;
            if (!_byCase.TryGetValue(name, out var like))
                _byCase[name] = like = [];
            like.Add(name);
            return true;
        }

        public bool Contains(string name) => _exact.Contains(name);

        /// <summary>The names here equal to <paramref name="name"/> regardless of case, but not it.</summary>
        public IReadOnlyList<string> OthersLike(string name) =>
            _byCase.TryGetValue(name, out var like) ? like.Where(n => !string.Equals(n, name, StringComparison.Ordinal)).ToList() : [];
    }

    private static string ParentOf(string path) => path.LastIndexOf('/') is var cut and > 0 ? path[..cut] : string.Empty;

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    /// <summary>A relative path under <paramref name="root"/>, or null for a name it can't have.</summary>
    private static FsUri? At(FsUri root, string relative)
    {
        try
        {
            var uri = root;
            foreach (var segment in relative.Split('/'))
                uri = uri.Child(segment);
            return uri;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

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
        IFileSystemProvider provider, FsUri root, CancellationToken ct, LinkCount links = LinkCount.Entry) =>
        new((await EntriesAsync(provider, root, ct, links).ConfigureAwait(false)).Keys, StringComparer.Ordinal);

    /// <summary>
    /// Every file below a directory as it is now, by path relative to it. Keyed by the names as
    /// listed: two that differ only in case may be two files, and keyed as one, one of them went
    /// unseen. A listing of the same folder later gives the same names back.
    /// </summary>
    internal static async Task<Dictionary<string, FsEntry>> EntriesAsync(
        IFileSystemProvider provider, FsUri root, CancellationToken ct, LinkCount links = LinkCount.Entry)
    {
        var found = new Dictionary<string, FsEntry>(StringComparer.Ordinal);
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
