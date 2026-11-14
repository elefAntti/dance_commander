using System.IO.Compression;
using System.Runtime.CompilerServices;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Watching;
using DanceCommander.Providers.Archive;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Enter on a file on a server or in an archive opens a local copy: whole before it takes its
/// name, never over anything, read-only, and marked as from the internet where it came from there.
/// </summary>
public sealed class OpenedCopiesTests : IDisposable
{
    private static readonly DateTimeOffset Written = new(2024, 3, 5, 10, 30, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("dc-open-").FullName;
    private readonly IProviderRegistry _providers;
    private readonly OpenedCopies _copies;

    public OpenedCopiesTests()
    {
        IProviderRegistry? registry = null;
        var archives = new ArchiveProvider(() => registry!, new ArchiveCache(() => registry!));
        registry = new ProviderRegistry([new LocalFileSystemProvider(), archives, new Server()]);
        _providers = registry;
        _copies = new OpenedCopies(registry, Path.Combine(_root, "opened"));
    }

    public void Dispose()
    {
        // The copies are read-only, which a recursive delete refuses.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AnEntryInAnArchiveIsCopiedOutUnderItsOwnName()
    {
        var entry = await EntryAsync(Zip("pack.zip"), "docs/report.txt");

        var path = (await _copies.FetchAsync(entry, null, CancellationToken.None))?.Path;

        path.ShouldNotBeNull();
        Path.GetFileName(path).ShouldBe("report.txt");
        (await File.ReadAllTextAsync(path)).ShouldBe("the report");
        File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly).ShouldBeTrue();
        // Beside it only its record, hidden, which the tidy reads.
        var record = Path.Combine(Path.GetDirectoryName(path)!, ".dc-opened");
        Directory.GetFiles(Path.GetDirectoryName(path)!).Order().ShouldBe(new[] { path, record }.Order());
        File.GetAttributes(record).HasFlag(FileAttributes.Hidden).ShouldBeTrue();
    }

    [Fact]
    public async Task TheCopyKeepsTheDateOfTheFileItCameFrom()
    {
        var path = (await _copies.FetchAsync(Server.Entry, null, CancellationToken.None))?.Path;

        File.GetLastWriteTimeUtc(path!).ShouldBe(Written.UtcDateTime);
    }

    [Fact]
    public async Task TwoOpensOfOneNameNeverMeet()
    {
        var zip = Zip("pack.zip");
        var first = (await _copies.FetchAsync(await EntryAsync(zip, "docs/report.txt"), null, CancellationToken.None))?.Path;
        var second = (await _copies.FetchAsync(await EntryAsync(zip, "docs/report.txt"), null, CancellationToken.None))?.Path;

        second.ShouldNotBe(first);
        File.Exists(first).ShouldBeTrue();
        File.Exists(second).ShouldBeTrue();
    }

    [Fact]
    public async Task AFileFromAServerIsMarkedAsFromTheInternet()
    {
        var path = (await _copies.FetchAsync(Server.Entry, null, CancellationToken.None))?.Path;

        ZoneMark.IsFromInternet(path!).ShouldBeTrue();
    }

    [Fact]
    public async Task AnArchiveOnThisDiskPassesOnItsMarkOrItsLackOfOne()
    {
        var marked = Zip("downloaded.zip");
        ZoneMark.MarkFromInternet(marked).ShouldBeTrue();
        var plain = Zip("made-here.zip");

        var fromMarked = (await _copies.FetchAsync(await EntryAsync(marked, "docs/report.txt"), null, CancellationToken.None))?.Path;
        var fromPlain = (await _copies.FetchAsync(await EntryAsync(plain, "docs/report.txt"), null, CancellationToken.None))?.Path;

        ZoneMark.IsFromInternet(fromMarked!).ShouldBeTrue();
        ZoneMark.IsFromInternet(fromPlain!).ShouldBeFalse();
    }

    [Fact]
    public async Task ADownloadThatStopsLeavesNothingAtTheRealName()
    {
        using var stop = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _copies.FetchAsync(Server.Endless, _ => stop.Cancel(), stop.Token));

        var left = Directory.GetFiles(_copies.Root, "*", SearchOption.AllDirectories);
        left.ShouldAllBe(file => file.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnterAgainWhileItDownloadsDoesNotFetchItTwice()
    {
        using var stop = new CancellationTokenSource();
        Task<OpenedCopy?>? second = null;

        var first = _copies.FetchAsync(Server.Endless, _ =>
        {
            second ??= _copies.FetchAsync(Server.Endless, null, CancellationToken.None);
            stop.Cancel();
        }, stop.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => first);
        (await second!).ShouldBeNull();
    }

    [Fact]
    public async Task AServersNameCannotChooseTheFolder()
    {
        var path = (await _copies.FetchAsync(Server.Entry with { Name = "..\\..\\evil.txt" }, null, CancellationToken.None))?.Path;

        path.ShouldNotBeNull();
        Path.GetDirectoryName(Path.GetDirectoryName(path)).ShouldBe(_copies.Root);
        Path.GetFileName(path).ShouldBe("evil.txt");
    }

    private string Zip(string name)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("docs/report.txt").Open()))
            writer.Write("the report");
        return path;
    }

    private async Task<FsEntry> EntryAsync(string zip, string inside)
    {
        var uri = FsUri.ArchiveEntry(FsUri.Local(zip), inside);
        return (await _providers.Resolve(uri).StatAsync(uri, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ACopyCutShortIsNeitherNamedNorOpened()
    {
        await Should.ThrowAsync<IOException>(() => _copies.FetchAsync(Server.Cut, null, CancellationToken.None));

        Directory.GetFiles(_copies.Root, "*", SearchOption.AllDirectories)
            .ShouldAllBe(file => file.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileThatShrankSinceItWasListedIsWhole()
    {
        var path = (await _copies.FetchAsync(Server.Shrunk, null, CancellationToken.None))?.Path;

        (await File.ReadAllTextAsync(path!)).ShouldBe("hello");
    }

    [Fact]
    public async Task ADateWindowsCannotHoldLeavesTodays()
    {
        var path = (await _copies.FetchAsync(Server.Entry with { Modified = DateTimeOffset.MinValue }, null, CancellationToken.None))?.Path;

        File.Exists(path).ShouldBeTrue();
    }

    /// <summary>A server with a small file, one that never ends, one cut short and one that shrank.</summary>
    private sealed class Server : IFileSystemProvider
    {
        public static readonly FsEntry Entry = new(FsUri.Remote("test", "host", "/notes.txt"), "notes.txt", false, 5, Written);

        public static readonly FsEntry Endless = new(FsUri.Remote("test", "host", "/big.iso"), "big.iso", false, long.MaxValue, Written);

        /// <summary>Ten bytes listed and still there, five sent: a connection that ended quietly.</summary>
        public static readonly FsEntry Cut = new(FsUri.Remote("test", "host", "/cut.txt"), "cut.txt", false, 10, Written);

        /// <summary>Ten bytes when listed, five now.</summary>
        public static readonly FsEntry Shrunk = new(FsUri.Remote("test", "host", "/shrunk.txt"), "shrunk.txt", false, 10, Written);

        public string Scheme => "test";

        public ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.Remote;

        public ColumnSet DefaultColumns => ColumnSet.Standard;

        public IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, CancellationToken ct) => Empty();

        public Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct) => Task.FromResult(
            path == Cut.Uri ? Cut : path == Shrunk.Uri ? Shrunk with { Size = 5 } : null);

        public Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct) =>
            Task.FromResult<Stream>(file == Endless.Uri ? new Zeros() : new MemoryStream("hello"u8.ToArray()));

        public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct) => throw new NotSupportedException();

        public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct) => throw new NotSupportedException();

        public IDirectoryWatcher? Watch(FsUri directory) => null;

        private static async IAsyncEnumerable<FsEntry> Empty([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>Bytes for as long as anyone reads.</summary>
    private sealed class Zeros : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => count;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
