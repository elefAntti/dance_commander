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

    // ---- WSL ------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"\\wsl.localhost\Debian\home\x", "Debian", "/home/x")]
    [InlineData(@"\\WSL$\Ubuntu-22.04\etc\", "Ubuntu-22.04", "/etc")]
    [InlineData(@"\\wsl.localhost\Debian", "Debian", "/")]
    [InlineData(@"\\wsl.localhost\Debian\", "Debian", "/")]
    [InlineData(@"\\?\UNC\wsl.localhost\Debian\home\a b", "Debian", "/home/a b")]
    [InlineData("//wsl.localhost/Debian/srv", "Debian", "/srv")]
    public void APathInsideADistributionSplitsIntoItsNameAndItsLinuxPath(string path, string distribution, string linux)
    {
        WslPaths.TrySplit(path, out var foundDistribution, out var foundLinux).ShouldBeTrue();

        foundDistribution.ShouldBe(distribution);
        foundLinux.ShouldBe(linux);
    }

    [Theory]
    [InlineData(@"C:\work")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"\\wsl.localhost\")]
    [InlineData(@"\\wsl.localhost")]
    [InlineData("/home/x")]
    public void AnyOtherPathIsNotInADistribution(string path) =>
        WslPaths.TrySplit(path, out _, out _).ShouldBeFalse();

    [Fact]
    public async Task OpenInWslGoesToTheDistributionAndFolderItIsIn()
    {
        var target = new ToolTarget(FsUri.Local(@"\\wsl.localhost\Debian\home\x"), [], null, null);

        var result = await Expand(target, "-d", "${distro}", "--cd", "${dir|wsl}");

        result.ShouldBe(["-d", "Debian", "--cd", "/home/x"]);
    }

    [Fact]
    public async Task ElsewhereItIsTheDefaultDistributionAndTheFolderUnderMnt()
    {
        var target = new ToolTarget(FsUri.Local(@"C:\work"), [], null, null);
        var decisions = new ScriptedDecisions();

        var result = await ToolPlaceholders.ExpandAsync(
            ["-d", "${distro}", "--cd", "${dir|wsl}"], target, decisions, CancellationToken.None);

        // This machine's own answer: a test can't install a distribution.
        if (WslPaths.DefaultDistribution() is { } fallback)
            result.ShouldBe(["-d", fallback, "--cd", "/mnt/c/work"]);
        else
            decisions.Notices.ShouldHaveSingleItem().Detail!.ShouldContain("no default distribution");
    }

    [Fact]
    public void OnAServerThereIsNoDistribution() =>
        ToolPlaceholders.CanResolve(["${distro}"], null, FsUri.Parse("sftp://me@host/home/me")).ShouldBeFalse();

    [Theory]
    [InlineData(@"C:\work\a.sh", "/mnt/c/work/a.sh")]
    [InlineData(@"D:\", "/mnt/d")]
    [InlineData("D:", "/mnt/d")]
    [InlineData(@"\\?\C:\work\notes.", "/mnt/c/work/notes.")]
    [InlineData(@"\\wsl.localhost\Debian\home\x", "/home/x")]
    public void APathAsLinuxInWslSpellsIt(string windows, string linux) =>
        WslPaths.ToLinux(windows).ShouldBe(linux);

    [Theory]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"\\?\UNC\server\share\x")]
    [InlineData("relative")]
    [InlineData("1:\\x")]
    public void APathWslCannotReachHasNoLinuxSpelling(string windows) =>
        WslPaths.ToLinux(windows).ShouldBeNull();

    [Theory]
    [InlineData("wsl cat ${name}", ShellQuoting.Posix)]
    [InlineData("wsl.exe -e cat ${path|wsl}", ShellQuoting.PowerShell)]
    [InlineData(@"C:\Windows\System32\wsl.exe cat ${name}", ShellQuoting.Cmd)]
    [InlineData("git log && wsl cat ${name}", ShellQuoting.Posix)]
    public async Task ATypedLineThatRunsWslRefusesANameTheLinuxShellWouldRun(string line, ShellQuoting shell)
    {
        var decisions = new ScriptedDecisions();

        var result = await ToolPlaceholders.ExpandOneAsync(line, Target("a;touch x.txt"), decisions, shell: shell);

        result.ShouldBeNull();
        decisions.Notices.ShouldHaveSingleItem().Message.ShouldContain("runs wsl");
    }

    [Theory]
    [InlineData("wsl rm -rf ${name}", "~", ShellQuoting.Posix)] // the Linux home
    [InlineData("wsl rm ${name}", "file[1].txt", ShellQuoting.Posix)] // file1.txt
    [InlineData("wsl rm ${name}", "{old,keep}.txt", ShellQuoting.Posix)] // two files
    [InlineData("wsl cp ${name} /backup", "#notes", ShellQuoting.Posix)]
    [InlineData("wsl bash -c \"rm -f ${name}\"", "notes important.txt", ShellQuoting.Posix)]
    [InlineData("wsl --exec sh -lc 'cat ${name}'", "a b.txt", ShellQuoting.PowerShell)]
    [InlineData("bash -c 'cat ${name}'", "a;b.txt", ShellQuoting.PowerShell)] // System32's bash
    [InlineData("bash.exe -c \"cat ${name}\"", "a;b.txt", ShellQuoting.Cmd)]
    public async Task ANameTheLinuxShellWouldReadAsMoreIsRefused(string line, string name, ShellQuoting shell)
    {
        var decisions = new ScriptedDecisions();

        (await ToolPlaceholders.ExpandOneAsync(line, Target(name), decisions, shell: shell)).ShouldBeNull();
        decisions.Notices.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("wsl cat ${name}", "Copy 2.txt")] // a space only splits; nothing runs
    [InlineData("wsl --cd \"${name}\" ls", "a b")] // quoted for wsl's own option, not a shell's line
    [InlineData("wsl ls ${name}", "{5F3A0C}")] // a GUID's braces don't expand
    [InlineData("wsl ls ${name}", "notes~1.txt")]
    [InlineData("cat ${name}", "a;touch x.txt")] // no wsl: bash's own quoting holds
    [InlineData("wslview ${name}", "a;touch x.txt")]
    [InlineData(@"type \\wsl.localhost\Ubuntu\${name}", "a;touch x.txt")]
    public async Task OtherwiseTheTypedLineRunsAsBefore(string line, string name) =>
        (await ToolPlaceholders.ExpandOneAsync(line, Target(name), new ScriptedDecisions(), shell: ShellQuoting.Posix))
            .ShouldNotBeNull();

    [Fact]
    public async Task TheFilterGivesAFileItsLinuxPath() =>
        (await Expand(Target("a b.sh"), "${path|wsl}")).ShouldBe(["/mnt/c/work/src/a b.sh"]);

    [Fact]
    public async Task ExpandingSaysWhichArgumentsHoldValuesFromOutside()
    {
        var outside = new List<bool>();

        await ToolPlaceholders.ExpandAsync(
            ["--exec", "${path}", "plain", "${env:PATH}", "${selection}"],
            Target("a.txt", "b.txt"), new ScriptedDecisions(), CancellationToken.None, outside);

        outside.ShouldBe([false, true, false, false, true, true]);
    }
}

public class WslArgumentsTests
{
    private static string? Check(string[] arguments, params int[] outside) =>
        WslArguments.UnsafeForShell(arguments, arguments.Select((_, i) => outside.Contains(i)).ToList())?.Value;

    [Theory]
    [InlineData("bash", "-c")]
    [InlineData("/bin/sh", "-c")]
    [InlineData("bash", "-lc")]
    [InlineData("zsh", "-ec")]
    public void AShellsLineAfterExecHoldsNoName(string shell, string flag)
    {
        // The notice sends people to --exec; "--exec bash -c 'cat <name>'" must not then pass.
        var refusal = WslArguments.UnsafeForShell(
            ["--exec", shell, flag, "cat /mnt/c/a.sh"], [false, false, false, true]);

        refusal.ShouldNotBeNull().InShellLine.ShouldBeTrue();
    }

    [Fact]
    public void ANameAfterTheShellsLineIsADollarOne() =>
        Check(["--exec", "bash", "-c", "cat \"$1\"", "_", "/mnt/c/a;b.sh"], 5).ShouldBeNull();

    [Fact]
    public void EnvBeforeTheShellIsSeenThrough() =>
        Check(["-e", "env", "LANG=C", "sh", "-c", "cat x"], 5).ShouldBe("cat x");

    [Theory]
    [InlineData("/mnt/c/a;b.sh")]
    [InlineData("/mnt/c/$(rm -rf ~).sh")]
    [InlineData("/mnt/c/a b.sh")]
    [InlineData("/mnt/c/`x`.sh")]
    [InlineData("/mnt/c/a'b.sh")]
    [InlineData("/mnt/c/a\nb.sh")]
    [InlineData("=bash")]
    public void ANameTheShellWouldReadAsMoreThanAWordIsRefused(string name) =>
        Check(["bash", name], 1).ShouldBe(name);

    [Fact]
    public void AfterExecEveryArgumentReachesTheProgramAsItIs() =>
        Check(["-d", "Debian", "--exec", "bash", "/mnt/c/a;b.sh"], 4).ShouldBeNull();

    [Fact]
    public void WslsOwnOptionsAreReadByItNotTheShell() =>
        Check(["-d", "Debian", "--cd", "/mnt/c/Program Files (x86)"], 1, 3).ShouldBeNull();

    [Fact]
    public void AfterADoubleDashItIsTheShellsLine() =>
        Check(["--cd", "/home", "--", "cat", "a;b"], 4).ShouldBe("a;b");

    [Fact]
    public void TheUsersOwnTextIsTheirsToWrite() =>
        Check(["bash", "-c", "ls | grep x"]).ShouldBeNull();

    [Fact]
    public void APlainNameIsLetThrough() =>
        Check(["bash", "/mnt/c/work/build-all.sh", "/home/mé/x"], 1, 2).ShouldBeNull();

    [Fact]
    public void AValueWhereAnOptionGoesIsRefused() =>
        Check(["-e", "x"], 0).ShouldBe("-e");

    [Theory]
    [InlineData(@"C:\Windows\System32\wsl.exe", true)]
    [InlineData("wsl", true)]
    [InlineData("WSL.EXE", true)]
    [InlineData("wslg.exe", true)]
    [InlineData(@"%windir%\System32\wsl.exe", true)]
    [InlineData(@"C:\Windows\System32\bash.exe", true)]
    [InlineData(@"C:\Program Files\Git\bin\bash.exe", false)]
    [InlineData("pwsh.exe", false)]
    public void KnowsWslByItsName(string exec, bool isWsl) =>
        WslArguments.IsWsl(exec).ShouldBe(isWsl);
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
