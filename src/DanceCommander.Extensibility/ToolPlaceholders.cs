using System.Text.RegularExpressions;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;

namespace DanceCommander.Extensibility;

public sealed record ToolTarget(
    FsUri Directory,
    IReadOnlyList<FsEntry> Selection,
    FsEntry? Focused,
    FsUri? OtherPaneDirectory,
    RepositoryInfo? Repository = null,
    IReadOnlyList<FsEntry>? OtherPaneMarked = null,
    bool OtherPaneUnconfirmed = false);

/// <summary>
/// Expands <c>${…}</c> placeholders into an <em>argument vector</em>, never into a command string
/// (§6.6). Nothing a file is called can therefore change the shape of the command: a file named
/// <c>a" &amp; del *.*</c> is one argument, because it is handed to the process as one array slot.
/// </summary>
public static partial class ToolPlaceholders
{
    /// <summary>
    /// The placeholders that answer a question about the <em>location</em> rather than the entry
    /// (§6.6). Unlike the entry ones, these can have no answer — there is no branch outside a
    /// repository — and a command using one is then not offered at all rather than run with an
    /// empty argument, which would fail somewhere confusing inside the child process.
    /// </summary>
    public static readonly IReadOnlyList<string> LocationTokens = ["repoRoot", "branch", "remoteUrl", "distro"];

    /// <summary>
    /// True when every location token the templates use has a value here. What a command's
    /// applicability check asks, so an unanswerable tool simply does not appear.
    /// </summary>
    public static bool CanResolve(IEnumerable<string?> templates, RepositoryInfo? repository, FsUri? directory = null) =>
        Unresolved(templates, repository, directory) is null;

    /// <summary>The first location token in the templates that has no value here, or null.</summary>
    public static string? Unresolved(IEnumerable<string?> templates, RepositoryInfo? repository, FsUri? directory = null)
    {
        foreach (var template in templates)
        {
            if (string.IsNullOrEmpty(template))
                continue;

            foreach (Match match in PlaceholderPattern().Matches(template))
            {
                var token = Base(match.Groups[1].Value);
                if (IsLocationToken(token) && Location(token, repository, directory) is null)
                    return token;
            }
        }

        return null;
    }

    /// <summary>The placeholders that read the selected rows rather than the folder (§6.18).</summary>
    private static readonly string[] EntryTokens = ["path", "name", "stem", "ext", "selection", "uri"];

    /// <summary>Every placeholder there is, besides <c>${env:…}</c> and <c>${prompt:…}</c>.</summary>
    public static readonly IReadOnlyList<string> Known =
        [.. EntryTokens, "dir", "otherPaneDir", "otherPaneSelection", .. LocationTokens];

