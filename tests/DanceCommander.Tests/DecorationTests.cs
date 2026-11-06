using DanceCommander.Core.Config;
using DanceCommander.Core.Decorations;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Matching;
using DanceCommander.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

internal sealed class FakeRow(FsEntry entry) : IRowDecorationSink
{
    public FsEntry Entry { get; } = entry;

    public HighlightStyle? Highlight { get; set; }

    public string? Badge { get; set; }

    public RowMarker? Marker { get; set; }

    public int SortGroup { get; set; }
}

public sealed class HighlightDecoratorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-hl-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private HighlightDecorator Build(params HighlightRule[] rules)
    {
        var store = new JsonConfigStore<HighlightingFile>(_root, "highlighting.json", NullLogger.Instance);
        store.SaveAsync(new HighlightingFile { Rules = rules.ToList() }).GetAwaiter().GetResult();
        return new HighlightDecorator(store);
    }

    private static FakeRow Row(string name, bool directory = false, long size = 0) =>
        new(new FsEntry(FsUri.Local(@"C:\work").Child(name), name, directory, size, null));

    private static async Task<List<FakeRow>> Decorate(HighlightDecorator decorator, params FakeRow[] rows)
    {
        await decorator.DecorateAsync(rows.Cast<IRowDecorationSink>().ToList(),
            FsUri.Local(@"C:\work"), CancellationToken.None);
        return rows.ToList();
    }

    [Fact]
    public async Task AppliesAMatchingRulesStyle()
    {
        var decorator = Build(new HighlightRule
        {
            Match = new MatchRule { Glob = "*.cs" },
            Foreground = "#7FC8FF",
            Bold = true,
        });

        var rows = await Decorate(decorator, Row("Program.cs"), Row("notes.md"));

        rows[0].Highlight.ShouldBe(new HighlightStyle("#7FC8FF", null, true, false));
        rows[1].Highlight.ShouldBeNull();
    }

    [Fact]
    public async Task FirstMatchWinsSoTheFileReadsAsAPriorityList()
    {
        var decorator = Build(
            new HighlightRule { Match = new MatchRule { Glob = "*.cs" }, Foreground = "#111111" },
            new HighlightRule { Match = new MatchRule { Glob = "*" }, Foreground = "#222222" });

        var rows = await Decorate(decorator, Row("Program.cs"));

        rows[0].Highlight!.Foreground.ShouldBe("#111111");
    }

    [Fact]
    public async Task DisabledRulesAreIgnored()
    {
        var decorator = Build(new HighlightRule
        {
            Match = new MatchRule { Glob = "*" },
            Foreground = "#111111",
            Enabled = false,
        });

        (await Decorate(decorator, Row("anything.txt")))[0].Highlight.ShouldBeNull();
    }

    [Fact]
    public async Task ARuleCanPinItsMatchesAboveTheNormalOrdering()
    {
        var decorator = Build(new HighlightRule
        {
            Match = new MatchRule { Glob = "readme*" },
            SortGroup = 10,
        });

        var rows = await Decorate(decorator, Row("readme.md"), Row("zebra.txt"));

        rows[0].SortGroup.ShouldBe(10);
        rows[1].SortGroup.ShouldBe(0);
    }

    [Fact]
    public void ShipsDefaultsThatAllCompile()
    {
        foreach (var rule in HighlightDecorator.Defaults().Rules)
            Should.NotThrow(() => CompiledMatcher.Compile(rule.Match));
    }

    [Fact]
    public void ShipsEveryDefaultSwitchedOff()
    {
        // A fresh install has a plain listing; colour is the user's choice to make.
        HighlightDecorator.Defaults().Rules.ShouldAllBe(rule => !rule.Enabled);
    }
}

