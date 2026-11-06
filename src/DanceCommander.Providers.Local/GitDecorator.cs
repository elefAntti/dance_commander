using DanceCommander.Core.Decorations;
using DanceCommander.Core.Fs;

namespace DanceCommander.Providers.Local;

/// <summary>
/// Badges folders that are repository roots with their checked-out branch, so a folder full of
/// repositories shows every branch at a glance (§6.3).
/// </summary>
public sealed class GitDecorator(IGitService git) : IRowDecorator
{
    /// <summary>The most folders one listing probes for a repository.</summary>
    public const int MaxProbed = 2000;

    public async ValueTask DecorateAsync(
        IReadOnlyList<IRowDecorationSink> rows, FsUri directory, CancellationToken ct)
    {
        if (!directory.IsLocal)
            return;

        var candidates = rows
            .Where(r => r.Entry.IsDirectory && r.Entry.Uri.IsLocal)
            .ToList();

        // A folder of repositories is dozens of folders, not thousands. Past this many, the probe
        // would be seconds of disk work — two misses per folder across all 26,000 in WinSxS — for
        // badges that would not be there, so a folder that large goes without them.
        if (candidates.Count is 0 or > MaxProbed)
            return;

        // Probing each folder means a handful of file-exists calls; off the UI thread regardless,
        // because a directory of several hundred repositories should not stutter the grid.
        var labels = await Task.Run(() =>
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var path = row.Entry.Uri.ToLocalPath();
                // One call for either shape: a .git folder, or the .git file of a linked worktree.
                if (!Path.Exists(Path.Combine(path, ".git")))
                    continue;

                if (git.Describe(row.Entry.Uri) is { } info &&
                    string.Equals(info.WorkTreeRoot, path, StringComparison.OrdinalIgnoreCase))
                {
                    found[row.Entry.Name] = info.Label;
                }
            }

            return found;
        }, ct).ConfigureAwait(true);

        foreach (var row in candidates)
        {
            if (labels.TryGetValue(row.Entry.Name, out var label))
                row.Badge = label;
        }
    }
}
