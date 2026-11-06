using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DanceCommander.Core.Commands;

namespace DanceCommander.App.ViewModels;

/// <summary>
/// One row of the palette: a command, or the header of the section it sits in (§6.18). Everything
/// on a command row was declared by the command itself.
/// </summary>
public sealed record CommandPaletteRow(
    CommandDefinition? Command,
    string? Heading = null,
    ImageSource? Icon = null,
    string? Branch = null)
{
    /// <summary>A header names what the commands under it act on. It is never highlighted or run.</summary>
    public bool IsHeader => Command is null;

    public string Title => Command?.Title ?? Heading ?? string.Empty;

    public string Group => Command?.Group ?? string.Empty;

    /// <summary>The key that does this without opening the palette at all — which is the point.</summary>
    public string Binding => Command?.Binding?.ToString() ?? string.Empty;

    /// <summary>Space above every header but the first, so a section reads as a section.</summary>
    public bool IsFirst { get; init; }
}

/// <summary>What the palette needs to know about where you are standing, to name it in its headers.</summary>
/// <param name="Title">What the listing calls itself: the folder's name, or a view's own title.</param>
/// <param name="Branch">The checked-out branch, as the pane footer shows it.</param>
public sealed record PaletteLocation(string Title, ImageSource? Icon, string? Branch);

