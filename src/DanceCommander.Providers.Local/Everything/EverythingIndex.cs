using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Matching;

namespace DanceCommander.Providers.Local.Everything;

/// <summary>
/// Everything (voidtools) as the index behind a search of a local folder, when it is running and
/// has the whole drive (<see cref="EverythingSettings"/>). It only proposes: <see cref="IndexedSearch"/>
/// decides what is a row.
/// </summary>
/// <remarks>
/// <para>Measured on this machine (IMPLEMENTATION_LOG.md, 2026-11-09): a search of 1.75 million
/// names in 40–90 ms, where the walk of the home folder takes three seconds.</para>
///
/// <para>Our words go to it quoted, so nothing in them is its syntax: <c>!</c>, <c>|</c> and
/// <c>ext:</c> are text. A word with a character no name can hold can match nothing, by either
/// rule, so it is not asked at all. A glob with brackets or braces, which our globs read and its
/// don't, is left to the walk: its answer would be stricter than ours.</para>
/// </remarks>
public sealed class EverythingIndex : ILocalSearchIndex
{
    private static readonly char[] NotInAName = ['\\', '/', ':', '"', '<', '>', '|'];
    private static readonly char[] OnlyOurGlobs = ['[', ']', '{', '}'];

    private readonly EverythingSettings _settings = new();

    public string Name => "Everything";

    public async Task<IReadOnlyList<IndexedPath>?> CandidatesAsync(
        FsUri root, IReadOnlyList<string> terms, int limit, CancellationToken ct)
    {
        if (!root.IsLocal || root.IsView)
            return null;

        // Below U+0020 only: Windows refuses those in names, and allows U+007F to U+009F.
        if (terms.Any(term => term.IndexOfAny(NotInAName) >= 0 || term.Any(c => c < ' ')))
            return [];

        if (terms.Any(term => NameQuery.IsGlob(term) && term.IndexOfAny(OnlyOurGlobs) >= 0))
            return null;

        var folder = root.Path.Replace('/', '\\').TrimEnd('\\') + "\\";
        if (EverythingIpc.ProcessId() is not { } process || !_settings.Covers(process, folder) || ThroughALink(root))
            return null;

        var scope = $"path:\"{folder}\"";
        var search = string.Join(' ', terms.Select(term => $"\"{term}\"").Prepend(scope));
        var reply = await EverythingIpc.Shared.QueryAsync(search, (uint)limit, ct).ConfigureAwait(false);

        // More than the limit, or fewer sent than it says it has: not an answer to use whole.
        if (reply is null || reply.Total > limit || reply.Paths.Count != reply.Total)
            return null;

        // Nothing found in a folder that has something in it may be a folder it doesn't have at all:
        // a volume it can't read, still being scanned. One more question tells them apart.
        if (reply.Total == 0 && HasAnything(root)
            && await EverythingIpc.Shared.QueryAsync(scope, 1, ct).ConfigureAwait(false) is not { Total: > 0 })
            return null;

        return reply.Paths;
    }

    /// <summary>
    /// Whether the folder is reached through a junction, a link or a mounted volume: Everything has
    /// what is there only under its real place, so asked about this one it would find nothing
    /// (review, 2026-11-10). The walk goes through it.
    /// </summary>
    private static bool ThroughALink(FsUri root)
    {
        try
        {
            for (var folder = root; folder is { IsRoot: false }; folder = folder.Parent)
            {
                if (File.GetAttributes(folder.ToLocalPath()).HasFlag(FileAttributes.ReparsePoint))
                    return true;
            }

            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true; // couldn't tell, so the walk
        }
    }

    private static bool HasAnything(FsUri folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder.ToLocalPath(), "*", new EnumerationOptions { AttributesToSkip = 0 }).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
