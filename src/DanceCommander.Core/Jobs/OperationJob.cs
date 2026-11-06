using DanceCommander.Core.Decisions;

namespace DanceCommander.Core.Jobs;

/// <summary>
/// The pane's questions, asked for a job: while one waits for an answer, the job says so and is
/// not counted as working.
/// </summary>
public sealed class WaitingDecisions(IDecisionHost inner, OperationJob job) : IDecisionHost
{
    public Task<string?> AskAsync(ChoiceRequest request, CancellationToken ct) => WhileAsking(() => inner.AskAsync(request, ct));

    public Task<string?> AskTextAsync(TextRequest request, CancellationToken ct) => WhileAsking(() => inner.AskTextAsync(request, ct));

    public Task<SecretAnswer?> AskSecretAsync(SecretRequest request, CancellationToken ct) => WhileAsking(() => inner.AskSecretAsync(request, ct));

    public void Report(Notice notice) => inner.Report(notice);

    private async Task<T> WhileAsking<T>(Func<Task<T>> ask)
    {
        job.Waiting("Waiting for your answer");
        try
        {
            return await ask().ConfigureAwait(false);
        }
        finally
        {
            job.Waiting(null);
        }
    }
}

/// <summary>
/// Work started as a command and awaited as one, which becomes a job if it is still running after
/// a moment (JOBS_PLAN.md §3.8, promotion): a Del of a large folder, the undo of a copy. Quick
/// work never reaches the strip; slow work is seen at once, with Stop.
/// </summary>
/// <remarks>
/// One second, not the plan's three (the user, 2026-10-18): a 2 s delete with nothing on screen
/// felt like nothing happening. The rows it acts on show it from the first moment, so the strip is
/// for what takes long enough to wonder about.
/// </remarks>
public sealed class OperationJob : Job
{
    public static readonly TimeSpan PromoteAfter = TimeSpan.FromSeconds(1);

    /// <summary>How long a finished one stays in the strip, as a transfer does.</summary>
    private static readonly TimeSpan StaysFor = TimeSpan.FromSeconds(4);

    private readonly string _title;
    private double _fraction;
    private bool _measured;

    private readonly bool _canCancel;

    /// <param name="canCancel">Whether the strip offers Cancel while it runs.</param>
    public OperationJob(string title, bool canCancel = true)
    {
        _title = title;
        _canCancel = canCancel;
        State = JobState.Running;
    }

    public override string Title => _title;

    public override bool CanCancel => _canCancel;

    public override double Fraction => IsFinished ? 1 : _fraction;

    public override bool IsIndeterminate => !IsFinished && !_measured;

    /// <summary>How far it is, 0..1, and what it is doing; null for "can't tell how far".</summary>
    public void Report(double? fraction, string? detail = null)
    {
        if (fraction is { } known)
        {
            _measured = true;
            _fraction = Math.Clamp(known, 0, 1);
        }
        if (detail is not null)
            Detail = detail;
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(ShowsMotion));
    }

    /// <summary>Waiting on the user (a question in the pane), or null once it works again.</summary>
    public void Waiting(string? what) => WaitingFor = what;

    /// <summary>
    /// Runs <paramref name="work"/>, adding the job to the strip once it has run for
    /// <see cref="PromoteAfter"/>. Stop in the strip cancels it, as <paramref name="ct"/> does.
    /// What it throws comes out of here as it was: the command that started it says what failed,
    /// so a failed job leaves the strip rather than say it twice.
    /// </summary>
    public static async Task RunAsync(
        IJobService jobs, OperationJob job, Func<OperationJob, CancellationToken, Task> work, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Token);
        var running = work(job, linked.Token);
        var shown = false;
        try
        {
            // Time waiting on an answer does not count: a question read for a while in the pane
            // is not slow work, and the strip saying "waiting" over it would be one thing too many.
            var worked = TimeSpan.Zero;
            while (!running.IsCompleted && !shown)
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                await Task.WhenAny(running, Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None)).ConfigureAwait(false);
                if (job.IsWaiting)
                    continue;
                worked += System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (!running.IsCompleted && worked >= PromoteAfter)
                {
                    jobs.Add(job);
                    shown = true;
                }
            }

            await running.ConfigureAwait(false);
            job.State = JobState.Completed;
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.State = JobState.Failed;
            throw;
        }
        finally
        {
            if (shown)
            {
                var delay = job.State == JobState.Completed ? StaysFor : TimeSpan.Zero;
                _ = Task.Delay(delay).ContinueWith(_ => jobs.Forget(job), TaskScheduler.Default);
            }
            else
            {
                job.Dispose();
            }
        }
    }
}
