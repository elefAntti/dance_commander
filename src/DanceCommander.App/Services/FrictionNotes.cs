using System.IO;
using System.Text;

namespace DanceCommander.App.Services;

/// <summary>
/// What got in someone's way, in their words, at the moment it did (PLAN.md §3, "Friction from
/// other people"). No telemetry: §1.4 rules out talking to a service, so a note is kept in
/// <see cref="FileName"/> on this computer, and sending it is the user's act, through "Copy
/// friction report". Each note carries what makes it readable later without asking: the time,
/// the kind of place (file, sftp, about…) and the last few commands, never a path.
/// </summary>
public static class FrictionNotes
{
    /// <summary>Beside the logs and <see cref="ErrorReports.FileName"/>, appended, one line per note.</summary>
    public static string FileName { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanceCommander", "friction.log");

    /// <summary>Enough to show what led up to it; more reads as a log rather than a note.</summary>
    private const int RecentKept = 5;

    /// <summary>The over-budget moments a report carries: the latest of a session, not all of a slow day.</summary>
    private const int SlowKept = 20;

    /// <summary>A report sends the latest notes; the file keeps them all until it reaches this.</summary>
    private const int NotesInReport = 50;

    private const long MostBytes = 1024 * 1024;

    private static readonly Lock Gate = new();
    private static readonly Queue<string> Recent = new();
    private static readonly Queue<string> Slow = new();

    /// <summary>
    /// A command was run, by whatever route. Its title only: a tool's arguments, or what a
    /// command was run on, can name the user's files.
    /// </summary>
    public static void Ran(string title)
    {
        lock (Gate)
        {
            Recent.Enqueue(title);
            if (Recent.Count > RecentKept)
                Recent.Dequeue();
        }
    }

    /// <summary>The commands run before this moment, oldest first, for a note to carry.</summary>
    public static IReadOnlyList<string> RecentCommands()
    {
        lock (Gate)
            return [.. Recent];
    }

    /// <summary>
    /// A moment over its budget (<see cref="Budgets"/>), kept as numbers only. The log's own line
    /// names the folder; this one has to be safe to send as it is.
    /// </summary>
    public static void Over(string what, double ms, TimeSpan budget)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss} {what} {ms:0} ms (budget {budget.TotalMilliseconds:0} ms)";
        lock (Gate)
        {
            Slow.Enqueue(line);
            if (Slow.Count > SlowKept)
                Slow.Dequeue();
        }
    }

    /// <summary>One line in the file: tabs within it would be the only thing to break it, so they go.</summary>
    public static string Line(DateTimeOffset at, string scheme, IReadOnlyList<string> recent, string text) =>
        $"{at:yyyy-MM-dd HH:mm} [{scheme}] {Flat(text)}"
        + (recent.Count == 0 ? "" : $"  (after: {string.Join(", ", recent.Select(Flat))})");

    /// <summary>Keeps the note, and says whether it could.</summary>
    public static bool Note(string text, string scheme, IReadOnlyList<string> recent)
    {
        try
        {
            var file = new FileInfo(FileName);
            if (file.Exists && file.Length > MostBytes)
                return false;
            Directory.CreateDirectory(file.DirectoryName!);
            // Appended to our own file beside the logs: nothing of the user's is touched.
            File.AppendAllText(FileName, Line(DateTimeOffset.Now, scheme, recent, text) + Environment.NewLine);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Could not keep a friction note in {File}", FileName);
            return false;
        }
    }

    /// <summary>The notes in the file, oldest first; none when there is no file or it can't be read.</summary>
    public static IReadOnlyList<string> Notes()
    {
        try
        {
            return File.Exists(FileName)
                ? File.ReadAllLines(FileName).Where(l => l.Length > 0).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Could not read {File}", FileName);
            return [];
        }
    }

    /// <summary>
    /// What to send: the version and Windows, the latest notes, this session's slow moments and
    /// how many errors it carried past. Only what the user typed can name a file; the heading says so.
    /// </summary>
    public static string Report()
    {
        var notes = Notes();
        List<string> slow;
        lock (Gate)
            slow = [.. Slow];

        var text = new StringBuilder()
            .AppendLine($"Dance Commander {ErrorReports.Version()}, Windows {Environment.OSVersion.Version}, .NET {Environment.Version}")
            .AppendLine("Paths are left out; only your own words in the notes can name a file or a server.");

        text.AppendLine()
            .AppendLine(notes.Count == 0 ? "No friction notes." : $"Friction notes ({notes.Count:N0}):");
        foreach (var note in notes.Skip(Math.Max(0, notes.Count - NotesInReport)))
            text.AppendLine(note);

        text.AppendLine()
            .AppendLine(slow.Count == 0 ? "Nothing over budget this session." : "Over budget this session, latest last:");
        foreach (var line in slow)
            text.AppendLine(line);

        if (ErrorReports.Count > 0)
            text.AppendLine()
                .AppendLine($"{ErrorReports.Count:N0} error{(ErrorReports.Count == 1 ? "" : "s")} carried past this session: " +
                            "\"Copy error report\" has the details.");
        return text.ToString();
    }

    private static string Flat(string text) =>
        string.Join(' ', text.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
