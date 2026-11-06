using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Matching;
using DanceCommander.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>Placeholder filters and the remote-URL condition (§6.6).</summary>
public class ToolConditionTests
{
    private static readonly FsUri Root = FsUri.Local(@"C:\work\orbit-worktree-2");

    private static ToolTarget InRepo(string? remote = "git@gitlab.example.com:team/orbit.git") =>
        new(Root, [], null, null, new RepositoryInfo(Root, "main", remote));

    [Fact]
    public async Task LowerDriveSpellsThePathTheWayADevContainerIsLabelled()
    {
        var result = await ToolPlaceholders.ExpandAsync(
            ["--id-label", "devcontainer.local_folder=${repoRoot|lowerDrive}"],
            InRepo(), new ScriptedDecisions(), CancellationToken.None);

        result.ShouldBe(["--id-label", @"devcontainer.local_folder=c:\work\orbit-worktree-2"]);
    }

    [Fact]
    public async Task AFilterLeavesAValueWithNoDriveAlone() =>
        (await ToolPlaceholders.ExpandAsync(
            ["${branch|lowerDrive}"], InRepo(), new ScriptedDecisions(), CancellationToken.None))
        .ShouldBe(["main"]);

    [Fact]
    public async Task AMisspeltFilterStopsTheCommandAndSaysWhichFilters()
    {
        // Passing the value through unchanged would be a lookup that almost works: exactly the
        // "Dev container not found" this filter exists to fix, with nothing to say why.
        var decisions = new ScriptedDecisions();

        var result = await ToolPlaceholders.ExpandAsync(
            ["${repoRoot|lowercase}"], InRepo(), decisions, CancellationToken.None);

        result.ShouldBeNull();
        decisions.Notices.ShouldHaveSingleItem().Detail!.ShouldContain("lowerDrive");
    }

    [Fact]
    public void AFilteredLocationTokenIsStillALocationToken()
    {
        ToolPlaceholders.CanResolve(["${repoRoot|lowerDrive}"], null).ShouldBeFalse();
        new ToolDefinition { Exec = "x", Args = ["${repoRoot|lowerDrive}"] }
            .ParsedSubject().ShouldBe(CommandSubject.Location);
        new ToolDefinition { Exec = "x", Args = ["${path|lowerDrive}"] }
            .ParsedSubject().ShouldBe(CommandSubject.Selection);
    }

    [Theory]
    [InlineData("orbit", "git@gitlab.example.com:team/orbit.git", true)]
    [InlineData("ORBIT", "https://gitlab.example.com/team/orbit.git", true)]
    // A * crosses slashes here, unlike in a file-name glob; a URL is mostly slashes.
    [InlineData("*gitlab.example.com*/team/*", "https://gitlab.example.com/team/orbit.git", true)]
    [InlineData("*/comet.git", "https://gitlab.example.com/team/orbit.git", false)]
    [InlineData("orbit", null, false)]
    public void MatchesRemoteUrls(string pattern, string? url, bool expected) =>
        TextPattern.Compile(pattern).Matches(url).ShouldBe(expected);

    // ---- the catalog, end to end --------------------------------------------------------------

    private sealed class Store(ToolsFile file) : IConfigStore<ToolsFile>
    {
        public ToolsFile Current => file;

        public event EventHandler<ToolsFile>? Changed { add { } remove { } }

        public Task SaveAsync(ToolsFile value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static FakeContext Context(RepositoryInfo? repository) =>
        new() { Directory = Root, Repository = repository };

    private static CommandRegistry Load(params ToolDefinition[] tools)
    {
        var registry = new CommandRegistry();
        new ToolCatalog(
                new Store(new ToolsFile { Tools = [.. tools] }), registry,
                new ToolRunner(new Core.Operations.ActivityLog(), new JobService(), NullLogger<ToolRunner>.Instance),
                new CommandListingCatalog(), NullLogger<ToolCatalog>.Instance)
            .Reload();
        return registry;
    }

    private static readonly ToolDefinition Release = new()
    {
        Id = "orbit-release",
        Title = "make release (devcontainer)",
        Exec = "devcontainer",
        Args = ["exec", "--id-label", "devcontainer.local_folder=${repoRoot|lowerDrive}",
                "make", "-C", "/media/sf_orbit/Make", "release", "-j6"],
        Launch = "job",
        RemoteMatches = "orbit",
    };

    [Fact]
    public void AToolForOneProjectAppearsOnlyInThatProject()
    {
        var command = Load(Release).Find("tool.orbit-release").ShouldNotBeNull();

        command.IsApplicable(Context(new RepositoryInfo(Root, "main", "git@gitlab.example.com:team/orbit.git")))
            .ShouldBeTrue();
        command.IsApplicable(Context(new RepositoryInfo(Root, "main", "git@github.com:me/dance_commander.git")))
            .ShouldBeFalse();
        command.IsApplicable(Context(new RepositoryInfo(Root, "main", RemoteUrl: null))).ShouldBeFalse();
        command.IsApplicable(Context(repository: null)).ShouldBeFalse();
    }

    [Fact]
    public void AToolWithAMisspeltFilterIsNotLoaded()
    {
        var broken = new ToolDefinition { Id = "broken", Title = "Broken", Exec = "x", Args = ["${repoRoot|lowerdrve}"] };

        Load(broken, Release).Find("tool.broken").ShouldBeNull();
        Load(broken, Release).Find("tool.orbit-release").ShouldNotBeNull(); // the rest still load
    }
}
