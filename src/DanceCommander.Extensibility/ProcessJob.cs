using System.ComponentModel;
using System.Diagnostics;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;

namespace DanceCommander.Extensibility;

/// <summary>
/// A tool with <c>"launch": "job"</c>: a process that runs in the job strip, streams its output as
/// it arrives, and says when it is done (§6.17). The devcontainer build is what it is for.
/// </summary>
/// <remarks>
/// The difference from <c>capture</c> is not backgrounding — detaching a process is the easy
/// half — but streaming. Capture reads to the end and shows the lot; a build that takes minutes
/// has to be watchable while it runs, and stoppable.
/// </remarks>
public sealed class ProcessJob : Job
{
    private readonly ProcessStartInfo _start;
    private readonly string _shown;
    private readonly IActivityLog _activity;
    private readonly OutputBuffer _output = new();
    private readonly Stopwatch _clock = new();
    private long _lastDetailAt = -1;

    /// <summary>How often the strip's one line is allowed to change. A build prints far faster.</summary>
    private const long DetailIntervalMs = 200;

    public ProcessJob(string title, ProcessStartInfo start, string shown, IActivityLog activity)
    {
        Title = title;
        _start = start;
        _shown = shown;
        _activity = activity;
        _output.Changed += OnOutput;
    }

    public override string Title { get; }

    public override string FailureHeadline =>
        ExitCode is { } code ? $"{Title} failed — exit code {code}" : $"{Title} failed";

    public override bool IsIndeterminate => !IsFinished;

    public override OutputBuffer Output => _output;

    public override bool AnnouncesCompletion => true;

    /// <summary>Set once the process has exited on its own.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>The command as it would be typed, for the activity log and the output panel.</summary>
    public string CommandLine => _shown;

    public TimeSpan Elapsed => _clock.Elapsed;

    public async Task RunAsync()
    {
        State = JobState.Running;
        Detail = "Starting…";
        _clock.Start();

        try
        {
            using var process = new Process { StartInfo = _start };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                    _output.Append(line);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                    _output.Append(line, isError: true);
            };

            if (!process.Start())
                throw new InvalidOperationException($"{_start.FileName} did not start.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Stop(process);
                Finish(JobState.Cancelled, $"Stopped after {Format(Elapsed)}");
                _activity.Record($"Ran {Title}", _shown, ActivityOutcome.Failed, "Stopped before it finished.");
                return;
            }

            // The process has gone, but its last lines may still be in the pipe. The parameterless
            // wait is the one that also waits for the asynchronous readers to reach end of stream,
            // so nothing it printed on the way out is lost from the tail we show on failure.
            process.WaitForExit();
            ExitCode = process.ExitCode;

            if (process.ExitCode == 0)
            {
                Finish(JobState.Completed, $"Finished in {Format(Elapsed)}");
                _activity.Record($"Ran {Title}", _shown);
            }
            else
            {
                // The child's own words, not a summary of ours (§6.17): it knows why it failed.
                Error = _output.FailureTail() ?? "It printed nothing to say why.";
                OnPropertyChanged(nameof(FailureHeadline));
                Finish(JobState.Failed, $"Failed after {Format(Elapsed)}");
                _activity.Record(
                    $"Ran {Title}", _shown, ActivityOutcome.Failed, $"Exit code {process.ExitCode}.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // Plain language: by far the most common cause is the program not being installed.
            Error = $"{_start.FileName} could not be started. {ex.Message}";
            Finish(JobState.Failed, "Did not start");
            _activity.Record($"Ran {Title}", _shown, ActivityOutcome.Failed, ex.Message);
        }
    }

    private void Finish(JobState state, string detail)
    {
        _clock.Stop();
        Detail = detail;
        State = state;
    }

    /// <summary>
    /// The whole tree, not just the process we started. A devcontainer build is node running the
    /// docker CLI running a compiler; killing only the first leaves the rest printing to nobody.
    /// </summary>
    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone, which is what we wanted.
        }
    }

    private void OnOutput(object? sender, EventArgs e)
    {
        if (IsFinished)
            return;

        // Throttled, and on a clock rather than a count: a build prints thousands of lines a
        // second at its busiest, and each change here is a trip to the UI thread.
        var now = _clock.ElapsedMilliseconds;
        if (_lastDetailAt >= 0 && now - _lastDetailAt < DetailIntervalMs)
            return;

        _lastDetailAt = now;
        Detail = _output.LastLine;
    }

    internal static string Format(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
        : elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
        : $"{Math.Max(elapsed.TotalSeconds, 0.1):0.#}s";
}
