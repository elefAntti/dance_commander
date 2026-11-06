namespace DanceCommander.Providers.Local;

/// <summary>
/// Just enough of <c>.git/config</c> to answer "where does this repository live": the remote URL
/// behind <c>${remoteUrl}</c> (§6.6). Read directly for the reason HEAD is (§6.3) — it is a few
/// lines of INI, and a process per listing to ask git would cost more than the answer is worth.
/// </summary>
/// <remarks>
/// Deliberately not a config parser. No includes, no <c>config.worktree</c>, no escapes beyond
/// quotes: a remote URL that needs any of those is one where the token is left unresolved and the
/// command that wanted it is not offered, which is the honest outcome for something we misread.
/// </remarks>
internal static class GitConfig
{
    /// <summary>
    /// The branch's upstream remote if it has one, else <c>origin</c>, else the only remote there
    /// is. Null when the repository has no remote, or several and nothing to choose between them.
    /// </summary>
    public static string? RemoteUrl(string gitDirectory, string? branch)
    {
        try
        {
            var config = Path.Combine(CommonDirectory(gitDirectory), "config");
            if (!File.Exists(config))
                return null;

            var (urls, upstreams) = Read(config);

            // "." as a branch's remote means "this repository" — a local upstream, not a place.
            if (branch is not null &&
                upstreams.TryGetValue(branch, out var tracked) && tracked != "." &&
                urls.TryGetValue(tracked, out var trackedUrl))
            {
                return trackedUrl;
            }

            if (urls.TryGetValue("origin", out var origin))
                return origin;

            return urls.Count == 1 ? urls.Values.Single() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A linked worktree's git directory holds its own HEAD but shares everything else, config
    /// included, with the main repository; <c>commondir</c> says where that is.
    /// </summary>
    private static string CommonDirectory(string gitDirectory)
    {
        var pointer = Path.Combine(gitDirectory, "commondir");
        if (!File.Exists(pointer))
            return gitDirectory;

        var target = File.ReadAllText(pointer).Trim();
        return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(gitDirectory, target));
    }

    private static (Dictionary<string, string> Urls, Dictionary<string, string> Upstreams) Read(string path)
    {
        // Subsection names are case-sensitive in git ("origin" and "Origin" are two remotes); the
        // section and key names around them are not.
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        var upstreams = new Dictionary<string, string>(StringComparer.Ordinal);

        string? section = null;
        string? subsection = null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;

            if (line[0] == '[')
            {
                (section, subsection) = Header(line);
                continue;
            }

            if (subsection is null)
                continue;

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            var key = line[..equals].Trim();
            var value = Unquote(line[(equals + 1)..].Trim());

            // First one wins: a remote with two "url" lines pushes to both but fetches from the
            // first, and the first is what anybody means by "the" URL.
            if (Is(section, "remote") && Is(key, "url"))
                urls.TryAdd(subsection, value);
            else if (Is(section, "branch") && Is(key, "remote"))
                upstreams.TryAdd(subsection, value);
        }

        return (urls, upstreams);
    }

    /// <summary><c>[remote "origin"]</c> → ("remote", "origin"); <c>[core]</c> → ("core", null).</summary>
    private static (string? Section, string? Subsection) Header(string line)
    {
        var close = line.LastIndexOf(']');
        if (close < 0)
            return (null, null);

        var inside = line[1..close].Trim();
        var quote = inside.IndexOf('"');
        if (quote < 0)
            return (inside, null);

        var name = inside[..quote].Trim();
        var sub = inside[(quote + 1)..].TrimEnd('"');
        return (name, sub);
    }

    private static string Unquote(string value)
    {
        // A trailing comment is only a comment outside quotes.
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 0 ? value[1..end] : value[1..];
        }

        var comment = value.IndexOfAny(['#', ';']);
        return (comment >= 0 ? value[..comment] : value).Trim();
    }

    private static bool Is(string? text, string expected) =>
        string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
}
