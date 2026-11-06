using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Matching;
using DanceCommander.Extensibility;
using Shouldly;

namespace DanceCommander.Tests;

public class MatcherTests
{
    private static FsEntry Entry(
        string name, bool directory = false, long size = 0, DateTimeOffset? modified = null) =>
        new(FsUri.Local(@"C:\work").Child(name), name, directory, size, modified);

    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "Program.CS", true)]          // Windows names are case-insensitive
    [InlineData("*.{cs,xaml}", "Shell.xaml", true)]
    [InlineData("*.cs", "notes.md", false)]
    [InlineData("{bin,obj}", "obj", true)]
    public void MatchesGlobs(string glob, string name, bool expected) =>
        CompiledMatcher.Compile(new MatchRule { Glob = glob }).Matches(Entry(name)).ShouldBe(expected);

    [Fact]
    public void RegexCaseSensitivityIsLeftToThePattern()
    {
        CompiledMatcher.Compile(new MatchRule { Regex = "secret" }).Matches(Entry("SECRET.txt")).ShouldBeFalse();
        CompiledMatcher.Compile(new MatchRule { Regex = "(?i)secret" }).Matches(Entry("SECRET.txt")).ShouldBeTrue();
    }

    [Fact]
    public void CombinesConditionsWithAnd()
    {
        var rule = new MatchRule { Glob = "*.log", MinSize = "1kB" };
        var matcher = CompiledMatcher.Compile(rule);

        matcher.Matches(Entry("big.log", size: 4096)).ShouldBeTrue();
        matcher.Matches(Entry("small.log", size: 10)).ShouldBeFalse();
        matcher.Matches(Entry("big.txt", size: 4096)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("512", 512L)]
    [InlineData("1kB", 1024L)]
    [InlineData("2MB", 2097152L)]
    [InlineData("1.5GB", 1610612736L)]
    [InlineData("nonsense", null)]
    public void ParsesHumanSizes(string text, long? expected) =>
        CompiledMatcher.ParseSize(text).ShouldBe(expected);

    [Fact]
    public void FiltersByAge()
    {
        var old = Entry("ancient.log", modified: DateTimeOffset.Now.AddDays(-40));
        var fresh = Entry("today.log", modified: DateTimeOffset.Now.AddMinutes(-5));

        var stale = CompiledMatcher.Compile(new MatchRule { OlderThan = "30d" });
        stale.Matches(old).ShouldBeTrue();
        stale.Matches(fresh).ShouldBeFalse();

        var recent = CompiledMatcher.Compile(new MatchRule { NewerThan = "1h" });
        recent.Matches(fresh).ShouldBeTrue();
        recent.Matches(old).ShouldBeFalse();
    }

    [Fact]
    public void SeparatesFilesFromDirectories()
    {
        var dirs = CompiledMatcher.Compile(new MatchRule { DirectoriesOnly = true });
        dirs.Matches(Entry("src", directory: true)).ShouldBeTrue();
        dirs.Matches(Entry("a.txt")).ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyRuleMatchesEverything() =>
        CompiledMatcher.Compile(null).Matches(Entry("anything")).ShouldBeTrue();
}

public class ToolPlaceholderTests
{
    private static readonly FsUri Dir = FsUri.Local(@"C:\work\src");

    private static FsEntry File(string name) =>
        new(Dir.Child(name), name, IsDirectory: false, 10, null);

    private static ToolTarget Target(params string[] names) =>
        new(Dir, names.Select(File).ToList(), names.Length > 0 ? File(names[0]) : null,
            FsUri.Local(@"C:\work\out"));

    private static Task<List<string>?> Expand(ToolTarget target, params string[] args) =>
        ToolPlaceholders.ExpandAsync(args, target, new ScriptedDecisions(), CancellationToken.None);

    [Fact]
    public async Task ExpandsTheUsualPlaceholders()
    {
        var result = await Expand(Target("Program.cs"),
            "${path}", "${dir}", "${name}", "${stem}", "${ext}", "${otherPaneDir}");

        result.ShouldBe([
            @"C:\work\src\Program.cs",
            @"C:\work\src",
            "Program.cs",
            "Program",
            "cs",
            @"C:\work\out",
        ]);
    }

    [Fact]
    public async Task SelectionBecomesOneArgumentPerItem()
    {
        var result = await Expand(Target("a.txt", "b.txt"), "--files", "${selection}");

        // Three argv slots, not a string that a shell would have to re-split.
        result.ShouldBe([@"--files", @"C:\work\src\a.txt", @"C:\work\src\b.txt"]);
    }

    [Fact]
    public async Task AFileNameCanNeverBecomePartOfTheCommand()
    {
        // The whole point of expanding to argv (§6.6): this name is hostile in a shell string and
        // completely inert as one array slot.
        var hostile = "report\" & del *.* & echo .txt";
        var target = new ToolTarget(Dir, [File(hostile)], File(hostile), null);

        var result = await Expand(target, "${path}");

        result.ShouldHaveSingleItem().ShouldBe(@"C:\work\src\" + hostile);
    }

    [Fact]
    public async Task SubstitutesInsideALargerArgument()
    {
        var result = await Expand(Target("a.txt"), "--out=${dir}\\build");
        result.ShouldHaveSingleItem().ShouldBe(@"--out=C:\work\src\build");
    }

    [Fact]
    public async Task ReadsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("DC_TEST_TOOL", "hello");
        try
        {
            var result = await Expand(Target("a.txt"), "${env:DC_TEST_TOOL}");
            result.ShouldHaveSingleItem().ShouldBe("hello");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DC_TEST_TOOL", null);
        }
    }

    [Fact]
    public async Task AsksTheUserForAPromptPlaceholderAndOnlyOncePerLabel()
    {
        var decisions = new ScriptedDecisions(text: "release");
        var result = await ToolPlaceholders.ExpandAsync(
            ["--tag", "${prompt:Tag}", "--also", "${prompt:Tag}"],
            Target("a.txt"), decisions, CancellationToken.None);

        result.ShouldBe(["--tag", "release", "--also", "release"]);
    }

    [Fact]
    public async Task CancellingAPromptAbandonsTheWholeExpansion()
    {
        var result = await ToolPlaceholders.ExpandAsync(
            ["${prompt:Tag}"], Target("a.txt"), new ScriptedDecisions(text: null), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task UnknownPlaceholdersBecomeEmptyRatherThanLeakingTheirOwnText()
    {
        var result = await Expand(Target("a.txt"), "${nonsense}");
        result.ShouldHaveSingleItem().ShouldBe(string.Empty);
    }

    // ---- location tokens (§6.6) -----------------------------------------------------------

    private static readonly RepositoryInfo Repo =
        new(FsUri.Local(@"C:\work"), "feature/ABC-123-thing", "git@example.com:team/work.git");

    private static ToolTarget InRepo(RepositoryInfo? repository, params string[] names) =>
        Target(names) with { Repository = repository };

    [Fact]
    public async Task ExpandsTheLocationTokens()
    {
        var result = await Expand(InRepo(Repo, "Program.cs"), "${repoRoot}", "${branch}", "${remoteUrl}");

        result.ShouldBe([@"C:\work", "feature/ABC-123-thing", "git@example.com:team/work.git"]);
    }

    [Fact]
    public async Task TheRepositoryIsTheSameFromANestedFile()
    {
        // The open issue this closes: the entry is deep inside, and ${repoRoot} still answers the
        // question about the repository rather than about the entry's own folder.
        var deep = FsUri.Local(@"C:\work\src\App\Views");
        var target = new ToolTarget(deep, [], null, null, Repo);

        var result = await Expand(target, "--workspace-folder", "${repoRoot}");

        result.ShouldBe(["--workspace-folder", @"C:\work"]);
    }

    [Theory]
    [InlineData("${branch}")]
    [InlineData("${repoRoot}")]
    [InlineData("--at=${remoteUrl}")]
    public async Task ALocationTokenOutsideARepositoryAbandonsTheExpansionAndSaysWhy(string argument)
    {
        var decisions = new ScriptedDecisions();

        var result = await ToolPlaceholders.ExpandAsync(
            ["first", argument], InRepo(null, "a.txt"), decisions, CancellationToken.None);

        // Not an empty argv slot: that would reach the child process as "" and fail inside it.
        result.ShouldBeNull();
        decisions.Notices.ShouldHaveSingleItem().Severity.ShouldBe(NoticeSeverity.Warning);
    }

    [Fact]
    public async Task ADetachedHeadHasNoBranch()
    {
        var detached = Repo with { Branch = null };
        var decisions = new ScriptedDecisions();

        (await ToolPlaceholders.ExpandAsync(["${branch}"], InRepo(detached), decisions, CancellationToken.None))
            .ShouldBeNull();
        decisions.Notices.ShouldHaveSingleItem().Detail!.ShouldContain("detached");

        // ...while the rest of the repository still answers.
        (await Expand(InRepo(detached), "${repoRoot}")).ShouldBe([@"C:\work"]);
    }

    [Fact]
    public async Task AnUnresolvableTokenIsCaughtBeforeAnythingIsAsked()
    {
        // A prompt answered and then thrown away because a later argument could not be filled in
        // is the user's time wasted on a command that was never going to run.
        var decisions = new ScriptedDecisions(text: "release");

        var result = await ToolPlaceholders.ExpandAsync(
            ["${prompt:Tag}", "${branch}"], InRepo(null), decisions, CancellationToken.None);

        result.ShouldBeNull();
        decisions.AskedText.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(new[] { "code", "${repoRoot}" }, false)]
    [InlineData(new[] { "code", "${path}" }, true)]
    [InlineData(new[] { "echo", "${BRANCH}" }, false)] // tokens are case-insensitive, like the rest
    public void CanResolveAsksOnlyAboutLocationTokens(string[] templates, bool outsideARepository)
    {
        ToolPlaceholders.CanResolve(templates, null).ShouldBe(outsideARepository);
        ToolPlaceholders.CanResolve(templates, Repo).ShouldBeTrue();
    }
}

public class ToolDefinitionTests
{
    [Theory]
    [InlineData(null, ToolScope.Default)]
    [InlineData("file", ToolScope.File)]
    [InlineData("directory|background", ToolScope.Directory | ToolScope.Background)]
    [InlineData("file|multi", ToolScope.File | ToolScope.Multi)]
    [InlineData("gibberish", ToolScope.Default)]
    public void ParsesScopes(string? appliesTo, ToolScope expected) =>
        new ToolDefinition { AppliesTo = appliesTo }.ParsedScope().ShouldBe(expected);

    [Fact]
    public void DefaultsToLocalFilesOnly() =>
        new ToolDefinition().ParsedSchemes().ShouldBe(["file"]);

    [Fact]
    public void ShipsAWorkingOpenInBash()
    {
        var bash = ToolPacks.GitBash.Tools(@"C:\Program Files\Git\bin\bash.exe").Single(t => t.Id == "open-in-bash");

        bash.Exec.ShouldEndWith("bash.exe");
        bash.ParsedLaunch().ShouldBe(ToolLaunch.Console); // a console program needs a console
        bash.WorkingDirectory.ShouldBe("${dir}");
        bash.ParsedScope().ShouldBe(ToolScope.Directory | ToolScope.Background);
    }
}
