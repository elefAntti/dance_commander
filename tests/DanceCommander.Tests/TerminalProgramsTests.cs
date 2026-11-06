using DanceCommander.Extensibility;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>Which typed lines are handed to a real terminal instead of run in place.</summary>
public class TerminalProgramsTests
{
    [Theory]
    [InlineData("vim notes.txt", "vim")]
    [InlineData("top", "top")]
    [InlineData("ssh build-server", "ssh")]
    [InlineData("python", "python")]
    [InlineData("/usr/bin/less log.txt", "less")]
    [InlineData("vim 'C:\\work\\a b.txt'", "vim")]
    public void InteractiveProgramsAreOfferedATerminal(string line, string program) =>
        TerminalPrograms.NeedsTerminal(line).ShouldBe(program);

    [Theory]
    [InlineData("python build.py")] // a script, not the REPL
    [InlineData("git log | less")] // someone else's decision to make
    [InlineData("ls; vim x")]
    [InlineData("cat vim.txt")]
    [InlineData("./not_vim.sh")]
    [InlineData("")]
    public void EverythingElseRunsInPlace(string line) =>
        TerminalPrograms.NeedsTerminal(line).ShouldBeNull();
}
