using System.Diagnostics;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using DanceCommander.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

public class OutputBufferTests
{
    [Fact]
    public void KeepsLinesInOrder()
    {
        var buffer = new OutputBuffer();
        buffer.Append("one");
        buffer.Append("two");

        buffer.Text.ShouldBe($"one{Environment.NewLine}two");
        buffer.LastLine.ShouldBe("two");
    }

    [Fact]
    public void DropsTheOldestLinesAndCountsThem()
    {
        // A twenty-minute build must not become the file manager running out of memory.
        var buffer = new OutputBuffer(capacity: 3);
        for (var i = 1; i <= 5; i++)
            buffer.Append($"line {i}");

        buffer.Count.ShouldBe(3);
        buffer.Dropped.ShouldBe(2);
        buffer.Text.ShouldStartWith("line 3");
    }

    [Fact]
    public void BlankLinesDoNotBecomeTheLastLine()
    {
        // The strip shows the last line; a build that ends with a blank one should not go quiet.
        var buffer = new OutputBuffer();
        buffer.Append("Compiling app.cs");
        buffer.Append("   ");

        buffer.LastLine.ShouldBe("Compiling app.cs");
    }

    [Fact]
    public void AFailureIsExplainedByStderrWhenThereIsAny()
    {
        var buffer = new OutputBuffer();
        buffer.Append("Compiling...");
        buffer.Append("error: no such file", isError: true);
        buffer.Append("Done.");

        buffer.FailureTail().ShouldBe("error: no such file");
    }

    [Fact]
    public void AFailureFallsBackToTheEndOfStdout()
    {
        // Plenty of compilers — dotnet build among them — report their errors on stdout.
        var buffer = new OutputBuffer();
        for (var i = 1; i <= 20; i++)
            buffer.Append($"line {i}");

        var tail = buffer.FailureTail().ShouldNotBeNull();
        tail.ShouldEndWith("line 20");
        tail.ShouldNotContain("line 1" + Environment.NewLine);
    }
}

public class JobServiceTests
{
    private sealed class ManualJob : Job
    {
        public override string Title => "manual";

        public void MoveTo(JobState state) => State = state;
    }

    [Fact]
    public void AnnouncesEachJobFinishingExactlyOnce()
    {
        var service = new JobService();
        var finished = new List<Job>();
        service.JobFinished += (_, job) => finished.Add(job);

        var job = new ManualJob();
        service.Add(job);
        job.MoveTo(JobState.Running);
        finished.ShouldBeEmpty();

        job.MoveTo(JobState.Failed);
        job.MoveTo(JobState.Completed); // nothing should, but if it did, it is not news twice

        finished.ShouldHaveSingleItem().ShouldBeSameAs(job);
    }

    [Fact]
    public void AJobThatFinishedBeforeBeingAddedIsStillAnnounced()
    {
        var service = new JobService();
        var finished = 0;
        service.JobFinished += (_, _) => finished++;

        var job = new ManualJob();
        job.MoveTo(JobState.Failed);
        service.Add(job);

        finished.ShouldBe(1);
    }

    [Fact]
    public void ForgettingRemovesIt()
    {
        var service = new JobService();
        var changes = 0;
        service.JobsChanged += (_, _) => changes++;

        var job = new ManualJob();
        service.Add(job);
        service.Forget(job);
        service.Forget(job); // twice is harmless

        service.Jobs.ShouldBeEmpty();
        changes.ShouldBe(2);
    }
}

