namespace DanceCommander.Extensibility;

/// <summary>
/// Finds the program a bare name like <c>devcontainer</c> means, the way a command prompt would.
/// </summary>
/// <remarks>
/// Without the shell, .NET looks for <c>name.exe</c> and nothing else, and a great deal of what
/// developers run is an npm or Python <c>.cmd</c> shim: the devcontainer CLI that motivated
/// background jobs is one. The shell-launched "process" kind comes through here as well, because
/// the shell would look in the working folder before PATH (ToolRunner).
/// </remarks>
public static class ExecutableSearch
{
    private static readonly string[] Runnable = [".com", ".exe", ".bat", ".cmd"];

    /// <summary>
    /// Characters cmd.exe gives meaning to while reading a batch file's command line. .NET quotes
    /// arguments for the C runtime's rules, and cmd does not follow them — so an argument holding
    /// one of these, passed to a batch file, can stop being an argument (§6.6).
    /// </summary>
    private static readonly char[] BatchHazards = ['&', '|', '<', '>', '^', '%', '"', '\r', '\n'];

    public static string Resolve(string exec)
    {
        // A path means exactly that.
        if (exec.Length == 0 || exec.IndexOfAny(['\\', '/', ':']) >= 0)
            return exec;

        // A name that says what it is ("notepad++.exe") is still looked for on PATH, as cmd does;
        // left bare, the shell would look in the folder being browsed first (review, 2026-10-12).
        var extensions = Path.HasExtension(exec)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(e => Runnable.Contains(e, StringComparer.OrdinalIgnoreCase))
                .ToList();

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            // A relative entry (".") means wherever the process happens to be, which for a shell
            // started from a pane is the folder being browsed.
            if (!Path.IsPathFullyQualified(directory.Trim('"')))
                continue;

            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), exec + extension.ToLowerInvariant());
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is somebody else's problem; skip it.
                }
            }
        }

        return AppPath(Path.HasExtension(exec) ? exec : exec + ".exe") ?? exec;
    }

    /// <summary>True when <see cref="Resolve"/> found nothing and gave the name back as it was.</summary>
    public static bool IsBare(string resolved) =>
        resolved.Length > 0 && resolved.IndexOfAny(['\\', '/', ':']) < 0;

    /// <summary>
    /// Where App Paths says a program is: how the shell finds <c>winword</c> or <c>notepad++</c>,
    /// which are on no PATH. The user's registration first, as the shell does.
    /// </summary>
    private static string? AppPath(string name)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (var hive in (Microsoft.Win32.RegistryKey[])[Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine])
        {
            try
            {
                using var entry = hive.OpenSubKey(key + name);
                if (entry?.GetValue(null) is string value)
                {
                    var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                    if (File.Exists(path))
                        return path;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unreadable or malformed: as though it were not registered.
            }
        }

        return null;
    }

    public static bool IsBatchFile(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first argument cmd.exe would misread if it were passed to a batch file, or null.
    /// </summary>
    /// <remarks>
    /// The argv guarantee (§6.6) is that a file name can never become part of the command. A batch
    /// file breaks it: cmd re-parses the command line with its own rules, so a folder called
    /// <c>a&amp;b</c> becomes two commands. Rather than quietly lose that guarantee for every
    /// <c>.cmd</c> tool, the runner refuses the one argument that would exploit it and says why.
    /// </remarks>
    public static string? UnsafeForBatch(IEnumerable<string> arguments) =>
        arguments.FirstOrDefault(a => a.IndexOfAny(BatchHazards) >= 0);
}
