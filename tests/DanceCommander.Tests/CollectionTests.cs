using DanceCommander.Core.Collections;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Navigation;
using DanceCommander.Core.Operations;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>Collections (§6.19): the list, its pages, and references that follow our own moves.</summary>
public sealed class CollectionTests : IDisposable
{
    private sealed class MemoryStore : IConfigStore<CollectionsFile>
    {
        public CollectionsFile Current { get; private set; } = new();

        public event EventHandler<CollectionsFile>? Changed { add { } remove { } }

        public Task SaveAsync(CollectionsFile value, CancellationToken ct = default)
        {
            Current = value;
            return Task.CompletedTask;
        }
    }

    private readonly string _root = Directory.CreateTempSubdirectory("dc-collections-").FullName;
    private readonly RelocationFeed _feed = new();
    private readonly CollectionService _collections;

    public CollectionTests() => _collections = new CollectionService(new MemoryStore(), _feed);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FsUri File(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, name);
        return FsUri.Local(path);
    }

    [Fact]
    public async Task AddingMakesTheCollectionAndCountsOnlyWhatIsNew()
    {
        var a = FsUri.Local(@"C:\src\a.txt");
        var b = FsUri.Local(@"C:\src\b.txt");

        (await _collections.AddAsync("Release", [a])).ShouldBe(1);
        (await _collections.AddAsync("release", [a, b])).ShouldBe(1); // same collection, a already there

        _collections.Collections.Single().Name.ShouldBe("Release");
        _collections.Members("RELEASE").ShouldBe([a, b]);
        _collections.LastUsed.ShouldBe("Release");
    }

    [Fact]
    public async Task RemovingTakesMembersOutAndLeavesTheFiles()
    {
        var file = File("keep.txt");
        await _collections.AddAsync("Work", [file]);

        await _collections.RemoveAsync("Work", [file]);

        _collections.Members("Work").ShouldBeEmpty();
        System.IO.File.Exists(file.ToLocalPath()).ShouldBeTrue();
    }

    [Fact]
    public async Task MembersFollowRenamesAndMovesOfTheFoldersTheyAreIn()
    {
        var report = FsUri.Local(@"C:\src\docs\report.md");
        var loose = FsUri.Local(@"C:\src\notes.txt");
        await _collections.AddAsync("Work", [report, loose]);

        _feed.Report([new Relocation(FsUri.Local(@"C:\src\docs"), FsUri.Local(@"D:\archive\docs-2026"))]);
        _feed.Report([new Relocation(loose, FsUri.Local(@"C:\src\notes-old.txt"))]);

        _collections.Members("Work").ShouldBe([
            FsUri.Local(@"D:\archive\docs-2026\report.md"),
            FsUri.Local(@"C:\src\notes-old.txt"),
        ]);
    }

    [Fact]
    public void ARelocationLeavesAloneWhatIsNotInIt()
    {
        var move = new Relocation(FsUri.Local(@"C:\src\docs"), FsUri.Local(@"C:\old"));

        move.Apply(FsUri.Local(@"C:\src\docs-other\x.txt")).ShouldBeNull(); // a prefix of the name is not a parent
        move.Apply(FsUri.Local(@"C:\src\docs\a\b.txt")).ShouldBe(FsUri.Local(@"C:\old\a\b.txt"));
    }

    [Fact]
    public async Task ACollectionPageListsTheFilesWhereTheyLiveAndSaysWhichAreGone()
    {
        var present = File(@"sub\here.txt");
        var gone = FsUri.Local(Path.Combine(_root, "gone.txt"));
        await _collections.AddAsync("Mixed", [present, gone]);
        var providers = new ProviderRegistry([new LocalFileSystemProvider()]);
        var about = new AboutProvider(_collections, () => providers);

        var rows = new List<FsEntry>();
        await foreach (var row in about.ListAsync(AboutPages.Collection("mixed"), CancellationToken.None))
            rows.Add(row);

        var here = rows.Single(r => r.Name == "here.txt");
        here.Uri.ShouldBe(present); // the row is the file, so Enter, F5 and F2 act on it
        here.Size.ShouldBe(@"sub\here.txt".Length); // the test file holds its own name
        here.Extra![ColumnIds.RelativePath].ShouldBe(present.Parent!.Display);
        rows.Single(r => r.Name == "gone.txt").Extra![ColumnIds.Note].ShouldBe("not found");

        about.ColumnsFor(AboutPages.Collection("mixed")).Columns.ShouldContain(c => c.Id == ColumnIds.RelativePath);
    }

    [Fact]
    public async Task TheListOfCollectionsAndTheComputerPageLeadToThem()
    {
        await _collections.AddAsync("Release", [FsUri.Local(@"C:\a.txt")]);
        var about = new AboutProvider(_collections);

        var list = new List<FsEntry>();
        await foreach (var row in about.ListAsync(AboutPages.Collections, CancellationToken.None))
            list.Add(row);
        list.Single().Uri.ShouldBe(AboutPages.Collection("Release"));
        list.Single().Extra![AboutProvider.CountColumn].ShouldBe("1");

        var computer = new List<FsEntry>();
        await foreach (var row in about.ListAsync(FsUri.Computer, CancellationToken.None))
            computer.Add(row);
        computer.ShouldContain(r => r.Uri == AboutPages.Collections && r.Name == "Collections");
    }

    [Fact]
    public void ACollectionAddressKeepsItsCaseButMatchesWithout()
    {
        var page = FsUri.Parse("About:Collections/Release 2026");

        page.ToString().ShouldBe("about:collections/Release 2026");
        page.ShouldBe(AboutPages.Collection("release 2026"));
        AboutPages.CollectionName(page).ShouldBe("Release 2026");
        page.Parent.ShouldBe(AboutPages.Collections);
        AboutPages.Collections.Name.ShouldBe("Collections");
        AboutPages.CollectionName(AboutPages.Collections).ShouldBeNull();
        AboutPages.CheckName("a/b").ShouldNotBeNull();
    }
}

