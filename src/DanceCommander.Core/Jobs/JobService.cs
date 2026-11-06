using System.ComponentModel;

namespace DanceCommander.Core.Jobs;

/// <summary>
/// Everything the job strip shows. Transfers and builds both land here, which is what lets one
/// strip serve both without claiming a sixth chrome region (§1.3, §6.17).
/// </summary>
public interface IJobService
{
    IReadOnlyList<Job> Jobs { get; }

    /// <summary>A job was added or forgotten. Raised on whichever thread did it.</summary>
    event EventHandler? JobsChanged;

    /// <summary>Raised once per job, when it completes, fails or is cancelled.</summary>
    event EventHandler<Job>? JobFinished;

    void Add(Job job);

    void Forget(Job job);
}

public sealed class JobService : IJobService
{
    private readonly List<Job> _jobs = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<Job> Jobs
    {
        get
        {
            lock (_gate)
                return _jobs.ToList();
        }
    }

    public event EventHandler? JobsChanged;

    public event EventHandler<Job>? JobFinished;

    public void Add(Job job)
    {
        lock (_gate)
            _jobs.Add(job);

        job.PropertyChanged += OnJobChanged;
        JobsChanged?.Invoke(this, EventArgs.Empty);

        // Finished before anybody was listening, e.g. a process that could not start at all.
        if (job.IsFinished)
            Finish(job);
    }

    public void Forget(Job job)
    {
        bool removed;
        lock (_gate)
            removed = _jobs.Remove(job);
        if (!removed)
            return;

        job.PropertyChanged -= OnJobChanged;
        job.Dispose();
        JobsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Job.State) && sender is Job { IsFinished: true } job)
            Finish(job);
    }

    private void Finish(Job job)
    {
        // State only moves forward, so this runs once — but unsubscribing makes that a guarantee
        // rather than an assumption about every job that will ever be written.
        job.PropertyChanged -= OnJobChanged;
        JobFinished?.Invoke(this, job);
    }
}
