using System.ComponentModel;
using System.Runtime.CompilerServices;
using DanceCommander.Core.Fs;

namespace DanceCommander.Core.Jobs;

public enum JobState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Something running in the background that the window reports on: a transfer, a build. One row
/// in the job strip (§6.17).
/// </summary>
/// <remarks>
/// Broader than a reified operation (§1.2/6), and deliberately separate from one: a build has no
/// undo and no invertible shell equivalent. A job is the surface that owns progress, cancellation
/// and completion; an operation is one of the things that can run as one.
/// </remarks>
public abstract class Job : INotifyPropertyChanged
{
    private readonly CancellationTokenSource _cts = new();

    private JobState _state = JobState.Pending;
    private string _detail = string.Empty;
    private string? _error;
    private string? _waitingFor;

    public CancellationToken Token => _cts.Token;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What the strip calls it: "Copying 3 to Docs", "Build in devcontainer".</summary>
    public abstract string Title { get; }

    /// <summary>
    /// Where it was started from, when that is not obvious from the title: the worktree and branch
    /// a build runs in. Several checkouts of one repository build with the same command, and a strip
    /// saying only "make release" does not say which of them is building.
    /// </summary>
    public string? Origin { get; init; }

    /// <summary>The full path behind <see cref="Origin"/>, for its tooltip.</summary>
    public string? OriginPath { get; init; }

    /// <summary>
    /// <see cref="Origin"/> as the strip writes it after the title. Composed here because a binding's
    /// StringFormat is applied to a null too, which left every transfer ending in a stray " · ".
    /// </summary>
    public string? OriginSuffix => Origin is null ? null : $" · {Origin}";

    /// <summary>Names the operation that failed, so the error is not a sentence without a subject.</summary>
    public virtual string FailureHeadline => $"{Title} failed";

    public JobState State
    {
        get => _state;
        protected internal set
        {
            if (_state == value)
                return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(HasAction));
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(ShowsMotion));
            OnPropertyChanged(nameof(Forecast));
        }
    }

    /// <summary>
    /// What the job is waiting on when it is not working: your answer to a question in the pane.
    /// Null while it is working, or has nothing to say about why it is not.
    /// </summary>
    public string? WaitingFor
    {
        get => _waitingFor;
        protected internal set
        {
            if (_waitingFor == value)
                return;
            _waitingFor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(ShowsMotion));
            OnPropertyChanged(nameof(Forecast));
        }
    }

    /// <summary>
    /// Not working, but not finished: queued, or suspended on a decision. It must not look like
    /// working, or there is no telling whether the computer is doing anything at all.
    /// </summary>
    public bool IsWaiting => State == JobState.Pending || WaitingFor is not null;

    /// <summary>Only work in progress moves; waiting is still.</summary>
    public bool ShowsMotion => IsIndeterminate && !IsWaiting;

    /// <summary>The line beside the title: why it is waiting if it is, otherwise what it is doing.</summary>
    public string Status => WaitingFor ?? (State == JobState.Pending ? "Waiting to start" : Detail);

    /// <summary>
    /// What is still to come, after the status: a transfer's time left. In a column of its own,
    /// so a long file name in the status trims away before it does. Null for none.
    /// </summary>
    public virtual string? Forecast => null;

    /// <summary>More about the forecast, for its tooltip: a transfer's speed. Null for none.</summary>
    public virtual string? StatusTip => null;

    /// <summary>The one line beside the title: the file being copied, the line just printed.</summary>
    public string Detail
    {
        get => _detail;
        protected internal set
        {
            Set(ref _detail, value);
            OnPropertyChanged(nameof(Status));
        }
    }

    public string? Error
    {
        get => _error;
        protected internal set => Set(ref _error, value);
    }

    /// <summary>0..1. Meaningless while <see cref="IsIndeterminate"/>.</summary>
    public virtual double Fraction => IsFinished ? 1 : 0;

    /// <summary>
    /// True for work with no total to divide by. A build does not know how far along it is, and a
    /// bar that pretends to is worse than one that says only that something is happening.
    /// </summary>
    public virtual bool IsIndeterminate => false;

    /// <summary>What the job printed, for jobs that print. Null for a transfer.</summary>
    public virtual OutputBuffer? Output => null;

    /// <summary>
    /// Where the job's result lands, when it declared one. A build produces files, and the ending a
    /// file manager can offer that a toast cannot is to go and look at them.
    /// </summary>
    public FsUri? ResultLocation { get; init; }

    /// <summary>Offer to show the result: it finished, and said where to look.</summary>
    public bool HasResult => State == JobState.Completed && ResultLocation is not null;

    /// <summary>
    /// Whether finishing is news. A build measured in minutes is something you went and did
    /// something else during; a four-second copy is not worth interrupting anybody for.
    /// </summary>
    public virtual bool AnnouncesCompletion => false;

    /// <summary>
    /// Forgotten by the job service, and on its way out of the strip. The window keeps it for the
    /// length of a fade, so a finished row settles out of sight rather than blinking away.
    /// </summary>
    public bool IsLeaving
    {
        get => _isLeaving;
        set => Set(ref _isLeaving, value);
    }

    private bool _isLeaving;

    /// <summary>
    /// Running, but not in the strip yet: a job that is over in a moment is not shown at all, only
    /// recorded (§1.3, "Completion is boring"). The window still counts it as running everywhere
    /// else: closing, signing out, keeping the machine awake.
    /// </summary>
    public bool IsHeld
    {
        get => _isHeld;
        set => Set(ref _isHeld, value);
    }

    private bool _isHeld;

    /// <summary>The button stops offering to cancel something that has already stopped.</summary>
    public string ActionLabel => IsFinished ? "Dismiss" : "Cancel";

    /// <summary>
    /// Whether Cancel is offered while it runs. Not for an undo: stopped part way, it has already
    /// left the undo stack, and what it had put back would be on record nowhere (review, 2026-10-18).
    /// </summary>
    public virtual bool CanCancel => true;

    /// <summary>The strip's button: Dismiss once it is over, Cancel while it runs if it can be.</summary>
    public bool HasAction => IsFinished || CanCancel;

    public bool IsFinished => State is JobState.Completed or JobState.Failed or JobState.Cancelled;

    public void Cancel()
    {
        if (!IsFinished)
            _cts.Cancel();
    }

    internal void Dispose() => _cts.Dispose();

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
