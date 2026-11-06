using System.Collections.Concurrent;
using System.IO;
using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Extensibility;
using DanceCommander.Providers.Sftp;

namespace DanceCommander.App.Services;

/// <summary>A shell a typed line can run in: its name for the prompt, its quoting, how to start it.</summary>
public sealed record CommandShell(string Name, ShellQuoting Quoting, Func<string, ToolDefinition> ToolFor)
{
    /// <summary>"bash", "cmd", "pwsh": what the line history is kept under, whatever the prompt says.</summary>
    public string Key => Name.Split(' ')[0];
}

/// <summary>
/// Which shell the command line talks to (PLAN.md §4): here, the one settings.json names, Git Bash
/// by default when it is installed; on a server, the account's login shell, asked once.
/// </summary>
/// <remarks>
/// cmd.exe was the only choice, and it has no single quotes at all, so a line that meant one thing
/// in the user's terminal meant another here. The prompt names the shell in grey, because the same
/// line means different things in each, and only saying so keeps that from being a surprise.
/// </remarks>
public sealed class CommandShells
{
    private readonly IConfigStore<SettingsFile>? _settings;
    private readonly ConcurrentDictionary<string, RemoteShell> _remote = new(StringComparer.OrdinalIgnoreCase);

    public CommandShells(IConfigStore<SettingsFile>? settings = null)
    {
        _settings = settings;
        Local = Resolve(settings?.Current.CommandLine.Shell);
        if (settings is not null)
            settings.Changed += (_, _) =>
            {
                Local = Resolve(settings.Current.CommandLine.Shell);
                Changed?.Invoke(this, EventArgs.Empty);
            };
    }

    /// <summary>The shell for lines typed in a local pane.</summary>
    public CommandShell Local { get; private set; }

    /// <summary>The local shell changed, or a server's shell became known: prompts say so.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// A line still running after this long moves to the job strip, where it streams, can be
    /// stopped and says when it's done, rather than holding the prompt (COMMAND_LINE_PLAN.md F).
    /// </summary>
    public static readonly TimeSpan BackgroundAfter = TimeSpan.FromSeconds(3);

    public static readonly CommandShell Cmd = new("cmd", ShellQuoting.Cmd, line => new ToolDefinition
    {
        Id = "command-line",
        Title = line,
        Exec = "cmd.exe",
        // Verbatim, because handed over as one argument the line would be wrapped in quotes and its
        // own quotes escaped as \" -- which cmd.exe does not read, so git commit -m "a message"
        // reached git as -m "a plus three stray pathspecs. /s makes cmd strip exactly the outer
        // pair and run what is between them as typed. /v:off whatever the registry says: with delayed
        // expansion on, a ! in a file name put on the line was taken away, or !NAME! expanded.
        Args = ["/v:off", "/s", "/c", $"\"{line}\""],
        VerbatimArgs = true,
        Launch = "job",
        BackgroundAfter = CommandShells.BackgroundAfter,
        QuietWhenSilent = true,
    });

    /// <summary>
    /// The local shells installed here, the configured one first, then Git Bash, PowerShell, cmd:
    /// the order Ctrl+E steps through on an open command line. Looked for when asked, not kept,
    /// since it is asked only on that key.
    /// </summary>
    public IReadOnlyList<CommandShell> LocalChoices()
    {
        var choices = new List<CommandShell> { Local };
        foreach (var shell in new[] { GitBash(), PowerShell(), Cmd })
            if (shell is not null && choices.All(known => known.Key != shell.Key))
                choices.Add(shell);
        return choices;
    }

    internal static CommandShell Resolve(string? choice)
    {
        switch (choice?.Trim().ToLowerInvariant())
        {
            case "cmd":
                return Cmd;
            case "pwsh" or "powershell":
                return PowerShell() ?? Cmd;
            case "bash":
                return GitBash() ?? Cmd;
            case null or "" or "auto":
                return GitBash() ?? Cmd;
            default:
                // Said where it applies (§1.3: defaults are never invisible): a value nobody knows
                // was ignored quietly, and the prompt then claimed a choice the user hadn't made.
                var fallback = GitBash() ?? Cmd;
                return fallback with { Name = $"{fallback.Name} (\"{choice!.Trim()}\" is not a shell)" };
        }
    }

