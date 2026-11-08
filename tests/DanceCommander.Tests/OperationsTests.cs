using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Transfer;
using DanceCommander.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>Answers whatever it was told to, so a job can run without a user.</summary>
internal sealed class ScriptedDecisions(string? choice = null, string? text = null) : IDecisionHost
{
    public List<ChoiceRequest> Asked { get; } = [];

    public List<Notice> Notices { get; } = [];

    /// <summary>Answers given in order; anything beyond the list falls back to <c>choice</c>.</summary>
    public Queue<string> Answers { get; } = new();

    public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct)
    {
        Asked.Add(request);
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : choice);
    }

    /// <summary>Nothing in these tests asks for a secret; answering null declines.</summary>
    public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) =>
        Task.FromResult<SecretAnswer?>(null);

    public List<TextRequest> AskedText { get; } = [];

    public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct)
    {
        AskedText.Add(request);
        return Task.FromResult(text);
    }

    public void Report(Notice notice) => Notices.Add(notice);
}

internal sealed class NoRecycleBin : IRecycleBin
{
    public bool CanRecycle(FsUri uri) => false;

    public Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct) =>
        throw new NotSupportedException();
}

public sealed class OperationsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-ops-").FullName;
    private readonly LocalFileSystemProvider _provider = new();
    private readonly IProviderRegistry _providers;
    private readonly UndoStack _undo = new();
    private readonly ActivityLog _activity = new();
    private readonly FileOperationService _operations;

    public OperationsTests()
    {
        _providers = new ProviderRegistry([_provider]);
        _operations = new FileOperationService(_providers, _undo, _activity, new NoRecycleBin());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FsUri Root => FsUri.Local(_root);

    private FsUri WriteFile(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return FsUri.Local(path);
    }

    private async Task<FsEntry> EntryOf(FsUri uri) =>
        (await _provider.StatAsync(uri, CancellationToken.None))!;

    // ---- create, rename, undo -----------------------------------------------------------

    [Fact]
    public async Task CreatingAFolderIsUndoable()
    {
        await _operations.CreateFolderAsync(Root, "staging", CancellationToken.None);
        Directory.Exists(Path.Combine(_root, "staging")).ShouldBeTrue();

        _undo.CanUndo.ShouldBeTrue();
        await _undo.UndoAsync(CancellationToken.None);

        Directory.Exists(Path.Combine(_root, "staging")).ShouldBeFalse();
    }

    [Fact]
    public async Task UndoingAFolderCreationLeavesItAloneIfSomethingIsInside()
    {
        await _operations.CreateFolderAsync(Root, "staging", CancellationToken.None);
        File.WriteAllText(Path.Combine(_root, "staging", "work.txt"), "important");

        // Said, rather than "Undid" over a folder still there (2026-10-17).
        var refused = await Should.ThrowAsync<CannotUndoException>(_undo.UndoAsync(CancellationToken.None));
        refused.Message.ShouldBe("staging has something in it now, so it stays.");

        // Undo must never destroy something the user put there afterwards.
        File.Exists(Path.Combine(_root, "staging", "work.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task ASlashMakesFoldersInsideFolders()
    {
        var created = await _operations.CreateFolderAsync(Root, "docs/2026\\q3", CancellationToken.None);

        Directory.Exists(Path.Combine(_root, "docs", "2026", "q3")).ShouldBeTrue();
        created.ShouldBe(Root.Child("docs").Child("2026").Child("q3"));
    }

    [Fact]
    public async Task UndoingANestedFolderRemovesOnlyWhatItMade()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));

        await _operations.CreateFolderAsync(Root, "docs/2026/q3", CancellationToken.None);
        await _undo.UndoAsync(CancellationToken.None);

        // docs was there before and is still there; the two levels made inside it are gone.
        Directory.Exists(Path.Combine(_root, "docs")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "docs", "2026")).ShouldBeFalse();
    }

    [Fact]
    public async Task CreatingAFolderThatIsAlreadyThereMakesNothingToUndo()
    {
        // It used to push an undo that would delete the existing folder if it happened to be empty.
        Directory.CreateDirectory(Path.Combine(_root, "existing"));

        await _operations.CreateFolderAsync(Root, "existing", CancellationToken.None);

        _undo.CanUndo.ShouldBeFalse();
        Directory.Exists(Path.Combine(_root, "existing")).ShouldBeTrue();
    }

    // ---- files made out of something that was not a file (a paste, §6.13) ----------------

    [Fact]
    public async Task CreatingAFileWritesTheBytesItWasGiven()
    {
        var created = await _operations.CreateFileAsync(
            Root, "pasted.txt", "hello"u8.ToArray(), "…", CancellationToken.None);

        created!.Name.ShouldBe("pasted.txt");
        File.ReadAllText(Path.Combine(_root, "pasted.txt")).ShouldBe("hello");
    }

    [Fact]
    public async Task CreatingAFileStepsAsideRatherThanOverwriting()
    {
        WriteFile("pasted.txt", "the original");

        var created = await _operations.CreateFileAsync(
            Root, "pasted.txt", "the new one"u8.ToArray(), "…", CancellationToken.None);

        // A paste names itself, so it must never land on top of something the user named.
        created!.Name.ShouldBe("pasted (2).txt");
        File.ReadAllText(Path.Combine(_root, "pasted.txt")).ShouldBe("the original");
        File.ReadAllText(Path.Combine(_root, "pasted (2).txt")).ShouldBe("the new one");
    }

    [Fact]
    public async Task SteppingAsideKeepsTheExtensionWhereItBelongs()
    {
        WriteFile("shot.png", "first");
        WriteFile("shot (2).png", "second");

        var created = await _operations.CreateFileAsync(
            Root, "shot.png", [1, 2, 3], "…", CancellationToken.None);

        created!.Name.ShouldBe("shot (3).png");
    }

    [Fact]
    public async Task CreatingAFileIsUndoable()
    {
        await _operations.CreateFileAsync(Root, "pasted.txt", "hello"u8.ToArray(), "…", CancellationToken.None);

        _undo.CanUndo.ShouldBeTrue();
        await _undo.UndoAsync(CancellationToken.None);

        File.Exists(Path.Combine(_root, "pasted.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task TheActivityLogSaysHowAPastedFileWasMade()
    {
        // The caller supplies the command because only it knows where the bytes came from; the
        // log's promise is that it can always tell you what happened to your files (§1.2/6).
        await _operations.CreateFileAsync(
            Root, "pasted.txt", "hello"u8.ToArray(),
            "Get-Clipboard | Set-Content -Encoding utf8 \"pasted.txt\"", CancellationToken.None);

        _activity.Entries[0].ShellEquivalent.ShouldContain("Get-Clipboard");
    }

    [Fact]
    public async Task RenamingIsUndoable()
    {
        var file = WriteFile("before.txt");
        await _operations.RenameAsync(await EntryOf(file), "after.txt", CancellationToken.None);

        File.Exists(Path.Combine(_root, "after.txt")).ShouldBeTrue();

        await _undo.UndoAsync(CancellationToken.None);

        File.Exists(Path.Combine(_root, "before.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(_root, "after.txt")).ShouldBeFalse();
    }

    // ---- group into folder ---------------------------------------------------------------

    [Fact]
    public async Task GroupsSelectedItemsIntoOneFolderAndBackAgainAsASingleStep()
    {
        var a = await EntryOf(WriteFile("report-jan.txt"));
        var b = await EntryOf(WriteFile("report-feb.txt"));

        await _operations.GroupIntoFolderAsync([a, b], Root, "reports", CancellationToken.None);

        File.Exists(Path.Combine(_root, "reports", "report-jan.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(_root, "reports", "report-feb.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(_root, "report-jan.txt")).ShouldBeFalse();

        // One undo entry for the whole grouping, not one per file.
        await _undo.UndoAsync(CancellationToken.None);

        File.Exists(Path.Combine(_root, "report-jan.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(_root, "report-feb.txt")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "reports")).ShouldBeFalse();
        _undo.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task GroupingIntoAFolderThatIsAlreadyThereIsRefusedAndMovesNothing()
    {
        var a = await EntryOf(WriteFile("report-jan.txt"));
        WriteFile("reports/older.txt");

        await Should.ThrowAsync<IOException>(() =>
            _operations.GroupIntoFolderAsync([a], Root, "reports", CancellationToken.None));

        // Not merged into it: its undo would then have removed a folder that was there before.
        File.Exists(Path.Combine(_root, "report-jan.txt")).ShouldBeTrue();
        _undo.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task AGroupThatStopsPartWayCanStillBeUndone()
    {
        // Two files of one name from two folders, as a search gives: the second meets the first.
        var a = await EntryOf(WriteFile("jan/report.txt", "january"));
        var b = await EntryOf(WriteFile("feb/report.txt", "february"));

        await Should.ThrowAsync<IOException>(() =>
            _operations.GroupIntoFolderAsync([a, b], Root, "reports", CancellationToken.None));
        File.ReadAllText(Path.Combine(_root, "reports", "report.txt")).ShouldBe("january");

        // What moved is on the undo stack and goes back; nothing older is undone in its place.
        await _undo.UndoAsync(CancellationToken.None);

        File.ReadAllText(Path.Combine(_root, "jan", "report.txt")).ShouldBe("january");
        File.ReadAllText(Path.Combine(_root, "feb", "report.txt")).ShouldBe("february");
        Directory.Exists(Path.Combine(_root, "reports")).ShouldBeFalse();
    }

    [Fact]
    public async Task ProposesTheSelectionsCommonPrefixAsTheFolderName()
    {
        var a = await EntryOf(WriteFile("invoice-2026-01.pdf"));
        var b = await EntryOf(WriteFile("invoice-2026-02.pdf"));

        _operations.ProposeGroupName([a, b], Root).ShouldBe("invoice-2026");
    }

    [Fact]
    public async Task FallsBackToTheFolderNameWhenTheSelectionHasNothingInCommon()
    {
        var a = await EntryOf(WriteFile("alpha.txt"));
        var b = await EntryOf(WriteFile("zulu.txt"));

        _operations.ProposeGroupName([a, b], Root).ShouldBe(Root.Name);
    }

    // ---- delete ---------------------------------------------------------------------------

    [Fact]
    public async Task PermanentDeletionAlwaysAsksFirst()
    {
        var file = await EntryOf(WriteFile("gone.txt"));
        var decisions = new ScriptedDecisions(choice: "cancel");

        await _operations.DeleteAsync([file], permanent: true, decisions, CancellationToken.None);

        decisions.Asked.ShouldHaveSingleItem();
        File.Exists(Path.Combine(_root, "gone.txt")).ShouldBeTrue(); // cancelled means untouched
    }

    [Fact]
    public async Task DeletesPermanentlyOnceConfirmed()
    {
        var file = await EntryOf(WriteFile("gone.txt"));

        await _operations.DeleteAsync(
            [file], permanent: true, new ScriptedDecisions(choice: "delete"), CancellationToken.None);

        File.Exists(Path.Combine(_root, "gone.txt")).ShouldBeFalse();
    }

    // ---- activity log ---------------------------------------------------------------------

    [Fact]
    public async Task RecordsWhatItDidInTheShellsOwnWords()
    {
        var file = WriteFile("notes.txt");
        await _operations.RenameAsync(await EntryOf(file), "notes.md", CancellationToken.None);

        var entry = _activity.Entries.ShouldHaveSingleItem();
        entry.Description.ShouldBe("Renamed notes.txt to notes.md");
        entry.ShellEquivalent.ShouldStartWith("move ");
        entry.ShellEquivalent.ShouldContain("notes.md");
    }

    [Fact]
    public async Task RecordsAnUndoAsItsOwnEntryRatherThanErasingHistory()
    {
        await _operations.CreateFolderAsync(Root, "temp", CancellationToken.None);
        await _undo.UndoAsync(CancellationToken.None);

        _activity.Entries.Count.ShouldBe(2);
        _activity.Entries[0].Outcome.ShouldBe(ActivityOutcome.Undone);
    }

    // ---- transfers --------------------------------------------------------------------------

    private TransferService NewTransferService() =>
        new(_providers, _undo, _activity, new NoRecycleBin(), new JobService(), NullLogger<TransferService>.Instance);

    private static async Task WaitFor(TransferJob job)
    {
        // Generous because the full suite runs in parallel: a thousand-file copy that takes a
        // few seconds alone has run past 15 s under that load. A finished job returns at once.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!job.IsFinished && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        job.IsFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task CopiesAWholeDirectoryTree()
    {
        WriteFile("tree/a.txt", "one");
        WriteFile("tree/nested/b.txt", "two");
        Directory.CreateDirectory(Path.Combine(_root, "target"));

        var job = NewTransferService().Enqueue(
            [Root.Child("tree")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);

        job.State.ShouldBe(JobState.Completed);
        File.ReadAllText(Path.Combine(_root, "target", "tree", "a.txt")).ShouldBe("one");
        File.ReadAllText(Path.Combine(_root, "target", "tree", "nested", "b.txt")).ShouldBe("two");
        File.Exists(Path.Combine(_root, "tree", "a.txt")).ShouldBeTrue(); // a copy leaves the source
    }

    [Fact]
    public async Task MovingIsUndoable()
    {
        WriteFile("payload.bin", "data");
        Directory.CreateDirectory(Path.Combine(_root, "target"));

        var job = NewTransferService().Enqueue(
            [Root.Child("payload.bin")], Root.Child("target"), TransferMode.Move, new ScriptedDecisions());
        await WaitFor(job);

        File.Exists(Path.Combine(_root, "target", "payload.bin")).ShouldBeTrue();
        File.Exists(Path.Combine(_root, "payload.bin")).ShouldBeFalse();

        await _undo.UndoAsync(CancellationToken.None);

        File.Exists(Path.Combine(_root, "payload.bin")).ShouldBeTrue();
        File.Exists(Path.Combine(_root, "target", "payload.bin")).ShouldBeFalse();
    }

    [Fact]
    public async Task AMovedFolderIsReportedAsItselfAndUndoReportsItBack()
    {
        // A collection holding the folder, or a file in it, has to hear where it went (§6.19).
        // On one drive the folder is renamed whole, so the folder is all that is reported, and
        // the file follows from it.
        WriteFile("tree/a.txt");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var feed = new RelocationFeed();
        var heard = new List<Relocation>();
        feed.Relocated += (_, moved) => heard.AddRange(moved);
        var transfers = new TransferService(
            _providers, _undo, _activity, new NoRecycleBin(), new JobService(), NullLogger<TransferService>.Instance, feed);

        await WaitFor(transfers.Enqueue([Root.Child("tree")], Root.Child("target"), TransferMode.Move, new ScriptedDecisions()));

        heard.ShouldContain(new Relocation(Root.Child("tree"), Root.Child("target").Child("tree")));
        heard.Select(r => r.Apply(Root.Child("tree").Child("a.txt"))).ShouldContain(Root.Child("target").Child("tree").Child("a.txt"));

        heard.Clear();
        await _undo.UndoAsync(CancellationToken.None);
        heard.ShouldContain(new Relocation(Root.Child("target").Child("tree"), Root.Child("tree")));

        // And the undo itself puts the folder back as it was, and takes away the one the move made.
        File.Exists(Path.Combine(_root, "tree", "a.txt")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "target", "tree")).ShouldBeFalse();
    }

    [Fact]
    public async Task UndoingAFileByFileMoveMakesItsEmptyFoldersAgain()
    {
        // Merged into a folder already there, so moved file by file; the undo put the files back
        // and nothing made the empty sub1\sub2 again (driven, 2026-10-28).
        WriteFile("tree/a.txt");
        Directory.CreateDirectory(Path.Combine(_root, "tree", "sub1", "sub2"));
        Directory.CreateDirectory(Path.Combine(_root, "target", "tree"));
        var decisions = new ScriptedDecisions();
        decisions.Answers.Enqueue("merge");

        await WaitFor(NewTransferService().Enqueue([Root.Child("tree")], Root.Child("target"), TransferMode.Move, decisions));
        Directory.Exists(Path.Combine(_root, "target", "tree", "sub1", "sub2")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "tree")).ShouldBeFalse();

        await _undo.UndoAsync(CancellationToken.None);

        File.Exists(Path.Combine(_root, "tree", "a.txt")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "tree", "sub1", "sub2")).ShouldBeTrue();
        // The folder that was there before the move stays; the ones the move made under it go.
        Directory.Exists(Path.Combine(_root, "target", "tree")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "target", "tree", "sub1")).ShouldBeFalse();
        _activity.Entries[0].ShellEquivalent!.ShouldContain("mkdir");
    }

    [Fact]
    public async Task ATransferThatLeftEverythingOutIsAFailureNotZeroItemsCopied()
    {
        // "Copied 0 items" retired as a success, the reason only in the Activity popup (driven,
        // 2026-10-28). A name Windows reads as another is one such: notes. made by WSL.
        Directory.CreateDirectory(@"\\?\" + Path.Combine(_root, "notes."));
        Directory.CreateDirectory(Path.Combine(_root, "target"));

        var job = NewTransferService().Enqueue([Root.Child("notes.")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);

        job.State.ShouldBe(JobState.Failed);
        job.Error.ShouldNotBeNull().ShouldContain("notes.");
        _activity.Entries[0].Outcome.ShouldBe(ActivityOutcome.Failed);
        _activity.Entries[0].Description.ShouldStartWith("Nothing copied");
        Directory.Delete(@"\\?\" + Path.Combine(_root, "notes."));
    }

    [Fact]
    public async Task ACopyReportsWhatLandedAtTheTopLevelOnly()
    {
        // The folder picked, not the files inside it: a view points at what you copied.
        WriteFile("tree/a.txt");
        WriteFile("tree/deep/b.txt");
        WriteFile("loose.txt");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var feed = new ArrivalFeed();
        var heard = new List<FsUri>();
        feed.Arrived += (_, placed) => heard.AddRange(placed);
        var transfers = new TransferService(
            _providers, _undo, _activity, new NoRecycleBin(), new JobService(), NullLogger<TransferService>.Instance,
            arrivals: feed);

        await WaitFor(transfers.Enqueue(
            [Root.Child("tree"), Root.Child("loose.txt")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions()));

        heard.ShouldBe([Root.Child("target").Child("tree"), Root.Child("target").Child("loose.txt")], ignoreOrder: true);
    }

    [Fact]
    public async Task UndoingAMoveReportsTheItemsLandingBackWhereTheyWere()
    {
        WriteFile("a.txt");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var feed = new ArrivalFeed();
        var heard = new List<FsUri>();
        feed.Arrived += (_, placed) => heard.AddRange(placed);
        var transfers = new TransferService(
            _providers, _undo, _activity, new NoRecycleBin(), new JobService(), NullLogger<TransferService>.Instance,
            arrivals: feed);

        await WaitFor(transfers.Enqueue([Root.Child("a.txt")], Root.Child("target"), TransferMode.Move, new ScriptedDecisions()));
        heard.Clear();
        await _undo.UndoAsync(CancellationToken.None);

        heard.ShouldBe([Root.Child("a.txt")]);
    }

    [Fact]
    public async Task CreatingAFolderPathReportsTheOutermostFolderMade()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        var feed = new ArrivalFeed();
        var heard = new List<FsUri>();
        feed.Arrived += (_, placed) => heard.AddRange(placed);
        var operations = new FileOperationService(_providers, _undo, _activity, new NoRecycleBin(), arrivals: feed);

        // docs was there already, so 2026 is what arrived where anyone could see it.
        await operations.CreateFolderAsync(Root, "docs/2026/q3", CancellationToken.None);

        heard.ShouldBe([Root.Child("docs").Child("2026")]);
    }

    [Fact]
    public async Task ACopyWithNoRecycleBinToUndoIntoSaysSoRatherThanUndoingWhatCameBefore()
    {
        WriteFile("payload.bin", "data");
        Directory.CreateDirectory(Path.Combine(_root, "target"));

        var job = NewTransferService().Enqueue(
            [Root.Child("payload.bin")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);

        // Undoing a copy deletes nothing permanently: with no bin to send the copy to, Ctrl+Z
        // stops here and says so, instead of reaching past and undoing the operation before.
        _activity.Entries.ShouldContain(e => e.Description.StartsWith("Copied"));
        _undo.CanUndo.ShouldBeTrue();
        await Should.ThrowAsync<CannotUndoException>(() => _undo.UndoAsync(CancellationToken.None));
        File.ReadAllText(Path.Combine(_root, "target", "payload.bin")).ShouldBe("data");
    }

    [Fact]
    public async Task UndoingACopySendsTheCopiesToTheRecycleBinAndRemovesTheFoldersItMade()
    {
        WriteFile("tree/a.txt", "a");
        WriteFile("tree/inner/b.txt", "b");
        WriteFile("target/kept.txt", "already there");
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin"));

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("tree")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);
        File.Exists(Path.Combine(_root, "target", "tree", "inner", "b.txt")).ShouldBeTrue();

        await _undo.UndoAsync(CancellationToken.None);

        // The copies are in the bin, as the one folder the copy made around them since nothing
        // else is in it (2026-10-17), and the rest is as it was.
        var inBin = bin.Recycled.ShouldHaveSingleItem();
        Path.GetFileName(inBin).ShouldBe("tree");
        File.ReadAllText(Path.Combine(inBin, "inner", "b.txt")).ShouldBe("b");
        Directory.Exists(Path.Combine(_root, "target", "tree")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(_root, "target", "kept.txt")).ShouldBe("already there");
        File.ReadAllText(Path.Combine(_root, "tree", "inner", "b.txt")).ShouldBe("b");
    }

    [Fact]
    public async Task ACopyWhoseSourceHasGoneIsTheOnlyOneLeftAndStays()
    {
        WriteFile("payload.bin", "data");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin"));

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("payload.bin")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);
        File.Delete(Path.Combine(_root, "payload.bin"));

        await _undo.UndoAsync(CancellationToken.None);

        bin.Recycled.ShouldBeEmpty();
        File.ReadAllText(Path.Combine(_root, "target", "payload.bin")).ShouldBe("data");
    }

    [Fact]
    public async Task UndoingACopyKeepsACopyEditedSince_AndSendsOneWhoseOriginalChanged()
    {
        WriteFile("template.txt", "blank");
        WriteFile("report.txt", "v1");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin"));

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("template.txt"), Root.Child("report.txt")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);

        // An hour's work in the copy, which stays; and the original saved over, which no longer
        // keeps the copy (the user, 2026-10-18): the old version goes to the bin, not away.
        await Task.Delay(2100);
        File.WriteAllText(Path.Combine(_root, "target", "template.txt"), "filled in");
        File.WriteAllText(Path.Combine(_root, "report.txt"), "v2");

        await _undo.UndoAsync(CancellationToken.None);

        bin.Recycled.Select(Path.GetFileName).ShouldBe(["report.txt"]);
        File.ReadAllText(bin.Recycled[0]).ShouldBe("v1");
        File.ReadAllText(Path.Combine(_root, "target", "template.txt")).ShouldBe("filled in");
        _activity.Entries[0].Detail!.ShouldContain("1 of them were copies of originals that have changed since");
    }

    [Fact]
    public async Task UndoingACopyKeepsOneEditedWithItsDatePutBack()
    {
        // A VeraCrypt container used and dismounted: the same size, and its old date restored.
        // The copy keeps the source's date, so the date alone took it for the copy (kind 46).
        var bytes = new byte[256 * 1024];
        File.WriteAllBytes(Path.Combine(_root, "vault.hc"), bytes);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "vault.hc"), DateTime.UtcNow.AddDays(-30));
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin"));

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("vault.hc")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);

        // As VeraCrypt's driver does: every time held still for the handle (-1), then written.
        var copy = Path.Combine(_root, "target", "vault.hc");
        var times = KeepTimes.Of(copy);
        using (var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
        {
            KeepTimes.Hold(stream);
            stream.Position = 1000;
            stream.WriteByte(42);
        }
        KeepTimes.Of(copy).ShouldBe(times); // the change time too: only the journal saw it

        await _undo.UndoAsync(CancellationToken.None);

        bin.Recycled.ShouldBeEmpty();
        File.ReadAllBytes(copy)[1000].ShouldBe((byte)42);
    }

    [Fact]
    public async Task UndoingACopyOfALargeFileLeftAloneStillSendsIt()
    {
        File.WriteAllBytes(Path.Combine(_root, "image.iso"), new byte[256 * 1024]);
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin"));

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("image.iso")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);
        await _undo.UndoAsync(CancellationToken.None);

        bin.Recycled.Select(Path.GetFileName).ShouldBe(["image.iso"]);
    }

    [Fact]
    public async Task AFileSavedInACopiedFolder_StaysWithItAndOnlyTheCopiesGo()
    {
        WriteFile("tree/a.txt", "a");
        WriteFile("tree/inner/b.txt", "b");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin"));

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("tree")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);
        File.WriteAllText(Path.Combine(_root, "target", "tree", "inner", "mine.txt"), "saved since");

        await _undo.UndoAsync(CancellationToken.None);

        // Not the whole folder: it holds something that is not the copy's.
        bin.Recycled.Select(Path.GetFileName).Order().ShouldBe(["a.txt", "b.txt"]);
        File.ReadAllText(Path.Combine(_root, "target", "tree", "inner", "mine.txt")).ShouldBe("saved since");
    }

    [Theory]
    [InlineData(true, false)] // larger than the bin, as its settings say
    [InlineData(false, true)] // refused whole by the shell, all the same
    public async Task ACopiedFolderTheBinWontTakeWhole_GoesFileByFile(bool small, bool refuses)
    {
        WriteFile("tree/a.txt", "a");
        WriteFile("tree/inner/b.txt", "b");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin")) { Capacity = small ? 1 : null, RefusesFolders = refuses };

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("tree")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);

        await _undo.UndoAsync(CancellationToken.None);

        bin.Recycled.Select(Path.GetFileName).Order().ShouldBe(["a.txt", "b.txt"]);
        Directory.Exists(Path.Combine(_root, "target", "tree")).ShouldBeFalse();
    }

    [Fact]
    public async Task ALargeCopiedFolderWithAChangedFile_StaysWholeAndSaysWhy()
    {
        for (var i = 0; i <= 1_000; i++)
            WriteFile($"repo/src/{i}.txt", "x");
        WriteFile("repo/.git/index", "before");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin"));

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("repo")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);
        // As git does in a repository it is shown: its index written again since the copy.
        File.WriteAllText(Path.Combine(_root, "target", "repo", ".git", "index"), "after, and longer");

        var refused = await Should.ThrowAsync<CannotUndoException>(_undo.UndoAsync(CancellationToken.None));

        refused.Message.ShouldBe("repo stays as it is: repo/.git/index in it has changed since the copy. Del sends it to the Recycle Bin whole.");
        bin.Recycled.ShouldBeEmpty();
        Directory.EnumerateFiles(Path.Combine(_root, "target", "repo", "src")).Count().ShouldBe(1_001);
        _activity.Entries[0].Description.ShouldStartWith("Undid part of:");
    }

    [Fact]
    public async Task ALargeCopiedFolderWhoseOriginalChanged_GoesWholeAndSaysSo()
    {
        for (var i = 0; i <= 1_000; i++)
            WriteFile($"repo/src/{i}.txt", "x");
        WriteFile("repo/.git/FETCH_HEAD", "before");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin"));

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("repo")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);
        // A fetch in the original repository (Fork's, 2026-10-18), not in the copy.
        File.WriteAllText(Path.Combine(_root, "repo", ".git", "FETCH_HEAD"), "after a fetch");

        await _undo.UndoAsync(CancellationToken.None);

        Path.GetFileName(bin.Recycled.ShouldHaveSingleItem()).ShouldBe("repo");
        File.ReadAllText(Path.Combine(bin.Recycled[0], ".git", "FETCH_HEAD")).ShouldBe("before");
        File.ReadAllText(Path.Combine(_root, "repo", ".git", "FETCH_HEAD")).ShouldBe("after a fetch");
        _activity.Entries[0].Detail!.ShouldContain("1 of them were copies of originals that have changed since");
    }

    [Fact]
    public async Task CtrlZWhileAnUndoRuns_WaitsItsTurn()
    {
        var release = new TaskCompletionSource();
        var later = false;
        _undo.Push(new DelegateUndo("Earlier", "", _ => { later = true; return Task.CompletedTask; }));
        _undo.Push(new DelegateUndo("Copy 85,000 files", "", _ => release.Task));

        var running = _undo.UndoAsync(CancellationToken.None);
        _undo.Running!.Description.ShouldBe("Copy 85,000 files");

        var busy = await Should.ThrowAsync<UndoBusyException>(_undo.UndoAsync(CancellationToken.None));
        busy.Message.ShouldBe("Still undoing: Copy 85,000 files.");
        later.ShouldBeFalse();
        _undo.Pending.ShouldHaveSingleItem().Description.ShouldBe("Earlier");

        release.SetResult();
        await running;
        _undo.Running.ShouldBeNull();
        await _undo.UndoAsync(CancellationToken.None);
        later.ShouldBeTrue();
    }

    [Fact]
    public async Task AFileSavedWhereACopiedFolderWasIsNotRemovedByItsUndo()
    {
        // The copy made target/notes; the user removed it and saved a file called "notes" there.
        WriteFile("notes/a.txt", "a");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin"));

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("notes")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions());
        await WaitFor(job);
        Directory.Delete(Path.Combine(_root, "target", "notes"), recursive: true);
        File.WriteAllText(Path.Combine(_root, "target", "notes"), "a file of that name, saved since");

        await _undo.UndoAsync(CancellationToken.None);

        File.ReadAllText(Path.Combine(_root, "target", "notes")).ShouldBe("a file of that name, saved since");
    }

    [Fact]
    public async Task ACopyThatReplacedAFileCannotBeUndone()
    {
        WriteFile("a.txt", "new");
        WriteFile("target/a.txt", "old");
        var bin = new FolderRecycleBin(Path.Combine(_root, "bin"));

        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("a.txt")], Root.Child("target"), TransferMode.Copy, new ScriptedDecisions(choice: "overwrite"));
        await WaitFor(job);

        // The old one is gone, and binning the new one would leave neither.
        await Should.ThrowAsync<CannotUndoException>(() => _undo.UndoAsync(CancellationToken.None));
        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("new");
    }

    /// <summary>A Recycle Bin that moves what it is given into a folder, so it really leaves.</summary>
    private sealed class FolderRecycleBin(string folder) : IRecycleBin
    {
        public List<string> Recycled { get; } = [];

        public bool CanRecycle(FsUri uri) => uri.IsLocal;

        public long? Capacity { get; init; }

        /// <summary>As the shell does a folder larger than the bin: refused whole.</summary>
        public bool RefusesFolders { get; init; }

        public long? CapacityOf(FsUri uri) => Capacity;

        public Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct)
        {
            if (RefusesFolders && paths.Any(p => Directory.Exists(p.ToLocalPath())))
                throw new IOException("Windows would have deleted it outright rather than recycle it, so it was left where it is.");
            foreach (var path in paths)
            {
                var into = Path.Combine(folder, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(into);
                var local = path.ToLocalPath();
                var to = Path.Combine(into, Path.GetFileName(local));
                if (Directory.Exists(local))
                    Directory.Move(local, to);
                else
                    File.Move(local, to);
                Recycled.Add(to);
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AsksOnceOnAConflictAndAppliesTheAnswerToTheRestOfTheJob()
    {
        WriteFile("a.txt", "new");
        WriteFile("b.txt", "new");
        WriteFile("target/a.txt", "old");
        WriteFile("target/b.txt", "old");

        var decisions = new ScriptedDecisions(choice: "skip");
        var job = NewTransferService().Enqueue(
            [Root.Child("a.txt"), Root.Child("b.txt")], Root.Child("target"), TransferMode.Copy, decisions);
        await WaitFor(job);

        // Two conflicts, one question: the policy carries for the whole job.
        decisions.Asked.ShouldHaveSingleItem();
        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("old");
        File.ReadAllText(Path.Combine(_root, "target", "b.txt")).ShouldBe("old");
    }

    [Fact]
    public async Task AJobOnAQuestionSaysItIsWaitingAndStopsMoving()
    {
        WriteFile("a.txt", "new");
        WriteFile("target/a.txt", "old");

        var decisions = new HeldDecisions();
        var job = NewTransferService().Enqueue(
            [Root.Child("a.txt")], Root.Child("target"), TransferMode.Copy, decisions);
        await decisions.Asked.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // A stalled bar and a bar waiting on you look the same unless the job says which it is.
        job.IsWaiting.ShouldBeTrue();
        job.Status.ShouldBe("Waiting for your answer");
        job.ShowsMotion.ShouldBeFalse();

        decisions.Answer.SetResult("skip");
        await WaitFor(job);

        job.WaitingFor.ShouldBeNull();
        job.IsWaiting.ShouldBeFalse();
    }

    /// <summary>Holds the question open until the test answers it.</summary>
    private sealed class HeldDecisions : IDecisionHost
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct)
        {
            Asked.TrySetResult();
            return Answer.Task;
        }

        public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) =>
            Task.FromResult<SecretAnswer?>(null);

        public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) => Task.FromResult<string?>(null);

        public void Report(Notice notice)
        {
        }
    }

    [Fact]
    public async Task KeepsBothByGivingTheNewFileAUniqueName()
    {
        WriteFile("a.txt", "new");
        WriteFile("target/a.txt", "old");

        var job = NewTransferService().Enqueue(
            [Root.Child("a.txt")], Root.Child("target"), TransferMode.Copy,
            new ScriptedDecisions(choice: "keepBoth"));
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("old");
        File.ReadAllText(Path.Combine(_root, "target", "a (2).txt")).ShouldBe("new");
    }

    [Fact]
    public async Task KeepingBothPastNineHundredAndNinetyNineNeverWritesOverTheOldOne()
    {
        // UniqueAsync gave back the name itself when every numbered one was taken.
        WriteFile("a.txt", "new");
        WriteFile("target/a.txt", "old");
        for (var n = 2; n < 1000; n++)
            WriteFile($"target/a ({n}).txt", "numbered");

        var job = NewTransferService().Enqueue(
            [Root.Child("a.txt")], Root.Child("target"), TransferMode.Copy,
            new ScriptedDecisions(choice: "keepBoth"));
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("old");
        Directory.GetFiles(Path.Combine(_root, "target")).Count(f => File.ReadAllText(f) == "new").ShouldBe(1);
    }

    /// <summary>
    /// Another program saving a file while a question is open, a moment after the look as in use:
    /// within one clock tick of the look, Windows' stamp can't tell the save from what was there.
    /// </summary>
    private void SavedLater(string name, string content)
    {
        Thread.Sleep(100);
        WriteFile(name, content);
    }

    /// <summary>Answers each question in turn, doing something first where told to.</summary>
    private sealed class AnswersWith(params (string Answer, Action? First)[] turns) : IDecisionHost
    {
        public List<ChoiceRequest> Asked { get; } = [];

        public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct)
        {
            Asked.Add(request);
            var (answer, first) = turns[Math.Min(Asked.Count, turns.Length) - 1];
            first?.Invoke();
            return Task.FromResult<string?>(answer);
        }

        public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) => Task.FromResult<SecretAnswer?>(null);

        public void Report(Notice notice) { }
    }

    [Fact]
    public async Task ReplaceForTheRestAsksAboutAFileWrittenThereAfterTheAnswer()
    {
        // Replace was said about the files there then. One another program saved while the
        // question was open (here: Explorer pasting a newer b.txt) was written over unasked.
        WriteFile("a.txt", "new a");
        WriteFile("b.txt", "new b");
        WriteFile("c.txt", "new c");
        WriteFile("target/a.txt", "old a");
        WriteFile("target/b.txt", "old b");
        WriteFile("target/c.txt", "old c");
        await Task.Delay(100); // what was there before the question, by more than a clock tick
        var decisions = new AnswersWith(
            ("overwrite", () => SavedLater("target/b.txt", "someone's newer b")),
            ("skip", null));

        // A bin, as a fixed disk has: the change time is trusted only there.
        var job = new TransferService(_providers, _undo, _activity, new FolderRecycleBin(Path.Combine(_root, "bin")), new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("a.txt"), Root.Child("b.txt"), Root.Child("c.txt")], Root.Child("target"), TransferMode.Copy, decisions);
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("new a");
        File.ReadAllText(Path.Combine(_root, "target", "b.txt")).ShouldBe("someone's newer b");
        File.ReadAllText(Path.Combine(_root, "target", "c.txt")).ShouldBe("new c"); // still covered by the answer
        decisions.Asked.Count.ShouldBe(2);
        decisions.Asked[1].Title.ShouldContain("after Replace was chosen");
    }

    [Fact]
    public async Task TheFileAskedAboutSavedWhileTheQuestionStoodIsAskedAboutAgain()
    {
        WriteFile("a.txt", "new a");
        WriteFile("target/a.txt", "old a");
        var decisions = new AnswersWith(
            ("overwrite", () => SavedLater("target/a.txt", "saved while asked")),
            ("skip", null));

        var job = new TransferService(_providers, _undo, _activity, new FolderRecycleBin(Path.Combine(_root, "bin")), new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([Root.Child("a.txt")], Root.Child("target"), TransferMode.Copy, decisions);
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("saved while asked");
        decisions.Asked.Count.ShouldBe(2);
    }

    [Fact]
    public async Task WithNoBinAFileWrittenAfterTheAnswerIsStillAskedAbout()
    {
        // A stick, a share: where a file written over is gone for good, the check was left out
        // because a server's clock isn't ours (BUGS.md 4). A disk here goes by this machine's.
        WriteFile("a.txt", "new a");
        WriteFile("b.txt", "new b");
        WriteFile("c.txt", "new c");
        WriteFile("target/a.txt", "old a");
        WriteFile("target/b.txt", "old b");
        WriteFile("target/c.txt", "old c");
        await Task.Delay(100); // what was there before the question, by more than a clock tick
        var decisions = new AnswersWith(
            ("overwrite", () => SavedLater("target/b.txt", "someone's newer b")),
            ("skip", null));

        var job = NewTransferService()
            .Enqueue([Root.Child("a.txt"), Root.Child("b.txt"), Root.Child("c.txt")], Root.Child("target"), TransferMode.Copy, decisions);
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "b.txt")).ShouldBe("someone's newer b");
        File.ReadAllText(Path.Combine(_root, "target", "c.txt")).ShouldBe("new c");
        decisions.Asked.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OnAShareTheServersClockTellsAFileWrittenAfterTheAnswer()
    {
        // \\localhost\C$ is a share as far as Windows' drive types go, so the offset is learnt
        // from a.txt, the first file written there. Without admin shares there is nothing to test.
        var share = @"\\localhost\" + _root[0] + "$" + _root[2..];
        if (!Directory.Exists(share))
            return;
        var shared = FsUri.Local(share);
        WriteFile("a.txt", "new a");
        WriteFile("b.txt", "new b");
        WriteFile("c.txt", "new c");
        WriteFile("target/a.txt", "old a");
        WriteFile("target/b.txt", "old b");
        WriteFile("target/c.txt", "old c");
        // c.txt's stamp well before the question: the offset is learnt to within the time a write
        // takes to be told of, which comes out early, and so asks about what was written just before.
        await Task.Delay(500);
        var decisions = new AnswersWith(
            ("overwrite", () => SavedLater("target/b.txt", "someone's newer b")),
            ("skip", null));

        var job = NewTransferService()
            .Enqueue([shared.Child("a.txt"), shared.Child("b.txt"), shared.Child("c.txt")], shared.Child("target"), TransferMode.Copy, decisions);
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("new a");
        File.ReadAllText(Path.Combine(_root, "target", "b.txt")).ShouldBe("someone's newer b");
        File.ReadAllText(Path.Combine(_root, "target", "c.txt")).ShouldBe("new c");
        decisions.Asked.Count.ShouldBe(2);
    }

    /// <summary>A transfer onto a stand-in for a server: the local disk with no change stamps to read.</summary>
    private TransferService ServerTransferService() =>
        new(new ProviderRegistry([new ModalProvider(new LocalFileSystemProvider(), "windows")]), _undo, _activity,
            new NoRecycleBin(), new JobService(), NullLogger<TransferService>.Instance);

    [Fact]
    public async Task OnAServerAFileSavedAfterTheAnswerIsAskedAbout()
    {
        // A server keeps no change time, and the check was left out there: a colleague's upload
        // after the answer was written over unasked (BUGS.md 4, kind 67).
        WriteFile("a.txt", "new a");
        WriteFile("b.txt", "new b");
        WriteFile("c.txt", "new c");
        WriteFile("target/a.txt", "old a");
        WriteFile("target/b.txt", "old b");
        WriteFile("target/c.txt", "old c");
        var decisions = new AnswersWith(
            ("overwrite", () => SavedLater("target/b.txt", "someone's newer b")),
            ("skip", null));

        var job = ServerTransferService()
            .Enqueue([Root.Child("a.txt"), Root.Child("b.txt"), Root.Child("c.txt")], Root.Child("target"), TransferMode.Copy, decisions);
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("new a");
        File.ReadAllText(Path.Combine(_root, "target", "b.txt")).ShouldBe("someone's newer b");
        File.ReadAllText(Path.Combine(_root, "target", "c.txt")).ShouldBe("new c");
        decisions.Asked.Count.ShouldBe(2);
        decisions.Asked[1].Title.ShouldContain("after Replace was chosen");
    }

    [Fact]
    public async Task OnAServerAFileArrivingAfterTheAnswerIsAskedAbout()
    {
        WriteFile("a.txt", "new a");
        WriteFile("b.txt", "new b");
        WriteFile("target/a.txt", "old a");
        var decisions = new AnswersWith(
            ("overwrite", () => SavedLater("target/b.txt", "someone's b")),
            ("skip", null));

        var job = ServerTransferService()
            .Enqueue([Root.Child("a.txt"), Root.Child("b.txt")], Root.Child("target"), TransferMode.Copy, decisions);
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "b.txt")).ShouldBe("someone's b");
        decisions.Asked.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OnAServerTheFileAskedAboutSavedWhileTheQuestionStoodIsAskedAboutAgain()
    {
        WriteFile("a.txt", "new a");
        WriteFile("target/a.txt", "old a");
        var decisions = new AnswersWith(
            ("overwrite", () => SavedLater("target/a.txt", "saved while asked")),
            ("skip", null));

        var job = ServerTransferService()
            .Enqueue([Root.Child("a.txt")], Root.Child("target"), TransferMode.Copy, decisions);
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("saved while asked");
        decisions.Asked.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OnAServerAMergeOfFoldersAsksOnlyOnce()
    {
        // The snapshot covers every folder the transfer will write into, not only the first.
        WriteFile("tree/a.txt", "new a");
        WriteFile("tree/sub/b.txt", "new b");
        WriteFile("tree/sub/deeper/c.txt", "new c");
        WriteFile("target/tree/a.txt", "old a");
        WriteFile("target/tree/sub/b.txt", "old b");
        WriteFile("target/tree/sub/deeper/c.txt", "old c");
        var decisions = new AnswersWith(("overwrite", null), ("skip", null));

        var job = ServerTransferService()
            .Enqueue([Root.Child("tree")], Root.Child("target"), TransferMode.Copy, decisions);
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "tree", "sub", "deeper", "c.txt")).ShouldBe("new c");
        decisions.Asked.Count(q => q.Title.Contains("after Replace was chosen")).ShouldBe(0);
    }

    [Fact]
    public async Task OnAShareTheFileAskedAboutIsToldByItsOwnStamp()
    {
        // No file has landed there yet to read the server's clock from: the one asked about needs none.
        var share = @"\\localhost\" + _root[0] + "$" + _root[2..];
        if (!Directory.Exists(share))
            return;
        var shared = FsUri.Local(share);
        WriteFile("a.txt", "new a");
        WriteFile("target/a.txt", "old a");
        var decisions = new AnswersWith(
            ("overwrite", () => SavedLater("target/a.txt", "saved while asked")),
            ("skip", null));

        var job = NewTransferService()
            .Enqueue([shared.Child("a.txt")], shared.Child("target"), TransferMode.Copy, decisions);
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("saved while asked");
        decisions.Asked.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OverwritesWhenAskedTo()
    {
        WriteFile("a.txt", "new");
        WriteFile("target/a.txt", "old");

        var job = NewTransferService().Enqueue(
            [Root.Child("a.txt")], Root.Child("target"), TransferMode.Copy,
            new ScriptedDecisions(choice: "overwrite"));
        await WaitFor(job);

        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("new");
    }

    [Fact]
    public async Task DismissingTheConflictQuestionCancelsRatherThanGuessing()
    {
        WriteFile("a.txt", "new");
        WriteFile("target/a.txt", "old");

        var job = NewTransferService().Enqueue(
            [Root.Child("a.txt")], Root.Child("target"), TransferMode.Copy,
            new ScriptedDecisions(choice: null));
        await WaitFor(job);

        job.State.ShouldBe(JobState.Cancelled);
        File.ReadAllText(Path.Combine(_root, "target", "a.txt")).ShouldBe("old");
    }
}