/// <summary>
/// The command palette (`Ctrl+Shift+P`): a text box and a list, over the one registry (§1.2/1).
/// </summary>
/// <remarks>
/// It is a third text input in an application that reserved exactly two (§1.2/7), and that is on
/// purpose rather than by oversight: the address bar and the command line are *regions*, permanently
/// reserved and permanently costing space, while this one is transient, owns the keyboard only
/// while it is up, and leaves on Escape handing focus back to the grid — the same contract every
/// other transient surface here signs.
///
/// It shows every command that applies where you are standing, with its key beside it, so it
/// doubles as the answer to "what can I do here, and what is it bound to". The F-key legend teaches
/// one modifier layer at a time; this teaches the rest.
///
/// The list is in sections by what each command acts on (§6.18), each headed by the *name* of that
/// thing — "ARCHITECTURE.md", "dance_commander ⎇ main" — because the question a header answers is
/// "what will this act on", and a name answers it where a category would only restate it.
/// </remarks>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ICommandRegistry _registry;
    private readonly Func<ICommandContext> _context;
    private readonly Func<PaletteLocation> _location;
    private readonly Func<Core.Fs.FsEntry, ImageSource?> _iconFor;
    private readonly CommandPalette _palette;

    public CommandPaletteViewModel(
        ICommandRegistry registry,
        Func<ICommandContext> context,
        Func<PaletteLocation> location,
        Func<Core.Fs.FsEntry, ImageSource?> iconFor,
        CommandUsage? usage = null)
    {
        _palette = new CommandPalette(usage);
        _registry = registry;
        _context = context;
        _location = location;
        _iconFor = iconFor;
    }

    /// <summary>
    /// Replaced whole on each refresh rather than cleared and refilled: added one at a time, each
    /// row was a change the list reacted to, 87 of a first open's 122 ms (DESIGN.md §1.5).
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<CommandPaletteRow> _rows = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private CommandPaletteRow? _selected;

    /// <summary>
    /// What the list is not showing, said out loud. Rows missing with nothing on screen to explain
    /// why is the thing this application exists to avoid (§1.3), and "why is Paste not in here" has
    /// exactly one honest answer: it does not apply to where you are.
    /// </summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>When the last open began (a Stopwatch timestamp), for the view's timing line.</summary>
    public long OpenedAt { get; private set; }

    /// <summary>How much of the last open was ours: deciding what applies and building the rows.</summary>
    public TimeSpan Built { get; private set; }

    public void Open()
    {
        OpenedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        // Clearing a query refreshes by itself; refreshing as well built the list twice.
        if (Query.Length > 0)
            Query = string.Empty;
        else
            Refresh();
        Built = System.Diagnostics.Stopwatch.GetElapsedTime(OpenedAt);
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        Rows = [];
        Selected = null;
    }

    partial void OnQueryChanged(string value) => Refresh();

    /// <summary>
    /// Moves the highlight without moving focus, which stays in the query box throughout. Headers
    /// are stepped over: they label a section, they are not something to choose.
    /// </summary>
    public void Move(int delta)
    {
        var commands = Rows.Where(r => !r.IsHeader).ToList();
        if (commands.Count == 0)
            return;

        var index = Selected is null ? 0 : commands.IndexOf(Selected) + delta;
        Selected = commands[Math.Clamp(index, 0, commands.Count - 1)];
    }

    /// <summary>
    /// Tab: to the first command of the next section, or the previous with Shift — what makes the
    /// sections something to navigate rather than decoration. Wraps, since there are only three.
    /// </summary>
    public void JumpSection(int direction)
    {
        var firsts = Rows
            .Select((row, index) => (row, index))
            .Where(entry => entry.row.IsHeader && entry.index + 1 < Rows.Count)
            .Select(entry => Rows[entry.index + 1])
            .ToList();
        if (firsts.Count == 0)
            return;

        var current = Selected is null ? -1 : SectionOf(Selected);
        var target = current < 0 ? 0 : (current + direction + firsts.Count) % firsts.Count;
        Selected = firsts[target];

        int SectionOf(CommandPaletteRow row)
        {
            var section = -1;
            foreach (var candidate in Rows)
            {
                if (candidate.IsHeader)
                    section++;
                if (ReferenceEquals(candidate, row))
                    return section;
            }
            return -1;
        }
    }

    /// <summary>The chosen command, remembered so it rises to the top of its section next time.</summary>
    public CommandDefinition? Take()
    {
        if (Selected is not { Command: { } command })
            return null;

        _palette.Remember(command);
        return command;
    }

    private void Refresh()
    {
        // As for keys and menus: a command whose check throws must not take the window with it.
        try
        {
            Build();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not list the palette's commands");
            Rows = [];
            Selected = null;
            Summary = "The commands could not be listed here. The log says why.";
        }
    }

    private void Build()
    {
        // Applicability is re-asked every keystroke rather than cached, because the palette is open
        // over a live application: a transfer can finish and an inline prompt can resolve while it
        // is up, and a list that had gone stale would offer commands that decline to run.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var context = _context();
        var location = _location();
        var contextAt = System.Diagnostics.Stopwatch.GetTimestamp();

        // Once, not per command: it names the selection and fetches its icon, and asking it inside
        // the filter below did that for every one of thirty-odd selection commands, an icon lookup
        // each (most of the first open's 119 ms, DESIGN.md §1.5).
        var selectionHeader = Header(CommandSubject.Selection, context, location, first: false);

        var applicable = _registry.ApplicableTo(context)
            .Where(command => command.Group is not null)
            // Commands about the selection with nothing selected and no row under the cursor act on
            // nothing. They are left out before anything is counted, so the footer does not report
            // them as hidden by the query — they were never on offer.
            .Where(command => command.Subject != CommandSubject.Selection || selectionHeader is not null)
            .ToList();
        var sections = _palette.Sections(applicable, Query);
        var decidedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        var rows = new List<CommandPaletteRow>();
        var counts = new List<string>(3);
        foreach (var section in sections)
        {
            var header = section.Subject == CommandSubject.Selection
                ? selectionHeader is null ? null : selectionHeader with { IsFirst = rows.Count == 0 }
                : Header(section.Subject, context, location, first: rows.Count == 0);
            if (header is null)
                continue;

            rows.Add(header);
            foreach (var command in section.Commands)
                rows.Add(new CommandPaletteRow(command));

            counts.Add($"{section.Commands.Count:N0} {Noun(section.Subject)}");
        }

        // Each assignment below runs the list's own reaction to it before returning, and between
        // them they were 83-134 ms of every slow open with the rows themselves next to free, so
        // the timing line names which one it was.
        var builtAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Rows = rows;
        var listedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Selected = Rows.FirstOrDefault(r => !r.IsHeader);
        var selectedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Summary = Describe(Rows.Count(r => !r.IsHeader), applicable.Count, counts);
        OnPropertyChanged(nameof(HasQuery));

        static long Ms(long from, long to) => (long)System.Diagnostics.Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
        var doneAt = System.Diagnostics.Stopwatch.GetTimestamp();
        BuildSteps = $"context {Ms(started, contextAt)} ms, deciding {Ms(contextAt, decidedAt)} ms, " +
            $"rows {Ms(decidedAt, builtAt)} ms, list {Ms(builtAt, listedAt)} ms, " +
            $"highlight {Ms(listedAt, selectedAt)} ms, footer {Ms(selectedAt, doneAt)} ms";
    }

    /// <summary>Where the last build's time went, for the timing line when it is over budget.</summary>
    public string BuildSteps { get; private set; } = string.Empty;

    /// <summary>
    /// Names the thing a section's commands act on. Null when there is nothing to name — commands
    /// about "the selection" with nothing selected and no row under the cursor would be commands
    /// about nothing, so the section is left out rather than headed with an apology.
    /// </summary>
    private CommandPaletteRow? Header(
        CommandSubject subject, ICommandContext context, PaletteLocation location, bool first)
    {
        switch (subject)
        {
            case CommandSubject.Selection:
                if (context.Selection.Count > 1)
                    return new CommandPaletteRow(null, $"{context.Selection.Count:N0} selected") { IsFirst = first };

                // One thing: named, and drawn with its own icon, the way the folder's header is.
                if ((context.Selection.Count == 1 ? context.Selection[0] : context.Focused) is not { } entry)
                    return null;

                // ".." is the parent folder, and saying so is kinder than a header made of two dots.
                var name = entry.Name == ".." ? ".. (parent folder)" : entry.Name;
                return new CommandPaletteRow(null, name, _iconFor(entry)) { IsFirst = first };

            case CommandSubject.Location:
                return new CommandPaletteRow(null, location.Title, location.Icon, location.Branch) { IsFirst = first };

            default:
                return new CommandPaletteRow(null, "This window") { IsFirst = first };
        }
    }

    private static string Noun(CommandSubject subject) => subject switch
    {
        CommandSubject.Selection => "for the selection",
        CommandSubject.Location => "for this folder",
        _ => "for the window",
    };

    /// <summary>Drives the hint in the empty query box; a blank field should still say what it is for.</summary>
    public bool HasQuery => Query.Length > 0;

    private static string Describe(int shown, int applicable, IReadOnlyList<string> counts)
    {
        if (shown == 0)
            return applicable == 0
                ? "No command applies here."
                : "Nothing here matches what you typed.";

        var total = shown == applicable
            ? $"{shown:N0} commands apply here"
            : $"{shown:N0} of the {applicable:N0} commands that apply here";

        return counts.Count > 1 ? $"{total} · {string.Join(", ", counts)}." : $"{total}.";
    }
}
