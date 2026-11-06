using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A view is a way of looking at another location, not a place of its own — which is what makes
/// it different from an archive entry, and what decides where Backspace goes.
/// </summary>
public sealed class ViewUriTests
{
    private static readonly FsUri Work = FsUri.Local(@"C:\work");

    [Fact]
    public void AViewRemembersWhatItIsAViewOf()
    {
        var flat = FsUri.View("flat", Work);

        flat.IsView.ShouldBeTrue();
        flat.Inner.ShouldBe(Work);
        flat.Scheme.ShouldBe("flat");
    }

    [Fact]
    public void AnArchiveEntryIsNotAView() =>
        FsUri.ArchiveEntry(FsUri.Local(@"C:\a.zip"), "x.txt").IsView.ShouldBeFalse();

    [Theory]
    [InlineData("flat", "", "flat:file:///C:/work")]
    [InlineData("search", "*.cs", "search:file:///C:/work?*.cs")]
    public void RoundTripsThroughText(string scheme, string parameter, string expected)
    {
        var view = FsUri.View(scheme, Work, parameter);

        view.ToString().ShouldBe(expected);
        FsUri.Parse(expected).ShouldBe(view);
    }

    [Fact]
    public void TheParameterIsPartOfTheIdentity() =>
        // Two searches of the same folder are two locations, or history and bookmarks collapse.
        FsUri.View("search", Work, "*.cs").ShouldNotBe(FsUri.View("search", Work, "*.md"));

    [Fact]
    public void AParameterIsNotTreatedAsAPath()
    {
        // "*.cs" must survive intact; normalising it as a path would mangle it.
        FsUri.View("search", Work, "*.cs").Parameter.ShouldBe("*.cs");
        FsUri.Parse("search:file:///C:/work?src/*.cs").Parameter.ShouldBe("src/*.cs");
    }

    [Fact]
    public void LeavingAViewLandsInTheFolderItWasAViewOf() =>
        // Not beside it, which is where leaving an archive lands: an archive is a file in its
        // parent, a view is not a thing in its root at all.
        FsUri.View("flat", Work).Parent.ShouldBe(Work);

    [Fact]
    public void AViewIsNamedSoATabStripCanTellItFromTheFolder()
    {
        FsUri.View("flat", Work).Name.ShouldBe("flat: work");
        FsUri.View("search", Work, "*.cs").Name.ShouldBe("search: *.cs");
    }

    [Fact]
    public void AViewOfARemoteLocationIsStillAView()
    {
        var remote = FsUri.Parse("sftp://claude@host/var/log");

        var flat = FsUri.View("flat", remote);

        flat.ToString().ShouldBe("flat:sftp://claude@host/var/log");
        FsUri.Parse(flat.ToString()).Inner.ShouldBe(remote);
    }

    [Fact]
    public void AViewOfNothingIsRefused() =>
        Should.Throw<FormatException>(() => FsUri.Parse("flat:"));
}

