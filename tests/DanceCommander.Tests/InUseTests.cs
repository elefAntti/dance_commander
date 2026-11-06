using System.Diagnostics;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using DanceCommander.Core.Transfer;
using DanceCommander.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// "In use by …" (DESIGN.md §1.3): a delete that fails because another program has the entry open
/// says which program, and offers Retry rather than a bare "code 32".
/// </summary>
public sealed class InUseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-inuse-").FullName;

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

    [Fact]
    public void SharingAndLockViolationsAreInUseAndNothingElseIs()
    {
        InUse.Is(new IOException("x", unchecked((int)0x80070020))).ShouldBeTrue();
        InUse.Is(new IOException("x", unchecked((int)0x80070021))).ShouldBeTrue();
        InUse.Is(new InUseException([], "x")).ShouldBeTrue();
        InUse.Is(new IOException("disk full", unchecked((int)0x80070070))).ShouldBeFalse();
        InUse.Is(new UnauthorizedAccessException()).ShouldBeFalse();
    }

    [Fact]
    public void TheExplanationNamesWhoAndSaysWhatToDo()
    {
        InUse.Explain([]).ShouldStartWith("Windows did not say which program.");
        InUse.Explain([new(1, "Windows PowerShell in Windows Terminal Host (process 1)")])
            .ShouldBe("Open in Windows PowerShell in Windows Terminal Host (process 1). Close it there, then Retry.");
        InUse.Explain([new(1, "A"), new(2, "B")]).ShouldBe("Open in A and B. Close them there, then Retry.");
        InUse.Explain([new(1, "A"), new(2, "B"), new(3, "C"), new(4, "D"), new(5, "E")])
            .ShouldBe("Open in A, B, C and 2 more. Close them there, then Retry.");
    }

    private sealed class BusyOnce(int failures) : IRecycleBin
    {
        public int Attempts { get; private set; }

        public bool CanRecycle(FsUri uri) => true;

        public Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct)
        {
            if (Attempts++ < failures)
                throw new InUseException(paths.Select(p => p.ToLocalPath()).ToList(), "in use");
            return Task.CompletedTask;
        }
    }

    /// <summary>Takes the first item, really, then stops on the next as the shell does on one in use.</summary>
    private sealed class TakesOneThenBusy : IRecycleBin
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public bool CanRecycle(FsUri uri) => true;

        public Task RecycleAsync(IReadOnlyList<FsUri> paths, CancellationToken ct)
        {
            Calls.Add(paths.Select(p => p.Name).ToList());
            if (Calls.Count > 1)
                return Task.CompletedTask;

            Directory.Delete(paths[0].ToLocalPath());
            throw new InUseException(paths.Skip(1).Select(p => p.ToLocalPath()).ToList(), "in use");
        }
    }

    [Fact]
    public async Task ARetryAfterARecycleStoppedPartWaySendsOnlyWhatIsLeft()
    {
        var bin = new TakesOneThenBusy();
        var activity = new ActivityLog();

        await Operations(bin, activity, usage: null)
            .DeleteAsync([Entry("a"), Entry("b")], permanent: false, new ScriptedDecisions(choice: "retry"), CancellationToken.None);

        // "a" went in the first try; sending it again would ask the bin for what isn't there.
        bin.Calls.ShouldBe([["a", "b"], ["b"]]);
        activity.Entries.ShouldHaveSingleItem().Description.ShouldStartWith("Sent 2 items to the Recycle Bin");
    }

    [Fact]
    public async Task TheQuestionNamesOnlyWhatIsStillThere()
    {
        var decisions = new ScriptedDecisions(choice: "cancel");

        await Operations(new TakesOneThenBusy(), new ActivityLog(), usage: null)
            .DeleteAsync([Entry("a"), Entry("b")], permanent: false, decisions, CancellationToken.None);

        // "a" is already in the bin, so it is "b" that is in use, not "2 items".
        decisions.Asked.ShouldHaveSingleItem().Title.ShouldBe("b is in use");
    }

    [Fact]
    public async Task GivingUpPartWayStillRecordsWhatWent()
    {
        var bin = new TakesOneThenBusy();
        var activity = new ActivityLog();

        await Operations(bin, activity, usage: null)
            .DeleteAsync([Entry("a"), Entry("b")], permanent: false, new ScriptedDecisions(choice: "cancel"), CancellationToken.None);

        // "a" is in the bin: said, and undoable, though the delete as a whole was given up.
        activity.Entries.ShouldHaveSingleItem().Description.ShouldBe("Sent 1 of 2 items to the Recycle Bin");
    }

    private sealed class Knows(params Holder[] holders) : IUsageFinder
    {
        public Task<IReadOnlyList<Holder>> FindAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Holder>>(holders);
    }

    /// <summary>A real folder: a recycle stopped part way is told apart by what is still in its place.</summary>
    private FsEntry Entry(string name)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        return new(FsUri.Local(path), name, IsDirectory: true, 0, null);
    }

    private static FileOperationService Operations(IRecycleBin bin, ActivityLog activity, IUsageFinder? usage) =>
        new(new ProviderRegistry([new LocalFileSystemProvider()]), new UndoStack(), activity, bin, usage: usage);

    [Fact]
    public async Task RetryAfterTheProgramLetsGoDeletes()
    {
        var bin = new BusyOnce(failures: 1);
        var activity = new ActivityLog();
        var decisions = new ScriptedDecisions(choice: "retry");

        await Operations(bin, activity, new Knows(new Holder(7, "Windows PowerShell (process 7)")))
            .DeleteAsync([Entry("for_claude")], permanent: false, decisions, CancellationToken.None);

        bin.Attempts.ShouldBe(2);
        var asked = decisions.Asked.ShouldHaveSingleItem();
        asked.Title.ShouldBe("for_claude is in use");
        asked.Severity.ShouldBe(NoticeSeverity.Warning);
        asked.Detail.ShouldBe("Finding out which program has it open…");
        (await asked.LateDetail.ShouldNotBeNull()).ShouldBe("Open in Windows PowerShell (process 7). Close it there, then Retry.");
        activity.Entries.ShouldHaveSingleItem().Description.ShouldStartWith("Sent for_claude to the Recycle Bin");
    }

    [Fact]
    public async Task CancellingLeavesItAndSaysNothingHappened()
    {
        var bin = new BusyOnce(failures: 5);
        var activity = new ActivityLog();

        await Operations(bin, activity, usage: null)
            .DeleteAsync([Entry("a"), Entry("b")], permanent: false, new ScriptedDecisions(choice: "cancel"), CancellationToken.None);

        bin.Attempts.ShouldBe(1);
        activity.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutAFinderItStillSaysWhatToDo()
    {
        var decisions = new ScriptedDecisions(choice: "cancel");
        await Operations(new BusyOnce(failures: 1), new ActivityLog(), usage: null)
            .DeleteAsync([Entry("a"), Entry("b")], permanent: false, decisions, CancellationToken.None);

        var asked = decisions.Asked.ShouldHaveSingleItem();
        asked.Title.ShouldBe("2 items are in use");
        asked.Detail.ShouldBe("Another program has it open. Close it there, then Retry.");
        asked.LateDetail.ShouldBeNull();
    }

    private string Locked(string name, out FileStream held, FileShare share = FileShare.None)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, share);
        return path;
    }

    private static FsEntry File_(string path) => new(FsUri.Local(path), Path.GetFileName(path), IsDirectory: false, 1, null);

    [Fact]
    public async Task RenamingAFileInUseAsksAndCancelLeavesIt()
    {
        var path = Locked("report.docx", out var held);
        using (held)
        {
            var decisions = new ScriptedDecisions(choice: "cancel");
            var renamed = await Operations(new BusyOnce(0), new ActivityLog(), usage: null)
                .RenameAsync(File_(path), "final.docx", CancellationToken.None, decisions);

            renamed.ShouldBeNull();
            decisions.Asked.ShouldHaveSingleItem().Title.ShouldBe("report.docx is in use");
            File.Exists(path).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task RetryRenamesOnceTheProgramLetsGo()
    {
        var path = Locked("report.docx", out var held);
        var decisions = new ReleasesThenRetries(held);

        var renamed = await Operations(new BusyOnce(0), new ActivityLog(), usage: null)
            .RenameAsync(File_(path), "final.docx", CancellationToken.None, decisions);

        renamed.ShouldNotBeNull().Name.ShouldBe("final.docx");
        File.Exists(Path.Combine(_root, "final.docx")).ShouldBeTrue();
    }

    [Fact]
    public async Task GroupingAnItemInUseAsksAndCancelLeavesWhatWentUndoable()
    {
        // The one ahead of it goes; the one in use is asked about rather than failing the lot
        // with Windows' bare "being used by another process".
        var first = Path.Combine(_root, "a.txt");
        File.WriteAllText(first, "a");
        var path = Locked("b.txt", out var held);
        using (held)
        {
            var undo = new UndoStack();
            var operations = new FileOperationService(
                new ProviderRegistry([new LocalFileSystemProvider()]), undo, new ActivityLog(), new BusyOnce(0));
            var decisions = new ScriptedDecisions(choice: "cancel");

            await operations.GroupIntoFolderAsync(
                [File_(first), File_(path)], FsUri.Local(_root), "group", CancellationToken.None, decisions);

            decisions.Asked.ShouldHaveSingleItem().Title.ShouldBe("b.txt is in use");
            File.Exists(path).ShouldBeTrue();
            File.Exists(Path.Combine(_root, "group", "a.txt")).ShouldBeTrue();

            await undo.UndoAsync(CancellationToken.None);
            File.Exists(first).ShouldBeTrue();
            Directory.Exists(Path.Combine(_root, "group")).ShouldBeFalse();
        }
    }

    /// <summary>The program closes the file while the question is up, and the user says Retry.</summary>
    private sealed class ReleasesThenRetries(FileStream held) : IDecisionHost
    {
        public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct)
        {
            held.Dispose();
            return Task.FromResult<string?>("retry");
        }

        public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) => Task.FromResult<SecretAnswer?>(null);

        public void Report(Notice notice)
        {
        }
    }

    private static TransferService Transfers(ActivityLog? activity = null) =>
        new(new ProviderRegistry([new LocalFileSystemProvider()]), new UndoStack(), activity ?? new ActivityLog(), new BusyOnce(0),
            new JobService(), NullLogger<TransferService>.Instance);

    private static async Task Finished(TransferJob job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!job.IsFinished && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        job.IsFinished.ShouldBeTrue();
    }

    [Fact]
    public async Task MovingAFileInUseNeverLeavesItInBothPlaces()
    {
        // Before: the failed rename was taken for a move across volumes, the file was copied, and
        // deleting the source failed. A copy in the target, the original in place, a failed job.
        // Readable by others but not movable, as many programs hold what they have open: with no
        // sharing at all the copy fails too, and this would pass without the fix.
        var path = Locked("report.docx", out var held, FileShare.Read);
        var target = Directory.CreateDirectory(Path.Combine(_root, "archive")).FullName;
        using (held)
        {
            var decisions = new ScriptedDecisions(choice: "skip");
            var activity = new ActivityLog();
            var job = Transfers(activity).Enqueue([FsUri.Local(path)], FsUri.Local(target), TransferMode.Move, decisions);
            await Finished(job);

            var asked = decisions.Asked.ShouldHaveSingleItem();
            asked.Title.ShouldBe("report.docx is in use");
            asked.Options.Select(o => o.Id).ShouldBe(["retry", "skip", "cancel"]);
            File.Exists(path).ShouldBeTrue();
            File.Exists(Path.Combine(target, "report.docx")).ShouldBeFalse();
            job.State.ShouldBe(JobState.Completed);

            // Not "skipped because they already existed", as it said until 2026-10-11.
            var detail = activity.Entries.ShouldHaveSingleItem().Detail;
            detail.ShouldBe("Skipped, because another program had them open: report.docx.");
        }
    }

    [Fact]
    public async Task RetryMovesItOnceTheProgramLetsGo()
    {
        var path = Locked("report.docx", out var held);
        var target = Directory.CreateDirectory(Path.Combine(_root, "archive")).FullName;

        var job = Transfers().Enqueue([FsUri.Local(path)], FsUri.Local(target), TransferMode.Move, new ReleasesThenRetries(held));
        await Finished(job);

        job.State.ShouldBe(JobState.Completed);
        File.Exists(path).ShouldBeFalse();
        File.Exists(Path.Combine(target, "report.docx")).ShouldBeTrue();
    }

    [Fact]
    public async Task AFileOpenWithoutSharingIsFoundByRestartManager()
    {
        var file = Path.Combine(_root, "report.docx");
        File.WriteAllText(file, "x");
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var holders = await new WindowsUsageFinder().FindAsync([file], CancellationToken.None);

        holders.ShouldContain(h => h.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public async Task AShellWhoseFolderItIsIsFoundByItsHandles()
    {
        // The case that prompted this: no file open at all, only a working directory, which
        // Restart Manager cannot see and Explorer cannot name.
        var folder = Directory.CreateDirectory(Path.Combine(_root, "for_claude")).FullName;
        using var shell = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            WorkingDirectory = folder,
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        try
        {
            await Task.Delay(300);
            var holders = await new WindowsUsageFinder().FindAsync([folder], CancellationToken.None);

            // The shell, and ping too: a child inherits its parent's working directory, so both
            // hold the folder and both are named.
            holders.ShouldContain(h => h.ProcessId == shell.Id && h.Description.Contains($"(process {shell.Id})"));
        }
        finally
        {
            shell.Kill(entireProcessTree: true);
            await shell.WaitForExitAsync();
        }
    }
}
