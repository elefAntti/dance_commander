namespace DanceCommander.Core.Transfer;

/// <summary>
/// How long a transfer has left, from how it has gone so far.
/// </summary>
/// <remarks>
/// A copy's time is two kinds of time that do not overlap. Moving time is while a file's bytes
/// flow; the rest is what each file costs beyond its bytes: opening the source, creating the
/// target, the wait for the first byte, the commit at the end (a server joining an upload's pieces
/// included). Measured apart, they add up without counting anything twice:
///
/// <c>left = bytes left ÷ speed + files left × cost per file</c>
///
/// One large file is nearly all the first term and ten thousand small ones nearly all the second,
/// which is where an estimate from bytes alone goes wrong. Speed is over the last
/// <see cref="SpeedWindow"/> of moving time, so it follows a real change without twitching at
/// every one; cost per file is over the last <see cref="FilesWindow"/> files, so a run of large
/// files does not make the small ones before it forgotten.
///
/// Time spent waiting for the user's answer is neither: a file that was asked about is not
/// counted, and the moving clock restarts after.
/// </remarks>
public sealed class TransferEstimate
{
    public static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(20);
    public const int FilesWindow = 20;

    private readonly object _gate = new();
    private readonly Queue<(TimeSpan Took, long Bytes)> _flow = new();
    private readonly Queue<(TimeSpan Cost, long Bytes)> _perFile = new();
    private TimeSpan _flowTime;
    private long _flowBytes;

    private TimeSpan? _fileStarted;
    private TimeSpan? _lastBytes;
    private TimeSpan _fileMoving;
    private long _fileUntimed;
    private bool _fileInterrupted;
    private TimeSpan _lastProgress;
    private bool _inFiles;

    /// <summary>The longest an estimate says; a crawl of a few bytes a second would otherwise overflow.</summary>
    private static readonly TimeSpan Longest = TimeSpan.FromDays(99);

    /// <summary>Files finished, whatever became of them: copied, skipped, the same file already.</summary>
    public int FilesHandled { get; private set; }

    /// <summary>
    /// Between the first file's start and the last file's end: the stretch where nothing moving
    /// means something. Listing the sources before, and tidying up after, are not files.
    /// </summary>
    public bool InFiles
    {
        get
        {
            lock (_gate)
                return _inFiles;
        }
    }

    /// <summary>Bytes per second over the recent moving time; null until there has been some.</summary>
    public double? Speed
    {
        get
        {
            lock (_gate)
                return _flowTime > TimeSpan.Zero ? _flowBytes / _flowTime.TotalSeconds : null;
        }
    }

    /// <summary>The next file begins, which ends the one before if it was still open.</summary>
    public void FileStarted(TimeSpan now)
    {
        lock (_gate)
        {
            FinishOpenFile(now);
            _fileStarted = now;
            _lastBytes = null;
            _fileMoving = TimeSpan.Zero;
            _fileUntimed = 0;
            _fileInterrupted = false;
            _lastProgress = now;
            _inFiles = true;
        }
    }

    /// <summary>
    /// A piece of the current file has been written. The time since the piece before is moving
    /// time; the first piece of a file has no "before", so its time stays the file's own cost.
    /// </summary>
    public void Moved(long bytes, TimeSpan now)
    {
        lock (_gate)
        {
            if (_lastBytes is { } previous && now > previous)
            {
                var took = now - previous;
                _fileMoving += took;
                _flow.Enqueue((took, bytes));
                _flowTime += took;
                _flowBytes += bytes;

                while (_flow.Count > 1 && _flowTime - _flow.Peek().Took >= SpeedWindow)
                {
                    var (old, oldBytes) = _flow.Dequeue();
                    _flowTime -= old;
                    _flowBytes -= oldBytes;
                }
            }
            else
            {
                // No interval to time it by: these bytes' time is in the file's cost, and the
                // estimate must not count them again as bytes left at the speed.
                _fileUntimed += bytes;
            }

            _lastBytes = now;
            _lastProgress = now;
        }
    }

    /// <summary>The user is being asked something: that wait is no file's cost and no flow.</summary>
    public void Interrupted()
    {
        lock (_gate)
        {
            _fileInterrupted = true;
            _lastBytes = null;
        }
    }

    /// <summary>Something happened that is neither a file nor a byte: a folder made on the way.</summary>
    public void Progressed(TimeSpan now)
    {
        lock (_gate)
            _lastProgress = now;
    }

    /// <summary>The user answered: progress is measured from here, not from before the question.</summary>
    public void Resumed(TimeSpan now)
    {
        lock (_gate)
            _lastProgress = now;
    }

    /// <summary>The last file is done.</summary>
    public void Finished(TimeSpan now)
    {
        lock (_gate)
        {
            FinishOpenFile(now);
            _inFiles = false;
        }
    }

    /// <summary>How long since a file started, a piece arrived, or the user answered.</summary>
    public TimeSpan SinceProgress(TimeSpan now)
    {
        lock (_gate)
            return now - _lastProgress;
    }

    /// <summary>The time left for so much more, or null while there is nothing to go on.</summary>
    public TimeSpan? Left(long bytesLeft, int filesLeft)
    {
        lock (_gate)
        {
            double? perFile = _perFile.Count > 0 ? _perFile.Average(f => f.Cost.TotalSeconds) : null;
            double? speed = _flowTime > TimeSpan.Zero ? _flowBytes / _flowTime.TotalSeconds : null;

            if (speed is null && perFile is null)
                return null;

            // Each file's first piece (all of a small file) has no interval to time it by, so its
            // time is in the file's cost: those bytes come off the bytes left, or they are counted
            // twice. With no speed yet, the files alone are the estimate.
            var untimedPerFile = _perFile.Count > 0 ? _perFile.Average(f => (double)f.Bytes) : 0;
            var timedLeft = Math.Max(0, bytesLeft - Math.Max(0, filesLeft) * untimedPerFile);

            var seconds = (speed is { } s && s > 0 ? timedLeft / s : 0) + Math.Max(0, filesLeft) * (perFile ?? 0);
            return TimeSpan.FromSeconds(Math.Min(seconds, Longest.TotalSeconds));
        }
    }

    private void FinishOpenFile(TimeSpan now)
    {
        if (_fileStarted is not { } started)
            return;

        FilesHandled++;
        if (!_fileInterrupted)
        {
            var cost = now - started - _fileMoving;
            _perFile.Enqueue((cost < TimeSpan.Zero ? TimeSpan.Zero : cost, _fileUntimed));
            if (_perFile.Count > FilesWindow)
                _perFile.Dequeue();
        }

        _fileStarted = null;
    }
}
