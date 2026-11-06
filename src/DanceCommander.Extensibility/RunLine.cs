namespace DanceCommander.Extensibility;

/// <summary>
/// The command line that runs a script or program: <c>./build.sh</c>, <c>py ./tool.py</c>,
/// <c>&amp; pwsh -NoProfile -File '.\deploy.ps1'</c>. Run sends it to the command line as it is;
/// Run with arguments puts it there for the rest to be typed.
/// </summary>
/// <remarks>
/// A line rather than a process of our own, so that a run is exactly what typing it would be: the
/// same shell, the pane's folder, output in the pane, the job strip if it outlasts the patience, and
/// the line in the history for Up to bring back with different arguments. And a line the user can
/// read before it runs, which is the point of not hiding what the application does.
///
/// A file from a search or a flattened tree is not in the pane's folder, and the line goes to its
/// own folder first: <c>cd 'C:/tools/x' &amp;&amp; ./x.exe</c>. Run from the search's root, a
/// program that reads files beside itself by relative name did not find them, as Enter already
/// knew (Launcher). Said on the line rather than slipped in as the process's folder, so Run with
/// arguments and Up keep it.
/// </remarks>
public static class RunLine
{
    private static readonly string[] Direct = [".exe", ".com", ".bat", ".cmd"];

    /// <summary>
    /// What cmd.exe reads in a batch file's command line even inside quotes, or splits it on where
    /// the shell before it left it unquoted: Git Bash quotes only ASCII spaces, and cmd also splits
    /// on ; , = and the other spaces, so x;y.bat ran x.bat (review, 2026-10-12).
    /// </summary>
    private static bool HasBatchHazard(string text) =>
        text.Any(c => c is '%' or '&' or '^' or '(' or ')' or '!' or '|' or '<' or '>' or ';' or ',' or '='
            || (c != ' ' && char.IsWhiteSpace(c)));

    private static bool IsBatch(string path) =>
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a file of this name is one Run knows how to start, from its name alone.</summary>
    public static bool Runs(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return Direct.Contains(extension) || extension is ".ps1" or ".py" or ".sh";
    }

    /// <param name="file">The file's full local path.</param>
    /// <param name="workingDirectory">Where the command line runs: the pane's folder.</param>
    /// <param name="find">Where a program of this bare name is on PATH, or null: picks pwsh over powershell, py over python.</param>
    /// <param name="gitBash">Git Bash, for a .sh from a shell that is not bash; null when not installed.</param>
    /// <returns>Null when the file is not one Run starts, or its path cannot be put on a line safely.</returns>
    public static string? For(
        string file, string workingDirectory, ShellQuoting shell, Func<string, string?> find, string? gitBash)
    {
        var name = Path.GetFileName(file);
        var extension = Path.GetExtension(name).ToLowerInvariant();

        // Windows takes a trailing dot or space off a name, so "tools.\x.cmd" on a line is
        // tools\x.cmd, a neighbour, in every shell (review, 2026-10-23). Only \\?\ keeps it, and
        // a line can't count on that.
        var plain = WithoutLongPrefix(file);
        if (plain.Split('\\', '/').Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            return null;

        // Relative to the folder it runs in, so the line reads as typed would; always with a ./ in
        // front, which keeps a name starting with "-" from being an option and makes the shell run
        // this file rather than search PATH for one of the same name. Compared without \\?\, which
        // a long file's path has and its short folder's doesn't.
        var here = string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(plain) ?? string.Empty),
            Path.TrimEndingDirectorySeparator(WithoutLongPrefix(workingDirectory)),
            StringComparison.OrdinalIgnoreCase);
        // A .sh goes to Git Bash, which reads its arguments with its own runtime: / there too, as
        // that runtime takes \ for an escape in a word it doesn't find quoted.
        var forBash = shell == ShellQuoting.Posix || extension == ".sh";
        var relative = forBash ? "./" + name : @".\" + name;

        // Elsewhere, the line goes there first. Only in the three shells whose cd is known here,
        // and only where it can go: cmd has no current folder on a share or behind \\?\ ("UNC paths
        // are not supported"), and pwsh 7 went behind \\?\ but then found no .\x.exe there.
        var folder = Path.GetDirectoryName(file);
        var longPath = plain.Length != file.Length;
        if (!here && (folder is null || shell switch
            {
                ShellQuoting.Posix => false,
                ShellQuoting.Cmd => longPath || plain.StartsWith(@"\\", StringComparison.Ordinal),
                ShellQuoting.PowerShell => longPath,
                _ => true,
            }))
        {
            return null;
        }

        // Judged by the full path when the folder goes on the line too, in the cd.
        var target = forBash
            ? (here ? relative : file.Replace('\\', '/'))
            : here ? relative : file;

