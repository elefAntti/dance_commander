namespace DanceCommander.Providers.Local;

/// <summary>
/// The commit HEAD points at, read from <c>.git</c> like the branch name is (§6.3): a loose ref
/// file, else <c>packed-refs</c>. Not cached, because it is asked for on a keystroke and moves with
/// every commit, where the branch moves rarely.
/// </summary>
public static class GitHead
{
    /// <summary>The full 40- (or 64-) character hash, or null when it cannot be read — an unborn branch has none.</summary>
    public static string? Commit(string gitDirectory)
    {
        try
        {
            var head = ReadFirstLine(Path.Combine(gitDirectory, "HEAD"));
            if (string.IsNullOrEmpty(head))
                return null;
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
                return IsHash(head) ? head : null;

            var reference = head[4..].Trim();

            // A linked worktree keeps HEAD in its own folder but branches in the main one.
            var common = gitDirectory;
            var commonFile = Path.Combine(gitDirectory, "commondir");
            if (File.Exists(commonFile) && ReadFirstLine(commonFile) is { Length: > 0 } relative)
                common = Path.GetFullPath(Path.Combine(gitDirectory, relative));

            foreach (var folder in new[] { gitDirectory, common }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var loose = Path.Combine(folder, reference.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loose) && ReadFirstLine(loose) is { } hash && IsHash(hash))
                    return hash;
            }

            var packed = Path.Combine(common, "packed-refs");
            if (!File.Exists(packed))
                return null;

            foreach (var line in ReadLines(packed))
            {
                // "<hash> <ref>"; '#' lines are the header and '^' lines are peeled tags.
                if (line.Length > 0 && line[0] is not ('#' or '^') &&
                    line.Split(' ', 2) is [var candidate, var name] &&
                    name.Trim() == reference && IsHash(candidate))
                    return candidate;
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

    private static bool IsHash(string text) =>
        text.Length is 40 or 64 && text.All(Uri.IsHexDigit);

    // Share-all: git rewrites these while we may be reading them.
    private static string? ReadFirstLine(string path) => ReadLines(path).FirstOrDefault()?.Trim();

    private static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}
