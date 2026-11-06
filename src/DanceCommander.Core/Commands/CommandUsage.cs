using DanceCommander.Core.Config;

namespace DanceCommander.Core.Commands;

/// <summary><c>palette.json</c>: the commands chosen in the palette, oldest first.</summary>
public sealed class CommandUsageFile
{
    public List<string> Taken { get; set; } = [];
}

/// <summary>
/// How often each command has been chosen in the palette, across sessions: what tells "te" for
/// Terminal, used every day, from "te" for Text size, used once. Only the palette's own picks
/// count. A command run by its key says nothing about what you look for when you type, and Back
/// would outweigh everything else.
/// </summary>
/// <remarks>
/// A window over the last <see cref="Capacity"/> picks rather than counts for all time, so a habit
/// that changes is followed within a few weeks instead of being outvoted by last year's.
/// </remarks>
public sealed class CommandUsage : IDisposable
{
    /// <summary>A few weeks of picks for someone who lives in the palette.</summary>
    public const int Capacity = 200;

    private readonly IConfigStore<CommandUsageFile>? _store;
    private readonly Debouncer _save = new(TimeSpan.FromSeconds(3));
    private readonly Queue<string> _taken;
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _changed;

    public CommandUsage(IConfigStore<CommandUsageFile>? store = null)
    {
        _store = store;
        var taken = store?.Current.Taken ?? [];
        _taken = new Queue<string>(taken.Skip(Math.Max(0, taken.Count - Capacity)));
        foreach (var id in _taken)
            _counts[id] = _counts.GetValueOrDefault(id) + 1;
    }

    /// <summary>How many of the remembered picks were this command.</summary>
    public int Count(string id)
    {
        lock (_gate)
            return _counts.GetValueOrDefault(id);
    }

    public void Add(string id)
    {
        lock (_gate)
        {
            _taken.Enqueue(id);
            _counts[id] = _counts.GetValueOrDefault(id) + 1;
            if (_taken.Count > Capacity)
            {
                var dropped = _taken.Dequeue();
                if (--_counts[dropped] == 0)
                    _counts.Remove(dropped);
            }

            _changed = true;
        }

        _save.Schedule(() => _ = FlushAsync());
    }

    private async Task FlushAsync()
    {
        if (_store is null)
            return;

        CommandUsageFile snapshot;
        lock (_gate)
        {
            // Nothing chosen since the last save, so nothing to write: a session that never opened
            // the palette leaves the file as it found it.
            if (!_changed)
                return;
            _changed = false;
            snapshot = new CommandUsageFile { Taken = _taken.ToList() };
        }

        try
        {
            await _store.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch
        {
            // Still to be saved: cleared up front, a failed save was never tried again.
            lock (_gate)
                _changed = true;
            throw;
        }
    }

    public void Dispose()
    {
        _save.Dispose();
        Debouncer.FlushAtExit(FlushAsync);
    }
}
