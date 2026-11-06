using DanceCommander.Core.Config;

namespace DanceCommander.Core.Navigation;

/// <summary><c>commands.json</c>: lines typed at the command line, one list per shell spoken to.</summary>
public sealed class CommandHistoryFile
{
    /// <summary>By what was spoken to ("bash", "cmd", "claude@host"), oldest first.</summary>
    public Dictionary<string, List<string>> Lines { get; set; } = [];
}

/// <summary>
/// Earlier lines for Up and Down at the command line (COMMAND_LINE_PLAN.md C). One list per shell
/// spoken to: local bash, cmd and pwsh apart, and each server account apart, since a line written
/// for one rarely means anything to another. Lines are kept as typed: a <c>${prompt:…}</c> stays a
/// placeholder, never the answer that was given to it.
/// </summary>
public sealed class CommandHistory : IDisposable
{
    /// <summary>Per list: enough to go back a working week, not a lifetime.</summary>
    public const int Capacity = 300;

    private readonly IConfigStore<CommandHistoryFile>? _store;
    private readonly Debouncer _save = new(TimeSpan.FromSeconds(3));
    private readonly Dictionary<string, List<string>> _lines;
    private readonly Lock _gate = new();

    public CommandHistory(IConfigStore<CommandHistoryFile>? store = null)
    {
        _store = store;
        _lines = new Dictionary<string, List<string>>(
            store?.Current.Lines ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The lines typed to <paramref name="key"/>, oldest first.</summary>
    public IReadOnlyList<string> For(string key)
    {
        lock (_gate)
            return _lines.TryGetValue(key, out var lines) ? lines.ToList() : [];
    }

    /// <summary>
    /// Adds a line as the newest. The same line again moves to the end rather than appearing
    /// twice, so Up after repeating a command goes to the one before it, as a shell's history does
    /// with duplicates ignored.
    /// </summary>
    public void Add(string key, string line)
    {
        line = line.Trim();
        if (line.Length == 0)
            return;

        lock (_gate)
        {
            if (!_lines.TryGetValue(key, out var lines))
                _lines[key] = lines = [];
            lines.Remove(line);
            lines.Add(line);
            if (lines.Count > Capacity)
                lines.RemoveRange(0, lines.Count - Capacity);
        }

        _save.Schedule(() => _ = FlushAsync());
    }

    private Task FlushAsync()
    {
        if (_store is null)
            return Task.CompletedTask;

        CommandHistoryFile snapshot;
        lock (_gate)
            snapshot = new CommandHistoryFile { Lines = _lines.ToDictionary(p => p.Key, p => p.Value.ToList()) };
        return _store.SaveAsync(snapshot);
    }

    public void Dispose()
    {
        _save.Dispose();
        Debouncer.FlushAtExit(FlushAsync);
    }
}

/// <summary>
/// Up and Down through one list, keeping what was being typed: Down past the newest line gives it
/// back, as a shell does.
/// </summary>
public sealed class HistoryCursor(IReadOnlyList<string> lines, string draft)
{
    private int _index = lines.Count;

    /// <summary>The older line, or null at the oldest.</summary>
    public string? Older() => _index > 0 ? lines[--_index] : null;

    /// <summary>The newer line, the draft once past the newest, or null if already there.</summary>
    public string? Newer()
    {
        if (_index >= lines.Count)
            return null;
        _index++;
        return _index == lines.Count ? draft : lines[_index];
    }
}
