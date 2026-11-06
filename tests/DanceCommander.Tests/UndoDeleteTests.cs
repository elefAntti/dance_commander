using DanceCommander.Core.Fs;
using DanceCommander.Core.Operations;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A delete is on the undo stack like every other mutation (DESIGN.md §1.2/6). Ctrl+Z after one
/// used to reach past it and undo whatever came before.
/// </summary>
public sealed class UndoDeleteTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-undo-").FullName;

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

    private static FsEntry Entry(string path, bool directory = false) =>
        new(FsUri.Local(path), Path.GetFileName(path), directory, 1, null);

    /// <summary>Whether the bin holds a record of <paramref name="path"/> with its content beside it.</summary>
    private static bool InTheBin(string path)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var bin = Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin", sid);
        return Directory.EnumerateFiles(bin, "$I*").Any(info =>
            WindowsRecycleBin.ReadRecord(info) is { } record &&
            string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase) &&
            Path.Exists(Path.Combine(bin, "$R" + Path.GetFileName(info)[2..])));
    }

    [Fact]
    public async Task TheRealRecycleBinGivesBackAFileAndAFolder()
    {
        var file = Path.Combine(_root, "report.txt");
        File.WriteAllText(file, "the report");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "drafts")).FullName;
        File.WriteAllText(Path.Combine(folder, "one.txt"), "1");

        var bin = new WindowsRecycleBin();
        var since = DateTimeOffset.Now;
        await bin.RecycleAsync([FsUri.Local(file), FsUri.Local(folder)], CancellationToken.None);
        File.Exists(file).ShouldBeFalse();
        Directory.Exists(folder).ShouldBeFalse();

        // In the bin, not deleted outright: the shell's flags were changed (2026-10-17), and a
        // wrong one there would destroy what the user meant to keep a way back to.
        InTheBin(file).ShouldBeTrue("the file was deleted outright, not recycled");
        InTheBin(folder).ShouldBeTrue("the folder was deleted outright, not recycled");

        await bin.RestoreAsync([FsUri.Local(file), FsUri.Local(folder)], since, CancellationToken.None);

        File.ReadAllText(file).ShouldBe("the report");
        File.ReadAllText(Path.Combine(folder, "one.txt")).ShouldBe("1");
    }

    [Fact]
    public async Task AName_TakenAgain_IsNotOverwritten()
    {
        var file = Path.Combine(_root, "notes.txt");
        File.WriteAllText(file, "old");
        var bin = new WindowsRecycleBin();
        var since = DateTimeOffset.Now;
        await bin.RecycleAsync([FsUri.Local(file)], CancellationToken.None);
        File.WriteAllText(file, "new");

        var failure = await Should.ThrowAsync<IOException>(
            bin.RestoreAsync([FsUri.Local(file)], since, CancellationToken.None));

        failure.Message.ShouldContain("something else has that name now");
        File.ReadAllText(file).ShouldBe("new");

        // Leave nothing of ours in the user's bin. A delete stays pending while another process
        // (a scanner, usually) has the new file open, and the name exists until it lets go.
        File.Delete(file);
        for (var wait = 0; File.Exists(file) && wait < 50; wait++)
            await Task.Delay(100);
        await bin.RestoreAsync([FsUri.Local(file)], since, CancellationToken.None);
    }

    /// <summary>The bin's record of the item, and the item beside it, or nulls.</summary>
    private static (string? Record, string? Content) BinPairOf(string path)
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var bin = Path.Combine(Path.GetPathRoot(path)!, "$Recycle.Bin", sid);
        var record = Directory.EnumerateFiles(bin, "$I*").FirstOrDefault(info =>
            WindowsRecycleBin.ReadRecord(info) is { } r && string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        return (record, record is null ? null : Path.Combine(bin, "$R" + Path.GetFileName(record)[2..]));
    }

    [Fact]
    public async Task OurRecordIsByteForByteWindowsOwn()
    {
        var file = Path.Combine(_root, "same.txt");
        File.WriteAllText(file, "twelve bytes");
        var bin = new WindowsRecycleBin();
        var since = DateTimeOffset.Now;
        await bin.RecycleAsync([FsUri.Local(file)], CancellationToken.None); // a file: the shell's way
        var (theirs, _) = BinPairOf(file);
        theirs.ShouldNotBeNull();
        var windows = File.ReadAllBytes(theirs);
        var deleted = DateTimeOffset.FromFileTime(BitConverter.ToInt64(windows, 16));

        var ours = Path.Combine(_root, "ours.bin");
        WindowsRecycleBin.WriteRecord(ours, file, BitConverter.ToInt64(windows, 8), deleted);
        File.ReadAllBytes(ours).ShouldBe(windows);

        await bin.RestoreAsync([FsUri.Local(file)], since, CancellationToken.None);
    }

    [Fact]
    public async Task ALargeFolderGoesToTheBinByRenameAndComesBack()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "large")).FullName;
        for (var i = 0; i < WindowsRecycleBin.DirectFrom; i++)
            File.WriteAllText(Path.Combine(folder, $"{i}.txt"), "12345");
        var bin = new WindowsRecycleBin();
        var since = DateTimeOffset.Now;

        await bin.RecycleAsync([FsUri.Local(folder)], CancellationToken.None);

        Directory.Exists(folder).ShouldBeFalse();
        var (record, content) = BinPairOf(folder);
        record.ShouldNotBeNull("no record of the folder in the bin");
        Directory.EnumerateFiles(content!).Count().ShouldBe(WindowsRecycleBin.DirectFrom);
        WindowsRecycleBin.ReadRecord(record)!.Value.Deleted.ShouldBeGreaterThanOrEqualTo(since.AddSeconds(-1));
        BitConverter.ToInt64(File.ReadAllBytes(record), 8).ShouldBe(5L * WindowsRecycleBin.DirectFrom, "the record's size");

        await bin.RestoreAsync([FsUri.Local(folder)], since, CancellationToken.None);
        Directory.EnumerateFiles(folder).Count().ShouldBe(WindowsRecycleBin.DirectFrom);
        File.Exists(record).ShouldBeFalse();
    }

    [Fact]
    public async Task ALargeFolderWithAJunctionInside_TakesTheLinkAndLeavesWhereItLeads()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "not part of it");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "linked")).FullName;
        for (var i = 0; i < WindowsRecycleBin.DirectFrom; i++)
            File.WriteAllText(Path.Combine(folder, $"{i}.txt"), "x");
        // A junction needs no rights a symbolic link would; one back up to the folder above too,
        // which a walk that follows links goes round forever.
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{Path.Combine(folder, "out")}\" \"{elsewhere}\" && mklink /J \"{Path.Combine(folder, "up")}\" \"{_root}\"")
            { CreateNoWindow = true, UseShellExecute = false })!)
            await mklink.WaitForExitAsync();
        var bin = new WindowsRecycleBin();
        var since = DateTimeOffset.Now;

        await bin.RecycleAsync([FsUri.Local(folder)], CancellationToken.None);

        Directory.Exists(folder).ShouldBeFalse();
        File.ReadAllText(Path.Combine(elsewhere, "keep.txt")).ShouldBe("not part of it");
        BinPairOf(folder).Record.ShouldNotBeNull();

        await bin.RestoreAsync([FsUri.Local(folder)], since, CancellationToken.None);
        File.ReadAllText(Path.Combine(folder, "out", "keep.txt")).ShouldBe("not part of it");
        Directory.Delete(Path.Combine(folder, "out"));
        Directory.Delete(Path.Combine(folder, "up"));
    }

    [Fact]
    public void ASyncFolder_AndOneHoldingIt_AreLeftToTheShell()
    {
        // Whichever this machine has; none, and there is nothing to ask about.
        if (Environment.GetEnvironmentVariable("OneDrive") is not { Length: > 0 } root || !Directory.Exists(root))
            return;

        WindowsRecycleBin.InOrHoldsASyncRoot(root).ShouldBeTrue();
        WindowsRecycleBin.InOrHoldsASyncRoot(Path.Combine(root, "Documents", "big")).ShouldBeTrue();
        WindowsRecycleBin.InOrHoldsASyncRoot(Path.GetDirectoryName(root)!).ShouldBeTrue("a folder holding a sync root");
        WindowsRecycleBin.InOrHoldsASyncRoot(root + " other").ShouldBeFalse("a name that only starts the same");
        WindowsRecycleBin.InOrHoldsASyncRoot(_root).ShouldBeFalse();
    }

    [Fact]
    public async Task ALargeFolderWithAFileHeldOpen_StaysAndNothingIsLeftInTheBin()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "held")).FullName;
        for (var i = 0; i < WindowsRecycleBin.DirectFrom; i++)
            File.WriteAllText(Path.Combine(folder, $"{i}.txt"), "x");
        var bin = new WindowsRecycleBin();

        using (new FileStream(Path.Combine(folder, "0.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Should.ThrowAsync<IOException>(bin.RecycleAsync([FsUri.Local(folder)], CancellationToken.None));

        Directory.EnumerateFiles(folder).Count().ShouldBe(WindowsRecycleBin.DirectFrom);
        BinPairOf(folder).Record.ShouldBeNull("a record was left for a folder that stayed");
    }

    [Fact]
    public async Task PutBackFromOutside_UndoSaysSoAndLeavesIt()
    {
        var file = Path.Combine(_root, "back.txt");
        File.WriteAllText(file, "mine");
        var bin = new WindowsRecycleBin();
        var since = DateTimeOffset.Now;
        await bin.RecycleAsync([FsUri.Local(file)], CancellationToken.None);

        // As Explorer's Restore does it.
        await bin.RestoreAsync([FsUri.Local(file)], since, CancellationToken.None);
        var failure = await Should.ThrowAsync<IOException>(
            bin.RestoreAsync([FsUri.Local(file)], since, CancellationToken.None));

        failure.Message.ShouldContain("something by that name is in its place now");
        File.ReadAllText(file).ShouldBe("mine");
    }

    [Fact]
    public async Task APermanentDeleteSaysItCantBeUndoneAndTheNextUndoGoesOn()
    {
        var undo = new UndoStack();
        var earlier = false;
        undo.Push(new DelegateUndo("Rename a to b", "mv a b", _ => { earlier = true; return Task.CompletedTask; }));

        var file = Path.Combine(_root, "gone.txt");
        File.WriteAllText(file, "x");
        var operations = new FileOperationService(
            new ProviderRegistry([new LocalFileSystemProvider()]), undo, new ActivityLog(), new NoRecycleBin());
        await operations.DeleteAsync([Entry(file)], permanent: true, new ScriptedDecisions(choice: "delete"), CancellationToken.None);

        var refused = await Should.ThrowAsync<CannotUndoException>(undo.UndoAsync(CancellationToken.None));
        refused.Message.ShouldBe("Deleted gone.txt permanently: that cannot be undone.");
        earlier.ShouldBeFalse();

        await undo.UndoAsync(CancellationToken.None);
        earlier.ShouldBeTrue();
    }
}
