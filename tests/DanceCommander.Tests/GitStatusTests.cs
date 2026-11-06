using DanceCommander.Core.Decorations;
using DanceCommander.Core.Fs;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Parsing <c>git status --porcelain=v1 -z</c>. The fixtures are real git output, NULs
/// and all, because every bug this could have is in the shape of the records rather than the logic.
/// </summary>
public sealed class GitStatusParsingTests
{
    private static GitStatus Parse(params string[] records) =>
        GitStatusService.Parse(string.Concat(records.Select(r => r + "\0")));

    [Fact]
    public void AModifiedFileIsModified() =>
        Parse(" M src/App.cs").For("src/App.cs").ShouldBe(GitState.Modified);

    [Fact]
    public void StagedAndUnstagedAreBothModified()
    {
        Parse("M  src/App.cs").For("src/App.cs").ShouldBe(GitState.Modified);
        Parse("MM src/App.cs").For("src/App.cs").ShouldBe(GitState.Modified);
    }

    [Fact]
    public void AnAddedFileIsAdded() =>
        Parse("A  src/New.cs").For("src/New.cs").ShouldBe(GitState.Added);

    [Fact]
    public void ADeletedFileIsDeleted()
    {
        Parse(" D src/Gone.cs").For("src/Gone.cs").ShouldBe(GitState.Deleted);
        Parse("D  src/Gone.cs").For("src/Gone.cs").ShouldBe(GitState.Deleted);
    }

    [Fact]
    public void UntrackedAndIgnoredAreDistinct()
    {
        var status = Parse("?? notes.txt", "!! bin/");

        status.For("notes.txt").ShouldBe(GitState.Untracked);
        status.For("bin").ShouldBe(GitState.Ignored);
    }

    [Fact]
    public void ADirectoryGitReportsWholeLosesItsTrailingSlash() =>
        // Git writes "obj/"; the pane looks rows up by name, which has no slash.
        Parse("!! obj/").For("obj").ShouldBe(GitState.Ignored);

    [Fact]
    public void ARenameConsumesItsOriginRecord()
    {
        // "R  new\0old\0" — the second record is where it came from, not a status of its own.
        var status = Parse("R  src/New.cs", "src/Old.cs");

        status.For("src/New.cs").ShouldBe(GitState.Renamed);
        // "src/Old.cs" must not be read as a record: its first two characters would parse as a
        // status code and invent a state for a file called "c/Old.cs".
        status.For("c/Old.cs").ShouldBe(GitState.None);
    }

    [Fact]
    public void AConflictOutranksEverything()
    {
        Parse("UU src/App.cs").For("src/App.cs").ShouldBe(GitState.Conflicted);
        Parse("AA src/App.cs").For("src/App.cs").ShouldBe(GitState.Conflicted);
        Parse("DD src/App.cs").For("src/App.cs").ShouldBe(GitState.Conflicted);
    }

    [Fact]
    public void AChangeShowsOnEveryFolderAboveIt()
    {
        // The whole point in a file manager: a repository whose one change is four levels down
        // must not look identical to a clean one.
        var status = Parse(" M src/deep/nested/App.cs");

        status.For("src").ShouldBe(GitState.Modified);
        status.For("src/deep").ShouldBe(GitState.Modified);
        status.For("src/deep/nested").ShouldBe(GitState.Modified);
    }

    [Fact]
    public void TheLoudestStateWinsOnAFolder()
    {
        var status = Parse(" M src/App.cs", "UU src/Other.cs");

        status.For("src").ShouldBe(GitState.Modified);
        status.For("src/Other.cs").ShouldBe(GitState.Conflicted);
    }

    [Fact]
    public void AWholeIgnoredFolderIsNotRolledUp()
    {
        // Git collapses "obj/" precisely so nothing inside needs looking at; marking the parent
        // as modified because of it would report a build directory as a change to the project.
        var status = Parse("!! src/obj/");

        status.For("src").ShouldBe(GitState.None);
    }

    [Fact]
    public void AnUntrackedFolderDoesNotMakeItsParentLookEdited() =>
        Parse("?? src/scratch/").For("src").ShouldBe(GitState.None);

    [Fact]
    public void APathWithASpaceSurvives() =>
        Parse(" M src/My File.cs").For("src/My File.cs").ShouldBe(GitState.Modified);

    [Fact]
    public void NothingInMeansNothingOut() =>
        GitStatusService.Parse(string.Empty).Count.ShouldBe(0);

    [Fact]
    public void AnUnknownPathIsSimplyUnchanged() =>
        Parse(" M src/App.cs").For("src/Other.cs").ShouldBe(GitState.None);
}

public sealed class GitStatusDecoratorTests
{
    [Theory]
    [InlineData(GitState.Modified, "M")]
    [InlineData(GitState.Added, "A")]
    [InlineData(GitState.Deleted, "D")]
    [InlineData(GitState.Renamed, "R")]
    [InlineData(GitState.Conflicted, "!")]
    [InlineData(GitState.Untracked, "?")]
    public void EachStateShowsGitsOwnLetter(GitState state, string glyph) =>
        GitStatusDecorator.Describe(state)!.Glyph.ShouldBe(glyph);