        // What goes on the line, judged whole: a search result's row carries its folders too, and
        // a folder called ${env:…} was as much a placeholder as a file of that name (review,
        // 2026-10-12). The line goes through the command line's placeholder expansion; cmd expands
        // %VAR% inside quotes; and a batch file is always started by cmd /c, whichever shell asks,
        // which reads & ^ ( ) ! and % again however the shell before it quoted them.
        // cmd also expands !VAR! inside quotes where delayed expansion is on in the registry.
        if (target.Contains("${", StringComparison.Ordinal) ||
            (shell == ShellQuoting.Cmd && target.IndexOfAny(['%', '!']) >= 0) ||
            (IsBatch(target) && HasBatchHazard(target)))
        {
            return null;
        }

        // Patterns the program at the end still expands, however the shell quoted the name: Windows
        // PowerShell 5.1 takes [ ] in a single-quoted path for a wildcard (& '.\a[1].exe' ran
        // a1.exe), and the command-line shell called PowerShell may be 5.1. Git Bash's runtime globs
        // a word holding [ { ' * ? that reaches it unquoted from cmd or PowerShell (review, 2026-10-12).
        if ((shell == ShellQuoting.PowerShell && target.IndexOfAny(['[', ']']) >= 0) ||
            (extension == ".sh" && shell != ShellQuoting.Posix && target.IndexOfAny(['\'', '"', '[', ']', '{', '}', '*', '?', '~', '`', '$']) >= 0))
        {
            return null;
        }

        // By its full path in every shell: cmd looks in the current folder before PATH, and any
        // shell does when PATH holds "."; the current folder is the one being browsed, so a py.cmd
        // beside the script would have run instead of Python. Not on PATH means not run. An
        // interpreter that is itself a batch file (pyenv's python.bat) hands the script's name to
        // cmd /c, so the batch rule then applies to the name too; and Windows PowerShell reads
        // [ ] in a -File path as a wildcard.
        string? Interpreter(string preferred, string fallback)
        {
            var name = find(preferred) is not null ? preferred : fallback;
            if (find(name) is not { } path)
                return null;
            if (IsBatch(path) && HasBatchHazard(target))
                return null;
            if (name == "powershell" && target.IndexOfAny(['[', ']']) >= 0)
                return null;
            return Program(path);
        }

        // The posix shell is Git Bash, which takes C:/… as it is and \ for an escape.
        string Program(string path) =>
            ToolPlaceholders.Quote(shell == ShellQuoting.Posix ? path.Replace('\\', '/') : path, shell);

        string[] interpreter;
        switch (extension)
        {
            case var direct when Direct.Contains(direct):
                interpreter = [];
                break;
            case ".ps1" when Interpreter("pwsh", "powershell") is { } powershell:
                // -File, and the execution policy as it stands: lifting it is the user's call.
                interpreter = [powershell, "-NoProfile", "-File"];
                break;
            case ".py" when Interpreter("py", "python") is { } python:
                interpreter = [python];
                break;
            case ".sh" when gitBash is not null:
                interpreter = [Program(gitBash)];
                break;
            default:
                return null;
        }

        var line = string.Join(' ', [.. interpreter, ToolPlaceholders.Quote(relative, shell)]);

        // PowerShell reads a quoted first word as a string, not a command, without the call operator.
        if (shell == ShellQuoting.PowerShell)
            line = "& " + line;

        return here ? line : GoTo(folder!, shell) + line;
    }

    /// <summary>
    /// The cd in front of a line, which stops it there if the folder can't be gone to: the file it
    /// names is relative, and from the pane's folder it would be some other file of the same name.
    /// PowerShell has no &amp;&amp; before 7. -ErrorAction Stop alone did not end the line: an
    /// error PowerShell counts as ending only its own statement went on to the next, and the
    /// pane's x.cmd ran (review, 2026-10-23). Rethrown from a catch, any error ends the line.
    /// </summary>
    private static string GoTo(string folder, ShellQuoting shell) => shell switch
    {
        ShellQuoting.Posix => $"cd {ToolPlaceholders.Quote(folder.Replace('\\', '/'), shell)} && ",
        ShellQuoting.Cmd => $"cd /d {ToolPlaceholders.Quote(folder, shell)} && ",
        _ => $"try {{ Set-Location -LiteralPath {ToolPlaceholders.Quote(folder, shell)} -ErrorAction Stop }} catch {{ throw }}; ",
    };

    /// <summary>A path as Windows spells it without \\?\ (and \\?\UNC\ back to \\).</summary>
    private static string WithoutLongPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path;
}
