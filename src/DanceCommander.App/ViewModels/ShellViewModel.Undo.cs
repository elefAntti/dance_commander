using DanceCommander.Core.Operations;

namespace DanceCommander.App.ViewModels;

/// <summary>One line of the undo list: what it is, and what Ctrl+Z will make of it.</summary>
public sealed record UndoRow(string Description, string? Note)
{
    /// <summary>
    /// <see cref="Note"/> as the list writes it after the description. Composed here because a
    /// binding's StringFormat is applied to a null too, which ended every row in a stray " · ".
    /// </summary>
    public string? NoteSuffix => Note is null ? null : $" · {Note}";
}

/// <summary>
/// The undo stack, shown above the log in the Activity panel: what the next Ctrl+Z undoes, and
/// the one after. The log alone said what had happened, not what Ctrl+Z would do next, and after
/// an undo that had left things in place there was no telling (2026-10-17).
/// </summary>
public sealed partial class ShellViewModel
{
    public bool HasUndo => Undo.Running is not null || Undo.CanUndo || Undo.UnderWay.Count > 0;

    /// <summary>When the last undo ended, by the clock key presses are stamped with.</summary>
    private int _undoEndedAt = int.MinValue;

    private bool _undoWasRunning;

    private void NoteUndoEnd()
    {
        var running = Undo.Running is not null;
        if (_undoWasRunning && !running)
            _undoEndedAt = Environment.TickCount;
        _undoWasRunning = running;
    }

    /// <summary>
    /// Whether a key press stamped <paramref name="timestamp"/> was made before the last undo
    /// ended: kept waiting while the window was busy, it reached us only after. Only within a
    /// minute of that end: the clock's difference wraps after 24.8 days, and an app left open
    /// that long would otherwise drop every Ctrl+Z after it (review, 2026-10-18).
    /// </summary>
    public bool PressedBeforeUndoEnded(int timestamp)
    {
        if (_undoEndedAt == int.MinValue)
            return false;
        var before = unchecked(_undoEndedAt - timestamp);
        return before > 0 && before <= 60_000;
    }

    /// <summary>"Ctrl+Z undoes these, the top one first", with whatever key it is now.</summary>
    public string UndoHeading =>
        Commands.Find("dc.undo")?.Binding is { } key ? $"{key} undoes these, the top one first" : "Undo, the top one first";

    /// <summary>The next few Ctrl+Z will reach; the stack below them is a count.</summary>
    private const int UndoRowsShown = 5;

    public IReadOnlyList<UndoRow> UndoRows
    {
        get
        {
            var rows = new List<UndoRow>();
            if (Undo.Running is { } running)
                rows.Add(new UndoRow(running.Description, "being undone now"));
            // On top, where their records will land: Ctrl+Z waited on them and nothing here said
            // why (BUGS.md 25).
            rows.AddRange(Undo.UnderWay.Select(doing =>
                new UndoRow(char.ToUpperInvariant(doing[0]) + doing[1..], "under way: undo waits for it")));
            var pending = Undo.Pending.ToList();
            rows.AddRange(pending.Take(UndoRowsShown).Select(operation => new UndoRow(
                operation.Description,
                // On the stack only to say so, rather than Ctrl+Z reaching past it.
                operation is Irreversible ? "cannot be undone" : null)));
            // The rest counted, not listed: a day's stack filled the panel until the activity
            // under it had no room left (BUGS.md 42), and Ctrl+Z reaches them only after these.
            if (pending.Count > UndoRowsShown)
                rows.Add(new UndoRow($"and {pending.Count - UndoRowsShown:N0} earlier", null));
            return rows;
        }
    }
}
