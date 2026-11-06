using DanceCommander.Core.Config;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Listing;
using DanceCommander.Providers.Local;
using DanceCommander.Providers.Sftp;
using DanceCommander.Providers.WebDav;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// The remote folders a pane can show at once while it lists them again (PLAN.md §5, "Remote
/// listings, remembered and fetched ahead").
/// </summary>
public sealed class ListingMemoryTests
{
    private static readonly DateTime At = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static FsUri Folder(string name) => FsUri.Parse($"davs://cloud.example/files/{name}");

    private static FsEntry[] Rows(FsUri folder, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new FsEntry(folder.Child($"f{i}.txt"), $"f{i}.txt", false, i, null))
            .ToArray();

    [Fact]
    public void AFolderComesBackAsItWasListed()
    {
        var memory = new ListingMemory();
        var folder = Folder("a");
        memory.Remember(folder, Rows(folder, 3), At);

        var recalled = memory.Recall(folder).ShouldNotBeNull();
        recalled.Entries.Select(e => e.Name).ShouldBe(["f0.txt", "f1.txt", "f2.txt"]);
        recalled.ListedAt.ShouldBe(At);
        memory.Recall(Folder("b")).ShouldBeNull();
    }

    [Fact]
    public void TheLeastRecentlyUsedGoesFirst()
    {
        var memory = new ListingMemory(capacity: 2);
        memory.Remember(Folder("a"), [], At);
        memory.Remember(Folder("b"), [], At);

        // Going back to a keeps it; c then pushes out b, the one not visited since.
        memory.Recall(Folder("a")).ShouldNotBeNull();
        memory.Remember(Folder("c"), [], At);

        memory.Recall(Folder("a")).ShouldNotBeNull();
        memory.Recall(Folder("b")).ShouldBeNull();
        memory.Recall(Folder("c")).ShouldNotBeNull();
    }

    [Fact]
    public void AFolderTooLargeIsNotHeldAndDropsItsOlderListing()
    {
        var memory = new ListingMemory(largest: 5);
        var folder = Folder("a");
        memory.Remember(folder, Rows(folder, 3), At);

        // Grown past the limit: the old, smaller listing would now be wrong by a lot, so it goes too.
        memory.Remember(folder, Rows(folder, 6), At.AddMinutes(1));
        memory.Recall(folder).ShouldBeNull();
    }

    [Fact]
    public void AFolderThatCouldNotBeListedIsForgotten()
    {
        var memory = new ListingMemory();
        memory.Remember(Folder("a"), [], At);
        memory.Forget(Folder("a"));
        memory.Recall(Folder("a")).ShouldBeNull();
    }

    [Fact]
    public void OnlyServersAreRemembered()
    {
        ListingMemory.Covers(Folder("a"), new WebDavSecureFileSystemProvider(null!)).ShouldBeTrue();
        ListingMemory.Covers(FsUri.Parse("sftp://me@host/home/me"), new SftpFileSystemProvider(null!)).ShouldBeTrue();

        // A local folder lists in milliseconds and has a watcher: remembering it adds only a way to be wrong.
        ListingMemory.Covers(FsUri.Local(@"C:\work"), new LocalFileSystemProvider()).ShouldBeFalse();
        ListingMemory.Covers(Folder("a"), null).ShouldBeFalse();
    }

    // ---- kept between sessions ---------------------------------------------------------------

    private sealed class Store(ListingsFile file) : IConfigStore<ListingsFile>
    {
        public ListingsFile Current { get; private set; } = file;

        public event EventHandler<ListingsFile>? Changed { add { } remove { } }