    [Fact]
    public void IgnoredAndUnchangedAreUnmarked()
    {
        // Most of a build tree is ignored; a column of marks saying "this is fine" would drown
        // the three rows that are not.
        GitStatusDecorator.Describe(GitState.Ignored).ShouldBeNull();
        GitStatusDecorator.Describe(GitState.None).ShouldBeNull();
    }

    [Theory]
    [InlineData(@"C:\repo", @"C:\repo\src\App.cs", "src/App.cs")]
    [InlineData(@"C:\repo\", @"C:\repo\src\App.cs", "src/App.cs")]
    [InlineData(@"C:\REPO", @"C:\repo\App.cs", "App.cs")]
    public void PathsAreRelativeToTheWorkTreeInGitsSpelling(string root, string path, string expected) =>
        GitStatusDecorator.Relative(root, path).ShouldBe(expected);

    [Fact]
    public void TheRootItselfAndAnythingOutsideItHaveNoRelativePath()
    {
        GitStatusDecorator.Relative(@"C:\repo", @"C:\repo").ShouldBeNull();
        // A flattened view can list rows from outside the repository the pane is anchored in.
        GitStatusDecorator.Relative(@"C:\repo", @"D:\elsewhere\App.cs").ShouldBeNull();
    }

    private sealed class OneRepository : IGitService
    {
        public GitInfo? Describe(FsUri directory) => new("main", false, @"C:\repo", @"C:\repo\.git");

        public void Invalidate(string gitDirectory)
        {
        }
    }

    private sealed class Answers(GitStatus? report) : IGitStatusService
    {
        public ValueTask<GitStatus?> GetAsync(string workTreeRoot, CancellationToken ct) => new(report);

        public void Invalidate(string workTreeRoot)
        {
        }
    }

    private static FakeRow MarkedRow() =>
        new(new FsEntry(FsUri.Local(@"C:\repo\page.html"), "page.html", IsDirectory: false, 1, null))
        {
            Marker = GitStatusDecorator.Describe(GitState.Modified),
        };

    private static Task Decorate(GitStatus? report, FakeRow row) =>
        new GitStatusDecorator(new OneRepository(), new Answers(report))
            .DecorateAsync([row], FsUri.Local(@"C:\repo"), CancellationToken.None).AsTask();

    [Fact]
    public async Task AFileCommittedSinceIsUnmarked()
    {
        // Clean files are absent from git's report, so "not in it" has to clear the old mark.
        var row = MarkedRow();
        await Decorate(new GitStatus(new Dictionary<string, GitState> { ["other.txt"] = GitState.Untracked }), row);
        row.Marker.ShouldBeNull();
    }

    [Fact]
    public async Task ACleanTreeClearsEveryMark()
    {
        var row = MarkedRow();
        await Decorate(GitStatus.Empty, row);
        row.Marker.ShouldBeNull();
    }

    [Fact]
    public async Task NoAnswerLeavesTheMarksAlone()
    {
        // No git, or a repository too big to wait for: what was known is still the best guess.
        var row = MarkedRow();
        await Decorate(null, row);
        row.Marker.ShouldNotBeNull();
    }

    private sealed class Asking(GitStatus? last) : IGitStatusService
    {
        public TaskCompletionSource<GitStatus?> Reply { get; } = new();

        public ValueTask<GitStatus?> GetAsync(string workTreeRoot, CancellationToken ct) => new(Reply.Task);

        public GitStatus? Last(string workTreeRoot) => last;

        public void Invalidate(string workTreeRoot)
        {
        }
    }

    private static GitStatus Says(GitState state) =>
        new(new Dictionary<string, GitState> { ["page.html"] = state });

    [Fact]
    public async Task TheLastAnswerShowsWhileGitIsAskedAgain()
    {
        // An editor's git rewrites .git/index every few seconds; each rewrite invalidates, and the
        // rows must not sit unmarked for the 2.5 s a large repository takes to answer again.
        var row = new FakeRow(MarkedRow().Entry);
        var status = new Asking(Says(GitState.Untracked));
        var decorating = new GitStatusDecorator(new OneRepository(), status)
            .DecorateAsync([row], FsUri.Local(@"C:\repo"), CancellationToken.None).AsTask();

        row.Marker.ShouldBe(GitStatusDecorator.Describe(GitState.Untracked));

        status.Reply.SetResult(Says(GitState.Modified));
        await decorating;
        row.Marker.ShouldBe(GitStatusDecorator.Describe(GitState.Modified));
    }

    [Fact]
    public async Task ALastAnswerOutlivesInvalidation()
    {
        // A real git, on a real (empty) repository: the point is what the cache keeps.
        var root = Path.Combine(Path.GetTempPath(), "dc-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var init = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -q")
                   { WorkingDirectory = root, CreateNoWindow = true, UseShellExecute = false })!)
                await init.WaitForExitAsync();
            File.WriteAllText(Path.Combine(root, "new.txt"), "x");

            var service = new GitStatusService();
            service.Last(root).ShouldBeNull();

            var answer = await service.GetAsync(root, CancellationToken.None);
            answer.ShouldNotBeNull().For("new.txt").ShouldBe(GitState.Untracked);

            service.Invalidate(root);
            service.Last(root).ShouldBeSameAs(answer);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal); // git's objects are read-only
            Directory.Delete(root, recursive: true);
        }
    }
}
