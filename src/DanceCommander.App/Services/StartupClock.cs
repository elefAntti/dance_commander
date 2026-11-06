using System.Diagnostics;
using System.Windows.Media;
using Serilog;

namespace DanceCommander.App.Services;

/// <summary>
/// Milliseconds since the process started, for the one line the log gets about every launch:
/// when the window was first drawn, when each pane had its rows, and when a frame with rows in it
/// reached the screen. Startup is only kept fast by measuring it, and a measurement from outside
/// (UI Automation, a stopwatch) waits on the same UI thread it is trying to time.
/// </summary>
public static class StartupClock
{
    private static readonly DateTime Started = Process.GetCurrentProcess().StartTime;
    private static readonly List<(string What, long Ms)> Marks = [];
    private static bool _reported;
    private static bool _reportScheduled;
    private static int _rowsAwaited;
    private static readonly HashSet<object> PanesCounted = [];
    private static bool _rowsDone;
    private static bool _windowShown;

    public static long Now => (long)(DateTime.Now - Started).TotalMilliseconds;

    /// <summary>True until the Startup line is written: what happens now is the launch's.</summary>
    public static bool Launching => !_reported;

    /// <summary>Records a moment. Safe from any thread: the launch placeholder marks from its own.</summary>
    public static void Mark(string what)
    {
        lock (Marks)
        {
            if (!_reported)
                Marks.Add((what, Now));
            else
                // After the line, as with the placeholder going a frame after the rows arrive: a
                // line of its own rather than lost, since a missing mark reads as one that never came.
                Log.Information("Startup, after that: {What} {Elapsed} ms", what, Now);
        }
    }

    /// <summary>
    /// The launch lists this many panes: the line is written on the first frame after the last of
    /// them has its rows (<see cref="RowsSet"/>), not after they are decorated. Waiting for that
    /// put git status into "rows on screen", ~140 ms after the rows were in fact drawn.
    /// </summary>
    public static void ExpectRows(int panes) => _rowsAwaited = panes;

    /// <summary>A pane's rows are in the grid. On the UI thread.</summary>
    public static void RowsSet(object pane, int count)
    {
        // A launch's listings only: after the line, every folder opened all day said so in the log.
        if (_reported)
            return;
        Mark($"{count} rows set");
        Counted(pane);
    }

    /// <summary>
    /// A pane is showing a question, such as a password for the server it restores. That is the
    /// launch done for that pane: what it shows next waits for the user, and the line waited with
    /// it, so a launch that ended at a password prompt never reported at all. On the UI thread.
    /// </summary>
    public static void PaneAsks(object pane)
    {
        if (_reported || _rowsAwaited == 0)
            return;
        Mark("a pane asks");
        Counted(pane);
    }

    // Once per pane: a pane that asks and then lists, or asks twice, is still one pane.
    private static void Counted(object pane)
    {
        if (_rowsAwaited > 0 && PanesCounted.Add(pane) && --_rowsAwaited == 0)
        {
            _rowsDone = true;
            ReportWhenOnScreen();
        }
    }

    /// <summary>The panes' listings have ended: the fallback, for one that failed and set no rows.</summary>
    public static void ListingsEnded()
    {
        _rowsDone = true;
        ReportWhenOnScreen();
    }

    /// <summary>
    /// The window is shown. Rows can be in the grid before this (they are listed while the window
    /// is built), and WPF renders ticks for a window not yet shown: a frame after the rows alone
    /// read as "rows on screen" half a second before there was a window to see them in.
    /// </summary>
    public static void WindowShown()
    {
        Mark("shown");
        _windowShown = true;
        ReportWhenOnScreen();
    }

    private static void ReportWhenOnScreen()
    {
        if (_rowsDone && _windowShown)
            ReportAfterNextFrame("rows on screen");
    }

    /// <summary>Marks the next frame actually rendered, then writes the line, once.</summary>
    private static void ReportAfterNextFrame(string what)
    {
        if (_reported || _reportScheduled)
            return;
        _reportScheduled = true;

        void OnRendering(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            Mark(what);
            lock (Marks)
            {
                _reported = true;
                Log.Information("Startup: {Marks}, {Verdict}",
                    string.Join(", ", Marks.Select(m => $"{m.What} {m.Ms} ms")),
                    Budgets.Verdict(
                        ("placeholder shown", At("placeholder shown"), Budgets.Placeholder),
                        (what, At(what), Budgets.RowsOnScreen)) ?? "within budget");
            }
        }

        CompositionTarget.Rendering += OnRendering;
    }

    // Not yet marked is not judged: the placeholder can land after the rows, on a line of its own.
    private static double At(string what) =>
        Marks.Where(m => m.What == what).Select(m => (double)m.Ms).DefaultIfEmpty(0).First();
}
