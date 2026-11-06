using System.Diagnostics;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Fs;
using DanceCommander.Extensibility;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A placeholder's value is one word in whichever shell reads the line: cmd, a POSIX shell (Git
/// Bash here, or a server's), or PowerShell. And a typed line reaches Git Bash and PowerShell
/// exactly as typed, single quotes and all, which cmd.exe never could (it has none).
/// </summary>
public class ShellQuotingTests
{
    private static readonly FsEntry Spaced = new(FsUri.Local(@"C:\work\it's a b.txt"), "it's a b.txt", IsDirectory: false, 1, null);

    private static Task<string?> Expand(string line, ShellQuoting shell) =>
        ToolPlaceholders.ExpandOneAsync(
            line, new ToolTarget(FsUri.Local(@"C:\work"), [Spaced], Spaced, null, null), new ScriptedDecisions(), shell: shell);

    [Fact]
    public async Task PowerShellGetsSingleQuotesWithTheirOwnEscape()
    {
        (await Expand("Get-Item ${path}", ShellQuoting.PowerShell)).ShouldBe(@"Get-Item 'C:\work\it''s a b.txt'");
    }

    [Fact]
    public async Task PowerShellLeavesTheUsersOwnQuotesAlone()
    {
        (await Expand("Write-Output \"${path} is here\"", ShellQuoting.PowerShell))
            .ShouldBe("Write-Output \"C:\\work\\it's a b.txt is here\"");
        (await Expand("Write-Output '${path}'", ShellQuoting.PowerShell)).ShouldBe(@"Write-Output 'C:\work\it''s a b.txt'");
    }

    [Fact]
    public async Task GitBashGetsPosixQuotesThatKeepBackslashes()
    {
        (await Expand("cat ${path}", ShellQuoting.Posix)).ShouldBe(@"cat 'C:\work\it'\''s a b.txt'");
    }

