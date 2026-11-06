using DanceCommander.Core.Fs;
using DanceCommander.Core.Jobs;

namespace DanceCommander.Core.Transfer;

public enum TransferMode
{
    Copy,
    Move,
}

/// <summary>
/// What to do when the target already exists. Resolved once per job rather than per file: a
/// question asked fifty times is a modal dialog wearing a disguise.
/// </summary>
public enum ConflictPolicy
{
    Ask,
    Overwrite,
    Skip,
    KeepBoth,
}

/// <summary>
/// A copy or a move: the first kind of job, and until builds arrived the only one (§6.17).
/// </summary>
public sealed class TransferJob : Job
{
    private long _bytesTotal;
    private long _bytesDone;
    private int _itemsTotal;
    private int _itemsDone;
    private string? _finishing;
    private string? _finishingWas;

    /// <summary>Too little to go on before this.</summary>
    private static readonly TimeSpan EstimateAfter = TimeSpan.FromSeconds(5);

    /// <summary>A copy with less than this left needs no forecast; with more, one is shown.</summary>
    private static readonly TimeSpan ShowFrom = TimeSpan.FromSeconds(15);

    /// <summary>Once shown, kept until this close to the end.</summary>
    private static readonly TimeSpan HideBelow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Nothing moving for this long is said, instead of a forecast that only grows. Not shorter:
    /// an upload to Nextcloud goes in 10 MB pieces, and on a slow link one piece is twenty seconds
    /// in which no byte is reported, though all of them are moving.
    /// </summary>
    private static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(30);

    private readonly System.Diagnostics.Stopwatch _clock = new();
    private string? _timeLeft;
    private string? _speed;
    private bool _showingLeft;

    public TransferJob(IReadOnlyList<FsUri> sources, FsUri targetDirectory, TransferMode mode)
    {
        Sources = sources;
        TargetDirectory = targetDirectory;
        Mode = mode;
    }

    public IReadOnlyList<FsUri> Sources { get; }

    public FsUri TargetDirectory { get; }

    public TransferMode Mode { get; }

    public long BytesTotal
    {
        get => _bytesTotal;
        internal set => Set(ref _bytesTotal, value);
    }

    public long BytesDone
    {
        get => _bytesDone;
        internal set
        {
            Set(ref _bytesDone, value);
            OnPropertyChanged(nameof(Fraction));
        }
    }

    public int ItemsTotal
    {
        get => _itemsTotal;
        internal set
        {
            Set(ref _itemsTotal, value);
            OnPropertyChanged(nameof(Title));
        }
    }

    public int ItemsDone
    {
        get => _itemsDone;
        internal set => Set(ref _itemsDone, value);
    }

    /// <summary>The file on its way now; what the strip shows beside the title.</summary>
    public string CurrentName
    {
        get => Detail;
        internal set => Detail = value;
    }

    /// <summary>
    /// What the job is waiting for once a file's last byte is sent: a server joining the pieces
    /// of an upload, or moving it into place. Null the rest of the time.
    /// </summary>
    /// <remarks>
    /// A bar standing at 100% says "done" while nothing visibly is, and joining a large file can
    /// take minutes. The server is working, so this moves (indeterminate) rather than standing still
    /// as a job waiting on the user does, and the line beside the title says what it is waiting for.
    /// </remarks>
    public string? Finishing
    {
        get => _finishing;
        internal set
        {
            if (_finishing == value)
                return;

            if (value is not null)
                _finishingWas = Detail;
            _finishing = value;
            Detail = value ?? _finishingWas ?? string.Empty;

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(ShowsMotion));
            OnPropertyChanged(nameof(Forecast));
        }
    }

    public override bool IsIndeterminate => _finishing is not null;

    /// <summary>How the transfer has gone so far, for the time it has left (<see cref="TransferEstimate"/>).</summary>
    internal TransferEstimate Estimate { get; } = new();

    /// <summary>Time since the transfer started running, the estimate's clock.</summary>
    internal TimeSpan Now => _clock.Elapsed;

    internal void Started() => _clock.Start();

    /// <summary>"about 2 min left", or "no progress for 40 s"; only while it is moving on its own.</summary>
    public override string? Forecast =>
        State == JobState.Running && !IsWaiting && _finishing is null ? _timeLeft : null;

    public override string? StatusTip => _speed;

    /// <summary>
    /// Decides, once a second, what the line says about time. Only an estimate worth having is
    /// shown: not in the first seconds, when there is too little to go on, and only for a copy
    /// with a while still to run, since one about to end needs no forecast. Once shown it stays
    /// until the end is close, so it does not blink on and off around the threshold. A copy that
    /// has stopped moving says that instead of a forecast that only grows.
    /// </summary>
    internal void Tick()
    {
        // On a timer thread, where anything thrown ends the process: what the strip says is not
        // worth that, so a failure here says nothing rather than taking the copies down with it.
        try
        {
            Update();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Estimating a transfer's time left failed: {ex}");
        }
    }

    private void Update()
    {
        var now = Now;
        string? said = null;

        if (State == JobState.Running && !IsWaiting && _finishing is null)
        {
            // Stillness is news only while files are being copied: listing the sources first and
            // tidying up after take their own time, and are not a stall.
            var still = Estimate.SinceProgress(now);
            if (Estimate.InFiles && still >= StalledAfter)
            {
                said = $"no progress for {Rough(still)}";
            }
            else if (now >= EstimateAfter
                     && Estimate.Left(BytesTotal - BytesDone, ItemsTotal - Estimate.FilesHandled) is { } left)
            {
                _showingLeft = left >= (_showingLeft ? HideBelow : ShowFrom);
                if (_showingLeft)
                    said = $"about {Rough(left)} left";
            }
        }

        var speed = Estimate.Speed is { } bytesPerSecond ? Rate(bytesPerSecond) : null;
        if (speed != _speed)
        {
            _speed = speed;
            OnPropertyChanged(nameof(StatusTip));
        }

        if (said != _timeLeft)
        {
            _timeLeft = said;
            OnPropertyChanged(nameof(Forecast));
        }
    }

    /// <summary>
    /// A time as a person would say it, coarse enough that the noise in an estimate does not show:
    /// 5 s steps under 20 s, then 10 s, whole minutes under an hour, then hours and 5 minutes.
    /// </summary>
    internal static string Rough(TimeSpan time)
    {
        var seconds = time.TotalSeconds;
        if (seconds < 20)
            return $"{Math.Max(5, Math.Round(seconds / 5) * 5):0} s";
        if (seconds < 59.5)
            return $"{Math.Round(seconds / 10) * 10:0} s";

        var minutes = (int)Math.Round(time.TotalMinutes);
        if (minutes < 60)
            return $"{minutes} min";

        minutes = (int)(Math.Round(time.TotalMinutes / 5) * 5);
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }

    private static string Rate(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024
            ? $"{bytesPerSecond / (1024 * 1024):0.0} MB/s"
            : $"{bytesPerSecond / 1024:0} KB/s";

    public override double Fraction => BytesTotal > 0 ? (double)BytesDone / BytesTotal : 0;

    public override string Title =>
        $"{(Mode == TransferMode.Copy ? "Copying" : "Moving")} {ItemsTotal:N0} to {TargetDirectory.Name}";

    public override string FailureHeadline =>
        $"{(Mode == TransferMode.Copy ? "Copy" : "Move")} to {TargetDirectory.Name} failed";
}