/// <summary>Against real processes, because streaming and stopping them is the whole point.</summary>
public class ProcessJobTests
{
    private static ProcessJob Job(string command, ActivityLog? activity = null)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(command);
        return new ProcessJob("Test job", start, $"cmd /c {command}", activity ?? new ActivityLog());
    }

    [Fact]
    public async Task StreamsOutputAndCompletes()
    {
        var activity = new ActivityLog();
        var job = Job("echo first&& echo second", activity);

        await job.RunAsync();

        job.State.ShouldBe(JobState.Completed);
        job.ExitCode.ShouldBe(0);
        job.Output.Text.ShouldContain("first");
        job.Output.Text.ShouldContain("second");
        job.Detail.ShouldStartWith("Finished in");
        job.IsIndeterminate.ShouldBeFalse();
        activity.Entries.ShouldHaveSingleItem().Outcome.ShouldBe(ActivityOutcome.Succeeded);
    }

    [Fact]
    public async Task AFailureCarriesTheChildsOwnWords()
    {
        var job = Job("echo it went wrong 1>&2 && exit /b 3");

        await job.RunAsync();

        job.State.ShouldBe(JobState.Failed);
        job.ExitCode.ShouldBe(3);
        job.Error.ShouldNotBeNull().ShouldContain("it went wrong");
        job.FailureHeadline.ShouldBe("Test job failed — exit code 3");
    }

    [Fact]
    public async Task StoppingKillsTheWholeTree()
    {
        // cmd running ping: stopping has to reach the ping, not just the cmd that started it.
        var job = Job("ping -n 60 127.0.0.1");
        var running = job.RunAsync();

        await WaitUntil(() => job.Output.Count > 0);
        job.Cancel();

        (await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(15)))).ShouldBeSameAs(running);
        job.State.ShouldBe(JobState.Cancelled);
        job.Detail.ShouldStartWith("Stopped after");
    }

    [Fact]
    public async Task AProgramThatIsNotThereFailsInPlainWords()
    {
        var start = new ProcessStartInfo(@"C:\definitely\not\here.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var job = new ProcessJob("Missing", start, "here.exe", new ActivityLog());

        await job.RunAsync();

        job.State.ShouldBe(JobState.Failed);
        job.Error.ShouldNotBeNull().ShouldContain("could not be started");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        condition().ShouldBeTrue();
    }
}

public sealed class ExecutableSearchTests : IDisposable
{
    private readonly string _bin = Directory.CreateTempSubdirectory("dc-path-").FullName;
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        Directory.Delete(_bin, recursive: true);
    }

    [Fact]
    public void FindsACmdShimTheWayAPromptWould()
    {
        // devcontainer, from npm, is a .cmd; without the shell only .exe would ever be tried.
        var shim = Path.Combine(_bin, "devcontainer.cmd");
        File.WriteAllText(shim, "@echo off");
        Environment.SetEnvironmentVariable("PATH", _bin + ";" + _path);

        ExecutableSearch.Resolve("devcontainer").ShouldBe(shim);
    }

    [Fact]
    public void PrefersAnExeOverACmdInTheSameFolder()
    {
        File.WriteAllText(Path.Combine(_bin, "tool.cmd"), "@echo off");
        File.WriteAllText(Path.Combine(_bin, "tool.exe"), string.Empty);
        Environment.SetEnvironmentVariable("PATH", _bin + ";" + _path);

        ExecutableSearch.Resolve("tool").ShouldEndWith("tool.exe");
    }

    [Fact]
    public void FindsANameThatAlreadyHasAnExtension()
    {
        // Left bare, the shell would look in the folder being browsed before PATH.
        var exe = Path.Combine(_bin, "editor.exe");
        File.WriteAllText(exe, string.Empty);
        Environment.SetEnvironmentVariable("PATH", _bin + ";" + _path);

        ExecutableSearch.Resolve("editor.exe").ShouldBe(exe);
        ExecutableSearch.IsBare(exe).ShouldBeFalse();
    }

    [Theory]
    [InlineData(@"C:\tools\thing")]
    [InlineData("nothing-by-this-name-anywhere.exe")]
    [InlineData("nothing-by-this-name-anywhere")]
    public void LeavesAloneWhatItCannotFind(string exec) =>
        ExecutableSearch.Resolve(exec).ShouldBe(exec);

    [Fact]
    public void SaysWhenANameWasFoundNowhere()
    {
        ExecutableSearch.IsBare("nothing-by-this-name-anywhere").ShouldBeTrue();
        ExecutableSearch.IsBare(@"C:\tools\thing.exe").ShouldBeFalse();
    }

    [Fact]
    public void RefusesAnArgumentCmdWouldReadAsACommand()
    {
        ExecutableSearch.UnsafeForBatch(["exec", @"C:\src\app"]).ShouldBeNull();
        ExecutableSearch.UnsafeForBatch(["exec", @"C:\src\R&D"]).ShouldBe(@"C:\src\R&D");
        ExecutableSearch.UnsafeForBatch(["100%"]).ShouldBe("100%");
    }
}

/// <summary>The runner end to end: a job launch lands in the strip and in the pane.</summary>
public class ToolRunnerJobTests
{
    private static readonly string Temp = Path.GetTempPath().TrimEnd('\\');