        // Through JSON as the real file is, so the round trip is the one a restart makes.
        public Task SaveAsync(ListingsFile value, CancellationToken ct = default)
        {
            var options = JsonConfigStore<ListingsFile>.SerializerOptions;
            Current = System.Text.Json.JsonSerializer.Deserialize<ListingsFile>(
                System.Text.Json.JsonSerializer.Serialize(value, options), options)!;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void FoldersComeBackAfterARestartAsTheyWere()
    {
        var store = new Store(new ListingsFile());
        var odd = Folder("odd names");
        var entries = new[]
        {
            // Names a server allows and an address could get wrong.
            new FsEntry(odd.Child("a b#c.txt"), "a b#c.txt", false, 12, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero)),
            new FsEntry(odd.Child("ä%20x"), "ä%20x", true, 0, null),
        };
        using (var first = new ListingMemory(store: store))
        {
            first.Remember(Folder("older"), Rows(Folder("older"), 2), At);
            first.Remember(odd, entries, At.AddMinutes(1));
        }

        using var second = new ListingMemory(store: store);
        var back = second.Recall(odd).ShouldNotBeNull();
        back.ListedAt.ShouldBe(At.AddMinutes(1));
        back.Entries.ShouldBe(entries, ignoreOrder: false);
        second.Recall(Folder("older")).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("../x.txt")]
    [InlineData("b/x.txt")]
    [InlineData("..")]
    [InlineData("")]
    public void ASavedNameThatIsNotOneNameDropsTheFolder(string name)
    {
        // A damaged or hand-edited file must not hand a row another place's address: Del acts on it.
        var file = new ListingsFile
        {
            Folders =
            [
                new SavedListing
                {
                    Uri = Folder("a").ToString(),
                    ListedAt = At,
                    Entries = [new SavedEntry { Name = "fine.txt" }, new SavedEntry { Name = name }],
                },
            ],
        };

        using var memory = new ListingMemory(store: new Store(file));
        memory.Recall(Folder("a")).ShouldBeNull();
    }

    [Fact]
    public void AnOlderAnswerArrivingLateDoesNotReplaceANewerOne()
    {
        // A slow fetch asked at 12:00 finishing after a listing asked at 12:01.
        var memory = new ListingMemory();
        var folder = Folder("a");
        memory.Remember(folder, Rows(folder, 2), At.AddMinutes(1));
        memory.Remember(folder, Rows(folder, 5), At);

        memory.Recall(folder).ShouldNotBeNull().Entries.Count.ShouldBe(2);
    }

    [Fact]
    public void AListingSavedInTheFutureIsNotKept()
    {
        // The clock set back since, or a file from elsewhere: its age means nothing.
        var file = new ListingsFile
        {
            Folders = [new SavedListing { Uri = Folder("a").ToString(), ListedAt = DateTimeOffset.UtcNow.AddHours(1) }],
        };

        using var memory = new ListingMemory(store: new Store(file));
        memory.Recall(Folder("a")).ShouldBeNull();
    }

    [Fact]
    public void AFolderSavedInAnyOtherSpellingIsNotKept()
    {
        // Rows take their address from the folder, user name and all, so the folder has to be
        // exactly what this application wrote, not something that parses to an equal address.
        var written = FsUri.Parse("sftp://alice@host/home/alice").ToString();
        var respelled = written.Replace("sftp://", "SFTP://", StringComparison.Ordinal);
        FsUri.Parse(respelled).ToString().ShouldBe(written);

        var file = new ListingsFile
        {
            Folders = [new SavedListing { Uri = respelled, ListedAt = At, Entries = [new SavedEntry { Name = "x.txt" }] }],
        };

        using var memory = new ListingMemory(store: new Store(file));
        memory.Recall(FsUri.Parse(written)).ShouldBeNull();
    }

    [Fact]
    public void TurnedOffItForgetsOnDiskToo()
    {
        var store = new Store(new ListingsFile());
        var keep = true;
        using (var memory = new ListingMemory(store: store, keep: () => keep))
            memory.Remember(Folder("a"), Rows(Folder("a"), 1), At);
        store.Current.Folders.ShouldHaveSingleItem();

        keep = false;
        using (new ListingMemory(store: store, keep: () => keep))
        {
        }

        store.Current.Folders.ShouldBeEmpty();
    }

    [Fact]
    public void OnlyTheFoldersPeopleMoveThroughAreWritten()
    {
        var store = new Store(new ListingsFile());
        using (var memory = new ListingMemory(store: store))
        {
            memory.Remember(Folder("small"), Rows(Folder("small"), 10), At);
            memory.Remember(Folder("big"), Rows(Folder("big"), 1_001), At);
        }

        store.Current.Folders.Select(f => f.Uri).ShouldBe([Folder("small").ToString()]);
    }