/// <summary>Del and F7 mean something else on a collection's pages, and only there.</summary>
public class PlacedCommandTests
{
    private static CommandDefinition Command(string id, string key, Func<FsUri, bool>? onlyAt = null) => new()
    {
        Id = id, Title = id, Binding = KeyStroke.Parse(key), OnlyAt = onlyAt,
        ExecuteAsync = (_, _) => Task.CompletedTask,
    };

    [Fact]
    public void APlacedCommandTakesItsKeyOnlyWhereItBelongs()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("dc.delete", "Delete"));
        registry.Register(Command("dc.removeFromCollection", "Delete", AboutPages.HoldsFiles));
        registry.Register(Command("dc.newFolder", "F7"));
        registry.Register(Command("dc.newCollection", "F7", uri => uri == AboutPages.Collections));

        var stroke = KeyStroke.Parse("Delete");
        registry.ForKey(stroke, FsUri.Local(@"C:\src"))!.Id.ShouldBe("dc.delete");
        registry.ForKey(stroke, AboutPages.Collection("Work"))!.Id.ShouldBe("dc.removeFromCollection");
        registry.ForKey(stroke)!.Id.ShouldBe("dc.delete");
        registry.Legend(KeyModifiers.None, at: AboutPages.Collections)[6]!.Id.ShouldBe("dc.newCollection");
        registry.Legend(KeyModifiers.None, at: FsUri.Local(@"C:\src"))[6]!.Id.ShouldBe("dc.newFolder");
        registry.Displaced.ShouldBeEmpty(); // sharing a key on purpose is not a collision
    }

    [Fact]
    public void InACollectionTheRowsAreFilesButDeleteIsNotOffered()
    {
        var file = new FsEntry(FsUri.Local(@"C:\src\a.txt"), "a.txt", IsDirectory: false, 1, null);
        var context = new FakeContext { Directory = AboutPages.Collection("Work"), Selection = [file], ServiceProvider = NoServices.Instance };

        var rename = new CommandDefinition { Id = "r", Title = "r", Needs = ProviderCapabilities.Rename, ExecuteAsync = (_, _) => Task.CompletedTask };
        var delete = new CommandDefinition { Id = "d", Title = "d", DeletesEntries = true, Needs = ProviderCapabilities.Delete, ExecuteAsync = (_, _) => Task.CompletedTask };
        var paste = new CommandDefinition { Id = "p", Title = "p", WritesHere = true, Subject = CommandSubject.Location, ExecuteAsync = (_, _) => Task.CompletedTask };
        var copy = new CommandDefinition { Id = "c", Title = "c", ReadsContents = true, ExecuteAsync = (_, _) => Task.CompletedTask };
        var flatten = new CommandDefinition { Id = "f", Title = "f", ReadsContents = true, Subject = CommandSubject.Location, ExecuteAsync = (_, _) => Task.CompletedTask };

        rename.AppliesAt(context).ShouldBeTrue();
        copy.AppliesAt(context).ShouldBeTrue();
        delete.AppliesAt(context).ShouldBeFalse();
        paste.AppliesAt(context).ShouldBeFalse();
        flatten.AppliesAt(context).ShouldBeFalse();

        // The list of collections is a page of places, like the computer page.
        copy.AppliesAt(new FakeContext { Directory = AboutPages.Collections, ServiceProvider = NoServices.Instance }).ShouldBeFalse();
    }

    private sealed class NoServices : IServiceProvider
    {
        public static readonly NoServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>The bookmarks page (§6.19): records that point somewhere, managed in the listing.</summary>
public sealed class BookmarkPageTests
{
    private sealed class MemoryStore(BookmarksFile file) : IConfigStore<BookmarksFile>
    {
        public BookmarksFile Current { get; private set; } = file;

        public event EventHandler<BookmarksFile>? Changed { add { } remove { } }

        public Task SaveAsync(BookmarksFile value, CancellationToken ct = default)
        {
            Current = value;
            return Task.CompletedTask;
        }
    }

    private readonly RelocationFeed _feed = new();
    private readonly BookmarkService _bookmarks;

    public BookmarkPageTests() => _bookmarks = new BookmarkService(
        new MemoryStore(new BookmarksFile
        {
            Bookmarks =
            [
                new Bookmark("src", FsUri.Local(@"C:\src").ToString()),
                new Bookmark("logs", "sftp://build01/var/log", "Servers"),
            ],
        }),
        _feed);

    private async Task<List<FsEntry>> List(FsUri page)
    {
        var rows = new List<FsEntry>();
        await foreach (var row in new AboutProvider(bookmarks: _bookmarks).ListAsync(page, CancellationToken.None))
            rows.Add(row);
        return rows;
    }

    [Fact]
    public async Task TheTopLevelHasTheUngroupedBookmarksAndAFolderPerGroup()
    {
        var rows = await List(AboutPages.Bookmarks);

        var src = rows.Single(r => r.Name == "src");
        src.Uri.ShouldBe(FsUri.Local(@"C:\src")); // Enter goes to the place
        src.LinkTarget.ShouldNotBeNull();         // and the row says it is a pointer
        rows.Single(r => r.Name == "Servers").Uri.ShouldBe(AboutPages.BookmarkGroup("Servers"));

        (await List(AboutPages.BookmarkGroup("servers"))).Single().Name.ShouldBe("logs");
        AboutPages.BookmarkGroup("Servers").Parent.ShouldBe(AboutPages.Bookmarks);
    }

    [Fact]
    public async Task RenamingALabelLeavesThePlaceAndRefusesATakenName()
    {
        (await _bookmarks.RenameAsync("src", "Sources")).ShouldBeTrue();
        _bookmarks.Find("Sources")!.Uri.ShouldBe(FsUri.Local(@"C:\src").ToString());

        (await _bookmarks.RenameAsync("Sources", "LOGS")).ShouldBeFalse();
    }

    [Fact]
    public async Task GroupsAreRenamedAndJoinedByName()
    {
        await _bookmarks.RenameGroupAsync("servers", "Remote");
        await _bookmarks.SetGroupAsync(["src"], "Remote");

        _bookmarks.Bookmarks.ShouldAllBe(b => b.Group == "Remote");
    }

    [Fact]
    public void ABookmarkFollowsTheFolderItPointsTo()
    {
        _feed.Report([new Relocation(FsUri.Local(@"C:\src"), FsUri.Local(@"D:\code"))]);

        _bookmarks.Find("src")!.Uri.ShouldBe(FsUri.Local(@"D:\code").ToString());
    }

    [Fact]
    public void OnTheBookmarksPageF2AndDelEditTheBookmarksNotThePlaces()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition { Id = "dc.rename", Title = "Rename", Needs = ProviderCapabilities.Rename, Binding = KeyStroke.Parse("F2"), ExecuteAsync = (_, _) => Task.CompletedTask });
        registry.Register(new CommandDefinition { Id = "dc.renameBookmark", Title = "Rename bookmark", Binding = KeyStroke.Parse("F2"), OnlyAt = AboutPages.IsBookmarks, ExecuteAsync = (_, _) => Task.CompletedTask });

        registry.ForKey(KeyStroke.Parse("F2"), AboutPages.BookmarkGroup("Servers"))!.Id.ShouldBe("dc.renameBookmark");

        // And from the menu: the general Rename would rename the folder the bookmark points to.
        var row = new FsEntry(FsUri.Local(@"C:\src"), "src", IsDirectory: true, 0, null);
        var context = new FakeContext { Directory = AboutPages.Bookmarks, Selection = [row] };
        registry.Find("dc.rename")!.AppliesAt(context).ShouldBeFalse();
    }
}