public sealed class FlatAndSearchListingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-view-").FullName;
    private readonly IProviderRegistry _providers;

    public FlatAndSearchListingTests()
    {
        _providers = new ProviderRegistry([new LocalFileSystemProvider()]);

        Write("readme.md");
        Write("src/app.cs");
        Write("src/util.cs");
        Write("src/deep/nested/buried.cs");
        Write("docs/guide.md");
        Write(".git/objects/ab/cdef");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private static IListingSource Source(IProviderRegistry providers, FsUri uri, bool showHidden = false) =>
        new ListingSourceFactory(providers).Create(uri, new ListingFilter(showHidden));

    private async Task<List<FsEntry>> Rows(FsUri uri, bool showHidden = false)
    {
        var rows = new List<FsEntry>();
        await foreach (var entry in Source(_providers, uri, showHidden).ListAsync(CancellationToken.None))
            rows.Add(entry);
        return rows;
    }

    private FsUri Root => FsUri.Local(_root);

    [Fact]
    public async Task FlatteningBringsTheWholeTreeUpAsFiles()
    {
        var rows = await Rows(FsUri.View("flat", Root));

        rows.Select(r => r.Name).OrderBy(n => n)
            .ShouldBe(["app.cs", "buried.cs", "guide.md", "readme.md", "util.cs"]);
        // Folders are not rows: the extra column already says where each file lives.
        rows.ShouldAllBe(r => !r.IsDirectory);
    }

    [Fact]
    public async Task EachRowSaysWhichFolderItCameFrom()
    {
        var rows = await Rows(FsUri.View("flat", Root));

        rows.Single(r => r.Name == "buried.cs").Extra!["relpath"].ShouldBe("src/deep/nested");
        rows.Single(r => r.Name == "readme.md").Extra!["relpath"].ShouldBe(".");
    }

    [Fact]
    public async Task AHiddenFolderIsNotWalkedAtAll()
    {
        // The point is not that ".git/objects/ab/cdef" is filtered out of the result -- it is that
        // the walk never opened ".git", which is where the minute goes on a real machine.
        var rows = await Rows(FsUri.View("flat", Root));

        rows.ShouldNotContain(r => r.Name == "cdef");
    }

    [Fact]
    public async Task ShowingHiddenEntriesWalksTheFoldersToo()
    {
        var rows = await Rows(FsUri.View("flat", Root), showHidden: true);

        rows.ShouldContain(r => r.Name == "cdef");
    }

    [Fact]
    public async Task ASearchDoesNotEnterHiddenFoldersEither()
    {
        (await Rows(FsUri.View("search", Root, "cdef"))).ShouldBeEmpty();
        (await Rows(FsUri.View("search", Root, "cdef"), showHidden: true)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ThePaneIsToldWhatWasNotWalked()
    {
        // Silently declining to search somewhere is the one thing pruning must never do.
        var source = Source(_providers, FsUri.View("flat", Root));
        await foreach (var _ in source.ListAsync(CancellationToken.None)) { }

        source.Omission.ShouldBe("1 hidden folder not searched");
    }

    [Fact]
    public async Task NothingIsSaidWhenNothingWasSkipped()
    {
        var source = Source(_providers, FsUri.View("flat", Root), showHidden: true);
        await foreach (var _ in source.ListAsync(CancellationToken.None)) { }

        source.Omission.ShouldBeNull();
    }

    [Fact]
    public void APrunedWalkAsksToBeListedAgainWhenTheFilterChanges() =>
        // Re-filtering rows that were never collected would show nothing new.
        Source(_providers, FsUri.View("flat", Root)).RereadWhenFilterChanges.ShouldBeTrue();

    [Fact]
    public void AnOrdinaryDirectoryJustRefilters() =>
        Source(_providers, Root).RereadWhenFilterChanges.ShouldBeFalse();

    [Fact]
    public async Task AFlattenedRowStillPointsAtTheRealFile()
    {
        var rows = await Rows(FsUri.View("flat", Root));

        var buried = rows.Single(r => r.Name == "buried.cs");
        File.Exists(buried.Uri.ToLocalPath()).ShouldBeTrue();
    }

    [Fact]
    public async Task ABareWordMatchesAnywhereInTheName()
    {
        var rows = await Rows(FsUri.View("search", Root, "util"));

        rows.Select(r => r.Name).ShouldBe(["util.cs"]);
    }

    [Fact]
    public async Task AWildcardIsTreatedAsAGlob()
    {
        var rows = await Rows(FsUri.View("search", Root, "*.md"));

        rows.Select(r => r.Name).OrderBy(n => n).ShouldBe(["guide.md", "readme.md"]);
    }

    [Fact]
    public async Task SearchReachesTheWholeTree()
    {
        var rows = await Rows(FsUri.View("search", Root, "*.cs"));

        rows.Select(r => r.Name).OrderBy(n => n).ShouldBe(["app.cs", "buried.cs", "util.cs"]);
    }

    [Fact]
    public async Task FoldersCanBeFoundToo()
    {
        var rows = await Rows(FsUri.View("search", Root, "deep"));

        var found = rows.ShouldHaveSingleItem();
        found.Name.ShouldBe("deep");
        found.IsDirectory.ShouldBeTrue();
    }

    [Fact]
    public async Task AMatchingFolderIsStillSearchedInside()
    {
        // "src" matches, and so does the file below it; finding the folder must not stop the walk.
        var rows = await Rows(FsUri.View("search", Root, "*s*"));

        rows.Select(r => r.Name).ShouldContain("buried.cs");
    }

    [Fact]
    public async Task ASearchOpensWithTheLikeliestFirst()
    {
        // The name starting with the word, shallow, before deeper, before the word mid-name, and
        // what a build left behind last however well it matches.
        Write("src/autil.txt");
        Write("src/deep/util.md");
        Write("bin/Debug/util.cs");
        var source = Source(_providers, FsUri.View("search", Root, "util"));

        var rows = new List<FsEntry>();
        await foreach (var entry in source.ListAsync(CancellationToken.None))
            rows.Add(entry);
        rows.Sort(new ListingOrder(source.Columns.DefaultSortColumnId));

        source.Columns.DefaultSortColumnId.ShouldBe(ColumnIds.Relevance);
        rows.Select(r => $"{r.Extra!["relpath"]}/{r.Name}")
            .ShouldBe(["src/util.cs", "src/deep/util.md", "src/autil.txt", "bin/Debug/util.cs"]);
    }

    [Fact]
    public void ASearchCanGoBackToItsOwnOrderAndAFlatViewHasNone()
    {
        // What Ctrl+F7 sorts by once Ctrl+F3..F6 have left it.
        var factory = new ListingSourceFactory(_providers);

        factory.Create(FsUri.View("search", Root, "x"), new ListingFilter()).Columns.RelevanceColumnId
            .ShouldBe(ColumnIds.Relevance);
        factory.Create(FsUri.View("flat", Root), new ListingFilter()).Columns.RelevanceColumnId.ShouldBeNull();
        ColumnSet.OfEverywhere.RelevanceColumnId.ShouldBe(ColumnIds.Where);
    }

    [Fact]
    public async Task AnEmptyQueryFindsEverything() =>
        (await Rows(FsUri.View("search", Root, ""))).ShouldNotBeEmpty();

    [Theory]
    [InlineData("flat", "")]
    [InlineData("search", "*.cs")]
    public async Task AFolderThatCannotBeReadIsPassedOverAndSaidSo(string scheme, string query)
    {
        // A symlink WSL or Docker made in a bind mount is a folder Windows cannot open; one of
        // those deep in a repository used to end the whole search with its error.
        var deep = FsUri.Local(Path.Combine(_root, "src", "deep"));
        var faulty = new FaultyProvider(new LocalFileSystemProvider()) { FailList = uri => uri == deep };
        var source = Source(new ProviderRegistry([faulty]), FsUri.View(scheme, Root, query));

        var rows = new List<FsEntry>();
        await foreach (var entry in source.ListAsync(CancellationToken.None))
            rows.Add(entry);

        rows.Select(r => r.Name).ShouldContain("util.cs");
        rows.Select(r => r.Name).ShouldNotContain("buried.cs");
        source.Omission.ShouldBe("1 hidden folder not searched, 1 folder could not be read");
    }

    [Fact]
    public void BothViewsOfferTheFolderColumn()
    {
        var factory = new ListingSourceFactory(_providers);

        foreach (var uri in (FsUri[])[FsUri.View("flat", Root), FsUri.View("search", Root, "*")])
            factory.Create(uri, new ListingFilter()).Columns.Find("relpath").ShouldNotBeNull();
    }

    [Fact]
    public void AnOrdinaryDirectoryStillGetsTheOrdinaryListing() =>
        new ListingSourceFactory(_providers).Create(Root, new ListingFilter())
            .ShouldBeOfType<DirectoryListingSource>();
}
