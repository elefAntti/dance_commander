using DanceCommander.Core.Config;
using DanceCommander.Core.Navigation;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-config-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private JsonConfigStore<BookmarksFile> Open() =>
        new(_root, "bookmarks.json", NullLogger.Instance);

    private void WriteRaw(string json) => File.WriteAllText(Path.Combine(_root, "bookmarks.json"), json);

    [Fact]
    public async Task RoundTripsThroughDisk()
    {
        using (var store = Open())
        {
            await store.SaveAsync(new BookmarksFile
            {
                Bookmarks = [new Bookmark("Build logs", "sftp://build01/var/log")],
            });
        }

        using var reopened = Open();
        var bookmark = reopened.Current.Bookmarks.ShouldHaveSingleItem();
        bookmark.Name.ShouldBe("Build logs");
        bookmark.Uri.ShouldBe("sftp://build01/var/log");
    }

    [Fact]
    public void AcceptsCommentsAndTrailingCommasBecauseTheseFilesAreHandEdited()
    {
        WriteRaw("""
            {
              // where the nightly logs land
              "bookmarks": [
                { "name": "Logs", "uri": "sftp://build01/var/log" },
              ],
            }
            """);

        using var store = Open();
        store.Current.Bookmarks.ShouldHaveSingleItem().Name.ShouldBe("Logs");
    }

    [Fact]
    public void KeepsTheLastGoodValueWhenTheFileIsBroken()
    {
        WriteRaw("""{ "bookmarks": [ { "name": "Logs", "uri": "file:///C:/logs" } ] }""");
        using var store = Open();
        store.Current.Bookmarks.ShouldHaveSingleItem();

        // Mid-edit, the file is briefly invalid. Reverting to defaults here would silently throw
        // away whatever the user was typing.
        WriteRaw("""{ "bookmarks": [ { "name": """);

        store.Current.Bookmarks.ShouldHaveSingleItem().Name.ShouldBe("Logs");
    }

    [Fact]
    public void StartsEmptyWhenThereIsNoFileYet()
    {
        using var store = Open();
        store.Current.Bookmarks.ShouldBeEmpty();
    }

    [Fact]
    public async Task WritesAtomicallyWithoutLeavingATempFileBehind()
    {
        using var store = Open();
        await store.SaveAsync(new BookmarksFile { Bookmarks = [new Bookmark("A", "file:///C:/a")] });

        Directory.GetFiles(_root).Select(Path.GetFileName).ShouldBe(["bookmarks.json"]);
    }

    // ---- a file that will not parse ------------------------------------------------------------

    private const string Broken = """
        {
          "bookmarks": [
            { "name": "Work", "uri": "file:///C:/work" }
            { "name": "Logs", "uri": "sftp://build01/var/log" }
          ]
        }
        """;

    [Fact]
    public void ABrokenFileAtStartupIsReportedWithTheLineToLookAt()
    {
        WriteRaw(Broken);

        using var store = Open();

        // It exists — so nothing may treat this as a first run and write defaults over it.
        store.FileExists.ShouldBeTrue();
        store.Current.Bookmarks.ShouldBeEmpty();
        var problem = store.Problem.ShouldNotBeNull();
        problem.Message.ShouldContain("line 4");
        problem.Message.ShouldContain("none of it is loaded");
    }

    [Fact]
    public async Task WritingOverABrokenFileKeepsACopyOfItFirst()
    {
        // Adding a bookmark while bookmarks.json has a missing comma used to throw away whatever
        // the user had been writing, to save one entry of ours.
        WriteRaw(Broken);
        using var store = Open();

        await store.SaveAsync(new BookmarksFile { Bookmarks = [new Bookmark("A", "file:///C:/a")] });

        var aside = Directory.GetFiles(_root, "bookmarks.broken-*.json").ShouldHaveSingleItem();
        File.ReadAllText(aside).ShouldBe(Broken);
        store.Problem.ShouldBeNull();
    }

    [Fact]
    public async Task AGoodFileIsWrittenOverWithoutACopy()
    {
        WriteRaw("""{ "bookmarks": [] }""");
        using var store = Open();

        await store.SaveAsync(new BookmarksFile { Bookmarks = [new Bookmark("A", "file:///C:/a")] });

        Directory.GetFiles(_root).Select(Path.GetFileName).ShouldBe(["bookmarks.json"]);
    }

    [Fact]
    public void AMissingFileIsNotAProblem()
    {
        using var store = Open();

        store.FileExists.ShouldBeFalse();
        store.Problem.ShouldBeNull();
    }

    [Fact]
    public async Task BreakingAFileWhileRunningKeepsTheOldVersionAndSaysSo()
    {
        WriteRaw("""{ "bookmarks": [ { "name": "Work", "uri": "file:///C:/work" } ] }""");
        using var store = Open();
        var reported = new TaskCompletionSource<ConfigProblem>();
        store.ProblemFound += (_, problem) => reported.TrySetResult(problem);

        WriteRaw(Broken);

        var problem = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        problem.Message.ShouldContain("still in use");
        store.Current.Bookmarks.ShouldHaveSingleItem().Name.ShouldBe("Work");
    }

    [Fact]
    public async Task AFileLockedAtStartupIsAProblemAndIsKeptAsideBeforeBeingSavedOver()
    {
        // It loaded as empty and counted as healthy, and bookmarks then saved the defaults over it.
        WriteRaw("""{ "bookmarks": [ { "name": "Mine", "uri": "file:///C:/mine" } ] }""");
        var path = Path.Combine(_root, "bookmarks.json");

        JsonConfigStore<BookmarksFile> store;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            store = Open();

        using (store)
        {
            store.Problem.ShouldNotBeNull();
            await store.SaveAsync(new BookmarksFile { Bookmarks = [new Bookmark("New", "file:///C:/new")] });
        }

        var aside = Directory.GetFiles(_root, "bookmarks.broken-*.json").ShouldHaveSingleItem();
        File.ReadAllText(aside).ShouldContain("Mine");
    }

    [Fact]
    public void BookmarksAreNotReplacedByTheDefaultsWhenTheFileIsThereButEmpty()
    {
        // No bookmarks loaded is also a list the user emptied, or one that failed to load.
        WriteRaw("""{ "bookmarks": [] }""");
        using var store = Open();

        _ = new BookmarkService(store);

        File.ReadAllText(Path.Combine(_root, "bookmarks.json")).ShouldNotContain("Desktop");
    }

    [Fact]
    public async Task AHandEditJustAfterOurOwnSaveIsNotIgnored()
    {
        // A quiet period after each save used to swallow it, and the next save wrote stale memory over it.
        using var store = Open();
        await store.SaveAsync(new BookmarksFile { Bookmarks = [new Bookmark("Ours", "file:///C:/ours")] });

        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, _) => reloaded.TrySetResult();
        await Task.Delay(50);
        WriteRaw("""{ "bookmarks": [ { "name": "Theirs", "uri": "file:///C:/theirs" } ] }""");

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        store.Current.Bookmarks.ShouldHaveSingleItem().Name.ShouldBe("Theirs");
    }
}
