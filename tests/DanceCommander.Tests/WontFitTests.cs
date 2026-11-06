using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// What the Recycle Bin wouldn't take is asked about here, inline, before anything goes: Windows
/// destroyed it after a modal box of its own (2026-10-17).
/// </summary>
public sealed class WontFitTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-wontfit-").FullName;
    private readonly UndoStack _undo = new();
    private readonly ActivityLog _activity = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private FsEntry Make(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, name);
        return new FsEntry(FsUri.Local(path), name, false, name.Length, null);
    }

    private FileOperationService Operations(IRecycleBin bin) =>
        new(new ProviderRegistry([new LocalFileSystemProvider()]), _undo, _activity, bin);

    [Fact]
    public async Task OneTooLarge_IsAskedAbout_AndCancelSendsNothing()
    {
        var big = Make("big.iso");
        var small = Make("small.txt");
        var bin = new SmallBin("big.iso");
        var decisions = new ScriptedDecisions(choice: "cancel");

        await Operations(bin).DeleteAsync([big, small], permanent: false, decisions, CancellationToken.None);

        var asked = decisions.Asked.ShouldHaveSingleItem();
        asked.Title.ShouldBe("Delete big.iso permanently?");
        asked.Detail.ShouldBe("big.iso is too large. This cannot be undone. The other item goes to the Recycle Bin.");
        File.Exists(big.Uri.ToLocalPath()).ShouldBeTrue();
        File.Exists(small.Uri.ToLocalPath()).ShouldBeTrue();
        bin.Recycled.ShouldBeEmpty();
    }

    [Fact]
    public async Task Confirmed_TheLargeOneIsDeletedAndTheRestRecycled_AndCtrlZSaysSoInThatOrder()
    {
        var big = Make("big.iso");
        var small = Make("small.txt");
        var bin = new SmallBin("big.iso");

        await Operations(bin).DeleteAsync([big, small], permanent: false, new ScriptedDecisions(choice: "delete"), CancellationToken.None);

        File.Exists(big.Uri.ToLocalPath()).ShouldBeFalse();
        bin.Recycled.ShouldBe([small.Uri]);

        // The recycle is the last on the stack, so the first Ctrl+Z brings back what it can.
        (await _undo.UndoAsync(CancellationToken.None))!.Description.ShouldBe("Sent small.txt to the Recycle Bin");
        bin.Restored.ShouldBe([small.Uri]);
        var refused = await Should.ThrowAsync<CannotUndoException>(_undo.UndoAsync(CancellationToken.None));
        refused.Message.ShouldBe("Deleted big.iso permanently: that cannot be undone.");
    }

    [Fact]
    public async Task ABinTurnedOff_IsSaidOnce()
    {
        var a = Make("a.txt");
        var b = Make("b.txt");
        var bin = new SmallBin("a.txt", "b.txt") { Reason = "The Recycle Bin on C: is turned off in its settings." };
        var decisions = new ScriptedDecisions(choice: "cancel");

        await Operations(bin).DeleteAsync([a, b], permanent: false, decisions, CancellationToken.None);

        var asked = decisions.Asked.ShouldHaveSingleItem();
        asked.Title.ShouldBe("Delete 2 items permanently?");
        asked.Detail.ShouldBe("The Recycle Bin on C: is turned off in its settings. This cannot be undone.");
    }

    [Fact]
    public async Task EverythingFits_NothingIsAsked()
    {
        var small = Make("small.txt");
        var bin = new SmallBin();
        var decisions = new ScriptedDecisions(choice: "cancel");

        await Operations(bin).DeleteAsync([small], permanent: false, decisions, CancellationToken.None);

        decisions.Asked.ShouldBeEmpty();
        bin.Recycled.ShouldBe([small.Uri]);
    }

    [Fact]
    public void ALongPathIsOnTheSameVolumeAsItsDrive()
    {
        var deep = Path.Combine(_root, new string('d', 120), new string('e', 120), "f.txt");
        var longPath = FsUri.Local(deep).ToLocalPath();
        longPath.ShouldStartWith(@"\\?\");

        var drive = WindowsRecycleBin.VolumeOf(Path.GetPathRoot(_root)!).ShouldNotBeNull();
        var volume = WindowsRecycleBin.VolumeOf(longPath).ShouldNotBeNull();
        volume.Guid.ShouldBe(drive.Guid);
        drive.Guid.ShouldStartWith("{");
    }

    [Fact]
    public async Task TheRealBinTakesASmallFile()
    {
        var small = Make("small.txt");
        (await new WindowsRecycleBin().WontFitAsync([small], CancellationToken.None)).ShouldBeEmpty();
    }

    /// <summary>Takes everything but the names it is given, which are "too large".</summary>
    private sealed class SmallBin(params string[] tooLarge) : IRecycleBin
    {
        /// <summary>Said of every one, as a bin turned off is; else each is "too large".</summary>
        public string? Reason { get; init; }

        public List<FsUri> Recycled { get; } = [];

        public List<FsUri> Restored { get; } = [];

        public bool CanRecycle(FsUri uri) => true;

        public Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct)
        {
            if (paths.Any(p => tooLarge.Contains(p.Name)))
                throw new InvalidOperationException("Recycled one it was told wouldn't fit.");
            Recycled.AddRange(paths);
            return Task.CompletedTask;
        }

        public Task RestoreAsync(IReadOnlyList<FsUri> paths, DateTimeOffset deletedSince, CancellationToken ct)
        {
            Restored.AddRange(paths);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WontFit>> WontFitAsync(IReadOnlyList<FsEntry> entries, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WontFit>>(
                entries.Where(e => tooLarge.Contains(e.Name)).Select(e => new WontFit(e, Reason ?? $"{e.Name} is too large.")).ToList());
    }
}
