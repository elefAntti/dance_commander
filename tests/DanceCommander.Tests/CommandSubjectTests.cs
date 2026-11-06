using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Extensibility;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>What a command acts on, and what that decides (§6.18).</summary>
public class CommandSubjectTests
{
    private static readonly FsUri Dir = FsUri.Local(@"C:\work");

    private static FakeContext Context(params FsEntry[] selection) =>
        new() { Directory = Dir, Selection = selection, Repository = new(Dir, "main", null) };

    private static FsEntry File(string name) => new(Dir.Child(name), name, IsDirectory: false, 1, null);

    private static CommandDefinition Command(CommandSubject subject, CommandScope scope) => new()
    {
        Id = "test",
        Title = "Test",
        Subject = subject,
        Scope = scope,
        ExecuteAsync = (_, _) => Task.CompletedTask,
    };

    [Fact]
    public void AFolderCommandIsOfferedWhateverIsSelected()
    {
        // The bug this model exists for: Worktrees vanished whenever a row had focus, because its
        // applicability was read off the selection it does not act on.
        var worktrees = Command(CommandSubject.Location, CommandScope.Background);

        worktrees.IsApplicable(Context()).ShouldBeTrue();
        worktrees.IsApplicable(Context(File("a.txt"))).ShouldBeTrue();
        worktrees.IsApplicable(Context(File("a.txt"), File("b.txt"))).ShouldBeTrue();
    }

    [Fact]
    public void ASelectionCommandStillMindsItsScope()
    {
        var rename = Command(CommandSubject.Selection, CommandScope.File | CommandScope.Directory);

        rename.IsApplicable(Context()).ShouldBeFalse();
        rename.IsApplicable(Context(File("a.txt"))).ShouldBeTrue();
        rename.IsApplicable(Context(File("a.txt"), File("b.txt"))).ShouldBeFalse();
    }

    [Theory]
    // What the tool reads decides it...
    [InlineData(null, new[] { "--cd", "${dir}" }, "location")]          // Open in WSL
    [InlineData(null, new[] { "${repoRoot}" }, "location")]              // Open repository in VS Code
    [InlineData(null, new[] { "${selection}" }, "selection")]            // Open with VS Code
    [InlineData("file|multi", new[] { "-hashfile", "${path}" }, "selection")]
    // ...including "Open in bash", which was offered on a selected folder but opens ${dir}.
    [InlineData("directory|background", new[] { "--login", "-i" }, "location")]
    // With nothing read, an appliesTo that leaves out empty space still means "rows".
    [InlineData("file", new[] { "--version" }, "selection")]
    [InlineData(null, new[] { "--version" }, "location")]
    public void InfersWhatAToolActsOnFromWhatItReads(string? appliesTo, string[] args, string expected)
    {
        var tool = new ToolDefinition { AppliesTo = appliesTo, Exec = "x", Args = [.. args], WorkingDirectory = "${dir}" };

        tool.ParsedSubject().ShouldBe(expected == "location" ? CommandSubject.Location : CommandSubject.Selection);
    }

    [Fact]
    public void ASayingSoOverridesTheInference()
    {
        new ToolDefinition { Exec = "x", Args = ["${path}"], Subject = "location" }
            .ParsedSubject().ShouldBe(CommandSubject.Location);
        new ToolDefinition { Exec = "x", Args = ["${dir}"], Subject = "selection" }
            .ParsedSubject().ShouldBe(CommandSubject.Selection);
    }

    [Fact]
    public void TheWorkingDirectoryCountsAsSomethingTheToolReads()
    {
        // "Open bash *in* the selected folder" is written by pointing the working directory at it.
        new ToolDefinition { Exec = "bash", WorkingDirectory = "${path}", AppliesTo = "directory|background" }
            .ParsedSubject().ShouldBe(CommandSubject.Selection);
    }

    [Fact]
    public void TheShippedDefaultsSortThemselvesAsIntended()
    {
        var subjects = ToolPacks.All.SelectMany(p => p.Sample).Concat(ToolCatalog.Defaults().Tools)
            .ToDictionary(t => t.Id, t => t.ParsedSubject());

        subjects["open-in-bash"].ShouldBe(CommandSubject.Location);
        subjects["open-in-terminal"].ShouldBe(CommandSubject.Location);
        subjects["vscode"].ShouldBe(CommandSubject.Selection);
        subjects["vscode-folder"].ShouldBe(CommandSubject.Location);
        subjects["vscode-repository"].ShouldBe(CommandSubject.Location);
        subjects["devcontainer-build"].ShouldBe(CommandSubject.Location);
        subjects["sha256"].ShouldBe(CommandSubject.Selection);
    }
}