    private static CommandShell? GitBash()
    {
        if (FindGitBash() is not { } bash)
            return null;
        return new CommandShell("bash", ShellQuoting.Posix, line => new ToolDefinition
        {
            Id = "command-line",
            Title = line,
            Exec = bash,
            // One argument, quoted the way Git Bash's runtime reads it back, so the line arrives at
            // bash -c exactly as typed (ProcessArguments says what .NET's own quoting got wrong).
            // Verbatim, too, because the line is expanded already: a file name holding ${name}
            // must not be expanded a second time, unquoted, by the tool runner.
            Args = ["-c", ProcessArguments.ForMsys(line)],
            VerbatimArgs = true,
            OutputEncoding = "utf-8",
            Launch = "job",
        BackgroundAfter = CommandShells.BackgroundAfter,
        QuietWhenSilent = true,
        });
    }

    private static CommandShell? PowerShell()
    {
        var exec = ExecutableSearch.Resolve("pwsh");
        if (!File.Exists(exec))
            exec = ExecutableSearch.Resolve("powershell");
        if (!File.Exists(exec))
            return null;
        return new CommandShell("pwsh", ShellQuoting.PowerShell, line => new ToolDefinition
        {
            Id = "command-line",
            Title = line,
            Exec = exec,
            // Verbatim, as bash's: expanded once, by the command line, and quoted for pwsh here.
            Args = ["-NoLogo", "-NoProfile", "-Command", ProcessArguments.ForWindows(line)],
            VerbatimArgs = true,
            Launch = "job",
        BackgroundAfter = CommandShells.BackgroundAfter,
        QuietWhenSilent = true,
        });
    }

    /// <summary>
    /// Git for Windows' bash, found through git on PATH, then where its installer puts it. Never
    /// System32's bash.exe: that one is WSL, a different machine with different paths.
    /// </summary>
    internal static string? FindGitBash()
    {
        var git = ExecutableSearch.Resolve("git");
        var candidates = new List<string>();
        if (File.Exists(git) && Path.GetDirectoryName(git) is { } gitDirectory)
        {
            // git.exe lives in Git\cmd, Git\bin or Git\mingw64\bin; bash in Git\bin.
            for (var root = Directory.GetParent(gitDirectory); root is not null; root = root.Parent)
            {
                candidates.Add(Path.Combine(root.FullName, "bin", "bash.exe"));
                if (candidates.Count >= 3)
                    break;
            }
        }
        foreach (var programs in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            candidates.Add(Path.Combine(Environment.GetFolderPath(programs), "Git", "bin", "bash.exe"));

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return candidates.FirstOrDefault(path =>
            File.Exists(path) && !path.StartsWith(system, StringComparison.OrdinalIgnoreCase));
    }

    // ---- servers --------------------------------------------------------------------------

    /// <summary>A server account's login shell, as far as it said.</summary>
    /// <param name="Name">"zsh", "bash"; null when it is not a POSIX shell.</param>
    public sealed record RemoteShell(string? Name)
    {
        public bool IsPosix => Name is not null;
    }

    /// <summary>The shell a server's account was found to have, or null if not asked yet.</summary>
    public RemoteShell? Known(FsUri location) => _remote.GetValueOrDefault(location.Authority);

    /// <summary>
    /// Asks the server once per account. <c>echo "$SHELL"</c> sorts every family in one round trip:
    /// a POSIX shell (bash, zsh, sh, ash, fish) prints its path, while a Windows server's cmd.exe or
    /// PowerShell prints something else (the text back, or an empty line), and there typed lines
    /// would be quoted for the wrong shell, so they are not run at all.
    /// </summary>
    public async Task<RemoteShell> AskAsync(IRemoteCommandService remote, FsUri directory, CancellationToken ct)
    {
        if (Known(directory) is { } known)
            return known;

        RemoteShell shell;
        try
        {
            var result = await remote.RunAsync(directory, "echo \"$SHELL\"", ct);
            var answer = result.Output.Trim();
            shell = answer.StartsWith('/') ? new RemoteShell(Path.GetFileName(answer.TrimEnd('/'))) : new RemoteShell(null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Not able to ask is not an answer; the line itself will say what is wrong.
            return new RemoteShell("sh");
        }

        _remote[directory.Authority] = shell;
        Changed?.Invoke(this, EventArgs.Empty);
        return shell;
    }
}
