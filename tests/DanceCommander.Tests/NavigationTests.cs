using DanceCommander.Core.Columns;
using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using DanceCommander.Core.Navigation;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

public class NavigationHistoryTests
{
    private static FsUri At(string name) => FsUri.Local(@"C:\" + name);

    [Fact]
    public void WalksBackAndForwardLikeABrowser()
    {
        var history = new NavigationHistory();
        history.Visit(At("a"));
        history.Visit(At("b"));
        history.Visit(At("c"));

        history.Back().ShouldBe(At("b"));
        history.Back().ShouldBe(At("a"));
        history.CanGoBack.ShouldBeFalse();

        history.Forward().ShouldBe(At("b"));
        history.Current.ShouldBe(At("b"));
    }

    [Fact]
    public void GoingToAnEntryKeepsTheOnesBetweenAndEitherSide()
    {
        var history = new NavigationHistory();
        foreach (var name in new[] { "a", "b", "c", "d" })
            history.Visit(At(name));

        history.GoTo(1).ShouldBe(At("b"));
        history.Current.ShouldBe(At("b"));
        history.CanGoBack.ShouldBeTrue();
        history.CanGoForward.ShouldBeTrue();
        history.Forward().ShouldBe(At("c"));
        history.Entries.Count.ShouldBe(4);

        history.GoTo(-1).ShouldBeNull();
        history.GoTo(4).ShouldBeNull();
        history.Current.ShouldBe(At("c"));
    }

    [Fact]
    public void VisitingSomewhereNewAfterGoingBackDiscardsTheForwardEntries()
    {
        var history = new NavigationHistory();
        history.Visit(At("a"));
        history.Visit(At("b"));
        history.Back();

        history.Visit(At("c"));

        history.CanGoForward.ShouldBeFalse();
        history.Entries.Select(e => e.Name).ShouldBe(["a", "c"]);
    }

    [Fact]
    public void IgnoresRepeatedVisitsToTheSamePlace()
    {
        var history = new NavigationHistory();
        history.Visit(At("a"));
        history.Visit(At("a"));

        history.CanGoBack.ShouldBeFalse();
        history.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public void DropsTheOldestEntriesPastCapacity()
    {
        var history = new NavigationHistory(capacity: 3);
        foreach (var name in (string[])["a", "b", "c", "d"])
            history.Visit(At(name));

        history.Entries.Select(e => e.Name).ShouldBe(["b", "c", "d"]);
        history.Current.ShouldBe(At("d"));
    }
}

public class ViewStateStoreTests
{
    private static FsUri At(string name) => FsUri.Local(@"C:\" + name);

    private static ViewState State(string? focus = null) =>
        new(new ListingOrder(), new ListingFilter(), focus);

    [Fact]
    public void RemembersHowALocationWasLeft()
    {
        var store = new ViewStateStore();
        store.Set(At("src"), State("Program.cs"));

        store.Get(At("src"))!.FocusedName.ShouldBe("Program.cs");
        store.Get(At("other")).ShouldBeNull();
    }

    [Fact]
    public void KeepsLocalLookupsCaseInsensitive()
    {
        var store = new ViewStateStore();
        store.Set(FsUri.Local(@"C:\Src"), State("a.cs"));

        store.Get(FsUri.Local(@"c:\src"))!.FocusedName.ShouldBe("a.cs");
    }

    [Fact]
    public void EvictsTheLeastRecentlyUsedLocationPastCapacity()
    {
        var store = new ViewStateStore(capacity: 2);
        store.Set(At("a"), State("a"));
        store.Set(At("b"), State("b"));

        store.Get(At("a")); // touching "a" makes "b" the least recent
        store.Set(At("c"), State("c"));

        store.Get(At("a")).ShouldNotBeNull();
        store.Get(At("c")).ShouldNotBeNull();
        store.Get(At("b")).ShouldBeNull();
    }
}

/// <summary>
/// Address-bar completions. The labelling matters as much as the matching: the popup has two
/// lines per entry and filling both with the same fact is worse than filling one.
/// </summary>
public sealed class SuggestionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-sug-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeBookmarks(params Bookmark[] bookmarks) : IBookmarkService
    {
        public IReadOnlyList<Bookmark> Bookmarks { get; } = bookmarks;

        public event EventHandler? Changed { add { } remove { } }

        public Task AddAsync(string name, FsUri uri, string? group = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> RenameAsync(string name, string newName, CancellationToken ct = default) => Task.FromResult(false);

        public Task RenameGroupAsync(string group, string newGroup, CancellationToken ct = default) => Task.CompletedTask;

        public Task SetGroupAsync(IReadOnlyList<string> names, string? group, CancellationToken ct = default) => Task.CompletedTask;

        public Bookmark? Find(string name) =>
            Bookmarks.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private HistoryService Build(IBookmarkService bookmarks, params string[] recent)
    {
        var store = new JsonConfigStore<HistoryFile>(_root, "history.json", NullLogger.Instance);
        store.SaveAsync(new HistoryFile
        {
            Recent = recent.Select(p => FsUri.Local(p).ToString()).ToList(),
        }).GetAwaiter().GetResult();

        return new HistoryService(store, bookmarks);
    }

    [Theory]
    [InlineData("dan work")]
    [InlineData("work dan")]
    [InlineData("DAN  WORK")]
    public void SeveralWordsFindAPlaceThatHasThemAll(string typed)
    {
        var history = Build(new FakeBookmarks(), @"C:\src\dance_worktree", @"C:\src\other");

        history.Suggest(typed).Select(s => s.Uri).ShouldBe([FsUri.Local(@"C:\src\dance_worktree")]);
    }

    [Fact]
    public void WordsAllInTheFolderNameBeatWordsSpreadOverItsPath()
    {
        var history = Build(new FakeBookmarks(), @"C:\dance\work\notes", @"C:\src\dance_worktree");

        history.Suggest("dan work").Select(s => s.Uri)
            .ShouldBe([FsUri.Local(@"C:\src\dance_worktree"), FsUri.Local(@"C:\dance\work\notes")]);
    }

    [Fact]
    public void AWordFoundNowhereLeavesThePlaceOut()
    {
        var history = Build(new FakeBookmarks(), @"C:\src\dance_worktree");

        history.Suggest("dan nope").ShouldBeEmpty();
    }

    [Fact]
    public void SeveralWordsFindABookmarkByItsName()
    {
        var history = Build(new FakeBookmarks(new Bookmark("Shadow copies", FsUri.Local(@"C:\snap").ToString())));

        history.Suggest("sha cop").Single().Label.ShouldBe("Shadow copies");
    }

    [Fact]
    public void AnAddressIsMatchedWholeWithItsSpaces()
    {
        // "C:\Program Files" is an address: its space is part of it, not between two words.
        var history = Build(new FakeBookmarks(), @"C:\Program Files", @"C:\Files\Program");

        history.Suggest(@"C:\Program Files").Select(s => s.Uri).ShouldBe([FsUri.Local(@"C:\Program Files")]);
    }

    [Fact]
    public void ABookmarkWhoseNameStartsWithTheTermComesFirst()
    {
        var history = Build(new FakeBookmarks(
            new Bookmark("Shadow copies", FsUri.Local(@"C:\snap").ToString()),
            new Bookmark("Archive", FsUri.Local(@"D:\old\downloads-2019").ToString()),
            new Bookmark("Downloads", FsUri.Local(@"C:\Users\me\Downloads").ToString())));

        // Typed the start of a name: that name first, then one containing it, then a path match —
        // not the order they happen to sit in the file.
        history.Suggest("dow").Select(s => s.Label).ShouldBe(["Downloads", "Shadow copies", "Archive"]);
    }

    [Fact]
    public void TheAddressCompletesToABookmarkNameFirst()
    {
        var history = Build(
            new FakeBookmarks(new Bookmark("Downloads", FsUri.Local(@"C:\Users\me\Downloads").ToString())),
            @"D:\downloads-2019");

        // The name is what Enter already goes by, so that is what fills in.
        history.Complete("dow").ShouldBe("Downloads");
    }

    [Fact]
    public void TheAddressCompletesToTheShortestPlaceThatCarriesOnFromIt()
    {
        var history = Build(new FakeBookmarks(),
            @"C:\src\dance_commander\src\DanceCommander.Core",
            @"C:\src\dance_commander",
            @"C:\src\other");

        history.Complete(@"c:\src\da").ShouldBe(@"C:\src\dance_commander");
    }

    [Fact]
    public void TheAddressCompletesOnlyTheNameBeingTyped()
    {
        var history = Build(
            new FakeBookmarks(new Bookmark("Test server", "sftp://demo@localhost:2222/config")),
            @"C:\src\orbit\src\Orbit.Cli");

        // A whole folder name is not carried on into a child that happens to be in the history:
        // Enter would go there instead of where it was told.
        history.Complete(@"C:\src\orbit\src").ShouldBeNull();
        history.Complete(@"C:\src\orb").ShouldBe(@"C:\src\orbit");
        history.Complete(@"C:\src\orbit\src\").ShouldBe(@"C:\src\orbit\src\Orbit.Cli");
        // The scheme's slashes are not a name's end.
        history.Complete("sftp://de").ShouldBe("sftp://demo@localhost:2222");
    }

    [Fact]
    public void NothingCompletesWhatNoKnownAddressStartsWith()
    {
        var history = Build(new FakeBookmarks(), @"C:\work\api");

        // Contained is enough for a suggestion, not for filling in the rest.
        history.Complete("api").ShouldBeNull();
        history.Complete("").ShouldBeNull();
    }

    [Fact]
    public void ARecentLocationIsLabelledWithItsPathAndNothingElse()
    {
        var history = Build(new FakeBookmarks(), @"C:\work\api\src");

        var suggestion = history.Suggest("api").ShouldHaveSingleItem();

        // The path alone: a "src" line above "C:\work\api\src" only repeats its last segment.
        suggestion.Label.ShouldBe(@"C:\work\api\src");
        suggestion.Detail.ShouldBeEmpty();
        suggestion.IsBookmark.ShouldBeFalse();
    }

    [Fact]
    public void ABookmarkKeepsItsNameBecauseThePathDoesNotImplyIt()
    {
        var bookmarks = new FakeBookmarks(new Bookmark("Build logs", FsUri.Local(@"C:\ci\out").ToString()));
        var history = Build(bookmarks);

        var suggestion = history.Suggest("build").ShouldHaveSingleItem();

        suggestion.Label.ShouldBe("Build logs");
        suggestion.Detail.ShouldBe(@"C:\ci\out");
        suggestion.IsBookmark.ShouldBeTrue();
    }

    [Fact]
    public void BookmarksAreOfferedBeforeRecentLocations()
    {
        var bookmarks = new FakeBookmarks(new Bookmark("Work", FsUri.Local(@"C:\work").ToString()));
        var history = Build(bookmarks, @"C:\work\api");

        var results = history.Suggest("work");

        results.Count.ShouldBe(2);
        results[0].IsBookmark.ShouldBeTrue();
        results[1].Label.ShouldBe(@"C:\work\api");
    }

    [Fact]
    public void TheFolderWhoseNameStartsWithTheTermComesFirst()
    {
        var history = Build(new FakeBookmarks(),
            // Most recent first, and deliberately the least useful answer.
            @"C:\work\dance_commander\src\DanceCommander.App",
            @"C:\work\dance_commander\src",
            @"C:\work\dance_commander",
            @"C:\work\scratch\dance_notes");

        var labels = history.Suggest("dance").Select(s => s.Label).ToList();

        // Names beginning with the term come first, shortest path among them leading.
        // "DanceCommander.App" qualifies too, and loses only on length.
        labels[0].ShouldBe(@"C:\work\dance_commander");
        labels[1].ShouldBe(@"C:\work\scratch\dance_notes");
        labels[2].ShouldBe(@"C:\work\dance_commander\src\DanceCommander.App");
        // "src" does not contain the term at all; it matched further up the path, so it is last
        // despite being the second most recent place visited.
        labels[3].ShouldBe(@"C:\work\dance_commander\src");
    }

    [Fact]
    public void AmongEquallyGoodMatchesTheShorterPathWins()
    {
        var history = Build(new FakeBookmarks(),
            @"C:\a\very\deeply\nested\logs",
            @"C:\logs");

        history.Suggest("logs")[0].Label.ShouldBe(@"C:\logs");
    }

    [Fact]
    public void RecencyStillBreaksTiesBetweenOtherwiseEqualPaths()
    {
        var history = Build(new FakeBookmarks(), @"C:\work\logs2", @"C:\work\logs1");

        var labels = history.Suggest("logs").Select(s => s.Label).ToList();

        labels[0].ShouldBe(@"C:\work\logs2"); // same rank, same length: the one visited last
        labels[1].ShouldBe(@"C:\work\logs1");
    }

    [Fact]
    public void AnEmptyTermOffersNothing() =>
        Build(new FakeBookmarks(), @"C:\work").Suggest("   ").ShouldBeEmpty();
}

public sealed class PersistentViewStateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-view-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private JsonConfigStore<ViewStateFile> Store() => new(_root, "viewstate.json", NullLogger.Instance);

    [Fact]
    public void AChosenViewSurvivesARestartAndAPassingOneDoesNot()
    {
        var downloads = FsUri.Local(@"C:\Users\me\Downloads");
        var passedThrough = FsUri.Local(@"C:\Users\me\Downloads\old");
        var newestFirst = new ListingOrder(ColumnIds.Modified, Descending: true);

        using (var store = Store())
        using (var views = new PersistentViewStateStore(store))
        {
            views.Set(downloads, new ViewState(newestFirst, new ListingFilter(), "a.txt", Chosen: true));
            // Inherited from the tab on the way through: remembered for the session only.
            views.Set(passedThrough, new ViewState(newestFirst, new ListingFilter()));
        }

        using var reopened = Store();
        var restored = new PersistentViewStateStore(reopened);

        var kept = restored.Get(downloads).ShouldNotBeNull();
        kept.Order.ShouldBe(newestFirst);
        kept.Chosen.ShouldBeTrue();
        kept.FocusedName.ShouldBeNull(); // the row you were on is session detail
        restored.Get(passedThrough).ShouldBeNull();
    }
}

/// <summary>
/// The folders on disk offered while a path is typed: the next name in the path, from what is
/// there rather than from where you have been.
/// </summary>
public sealed class FolderCompletionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-fold-").FullName;
    private readonly FolderCompletion _folders =
        new(new ProviderRegistry([new DanceCommander.Providers.Local.LocalFileSystemProvider()]));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(@"C:\Users\An", @"C:\Users", "An")]
    [InlineData(@"c:/users/", @"C:\users", "")]
    [InlineData(@"C:\", @"C:\", "")]
    [InlineData(@"\\server\share\Pro", @"\\server\share", "Pro")]
    public void SplitsAPathIntoTheFolderAndTheNameBegunInIt(string text, string folder, string prefix)
    {
        var split = FolderCompletion.Split(text).ShouldNotBeNull();
        split.Folder.ShouldBe(FsUri.Local(folder));
        split.Prefix.ShouldBe(prefix);
    }

    [Theory]
    [InlineData("downloads")] // a bookmark's name or a search, not a path
    [InlineData(@"src\Da")] // relative: relative to nothing, in an address bar
    [InlineData(@"\\server\")] // a server's shares are not a folder to list
    [InlineData(@"\work\Da")] // no drive: which one is meant is a guess
    [InlineData("sftp://host/var/l")] // listing would connect, mid-typing
    [InlineData(@"C:\work\a.zip!/doc")]
    [InlineData(@"C:\work\..")]
    public void OffersNothingForWhatIsNotALocalPathBeingTyped(string text) =>
        FolderCompletion.Split(text).ShouldBeNull();

    [Fact]
    public async Task OffersTheFoldersThatStartWithTheNameBegunNotTheFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Beta"));
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "Another"));
        File.WriteAllText(Path.Combine(_root, "apple.txt"), "");

        var found = await _folders.ListAsync(FsUri.Local(_root), "a", CancellationToken.None);

        found.Select(uri => uri.Name).ShouldBe(["alpha", "Another"]);
    }

    [Fact]
    public async Task FurtherLettersFilterTheListingAlreadyMade()
    {
        var root = FsUri.Local(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        _folders.Cached(root, "s").ShouldBeNull();

        await _folders.ListAsync(root, "s", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_root, "srv"));

        _folders.Cached(root, "sr").ShouldNotBeNull().Select(uri => uri.Name).ShouldBe(["src"]);

        // Once the address bar is left, the next address sees the disk as it is.
        _folders.Forget();
        (await _folders.ListAsync(root, "sr", CancellationToken.None)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task HiddenFoldersOnlyOnceTheirNameIsBegun()
    {
        var hidden = Directory.CreateDirectory(Path.Combine(_root, "$stash"));
        hidden.Attributes |= FileAttributes.Hidden;
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        var root = FsUri.Local(_root);

        (await _folders.ListAsync(root, "", CancellationToken.None)).Select(uri => uri.Name).ShouldBe(["work"]);
        _folders.Cached(root, "$")!.Select(uri => uri.Name).ShouldBe(["$stash"]);
    }

    [Fact]
    public async Task AFolderThatIsNotThereOffersNothingAndIsAskedAgainNextTime()
    {
        var gone = FsUri.Local(Path.Combine(_root, "gone"));

        (await _folders.ListAsync(gone, "", CancellationToken.None)).ShouldBeEmpty();

        // Not remembered as empty: a share slow to answer once may answer the next letter.
        _folders.Cached(gone, "").ShouldBeNull();
    }
}
