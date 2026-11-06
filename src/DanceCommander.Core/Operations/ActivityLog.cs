namespace DanceCommander.Core.Operations;

public enum ActivityOutcome
{
    Succeeded,
    Failed,
    Undone,
}

public sealed record ActivityEntry(
    DateTimeOffset At,
    string Description,
    string ShellEquivalent,
    ActivityOutcome Outcome,
    string? Detail = null);

public interface IActivityLog
{
    IReadOnlyList<ActivityEntry> Entries { get; }

    event EventHandler? Changed;

    void Record(string description, string shellEquivalent, ActivityOutcome outcome = ActivityOutcome.Succeeded, string? detail = null);
}

/// <summary>
/// What the application did to your files, in the shell's own words. This is the clearest
/// statement of the project's values: no magic, and the computer tells you what it did (§1.2/6).
/// </summary>
public sealed class ActivityLog(int capacity = 500) : IActivityLog
{
    private readonly List<ActivityEntry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<ActivityEntry> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToList();
        }
    }

    public event EventHandler? Changed;

    public void Record(
        string description,
        string shellEquivalent,
        ActivityOutcome outcome = ActivityOutcome.Succeeded,
        string? detail = null)
    {
        lock (_gate)
        {
            _entries.Insert(0, new ActivityEntry(DateTimeOffset.Now, description, shellEquivalent, outcome, detail));
            if (_entries.Count > capacity)
                _entries.RemoveRange(capacity, _entries.Count - capacity);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Renders operations as the shell commands that would have done the same thing.</summary>
public static class ShellText
{
    public static string Quote(string value) =>
        value.Length == 0 || value.Contains(' ') || value.Contains('&') ? $"\"{value}\"" : value;

    public static string Quote(Fs.FsUri uri) => Quote(uri.Display);

    public static string Move(Fs.FsUri from, Fs.FsUri to) => $"move {Quote(from)} {Quote(to)}";

    public static string Copy(Fs.FsUri from, Fs.FsUri to) => $"copy {Quote(from)} {Quote(to)}";

    public static string MakeDirectory(Fs.FsUri dir) => $"mkdir {Quote(dir)}";

    public static string Delete(Fs.FsUri path) => $"del {Quote(path)}";

    /// <summary>
    /// What Delete permanently did, as cmd would have to be told: <c>/f</c>, or a read-only file is
    /// refused, and a folder with all it holds is <c>rmdir /s /q</c> (<c>del</c> on a folder empties
    /// it and leaves the folder).
    /// </summary>
    public static string DeletePermanently(Fs.FsUri path, bool isDirectory) =>
        isDirectory ? $"rmdir /s /q {Quote(path)}" : $"del /f {Quote(path)}";

    public static string RemoveDirectory(Fs.FsUri dir) => $"rmdir {Quote(dir)}";

    public static string Chmod(Fs.FsUri path, int mode) => $"chmod {Convert.ToString(mode, 8)} {Quote(path)}";

    /// <summary>Joins several lines, collapsing a long list so the log stays readable.</summary>
    public static string Lines(IEnumerable<string> commands, int max = 6)
    {
        var all = commands.ToList();
        if (all.Count <= max)
            return string.Join(Environment.NewLine, all);

        return string.Join(Environment.NewLine, all.Take(max))
            + Environment.NewLine + $"… and {all.Count - max:N0} more";
    }
}