    // ---- fetched ahead ------------------------------------------------------------------------

    private sealed class Server(ProviderCapabilities extra = ProviderCapabilities.Remote) : IFileSystemProvider
    {
        public TaskCompletionSource Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Listings;

        public IDecisionHost? AskedIn;

        public string Scheme => "davs";
        public ProviderCapabilities Capabilities => ProviderCapabilities.Read | extra;
        public DanceCommander.Core.Columns.ColumnSet DefaultColumns => DanceCommander.Core.Columns.ColumnSet.Standard;

        public async IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref Listings);
            AskedIn = DecisionScope.Current;
            await Answer.Task.WaitAsync(ct);
            yield return new FsEntry(directory.Child("x.txt"), "x.txt", false, 1, null);
        }

        public Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct) => throw new NotSupportedException();
        public DanceCommander.Core.Watching.IDirectoryWatcher? Watch(FsUri directory) => null;
    }

    private static (ListingPrefetcher Prefetcher, ListingMemory Memory) Prefetching(Server server, bool on = true)
    {
        var memory = new ListingMemory();
        return (new ListingPrefetcher(new ProviderRegistry([server]), memory, () => on,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ListingPrefetcher>.Instance), memory);
    }

    [Fact]
    public async Task AFolderFetchedAheadIsRememberedAndNoQuestionIsAsked()
    {
        var server = new Server();
        var (prefetcher, memory) = Prefetching(server);

        prefetcher.Ahead(Folder("a"), TimeSpan.Zero);
        var joined = prefetcher.Join(Folder("a"), CancellationToken.None).ShouldNotBeNull();
        server.Answer.SetResult();

        (await joined).ShouldNotBeNull().Entries.Single().Name.ShouldBe("x.txt");
        memory.Recall(Folder("a")).ShouldNotBeNull();
        server.Listings.ShouldBe(1);

        // Nobody asked for it, so a password prompt it raised would come out of nowhere.
        server.AskedIn.ShouldBe(NoOneAsks.Instance);
    }

    [Fact]
    public async Task MovingOnCancelsTheFetchThatWasWaitingForTheCursorToRest()
    {
        var server = new Server();
        var (prefetcher, memory) = Prefetching(server);

        prefetcher.Ahead(Folder("a"), TimeSpan.FromSeconds(30));
        prefetcher.Ahead(Folder("b"), TimeSpan.FromSeconds(30));
        prefetcher.Cancel();
        server.Answer.SetResult();
        await Task.Delay(50);

        server.Listings.ShouldBe(0);
        memory.Recall(Folder("a")).ShouldBeNull();
        prefetcher.Join(Folder("b"), CancellationToken.None).ShouldBeNull();
    }

    [Fact]
    public async Task OpeningTheFolderTakesTheFetchOverWithoutWaitingForTheRest()
    {
        var server = new Server();
        var (prefetcher, _) = Prefetching(server);
        server.Answer.SetResult();

        prefetcher.Ahead(Folder("a"), TimeSpan.FromMinutes(5));
        var joined = prefetcher.Join(Folder("a"), CancellationToken.None).ShouldNotBeNull();

        (await joined.WaitAsync(TimeSpan.FromSeconds(5))).ShouldNotBeNull();
        server.Listings.ShouldBe(1);
    }

    [Fact]
    public void NothingIsFetchedAheadWhenTurnedOffOrNotOnAServer()
    {
        var off = new Server();
        var (prefetcher, _) = Prefetching(off, on: false);
        prefetcher.Ahead(Folder("a"), TimeSpan.Zero);
        prefetcher.Join(Folder("a"), CancellationToken.None).ShouldBeNull();

        var local = new Server(extra: ProviderCapabilities.None);
        (prefetcher, _) = Prefetching(local);
        prefetcher.Ahead(Folder("a"), TimeSpan.Zero);
        prefetcher.Join(Folder("a"), CancellationToken.None).ShouldBeNull();
    }
}
