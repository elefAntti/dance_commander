using System.IO.Compression;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;
using DanceCommander.Providers.Archive;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Copies nobody changed go to the Recycle Bin after a week; a changed copy is the user's work,
/// said once and left; anything the tidy can't be sure of stays (DESIGN.md §1.6).
/// </summary>
public sealed class OpenedCopiesTidyTests : IDisposable
{
    private static readonly DateTimeOffset Later = DateTimeOffset.UtcNow + OpenedCopies.KeptFor + TimeSpan.FromDays(1);

    private readonly string _root = Directory.CreateTempSubdirectory("dc-tidy-").FullName;
    private readonly IProviderRegistry _providers;
    private readonly OpenedCopies _copies;
    private readonly Bin _bin;

    public OpenedCopiesTidyTests()
    {
        IProviderRegistry? registry = null;
        var archives = new ArchiveProvider(() => registry!, new ArchiveCache(() => registry!));
        registry = new ProviderRegistry([new LocalFileSystemProvider(), archives]);
        _providers = registry;
        _copies = new OpenedCopies(registry, Path.Combine(_root, "opened"));
        _bin = new Bin(Path.Combine(_root, "bin"));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AnUntouchedCopyGoesToTheBinAfterAWeekAndItsFolderWithIt()
    {
        var copy = await OpenAsync();

        var tidied = await _copies.TidyAsync(_bin, Later, CancellationToken.None);

        tidied.Recycled.ShouldBe(1);
        tidied.Changed.ShouldBeEmpty();
        _bin.Taken.ShouldBe([copy]);
        Directory.Exists(Path.GetDirectoryName(copy)).ShouldBeFalse();
    }

    [Fact]
    public async Task ACopyWithinTheWeekStays()
    {
        var copy = await OpenAsync();

        var tidied = await _copies.TidyAsync(_bin, DateTimeOffset.UtcNow, CancellationToken.None);

        tidied.Recycled.ShouldBe(0);
        File.Exists(copy).ShouldBeTrue();
    }

    [Fact]
    public async Task AChangedCopyIsSaidOnceAndNeverTaken()
    {
        var copy = await OpenAsync();
        File.SetAttributes(copy, FileAttributes.Normal);
        await File.AppendAllTextAsync(copy, " with my notes");

        var first = await _copies.TidyAsync(_bin, Later, CancellationToken.None);
        // Not yet seen (the window closed before the notice was answered): said again.
        var again = await _copies.TidyAsync(_bin, Later, CancellationToken.None);
        OpenedCopies.MarkTold(copy);
        var afterSeen = await _copies.TidyAsync(_bin, Later, CancellationToken.None);

        first.Changed.Select(c => c.Path).ShouldBe([copy]);
        first.Changed[0].From.ShouldNotBeNull().ShouldEndWith("report.txt");
        again.Changed.Select(c => c.Path).ShouldBe([copy]);
        afterSeen.Changed.ShouldBeEmpty();
        _bin.Taken.ShouldBeEmpty();
        (await File.ReadAllTextAsync(copy)).ShouldBe("the report with my notes");
    }

    [Fact]
    public async Task ACopyNamedLikeTheRecordIsNotTakenForIt()
    {
        var copy = await OpenAsync(".dc-opened");

        Path.GetFileName(copy).ShouldBe("_.dc-opened");
        (await _copies.TidyAsync(_bin, Later, CancellationToken.None)).Recycled.ShouldBe(1);
        _bin.Taken.ShouldBe([copy]);
    }

    [Fact]
    public async Task ACopyAProgramHasOpenStays()
    {
        var copy = await OpenAsync();
        using var held = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);

        await _copies.TidyAsync(_bin, Later, CancellationToken.None);

        _bin.Taken.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhereThereIsNoBinOrItIsTooSmallTheCopyStays()
    {
        await OpenAsync();

        await _copies.TidyAsync(_bin with { Recycles = false }, Later, CancellationToken.None);
        await _copies.TidyAsync(_bin with { Capacity = 1 }, Later, CancellationToken.None);

        _bin.Taken.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACopyTheUserMovedAwayLeavesNothingOfOursBehind()
    {
        var copy = await OpenAsync();
        File.Move(copy, Path.Combine(_root, "kept.txt"));

        await _copies.TidyAsync(_bin, Later, CancellationToken.None);

        Directory.Exists(Path.GetDirectoryName(copy)).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "kept.txt")).ShouldBeTrue();
        _bin.Taken.ShouldBeEmpty();
    }

