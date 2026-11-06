using System.Diagnostics;
using DanceCommander.Providers.Sftp;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// What goes to the server around a typed line must never change what the line does. Run here in
/// Git Bash, a POSIX shell like the server's.
/// </summary>
public class RemoteScriptTests
{
    private static readonly string Bash =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");

    private static (string Output, int Exit)? Run(string script)
    {
        if (!File.Exists(Bash))
            return null; // no Git for Windows on this machine
        var info = new ProcessStartInfo(Bash) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (output.Replace("\r\n", "\n"), process.ExitCode);
    }

    [Fact]
    public void AFailedCdRunsNothingOfTheLine()
    {
        // The old form ran the part after ';' anyway, in the home folder.
        Run("cd '/no/such/folder' && echo a; echo b")?.Output.ShouldBe("b\n");

        var result = Run(SftpCommandService.Script("/no/such/folder", "echo a; echo b"));
        if (result is null)
            return;
        result.Value.Output.ShouldBeEmpty();
        result.Value.Exit.ShouldBe(1);
    }

    [Fact]
    public void TheLineGroupsAsTyped()
    {
        var result = Run(SftpCommandService.Script("/", "false || echo fallback; echo after"));
        result?.Output.ShouldBe("fallback\nafter\n");
    }

    [Fact]
    public void TheLineIsSentExactlyAsTyped()
    {
        const string line = "ls | grep -v 'a b' && echo \"$HOME\" # done";
        SftpCommandService.Script("/home/x/it's", line)
            .ShouldBe("cd '/home/x/it'\\''s' || exit 1\n" + line);
    }
}