    private static bool IsKnown(string token) =>
        token.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
        token.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase) ||
        Known.Contains(token, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The first placeholder in the templates that isn't one, or null. A misspelt one used to
    /// expand to nothing: <c>rm -rf ${dir}\${nmae}</c> was <c>rm -rf</c> of the folder itself.
    /// </summary>
    /// <param name="nearOnly">
    /// Only those within a typo of a real one. A typed line keeps its shell's own <c>${HOME}</c>,
    /// which the shell expands; a near miss like <c>${selction}</c> is still caught there.
    /// </param>
    public static string? UnknownPlaceholder(IEnumerable<string?> templates, bool nearOnly = false)
    {
        foreach (var template in templates)
        {
            if (string.IsNullOrEmpty(template))
                continue;

            foreach (Match match in PlaceholderPattern().Matches(template))
            {
                var token = Base(match.Groups[1].Value);
                if (nearOnly ? !IsKnown(token) && !InCapitals(token) && Nearest(token) is not null : !IsKnown(token))
                    return token;
            }
        }

        return null;
    }

    /// <summary>
    /// On a typed line, a <c>${…}</c> that is the shell's own and goes to it as typed: not one of
    /// ours, or written in capitals as environment variables are, so <c>${PATH}</c> is the shell's
    /// and not the cursor's file. Never one with <c>${</c> inside: in <c>${x:-${name}}</c> our
    /// <c>${name}</c> would reach the shell unfilled, and the shell's own (unset) name is nothing.
    /// </summary>
    private static bool IsShells(string token) =>
        !token.Contains("${", StringComparison.Ordinal) && (!IsKnown(token) || InCapitals(token));

    private static bool InCapitals(string token) =>
        token.Length > 1 && token.Any(char.IsLetter) && !token.Any(char.IsLower);

    /// <summary>A shell's ${…} on a typed line with a placeholder of ours inside it, or null.</summary>
    private static string? Nested(string text)
    {
        foreach (Match match in PlaceholderPattern().Matches(text))
        {
            if (match.Groups[1].Value.Contains("${", StringComparison.Ordinal))
                return match.Value;
        }

        return null;
    }

    /// <summary>The real placeholder a misspelt one is a typo away from, or null.</summary>
    public static string? Nearest(string token)
    {
        var lower = token.ToLowerInvariant();
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var known in Known)
        {
            var distance = Distance(lower, known.ToLowerInvariant());
            if (distance < bestDistance)
                (best, bestDistance) = (known, distance);
        }

        // One slip in a short name, two in a longer one: ${id} is somebody's shell variable, not ${dir}.
        return bestDistance <= (best!.Length <= 4 ? 1 : 2) ? best : null;
    }

    /// <summary>Edits between two names, a swapped pair of letters counting as one: nmae is one slip from name.</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            d[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }

        return d[a.Length, b.Length];
    }

    public static void ReportUnknownPlaceholder(string token, IDecisionHost decisions) =>
        decisions.Report(new Notice(
            $"${{{token}}} isn't a placeholder, so nothing was run.",
            NoticeSeverity.Warning,
            (Nearest(token) is { } near ? $"${{{near}}} is the closest one. " : string.Empty) +
            $"The placeholders are {string.Join(", ", Known.Select(k => "${" + k + "}"))}, " +
            "${env:NAME} for an environment variable and ${prompt:Question} to ask."));

    /// <summary>True when any template reads the selection, which makes the tool about the selection.</summary>
    public static bool ReadsEntries(IEnumerable<string?> templates) =>
        templates.Any(template => !string.IsNullOrEmpty(template) &&
            PlaceholderPattern().Matches(template).Any(match =>
                EntryTokens.Contains(Base(match.Groups[1].Value), StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// True when any template reads the other pane's folder. Such a tool needs the other pane on
    /// screen, and somewhere its program can open: with one pane, <c>${otherPaneDir}\${name}</c>
    /// came out as <c>\name</c>, the root of the drive.
    /// </summary>
    public static bool ReadsOtherPane(IEnumerable<string?> templates) =>
        templates.Any(template => !string.IsNullOrEmpty(template) &&
            PlaceholderPattern().Matches(template).Any(match =>
                Base(match.Groups[1].Value) is var token &&
                (token.Equals("otherPaneDir", StringComparison.OrdinalIgnoreCase) ||
                 token.Equals("otherPaneSelection", StringComparison.OrdinalIgnoreCase))));

    // ---- filters ------------------------------------------------------------------------------

    /// <summary>
    /// What may follow a <c>|</c> inside a placeholder, to reshape its value for a program with
    /// its own ideas about spelling. <c>${repoRoot|lowerDrive}</c> is <c>c:\src\app</c>: the drive
    /// letter as VS Code writes it, which is how a dev container is labelled with the folder it was
    /// opened from — so <c>devcontainer exec --id-label</c> can find the container for whichever
    /// worktree you are standing in. Named for what it does, not for who wants it: the application
    /// knows about drive letters, not about VS Code.
    /// <c>${path|wsl}</c> is the path as Linux in WSL spells it (<see cref="WslPaths.ToLinux"/>):
    /// <c>\\wsl.localhost\Debian\home\x</c> is <c>/home/x</c>, <c>C:\work\a.sh</c> is
    /// <c>/mnt/c/work/a.sh</c>; a share WSL can't reach is left as it is. For an argument list: on
    /// a line Git Bash runs, a value starting with / is rewritten under Git's own folder before a
    /// Windows program sees it (AUDIT.md §2 kind 81).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Func<string, string>> Filters =
        new Dictionary<string, Func<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["lowerDrive"] = value =>
                value.Length >= 2 && value[1] == ':' && char.IsAsciiLetter(value[0])
                    ? char.ToLowerInvariant(value[0]) + value[1..]
                    : value,
            ["wsl"] = value => WslPaths.ToLinux(value) ?? value,
        };

    /// <summary>The placeholder's name without its filters: <c>repoRoot|lowerDrive</c> → <c>repoRoot</c>.</summary>
    private static string Base(string token)
    {
        var bar = token.IndexOf('|');
        return bar < 0 ? token : token[..bar].Trim();
    }

    /// <summary>
    /// The first filter in the templates that does not exist, or null. A misspelt filter is caught
    /// when tools.json loads rather than quietly passing the value through unchanged — which would
    /// be the worst kind of failure here, a lookup that almost works.
    /// </summary>
    public static string? UnknownFilter(IEnumerable<string?> templates)
    {
        foreach (var template in templates)
        {
            if (string.IsNullOrEmpty(template))
                continue;

            foreach (Match match in PlaceholderPattern().Matches(template))
            {
                foreach (var filter in match.Groups[1].Value.Split('|').Skip(1).Select(f => f.Trim()))
                {
                    if (!Filters.ContainsKey(filter))
                        return filter;
                }
            }
        }

        return null;
    }

    private static string Filtered(string token, string value)
    {
        foreach (var filter in token.Split('|').Skip(1).Select(f => f.Trim()))
        {
            if (Filters.TryGetValue(filter, out var apply))
                value = apply(value);
        }

        return value;
    }

    private static void ReportUnknownFilter(string filter, IDecisionHost decisions) =>
        decisions.Report(new Notice(
            $"“{filter}” is not a placeholder filter.",
            NoticeSeverity.Warning,
            $"The filters there are: {string.Join(", ", Filters.Keys)}."));

    private static bool IsLocationToken(string token) =>
        LocationTokens.Contains(token, StringComparer.OrdinalIgnoreCase);

    private static string? Location(string token, RepositoryInfo? repository, FsUri? directory) =>
        token.ToLowerInvariant() switch
        {
            "reporoot" => repository is null ? null : Render(repository.Root),
            "branch" => repository?.Branch,
            "remoteurl" => repository?.RemoteUrl,
            "distro" => Distribution(directory),
            _ => null,
        };

    /// <summary>
    /// The WSL distribution a folder is in, or the one <c>wsl.exe</c> opens by default anywhere
    /// else: so <c>wsl -d ${distro} --cd ${dir|wsl}</c> opens a distribution's own folder in that
    /// distribution, and any other folder where plain <c>wsl --cd</c> did. No value on a server or
    /// in an archive, where this computer's Linux has nothing to do with the folder.
    /// </summary>
    private static string? Distribution(FsUri? directory) =>
        directory?.Anchor switch
        {
            { IsLocal: true } local when WslPaths.TrySplit(local.ToLocalPath(), out var distribution, out _) => distribution,
            null or { IsLocal: true } => WslPaths.DefaultDistribution(),
            _ => null,
        };

    /// <summary>
    /// Returns null if the user cancelled a ${prompt:…}, or if a location token has no value here
    /// — which is reported, since unlike a cancel it is not something the user just chose.
    /// </summary>
    /// <param name="fromOutside">
    /// Filled, one per argument, with whether it holds a value from outside (a name, a path, a
    /// branch) rather than only the user's own text, ${env:…} and ${prompt:…}: what a program that
    /// re-reads its arguments as a command line must check (wsl.exe without --exec).
    /// </param>
    public static async Task<List<string>?> ExpandAsync(
        IReadOnlyList<string> template,
        ToolTarget target,
        IDecisionHost decisions,
        CancellationToken ct,
        List<bool>? fromOutside = null)
    {
        if (Unresolved(template, target.Repository, target.Directory) is { } missing)
        {
            ReportUnresolved(missing, target.Repository, decisions);
            return null;
        }

        if (UnknownFilter(template) is { } unknown)
        {
            ReportUnknownFilter(unknown, decisions);
            return null;
        }

        if (UnknownPlaceholder(template) is { } misspelt)
        {
            ReportUnknownPlaceholder(misspelt, decisions);
            return null;
        }

        if (!HasOtherPaneMarks(template, target, decisions))
            return null;

        var result = new List<string>(template.Count);
        var prompted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var argument in template)
        {
            // Whole-argument ${selection} becomes one argv slot per selected item, which is the
            // only way a multi-file tool can receive names that contain spaces safely.
            if (string.Equals(argument.Trim(), "${selection}", StringComparison.OrdinalIgnoreCase))
            {
                result.AddRange(target.Selection.Select(e => Render(e.Uri)));
                fromOutside?.AddRange(target.Selection.Select(_ => true));
                continue;
            }

            // The other pane's marks the same way, one slot each.
            if (string.Equals(argument.Trim(), "${otherPaneSelection}", StringComparison.OrdinalIgnoreCase))
            {
                var marked = target.OtherPaneMarked ?? [];
                result.AddRange(marked.Select(e => Render(e.Uri)));
                fromOutside?.AddRange(marked.Select(_ => true));
                continue;
            }

            var expanded = await ExpandOneAsync(argument, target, decisions, prompted, ct);
            if (expanded is null)
                return null;

            result.Add(expanded);
            fromOutside?.Add(PlaceholderPattern().Matches(argument).Any(match => IsFromOutside(Base(match.Groups[1].Value))));
        }

        return result;
    }

    /// <summary>One value as one word for this shell, outside any quotes of the user's.</summary>
    public static string Quote(string value, ShellQuoting shell) => new ShellQuoteState(shell).Quote(value);

    public static async Task<string?> ExpandOneAsync(
        string text,
        ToolTarget target,
        IDecisionHost decisions,
        Dictionary<string, string>? prompted = null,
        CancellationToken ct = default,
        ShellQuoting shell = ShellQuoting.None,
        bool typedLine = false)
    {
        prompted ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Checked up front, not token by token: a command that cannot be completed must not ask a
        // ${prompt:…} question first and then fail anyway. Menus never get here -- they check
        // CanResolve -- but a typed command line has no applicability check to lean on.
        if (Unresolved([text], target.Repository, target.Directory) is { } missing)
        {
            ReportUnresolved(missing, target.Repository, decisions);
            return null;
        }

        if (UnknownFilter([text]) is { } unknown)
        {
            ReportUnknownFilter(unknown, decisions);
            return null;
        }

        // On a typed line, ${HOME} is the shell's and goes to it as typed; ${nmae} is a slip.
        if (UnknownPlaceholder([text], nearOnly: typedLine) is { } misspelt)
        {
            ReportUnknownPlaceholder(misspelt, decisions);
            return null;
        }

        if (typedLine && Nested(text) is { } nested)
        {
            decisions.Report(new Notice(
                $"{nested} has a placeholder inside the shell's own braces, so nothing was run.",
                NoticeSeverity.Warning,
                "It would reach the shell unfilled. Put the placeholder outside the braces."));
            return null;
        }

        if (!HasOtherPaneMarks([text], target, decisions))
            return null;

        if (typedLine && !OtherPaneIsHere(text, target, decisions))
            return null;

        var cancelled = false;
        string? reread = null;
        string? forLinux = null;
        var focused = target.Focused ?? target.Selection.FirstOrDefault();
        List<Range>? inserted = shell == ShellQuoting.Cmd ? [] : null;
        // In cmd and PowerShell a bare bash is System32's, which is WSL's; in Git Bash it is Git's.
        var runsWsl = shell != ShellQuoting.None && (RunsWsl().IsMatch(text) ||
            (shell is ShellQuoting.Cmd or ShellQuoting.PowerShell && RunsBash().IsMatch(text)));

        var givesALine = runsWsl && ShellLineFlag().IsMatch(text);

        var result = await ReplaceAsync(text, shell, async (token, inUsersQuotes) =>
        {
            if (typedLine && IsShells(token))
                return null; // the shell's own variable, left for it
            var words = await ResolveAsync(token);
            if (shell == ShellQuoting.Cmd && reread is null && IsFromOutside(token))
                reread = words.FirstOrDefault(word => word.IndexOfAny(CmdUnquotable) >= 0);
            if (runsWsl && forLinux is null && IsFromOutside(token))
                forLinux = words.FirstOrDefault(word => LinuxShellActsOn(word, inUsersQuotes && givesALine));
            return words;
        }, inserted);

        // A line that runs wsl is quoted right for this shell, which then starts wsl.exe, which
        // hands what follows to the Linux shell as a line: a name is read twice, and the second
        // time unquoted. Only a name with what that shell acts on is refused; anything else
        // reaches Linux as it did (AUDIT.md §2 kind 26; the tools.json side is WslArguments).
        if (forLinux is not null)
        {
            decisions.Report(new Notice(
                $"“{forLinux}” can't go on a line that runs wsl.",
                NoticeSeverity.Warning,
                "wsl hands the line on to the Linux shell, which would read part of the name as a command. " +
                "Rename it, or run it from a tool in tools.json with --exec."));
            return null;
        }

        // cmd pairs %s across the whole line, so a value is judged where it landed: a branch fix%
        // beside a file USERPROFILE% made %USERPROFILE% out of two values that each had one.
        if (reread is null && inserted is { Count: > 0 } && CmdExpandsInto(result, inserted) is { } expanded)
            reread = expanded;

        if (reread is not null)
        {
            decisions.Report(new Notice(
                $"“{reread}” can't go on a cmd line.",
                NoticeSeverity.Warning,
                "cmd reads a quote, a line break or a %variable% in it whatever the quoting, so the line would " +
                "run something else. Ctrl+E steps the line to bash or PowerShell, which can quote it."));
            return null;
        }

        return cancelled ? null : result;

        async Task<string[]> ResolveAsync(string token)
        {
            if (token.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
                return [Environment.GetEnvironmentVariable(token[4..]) ?? string.Empty];

            if (token.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase))
            {
                var label = token[7..];
                if (prompted.TryGetValue(label, out var remembered))
                    return [remembered];

                var answer = await decisions.AskTextAsync(new TextRequest(label, ConfirmLabel: "Run"), ct);
                if (answer is null)
                {
                    cancelled = true;
                    return [string.Empty];
                }

                prompted[label] = answer;
                return [answer];
            }

            if (IsLocationToken(token))
                return [Location(token, target.Repository, target.Directory)!];

            return token.ToLowerInvariant() switch
            {
                "path" => [focused is null ? string.Empty : Render(focused.Uri, shell)],
                "dir" => [Render(target.Directory, shell)],
                "name" => [focused?.Name ?? string.Empty],
                "stem" => [focused is null ? string.Empty : Path.GetFileNameWithoutExtension(focused.Name)],
                "ext" => [focused?.Extension ?? string.Empty],
                "uri" => [focused?.Uri.Text ?? target.Directory.Text],
                "otherpanedir" => [target.OtherPaneDirectory is { } other ? Render(other, shell) : string.Empty],
                // One word per row, so that at a shell prompt each is quoted on its own.
                "selection" => target.Selection.Select(e => Render(e.Uri, shell)).ToArray(),
                "otherpaneselection" => (target.OtherPaneMarked ?? []).Select(e => Render(e.Uri, shell)).ToArray(),
                _ => [string.Empty],
            };
        }
    }

    /// <summary>
    /// The user's own ${env:…} and ${prompt:…} are the user's shell syntax; everything else is a
    /// name, a path, a branch or a URL that came from somewhere else.
    /// </summary>
    private static bool IsFromOutside(string token) =>
        !token.StartsWith("env:", StringComparison.OrdinalIgnoreCase) &&
        !token.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What cmd reads inside quotes with no escape there: a <c>"</c> ends them (a server's name, a
    /// branch or a remote URL can hold one, and the rest was cmd syntax, <c>&amp;</c> and all), a
    /// line break ends the command, and a NUL ends the whole line.
    /// </summary>
    private static readonly char[] CmdUnquotable = ['"', '\r', '\n', '\0'];

    /// <summary>
    /// What makes the Linux shell run something, lose its place, or name other files, in a value
    /// it reads unquoted. Not a space or a backslash, which only split or drop: <c>wsl --cd ${dir}</c>
    /// on <c>C:\Program Files</c> is wsl's own option and keeps working.
    /// </summary>
    private static readonly char[] LinuxShellReads =
        ['$', '`', ';', '&', '|', '<', '>', '(', ')', '\'', '"', '\r', '\n', '\0', '[', ']', '*', '?'];

    /// <summary>
    /// Whether the Linux shell behind wsl would read <paramref name="value"/> as more than its
    /// name (review, 2026-11-12). A leading <c>~</c> is a home folder: a folder called <c>~</c>,
    /// a common accident, in <c>wsl rm -rf ${name}</c> was the Linux home. A leading <c>#</c>
    /// comments the rest out, <c>=</c> and <c>%</c> are zsh's and fish's. Braces only when they
    /// expand, so a {GUID} folder passes. Inside the user's own quotes on a line with a -c, the
    /// value goes bare into a shell's line, and that shell splits at a space: in
    /// <c>wsl bash -c "rm -f ${name}"</c>, "notes important.txt" was two files to rm. Quoted for
    /// wsl's own <c>--cd "${dir}"</c>, a space is still only a space.
    /// </summary>
    private static bool LinuxShellActsOn(string value, bool inAShellsLine) =>
        value.IndexOfAny(LinuxShellReads) >= 0 ||
        (value.Length > 0 && value[0] is '~' or '#' or '=' or '%') ||
        (value.Contains('{') && (value.Contains(',') || value.Contains("..", StringComparison.Ordinal))) ||
        (inAShellsLine && value.Any(char.IsWhiteSpace));

    /// <summary>A -c among the line's words, alone or bundled (-lc): a shell given a line to run.</summary>
    [GeneratedRegex(@"(^|\s)-[a-zA-Z]*c(?=\s|$)")]
    private static partial Regex ShellLineFlag();

    /// <summary>The line runs wsl.exe (or wslg), as a word of its own, with or without a path.</summary>
    [GeneratedRegex(@"(^|[\s\\/""'&|;({])wslg?(\.exe)?(?=[\s""';&|)}>]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex RunsWsl();

    /// <summary>The line runs bash, as a word of its own: WSL's, in cmd and PowerShell.</summary>
    [GeneratedRegex(@"(^|[\s\\/""'&|;({])bash(\.exe)?(?=[\s""';&|)}>]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex RunsBash();

    /// <summary>
    /// Whether cmd would read a value as anything but itself, alone on a line: see
    /// <see cref="CmdUnquotable"/> and <see cref="CmdExpands"/>.
    /// </summary>
    public static bool CmdRereads(string value) =>
        value.IndexOfAny(CmdUnquotable) >= 0 || CmdExpandsInto(value, [0..value.Length]) is not null;

    /// <summary>
    /// The first %NAME% in the line that would expand and touches a value put in from outside.
    /// Each pair of neighbouring %s, as cmd may pair them: an unset name leaves the second % to
    /// start the next one.
    /// </summary>
    private static string? CmdExpandsInto(string line, IReadOnlyList<Range> inserted)
    {
        var start = line.IndexOf('%');
        while (start >= 0)
        {
            var end = line.IndexOf('%', start + 1);
            if (end < 0)
                return null;
            if (inserted.Any(range => range.Start.Value <= end && start < range.End.Value) &&
                CmdExpands(line[(start + 1)..end]))
            {
                return line[start..(end + 1)];
            }
            start = end;
        }

        return null;
    }

    /// <summary>
    /// Whether %NAME% expands at a cmd /c line: a variable that is set (so
    /// <c>report%20final%20.pdf</c> passes), or one of cmd's own, which are in no environment
    /// block: <c>%CD%</c> as a folder's name in <c>rd /s /q ${name}</c> was the folder being
    /// browsed (review, 2026-10-28). <c>%NAME:~0,5%</c> and <c>%NAME:a=b%</c> read NAME; the
    /// hidden <c>=C:</c> ones keep their colon.
    /// </summary>
    internal static bool CmdExpands(string name)
    {
        if (name.Length == 0)
            return false;
        if (name[0] == '=' || CmdDynamicVariables.Contains(name) || Environment.GetEnvironmentVariable(name) is not null)
            return true;
        var colon = name.IndexOf(':');
        return colon > 0 && CmdExpands(name[..colon]);
    }

    private static readonly HashSet<string> CmdDynamicVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "CD", "__CD__", "__APPDIR__", "DATE", "TIME", "RANDOM", "ERRORLEVEL", "CMDEXTVERSION",
        "CMDCMDLINE", "HIGHESTNUMANODENUMBER",
    };

    /// <summary>
    /// ${otherPaneSelection} is what is marked there, never the cursor's row standing in: the
    /// cursor of the pane you aren't in sits wherever it was left. Nothing marked is said, and
    /// nothing runs.
    /// </summary>
    private static bool HasOtherPaneMarks(IEnumerable<string?> templates, ToolTarget target, IDecisionHost decisions)
    {
        var reads = templates.Any(template => !string.IsNullOrEmpty(template) &&
            PlaceholderPattern().Matches(template).Any(match =>
                Base(match.Groups[1].Value).Equals("otherPaneSelection", StringComparison.OrdinalIgnoreCase)));
        if (!reads)
            return true;

        if (target.OtherPaneDirectory is null)
        {
            decisions.Report(new Notice(
                "The other pane isn't on screen, so ${otherPaneSelection} has nothing to give.",
                NoticeSeverity.Warning,
                "Show both panes (Ctrl+D), mark files in the other one, then run it again."));
            return false;
        }

        // Rows remembered from the last visit to a server may not be what is there now: a name
        // marked as a file may be a folder by now (AUDIT.md §2 kind 50).
        if (target.OtherPaneUnconfirmed)
        {
            decisions.Report(new Notice(
                "The other pane is still checking its folder, so its marks weren't used.",
                NoticeSeverity.Warning,
                "Run it again once the other pane has listed."));
            return false;
        }

        if (target.OtherPaneMarked is { Count: > 0 })
            return true;

        decisions.Report(new Notice(
            "Nothing is marked in the other pane.",
            NoticeSeverity.Warning,
            "${otherPaneSelection} is the files marked there. Mark them with Insert or Space, then run it again."));
        return false;
    }

    /// <summary>
    /// Whether the other pane's paths mean the same files where a typed line runs. A line runs
    /// where the active pane is, and a server's path is just /var/www/x: on another server, or
    /// read by Git Bash here, it names other files, or a folder under Git's own (review, 2026-11-20).
    /// </summary>
    private static bool OtherPaneIsHere(string text, ToolTarget target, IDecisionHost decisions)
    {
        var reads = PlaceholderPattern().Matches(text).Any(match =>
            Base(match.Groups[1].Value) is var token &&
            (token.Equals("otherPaneSelection", StringComparison.OrdinalIgnoreCase) ||
             token.Equals("otherPaneDir", StringComparison.OrdinalIgnoreCase)));
        if (!reads)
            return true;

        var here = target.Directory.Anchor;
        var others = (target.OtherPaneMarked ?? []).Select(e => e.Uri)
            .Concat(target.OtherPaneDirectory is { } dir ? [dir] : []);
        if (others.All(uri => SameSystem(uri.Anchor, here)))
            return true;

        decisions.Report(new Notice(
            "The other pane is on another computer, so its paths can't go on this line.",
            NoticeSeverity.Warning,
            "A line runs where this pane is. Copy the files over first, or run it from the other pane."));
        return false;
    }

    private static bool SameSystem(FsUri a, FsUri b) =>
        a.IsLocal ? b.IsLocal
        : string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(a.Authority, b.Authority, StringComparison.OrdinalIgnoreCase);

    public static void ReportUnresolved(string token, RepositoryInfo? repository, IDecisionHost decisions) =>
        decisions.Report(new Notice(
            $"${{{token}}} has no value here.", NoticeSeverity.Warning, Explain(token, repository)));

    private static string Explain(string token, RepositoryInfo? repository) =>
        token.Equals("distro", StringComparison.OrdinalIgnoreCase)
            ? "There is no WSL distribution for this folder: it is on another computer, or WSL has no default distribution."
        : repository is null
            ? $"{token} describes the git repository a folder is in, and this folder is not in one."
            : token.Equals("branch", StringComparison.OrdinalIgnoreCase)
                ? "HEAD is detached, so there is no branch checked out. Check one out and try again."
                : "This repository has no remote to name. Add one with git remote add.";

    /// <summary>Local URIs become real paths; anything else keeps its URI form.</summary>
    private static string Render(FsUri uri) => uri.IsLocal ? uri.ToLocalPath() : uri.Text;

    /// <summary>
    /// For a line run on a server, a path is the server's path: <c>/home/x/a.txt</c> is what its
    /// shell can open, and <c>sftp://x@host/home/x/a.txt</c> is not.
    /// </summary>
    private static string Render(FsUri uri, ShellQuoting shell) =>
        shell == ShellQuoting.Posix && !uri.IsLocal ? uri.Path : Render(uri);

    private static async Task<string> ReplaceAsync(
        string text, ShellQuoting shell, Func<string, bool, Task<string[]?>> resolve, List<Range>? inserted = null)
    {
        var matches = PlaceholderPattern().Matches(text);
        if (matches.Count == 0)
            return text;

        var builder = new System.Text.StringBuilder();
        var quotes = new ShellQuoteState(shell);
        var position = 0;
        // Where a ${…} left for the shell ended, if it started a word: the shell's own ${p} is
        // likely empty, so a name straight after it starts the word as far as a program can tell.
        var shellsWordEndsAt = -1;

        foreach (Match match in matches)
        {
            var literal = text.Substring(position, match.Index - position);
            builder.Append(literal);
            quotes.Read(literal);

            var token = match.Groups[1].Value;
            // Whether it lands inside the user's own quotes: a value there goes in as it is.
            var words = await resolve(Base(token), quotes.IsOpen);
            if (words is null)
            {
                // Not ours: kept as written, part of the user's own line.
                var started = match.Index == 0 || char.IsWhiteSpace(text[match.Index - 1]) || match.Index == shellsWordEndsAt;
                builder.Append(match.Value);
                quotes.Read(match.Value);
                position = match.Index + match.Length;
                shellsWordEndsAt = started ? position : -1;
                continue;
            }

            // A quote just before starts a word only if it opened one: "pre"${name} is one word.
            var startsWord = match.Index == 0 || char.IsWhiteSpace(text[match.Index - 1]) ||
                             (text[match.Index - 1] is '"' or '\'' && quotes.IsOpen) ||
                             match.Index == shellsWordEndsAt;
            var from = builder.Length;
            builder.AppendJoin(' ', words.Select(word => quotes.Quote(NotAnOption(token, Filtered(token, word), startsWord, shell))));
            if (inserted is not null && IsFromOutside(Base(token)))
                inserted.Add(from..builder.Length);
            position = match.Index + match.Length;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    /// <summary>
    /// A bare name that starts with a dash, standing as a word of its own, is read as an option by
    /// whatever program it reaches: a file called <c>-rf</c> in <c>rm ${name}</c> (audit round 3,
    /// AUDIT.md §2 kind 7). Written as a path in this folder instead, which every program reads as a
    /// file; with a backslash for cmd, whose built-ins take a slash for a switch. Only the bare
    /// names: a path or the selection is already absolute, and a name inside a word is not a word.
    /// </summary>
    private static string NotAnOption(string token, string value, bool startsWord, ShellQuoting shell) =>
        startsWord && value.StartsWith('-') && Base(token).ToLowerInvariant() is "name" or "stem"
            ? (shell == ShellQuoting.Cmd ? @".\" : "./") + value
            : value;

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex PlaceholderPattern();
}

/// <summary>
/// How a placeholder's value is made safe in a line a shell will read. A tool's arguments need none
/// of this -- they are an array -- but a typed command line is shell syntax, and there a path with
/// a space in it must still be one word.
/// </summary>
public enum ShellQuoting
{
    /// <summary>Values go in as they are: an argument vector, where each slot is already one word.</summary>
    None,

    /// <summary>cmd.exe on this machine.</summary>
    Cmd,

    /// <summary>A POSIX shell: on a server, or Git Bash on this machine.</summary>
    Posix,

    /// <summary>PowerShell: single quotes are literal, with '' for a quote; ` escapes in double ones.</summary>
    PowerShell,
}

/// <summary>
/// Follows the quotes in a line as it is read, so a value is quoted only where the user has not
/// already quoted it: <c>"${dir}\out"</c> must not become <c>""C:\a b"\out"</c>, which cmd would
/// read as two words.
/// </summary>
internal sealed partial class ShellQuoteState(ShellQuoting shell)
{
    private char? _open; // the quote the line is inside at this point

    /// <summary>Whether the line is inside a quote at this point.</summary>
    public bool IsOpen => _open is not null;
    private bool _escaped;

    public void Read(string literal)
    {
        foreach (var typed in literal)
        {
            // PowerShell reads the typographic quotes as its own, so a line pasted from a document
            // with ‘${name}’ is inside quotes there, and must be read as being (review, 2026-10-12).
            var c = shell == ShellQuoting.PowerShell
                ? typed switch { '‘' or '’' or '‚' or '‛' => '\'', '“' or '”' or '„' => '"', _ => typed }
                : typed;

            if (_escaped)
                _escaped = false;
            else if (shell == ShellQuoting.Posix && c == '\\' && _open != '\'')
                _escaped = true; // outside quotes and inside double ones; single quotes take it literally
            else if (shell == ShellQuoting.PowerShell && c == '`' && _open != '\'')
                _escaped = true; // PowerShell's escape character, which single quotes also take literally
            else if (shell == ShellQuoting.Cmd && c == '^' && _open is null)
                _escaped = true; // cmd's ^" is a quote character, not a quote: read as one, a&calc went in bare
            else if (_open is null && (c == '"' || (c == '\'' && shell is ShellQuoting.Posix or ShellQuoting.PowerShell)))
                _open = c;
            else if (c == _open)
                _open = null;
        }
    }

    public string Quote(string value) => (shell, _open) switch
    {
        (ShellQuoting.None, _) => value,

        // Inside the user's own quotes cmd has nothing left to escape: a name, path or branch
        // holding a quote, a line break or a %variable% is refused (CmdUnquotable, CmdExpands).
        (ShellQuoting.Cmd, '"') => value,
        (ShellQuoting.Cmd, _) => value.Length == 0 || CmdUnsafe().IsMatch(value)
            // A trailing backslash is doubled, or "C:\" would reach the program as C:" -- the C
            // runtime reads \" as a quote that belongs to the word.
            ? $"\"{value}{new string('\\', value.Length - value.TrimEnd('\\').Length)}\""
            : value,

        (ShellQuoting.Posix, '\'') => PosixInSingleQuotes(value),
        (ShellQuoting.Posix, '"') => PosixSpecialInDoubleQuotes().Replace(value, @"\$0"),
        // PowerShell takes the typographic quotes (’ ‘ ‚ ‛, “ ” „) for quotes too: a name holding ’
        // closed the string and the rest ran as script (review, 2026-10-12). Doubled, as ' is.
        (ShellQuoting.PowerShell, '\'') => PowerShellSingleQuotes().Replace(value, "$0$0"),
        (ShellQuoting.PowerShell, '"') => PowerShellSpecialInDoubleQuotes().Replace(value, "`$0"),
        // Bare only what PowerShell reads as one plain word: a , makes an array (a,b.txt was two
        // files to Remove-Item), @ splats, % and = start other syntax (review, 2026-10-12).
        (ShellQuoting.PowerShell, _) => PowerShellSafe().IsMatch(value) ? value : $"'{PowerShellSingleQuotes().Replace(value, "$0$0")}'",

        _ => PosixSafe().IsMatch(value) ? value : $"'{PosixInSingleQuotes(value)}'",
    };

    /// <summary>
    /// A value inside single quotes for any POSIX shell and for fish. Fish reads \' and \\ as
    /// escapes even inside single quotes, so a server's file called <c>a\</c> left the quote open
    /// there and the rest of the line, the next file's name included, ran as commands. Such a
    /// backslash, and every quote, is written outside the quotes, escaped, which every shell reads
    /// the same way. Only before \ or ' or at the end, where fish would read an escape: anywhere
    /// else it is literal there too, and C:\work stays readable for Git Bash.
    /// </summary>
    internal static string PosixInSingleQuotes(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\'' || (c == '\\' && (i + 1 == value.Length || value[i + 1] is '\\' or '\'')))
                builder.Append('\'').Append('\\').Append(c).Append('\''); // close, escaped, reopen
            else
                builder.Append(c);
        }

        return builder.ToString();
    }

    [GeneratedRegex("[`$\"“”„]")]
    private static partial Regex PowerShellSpecialInDoubleQuotes();

    [GeneratedRegex("['‘’‚‛]")]
    private static partial Regex PowerShellSingleQuotes();

    [GeneratedRegex(@"[\s&|<>^(),;=%!""]")]
    private static partial Regex CmdUnsafe();

    // Not a leading = or %: zsh reads a bare =bash as the path of bash (rm ${name} on a file
    // called =bash was rm /usr/bin/bash), and fish a bare %self as its own process id.
    [GeneratedRegex(@"^[A-Za-z0-9_\-./:@+,][A-Za-z0-9_\-./:@%+=,]*$")]
    private static partial Regex PosixSafe();

    // Not a leading -: bare, -Recurse binds as a switch; quoted, it is only a name.
    [GeneratedRegex(@"^[A-Za-z0-9_./:+][A-Za-z0-9_\-./:+]*$")]
    private static partial Regex PowerShellSafe();

    [GeneratedRegex(@"[\\$`""]")]
    private static partial Regex PosixSpecialInDoubleQuotes();
}