    [Fact]
    public async Task SomethingElsePutInTheFolderKeepsTheFolder()
    {
        var copy = await OpenAsync();
        var mine = Path.Combine(Path.GetDirectoryName(copy)!, "mine.txt");
        await File.WriteAllTextAsync(mine, "mine");

        await _copies.TidyAsync(_bin, Later, CancellationToken.None);

        _bin.Taken.ShouldBe([copy]);
        File.Exists(mine).ShouldBeTrue();
    }

    [Fact]
    public async Task AFolderWithoutARecordIsNotOurs()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_copies.Root, "someone")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "not a copy");

        await _copies.TidyAsync(_bin, Later, CancellationToken.None);

        File.Exists(Path.Combine(folder, "notes.txt")).ShouldBeTrue();
        _bin.Taken.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARecordCannotPointOutsideItsFolder()
    {
        var copy = await OpenAsync();
        var outside = Path.Combine(_copies.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "x");
        var record = Path.Combine(Path.GetDirectoryName(copy)!, ".dc-opened");
        var lines = await File.ReadAllLinesAsync(record);
        File.SetAttributes(record, FileAttributes.Normal);
        lines[0] = "..\\outside.txt";
        lines[1] = "1";
        lines[2] = File.GetLastWriteTimeUtc(outside).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllLinesAsync(record, lines);

        await _copies.TidyAsync(_bin, Later, CancellationToken.None);

        File.Exists(outside).ShouldBeTrue();
        _bin.Taken.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADownloadThatStoppedLongAgoGoesToTheBin()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_copies.Root, "stopped")).FullName;
        var part = Path.Combine(folder, "big.iso.part");
        await File.WriteAllTextAsync(part, "half");

        (await _copies.TidyAsync(_bin, DateTimeOffset.UtcNow, CancellationToken.None)).Recycled.ShouldBe(0);
        (await _copies.TidyAsync(_bin, Later, CancellationToken.None)).Recycled.ShouldBe(1);

        _bin.Taken.ShouldBe([part]);
        Directory.Exists(folder).ShouldBeFalse();
    }

    [Fact]
    public async Task AJunctionAmongTheCopiesIsNotFollowed()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var stopped = Directory.CreateDirectory(Path.Combine(elsewhere, "stopped")).FullName;
        var part = Path.Combine(stopped, "someone's.part");
        await File.WriteAllTextAsync(part, "not ours");
        Directory.CreateDirectory(_copies.Root);
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", ["/c", "mklink", "/J", Path.Combine(_copies.Root, "link"), stopped])
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mklink.WaitForExit();

        await _copies.TidyAsync(_bin, Later, CancellationToken.None);

        File.Exists(part).ShouldBeTrue();
        _bin.Taken.ShouldBeEmpty();

        // The link itself, not what it leads to, before the fixture's recursive delete meets it.
        Directory.Delete(Path.Combine(_copies.Root, "link"));
    }

    [Fact]
    public void TheDriveTheCopiesGoToHasRoomToTell() =>
        _copies.Room().ShouldNotBeNull().Free.ShouldBeGreaterThan(0);

    private async Task<string> OpenAsync(string name = "report.txt")
    {
        var zip = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(name).Open()))
            writer.Write("the report");

        var uri = FsUri.ArchiveEntry(FsUri.Local(zip), name);
        var entry = (await _providers.Resolve(uri).StatAsync(uri, CancellationToken.None)).ShouldNotBeNull();
        return (await _copies.FetchAsync(entry, null, CancellationToken.None)).ShouldNotBeNull().Path;
    }

    /// <summary>Takes files into a folder of its own, where the test can see them.</summary>
    /// <remarks>A record so <c>with</c> makes a variant that still shares <see cref="Taken"/>.</remarks>
    private sealed record Bin(string Folder) : IRecycleBin
    {
        public bool Recycles { get; init; } = true;

        public long? Capacity { get; init; }

        public List<string> Taken { get; init; } = [];

        public bool CanRecycle(FsUri uri) => Recycles;

        public long? CapacityOf(FsUri uri) => Capacity;

        public Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct)
        {
            Directory.CreateDirectory(Folder);
            foreach (var path in paths.Select(p => p.ToLocalPath()))
            {
                File.Move(path, Path.Combine(Folder, $"{Guid.NewGuid():N}-{Path.GetFileName(path)}"));
                Taken.Add(path);
            }
            return Task.CompletedTask;
        }
    }
}
