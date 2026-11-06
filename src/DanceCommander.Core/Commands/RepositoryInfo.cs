using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Commands;

/// <summary>
/// The git work tree a location sits in: what the location tokens (<c>${repoRoot}</c>,
/// <c>${branch}</c>, <c>${remoteUrl}</c>) answer from (§6.6).
/// </summary>
/// <remarks>
/// Facts about the <em>location</em>, not about any entry in it — which is why they live on the
/// command context beside <see cref="ICommandContext.InRepository"/> rather than in the matcher.
/// Core knows nothing about git; the pane fills this in from what the branch decoration already
/// read.
/// </remarks>
/// <param name="Root">The top of the work tree. For a linked worktree, that worktree's own root.</param>
/// <param name="Branch">The checked-out branch, or null when HEAD is detached — a SHA is not a
/// branch, and a script parsing a ticket key out of one would be parsing noise.</param>
/// <param name="RemoteUrl">The remote the branch tracks, else <c>origin</c>, else the only remote;
/// null when there is none. Exactly as written in the config, so an SSH remote stays an SSH remote.</param>
/// <param name="Conflicts">The files git reports as unmerged, relative to <see cref="Root"/> with '/'
/// separators, as of the last <c>git status</c> the rows were marked from; null before there was
/// one. What a tool with <c>inConflict</c> asks, so a merge tool is offered only when there is
/// something to merge.</param>
public sealed record RepositoryInfo(
    FsUri Root, string? Branch, string? RemoteUrl, IReadOnlyList<string>? Conflicts = null)
{
    public bool HasConflicts => Conflicts is { Count: > 0 };

    /// <summary>Whether this entry is an unmerged file, or a folder with one somewhere below it.</summary>
    public bool IsConflicted(FsUri entry)
    {
        if (!HasConflicts || !entry.IsLocal || !Root.IsLocal)
            return false;

        var relative = Path.GetRelativePath(Root.ToLocalPath(), entry.ToLocalPath()).Replace('\\', '/');
        if (relative == ".")
            return true; // the root holds them all
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return false; // outside it, on another drive included

        // Windows names are case-insensitive whatever git's index says about them.
        return Conflicts!.Any(path =>
            path.Equals(relative, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase));
    }
}