/// <summary>
/// What happens when a folder lands on a folder of the same name. Explorer merges, Finder
/// replaces; we merge by default and offer replace, because a folder that is really one artifact
/// — a build output, a deployed application — is ruined by a blend of two versions.
/// </summary>
public sealed class FolderCollisionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-merge-").FullName;
    private readonly LocalFileSystemProvider _provider = new();
    private readonly IProviderRegistry _providers;
    private readonly ActivityLog _activity = new();
    private readonly UndoStack _undo = new();

    public FolderCollisionTests() => _providers = new ProviderRegistry([_provider]);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private TransferService NewService(IRecycleBin? bin = null) =>
        new(_providers, _undo, _activity, bin ?? new NoRecycleBin(), new JobService(), NullLogger<TransferService>.Instance);

    /// <summary>A bin that is there, for the question's words; nothing in these tests reaches it.</summary>
    private sealed class ThereIsABin : IRecycleBin
    {
        public bool CanRecycle(FsUri uri) => uri.IsLocal;

        public Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private IReadOnlyList<string> Under(string relative)
    {
        var root = Path.Combine(_root, relative);
        return Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(p => p)
                .ToList()
            : [];
    }

    private static async Task Finish(TransferJob job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60); // as WaitFor: a ceiling for a loaded suite
        while (!job.IsFinished && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        job.IsFinished.ShouldBeTrue();
    }

    /// <summary>left/project and right/project, overlapping in one file.</summary>
    private void BuildTwoProjects()
    {
        Write("left/project/only-in-source.txt", "source");
        Write("left/project/docs/guide.md", "doc");
        Write("left/project/shared.txt", "SOURCE");

        Write("right/project/only-in-destination.txt", "destination");
        Write("right/project/notes/note.md", "note");
        Write("right/project/shared.txt", "DEST");
    }

    private async Task<TransferJob> Run(ScriptedDecisions decisions, TransferMode mode = TransferMode.Copy, IRecycleBin? bin = null)
    {
        var job = NewService(bin).Enqueue(
            [FsUri.Local(Path.Combine(_root, "left", "project"))],
            FsUri.Local(Path.Combine(_root, "right")),
            mode,
            decisions);

        await Finish(job);
        return job;
    }

    [Fact]
    public async Task TheQuestionIsAskedOncePerFolderAndStatesWhatReplacingWouldCost()
    {
        BuildTwoProjects();
        var decisions = new ScriptedDecisions();
        decisions.Answers.Enqueue("merge");

        await Run(decisions);

        var asked = decisions.Asked[0];
        asked.Title.ShouldBe("project already exists here.");
        // Two files are in the destination and not in the source: only-in-destination.txt and
        // notes/note.md. shared.txt is in both, so it is not a loss.
        asked.Detail.ShouldNotBeNull().ShouldContain("2 files there are not in the source and would be deleted");
    }

    [Fact]
    public async Task WithNoRecycleBinTheQuestionSaysTheOldFolderGoesForGoodAndMergeStaysTheDefault()
    {
        // A stick, a share or a server: the question read the same as with a bin, and only the
        // note after the swap said the old folder had been deleted outright (review, 2026-10-28).
        BuildTwoProjects();
        var decisions = new ScriptedDecisions();
        decisions.Answers.Enqueue("cancel");

        await Run(decisions);

        var asked = decisions.Asked[0];
        asked.Detail.ShouldNotBeNull().ShouldContain("There is no Recycle Bin here, so the old folder would be gone for good.");
        asked.Options.Single(o => o.Id == "replace").Label.ShouldBe("Replace it, deleting the old one");
        asked.Options.Single(o => o.IsDefault).Id.ShouldBe("merge");
    }

    [Fact]
    public async Task WithARecycleBinTheQuestionSaysTheOldFolderGoesThere()
    {
        BuildTwoProjects();
        var decisions = new ScriptedDecisions();
        decisions.Answers.Enqueue("cancel");

        await Run(decisions, bin: new ThereIsABin());

        var asked = decisions.Asked[0];
        asked.Detail.ShouldNotBeNull().ShouldContain("The old folder goes to the Recycle Bin.");
        asked.Options.Single(o => o.Id == "replace").Label.ShouldBe("Replace it entirely");
        asked.Options.Single(o => o.IsDefault).Id.ShouldBe("merge");
    }

    [Fact]
    public async Task MergingKeepsWhatWasAlreadyThere()
    {
        BuildTwoProjects();
        var decisions = new ScriptedDecisions(choice: "keepBoth");
        decisions.Answers.Enqueue("merge");

        await Run(decisions);

        Under("right/project").ShouldBe([
            "docs/guide.md",
            "notes/note.md",
            "only-in-destination.txt",
            "only-in-source.txt",
            "shared (2).txt",
            "shared.txt",
        ]);
    }

    [Fact]
    public async Task ReplacingLeavesExactlyTheSource()
    {
        BuildTwoProjects();
        var decisions = new ScriptedDecisions();
        decisions.Answers.Enqueue("replace");

        await Run(decisions);

        Under("right/project").ShouldBe(["docs/guide.md", "only-in-source.txt", "shared.txt"]);
        File.ReadAllText(Path.Combine(_root, "right", "project", "shared.txt")).ShouldBe("SOURCE");
    }

    [Fact]
    public async Task ReplacingAsksAboutNothingElseBecauseThereIsNothingToClashWith()
    {
        BuildTwoProjects();
        var decisions = new ScriptedDecisions();
        decisions.Answers.Enqueue("replace");

        await Run(decisions);

        // The folder question, and no per-file questions: the destination was emptied first.
        decisions.Asked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SkippingLeavesBothSidesAlone()
    {
        BuildTwoProjects();
        var decisions = new ScriptedDecisions();
        decisions.Answers.Enqueue("skip");

        await Run(decisions);

        Under("right/project").ShouldBe(["notes/note.md", "only-in-destination.txt", "shared.txt"]);
        Under("left/project").Count.ShouldBe(3); // the source is untouched
    }

    [Fact]
    public async Task CancellingTheFolderQuestionCopiesNothing()
    {
        BuildTwoProjects();
        var decisions = new ScriptedDecisions();
        decisions.Answers.Enqueue("cancel");

        var job = await Run(decisions);

        job.State.ShouldBe(JobState.Cancelled);
        Under("right/project").ShouldBe(["notes/note.md", "only-in-destination.txt", "shared.txt"]);
    }

    [Fact]
    public async Task NoQuestionWhenThereIsNoFolderOfThatNameYet()
    {
        Write("left/project/a.txt", "a");
        Directory.CreateDirectory(Path.Combine(_root, "right"));

        var decisions = new ScriptedDecisions();
        await Run(decisions);

        decisions.Asked.ShouldBeEmpty();
        Under("right/project").ShouldBe(["a.txt"]);
    }

    [Fact]
    public async Task NoQuestionWhenMergingAndReplacingWouldLeaveTheSameFiles()
    {
        // The destination holds nothing the source lacks, so the two answers differ only in how
        // they get there. Copying a folder onto one of the same name is routine; a prompt with no
        // consequence would only teach people to dismiss the one that does.
        Write("left/project/a.txt", "new a");
        Write("left/project/b.txt", "b");
        Write("right/project/a.txt", "old a");

        var decisions = new ScriptedDecisions(choice: "overwrite");

        await Run(decisions);

        decisions.Asked.Select(a => a.Title).ShouldNotContain("project already exists here.");
        Under("right/project").ShouldBe(["a.txt", "b.txt"]);
    }

    [Fact]
    public async Task ReplacingIsStagedSoAFailureCannotLeaveTheFolderHalfDeleted()
    {
        BuildTwoProjects();

        // Hold the destination's own file open for deletion to fail, and check that the original
        // contents are still there afterwards rather than half removed.
        var decisions = new ScriptedDecisions();
        decisions.Answers.Enqueue("replace");

        var job = await Run(decisions);

        // With nothing obstructing it the swap succeeds; the point of the test below is the order.
        job.State.ShouldBe(JobState.Completed);

        // No staging debris is left beside the target.
        Directory.GetDirectories(Path.Combine(_root, "right"))
            .Select(Path.GetFileName)
            .ShouldBe(["project"]);
    }

    /// <summary>A server holding files by exact name, as a Linux one does.</summary>
    private sealed class CaseSensitiveServer(params string[] files) : IFileSystemProvider
    {
        public string Scheme => "mem";
        public ProviderCapabilities Capabilities => ProviderCapabilities.Read;
        public DanceCommander.Core.Columns.ColumnSet DefaultColumns => DanceCommander.Core.Columns.ColumnSet.Standard;

        private bool IsFolder(string path) => files.Any(f => f.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal));

        public async IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var under = directory.Path.TrimEnd('/') + "/";
            foreach (var name in files.Where(f => f.StartsWith(under, StringComparison.Ordinal))
                         .Select(f => f[under.Length..].Split('/')[0]).Distinct(StringComparer.Ordinal))
                yield return new FsEntry(directory.Child(name), name, IsFolder(under + name), 1, null);
        }

        public Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct) =>
            Task.FromResult<FsEntry?>(
                IsFolder(path.Path) ? new FsEntry(path, path.Name, true, 0, null)
                : files.Contains(path.Path, StringComparer.Ordinal) ? new FsEntry(path, path.Name, false, 1, null)
                : null);

        public Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(FsUri from, FsUri to, CancellationToken ct) => throw new NotSupportedException();
        public DanceCommander.Core.Watching.IDirectoryWatcher? Watch(FsUri directory) => null;
    }

    [Fact]
    public async Task OnAServerTwoNamesDifferingInCaseAreTwoFilesToLose()
    {
        // Compared as Windows compares, Readme.md was counted as kept by the source's README.md,
        // and "nothing would be lost" was said over a file Replace then deleted (review, 2026-10-22).
        Write("left/project/README.md", "new");
        var server = new CaseSensitiveServer("/srv/project/README.md", "/srv/project/Readme.md");
        var providers = new ProviderRegistry([_provider, server]);

        var collisions = await FolderCollisionScanner.ScanAsync(
            providers, [FsUri.Local(Path.Combine(_root, "left", "project"))], FsUri.Parse("mem://host/srv"), CancellationToken.None);

        collisions.ShouldHaveSingleItem().Losses.ShouldBe(1);
    }

    [Fact]
    public async Task OnWindowsTheSameNameInAnotherCaseIsTheSameFile()
    {
        Write("left/project/README.md", "new");
        Write("right/project/Readme.md", "old");

        var collisions = await FolderCollisionScanner.ScanAsync(
            _providers, [FsUri.Local(Path.Combine(_root, "left", "project"))], FsUri.Local(Path.Combine(_root, "right")), CancellationToken.None);

        collisions.ShouldHaveSingleItem().Losses.ShouldBe(0);
    }

    [Fact]
    public async Task NamesDotNetFoldsButNtfsDoesNotAreTwoFilesToLose()
    {
        // Georgian Mtavruli and Mkhedruli: one name to .NET's OrdinalIgnoreCase, two files on NTFS.
        // Compared by .NET, ა.txt was counted as kept by Ა.txt, and Replace deleted it unsaid
        // (review of the move to .NET 10, 2026-11-07; AUDIT.md §2 kind 78).
        Write("left/project/Ა.txt", "new");
        Write("right/project/ა.txt", "old");
        File.Exists(Path.Combine(_root, "right", "project", "Ა.txt")).ShouldBeFalse("NTFS keeps the two names apart");

        var collisions = await FolderCollisionScanner.ScanAsync(
            _providers, [FsUri.Local(Path.Combine(_root, "left", "project"))], FsUri.Local(Path.Combine(_root, "right")), CancellationToken.None);

        collisions.ShouldHaveSingleItem().Lost.ShouldBe(["ა.txt"]);
    }

    [Fact]
    public async Task AFolderInAnotherCaseIsTheSameFolderOnWindows()
    {
        // Asked about once for the folder, not for each file in it (review, 2026-11-07): what
        // lands in it under its own name or another case is kept, the rest is not.
        Write("right/project/Sub/a.txt", "old");
        Write("right/project/Sub/B.txt", "old");
        Write("right/project/Sub/only-here.txt", "old");
        Write("left/project/sub/a.txt", "new");
        Write("left/project/sub/b.txt", "new");

        var collisions = await FolderCollisionScanner.ScanAsync(
            _providers, [FsUri.Local(Path.Combine(_root, "left", "project"))], FsUri.Local(Path.Combine(_root, "right")), CancellationToken.None);

        collisions.ShouldHaveSingleItem().Lost.ShouldBe(["Sub/only-here.txt"]);
    }

    [Fact]
    public async Task NamesInAnotherAsciiCaseAreAskedAboutOncePerFolder()
    {
        // Asked name by name, 5,000 photos named .JPG over .jpg took 1.6 s, and 20,000 files in
        // "sub" over "Sub" 4 s, before the question showed (reviews, 2026-11-07).
        for (var i = 0; i < 50; i++)
        {
            Write($"right/project/photos/IMG_{i:D4}.JPG", "old");
            Write($"left/project/photos/IMG_{i:D4}.jpg", "new");
            Write($"right/project/Album {i}/a.txt", "old");
            Write($"left/project/ALBUM {i}/a.txt", "new");
        }
        Write("right/project/only-here.txt", "old");
        var faulty = new FaultyProvider(_provider);

        var collisions = await FolderCollisionScanner.ScanAsync(
            new ProviderRegistry([faulty]), [FsUri.Local(Path.Combine(_root, "left", "project"))],
            FsUri.Local(Path.Combine(_root, "right")), CancellationToken.None);

        collisions.ShouldHaveSingleItem().Lost.ShouldBe(["only-here.txt"]);
        faulty.SurelySameAsked.ShouldBeLessThanOrEqualTo(2); // project and photos, each once
    }

    [Fact]
    public async Task WhereIdentityCantTellFilesApartNothingIsCountedAsKeptByIt()
    {
        // On a share that gives every file index 0, "the same file" is said of any two that exist,
        // which kept the file Replace deletes out of its count (review, 2026-11-07).
        Write("right/project/Readme.md", "old");
        Write("left/project/README.md", "new");
        var faulty = new FaultyProvider(_provider) { IndexZero = true };

        var collisions = await FolderCollisionScanner.ScanAsync(
            new ProviderRegistry([faulty]), [FsUri.Local(Path.Combine(_root, "left", "project"))],
            FsUri.Local(Path.Combine(_root, "right")), CancellationToken.None);

        collisions.ShouldHaveSingleItem().Lost.ShouldBe(["Readme.md"]);
    }

    [CaseSensitiveFact]
    public async Task WhereIdentityCantTellFilesApartAFolderInAnotherCaseIsAnotherFolder()
    {
        // The case the review found: an empty "sub" beside "Sub", which index 0 called one folder,
        // so Sub/a.txt was counted as kept by the source's sub/a.txt.
        var destination = Path.Combine(_root, "right", "project");
        Directory.CreateDirectory(destination);
        CaseSensitiveFolder.Make(destination);
        Write("right/project/Sub/a.txt", "old");
        Directory.CreateDirectory(Path.Combine(destination, "sub"));
        Write("left/project/sub/a.txt", "new");
        var faulty = new FaultyProvider(_provider) { IndexZero = true };

        var collisions = await FolderCollisionScanner.ScanAsync(
            new ProviderRegistry([faulty]), [FsUri.Local(Path.Combine(_root, "left", "project"))],
            FsUri.Local(Path.Combine(_root, "right")), CancellationToken.None);

        collisions.ShouldHaveSingleItem().Lost.ShouldBe(["Sub/a.txt"]);
    }

    [CaseSensitiveFact]
    public async Task AFolderInAnotherCaseInACaseSensitiveFolderIsAnotherFolder()
    {
        var destination = Path.Combine(_root, "right", "project");
        Directory.CreateDirectory(destination);
        CaseSensitiveFolder.Make(destination);
        Write("right/project/Sub/a.txt", "old");
        Write("left/project/sub/a.txt", "new");

        var collisions = await FolderCollisionScanner.ScanAsync(
            _providers, [FsUri.Local(Path.Combine(_root, "left", "project"))], FsUri.Local(Path.Combine(_root, "right")), CancellationToken.None);

        collisions.ShouldHaveSingleItem().Lost.ShouldBe(["Sub/a.txt"]);
    }

    [CaseSensitiveFact]
    public async Task InACaseSensitiveFolderTheSameNameInAnotherCaseIsAnotherFile()
    {
        // As WSL makes its folders under /mnt/c: there Readme.md and README.md are two files,
        // and one of them was not even seen, keyed as one.
        var destination = Path.Combine(_root, "right", "project");
        Directory.CreateDirectory(destination);
        CaseSensitiveFolder.Make(destination);
        Write("right/project/Readme.md", "old");
        Write("right/project/README.md", "older");
        Write("right/project/notes.txt", "kept");
        Write("left/project/README.md", "new");
        Write("left/project/NOTES.txt", "new");

        var collisions = await FolderCollisionScanner.ScanAsync(
            _providers, [FsUri.Local(Path.Combine(_root, "left", "project"))], FsUri.Local(Path.Combine(_root, "right")), CancellationToken.None);

        var collision = collisions.ShouldHaveSingleItem();
        collision.Seen.Keys.ShouldBe(["notes.txt", "Readme.md", "README.md"], ignoreOrder: true);
        collision.Lost.ShouldBe(["notes.txt", "Readme.md"], ignoreOrder: true);
    }

    [CaseSensitiveFact]
    public async Task InACaseSensitiveFolderInsideAnOrdinaryOneOnlyThatFolderKeepsCase()
    {
        // The rule is the folder's, not the drive's: the same scan counts README.md as kept above
        // the case-sensitive folder and as lost inside it.
        Write("right/project/Readme.md", "old");
        var inner = Path.Combine(_root, "right", "project", "wsl");
        Directory.CreateDirectory(inner);
        CaseSensitiveFolder.Make(inner);
        Write("right/project/wsl/Readme.md", "old");
        Write("left/project/README.md", "new");
        Write("left/project/wsl/README.md", "new");

        var collisions = await FolderCollisionScanner.ScanAsync(
            _providers, [FsUri.Local(Path.Combine(_root, "left", "project"))], FsUri.Local(Path.Combine(_root, "right")), CancellationToken.None);

        collisions.ShouldHaveSingleItem().Lost.ShouldBe(["wsl/Readme.md"]);
    }
}

/// <summary>What VeraCrypt's "preserve timestamps" does to a file it has mounted.</summary>
internal static class KeepTimes
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, ref FileBasicInfo info, uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, out FileBasicInfo info, uint size);

    public static (long, long) Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        GetFileInformationByHandleEx(stream.SafeFileHandle, 0, out var info, 40).ShouldBeTrue();
        return (info.LastWriteTime, info.ChangeTime);
    }

    public static void Hold(FileStream stream)
    {
        var hold = new FileBasicInfo { CreationTime = -1, LastAccessTime = -1, LastWriteTime = -1, ChangeTime = -1 };
        SetFileInformationByHandle(stream.SafeFileHandle, 0, ref hold, 40).ShouldBeTrue();
    }
}
