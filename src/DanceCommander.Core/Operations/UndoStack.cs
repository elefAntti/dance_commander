namespace DanceCommander.Core.Operations;

/// <summary>
/// A completed mutation, reified (§1.2/6). The same object carries what to tell the user, how to
/// put it back, and the shell command that would have done the same thing — so undo and the
/// activity log are two readings of one record rather than two parallel implementations.
/// </summary>
public interface IUndoableOperation
{
    /// <summary>Human description, e.g. "Move 3 items to src\old".</summary>
    string Description { get; }

    /// <summary>What this would have looked like typed into a shell.</summary>
    string ShellEquivalent { get; }

    Task UndoAsync(CancellationToken ct);
}

public interface IUndoStack
{
    bool CanUndo { get; }

    string? NextDescription { get; }

    /// <summary>What is on the stack, the next one Ctrl+Z undoes first.</summary>
    IReadOnlyList<IUndoableOperation> Pending { get; }

    /// <summary>The undo still running, if one is: the next waits for it rather than starting beside it.</summary>
    IUndoableOperation? Running { get; }

    /// <summary>Operations under way, newest first ("copying to backup"): Ctrl+Z waits for them.</summary>
    IReadOnlyList<string> UnderWay { get; }

    /// <summary>Whether Ctrl+Z now would wait, for an undo or an operation still under way.</summary>
    bool Waits { get; }

    /// <summary>
    /// An operation under way, not on the stack until it ends ("sending orbit to the Recycle
    /// Bin"): Ctrl+Z meanwhile says so rather than undo the one before it beside it (2026-10-18).
    /// What it did is recorded through the slot, before it lets go. Until the slot is disposed.
    /// </summary>
    /// <remarks>
    /// Letting Ctrl+Z through beside a running operation was tried on 2026-10-30, first for
    /// whatever was done after it began, then only for work on this computer beside uploads, and
    /// reviewed four times: each found a way to lose a file (AUDIT.md §2, 62 and 63; an upload
    /// to this computer under another name). Every undo waits.
    /// </remarks>
    IUndoSlot Working(string doing);

    event EventHandler? Changed;

    /// <summary>Raised after an operation lands on the stack, with that operation.</summary>
    event EventHandler<IUndoableOperation>? Pushed;

    void Push(IUndoableOperation operation);

    /// <summary>
    /// Undoes the most recent operation and returns it, or null if there was none. Throws
    /// <see cref="UndoBusyException"/> while another undo, or an operation, is still running.
    /// </summary>
    Task<IUndoableOperation?> UndoAsync(CancellationToken ct);
}

/// <summary>An operation's hold on Ctrl+Z while it runs (<see cref="IUndoStack.Working"/>).</summary>
public interface IUndoSlot : IDisposable
{
    /// <summary>Records what the operation did, on top, as any push does.</summary>
    void Push(IUndoableOperation operation);
}

/// <summary>
/// Ctrl+Z while an undo is still running. Two at once let a long one (a copy of 85,000 files)
/// carry on unseen while the next was undone and reported beside it (2026-10-17).
/// </summary>
public sealed class UndoBusyException(string message) : InvalidOperationException(message)
{
    public UndoBusyException(IUndoableOperation running)
        : this($"Still undoing: {running.Description}.")
    {
    }
}

public sealed class UndoStack(int depth = 20) : IUndoStack
{
    private readonly List<IUndoableOperation> _operations = [];
    private readonly Lock _gate = new();

    public bool CanUndo
    {
        get
        {
            lock (_gate)
                return _operations.Count > 0;
        }
    }

    public string? NextDescription
    {
        get
        {
            lock (_gate)
                return _operations.Count > 0 ? _operations[^1].Description : null;
        }
    }

    public event EventHandler? Changed;

    public event EventHandler<IUndoableOperation>? Pushed;

    public void Push(IUndoableOperation operation)
    {
        lock (_gate)
        {
            _operations.Add(operation);
            if (_operations.Count > depth)
                _operations.RemoveAt(0);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Pushed?.Invoke(this, operation);
    }

    public IReadOnlyList<IUndoableOperation> Pending
    {
        get
        {
            lock (_gate)
                return _operations.AsEnumerable().Reverse().ToList();
        }
    }

    private IUndoableOperation? _running;

    private readonly List<Slot> _working = [];

    public IReadOnlyList<string> UnderWay
    {
        get
        {
            lock (_gate)
                return _working.Select(s => s.Doing).Reverse().ToList();
        }
    }

    public bool Waits
    {
        get
        {
            lock (_gate)
                return _running is not null || _working.Count > 0;
        }
    }

    public IUndoSlot Working(string doing)
    {
        var slot = new Slot(this, doing);
        lock (_gate)
            _working.Add(slot);
        // Raised both ways: the Activity panel lists what is under way, and nothing said that
        // Ctrl+Z was waiting on it (BUGS.md, 2026-10-23). A listener that throws must not leave a
        // slot nobody owns, holding Ctrl+Z for the rest of the session.
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            lock (_gate)
                _working.Remove(slot);
            throw;
        }
        return slot;
    }

    private sealed class Slot(UndoStack stack, string doing) : IUndoSlot
    {
        private int _disposed;

        public string Doing => doing;

        public void Push(IUndoableOperation operation) => stack.Push(operation);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            lock (stack._gate)
                stack._working.Remove(this);
            stack.Changed?.Invoke(stack, EventArgs.Empty);
        }
    }

    public IUndoableOperation? Running
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    public async Task<IUndoableOperation?> UndoAsync(CancellationToken ct)
    {
        IUndoableOperation operation;
        lock (_gate)
        {
            if (_running is not null)
                throw new UndoBusyException(_running);
            if (_working.Count > 0)
                throw new UndoBusyException($"Still {_working[^1].Doing}.");
            if (_operations.Count == 0)
                return null;
            operation = _operations[^1];
            _operations.RemoveAt(_operations.Count - 1);
            _running = operation;
        }

        try
        {
            // Inside: a listener that throws must not leave the stack thinking an undo still runs.
            Changed?.Invoke(this, EventArgs.Empty);
            await operation.UndoAsync(ct);
        }
        finally
        {
            lock (_gate)
                _running = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return operation;
    }
}

/// <summary>An undo built from a delegate, for operations simple enough not to need a type.</summary>
public sealed class DelegateUndo(string description, string shellEquivalent, Func<CancellationToken, Task> undo)
    : IUndoableOperation
{
    public string Description { get; } = description;

    public string ShellEquivalent { get; } = shellEquivalent;

    public Task UndoAsync(CancellationToken ct) => undo(ct);
}