/// <summary>Against real repositories on disk, because the point is reading git's own files.</summary>
public sealed class GitServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-git-").FullName;
    private readonly GitService _git = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeRepo(string name, string headContent)
    {
        var repo = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), headContent);
        return repo;
    }

    [Fact]
    public void ReadsTheCheckedOutBranch()
    {
        var repo = MakeRepo("app", "ref: refs/heads/main\n");

        var info = _git.Describe(FsUri.Local(repo)).ShouldNotBeNull();
        info.Branch.ShouldBe("main");
        info.Detached.ShouldBeFalse();
        info.Label.ShouldBe("main");
        info.WorkTreeRoot.ShouldBe(repo);
    }

    [Fact]
    public void KeepsSlashesInBranchNames()
    {
        var repo = MakeRepo("app", "ref: refs/heads/feature/ORB-11047-lazy-listing\n");

        _git.Describe(FsUri.Local(repo))!.Branch.ShouldBe("feature/ORB-11047-lazy-listing");
    }

    [Fact]
    public void ReportsADetachedHeadAsAShortSha()
    {
        var repo = MakeRepo("app", "9fceb02d0ae598e95dc970b74767f19372d61af8\n");

        var info = _git.Describe(FsUri.Local(repo)).ShouldNotBeNull();
        info.Detached.ShouldBeTrue();
        info.Branch.ShouldBe("9fceb02");
        info.Label.ShouldBe("detached 9fceb02");
    }

    [Fact]
    public void FindsTheRepositoryFromASubdirectory()
    {
        var repo = MakeRepo("app", "ref: refs/heads/develop\n");
        var nested = Path.Combine(repo, "src", "deep");
        Directory.CreateDirectory(nested);

        var info = _git.Describe(FsUri.Local(nested)).ShouldNotBeNull();
        info.Branch.ShouldBe("develop");
        info.WorkTreeRoot.ShouldBe(repo); // the root, not the subdirectory
    }

    [Fact]
    public void FollowsTheGitdirFileOfAWorktreeOrSubmodule()
    {
        // A worktree has a .git *file* pointing at the real git directory.
        var real = Path.Combine(_root, "real-git-dir");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "HEAD"), "ref: refs/heads/worktree-branch\n");

        var tree = Path.Combine(_root, "tree");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, ".git"), $"gitdir: {real}\n");

        var info = _git.Describe(FsUri.Local(tree)).ShouldNotBeNull();
        info.Branch.ShouldBe("worktree-branch");
        info.GitDirectory.ShouldBe(real);
    }

    [Fact]
    public void ResolvesARelativeGitdir()
    {
        var real = Path.Combine(_root, "shared");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "HEAD"), "ref: refs/heads/sub\n");

        var tree = Path.Combine(_root, "module");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: ../shared\n");

        _git.Describe(FsUri.Local(tree))!.Branch.ShouldBe("sub");
    }

    [Fact]
    public void ReturnsNothingOutsideARepository() =>
        _git.Describe(FsUri.Local(_root)).ShouldBeNull();

    [Fact]
    public void ReturnsNothingForRemoteLocations() =>
        _git.Describe(FsUri.Remote("sftp", "host", "/var/log")).ShouldBeNull();

    [Fact]
    public void InvalidatingPicksUpABranchSwitch()
    {
        var repo = MakeRepo("app", "ref: refs/heads/main\n");
        _git.Describe(FsUri.Local(repo))!.Branch.ShouldBe("main");

        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/experiment\n");
        _git.Describe(FsUri.Local(repo))!.Branch.ShouldBe("main"); // still cached

        _git.Invalidate(Path.Combine(repo, ".git"));
        _git.Describe(FsUri.Local(repo))!.Branch.ShouldBe("experiment");
    }

    // ---- remote URL, for ${remoteUrl} (§6.6) ----------------------------------------------

    private string MakeRepoWithConfig(string head, string config)
    {
        var repo = MakeRepo("app", head);
        File.WriteAllText(Path.Combine(repo, ".git", "config"), config);
        return repo;
    }

    [Fact]
    public void ReadsOriginsUrl()
    {
        var repo = MakeRepoWithConfig("ref: refs/heads/main\n", """
            [core]
            	bare = false
            [remote "origin"]
            	url = git@gitlab.example.com:team/app.git
            	fetch = +refs/heads/*:refs/remotes/origin/*
            """);

        _git.Describe(FsUri.Local(repo))!.RemoteUrl.ShouldBe("git@gitlab.example.com:team/app.git");
    }

    [Fact]
    public void PrefersTheRemoteTheBranchTracksOverOrigin()
    {
        // A fork: origin is yours, but this branch pulls from upstream, and upstream is where its
        // pipelines run.
        var repo = MakeRepoWithConfig("ref: refs/heads/feature/x\n", """
            [remote "origin"]
            	url = https://example.com/me/app.git
            [remote "upstream"]
            	url = https://example.com/team/app.git
            [branch "feature/x"]
            	remote = upstream
            	merge = refs/heads/main
            """);

        _git.Describe(FsUri.Local(repo))!.RemoteUrl.ShouldBe("https://example.com/team/app.git");
    }

    [Fact]
    public void FallsBackToTheOnlyRemoteWhenNoneIsCalledOrigin()
    {
        var repo = MakeRepoWithConfig("ref: refs/heads/main\n", """
            [remote "gitlab"]
            	url = "https://example.com/team/app.git" # quoted, with a comment
            """);

        _git.Describe(FsUri.Local(repo))!.RemoteUrl.ShouldBe("https://example.com/team/app.git");
    }

    [Fact]
    public void HasNoRemoteUrlWhenItCannotTellWhichIsMeant()
    {
        var repo = MakeRepoWithConfig("ref: refs/heads/main\n", """
            [remote "one"]
            	url = https://example.com/one.git
            [remote "two"]
            	url = https://example.com/two.git
            """);

        _git.Describe(FsUri.Local(repo))!.RemoteUrl.ShouldBeNull();
    }

    [Fact]
    public void HasNoRemoteUrlWithoutAConfig() =>
        _git.Describe(FsUri.Local(MakeRepo("app", "ref: refs/heads/main\n")))!.RemoteUrl.ShouldBeNull();

    [Fact]
    public void ALinkedWorktreeReadsTheMainRepositorysConfig()
    {
        // git worktree add: the worktree's git directory has its own HEAD, and a commondir file
        // pointing back at the repository whose config it shares.
        var main = MakeRepoWithConfig("ref: refs/heads/main\n", """
            [remote "origin"]
            	url = https://example.com/team/app.git
            """);
        var linked = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(linked);
        File.WriteAllText(Path.Combine(linked, "HEAD"), "ref: refs/heads/wt\n");
        File.WriteAllText(Path.Combine(linked, "commondir"), "../..\n");

        var tree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, ".git"), $"gitdir: {linked}\n");

        var info = _git.Describe(FsUri.Local(tree)).ShouldNotBeNull();
        info.Branch.ShouldBe("wt");
        info.RemoteUrl.ShouldBe("https://example.com/team/app.git");
    }
}