/// <summary>The history page (§6.19): a log, where Del forgets a visit.</summary>
public sealed class HistoryPageTests
{
    private sealed class MemoryStore(HistoryFile file) : IConfigStore<HistoryFile>
    {
        public HistoryFile Current { get; private set; } = file;

        public event EventHandler<HistoryFile>? Changed { add { } remove { } }

        public Task SaveAsync(HistoryFile value, CancellationToken ct = default)
        {
            Current = value;
            return Task.CompletedTask;
        }
    }

    private sealed class NoBookmarks : IBookmarkService
    {
        public IReadOnlyList<Bookmark> Bookmarks => [];
        public event EventHandler? Changed { add { } remove { } }
        public Task AddAsync(string name, FsUri uri, string? group = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RenameAsync(string name, string newName, CancellationToken ct = default) => Task.FromResult(false);
        public Task RenameGroupAsync(string group, string newGroup, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetGroupAsync(IReadOnlyList<string> names, string? group, CancellationToken ct = default) => Task.CompletedTask;
        public Bookmark? Find(string name) => null;
    }

    private static readonly FsUri Old = FsUri.Local(@"C:\old");
    private static readonly FsUri Src = FsUri.Local(@"C:\src");
    private static readonly FsUri Docs = FsUri.Local(@"C:\docs");

    // A file from before visit times were kept: two places, no times.
    private readonly HistoryService _history = new(
        new MemoryStore(new HistoryFile { Recent = [Docs.ToString(), Old.ToString()] }), new NoBookmarks());

    [Fact]
    public void AVisitIsTimedAndPlacesFromBeforeTimesStillRead()
    {
        _history.Record(Src);

        var visits = _history.Visits;
        visits.Select(v => v.Uri).ShouldBe([Src, Docs, Old]);
        visits[0].At.ShouldNotBeNull();
        visits[1].At.ShouldBeNull();
    }

    [Fact]
    public void ForgettingAndPuttingBackKeepsThePlacesWhereTheyStood()
    {
        _history.Record(Src);
        var forgotten = _history.Forget([Docs]);

        _history.Recent.ShouldBe([Src, Old]);

        _history.Restore(forgotten);
        _history.Recent.ShouldBe([Src, Docs, Old]);
    }

    [Fact]
    public async Task ThePageListsThePlacesWithWhenAndSortsNewestFirst()
    {
        _history.Record(Src);
        var rows = new List<FsEntry>();
        await foreach (var row in new AboutProvider(history: _history).ListAsync(AboutPages.History, CancellationToken.None))
            rows.Add(row);

        rows.Single(r => r.Uri == Src).Modified.ShouldNotBeNull();
        var columns = new AboutProvider(history: _history).ColumnsFor(AboutPages.History);
        columns.DefaultSortColumnId.ShouldBe(ColumnIds.Modified);
        columns.DefaultDescending.ShouldBeTrue();
    }
}

/// <summary>The shelf (§6.19): files picked up across folders, for this session.</summary>
public class ShelfTests
{
    [Fact]
    public void AddingCountsOnlyWhatIsNewAndItFollowsOurMoves()
    {
        var feed = new RelocationFeed();
        var shelf = new Shelf(feed);
        var a = FsUri.Local(@"C:\src\a.txt");
        var b = FsUri.Local(@"C:\docs\b.txt");

        shelf.Add([a, b]).ShouldBe(2);
        shelf.Add([a]).ShouldBe(0);

        feed.Report([new Relocation(FsUri.Local(@"C:\src"), FsUri.Local(@"D:\src"))]);
        shelf.Items.ShouldBe([FsUri.Local(@"D:\src\a.txt"), b]);

        shelf.Remove([b]);
        shelf.Items.ShouldBe([FsUri.Local(@"D:\src\a.txt")]);
    }

    [Fact]
    public async Task ItsPageListsTheFilesAndIsAPageOfFiles()
    {
        var root = Directory.CreateTempSubdirectory("dc-shelf-").FullName;
        try
        {
            var path = Path.Combine(root, "x.txt");
            File.WriteAllText(path, "x");
            var shelf = new Shelf();
            shelf.Add([FsUri.Local(path)]);
            var providers = new ProviderRegistry([new LocalFileSystemProvider()]);

            var rows = new List<FsEntry>();
            await foreach (var row in new AboutProvider(providers: () => providers, shelf: shelf).ListAsync(AboutPages.Shelf, CancellationToken.None))
                rows.Add(row);

            rows.Single().Uri.ShouldBe(FsUri.Local(path));
            AboutPages.HoldsFiles(AboutPages.Shelf).ShouldBeTrue(); // so F5 and F2 act on the files
            AboutPages.Shelf.ToString().ShouldBe("about:shelf");
            AboutPages.Shelf.Name.ShouldBe("Shelf");
            FsUri.Parse("about:Marked").ShouldBe(AboutPages.Shelf); // its old address, in saved tabs
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
