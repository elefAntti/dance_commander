using System.Collections.Concurrent;
using DanceCommander.Core.Fs;

namespace DanceCommander.Providers.Local;

/// <param name="GitDirectory">Where HEAD actually lives — not the same as the work tree for a
/// worktree or a submodule, and the thing to watch for branch changes.</param>
/// <param name="RemoteUrl">The remote this branch tracks, else origin, else the only one (§6.6).</param>
public sealed record GitInfo(
    string Branch, bool Detached, string WorkTreeRoot, string GitDirectory, string? RemoteUrl = null)
{
    /// <summary>What the badge shows: a branch name, or a short SHA when detached.</summary>
    public string Label => Detached ? $"detached {Branch}" : Branch;
}

public interface IGitService
{
    /// <summary>The repository containing this directory, or null if there is none.</summary>
    GitInfo? Describe(FsUri directory);

    /// <summary>Drops cached answers for a repository, e.g. after its HEAD changed.</summary>
    void Invalidate(string gitDirectory);
}

/// <summary>
/// Branch and remote, read straight from <c>.git</c> (§6.3). A branch name is a forty-byte read of HEAD;
/// taking a native git dependency to learn it would be a poor trade, and per-file status — which
/// genuinely needs one — is deliberately out of scope.
/// </summary>
public sealed class GitService : IGitService
{
    // Bounded, and negative answers are cached too: a tree with no repository in it must not cost
    // a directory walk on every listing.
    private readonly ConcurrentDictionary<string, GitInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private const int CacheLimit = 2000;

    public GitInfo? Describe(FsUri directory)
    {
        if (!directory.IsLocal)
            return null;

        var path = directory.ToLocalPath();
        if (_cache.TryGetValue(path, out var cached))
            return cached;

        var info = Discover(path);

        if (_cache.Count > CacheLimit)
            _cache.Clear();
        _cache[path] = info;

        return info;
    }

    public void Invalidate(string gitDirectory)
    {
        foreach (var (key, value) in _cache)
        {
            if (value is not null &&
                string.Equals(value.GitDirectory, gitDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private static GitInfo? Discover(string startPath)
    {
        try
        {
            var current = new DirectoryInfo(startPath);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, ".git");

                if (Directory.Exists(candidate))
                    return Read(candidate, current.FullName);

                // A worktree or submodule has a .git *file* pointing at the real git directory.
                if (File.Exists(candidate))
                {
                    var redirect = ReadGitDirFile(candidate, current.FullName);
                    return redirect is null ? null : Read(redirect, current.FullName);
                }

                current = current.Parent;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string? ReadGitDirFile(string gitFile, string workTree)
    {
        foreach (var line in File.ReadLines(gitFile))
        {
            if (!line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                continue;

            var target = line[7..].Trim();
            return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(workTree, target));
        }

        return null;
    }

    private static GitInfo? Read(string gitDirectory, string workTree)
    {
        var head = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(head))
            return null;

        // Opened share-all: git rewrites HEAD while we may be reading it.
        using var stream = new FileStream(head, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var text = reader.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(text))
            return null;

        if (text.StartsWith("ref:", StringComparison.Ordinal))
        {
            var reference = text[4..].Trim();
            const string Prefix = "refs/heads/";
            var branch = reference.StartsWith(Prefix, StringComparison.Ordinal)
                ? reference[Prefix.Length..]
                : reference;
            return new GitInfo(
                branch, Detached: false, workTree, gitDirectory, GitConfig.RemoteUrl(gitDirectory, branch));
        }

        // A bare SHA means a detached HEAD; seven characters is what git itself shows.
        return new GitInfo(
            text.Length >= 7 ? text[..7] : text, Detached: true, workTree, gitDirectory,
            GitConfig.RemoteUrl(gitDirectory, branch: null));
    }
}