    /// <summary>As CommandShells starts a shell: the arguments as one finished command line.</summary>
    private static string? Run(string exec, string arguments)
    {
        if (!File.Exists(exec))
            return null;
        var info = new ProcessStartInfo(exec, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Replace("\r\n", "\n");
    }

    private static readonly string Bash =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");

    private static readonly string Pwsh =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");

    [Theory]
    [InlineData("""printf '[%s]\n' 'single quotes' "double quotes" it\'s""", "[single quotes]\n[double quotes]\n[it's]\n")]
    // No space: .NET left it unquoted, and Git Bash's runtime took the quotes off and ran the $( ).
    [InlineData("""echo${IFS}'$(echo${IFS}INJECTED)'""", "$(echo${IFS}INJECTED)\n")]
    // Every backslash as typed: the runtime halves pairs inside quotes.
    [InlineData("""printf '%s\n' '\\server\share' 'a\\b' "C:\\x\\" """, "\\\\server\\share\na\\\\b\nC:\\x\\\n")]
    public void ALineReachesGitBashAsTyped(string line, string expected)
    {
        var output = Run(Bash, "-c " + ProcessArguments.ForMsys(line));
        if (output is null)
            return; // no Git for Windows on this machine
        output.ShouldBe(expected);
    }

    [Theory]
    [InlineData("""Write-Output 'single quotes' "double quotes" 'it''s'""", "single quotes\ndouble quotes\nit's\n")]
    [InlineData("""Write-Output '\\server\share' 'C:\x\'""", "\\\\server\\share\nC:\\x\\\n")]
    [InlineData("""Write-Output 'a"b' "c`"d\" """, "a\"b\nc\"d\\\n")]
    public void ALineReachesPowerShellAsTyped(string line, string expected)
    {
        var output = Run(Pwsh, "-NoLogo -NoProfile -Command " + ProcessArguments.ForWindows(line));
        if (output is null)
            return;
        output.ShouldBe(expected);
    }

    [Fact]
    public async Task PowerShellQuotesACommaOrAnAt()
    {
        // a,b.txt bare is an array of two names: Remove-Item would have taken both.
        var comma = new FsEntry(FsUri.Local(@"C:\work\a,b.txt"), "a,b.txt", IsDirectory: false, 1, null);
        (await ToolPlaceholders.ExpandOneAsync(
                "Remove-Item ${name}", new ToolTarget(FsUri.Local(@"C:\work"), [comma], comma, null, null),
                new ScriptedDecisions(), shell: ShellQuoting.PowerShell))
            .ShouldBe("Remove-Item 'a,b.txt'");
    }

    private static readonly FsEntry Dashed = new(FsUri.Local(@"C:\work\-rf"), "-rf", IsDirectory: false, 1, null);

    private static Task<string?> ExpandDashed(string line, ShellQuoting shell) =>
        ToolPlaceholders.ExpandOneAsync(
            line, new ToolTarget(FsUri.Local(@"C:\work"), [Dashed], Dashed, null, null), new ScriptedDecisions(), shell: shell);

    [Theory]
    [InlineData(ShellQuoting.Posix, "rm ./-rf")]
    [InlineData(ShellQuoting.None, "rm ./-rf")]
    [InlineData(ShellQuoting.Cmd, @"del .\-rf")]
    public async Task ANameThatStartsWithADashIsAFileNotAnOption(ShellQuoting shell, string expected) =>
        (await ExpandDashed(shell == ShellQuoting.Cmd ? "del ${name}" : "rm ${name}", shell)).ShouldBe(expected);

    [Fact]
    public async Task ADashedNameInsideAWordOrAsAPathIsLeftAsItIs()
    {
        (await ExpandDashed("cp ${path} out_${name}", ShellQuoting.None)).ShouldBe(@"cp C:\work\-rf out_-rf");
    }

    [Fact]
    public async Task AClosingQuoteBeforeTheNameDoesNotStartAWord()
    {
        // "pre"${name} is one word, pre-rf: a ./ there made it pre./-rf, another name.
        (await ExpandDashed("touch \"pre\"${name}", ShellQuoting.Posix)).ShouldNotBeNull().ShouldNotContain("./");
        (await ExpandDashed("rm \"${name}\"", ShellQuoting.Posix)).ShouldNotBeNull().ShouldContain("./-rf");
    }

    private static ToolTarget OnServer(params string[] names)
    {
        var entries = names.Select(n => new FsEntry(FsUri.Remote("sftp", "x@host", "/srv").Child(n), n, IsDirectory: false, 1, null)).ToArray();
        return new ToolTarget(FsUri.Remote("sftp", "x@host", "/srv"), entries, entries[0], null, null);
    }

    [Fact]
    public async Task ABackslashBeforeTheClosingQuoteIsWrittenOutsideIt()
    {
        // Fish reads \' inside single quotes as an escaped quote: 'a\' '; echo INJECTED #' ran echo.
        (await ToolPlaceholders.ExpandOneAsync(
                "rm ${selection}", OnServer("a\\", "; echo INJECTED #"), new ScriptedDecisions(), shell: ShellQuoting.Posix))
            .ShouldBe(@"rm '/srv/a'\\'' '/srv/; echo INJECTED #'");
        (await ToolPlaceholders.ExpandOneAsync(
                "cat '${name}'", OnServer(@"a\\b\'c"), new ScriptedDecisions(), shell: ShellQuoting.Posix))
            .ShouldBe(@"cat 'a'\\'\b'\\''\''c'");
    }

    [Theory]
    [InlineData("a\\")]
    [InlineData(@"a\\b\'c d")]
    [InlineData(@"C:\work\x")]
    public async Task BashStillReadsTheNameAsItIs(string name)
    {
        var line = await ToolPlaceholders.ExpandOneAsync(
            "printf '[%s]' ${name} '${name}'", OnServer(name), new ScriptedDecisions(), shell: ShellQuoting.Posix);
        var output = Run(Bash, "-c " + ProcessArguments.ForMsys(line!));
        if (output is null)
            return;
        output.ShouldBe($"[{name}][{name}]");
    }

    [Theory]
    [InlineData("echo ${otherPaneDir}")]
    [InlineData("echo \"${otherPaneDir}\"")]
    public async Task CmdRefusesAQuoteFromAServer(string line)
    {
        var here = new FsEntry(FsUri.Local(@"C:\work\a.txt"), "a.txt", IsDirectory: false, 1, null);
        var target = new ToolTarget(
            FsUri.Local(@"C:\work"), [here], here, FsUri.Remote("sftp", "x@host", "/srv/x\" & calc & \""), null);
        var decisions = new ScriptedDecisions();
        (await ToolPlaceholders.ExpandOneAsync(line, target, decisions, shell: ShellQuoting.Cmd)).ShouldBeNull();
        decisions.Notices.ShouldHaveSingleItem().Message.ShouldContain("can't go on a cmd line");
    }

    [Fact]
    public async Task CmdRefusesANameThatNamesAVariable()
    {
        var profile = new FsEntry(FsUri.Local(@"C:\work\%USERPROFILE%"), "%USERPROFILE%", IsDirectory: true, 0, null);
        var target = new ToolTarget(FsUri.Local(@"C:\work"), [profile], profile, null, null);
        (await ToolPlaceholders.ExpandOneAsync("rd /s /q ${name}", target, new ScriptedDecisions(), shell: ShellQuoting.Cmd))
            .ShouldBeNull();
        // The same name is only a name to PowerShell.
        (await ToolPlaceholders.ExpandOneAsync("Remove-Item ${name}", target, new ScriptedDecisions(), shell: ShellQuoting.PowerShell))
            .ShouldBe("Remove-Item '%USERPROFILE%'");
    }

    private static FsEntry Local(string name) => new(FsUri.Local(@"C:\work").Child(name), name, IsDirectory: true, 0, null);

    [Theory]
    [InlineData("%CD%")]          // cmd's own, in no environment block: the folder being browsed
    [InlineData("%random%")]
    [InlineData("%CMDCMDLINE%")]
    [InlineData("%=ExitCode%")]
    public async Task CmdRefusesItsOwnDynamicVariables(string name)
    {
        var entry = Local(name);
        (await ToolPlaceholders.ExpandOneAsync(
                "rd /s /q ${name}", new ToolTarget(FsUri.Local(@"C:\work"), [entry], entry, null, null), new ScriptedDecisions(),
                shell: ShellQuoting.Cmd))
            .ShouldBeNull();
    }

    [Fact]
    public async Task CmdJudgesPercentsWhereTheValuesLanded()
    {
        // Each has one %; side by side they are %USERPROFILE%.
        var entry = Local("USERPROFILE%");
        var target = new ToolTarget(
            FsUri.Local(@"C:\work"), [entry], entry, null, new RepositoryInfo(FsUri.Local(@"C:\work"), "fix%", null));
        (await ToolPlaceholders.ExpandOneAsync("rd /s /q \"${branch}${name}\"", target, new ScriptedDecisions(), shell: ShellQuoting.Cmd))
            .ShouldBeNull();
        // The user's own %PATH% beside a value is the user's.
        var plain = Local("a");
        (await ToolPlaceholders.ExpandOneAsync(
                "echo %PATH% ${name}", new ToolTarget(FsUri.Local(@"C:\work"), [plain], plain, null, null), new ScriptedDecisions(),
                shell: ShellQuoting.Cmd))
            .ShouldBe("echo %PATH% a");
    }

    [Fact]
    public async Task CmdReadsACaretQuoteAsACharacter()
    {
        var entry = Local("a&calc");
        (await ToolPlaceholders.ExpandOneAsync(
                "echo ^\"${name}", new ToolTarget(FsUri.Local(@"C:\work"), [entry], entry, null, null), new ScriptedDecisions(),
                shell: ShellQuoting.Cmd))
            .ShouldBe("echo ^\"\"a&calc\"");
    }

    [Fact]
    public async Task ZshGetsNoBareEqualsAsAName() =>
        (await ToolPlaceholders.ExpandOneAsync("rm ${name}", OnServer("=bash"), new ScriptedDecisions(), shell: ShellQuoting.Posix))
            .ShouldBe("rm '=bash'");

    [Theory]
    [InlineData("report%20final%20.pdf", false)] // no variable called 20final
    [InlineData("100%.txt", false)]
    [InlineData("a%PATH:~0,3%b", true)]
    [InlineData("x%nope%PATH%", true)]           // %nope% unset, so cmd pairs %PATH%
    public void CmdRereadsOnlyAVariableThatIsSet(string name, bool rereads) =>
        ToolPlaceholders.CmdRereads(name).ShouldBe(rereads);

    [Fact]
    public async Task PowerShellReadsTypographicQuotesAsQuotes()
    {
        var name = new FsEntry(FsUri.Local(@"C:\work\a;b’c.txt"), "a;b’c.txt", IsDirectory: false, 1, null);
        var target = new ToolTarget(FsUri.Local(@"C:\work"), [name], name, null, null);

        // Inside the user's ‘…’ already: escaped there, not wrapped in a second pair.
        (await ToolPlaceholders.ExpandOneAsync("echo ‘${name}’", target, new ScriptedDecisions(), shell: ShellQuoting.PowerShell))
            .ShouldBe("echo ‘a;b’’c.txt’");
    }
}
