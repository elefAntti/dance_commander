using System.Diagnostics;
using System.Text;
using DanceCommander.Core.Commands;
using DanceCommander.Core.Decisions;
using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;
using DanceCommander.Core.Operations;
using Microsoft.Extensions.Logging;

namespace DanceCommander.Extensibility;

public interface IToolRunner
{
    Task RunAsync(ToolDefinition tool, ICommandContext context, CancellationToken ct);
}

public sealed class ToolRunner(IActivityLog activity, IJobService jobs, ILogger<ToolRunner> log) : IToolRunner
{
    /// <summary>
    /// Where a tool runs when it does not say: the folder the pane shows, or the folder a flattened
    /// tree or a search is a view of. Somewhere with no folder on this machine (a server, an
    /// archive, a page) gets the temp folder, said plainly. It used to get nothing, which made the
    /// process start wherever the application had been started from — System32, as often as not.
    /// </summary>
    public static string WorkingDirectoryFor(FsUri directory) =>
        directory.Anchor is { IsLocal: true } anchor ? anchor.ToLocalPath() : Path.GetTempPath();

    public async Task RunAsync(ToolDefinition tool, ICommandContext context, CancellationToken ct)
    {
        // Every template at once, before anything runs or asks: a tool whose working directory
        // wants ${repoRoot} must not prompt for its arguments first and then fail.
        if (ToolPlaceholders.Unresolved(tool.Templates(), context.Repository) is { } missing)
        {
            ToolPlaceholders.ReportUnresolved(missing, context.Repository, context.Decisions);
            return;
        }

        // Once per row only means something for a tool about the rows.
        var selections = tool.ForEachSelected && tool.ParsedSubject() == CommandSubject.Selection
            ? context.Selection.Select(entry => (IReadOnlyList<FsEntry>)[entry]).ToList()
            : [context.Selection];

        foreach (var selection in selections)
        {
            ct.ThrowIfCancellationRequested();

            var target = new ToolTarget(
                context.Directory,
                selection,
                selection.Count == 1 ? selection[0] : context.Focused,
                context.OtherPaneDirectory,
                context.Repository);

            // A verbatim line has been expanded already, quoted for the shell that reads it; a
            // file name containing "${" must not be expanded a second time.
            var arguments = tool.VerbatimArgs
                ? tool.Args
                : await ToolPlaceholders.ExpandAsync(tool.Args, target, context.Decisions, ct);
            if (arguments is null)
                return; // a ${prompt:…} was cancelled

            var exec = await ToolPlaceholders.ExpandOneAsync(tool.Exec, target, context.Decisions, ct: ct);
            if (string.IsNullOrWhiteSpace(exec))
                return;

            var workingDirectory = tool.WorkingDirectory is null
                ? WorkingDirectoryFor(context.Directory)
                : await ToolPlaceholders.ExpandOneAsync(tool.WorkingDirectory, target, context.Decisions, ct: ct);

            // A working folder that came out empty (${otherPaneDir} with one pane showing) is not
            // "anywhere": the process would start wherever the application did.
            if (tool.WorkingDirectory is not null && string.IsNullOrWhiteSpace(workingDirectory))
            {
                context.Decisions.Report(new Notice(
                    $"{tool.Title} was not run.",
                    NoticeSeverity.Warning,
                    $"Its working folder, {tool.WorkingDirectory}, has no value here."));
                return;
            }

            var output = tool.OutputDirectory is null
                ? null
                : await ToolPlaceholders.ExpandOneAsync(tool.OutputDirectory, target, context.Decisions, ct: ct);

            await LaunchAsync(
                tool, Environment.ExpandEnvironmentVariables(exec), arguments, workingDirectory,
                ResultLocation(output, workingDirectory), context, ct);
        }
    }

