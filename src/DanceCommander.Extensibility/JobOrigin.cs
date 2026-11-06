using DanceCommander.Core.Commands;

namespace DanceCommander.Extensibility;

/// <summary>Names where a job runs, short enough to sit beside its title in the strip.</summary>
public static class JobOrigin
{
    /// <summary>
    /// Inside a work tree, the work tree's folder and its branch — <c>orbit-worktree-2 · ORB-11026</c>
    /// — since that is what tells one checkout's build from another's; the folder name alone is
    /// often the repository's name in every one of them, and the branch alone can repeat. Anywhere
    /// else, the folder it runs in. Null when there is no folder to name.
    /// </summary>
    public static (string Label, string Path)? Describe(string? workingDirectory, RepositoryInfo? repository)
    {
        var path = workingDirectory?.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(path))
            return null;

        if (repository is { Root.IsLocal: true } repo && IsWithin(path, repo.Root.ToLocalPath()))
        {
            var root = repo.Root.ToLocalPath().TrimEnd('\\', '/');
            var name = Path.GetFileName(root);
            return (repo.Branch is { } branch ? $"{name} · {branch}" : name, path);
        }

        var folder = Path.GetFileName(path);
        return (string.IsNullOrEmpty(folder) ? path : folder, path);
    }

    private static bool IsWithin(string path, string root)
    {
        root = root.TrimEnd('\\', '/');
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + '/', StringComparison.OrdinalIgnoreCase);
    }
}
