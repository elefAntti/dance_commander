using System.Text.RegularExpressions;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// The extraction rule, against real output. Everything that can go wrong with this feature is in
/// the shape of what a program prints, so the fixtures are verbatim.
/// </summary>
public sealed class CommandExtractionTests
{
    /// <summary>The pattern the built-in worktree listing uses.</summary>
    private const string Worktrees =
        @"^worktree (?<path>.+?)\s*?\r?\n(?:HEAD \S+\r?\n)?(?:branch refs/heads/(?<branch>\S+))?";

    private static List<(string Path, string Branch)> Rows(string pattern, string output) =>
        new Regex(pattern, RegexOptions.Multiline)
            .Matches(output)
            .Select(m => (m.Groups["path"].Value, m.Groups["branch"].Value))
            .ToList();

    [Fact]
    public void ReadsGitsPorcelainWorktreeList()
    {
        // Verbatim from "git worktree list --porcelain", blank line between stanzas and all.
        var output =
            "worktree C:/work/repo\nHEAD cf3a0568847af72d6809e788391a6ac1b496afe6\nbranch refs/heads/main\n" +
            "\n" +
            "worktree C:/work/repo-agent1\nHEAD 1111111111111111111111111111111111111111\nbranch refs/heads/feature/one\n";

        var rows = Rows(Worktrees, output);

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(("C:/work/repo", "main"));
        rows[1].ShouldBe(("C:/work/repo-agent1", "feature/one"));
    }

    [Fact]
    public void APathWithSpacesSurvives()
    {
        // The whole reason for --porcelain: the plain form separates fields with spaces, so
        // "C:/My Projects/repo abc1234 [main]" has no unambiguous reading.
        var output = "worktree C:/My Projects/repo\nHEAD abc\nbranch refs/heads/main\n";

        Rows(Worktrees, output).ShouldHaveSingleItem().Path.ShouldBe("C:/My Projects/repo");
    }

    [Fact]
    public void ADetachedWorktreeHasNoBranchAndIsStillAWorktree()
    {
        var output = "worktree C:/work/detached\nHEAD abc123\ndetached\n";

        var row = Rows(Worktrees, output).ShouldHaveSingleItem();
        row.Path.ShouldBe("C:/work/detached");
        row.Branch.ShouldBeEmpty();
    }

    [Fact]
    public void CarriageReturnsDoNotLeakIntoThePath()
    {
        // Git writes LF even on Windows, but a command run through a shell may not.
        var output = "worktree C:/work/repo\r\nHEAD abc\r\nbranch refs/heads/main\r\n";

        Rows(Worktrees, output).ShouldHaveSingleItem().Path.ShouldBe("C:/work/repo");
    }

    [Fact]
    public void TheDefaultPatternTakesAWholeLineAtATime()
    {
        // What a plain list of paths needs, and the default for a new listing entry.
        var rows = Rows("^(?<path>.+)$", "C:/one\nC:/two\nC:/three\n");

        rows.Select(r => r.Path).ShouldBe(["C:/one", "C:/two", "C:/three"]);
    }

    [Fact]
    public void AGrepStyleExtractionPullsPathsOutOfProse()
    {
        // The second use case: paths mentioned in a document, as a location.
        var output = "see src/main.cpp for detail\nand also src/util.cpp\n";

        Rows(@"(?<path>\S+\.cpp)", output).Select(r => r.Path).ShouldBe(["src/main.cpp", "src/util.cpp"]);
    }
}

