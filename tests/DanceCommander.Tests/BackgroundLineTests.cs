using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using DanceCommander.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DanceCommander.Tests;

/// <summary>
/// A typed line is a job from the start: answered in place if it ends within its patience, moved
/// to the strip if it doesn't (COMMAND_LINE_PLAN.md F). Real processes, through cmd.exe.
/// </summary>
public class BackgroundLineTests
{
    private static (ToolRunner Runner, JobService Jobs, FakeContext Context) Setup()
    {
        var jobs = new JobService();
        return (new ToolRunner(new ActivityLog(), jobs, NullLogger<ToolRunner>.Instance), jobs,
            new FakeContext { Directory = FsUri.Local(Path.GetTempPath()) });
    }

    private static ToolDefinition Line(string line, TimeSpan patience) => new()
    {
        Id = "command-line",
        Title = line,
        Exec = "cmd.exe",
        Args = ["/c", line],
        Launch = "job",
        BackgroundAfter = patience,
        QuietWhenSilent = true,
    };

    [Fact]
    public async Task AQuickSilentLineSaysDoneAndNeverReachesTheStrip()
    {
        var (runner, jobs, context) = Setup();
        await runner.RunAsync(Line("exit 0", TimeSpan.FromSeconds(3)), context, CancellationToken.None);

        context.Done.ShouldBe(["✓ exit 0"]);
        jobs.Jobs.ShouldBeEmpty();
        context.Followed.ShouldBeEmpty();
    }

    [Fact]
    public async Task AQuickLineThatPrintsOrFailsAnswersInThePanel()
    {
        var (runner, jobs, context) = Setup();
        await runner.RunAsync(Line("echo hello", TimeSpan.FromSeconds(3)), context, CancellationToken.None);
        await runner.RunAsync(Line("exit 3", TimeSpan.FromSeconds(3)), context, CancellationToken.None);

        context.Shown.ShouldBe([("echo hello", "hello"), ("exit 3 — exit code 3", "(no output)")]);
        jobs.Jobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALongLineMovesToTheStripWithoutAPanelSayingTheSame()
    {
        var (runner, jobs, context) = Setup();
        var line = Line("ping -n 6 127.0.0.1 >nul", TimeSpan.FromMilliseconds(500));

        await runner.RunAsync(line, context, CancellationToken.None);

        // Its row in the strip says it and opens its output; a panel as well said it twice.
        var job = jobs.Jobs.ShouldHaveSingleItem();
        context.Followed.ShouldBeEmpty();
        job.IsFinished.ShouldBeFalse();
        job.Cancel();
    }

    [Fact]
    public async Task ZeroPatienceGoesToTheStripAtOnce()
    {
        var (runner, jobs, context) = Setup();
        await runner.RunAsync(Line("exit 0", TimeSpan.Zero), context, CancellationToken.None);

        jobs.Jobs.ShouldHaveSingleItem();
        context.Done.ShouldBeEmpty();
    }
}
