using System.Runtime.InteropServices;
using DanceCommander.Core.Columns;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Transfer;
using DanceCommander.Core.Watching;
using DanceCommander.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// §1.6, files are never lost: transfers that are cut short, cancelled or fail part way, and the
/// cases where one file has two names. Each test states where every file is afterwards.
/// </summary>
public sealed class FileSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-safety-").FullName;
    private readonly FaultyProvider _provider = new(new LocalFileSystemProvider());
    private readonly IProviderRegistry _providers;
    private readonly UndoStack _undo = new();
    private readonly ActivityLog _activity = new();

    public FileSafetyTests() => _providers = new ProviderRegistry([_provider]);

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private FsUri Root => FsUri.Local(_root);

    private FsUri At(string relative) => FsUri.Local(Path.Combine(_root, relative.Replace('/', '\\')));

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative.Replace('/', '\\')));

    private bool Exists(string relative) => File.Exists(Path.Combine(_root, relative.Replace('/', '\\')));

    /// <summary>Every file under a folder, relative to it.</summary>
    private IReadOnlyList<string> Under(string relative)
    {
        var root = Path.Combine(_root, relative);
        return Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList()
            : [];
    }

    private async Task<TransferJob> Run(IReadOnlyList<FsUri> sources, string target, TransferMode mode, IDecisionHost decisions)
    {
        var job = new TransferService(
                _providers, _undo, _activity, new NoRecycleBin(), new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue(sources, At(target), mode, decisions);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!job.IsFinished && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        job.IsFinished.ShouldBeTrue();
        return job;
    }

    // ---- a copy cut short ----------------------------------------------------------------

    [Fact]
    public async Task ACopyWhoseSourceEndsEarlyKeepsTheFileItWouldHaveReplaced()
    {
        // A server that closes the connection mid-body: the read just ends, without an error.
        Write("new/report.txt", "the whole of the new report");
        Write("target/report.txt", "the old report");
        _provider.CutShort = uri => uri.Name == "report.txt" && uri.Path.Contains("/new/");

        var job = await Run([At("new/report.txt")], "target", TransferMode.Copy, new ScriptedDecisions(choice: "overwrite"));

        job.State.ShouldBe(JobState.Failed);
        Read("target/report.txt").ShouldBe("the old report");
        Under("target").ShouldBe(["report.txt"]); // and no .part
    }

    [Fact]
    public async Task ACopyThatFailsPartWayLeavesNothingAtTheName()
    {
        Write("a.bin", "twenty bytes of data");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        _provider.FailRead = uri => uri.Name == "a.bin";

        var job = await Run([At("a.bin")], "target", TransferMode.Copy, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Failed);
        Under("target").ShouldBeEmpty();
    }

    [Fact]
    public async Task AMoveWhoseSourceEndsEarlyKeepsTheSource()
    {
        // Across volumes a move is a copy and a delete; the delete used to follow the read loop
        // on nothing but the absence of an exception.
        Write("a.txt", "all of the file");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        _provider.AcrossVolumes = true;
        _provider.CutShort = uri => uri.Name == "a.txt" && !uri.Path.Contains("/target/");

        var job = await Run([At("a.txt")], "target", TransferMode.Move, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Failed);
        Read("a.txt").ShouldBe("all of the file");
        Under("target").ShouldBeEmpty();
    }

    [Fact]
    public async Task AMoveAcrossVolumesStillMovesWhenNothingGoesWrong()
    {
        Write("a.txt", "all of the file");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        _provider.AcrossVolumes = true;

        var job = await Run([At("a.txt")], "target", TransferMode.Move, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Completed);
        Exists("a.txt").ShouldBeFalse();
        Read("target/a.txt").ShouldBe("all of the file");
    }

    [Fact]
    public async Task RetryAfterTheOriginalWasEditedCopiesItAgainRatherThanDeletingTheEdits()
    {
        // "Close it there, then Retry": the user saved their edits first. The retry used to check
        // only the copy, then delete the original, which by then held the newer version.
        var original = Write("report.txt", "draft");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        _provider.AcrossVolumes = true;
        _provider.InUseOnce = At("report.txt");
        _provider.WhileInUse = () => File.WriteAllText(original, "draft, and the afternoon's edits");

        var job = await Run([At("report.txt")], "target", TransferMode.Move, new ScriptedDecisions(choice: "retry"));

        job.State.ShouldBe(JobState.Completed);
        Read("target/report.txt").ShouldBe("draft, and the afternoon's edits");
        Exists("report.txt").ShouldBeFalse();
    }

    // ---- a move that stops part way ------------------------------------------------------

    [Fact]
    public async Task AMoveCancelledAtAQuestionIsRecordedAndUndoable()
    {
        Write("src/a.txt", "a");
        Write("src/b.txt", "b");
        Write("src/c.txt", "c");
        Write("target/b.txt", "already there");

        var job = await Run(
            [At("src/a.txt"), At("src/b.txt"), At("src/c.txt")], "target", TransferMode.Move,
            new ScriptedDecisions(choice: "cancel"));

        job.State.ShouldBe(JobState.Cancelled);
        Read("target/a.txt").ShouldBe("a");
        _activity.Entries.ShouldContain(e => e.Description.Contains("Moved 1 of 3") && e.Description.Contains("(cancelled)"));

        // a has left its folder, and Ctrl+Z is how it gets home.
        _undo.CanUndo.ShouldBeTrue();
        await _undo.UndoAsync(CancellationToken.None);

        Under("src").ShouldBe(["a.txt", "b.txt", "c.txt"]);
        Under("target").ShouldBe(["b.txt"]);
        Read("target/b.txt").ShouldBe("already there");
    }

    [Fact]
    public async Task AMoveThatFailsPartWayIsUndoable()
    {
        Write("src/a.txt", "a");
        Write("src/b.txt", "b");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        _provider.FailMove = (from, _) => from.Name == "b.txt";

        var job = await Run([At("src/a.txt"), At("src/b.txt")], "target", TransferMode.Move, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Failed);
        Under("target").ShouldBe(["a.txt"]);

        _provider.FailMove = (_, _) => false;
        await _undo.UndoAsync(CancellationToken.None);
        Under("src").ShouldBe(["a.txt", "b.txt"]);
    }

    // ---- replacing a folder --------------------------------------------------------------

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

    private static ScriptedDecisions Replace()
    {
        var decisions = new ScriptedDecisions();
        decisions.Answers.Enqueue("replace");
        return decisions;
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    public async Task AFileSkippedInsideAReplacedFolderKeepsTheOldOne(TransferMode mode)
    {
        // Skipped means left as it was; the replaced folder was discarded whole with it, and with
        // no Recycle Bin (a stick) the old file was gone for good (review, 2026-10-14).
        BuildTwoProjects();
        var decisions = Replace();
        decisions.Answers.Enqueue("skip");

        using (File.Open(Path.Combine(_root, "left", "project", "shared.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            await Run([At("left/project")], "right", mode, decisions);

        Read("right/project/shared.txt").ShouldBe("DEST");
        Read("right/project/only-in-source.txt").ShouldBe("source");
        Exists("right/project/only-in-destination.txt").ShouldBeFalse();
        Read("left/project/shared.txt").ShouldBe("SOURCE");
        Directory.GetDirectories(Path.Combine(_root, "right")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AMoveIntoAReplacedFolderThatFailsPutsEveryFileBack()
    {
        // The files had been renamed into the staging folder, so they had already left the
        // source; discarding staging was a permanent recursive delete (found 2026-10-03).
        BuildTwoProjects();
        _provider.FailMove = (from, to) => from.Name == "shared.txt" && to.Path.Contains(".dc-incoming-");

        var job = await Run([At("left/project")], "right", TransferMode.Move, Replace());

        job.State.ShouldBe(JobState.Failed);
        Under("left/project").ShouldBe(["docs/guide.md", "only-in-source.txt", "shared.txt"]);
        Under("right/project").ShouldBe(["notes/note.md", "only-in-destination.txt", "shared.txt"]);
        Read("right/project/shared.txt").ShouldBe("DEST");
        Directory.GetDirectories(Path.Combine(_root, "right")).Select(Path.GetFileName).ShouldBe(["project"]);

        // Nothing in the target changed, so there is nothing to undo.
        _undo.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task ACopyIntoAReplacedFolderThatFailsLeavesBothSidesAsTheyWere()
    {
        BuildTwoProjects();
        _provider.FailRead = uri => uri.Name == "shared.txt" && uri.Path.Contains("/left/");

        var job = await Run([At("left/project")], "right", TransferMode.Copy, Replace());

        job.State.ShouldBe(JobState.Failed);
        Under("left/project").ShouldBe(["docs/guide.md", "only-in-source.txt", "shared.txt"]);
        Under("right/project").ShouldBe(["notes/note.md", "only-in-destination.txt", "shared.txt"]);
        Directory.GetDirectories(Path.Combine(_root, "right")).Select(Path.GetFileName).ShouldBe(["project"]);
    }

    private TransferService NewService() =>
        new(_providers, _undo, _activity, new NoRecycleBin(), new JobService(), NullLogger<TransferService>.Instance);

    private static async Task Finished(TransferJob job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!job.IsFinished && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        job.IsFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task CtrlZWhileATransferRunsSaysSoRatherThanUndoTheOneBefore()
    {
        // Pressed to stop the copy, it undid the operation before it beside the running job
        // (review, 2026-10-22).
        Write("src/a.txt", "a");
        Directory.CreateDirectory(Path.Combine(_root, "T"));
        var earlierUndone = false;
        _undo.Push(new DelegateUndo("earlier", "", _ =>
        {
            earlierUndone = true;
            return Task.CompletedTask;
        }));
        using var reading = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        _provider.AfterRead = _ =>
        {
            reading.Set();
            go.Wait(TimeSpan.FromSeconds(10));
        };

        var job = NewService().Enqueue([At("src/a.txt")], At("T"), TransferMode.Copy, new ScriptedDecisions());
        reading.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        await Should.ThrowAsync<UndoBusyException>(() => _undo.UndoAsync(CancellationToken.None));
        earlierUndone.ShouldBeFalse();

        go.Set();
        await Finished(job);
        _undo.NextDescription.ShouldNotBeNull().ShouldStartWith("Copied");
    }

    [Fact]
    public async Task WhatWasDoneWhileATransferRanWaitsForIt()
    {
        // Undoing beside a running job, if only what was done after it began, lost files in
        // review (2026-10-30): a rename made meanwhile waits for the copy like anything else.
        Write("src/a.txt", "a");
        Directory.CreateDirectory(Path.Combine(_root, "T"));
        using var reading = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        _provider.AfterRead = _ =>
        {
            reading.Set();
            go.Wait(TimeSpan.FromSeconds(10));
        };

        var job = NewService().Enqueue([At("src/a.txt")], At("T"), TransferMode.Copy, new ScriptedDecisions());
        reading.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        _undo.Push(new DelegateUndo("rename while it ran", "", _ => Task.CompletedTask));
        _undo.UnderWay.ShouldBe(["copying to T"]);
        (await Should.ThrowAsync<UndoBusyException>(() => _undo.UndoAsync(CancellationToken.None)))
            .Message.ShouldBe("Still copying to T.");

        go.Set();
        await Finished(job);
        _undo.Pending.Count.ShouldBe(2);
        _undo.NextDescription.ShouldNotBeNull().ShouldStartWith("Copied");
    }

    [Fact]
    public async Task AFileSavedIntoAFolderBeingReplacedIsKept()
    {
        // "Replace it entirely" counted the files there then; one saved during the copy was
        // discarded with the old folder, and with no bin for good (review, 2026-10-22).
        BuildTwoProjects();
        _provider.AfterRead = uri =>
        {
            if (uri.Name != "shared.txt")
                return;
            _provider.AfterRead = null;
            Write("right/project/saved-meanwhile.txt", "someone's");
        };

        var job = await Run([At("left/project")], "right", TransferMode.Copy, Replace());

        job.State.ShouldBe(JobState.Completed, job.Error);
        Read("right/project/saved-meanwhile.txt").ShouldBe("someone's");
        Read("right/project/shared.txt").ShouldBe("SOURCE");
        Exists("right/project/only-in-destination.txt").ShouldBeFalse();
        _activity.Entries[0].Detail.ShouldNotBeNull().ShouldContain("arrived or changed after Replace");
    }

    [CaseSensitiveFact]
    public async Task AFileChangedAfterTheAnswerBesideItsNameInAnotherCaseIsKept()
    {
        // In a case-sensitive folder Readme.md and README.md are two files. Keyed regardless of
        // case, what Replace was answered about held one entry for both, the one listed last, so a
        // change to the one listed first went unseen and, with no bin, was deleted with the old
        // folder (reviews, 2026-11-07). Only that one changes here, and its size doesn't.
        var destination = Path.Combine(_root, "right", "project");
        Directory.CreateDirectory(destination);
        CaseSensitiveFolder.Make(destination);
        Write("right/project/Readme.md", "old one");
        Write("right/project/README.md", "old two");
        Write("left/project/README.md", "new");
        Write("left/project/other.txt", "new");
        var hidden = Path.GetFileName(Directory.EnumerateFiles(destination).First());
        _provider.AfterRead = uri =>
        {
            if (uri.Name != "other.txt")
                return;
            _provider.AfterRead = null;
            Write($"right/project/{hidden}", "CHANGED");
        };

        var job = await Run([At("left/project")], "right", TransferMode.Copy, Replace());

        job.State.ShouldBe(JobState.Completed, job.Error);
        var everything = Directory.GetFiles(Path.Combine(_root, "right"), "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
        everything.ShouldContain("CHANGED");
        everything.ShouldContain("new");
    }

    [Fact]
    public async Task AFileSavedJustBeforeTheSwapKeepsTheOldFolderBesideTheNewOne()
    {
        // After the last look for arrivals and before the old folder stepped aside: never seen,
        // and with no bin deleted with the old folder for good (BUGS.md, 2026-10-28).
        BuildTwoProjects();
        _provider.BeforeMove = (from, to) =>
        {
            if (!to.Name.Contains(".dc-old-"))
                return;
            _provider.BeforeMove = null;
            Write("right/project/saved-at-the-swap.txt", "someone's");
        };

        var job = await Run([At("left/project")], "right", TransferMode.Copy, Replace());

        job.State.ShouldBe(JobState.Completed, job.Error);
        Under("right/project").ShouldBe(["docs/guide.md", "only-in-source.txt", "shared.txt"]);
        var aside = Directory.GetDirectories(Path.Combine(_root, "right")).Single(d => d.Contains(".dc-old-"));
        File.ReadAllText(Path.Combine(aside, "saved-at-the-swap.txt")).ShouldBe("someone's");
        _activity.Entries[0].Detail.ShouldNotBeNull().ShouldContain("was left beside the new one");

        // Ctrl+Z says where the old folder is, not that it cannot be brought back.
        var refused = await Should.ThrowAsync<CannotUndoException>(() => _undo.UndoAsync(CancellationToken.None));
        refused.Message.ShouldContain($"the old one is beside it, as {Path.GetFileName(aside)}");
    }

    [Fact]
    public async Task AMoveWhoseOldFolderWasKeptAsideSaysWhereOnUndo()
    {
        BuildTwoProjects();
        _provider.BeforeMove = (from, to) =>
        {
            if (!to.Name.Contains(".dc-old-"))
                return;
            _provider.BeforeMove = null;
            Write("right/project/saved-at-the-swap.txt", "someone's");
        };

        var job = await Run([At("left/project")], "right", TransferMode.Move, Replace());
        job.State.ShouldBe(JobState.Completed, job.Error);
        var aside = Path.GetFileName(Directory.GetDirectories(Path.Combine(_root, "right")).Single(d => d.Contains(".dc-old-")));

        await _undo.UndoAsync(CancellationToken.None);

        Under("left/project").ShouldBe(["docs/guide.md", "only-in-source.txt", "shared.txt"]);
        Read($"right/{aside}/saved-at-the-swap.txt").ShouldBe("someone's");
        _activity.Entries[0].Detail.ShouldNotBeNull().ShouldContain($"The old project is still at {aside}");
    }

    [Fact]
    public async Task AFolderReplacedWithNothingArrivingIsDeletedOnceSwapped()
    {
        // The last look finds only what was agreed to, so the old folder goes as before.
        BuildTwoProjects();

        var job = await Run([At("left/project")], "right", TransferMode.Copy, Replace());

        job.State.ShouldBe(JobState.Completed, job.Error);
        Directory.GetDirectories(Path.Combine(_root, "right")).Select(Path.GetFileName).ShouldBe(["project"]);
    }

    [Fact]
    public async Task AFileChangedInAFolderBeingReplacedIsKeptToo()
    {
        // Saved again over a file the user agreed to lose, or over one the new folder also has:
        // the newer content went with the old folder (review, 2026-10-23).
        BuildTwoProjects();
        _provider.AfterRead = uri =>
        {
            if (uri.Name != "shared.txt")
                return;
            _provider.AfterRead = null;
            Write("right/project/only-in-destination.txt", "edited meanwhile");
            Write("right/project/shared.txt", "DEST, edited meanwhile");
        };

        var job = await Run([At("left/project")], "right", TransferMode.Copy, Replace());

        job.State.ShouldBe(JobState.Completed, job.Error);
        Read("right/project/only-in-destination.txt").ShouldBe("edited meanwhile");
        Read("right/project/shared.txt").ShouldBe("SOURCE");
        Read("right/project/shared (2).txt").ShouldBe("DEST, edited meanwhile");
        Exists("right/project/notes/note.md").ShouldBeFalse();
    }

    [Fact]
    public async Task AReplaceAnsweredEarlierIsNotAnAnswerAboutAFileAnotherTransferBroughtIn()
    {
        // "Replace" for the rest of this transfer wrote over a file another transfer had just
        // moved in, its only copy (review, 2026-10-22).
        Write("src/a.txt", "new a");
        Write("src/c.txt", "new c");
        Write("T/a.txt", "old a");
        Write("Q/c.txt", "moved in");
        var service = NewService();
        var answers = new ScriptedDecisions();
        answers.Answers.Enqueue("overwrite");
        answers.Answers.Enqueue("keepBoth");
        _provider.AfterRead = uri =>
        {
            if (uri.Name != "a.txt")
                return;
            _provider.AfterRead = null;
            var other = service.Enqueue([At("Q/c.txt")], At("T"), TransferMode.Move, new ScriptedDecisions());
            SpinWait.SpinUntil(() => other.IsFinished, TimeSpan.FromSeconds(10)).ShouldBeTrue();
        };

        var job = service.Enqueue([At("src/a.txt"), At("src/c.txt")], At("T"), TransferMode.Copy, answers);
        await Finished(job);

        job.State.ShouldBe(JobState.Completed, job.Error);
        Read("T/a.txt").ShouldBe("new a");
        Read("T/c.txt").ShouldBe("moved in");
        answers.Asked.Count.ShouldBe(2);
        answers.Asked[1].Title.ShouldContain("another transfer");
        Under("T").Count.ShouldBe(3);
    }

    [Fact]
    public async Task ACopyThatFailsKeepsWhatSomeoneElsePutInItsStagingFolder()
    {
        // The staging folder was deleted whole as "only copies", past the Recycle Bin, with
        // whatever another job or Explorer had put in it meanwhile (review, 2026-10-22).
        BuildTwoProjects();
        _provider.FailRead = uri =>
        {
            if (uri.Name != "shared.txt" || !uri.Path.Contains("/left/"))
                return false;
            var staging = Directory.GetDirectories(Path.Combine(_root, "right"), "*.dc-incoming-*").Single();
            File.WriteAllText(Path.Combine(staging, "saved-meanwhile.txt"), "someone's");
            return true;
        };

        var job = await Run([At("left/project")], "right", TransferMode.Copy, Replace());

        job.State.ShouldBe(JobState.Failed);
        Under("right/project").ShouldBe(["notes/note.md", "only-in-destination.txt", "shared.txt"]);
        var left = Directory.GetDirectories(Path.Combine(_root, "right"), "*.dc-incoming-*").ShouldHaveSingleItem();
        Directory.GetFiles(left, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ShouldBe(["saved-meanwhile.txt"]);
    }

    [Fact]
    public async Task AMoveIntoAReplacedFolderCanBeUndone()
    {
        // The undo used to point into the staging folder, which had been renamed by then.
        BuildTwoProjects();

        var job = await Run([At("left/project")], "right", TransferMode.Move, Replace());
        job.State.ShouldBe(JobState.Completed);
        Under("right/project").ShouldBe(["docs/guide.md", "only-in-source.txt", "shared.txt"]);

        await _undo.UndoAsync(CancellationToken.None);

        Under("left/project").ShouldBe(["docs/guide.md", "only-in-source.txt", "shared.txt"]);

        // No bin here, so the old folder is gone for good, and the undo says so rather than
        // "Undid" over a target that is not as it was (driving, 2026-10-14).
        _activity.Entries[0].Detail.ShouldNotBeNull().ShouldContain("cannot be brought back");
    }

    [Fact]
    public async Task UndoingAMoveIntoAFolderThatWasThereKeepsTheFolder()
    {
        // The undo removed every folder a moved file sat in once emptied, a folder that had been
        // there before included (§4, 2026-10-08). Only the folders the move made go.
        Write("src/box/a.txt", "a");
        Directory.CreateDirectory(Path.Combine(_root, "target", "box"));

        var job = await Run([At("src/box")], "target", TransferMode.Move, new ScriptedDecisions());
        job.State.ShouldBe(JobState.Completed);
        Read("target/box/a.txt").ShouldBe("a");

        await _undo.UndoAsync(CancellationToken.None);

        Read("src/box/a.txt").ShouldBe("a");
        Directory.Exists(Path.Combine(_root, "target", "box")).ShouldBeTrue();
    }

    [Fact]
    public async Task UndoingAMoveRemovesTheFoldersItMade()
    {
        Write("src/box/inner/a.txt", "a");
        Directory.CreateDirectory(Path.Combine(_root, "target"));

        await Run([At("src/box")], "target", TransferMode.Move, new ScriptedDecisions());
        await _undo.UndoAsync(CancellationToken.None);

        Read("src/box/inner/a.txt").ShouldBe("a");
        Directory.GetFileSystemEntries(Path.Combine(_root, "target")).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFolderMovedOnOneDriveIsOneRenameAndSoIsItsUndo()
    {
        Write("src/box/inner/a.txt", "a");
        Write("src/box/b.txt", "b");
        Directory.CreateDirectory(Path.Combine(_root, "target"));

        var job = await Run([At("src/box")], "target", TransferMode.Move, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Completed);
        _provider.Renamed.ShouldBe([(At("src/box"), At("target/box"))]);
        Under("target/box").ShouldBe(["b.txt", "inner/a.txt"]);
        Directory.Exists(Path.Combine(_root, "src", "box")).ShouldBeFalse();

        await _undo.UndoAsync(CancellationToken.None);

        _provider.Renamed[^1].ShouldBe((At("target/box"), At("src/box")));
        Under("src/box").ShouldBe(["b.txt", "inner/a.txt"]);
        Directory.GetFileSystemEntries(Path.Combine(_root, "target")).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFolderWindowsWontRenameWholeIsMovedFileByFileAndUndoneSo()
    {
        // As when a file in it is open, or a terminal stands in it.
        Write("src/box/inner/a.txt", "a");
        Write("src/box/b.txt", "b");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        _provider.RefuseMove = (from, _) => from.Name == "box";

        var job = await Run([At("src/box")], "target", TransferMode.Move, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Completed);
        Under("target/box").ShouldBe(["b.txt", "inner/a.txt"]);
        Directory.Exists(Path.Combine(_root, "src", "box")).ShouldBeFalse();

        await _undo.UndoAsync(CancellationToken.None);

        Under("src/box").ShouldBe(["b.txt", "inner/a.txt"]);
        Directory.GetFileSystemEntries(Path.Combine(_root, "target")).ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoPickedFoldersOfOneNameAreNeitherRenamedWholeNorReplaceEachOthersFiles()
    {
        // Renamed whole, the first would be at target/box unseen by the check that keeps a second
        // source from replacing what a first one brought.
        Write("a/box/x.txt", "from a");
        Write("b/box/x.txt", "from b");
        Directory.CreateDirectory(Path.Combine(_root, "target"));

        await Run([At("a/box"), At("b/box")], "target", TransferMode.Move, new ScriptedDecisions(choice: "overwrite"));

        _provider.Renamed.ShouldNotContain(r => r.From.Name == "box");
        Under("target/box").Select(f => Read(Path.Combine("target", "box", f))).ShouldBe(["from a", "from b"], ignoreOrder: true);
    }

    [Fact]
    public async Task AFolderAndOneInsideItBothPickedComeBackWhole()
    {
        // Picked from a search: box/inner goes first, then box. Undone in that order, inner made a
        // new box for itself, and the real one could not come back (review, 2026-10-26).
        Write("src/box/inner/a.txt", "a");
        Write("src/box/b.txt", "b");
        Directory.CreateDirectory(Path.Combine(_root, "target"));

        await Run([At("src/box/inner"), At("src/box")], "target", TransferMode.Move, new ScriptedDecisions());
        Under("target").ShouldBe(["box/b.txt", "inner/a.txt"]);

        await _undo.UndoAsync(CancellationToken.None);

        Under("src/box").ShouldBe(["b.txt", "inner/a.txt"]);
        Directory.GetFileSystemEntries(Path.Combine(_root, "target")).ShouldBeEmpty();
    }

    [Fact]
    public async Task AReplaceAnsweredForAFolderThatWentMeanwhileIsNotARenameIntoItsPlace()
    {
        // Renamed whole into the name, the swap then sent the moved folder to the bin as the one
        // being replaced (review, 2026-10-26).
        Write("src/box/same.txt", "new");
        Write("target/box/only-there.txt", "old");
        var decisions = new WhileAsked(
            () => Directory.Move(Path.Combine(_root, "target", "box"), Path.Combine(_root, "elsewhere")),
            "replace");

        await Run([At("src/box")], "target", TransferMode.Move, decisions);

        _provider.Renamed.ShouldNotContain(r => r.From == At("src/box"));
        Read("target/box/same.txt").ShouldBe("new");
        Read("elsewhere/only-there.txt").ShouldBe("old");
    }

    [Fact]
    public async Task AReplaceAnsweredForAFolderThatIsAFileNowLeavesTheFile()
    {
        // With no bin, the swap set the file aside and deleted it as the old folder (review, 2026-10-26).
        Write("src/box/same.txt", "new");
        Write("target/box/only-there.txt", "old");
        var decisions = new WhileAsked(
            () =>
            {
                Directory.Move(Path.Combine(_root, "target", "box"), Path.Combine(_root, "elsewhere"));
                Write("target/box", "a file now");
            },
            "replace");

        await Run([At("src/box")], "target", TransferMode.Move, decisions);

        Read("target/box").ShouldBe("a file now");
        Read("src/box/same.txt").ShouldBe("new");
        Read("elsewhere/only-there.txt").ShouldBe("old");
    }

    /// <summary>src/box and target/box, the target's box moved to elsewhere while Replace is asked.</summary>
    private WhileAsked BoxGoesWhileAsked()
    {
        Write("src/box/same.txt", "new");
        Write("target/box/only-there.txt", "old");
        return new WhileAsked(
            () => Directory.Move(Path.Combine(_root, "target", "box"), Path.Combine(_root, "elsewhere")),
            "replace");
    }

    [Fact]
    public async Task AMoveIntoAFolderThatWentMeanwhileUndoesHome()
    {
        await Run([At("src/box")], "target", TransferMode.Move, BoxGoesWhileAsked());

        await _undo.UndoAsync(CancellationToken.None);

        Read("src/box/same.txt").ShouldBe("new");
        Read("elsewhere/only-there.txt").ShouldBe("old");
        Directory.GetFileSystemEntries(Path.Combine(_root, "target")).ShouldBeEmpty();
    }

    [Fact]
    public async Task ACopyIntoAFolderThatWentMeanwhileCanStillBeUndone()
    {
        // Counted as a replace, the copy's undo was refused as having replaced what was there,
        // beside a note saying nothing was (review, 2026-10-26).
        var decisions = BoxGoesWhileAsked();
        var bin = new KeepingRecycleBin();
        var job = new TransferService(_providers, _undo, _activity, bin, new JobService(), NullLogger<TransferService>.Instance)
            .Enqueue([At("src/box")], At("target"), TransferMode.Copy, decisions);
        await Finished(job);

        job.State.ShouldBe(JobState.Completed);
        Read("target/box/same.txt").ShouldBe("new");

        await _undo.UndoAsync(CancellationToken.None);

        bin.Recycled.ShouldContain(u => u == At("target/box/same.txt") || u == At("target/box"));
        bin.Recycled.ShouldNotContain(u => u.Path.Contains("/elsewhere") || u.Path.Contains("/src/"));
    }

    [Fact]
    public async Task AFolderThatWentMeanwhileButWhoseNameIsRefusedPutsTheMoveBack()
    {
        var decisions = BoxGoesWhileAsked();
        _provider.RefuseMove = (from, to) => from.Name.Contains(".dc-incoming-") && to == At("target/box");

        var job = await Run([At("src/box")], "target", TransferMode.Move, decisions);

        job.State.ShouldBe(JobState.Failed);
        Read("src/box/same.txt").ShouldBe("new");
        Read("elsewhere/only-there.txt").ShouldBe("old");
        Directory.GetFileSystemEntries(Path.Combine(_root, "target")).ShouldBeEmpty();
    }

    /// <summary>Does something the moment a question is asked, as the user in the other pane might, then answers.</summary>
    private sealed class WhileAsked(Action meanwhile, string answer) : IDecisionHost
    {
        public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct)
        {
            meanwhile();
            return Task.FromResult<string?>(answer);
        }

        public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) => Task.FromResult<SecretAnswer?>(null);

        public void Report(Notice notice)
        {
        }
    }

    [Fact]
    public async Task AFolderMovedOntoOneOfItsNameStillMerges()
    {
        Write("src/box/new.txt", "new");
        Write("target/box/old.txt", "old");

        await Run([At("src/box")], "target", TransferMode.Move, new ScriptedDecisions(choice: "merge"));

        Under("target/box").ShouldBe(["new.txt", "old.txt"]);
        _provider.Renamed.ShouldNotContain(r => r.From == At("src/box"));
    }

    [Fact]
    public async Task ReplacingAFolderWithoutARecycleBinPutsTheOldOneAsideUntilTheNewOneIsIn()
    {
        // Where there is no Recycle Bin (a server), the old folder was deleted before the new one
        // was moved in. If that move fails, the old folder has to still be there.
        BuildTwoProjects();
        _provider.FailMove = (from, to) => from.Name.Contains(".dc-incoming-") && to.Name == "project";

        var job = await Run([At("left/project")], "right", TransferMode.Copy, Replace());

        job.State.ShouldBe(JobState.Failed);
        Under("right/project").ShouldBe(["notes/note.md", "only-in-destination.txt", "shared.txt"]);
        Directory.GetDirectories(Path.Combine(_root, "right")).Select(Path.GetFileName).ShouldBe(["project"]);
    }

    // ---- one file, two names -------------------------------------------------------------

    [Fact]
    public async Task CopyingAFileOntoAnotherNameForItselfLeavesItAlone()
    {
        // A hard link stands in for a subst drive or a junction: two addresses, one file.
        var original = Write("a.txt", "the only copy");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        HardLink.Create(Path.Combine(_root, "target", "a.txt"), original);

        var decisions = new ScriptedDecisions(choice: "overwrite");
        var job = await Run([At("a.txt")], "target", TransferMode.Move, decisions);

        job.State.ShouldBe(JobState.Completed);
        decisions.Asked.ShouldBeEmpty(); // nothing to decide: it is already there
        Read("a.txt").ShouldBe("the only copy");
        Read("target/a.txt").ShouldBe("the only copy");
        _activity.Entries[0].Detail.ShouldNotBeNull().ShouldContain("same file");
    }

    [Fact]
    public async Task AFolderThatKeepsWhatWasSkippedIsNotReportedAsMoved()
    {
        // A collection holding tree/ was pointed at target/tree although tree/ was still there,
        // holding the file the skip had kept; its b.txt then meant the other, older b.txt.
        Write("tree/a.txt", "a");
        Write("tree/b.txt", "new b");
        Write("target/tree/b.txt", "old b");
        var feed = new RelocationFeed();
        var heard = new List<Relocation>();
        feed.Relocated += (_, moved) => heard.AddRange(moved);
        var service = new TransferService(
            _providers, _undo, _activity, new NoRecycleBin(), new JobService(), NullLogger<TransferService>.Instance, feed);
        // Nothing in target/tree is missing from tree/, so no folder question: only b.txt clashes.
        var decisions = new ScriptedDecisions(choice: "skip");

        var job = service.Enqueue([At("tree")], At("target"), TransferMode.Move, decisions);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!job.IsFinished && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        job.State.ShouldBe(JobState.Completed);
        Read("tree/b.txt").ShouldBe("new b");
        heard.ShouldNotContain(r => r.From == At("tree"));
        heard.ShouldContain(new Relocation(At("tree/a.txt"), At("target/tree/a.txt")));
    }

    // ---- names -----------------------------------------------------------------------------

    [Fact]
    public async Task TwoFilesOfOneNameInOneMoveNeverReplaceEachOther()
    {
        // A flattened tree gives two README.md; "Replace", answered for a real clash, used to let
        // the second write over the first, which had already left its own folder.
        Write("tree/a/README.md", "first");
        Write("tree/b/README.md", "second");
        Write("tree/c.txt", "new c");
        Write("target/c.txt", "old c");
        var decisions = new ScriptedDecisions(choice: "overwrite");

        var job = await Run(
            [At("tree/c.txt"), At("tree/a/README.md"), At("tree/b/README.md")], "target", TransferMode.Move, decisions);

        job.State.ShouldBe(JobState.Completed);
        Under("target").ShouldBe(["README (2).md", "README.md", "c.txt"]);
        new[] { Read("target/README.md"), Read("target/README (2).md") }.ShouldBe(["first", "second"], ignoreOrder: true);
        _activity.Entries[0].Detail.ShouldNotBeNull().ShouldContain("numbered name");
    }

    [Theory]
    [InlineData("readme.md", true)]
    [InlineData("docs/guide.md", true)]
    [InlineData(@"..\..\AppData\Startup\x.bat", false)] // a Linux name with backslashes in it
    [InlineData("docs/../../x", false)]
    [InlineData("a:stream", false)] // an alternate data stream on Windows
    [InlineData("trailing.", false)]
    [InlineData("CON", false)]
    [InlineData("nul.txt", false)]
    [InlineData("console.txt", true)]
    public void OnlyPlainNamesAreWrittenToADisk(string relativePath, bool safe) =>
        TransferService.IsSafeOnWindows(relativePath).ShouldBe(safe);

    // ---- where things run, and where deleting is safe ------------------------------------

    [Fact]
    public void AToolInAFlattenedViewRunsInTheFolderItIsAViewOf()
    {
        DanceCommander.Extensibility.ToolRunner.WorkingDirectoryFor(FsUri.View("flat", Root))
            .ShouldBe(Root.ToLocalPath());
        DanceCommander.Extensibility.ToolRunner.WorkingDirectoryFor(FsUri.Remote("sftp", "host", "/srv"))
            .ShouldBe(Path.GetTempPath());
    }

    [Fact]
    public void OnlyAFixedDiskHasARecycleBin()
    {
        // A share has none, and the shell's "recycle" there deletes outright.
        var bin = new WindowsRecycleBin();
        bin.CanRecycle(FsUri.Local(@"\\server\share\photos")).ShouldBeFalse();
        bin.CanRecycle(FsUri.Remote("sftp", "host", "/srv/x")).ShouldBeFalse();
        bin.CanRecycle(Root).ShouldBe(new DriveInfo(Path.GetPathRoot(_root)!).DriveType == DriveType.Fixed);
    }

    // ---- undoing a file this application made --------------------------------------------

    [Fact]
    public async Task UndoingCreateFileKeepsAFileThatWasEditedSince()
    {
        var operations = new FileOperationService(_providers, _undo, _activity, new NoRecycleBin());
        var made = await operations.CreateFileAsync(Root, "pasted.txt", "hello"u8.ToArray(), "…", CancellationToken.None);

        File.WriteAllText(made!.ToLocalPath(), "hello, and then a page of writing");

        await Should.ThrowAsync<CannotUndoException>(() => _undo.UndoAsync(CancellationToken.None));
        Read("pasted.txt").ShouldBe("hello, and then a page of writing");
    }

    [Fact]
    public async Task UndoingCreateFileSendsAnEditedFileToTheRecycleBinWhereThereIsOne()
    {
        var bin = new KeepingRecycleBin();
        var operations = new FileOperationService(_providers, _undo, _activity, bin);
        var made = await operations.CreateFileAsync(Root, "pasted.txt", "hello"u8.ToArray(), "…", CancellationToken.None);
        File.WriteAllText(made!.ToLocalPath(), "edited");

        await _undo.UndoAsync(CancellationToken.None);

        bin.Recycled.ShouldBe([made]);
    }

    // ---- a copy into its own folder -------------------------------------------------------

    [Fact]
    public async Task CopyingAFileIntoItsOwnFolderMakesADuplicateBesideIt()
    {
        // Ctrl+C, Ctrl+V in one place: it used to meet itself, be skipped as the same file, and
        // the job ended having done nothing.
        Write("work/checksums.sha256", "abc *x.iso\n");

        var job = await Run([At("work/checksums.sha256")], "work", TransferMode.Copy, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Completed);
        Under("work").ShouldBe(["checksums (2).sha256", "checksums.sha256"]);
        Read("work/checksums (2).sha256").ShouldBe("abc *x.iso\n");
        Read("work/checksums.sha256").ShouldBe("abc *x.iso\n");
    }

    [Fact]
    public async Task CopyingAFolderIntoItsOwnFolderDuplicatesTheWholeOfIt()
    {
        Write("work/my.photos/a.jpg", "a");
        Write("work/my.photos/inner/b.jpg", "b");

        var job = await Run([At("work/my.photos")], "work", TransferMode.Copy, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Completed);
        // A folder's name has no extension to keep: "my.photos (2)", not "my (2).photos".
        Under("work").ShouldBe(["my.photos (2)/a.jpg", "my.photos (2)/inner/b.jpg", "my.photos/a.jpg", "my.photos/inner/b.jpg"]);
    }

    [Fact]
    public async Task TwoDuplicatesNeverShareAName()
    {
        // "a (2).txt" is a source too: a's copy must not be planned onto it, nor onto a name the
        // other copy takes, or the originals would be the ones met at those names.
        Write("work/a.txt", "a");
        Write("work/a (2).txt", "a two");

        var job = await Run([At("work/a.txt"), At("work/a (2).txt")], "work", TransferMode.Copy, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Completed);
        Read("work/a.txt").ShouldBe("a");
        Read("work/a (2).txt").ShouldBe("a two");
        Read("work/a (3).txt").ShouldBe("a");
        Read("work/a (2) (2).txt").ShouldBe("a two");
        Under("work").Count.ShouldBe(4);
    }

    [Fact]
    public async Task AFolderInItsOwnPlaceSharingANameIsNeverReplacedWithNothing()
    {
        // T/work and X/work (a search's two hits) copied into T, and the user answers Replace for
        // the clash X/work has with T/work. T/work's own files must still be there afterwards,
        // whatever stands at the name: never recycled with nothing in its place.
        Write("T/work/mine.txt", "mine");
        Write("X/work/theirs.txt", "theirs");

        await Run([At("T/work"), At("X/work")], "T", TransferMode.Copy, new ScriptedDecisions(choice: "replace"));

        Under("T").ShouldContain("work/mine.txt");
        Read("T/work/mine.txt").ShouldBe("mine");
        Under("T").ShouldContain("work/theirs.txt");
    }

    [Fact]
    public async Task AFolderMovedOntoItselfUnderAnotherNameKeepsItsEmptyFolders()
    {
        // A junction stands in for the short name %TEMP% gives, or a subst drive: every file was
        // skipped as the same file, and then the empty folders were removed as "emptied".
        Write("work/build/a.txt", "kept");
        Directory.CreateDirectory(Path.Combine(_root, "work", "build", "empty"));
        Directory.CreateDirectory(Path.Combine(_root, "work", "build", "sub", "deeper"));
        Junction.Create(Path.Combine(_root, "alias"), Path.Combine(_root, "work"));
        try
        {
            await Run([At("work/build")], "alias", TransferMode.Move, new ScriptedDecisions(choice: "overwrite"));

            Read("work/build/a.txt").ShouldBe("kept");
            Directory.Exists(Path.Combine(_root, "work", "build", "empty")).ShouldBeTrue();
            Directory.Exists(Path.Combine(_root, "work", "build", "sub", "deeper")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(Path.Combine(_root, "alias"));
        }
    }

    private string? LinkAt(string relative) => new DirectoryInfo(Path.Combine(_root, relative.Replace('/', '\\'))).LinkTarget;

    [Theory]
    [InlineData("proj", "T/proj/deps")]
    [InlineData("proj/deps", "T/deps")]
    public async Task AMoveCarriesAFolderLinkAsItselfAndUndoBringsItBack(string selected, string landsAt)
    {
        // The move went file by file through the junction, emptying the shared folder it points
        // to; undo then made a real folder where the link had been (review, 2026-10-22).
        Write("proj/main.txt", "mine");
        Write("shared/lib.txt", "everyone's");
        Directory.CreateDirectory(Path.Combine(_root, "T"));
        var shared = Path.Combine(_root, "shared");
        Junction.Create(Path.Combine(_root, "proj", "deps"), shared);

        var job = await Run([At(selected)], "T", TransferMode.Move, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Completed, job.Error);
        Read("shared/lib.txt").ShouldBe("everyone's");
        LinkAt(landsAt).ShouldBe(shared);
        Directory.Exists(Path.Combine(_root, "proj", "deps")).ShouldBeFalse();

        await _undo.UndoAsync(CancellationToken.None);

        LinkAt("proj/deps").ShouldBe(shared);
        Read("proj/main.txt").ShouldBe("mine");
        Read("shared/lib.txt").ShouldBe("everyone's");
        Directory.Exists(Path.Combine(_root, landsAt)).ShouldBeFalse();
        Directory.Delete(Path.Combine(_root, "proj", "deps"));
    }

    [Fact]
    public async Task ALinkWhoseNameIsTakenThereStaysWhereItWasAndSaysSo()
    {
        Write("proj/main.txt", "mine");
        Write("shared/lib.txt", "everyone's");
        Write("T/proj/deps/theirs.txt", "theirs");
        var shared = Path.Combine(_root, "shared");
        Junction.Create(Path.Combine(_root, "proj", "deps"), shared);
        var merge = new ScriptedDecisions();
        merge.Answers.Enqueue("merge");

        var job = await Run([At("proj")], "T", TransferMode.Move, merge);

        job.State.ShouldBe(JobState.Completed, job.Error);
        LinkAt("proj/deps").ShouldBe(shared);
        Read("T/proj/main.txt").ShouldBe("mine");
        Read("T/proj/deps/theirs.txt").ShouldBe("theirs");
        Read("shared/lib.txt").ShouldBe("everyone's");
        _activity.Entries[0].Detail.ShouldNotBeNull().ShouldContain("Links to other folders left where they were");
        Directory.Delete(Path.Combine(_root, "proj", "deps"));
    }

    [Fact]
    public async Task ALinkPickedOntoAFolderOfItsNameIsLeftWithoutAQuestion()
    {
        // The scan took the picked link for a folder and offered Replace for a staging folder
        // that was never made (review, 2026-10-23).
        Write("shared/lib.txt", "everyone's");
        Write("T/deps/theirs.txt", "theirs");
        var shared = Path.Combine(_root, "shared");
        Junction.Create(Path.Combine(_root, "deps"), shared);
        var decisions = new ScriptedDecisions();

        var job = await Run([At("deps")], "T", TransferMode.Move, decisions);

        job.State.ShouldBe(JobState.Completed, job.Error);
        decisions.Asked.ShouldBeEmpty();
        LinkAt("deps").ShouldBe(shared);
        Read("T/deps/theirs.txt").ShouldBe("theirs");
        Read("shared/lib.txt").ShouldBe("everyone's");
        Directory.Delete(Path.Combine(_root, "deps"));
    }

    [Fact]
    public async Task ReplacingAFolderCountsWhatSitsAtAMovedLinksName()
    {
        // A link a move carries keeps nothing in the destination for certain: it may stay where it
        // is, and the old folder's "deps" went with the swap, never counted (review, 2026-10-23).
        Write("proj/main.txt", "mine");
        Write("shared/lib.txt", "everyone's");
        Write("T/proj/deps", "a file named deps");
        Junction.Create(Path.Combine(_root, "proj", "deps"), Path.Combine(_root, "shared"));

        var moving = await FolderCollisionScanner.ScanAsync(
            _providers, [At("proj")], At("T"), CancellationToken.None, linksAsThemselves: true);
        var copying = await FolderCollisionScanner.ScanAsync(_providers, [At("proj")], At("T"), CancellationToken.None);

        moving.ShouldHaveSingleItem().Lost.ShouldBe(["deps"]);
        copying.ShouldHaveSingleItem().Lost.ShouldBe(["deps"]);
        Directory.Delete(Path.Combine(_root, "proj", "deps"));
    }

    [Fact]
    public async Task ALinkThatCannotGoBackDoesNotStopTheFilesGoingHome()
    {
        // One link failing stopped the whole Ctrl+Z, which was used up with every file still at
        // the target (review, 2026-10-23). An empty proj already there makes the move merge file
        // by file, the way that carries links one at a time.
        Write("proj/main.txt", "mine");
        Write("shared/lib.txt", "everyone's");
        Directory.CreateDirectory(Path.Combine(_root, "T", "proj"));
        Junction.Create(Path.Combine(_root, "proj", "deps"), Path.Combine(_root, "shared"));
        await Run([At("proj")], "T", TransferMode.Move, new ScriptedDecisions());
        _provider.Renamed.ShouldNotContain(r => r.From == At("proj"));
        Directory.Delete(Path.Combine(_root, "T", "proj", "deps"));
        Write("T/proj/deps/real.txt", "a real folder now");

        await _undo.UndoAsync(CancellationToken.None);

        Read("proj/main.txt").ShouldBe("mine");
        Read("T/proj/deps/real.txt").ShouldBe("a real folder now");
        Read("shared/lib.txt").ShouldBe("everyone's");
        _activity.Entries.ShouldContain(e => e.Detail != null && e.Detail.Contains("The link deps stays"));
    }

    [Fact]
    public async Task AFolderMovedWholeGoesHomeWholeWithWhatWasPutInItSince()
    {
        // Renamed in one step, link and all, and back the same way: what was made inside it at
        // the target goes home with it rather than staying behind on its own.
        Write("proj/main.txt", "mine");
        Write("shared/lib.txt", "everyone's");
        Directory.CreateDirectory(Path.Combine(_root, "T"));
        Junction.Create(Path.Combine(_root, "proj", "deps"), Path.Combine(_root, "shared"));
        await Run([At("proj")], "T", TransferMode.Move, new ScriptedDecisions());
        Directory.Delete(Path.Combine(_root, "T", "proj", "deps"));
        Write("T/proj/deps/real.txt", "a real folder now");

        await _undo.UndoAsync(CancellationToken.None);

        Read("proj/main.txt").ShouldBe("mine");
        Read("proj/deps/real.txt").ShouldBe("a real folder now");
        Read("shared/lib.txt").ShouldBe("everyone's");
        Directory.GetFileSystemEntries(Path.Combine(_root, "T")).ShouldBeEmpty();
    }

    [Fact]
    public void AJunctionMadeAgainElsewherePointsWhereItDidAndTheOriginalGoes()
    {
        // What a move across volumes does; here on one, so the test can run anywhere.
        Write("shared/lib.txt", "everyone's");
        Directory.CreateDirectory(Path.Combine(_root, "T"));
        var shared = Path.Combine(_root, "shared");
        Junction.Create(Path.Combine(_root, "deps"), shared);

        DirectoryLinks.Remake(Path.Combine(_root, "deps"), Path.Combine(_root, "T", "deps"));

        LinkAt("T/deps").ShouldBe(shared);
        Directory.Exists(Path.Combine(_root, "deps")).ShouldBeFalse();
        Read("shared/lib.txt").ShouldBe("everyone's");
        Read("T/deps/lib.txt").ShouldBe("everyone's");
        Directory.Delete(Path.Combine(_root, "T", "deps"));
    }

    [Fact]
    public void ALinkIsNeverMadeOverSomethingAlreadyThere()
    {
        Write("shared/lib.txt", "everyone's");
        Write("T/deps/theirs.txt", "theirs");
        var shared = Path.Combine(_root, "shared");
        Junction.Create(Path.Combine(_root, "deps"), shared);

        Should.Throw<IOException>(() => DirectoryLinks.Remake(Path.Combine(_root, "deps"), Path.Combine(_root, "T", "deps")));

        LinkAt("deps").ShouldBe(shared);
        Read("T/deps/theirs.txt").ShouldBe("theirs");
        Directory.Delete(Path.Combine(_root, "deps"));
    }

    [Fact]
    public void AFolderThatIsNoLinkIsNotMovedAsOne()
    {
        // Replaced by a real folder since it was listed: renaming it would move a whole tree.
        Write("deps/real.txt", "real");

        Should.Throw<IOException>(() => DirectoryLinks.Move(Path.Combine(_root, "deps"), Path.Combine(_root, "elsewhere")));

        Read("deps/real.txt").ShouldBe("real");
    }

    [Fact]
    public async Task ACopyThroughAFolderLinkStillCopies()
    {
        Write("proj/main.txt", "mine");
        Write("shared/lib.txt", "everyone's");
        Directory.CreateDirectory(Path.Combine(_root, "T"));
        Junction.Create(Path.Combine(_root, "proj", "deps"), Path.Combine(_root, "shared"));
        try
        {
            await Run([At("proj")], "T", TransferMode.Copy, new ScriptedDecisions());

            Read("T/proj/deps/lib.txt").ShouldBe("everyone's");
            Read("shared/lib.txt").ShouldBe("everyone's");
        }
        finally
        {
            Directory.Delete(Path.Combine(_root, "proj", "deps"));
        }
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    public async Task AFolderIsNeverPutInsideItself(TransferMode mode)
    {
        Write("work/a.txt", "kept");
        Directory.CreateDirectory(Path.Combine(_root, "work", "sub"));

        var job = await Run([At("work")], "work/sub", mode, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Failed);
        job.Error.ShouldNotBeNull().ShouldContain("into itself");
        Under("work").ShouldBe(["a.txt"]);
    }

    [Fact]
    public async Task AFolderIsNeverPutInsideItselfUnderAnotherName()
    {
        Write("work/a.txt", "kept");
        Directory.CreateDirectory(Path.Combine(_root, "work", "sub"));
        Junction.Create(Path.Combine(_root, "alias"), Path.Combine(_root, "work"));
        try
        {
            var job = await Run([At("work")], "alias/sub", TransferMode.Copy, new ScriptedDecisions());

            job.State.ShouldBe(JobState.Failed);
            Under("work").ShouldBe(["a.txt"]);
        }
        finally
        {
            Directory.Delete(Path.Combine(_root, "alias"));
        }
    }

    [Theory]
    [InlineData("FAT32", 4294967295L)]
    [InlineData("FAT", 2147483647L)]
    [InlineData("exFAT", null)]
    [InlineData("NTFS", null)]
    [InlineData("ReFS", null)]
    public void OnlyFatLimitsAFilesSize(string format, long? most) =>
        (VolumeLimits.For(format)?.MaxFile).ShouldBe(most);

    [Fact]
    public async Task AFolderIsKnownUnderAnotherName()
    {
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        Junction.Create(Path.Combine(_root, "alias"), Path.Combine(_root, "work"));
        try
        {
            var local = new LocalFileSystemProvider();
            (await local.IsSameFileAsync(At("work"), At("alias"), CancellationToken.None)).ShouldBeTrue();
            (await local.IsSameFileAsync(At("work"), Root, CancellationToken.None)).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(Path.Combine(_root, "alias"));
        }
    }

    [Fact]
    public void AShortNameIsTheSameAddressAsItsLongOne()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "a folder with a long name")).FullName;
        var buffer = new char[1024];
        var length = ShortName.Get(folder, buffer);
        var shortened = new string(buffer, 0, length);
        // A volume with short names turned off has none to give; there is nothing to expand then.
        if (length == 0 || string.Equals(shortened, folder, StringComparison.OrdinalIgnoreCase))
            return;

        FsUri.Local(shortened).ShouldBe(FsUri.Local(folder));
        FsUri.Local(shortened + @"\not made yet.txt").ShouldBe(FsUri.Local(folder + @"\not made yet.txt"));
    }

    [Fact]
    public async Task MovingIntoItsOwnFolderIsStillNothingToDo()
    {
        Write("work/notes.txt", "kept");

        await Run([At("work/notes.txt")], "work", TransferMode.Move, new ScriptedDecisions());

        Under("work").ShouldBe(["notes.txt"]);
        Read("work/notes.txt").ShouldBe("kept");
    }

    // ---- a move across volumes -------------------------------------------------------------

    [Fact]
    public async Task AReadOnlyFileMovesAcrossVolumes()
    {
        // The rename it stands in for never minded read-only; the delete after the copy did, and
        // stopped the move with every such file in both places.
        var path = Write("src/locked.txt", "read only");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        _provider.AcrossVolumes = true;

        var job = await Run([At("src/locked.txt")], "target", TransferMode.Move, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Completed, job.Error);
        Exists("src/locked.txt").ShouldBeFalse();
        Read("target/locked.txt").ShouldBe("read only");
    }

    [Fact]
    public async Task AFileSavedWhileItIsMovedIsKept()
    {
        // Saved again after it was read: deleting it would keep the older copy and lose the newer.
        Write("src/notes.txt", "first");
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        _provider.AcrossVolumes = true;
        _provider.AfterRead = uri =>
        {
            if (uri.Name == "notes.txt")
            {
                _provider.AfterRead = null;
                File.AppendAllText(uri.ToLocalPath(), ", then more");
            }
        };

        var job = await Run([At("src/notes.txt")], "target", TransferMode.Move, new ScriptedDecisions());

        job.State.ShouldBe(JobState.Failed);
        Read("src/notes.txt").ShouldBe("first, then more");
        Read("target/notes.txt").ShouldBe("first");
    }

    private sealed class KeepingRecycleBin : IRecycleBin
    {
        public List<FsUri> Recycled { get; } = [];

        public bool CanRecycle(FsUri uri) => true;

        public Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct)
        {
            Recycled.AddRange(paths);
            return Task.CompletedTask;
        }
    }
}

internal static class HardLink
{
    public static void Create(string link, string existing)
    {
        if (!CreateHardLink(link, existing, IntPtr.Zero))
            throw new IOException($"Could not make a hard link (error {Marshal.GetLastWin32Error()}).");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

internal static class Junction
{
    /// <summary>A directory junction, which unlike a symbolic link needs no elevation.</summary>
    public static void Create(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new IOException($"Could not make a junction (exit {process.ExitCode}).");
    }
}

internal static class ShortName
{
    public static int Get(string path, char[] buffer) => GetShortPathNameW(path, buffer, buffer.Length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathNameW(string longPath, [Out] char[] shortPath, int size);
}

/// <summary>The local disk, with faults put in wherever a test wants one.</summary>
internal sealed class FaultyProvider(LocalFileSystemProvider inner) : IFileSystemProvider, IFileIdentity, IMovesLinks
{
    public Task MoveLinkAsync(FsUri from, FsUri to, CancellationToken ct) => inner.MoveLinkAsync(from, to, ct);

    /// <summary>Reads of these end after a few bytes, without an error, as a dropped connection can.</summary>
    public Func<FsUri, bool> CutShort { get; set; } = _ => false;

    /// <summary>Reads of these throw after a few bytes.</summary>
    public Func<FsUri, bool> FailRead { get; set; } = _ => false;

    /// <summary>Renames between these fail outright.</summary>
    public Func<FsUri, FsUri, bool> FailMove { get; set; } = (_, _) => false;

    /// <summary>Nothing can be renamed, as between two volumes, so a move is a copy and a delete.</summary>
    public bool AcrossVolumes { get; set; }

    public string Scheme => inner.Scheme;

    public ProviderCapabilities Capabilities => inner.Capabilities;

    public ColumnSet DefaultColumns => inner.DefaultColumns;

    /// <summary>
    /// Listings of these throw, as a symlink WSL made does: Windows sees a folder it cannot open.
    /// </summary>
    public Func<FsUri, bool> FailList { get; set; } = _ => false;

    public IAsyncEnumerable<FsEntry> ListAsync(FsUri directory, CancellationToken ct) =>
        FailList(directory)
            ? throw new IOException("The file cannot be accessed by the system.")
            : inner.ListAsync(directory, ct);

    public Task<FsEntry?> StatAsync(FsUri path, CancellationToken ct) => inner.StatAsync(path, ct);

    /// <summary>Run once a file has been read whole and closed: the user saving it again, say.</summary>
    public Action<FsUri>? AfterRead { get; set; }

    public async Task<Stream> OpenReadAsync(FsUri file, CancellationToken ct)
    {
        var stream = await inner.OpenReadAsync(file, ct);
        return CutShort(file) ? new Stopping(stream, fail: false)
            : FailRead(file) ? new Stopping(stream, fail: true)
            : AfterRead is { } after ? new Closing(stream, () => after(file))
            : stream;
    }

    /// <summary>A read stream that tells when it is closed.</summary>
    private sealed class Closing(Stream inner, Action closed) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                closed();
            }

            base.Dispose(disposing);
        }
    }

    public Task<Stream> OpenWriteAsync(FsUri file, long? length, CancellationToken ct) => inner.OpenWriteAsync(file, length, ct);

    public Task CreateDirectoryAsync(FsUri directory, CancellationToken ct) => inner.CreateDirectoryAsync(directory, ct);

    /// <summary>The next delete of this file says it is in use, after running <see cref="WhileInUse"/>.</summary>
    public FsUri? InUseOnce { get; set; }

    /// <summary>What the user does while the file is "in use", such as saving edits to it.</summary>
    public Action? WhileInUse { get; set; }

    public Task DeleteAsync(FsUri path, bool recursive, CancellationToken ct)
    {
        if (InUseOnce is { } busy && busy == path)
        {
            InUseOnce = null;
            WhileInUse?.Invoke();
            throw new InUseException([path.ToLocalPath()], "Another program has it open.");
        }

        return inner.DeleteAsync(path, recursive, ct);
    }

    /// <summary>Every rename that went through, in order.</summary>
    public List<(FsUri From, FsUri To)> Renamed { get; } = [];

    /// <summary>Renames of these fail as Windows refuses one: an IOException, nothing moved.</summary>
    public Func<FsUri, FsUri, bool> RefuseMove { get; set; } = (_, _) => false;

    /// <summary>Run just before a rename: another program saving into the folder about to go, say.</summary>
    public Action<FsUri, FsUri>? BeforeMove { get; set; }

    public async Task MoveAsync(FsUri from, FsUri to, CancellationToken ct)
    {
        BeforeMove?.Invoke(from, to);
        if (FailMove(from, to))
            throw new InvalidOperationException($"Told to fail moving {from.Name}.");
        if (RefuseMove(from, to))
            throw new IOException($"Refused to move {from.Name}.");
        // Windows renames neither a file nor a folder from one volume to another.
        if (AcrossVolumes && Path.Exists(from.ToLocalPath()))
            throw new IOException("Not the same volume.");
        await inner.MoveAsync(from, to, ct);
        Renamed.Add((from, to));
    }

    public IDirectoryWatcher? Watch(FsUri directory) => null;

    public Task<bool> IsSameFileAsync(FsUri a, FsUri b, CancellationToken ct) =>
        IndexZero ? Task.FromResult(Path.Exists(a.ToLocalPath()) && Path.Exists(b.ToLocalPath())) : inner.IsSameFileAsync(a, b, ct);

    /// <summary>How many times <see cref="IsSurelySameFileAsync"/> was asked.</summary>
    public int SurelySameAsked;

    /// <summary>
    /// Every identity read as index 0, as on a share that gives every file that: the loose answer
    /// is then "the same" for any two that exist, and the sure one can't say.
    /// </summary>
    public bool IndexZero { get; set; }

    public async Task<bool> IsSurelySameFileAsync(FsUri a, FsUri b, CancellationToken ct)
    {
        Interlocked.Increment(ref SurelySameAsked);
        return !IndexZero && await inner.IsSurelySameFileAsync(a, b, ct);
    }

    /// <summary>Gives four bytes, then ends or throws.</summary>
    private sealed class Stopping(Stream inner, bool fail) : Stream
    {
        private int _left = 4;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left == 0)
                return fail ? throw new IOException("The connection was lost.") : 0;
            var read = inner.Read(buffer, offset, Math.Min(count, _left));
            _left -= read;
            return read;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