public sealed class CommandListingSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-from-").FullName;
    private readonly IProviderRegistry _providers = new ProviderRegistry([new LocalFileSystemProvider()]);
    private readonly CommandListingCatalog _catalog = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A listing whose "command" is cmd echoing lines we control.</summary>
    private CommandListing Echoing(string text, string extract = "^(?<path>.+)$") =>
        new("test", "Test", "cmd.exe", ["/c", $"echo {text}"], extract);

    private async Task<List<FsEntry>> Rows(CommandListing listing)
    {
        _catalog.Register(listing);
        var uri = FsUri.View("from", FsUri.Local(_root), listing.Id);
        var source = new ListingSourceFactory(_providers, _catalog).Create(uri, new ListingFilter());

        var rows = new List<FsEntry>();
        await foreach (var entry in source.ListAsync(CancellationToken.None))
            rows.Add(entry);
        return rows;
    }

    [Fact]
    public async Task APrintedNameWithItsOwnLastSpaceIsThatFileNotItsNeighbour()
    {
        // Trimmed, "report " was the row for report beside it, for Del to act on.
        File.WriteAllText(Path.Combine(_root, "report"), "the neighbour");
        var spaced = @"\\?\" + Path.Combine(_root, "report ");
        File.WriteAllText(spaced, "the one printed");
        try
        {
            var row = (await Rows(Echoing(Path.Combine(_root, "report ")))).ShouldHaveSingleItem();
            row.Name.ShouldBe("report ");
        }
        finally
        {
            File.Delete(spaced);
        }

        // With only report there, a space the script left at the end is the script's.
        (await Rows(Echoing(Path.Combine(_root, "report ")))).ShouldHaveSingleItem().Name.ShouldBe("report");
    }

    [Fact]
    public async Task AnAddressRefusedForItsPasswordIsNotALocalRow()
    {
        // Joined to the working folder, it became a local path with the password in it.
        var row = (await Rows(Echoing("sftp://u:hunter2@h/x"))).ShouldHaveSingleItem();

        row.Uri.IsLocal.ShouldBeFalse();
        row.Extra![ColumnIds.Note].ShouldBe("bad path");
    }

    [Fact]
    public async Task ALeadingSpaceBeforeDotDotIsNotAFolderForDotDotToTakeOff()
    {
        // `git status -s` prints " M ../README.md": kept as printed, "<here>\ ..\README.md" lost its
        // space folder to the `..` by text, and the row was the README here, for Del to act on.
        File.WriteAllText(Path.Combine(_root, "README.md"), "not the one printed");

        var row = (await Rows(Echoing(@"M ..\README.md", "^M(?<path>.+)$"))).ShouldHaveSingleItem();

        row.Uri.ShouldNotBe(FsUri.Local(Path.Combine(_root, "README.md")));
        row.Uri.Parent.ShouldBe(FsUri.Local(Path.GetDirectoryName(_root)!));
    }

    [Fact]
    public async Task APrintedNameWithItsOwnFirstSpaceIsThatFileNotItsNeighbour()
    {
        // Trimming the start against ` ../README.md` took ` notes` for notes beside it.
        File.WriteAllText(Path.Combine(_root, "notes"), "the neighbour");
        File.WriteAllText(Path.Combine(_root, " notes"), "the one printed");

        // cmd's echo eats one space after itself, so this prints " notes".
        var row = (await Rows(Echoing(" notes"))).ShouldHaveSingleItem();

        row.Name.ShouldBe(" notes");
    }

    [Fact]
    public async Task TwoFilesWhoseNamesDifferByALastSpaceAreTwoRows()
    {
        // Told apart by the trimmed text, the second was dropped, with nothing said.
        var plain = Path.Combine(_root, "report");
        File.WriteAllText(plain, "plain");
        var spaced = @"\\?\" + plain + " ";
        File.WriteAllText(spaced, "spaced");
        try
        {
            var rows = await Rows(Echoing($"{plain}& echo {plain} "));
            rows.Select(r => r.Name).Order().ShouldBe(["report", "report "]);
        }
        finally
        {
            File.Delete(spaced);
        }
    }

    [Fact]
    public async Task APrintedPathBecomesARealEntry()
    {
        var folder = Path.Combine(_root, "somewhere");
        Directory.CreateDirectory(folder);

        var row = (await Rows(Echoing(folder))).ShouldHaveSingleItem();

        row.Name.ShouldBe("somewhere");
        // A real entry, not a reference to one: opening and copying must behave as they would in
        // the folder it actually lives in.
        row.IsDirectory.ShouldBeTrue();
        row.Uri.ShouldBe(FsUri.Local(folder));
    }

    [Fact]
    public async Task APathThatIsGoneIsShownRatherThanDropped()
    {
        // A worktree list quietly missing the one you deleted has stopped telling you the truth.
        var row = (await Rows(Echoing(Path.Combine(_root, "deleted")))).ShouldHaveSingleItem();

        row.Extra!["note"].ShouldBe("not found");
    }

    [Fact]
    public async Task TheFooterSaysHowManyAreGone()
    {
        _catalog.Register(Echoing(Path.Combine(_root, "deleted")));
        var uri = FsUri.View("from", FsUri.Local(_root), "test");
        var source = new ListingSourceFactory(_providers, _catalog).Create(uri, new ListingFilter());

        await foreach (var _ in source.ListAsync(CancellationToken.None)) { }

        source.Omission.ShouldBe("1 path no longer exists");
    }

    [Fact]
    public async Task ACommandThatCannotRunSaysSoInsteadOfLookingEmpty()
    {
        _catalog.Register(new CommandListing("broken", "Broken", "no-such-program-at-all", [], "^(.+)$"));
        var uri = FsUri.View("from", FsUri.Local(_root), "broken");
        var source = new ListingSourceFactory(_providers, _catalog).Create(uri, new ListingFilter());

        await foreach (var _ in source.ListAsync(CancellationToken.None)) { }

        source.Omission.ShouldNotBeNull();
    }

    [Fact]
    public void AnAddressNamingNoListingIsRefusedClearly() =>
        // A bookmark kept after its entry was renamed. The message has to name the entry.
        Should.Throw<InvalidOperationException>(() =>
                new ListingSourceFactory(_providers, _catalog)
                    .Create(FsUri.View("from", FsUri.Local(_root), "gone"), new ListingFilter()))
            .Message.ShouldContain("gone");

    [Fact]
    public async Task ExtraGroupsBecomeColumns()
    {
        var folder = Path.Combine(_root, "wt");
        Directory.CreateDirectory(folder);

        var listing = new CommandListing(
            // A comma and not a pipe: cmd would read the pipe as one, and the test would be
            // measuring cmd rather than the extraction.
            "cols", "Cols", "cmd.exe", ["/c", $"echo {folder},main"],
            @"^(?<path>[^,]+),(?<branch>\S+)",
            [new CommandListingColumn("branch", "Branch")]);

        var row = (await Rows(listing)).ShouldHaveSingleItem();

        row.Extra!["branch"].ShouldBe("main");
    }

    [Fact]
    public void TheColumnSetCarriesWhateverTheListingDeclared()
    {
        var listing = new CommandListing(
            "c", "C", "x", [], "^(.+)$", [new CommandListingColumn("branch", "Branch")]);

        var columns = CommandListingColumns.For(listing);

        columns.Find("branch")!.Header.ShouldBe("Branch");
        // Always there: a row that is not there needs somewhere to say so.
        columns.Find(ColumnIds.Note).ShouldNotBeNull();
        columns.Find(ColumnIds.RelativePath).ShouldNotBeNull();
    }

    [Fact]
    public void AListingOfFoldersCanLeaveOutModified()
    {
        var files = new CommandListing("c", "C", "x", [], "^(.+)$");
        var folders = files with { ShowModified = false };

        CommandListingColumns.For(files).Find(ColumnIds.Modified).ShouldNotBeNull();
        CommandListingColumns.For(folders).Find(ColumnIds.Modified).ShouldBeNull();
        // The Note column is not optional: a row that is missing needs somewhere to say so.
        CommandListingColumns.For(folders).Find(ColumnIds.Note).ShouldNotBeNull();
    }

    [Fact]
    public void TheSlackGoesToThePathUnlessTheListingClaimsIt()
    {
        CommandListing With(bool flexible) => new(
            "c", "C", "x", [], "^(.+)$", [new CommandListingColumn("branch", "Branch", Flexible: flexible)]);

        var plain = CommandListingColumns.For(With(flexible: false));
        plain.Columns.Where(c => c.Flexible).ShouldHaveSingleItem().Id.ShouldBe(ColumnIds.RelativePath);

        // Only one column can take what is left, or two would each be sized to fill the pane.
        var claimed = CommandListingColumns.For(With(flexible: true));
        claimed.Columns.Where(c => c.Flexible).ShouldHaveSingleItem().Id.ShouldBe("branch");
    }

    [Fact]
    public async Task TheSamePathTwiceIsOneRow()
    {
        var folder = Path.Combine(_root, "dup");
        Directory.CreateDirectory(folder);

        (await Rows(Echoing($"{folder} & echo {folder}"))).Count.ShouldBe(1);
    }
}
