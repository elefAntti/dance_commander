using DanceCommander.Core.Config;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Navigation;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>A first run's bookmarks from Explorer's Quick access, and the import again (SETTINGS_PLAN.md §8).</summary>
public sealed class QuickAccessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-quick-").FullName;

    private static Bookmark Fixed(string name, string path) => new(name, FsUri.Local(path).ToString());

    private static readonly Bookmark[] Always =
    [
        Fixed("Home", @"C:\Users\me"),
        Fixed("Desktop", @"C:\Users\me\Desktop"),
        Fixed("Documents", @"C:\Users\me\OneDrive\Documents"),
    ];

    [Fact]
    public void TheFixedPlacesComeFirstAndPinsAlreadyAmongThemAreNotRepeated()
    {
        var list = QuickAccessBookmarks.FirstRun(
            [
                new("Desktop", @"C:\Users\me\Desktop"),
                new("Pictures", @"C:\Users\me\Pictures"),
                new("Documents", @"c:\users\me\onedrive\documents"),
                new("src", @"C:\src"),
            ],
            Always);

        list.Select(b => b.Name).ShouldBe(["Home", "Desktop", "Documents", "Pictures", "src"]);
        list.Take(3).ShouldAllBe(b => b.Group == null);
        list.Skip(3).ShouldAllBe(b => b.Group == QuickAccessBookmarks.Group);
    }

    [Fact]
    public void TwoFoldersOfOneNameAreToldApartByWhereTheyAre()
    {
        var list = QuickAccessBookmarks.FirstRun([new("Documents", @"C:\Users\me\Documents")], Always);

        list.Last().Name.ShouldBe("Documents (me)");
        list.Last().Uri.ShouldBe(FsUri.Local(@"C:\Users\me\Documents").ToString());
    }

    [Fact]
    public void ImportingAgainAddsOnlyWhatIsNotBookmarkedAndTakesNoNameInUse()
    {
        Bookmark[] existing = [.. Always, Fixed("work", @"D:\work")];

        var added = QuickAccessBookmarks.NotYetBookmarked(
            [new("work", @"D:\work"), new("work", @"E:\work"), new("Pictures", @"C:\Users\me\Pictures")],
            existing);

        added.Select(b => b.Name).ShouldBe(["work (E:)", "Pictures"]);
    }

    [Fact]
    public void NothingPinnedLeavesTheFixedPlacesAlone() =>
        QuickAccessBookmarks.FirstRun([], Always).ShouldBe(Always);

    [Fact]
    public async Task AFirstRunSeedsFromQuickAccess()
    {
        using var store = Open();
        var pinned = Directory.CreateDirectory(Path.Combine(_root, "pinned")).FullName;

        var service = new BookmarkService(store, quickAccess: new Pinned([new("pinned", pinned)]));

        (await service.FirstRun!).ShouldBe(1);
        service.Bookmarks.ShouldContain(b => b.Name == "pinned" && b.Group == QuickAccessBookmarks.Group);
    }

    [Fact]
    public async Task ASlowExplorerStillGetsTheFixedPlaces()
    {
        using var store = Open();

        var service = new BookmarkService(store, quickAccess: new Never());

        (await service.FirstRun!).ShouldBe(0);
        service.Bookmarks.ShouldNotContain(b => b.Group == QuickAccessBookmarks.Group);
        File.Exists(Path.Combine(_root, "bookmarks.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task AFileThatArrivesWhileExplorerAnswersIsNotWrittenOver()
    {
        using var store = Open();
        var answer = new TaskCompletionSource<IReadOnlyList<PinnedFolder>>();
        var service = new BookmarkService(store, quickAccess: new Answers(answer.Task));

        // Put back by hand, or by a restore, in the moments before Explorer answers.
        var path = Path.Combine(_root, "bookmarks.json");
        File.WriteAllText(path, """{ "bookmarks": [ { "name": "Mine", "uri": "file:///C:/mine" } ] }""");
        answer.SetResult([new("x", @"C:\x")]);

        (await service.FirstRun!).ShouldBe(0);
        File.ReadAllText(path).ShouldContain("Mine");
        File.ReadAllText(path).ShouldNotContain("Desktop");
    }

    [Fact]
    public async Task ChangesAtOnceAreAllKept()
    {
        using var store = Open();
        var service = new BookmarkService(store);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(n => Task.Run(() => service.AddAsync($"b{n}", FsUri.Local($@"C:\b{n}")))));

        service.Bookmarks.Count(b => b.Name.StartsWith('b')).ShouldBe(20);
    }

    [Fact]
    public void AnExistingFileIsNoFirstRun()
    {
        File.WriteAllText(Path.Combine(_root, "bookmarks.json"), """{ "bookmarks": [] }""");
        using var store = Open();

        new BookmarkService(store, quickAccess: new Pinned([new("x", @"C:\x")])).FirstRun.ShouldBeNull();
    }

    private JsonConfigStore<BookmarksFile> Open() =>
        new(_root, "bookmarks.json", NullLogger<BookmarksFile>.Instance);

    /// <summary>
    /// A few tries: a file written by the test is still being re-read by the store's watcher when
    /// the test ends, and under load the delete met it open (a full run, 2026-10-15).
    /// </summary>
    public void Dispose()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class Pinned(IReadOnlyList<PinnedFolder> folders) : IQuickAccess
    {
        public Task<IReadOnlyList<PinnedFolder>> PinnedAsync(CancellationToken ct) => Task.FromResult(folders);
    }

    private sealed class Answers(Task<IReadOnlyList<PinnedFolder>> answer) : IQuickAccess
    {
        public Task<IReadOnlyList<PinnedFolder>> PinnedAsync(CancellationToken ct) => answer;
    }

    /// <summary>A shell that never answers: the deadline has to end the wait.</summary>
    private sealed class Never : IQuickAccess
    {
        public Task<IReadOnlyList<PinnedFolder>> PinnedAsync(CancellationToken ct) => new TaskCompletionSource<IReadOnlyList<PinnedFolder>>().Task;
    }
}
