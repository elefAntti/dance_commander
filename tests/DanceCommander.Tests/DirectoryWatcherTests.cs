using DanceCommander.Core.Fs;
using DanceCommander.Core.Watching;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// Integration tests against a real directory. These exercise the behaviour that makes live
/// updating trustworthy rather than merely present (§6.1).
/// </summary>
public sealed class DirectoryWatcherTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateTempSubdirectory("dc-watch-").FullName;
    private readonly LocalFileSystemProvider _provider = new();
    private readonly List<DirectoryChange> _changes = [];
    private readonly List<IReadOnlyList<DirectoryChange>> _batches = [];
    private readonly IDirectoryWatcher _watcher;

    private TaskCompletionSource _batchArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DirectoryWatcherTests()
    {
        _watcher = _provider.Watch(FsUri.Local(_root))!;
        _watcher.Changed += (_, batch) =>
        {
            lock (_changes)
            {
                _changes.AddRange(batch.Changes);
                _batches.Add(batch.Changes.ToList());
            }
            _batchArrived.TrySetResult();
        };
        _watcher.Start();
        Thread.Sleep(150); // let the OS arm the handle before the test writes anything
    }

    public void Dispose()
    {
        _watcher.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Path(string name) => System.IO.Path.Combine(_root, name);

    private async Task<IReadOnlyList<DirectoryChange>> WaitForChangesAsync(int atLeast = 1)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_changes)
            {
                if (_changes.Count >= atLeast)
                    return _changes.ToList();
            }

            await _batchArrived.Task.WaitAsync(Timeout);
            _batchArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        lock (_changes)
            return _changes.ToList();
    }

    [Fact]
    public async Task ReportsANewFileWithItsMetadataAlreadyFilledIn()
    {
        File.WriteAllText(Path("fresh.txt"), "hello");

        var changes = await WaitForChangesAsync();
        var change = changes.ShouldHaveSingleItem();

        change.Kind.ShouldBe(DirectoryChangeKind.Added);
        change.Name.ShouldBe("fresh.txt");
        // Size is read at flush time, so the row never appears as an empty file (§6.1).
        change.Entry.ShouldNotBeNull().Size.ShouldBe(5);
    }

    [Fact]
    public async Task ReportsDeletion()
    {
        File.WriteAllText(Path("doomed.txt"), "x");
        await WaitForChangesAsync();

        lock (_changes)
            _changes.Clear();
        File.Delete(Path("doomed.txt"));

        var changes = await WaitForChangesAsync();
        changes.ShouldContain(c => c.Name == "doomed.txt" && c.Kind == DirectoryChangeKind.Removed);
    }

    [Fact]
    public async Task ReportsARenameAsBothARemovalAndAnAddition()
    {
        File.WriteAllText(Path("before.txt"), "x");
        await WaitForChangesAsync();

        lock (_changes)
            _changes.Clear();
        File.Move(Path("before.txt"), Path("after.txt"));

        var changes = await WaitForChangesAsync(atLeast: 2);
        changes.ShouldContain(c => c.Name == "before.txt" && c.Kind == DirectoryChangeKind.Removed);
        changes.ShouldContain(c => c.Name == "after.txt" && c.Kind != DirectoryChangeKind.Removed);
    }

    [Fact]
    public async Task CollapsesAStormOfWritesIntoOneChangePerFile()
    {
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path("busy.log"), new string('x', i + 1));

        await WaitForChangesAsync();
        await Task.Delay(600); // past the quiet window, so any stragglers have landed

        List<IReadOnlyList<DirectoryChange>> batches;
        lock (_changes)
            batches = _batches.ToList();

        // Twenty writes must not become twenty row updates. A loaded machine can pause the loop
        // past the 60 ms quiet window and split the storm into a few batches, which is by
        // design, so what is checked is that no batch repeats the file and the storm collapsed.
        batches.ShouldAllBe(b => b.Count(c => c.Name == "busy.log") <= 1);
        batches.Sum(b => b.Count(c => c.Name == "busy.log")).ShouldBeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task IgnoresAFileThatIsCreatedAndDeletedWithinTheSameWindow()
    {
        File.WriteAllText(Path("temp.tmp"), "x");
        File.Delete(Path("temp.tmp"));

        await Task.Delay(800);

        lock (_changes)
        {
            // Either nothing at all, or a removal — but never a row for a file that is gone.
            _changes.ShouldNotContain(c => c.Name == "temp.tmp" && c.Kind == DirectoryChangeKind.Added);
        }
    }

    [Fact]
    public async Task ReportsNewDirectories()
    {
        Directory.CreateDirectory(Path("sub"));

        var changes = await WaitForChangesAsync();
        var change = changes.ShouldHaveSingleItem();
        change.Name.ShouldBe("sub");
        change.Entry.ShouldNotBeNull().IsDirectory.ShouldBeTrue();
    }

    [Fact]
    public async Task AWatchedFolderRenamedSaysWhereItWent()
    {
        var shown = Directory.CreateDirectory(Path("shown")).FullName;
        using var watcher = _provider.Watch(FsUri.Local(shown))!;
        var moved = new TaskCompletionSource<FsUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Moved += (_, to) => moved.TrySetResult(to);
        watcher.Start();
        Thread.Sleep(150);

        Directory.Move(shown, Path("renamed"));

        (await moved.Task.WaitAsync(Timeout)).ShouldBe(FsUri.Local(Path("renamed")));
    }

    [Fact]
    public async Task AWatchedFolderMovedAwaySaysItIsGone()
    {
        // The Recycle Bin is a move away, to Windows: a watch inside the folder follows it silently.
        var shown = Directory.CreateDirectory(Path("shown")).FullName;
        Directory.CreateDirectory(Path("elsewhere"));
        using var watcher = _provider.Watch(FsUri.Local(shown))!;
        var moved = new TaskCompletionSource<FsUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Moved += (_, to) => moved.TrySetResult(to);
        watcher.Start();
        Thread.Sleep(150);

        Directory.Move(shown, System.IO.Path.Combine(Path("elsewhere"), "shown"));

        (await moved.Task.WaitAsync(Timeout)).ShouldBeNull();
    }
}