public sealed class GitDecoratorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-gitdec-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FakeRow Folder(string name) =>
        new(new FsEntry(FsUri.Local(Path.Combine(_root, name)), name, IsDirectory: true, 0, null));

    [Fact]
    public async Task ARepositoryFolderGetsItsBranchAndAPlainOneDoesNot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo", ".git"));
        File.WriteAllText(Path.Combine(_root, "repo", ".git", "HEAD"), "ref: refs/heads/feature/x\n");
        Directory.CreateDirectory(Path.Combine(_root, "plain"));

        var rows = new[] { Folder("repo"), Folder("plain") };
        await new GitDecorator(new GitService()).DecorateAsync(rows, FsUri.Local(_root), CancellationToken.None);

        rows[0].Badge.ShouldBe("feature/x");
        rows[1].Badge.ShouldBeNull();
    }

    [Fact]
    public async Task AFolderOfThousandsOfFoldersIsNotProbed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo", ".git"));
        File.WriteAllText(Path.Combine(_root, "repo", ".git", "HEAD"), "ref: refs/heads/main\n");

        // The rows need not exist on disk beyond the one repository: past the cap, nothing is asked.
        var rows = Enumerable.Range(0, GitDecorator.MaxProbed).Select(i => Folder($"f{i}")).Prepend(Folder("repo")).ToList();
        await new GitDecorator(new GitService()).DecorateAsync(rows, FsUri.Local(_root), CancellationToken.None);

        rows[0].Badge.ShouldBeNull();
    }
}
