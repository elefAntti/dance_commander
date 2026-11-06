using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;

namespace DanceCommander.Providers.Sftp;

/// <summary>
/// A line run on a server as a job (COMMAND_LINE_PLAN.md F): its output streamed as it arrives,
/// stoppable, and reported when it ends, like a local line's <c>ProcessJob</c>.
/// </summary>
public sealed class RemoteCommandJob : Job
{
    private readonly ISftpConnections _connections;
    private readonly FsUri _directory;
    private readonly string _line;
    private readonly IActivityLog _activity;
    private readonly OutputBuffer _output = new();
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private long _lastDetailAt = -1000;

    public RemoteCommandJob(ISftpConnections connections, FsUri directory, string line, IActivityLog activity)
    {
        _connections = connections;
        _directory = directory;
        _line = line;
        _activity = activity;
        Title = line;
        // The strip's one line changes at most every 200 ms, as a local job's does: a build prints
        // far faster than anyone reads.
        _output.Changed += (_, _) =>
        {
            var now = _clock.ElapsedMilliseconds;
            if (now - _lastDetailAt < 200)
                return;
            _lastDetailAt = now;
            Detail = _output.LastLine;
        };
    }

    public override string Title { get; }

    public override string FailureHeadline =>
        ExitStatus is { } status ? $"{Title} failed — exit status {status}" : $"{Title} failed";

    public override bool IsIndeterminate => !IsFinished;

    public override OutputBuffer Output => _output;

    public override bool AnnouncesCompletion => true;

    /// <summary>The command's exit status once it has ended on its own.</summary>
    public int? ExitStatus { get; private set; }

    public async Task RunAsync()
    {
        State = JobState.Running;
        Detail = $"Running on {_directory.Authority}…";
        _clock.Start();
        var shown = $"ssh {_directory.Authority} {_line}";

        // Held for as long as it runs: it uses one session throughout, so it never looked busy, and
        // the idle reaper closed the connection under a line that had run for five minutes.
        using var hold = _connections.Hold(SftpTarget.From(_directory));
        var started = false;

        try
        {
            var shell = await _connections.GetShellAsync(SftpTarget.From(_directory), Token).ConfigureAwait(false);
            using var command = shell.CreateCommand(SftpCommandService.Script(_directory.Path, _line));

            // Read while it runs, both streams, so a long line shows what it is saying.
            var running = command.ExecuteAsync(Token);
            started = true;
            var stdout = PumpAsync(command.OutputStream, isError: false);
            var stderr = PumpAsync(command.ExtendedOutputStream, isError: true);

            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Finish(JobState.Cancelled, $"Stopped after {Format(_clock.Elapsed)}");
                _activity.Record($"Ran on {_directory.Authority}: {_line}", shown, ActivityOutcome.Failed, "Stopped before it finished.");
                return;
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            ExitStatus = command.ExitStatus;

            if (command.ExitStatus == 0)
            {
                Finish(JobState.Completed, $"Finished in {Format(_clock.Elapsed)}");
                _activity.Record($"Ran on {_directory.Authority}: {_line}", shown);
            }
            else
            {
                Error = _output.FailureTail() ?? "It printed nothing to say why.";
                OnPropertyChanged(nameof(FailureHeadline));
                Finish(JobState.Failed, $"Failed after {Format(_clock.Elapsed)}");
                _activity.Record($"Ran on {_directory.Authority}: {_line}", shown, ActivityOutcome.Failed,
                    $"Exit status {command.ExitStatus}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Once it has started, "did not run" invited running again a line that may have done
            // half its work (a move, an rsync --remove-source-files) before the connection went.
            Error = started
                ? $"{SftpFileSystemProvider.Translate(ex, _directory).Message} It had started, and may have done part of its work."
                : SftpFileSystemProvider.Translate(ex, _directory).Message;
            Finish(JobState.Failed, started ? $"Cut off after {Format(_clock.Elapsed)}" : "Did not run");
            _activity.Record($"Ran on {_directory.Authority}: {_line}", shown, ActivityOutcome.Failed, Error);
        }
        catch (OperationCanceledException)
        {
            Finish(JobState.Cancelled, "Stopped before it started");
        }
    }

    private async Task PumpAsync(Stream stream, bool isError)
    {
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            _output.Append(line, isError);
    }

    private void Finish(JobState state, string detail)
    {
        _clock.Stop();
        Detail = detail;
        State = state;
    }

    private static string Format(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s" : $"{elapsed.TotalSeconds:0.0} s";
}
