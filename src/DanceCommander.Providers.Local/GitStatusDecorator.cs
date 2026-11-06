using DanceCommander.Core.Decorations;
using DanceCommander.Core.Fs;

namespace DanceCommander.Providers.Local;

/// <summary>
/// Marks rows with what git says about them: modified, added, untracked (§6.5).
/// </summary>
/// <remarks>
/// The letters are git's own, so there is nothing to learn, and the colours are the ones every
/// tool that shows this uses. Folders get a mark too, rolled up from what is inside them — a
/// repository where the one changed file is four levels down should not look identical to a clean
/// one, because the whole reason to want this in a file manager is to see that at a glance.
///
/// Running this per listing rather than per row is what makes it affordable: one <c>git status</c>
/// for the directory's repository, cached until something in it changes, whatever the row count.
/// </remarks>
public sealed class GitStatusDecorator(IGitService git, IGitStatusService status) : IRowDecorator
{
    public async ValueTask DecorateAsync(
        IReadOnlyList<IRowDecorationSink> rows, FsUri directory, CancellationToken ct)
    {
        if (!directory.IsLocal || rows.Count == 0)
            return;

        if (git.Describe(directory) is not { } repository)
            return;

        // The last answer first, when git has to be asked again: marks from a moment ago, corrected
        // when it replies, rather than a repository that looks clean for as long as it takes.
        var pending = status.GetAsync(repository.WorkTreeRoot, ct);
        if (!pending.IsCompleted && status.Last(repository.WorkTreeRoot) is { } last)
            Mark(rows, repository.WorkTreeRoot, last, ct);

        // No answer (no git, or a repository too big to wait for) leaves the marks as they were. An
        // empty answer is an answer: a clean tree, which has to clear them.
        var report = await pending.ConfigureAwait(true);
        if (report is null)
            return;

        Mark(rows, repository.WorkTreeRoot, report, ct);
    }

    private static void Mark(IReadOnlyList<IRowDecorationSink> rows, string workTreeRoot, GitStatus report, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (!row.Entry.Uri.IsLocal)
                continue;

            var relative = Relative(workTreeRoot, row.Entry.Uri.ToLocalPath());
            if (relative is null)
                continue;

            // Assigned even when there is nothing to say. A file that is clean again is simply absent
            // from git's report, so a mark only ever set, never cleared, outlived the commit.
            row.Marker = Describe(report.For(relative));
        }
    }

    /// <summary>
    /// A path below the work tree root, in git's spelling: forward slashes, no leading separator.
    /// Null when the row is not inside this repository at all, which a flattened view can produce.
    /// </summary>
    internal static string? Relative(string workTreeRoot, string path)
    {
        var root = workTreeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = path[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rest.Length == 0 ? null : rest.Replace('\\', '/');
    }

    /// <summary>
    /// The letter, the colour and the words for a state. Colours are the semantic theme keys
    /// rather than literals, so the marks follow the palette like everything else.
    /// </summary>
    internal static RowMarker? Describe(GitState state) => state switch
    {
        GitState.Modified => new RowMarker("M", "GitModifiedBrush", "modified"),
        GitState.Added => new RowMarker("A", "GitAddedBrush", "added"),
        GitState.Deleted => new RowMarker("D", "GitDeletedBrush", "deleted"),
        GitState.Renamed => new RowMarker("R", "GitAddedBrush", "renamed"),
        GitState.Conflicted => new RowMarker("!", "GitConflictBrush", "conflicted"),
        GitState.Untracked => new RowMarker("?", "GitUntrackedBrush", "untracked"),
        // Ignored gets no letter. It is the normal state of most of a build tree, and a column of
        // marks saying "this is fine" would drown the three rows that are not.
        _ => null,
    };
}
