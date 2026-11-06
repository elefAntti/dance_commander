namespace DanceCommander.App.Services;

/// <summary>
/// DESIGN.md §1.5 in numbers: how long the moments the app is judged by may take on this machine.
/// Every timing line in the log is written against one of these and names the one it broke, so
/// the log reads as a list of promises not kept rather than a list of numbers to interpret.
/// </summary>
public static class Budgets
{
    /// <summary>Launch: the placeholder window, so a click is answered at once.</summary>
    public static readonly TimeSpan Placeholder = TimeSpan.FromMilliseconds(300);

    /// <summary>Launch: a frame with both panes' rows in it.</summary>
    public static readonly TimeSpan RowsOnScreen = TimeSpan.FromMilliseconds(1500);

    /// <summary>A local folder's rows on screen: navigation is the thing that must feel instant.</summary>
    public static readonly TimeSpan LocalRows = TimeSpan.FromMilliseconds(250);

    /// <summary>Anywhere else (SFTP, WebDAV, archives, search, scripts): allowed the network, not more.</summary>
    public static readonly TimeSpan OtherRows = TimeSpan.FromMilliseconds(1000);

    /// <summary>Rows first, then branch pills, highlights and icons: the late part may not arrive much later.</summary>
    public static readonly TimeSpan Decorated = TimeSpan.FromMilliseconds(1000);

    /// <summary>A context menu, from the click to the frame it is on: the threshold of "instant".</summary>
    public static readonly TimeSpan Menu = TimeSpan.FromMilliseconds(100);

    /// <summary>The command palette, key to frame: the feature that is the feeling of control.</summary>
    public static readonly TimeSpan Palette = TimeSpan.FromMilliseconds(100);

    /// <summary>A key typed into the quick filter, to the grid laid out: it replaces type-ahead, which was instant.</summary>
    public static readonly TimeSpan Filter = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Any click or key, from Windows taking it to the next frame: the floor under the budgets
    /// above, for a stall none of them sees. Looser than they are, so a slow menu or filter is
    /// reported by its own line and not by this one too.
    /// </summary>
    public static readonly TimeSpan Answer = TimeSpan.FromMilliseconds(500);

    /// <summary>What <see cref="Answer"/>'s line calls it, and what keeps it out of the friction report.</summary>
    public const string AnswerName = "answer";

    /// <summary>"over budget: rows (250 ms)" for each budget exceeded, or null when none was.</summary>
    public static string? Verdict(params (string What, double Ms, TimeSpan Budget)[] checks)
    {
        var over = checks
            .Where(c => c.Ms > c.Budget.TotalMilliseconds)
            .ToList();
        // Kept for "Copy friction report" too, as numbers: the log's line names the folder. Not the
        // answer clock's: it waits for a frame, and a key that changes nothing on screen gets none
        // until some later repaint, so it reads 20 s where the window answered at once (BUGS.md
        // 49). Sent from someone else's machine, nobody could tell those from real stalls.
        foreach (var (what, ms, budget) in over)
        {
            if (what != AnswerName)
                FrictionNotes.Over(what, ms, budget);
        }
        return over.Count == 0 ? null
            : "over budget: " + string.Join(", ", over.Select(c => $"{c.What} ({c.Budget.TotalMilliseconds:0} ms)"));
    }
}
