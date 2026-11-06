using System.IO;
using System.Reflection;
using System.Text;
using DanceCommander.Core.Decisions;

namespace DanceCommander.App.Services;

/// <summary>
/// Errors nothing caught, kept so they can be seen and sent. Ending the application on each one
/// made every one impossible to miss while it was ours alone (2026-10-17); for colleagues, a slip
/// in drawing a row or reading a folder should cost a notice, not the window and every tab in it.
/// The application carries on, says so, and keeps the details: in <see cref="FileName"/> beside
/// the logs, and as a report "Copy error report" puts on the clipboard.
/// </summary>
public static class ErrorReports
{
    /// <summary>Beside the logs, appended, one block per error.</summary>
    public static string FileName { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanceCommander", "errors.log");

    /// <summary>
    /// More than this many within <see cref="StormWindow"/> and the application ends after all: an
    /// error in drawing comes back on every frame, and carrying on would be a notice a frame.
    /// </summary>
    private const int StormCount = 5;

    private static readonly TimeSpan StormWindow = TimeSpan.FromSeconds(10);

    /// <summary>Past this the file is left alone, rather than grow without end; the log still has each.</summary>
    private const long MostBytes = 2 * 1024 * 1024;

    private const int MostKept = 50;

    private static readonly Lock Gate = new();

    private static readonly List<(DateTimeOffset At, string Where, Exception Error)> Session = [];

    /// <summary>Where a notice goes, once there is a window to show it in.</summary>
    public static Action<Notice>? Tell { get; set; }

    /// <summary>Errors kept this session.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
                return Session.Count;
        }
    }

    /// <summary>
    /// Keeps the error, and says whether the application can carry on: not before there is a
    /// window to say so in, and not in a storm.
    /// </summary>
    /// <param name="where">"on the UI thread", "in a background task".</param>
    public static bool Record(Exception error, string where)
    {
        var at = DateTimeOffset.Now;
        bool storm;
        lock (Gate)
        {
            Session.Add((at, where, error));
            if (Session.Count > MostKept)
                Session.RemoveAt(0);
            // Counted on the thread that carries on: the collector finds failed tasks in batches,
            // and six of those with one UI error after them ended the application (review, 2026-10-30).
            storm = Session.Count(e => e.Where == where && at - e.At < StormWindow) > StormCount;
        }

        Append(at, where, error);
        return Tell is not null && !storm;
    }

    /// <summary>The notice for one carried past: what it was, and how to send it.</summary>
    public static Notice NoticeFor(Exception error) => new(
        "Something went wrong, and Dance Commander carried on.",
        NoticeSeverity.Warning,
        $"{error.GetType().Name}: {FirstLine(error.Message)} " +
        "\"Copy error report\" in the palette puts the details on the clipboard to send.");

    /// <summary>
    /// What to send: the version and Windows, then each error of this session with where it
    /// happened and its stack. Paths in the messages are the user's; the heading says so.
    /// </summary>
    public static string Report()
    {
        List<(DateTimeOffset At, string Where, Exception Error)> errors;
        lock (Gate)
            errors = [.. Session];

        var text = new StringBuilder()
            .AppendLine($"Dance Commander {Version()}, Windows {Environment.OSVersion.Version}, .NET {Environment.Version}")
            .AppendLine($"{errors.Count:N0} error{(errors.Count == 1 ? "" : "s")} this session. " +
                        "What follows can name your files and folders, servers and the accounts on them, " +
                        "and what your scripts printed: remove anything you would rather not send.");
        foreach (var (at, where, error) in errors)
        {
            text.AppendLine()
                .AppendLine($"{at:yyyy-MM-dd HH:mm:ss}, {where}:")
                .AppendLine(error.ToString());
        }
        return text.ToString();
    }

    private static void Append(DateTimeOffset at, string where, Exception error)
    {
        try
        {
            var file = new FileInfo(FileName);
            if (file.Exists && file.Length > MostBytes)
                return;
            // Appended to our own file beside the logs: nothing of the user's is touched.
            File.AppendAllText(FileName,
                $"{at:O} {Version()}, {where}:{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log has it; a second failure here must not become the next error.
            Serilog.Log.Warning(ex, "Could not keep an error in {File}", FileName);
        }
    }

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.EndsWith('.') ? line : line + ".";
    }

    /// <summary>The version the build was given, with the commit's first seven characters.</summary>
    public static string Version()
    {
        var informational = (Assembly.GetEntryAssembly() ?? typeof(ErrorReports).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 || informational.Length - plus - 1 <= 7
            ? informational
            : informational[..(plus + 8)];
    }
}
