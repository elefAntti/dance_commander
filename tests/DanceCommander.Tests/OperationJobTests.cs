using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using DanceCommander.Providers.Local;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A Del is seen from the first moment and becomes a job once it runs long (2026-10-18): the strip
/// for slow work only, Stop that stops it, and Ctrl+Z meanwhile saying it is still going.
/// </summary>
public sealed class OperationJobTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dc-opjob-").FullName;

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
    public async Task QuickWork_NeverReachesTheStrip()
    {
        var jobs = new JobService();
        await OperationJob.RunAsync(jobs, new OperationJob("Sending a.txt to the Recycle Bin"), (_, _) => Task.Delay(50), CancellationToken.None);
        jobs.Jobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task SlowWork_IsInTheStripAfterASecond_AndStopStopsIt()
    {
        var jobs = new JobService();
        var job = new OperationJob("Sending orbit to the Recycle Bin");
        var running = OperationJob.RunAsync(jobs, job, (_, token) => Task.Delay(Timeout.Infinite, token), CancellationToken.None);

        await Task.Delay(OperationJob.PromoteAfter + TimeSpan.FromMilliseconds(500));
        jobs.Jobs.ShouldHaveSingleItem().ShouldBe(job);

        job.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(running);
        job.State.ShouldBe(JobState.Cancelled);
    }

    [Fact]
    public async Task TimeWaitingForAnAnswer_DoesNotCount()
    {
        var jobs = new JobService();
        var job = new OperationJob("Deleting big.iso");
        var answer = new TaskCompletionSource<string?>();
        var decisions = new WaitingDecisions(new Answering(answer.Task), job);

        var running = OperationJob.RunAsync(jobs, job, async (_, token) =>
        {
            await decisions.AskAsync(new ChoiceRequest("Delete big.iso permanently?", []), token);
        }, CancellationToken.None);

        await Task.Delay(OperationJob.PromoteAfter + TimeSpan.FromMilliseconds(500));
        jobs.Jobs.ShouldBeEmpty("a question being read was counted as slow work");
        job.IsWaiting.ShouldBeTrue();

        answer.SetResult("cancel");
        await running;
        jobs.Jobs.ShouldBeEmpty();
    }

    [Fact]
    public void CtrlZWhileAnOperationIsUnderWay_SaysSo()
    {
        var undo = new UndoStack();
        undo.Push(new DelegateUndo("Copy orbit to test", "", _ => Task.CompletedTask));

        using (undo.Working("sending orbit to the Recycle Bin"))
        {
            var busy = Should.Throw<UndoBusyException>(() => undo.UndoAsync(CancellationToken.None));
            busy.Message.ShouldBe("Still sending orbit to the Recycle Bin.");
            undo.Pending.Count.ShouldBe(1);
        }

        undo.UndoAsync(CancellationToken.None).IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task AnOperationsOwnRecordsWaitUntilItLetsGo()
    {
        // A Del records its permanent part and then its recycled part: Ctrl+Z between the two
        // reached the first, which only says it can't be undone.
        var undo = new UndoStack();
        undo.Push(new DelegateUndo("Earlier", "", _ => Task.CompletedTask));

        using (var working = undo.Working("deleting 3 items"))
        {
            working.Push(new Irreversible("Deleted big.iso permanently", "", "no way back"));
            Should.Throw<UndoBusyException>(() => undo.UndoAsync(CancellationToken.None))
                .Message.ShouldBe("Still deleting 3 items.");
            working.Push(new DelegateUndo("Sent 2 items to the Recycle Bin", "", _ => Task.CompletedTask));
        }

        undo.Pending.Select(o => o.Description)
            .ShouldBe(["Sent 2 items to the Recycle Bin", "Deleted big.iso permanently", "Earlier"]);
        (await undo.UndoAsync(CancellationToken.None))!.Description.ShouldBe("Sent 2 items to the Recycle Bin");
    }

    [Fact]
    public void EveryOperationUnderWayHoldsEveryUndoAndSaysSo()
    {
        // Letting some through beside a running job lost files in review, four ways (2026-10-30).
        var undo = new UndoStack();
        var changes = 0;
        undo.Changed += (_, _) => changes++;
        var copying = undo.Working("copying to A");
        undo.Push(new DelegateUndo("Renamed a to b", "", _ => Task.CompletedTask));
        var moving = undo.Working("moving to B");

        undo.UnderWay.ShouldBe(["moving to B", "copying to A"]);
        undo.Waits.ShouldBeTrue();
        Should.Throw<UndoBusyException>(() => undo.UndoAsync(CancellationToken.None)).Message.ShouldBe("Still moving to B.");
        moving.Dispose();
        Should.Throw<UndoBusyException>(() => undo.UndoAsync(CancellationToken.None)).Message.ShouldBe("Still copying to A.");
        copying.Dispose();

        undo.Waits.ShouldBeFalse();
        undo.UndoAsync(CancellationToken.None).IsCompletedSuccessfully.ShouldBeTrue();
        // The Activity panel and the echo hear of each start and end, not only of records.
        changes.ShouldBe(7);
    }

    [Theory]
    [InlineData("cancel", false)]
    [InlineData("delete", true)]
    public async Task RowsAreMarkedGoingOnlyOnceTheQuestionIsAnswered(string answer, bool marked)
    {
        var file = Path.Combine(_root, "gone.txt");
        File.WriteAllText(file, "x");
        var entry = new FsEntry(FsUri.Local(file), "gone.txt", false, 1, null);
        var operations = new FileOperationService(
            new ProviderRegistry([new LocalFileSystemProvider()]), new UndoStack(), new ActivityLog(), new NoRecycleBin());
        var progress = new Recording();

        await operations.DeleteAsync([entry], permanent: true, new ScriptedDecisions(choice: answer), CancellationToken.None, progress);

        (progress.Started.Count > 0).ShouldBe(marked);
        if (marked)
            progress.Started.ShouldHaveSingleItem().ShouldBe([entry.Uri]);
    }

    private sealed class Recording : IOperationProgress
    {
        public List<IReadOnlyCollection<FsUri>> Started { get; } = [];

        public void Starting(IReadOnlyCollection<FsUri> items) => Started.Add(items);

        public void Report(double? fraction, string? detail)
        {
        }
    }

    private sealed class Answering(Task<string?> answer) : IDecisionHost
    {
        public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct) => answer;

        public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) => Task.FromResult<SecretAnswer?>(null);

        public void Report(Notice notice)
        {
        }
    }
}
