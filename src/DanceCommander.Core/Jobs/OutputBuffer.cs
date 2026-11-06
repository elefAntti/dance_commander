namespace DanceCommander.Core.Jobs;

/// <summary>
/// What a running process has printed, kept as it arrives rather than collected at exit (§6.17).
/// </summary>
/// <remarks>
/// Bounded, because a build that runs for twenty minutes prints far more than anybody will scroll
/// back through, and a buffer that grew with it would be the file manager slowly running out of
/// memory on behalf of a compiler. The oldest lines go first, and the buffer says how many went —
/// the output panel prints that count, since a log whose beginning silently vanished reads as a
/// build that started half way through.
/// </remarks>
public sealed class OutputBuffer(int capacity = 4000)
{
    private readonly Queue<string> _lines = new();
    private readonly Queue<string> _errors = new();
    private readonly Lock _gate = new();
    private const int ErrorTail = 8;

    public int Capacity { get; } = capacity;

    /// <summary>Raised after every append, on the thread that appended. Throttle before painting.</summary>
    public event EventHandler? Changed;

    /// <summary>Lines dropped off the front to stay within <see cref="Capacity"/>.</summary>
    public long Dropped { get; private set; }

    public string LastLine { get; private set; } = string.Empty;

    public void Append(string line, bool isError = false)
    {
        line = TerminalText.Plain(line);
        lock (_gate)
        {
            _lines.Enqueue(line);
            if (_lines.Count > Capacity)
            {
                _lines.Dequeue();
                Dropped++;
            }

            if (isError && line.Length > 0)
            {
                _errors.Enqueue(line);
                if (_errors.Count > ErrorTail)
                    _errors.Dequeue();
            }

            if (line.Trim().Length > 0)
                LastLine = line.Trim();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Text
    {
        get
        {
            lock (_gate)
                return string.Join(Environment.NewLine, _lines);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _lines.Count;
        }
    }

    /// <summary>
    /// What to put under "failed": the process's own last words on stderr, or, when it wrote
    /// nothing there, the end of what it printed. Plenty of compilers report errors on stdout.
    /// </summary>
    public string? FailureTail()
    {
        lock (_gate)
        {
            if (_errors.Count > 0)
                return string.Join(Environment.NewLine, _errors);

            var tail = _lines.Where(l => l.Trim().Length > 0).TakeLast(ErrorTail).ToList();
            return tail.Count > 0 ? string.Join(Environment.NewLine, tail) : null;
        }
    }
}