    [Fact]
    public async Task AJobToolRunsInTheStripAndIsFollowedInThePane()
    {
        var jobs = new JobService();
        var finished = new TaskCompletionSource<Job>();
        jobs.JobFinished += (_, job) => finished.TrySetResult(job);

        var runner = new ToolRunner(new ActivityLog(), jobs, NullLogger<ToolRunner>.Instance);
        var context = new FakeContext
        {
            Directory = FsUri.Local(Temp),
            Repository = new RepositoryInfo(FsUri.Local(Temp), "main", null),
        };

        var tool = new ToolDefinition
        {
            Id = "build",
            Title = "Build",
            Exec = "cmd.exe",
            Args = ["/c", "echo building ${branch}"],
            Launch = "job",
            OutputDirectory = "${repoRoot}",
        };

        await runner.RunAsync(tool, context, CancellationToken.None);

        // The command returns at once; the job is what carries on.
        var job = jobs.Jobs.ShouldHaveSingleItem();
        context.Followed.ShouldHaveSingleItem().ShouldBeSameAs(job);

        (await finished.Task.WaitAsync(TimeSpan.FromSeconds(15))).State.ShouldBe(JobState.Completed);
        job.Output!.Text.ShouldContain("building main");
        job.ResultLocation.ShouldBe(FsUri.Local(Temp));
        job.HasResult.ShouldBeTrue();
    }

    [Fact]
    public async Task AJobWhoseLocationTokenHasNoValueNeverStarts()
    {
        var jobs = new JobService();
        var runner = new ToolRunner(new ActivityLog(), jobs, NullLogger<ToolRunner>.Instance);
        var context = new FakeContext { Directory = FsUri.Local(Temp) };

        await runner.RunAsync(
            new ToolDefinition { Id = "b", Title = "B", Exec = "cmd.exe", Args = ["/c", "echo"], Launch = "job", WorkingDirectory = "${repoRoot}" },
            context, CancellationToken.None);

        jobs.Jobs.ShouldBeEmpty();
        context.Scripted.Notices.ShouldHaveSingleItem();
    }
}

public class JobOriginTests
{
    private static readonly RepositoryInfo Worktree =
        new(FsUri.Local(@"C:\work\orbit-worktree-2"), "ORB-11026-new-parser", null);

    [Fact]
    public void InsideAWorkTreeItIsTheWorkTreeAndItsBranch()
    {
        var origin = JobOrigin.Describe(@"C:\work\orbit-worktree-2\src\", Worktree);

        // The folder alone repeats across checkouts, the branch alone can too; both together do not.
        origin!.Value.Label.ShouldBe("orbit-worktree-2 · ORB-11026-new-parser");
        origin.Value.Path.ShouldBe(@"C:\work\orbit-worktree-2\src");
    }

    [Fact]
    public void ADetachedHeadIsNamedByItsFolderAlone() =>
        JobOrigin.Describe(@"C:\work\orbit-worktree-2", Worktree with { Branch = null })!
            .Value.Label.ShouldBe("orbit-worktree-2");

    [Fact]
    public void OutsideTheRepositoryItIsTheFolder()
    {
        // A sibling whose name starts with the root's is not inside it.
        JobOrigin.Describe(@"C:\work\orbit-worktree-20", Worktree)!.Value.Label.ShouldBe("orbit-worktree-20");
        JobOrigin.Describe(@"C:\Temp\build", null)!.Value.Label.ShouldBe("build");
    }

    [Fact]
    public void NoFolderNoOrigin() => JobOrigin.Describe(null, Worktree).ShouldBeNull();
}

public class TransferJobFinishingTests
{
    [Fact]
    public void FinishingMovesTheBarAndSaysWhatItWaitsFor()
    {
        // The upload's last byte has gone and the server is joining it: not 100% standing still.
        var job = new DanceCommander.Core.Transfer.TransferJob(
            [FsUri.Parse("davs://hub.example.com/a.iso")], FsUri.Parse("davs://hub.example.com/"),
            DanceCommander.Core.Transfer.TransferMode.Copy) { CurrentName = "a.iso" };
        job.State = JobState.Running;

        job.Finishing = "Waiting for hub.example.com to finish a.iso";
        job.IsIndeterminate.ShouldBeTrue();
        job.ShowsMotion.ShouldBeTrue();
        job.Status.ShouldBe("Waiting for hub.example.com to finish a.iso");

        job.Finishing = null;
        job.IsIndeterminate.ShouldBeFalse();
        job.Status.ShouldBe("a.iso");
    }
}
