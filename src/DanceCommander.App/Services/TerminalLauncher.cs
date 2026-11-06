using System.Diagnostics;
using System.IO;
using DanceCommander.Core.Fs;
using DanceCommander.Extensibility;
using DanceCommander.Providers.Sftp;

namespace DanceCommander.App.Services;

/// <summary>
/// The hand-off to a real terminal (COMMAND_LINE_PLAN.md J). The command line runs and reports;
/// vim, top or a REPL need a terminal, and Windows Terminal is one, so they get one key away rather
/// than a second application inside this one (the kind of surface DESIGN.md §1.4 declines).
/// </summary>
public static class TerminalLauncher
{
    /// <summary>The program a line starts, if it needs a terminal (<see cref="TerminalPrograms"/>).</summary>
    public static string? NeedsTerminal(string line) => TerminalPrograms.NeedsTerminal(line);

    /// <summary>Windows Terminal in a local folder, running <paramref name="line"/> there if given.</summary>
    public static void OpenLocal(string folder, CommandShell shell, string? line)
    {
        var info = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
        info.ArgumentList.Add("-d");
        info.ArgumentList.Add(folder);
        if (line is not null)
        {
            var run = shell.ToolFor(line);
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(run.Exec);
            // bash's and pwsh's arguments come quoted for starting them directly (ProcessArguments);
            // here they go through wt's own argument list instead, so they are given as they were.
            IReadOnlyList<string> arguments = shell.Key switch
            {
                "bash" => ["-c", line],
                "pwsh" => ["-NoLogo", "-NoProfile", "-Command", line],
                _ => run.Args,
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(Escape(argument));
        }
        Process.Start(info)?.Dispose();
    }

    /// <summary>
    /// Windows Terminal with ssh, already in the pane's folder on the server, in the account's own
    /// shell; or running <paramref name="line"/> there. A second login (Windows' own ssh), which a key
    /// or agent makes invisible. A server without a POSIX shell gets plain ssh: the cd can't be said.
    /// </summary>
    public static void OpenRemote(FsUri directory, bool posix, string? line)
    {
        var target = SftpTarget.From(directory);
        var info = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
        foreach (var argument in (string[])["new-tab", "--title", target.ToString(), "--", "ssh", "-t", "-p", target.Port.ToString(), $"{target.User}@{target.Host}"])
            info.ArgumentList.Add(argument);
        // One line, joined with &&: safe here because what follows the cd is ours, or a single
        // command with no operator in it (NeedsTerminal turns the rest down).
        if (posix)
            info.ArgumentList.Add(Escape($"cd {SftpCommandService.Quote(directory.Path)} && {line ?? "exec $SHELL -l"}"));
        Process.Start(info)?.Dispose();
    }

    /// <summary>Windows Terminal reads an unescaped ';' as "and another tab", even inside an argument.</summary>
    private static string Escape(string argument) => argument.Replace(";", "\\;");
}