    private async Task LaunchAsync(
        ToolDefinition tool,
        string exec,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        FsUri? resultLocation,
        ICommandContext context,
        CancellationToken ct)
    {
        var launch = tool.ParsedLaunch();
        var redirected = launch is ToolLaunch.Capture or ToolLaunch.Job;

        // Without the shell, a bare "devcontainer" would only ever be looked for as an .exe. With it,
        // a bare "code" was looked for in the working folder first, which is the folder being
        // browsed: a code.cmd in an unpacked download ran on F4 (review, 2026-10-12). So the shell
        // gets a full path too, from PATH or App Paths, and the batch check below sees a .cmd shim
        // for what it is.
        exec = ExecutableSearch.Resolve(exec);

        // Found in neither: the only place the shell could still find it is that working folder.
        if (launch == ToolLaunch.Process && ExecutableSearch.IsBare(exec))
        {
            context.Decisions.Report(new Notice(
                $"{tool.Title} was not run.",
                NoticeSeverity.Warning,
                $"{exec} is not on PATH or registered in App Paths. Give the tool's exec as a full path."));
            activity.Record($"Ran {tool.Title}", exec, ActivityOutcome.Failed, "Refused: program not found.");
            return;
        }

        var commandLine = tool.VerbatimArgs ? string.Join(' ', arguments) : null;
        var shown = $"{exec} {commandLine ?? string.Join(' ', arguments.Select(Quote))}";

        if (ExecutableSearch.IsBatchFile(exec) && ExecutableSearch.UnsafeForBatch(arguments) is { } unsafeArgument)
        {
            context.Decisions.Report(new Notice(
                $"{tool.Title} was not run.",
                NoticeSeverity.Warning,
                $"{Path.GetFileName(exec)} is a batch file, and cmd.exe would read part of " +
                $"“{unsafeArgument}” as a command rather than as an argument. Rename it, or point " +
                "the tool's exec at the program the batch file wraps."));
            activity.Record(
                $"Ran {tool.Title}", shown, ActivityOutcome.Failed, "Refused: unsafe argument for a batch file.");
            return;
        }

        // A process can't start in a folder whose name ends in a dot or a space: Windows reads it
        // as the folder without, and cmd, given it behind \\?\, starts in C:\Windows instead. A
        // line meant for build. ran in build, or in System32 (review, 2026-10-28).
        if (workingDirectory is { Length: > 0 } && FsUri.HasNameWin32Trims(workingDirectory))
        {
            context.Decisions.Report(new Notice(
                $"{tool.Title} was not run.",
                NoticeSeverity.Warning,
                $"It would run in {workingDirectory}, and Windows can't start a program in a folder whose name ends " +
                "in a dot or a space: it would start in another folder. Rename the folder to run it there."));
            activity.Record($"Ran {tool.Title}", shown, ActivityOutcome.Failed, "Refused: the working folder's name ends in a dot or a space.");
            return;
        }

        var info = new ProcessStartInfo(exec)
        {
            WorkingDirectory = workingDirectory ?? string.Empty,
            // Arguments go in as an array. .NET does the Windows quoting; we never build a
            // command string, so a file name can never become part of the command (§6.6).
            UseShellExecute = launch == ToolLaunch.Process,
            // Only a console tool wants a console. A captured one has its output redirected into
            // the pane, so the black rectangle that flashed up alongside it showed nothing and
            // stole focus on the way past.
            CreateNoWindow = launch != ToolLaunch.Console,
            RedirectStandardOutput = redirected,
            RedirectStandardError = redirected,
        };

        if (redirected && tool.OutputEncoding is { Length: > 0 } encodingName)
        {
            try
            {
                var encoding = System.Text.Encoding.GetEncoding(encodingName);
                info.StandardOutputEncoding = encoding;
                info.StandardErrorEncoding = encoding;
            }
            catch (ArgumentException)
            {
                // An encoding nobody has heard of: read as before rather than not run at all.
            }
        }

        if (commandLine is not null)
            info.Arguments = commandLine;
        else
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

        if (launch == ToolLaunch.Job)
        {
            await StartJobAsync(tool, info, shown, resultLocation, context);
            return;
        }

        try
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException($"{exec} did not start.");

            if (launch != ToolLaunch.Capture)
            {
                activity.Record($"Ran {tool.Title}", shown);
                return;
            }

            var output = new StringBuilder();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            output.Append(await stdout);
            var errors = await stderr;
            if (errors.Length > 0)
                output.AppendLine().Append(errors);

            var failed = process.ExitCode != 0;
            var text = output.ToString().TrimEnd();

            if (!failed && text.Length == 0 && tool.QuietWhenSilent)
            {
                context.Output.ShowDone($"✓ {tool.Title}");
            }
            else
            {
                context.Output.ShowOutput(
                    failed ? $"{tool.Title} — exit code {process.ExitCode}" : tool.Title,
                    // Something, always. "git diff" on a file whose changes are all staged succeeds
                    // and prints nothing, and an empty panel is indistinguishable from a menu entry
                    // that does not work -- which is exactly how it was read the first time it
                    // happened. Saying "no output" answers the question the silence raises.
                    text.Length > 0 ? text : "(no output)",
                    failed);
            }

            activity.Record(
                $"Ran {tool.Title}", shown,
                failed ? ActivityOutcome.Failed : ActivityOutcome.Succeeded,
                failed ? $"Exit code {process.ExitCode}." : null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Tool {Tool} failed to start", tool.Id);

            // Plain language: the most common cause by far is the program not being installed.
            context.Decisions.Report(new Notice(
                $"{tool.Title} could not run.",
                NoticeSeverity.Error,
                $"{exec} could not be started. {ex.Message}"));

            activity.Record($"Ran {tool.Title}", shown, ActivityOutcome.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Hands the process to the job strip and returns at once (§6.17). The command is over as far
    /// as the menu is concerned; the job is what the user watches from here.
    /// </summary>
    private async Task StartJobAsync(
        ToolDefinition tool, ProcessStartInfo info, string shown, FsUri? resultLocation, ICommandContext context)
    {
        var origin = JobOrigin.Describe(
            string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, context.Repository);
        var job = new ProcessJob(tool.Title, info, shown, activity)
        {
            ResultLocation = resultLocation,
            Origin = origin?.Label,
            OriginPath = origin?.Path,
        };
        var running = Task.Run(async () =>
        {
            try
            {
                await job.RunAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // RunAsync reports its own failures; anything reaching here is a bug of ours, and
                // an unobserved one would vanish without a trace.
                log.LogError(ex, "Job {Tool} failed unexpectedly", tool.Id);
            }
        });

        // A typed line is a job from the start, so a long one never has to be started again to
        // become one; it is only shown as one if it outlasts the patience, and a quick one answers
        // in place as a captured run would.
        if (tool.BackgroundAfter is { } patience && patience > TimeSpan.Zero &&
            await Task.WhenAny(running, Task.Delay(patience)).ConfigureAwait(true) == running)
        {
            ReportFinished(tool, job, context);
            return;
        }

        jobs.Add(job);

        // Straight into the pane that started it: you asked for a build, so the first thing you
        // want to see is that it has begun and what it is saying. Not a typed line that outlasted
        // its patience: it went to the strip to stop holding the prompt, and a panel saying the
        // same as its row in the strip was the thing stated twice (2026-10-04). Its row opens it.
        if (tool.BackgroundAfter is null)
            context.Output.Follow(job);
    }

    /// <summary>A job that finished within its patience, reported the way a captured run is.</summary>
    private static void ReportFinished(ToolDefinition tool, ProcessJob job, ICommandContext context)
    {
        var text = job.Output.Text.TrimEnd();
        if (job.ExitCode is null && job.State == JobState.Failed)
        {
            context.Decisions.Report(new Notice($"{tool.Title} could not run.", NoticeSeverity.Error, job.Error));
            return;
        }

        var failed = job.State != JobState.Completed;
        if (!failed && text.Length == 0 && tool.QuietWhenSilent)
            context.Output.ShowDone($"✓ {tool.Title}");
        else
            context.Output.ShowOutput(
                failed ? $"{tool.Title} — exit code {job.ExitCode}" : tool.Title,
                text.Length > 0 ? text : "(no output)",
                failed);
    }

    /// <summary>
    /// The tool's declared output directory as a location, relative to where it ran. Local only:
    /// a build writes to disk, and that is the thing worth opening.
    /// </summary>
    private static FsUri? ResultLocation(string? expanded, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(expanded))
            return null;

        try
        {
            var path = Path.IsPathRooted(expanded) || workingDirectory is null
                ? expanded
                : Path.Combine(workingDirectory, expanded);
            return Path.IsPathRooted(path) ? FsUri.Local(Path.GetFullPath(path)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string Quote(string value) =>
        value.Length == 0 || value.Contains(' ') ? $"\"{value}\"" : value;
}
