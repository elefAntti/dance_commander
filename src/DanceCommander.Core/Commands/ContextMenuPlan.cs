namespace DanceCommander.Core.Commands;

/// <summary>What a context menu is about (§6.18).</summary>
public enum MenuTarget
{
    /// <summary>The selected rows.</summary>
    Selection,

    /// <summary>The ".." row: the parent folder, and nothing else.</summary>
    Parent,

    /// <summary>The folder the pane is showing.</summary>
    Location,
}

/// <summary>One line of a context menu, before anything draws it.</summary>
public abstract record MenuEntry;

/// <param name="IsPrimary">Open, drawn in bold at the top the way every file manager does it.</param>
public sealed record MenuCommand(CommandDefinition Command, bool IsPrimary = false) : MenuEntry;

public sealed record MenuSeparator : MenuEntry;

/// <summary>The line naming the folder a location menu is about. A label, not an option.</summary>
public sealed record MenuHeader : MenuEntry;

/// <summary>"In dance_commander ›": the folder's own menu at the foot of a row's.</summary>
public sealed record MenuFolder(IReadOnlyList<MenuEntry> Items) : MenuEntry;

/// <summary>
/// Which commands go in which context menu, and in what order (§6.18) — decided here, where it can
/// be tested, and only drawn by the window.
/// </summary>
/// <remarks>
/// Which commands belong is each command's own declaration of what it acts on: the selection's
/// commands on a row, the folder's on empty space. It used to be inferred in the window's code from
/// groups and scopes, untested, and the inference is how Worktrees came to vanish from one menu or
/// the other depending on what happened to be selected. Window commands — tabs, panes, sorting —
/// are never in a context menu: they are not about the thing under the cursor.
/// </remarks>
public static class ContextMenuPlan
{
    /// <summary>
    /// The ways of opening, together at the top: Open, then a new tab or the other pane for a
    /// folder, the other pane for an archive, a preview for a file. Each offers itself only where
    /// it works, so the block stays a few lines.
    /// </summary>
    public static readonly IReadOnlyList<string> Opening = ["dc.open", "dc.openInNewTab", "dc.openInOtherPane", "dc.preview"];

    /// <summary>
    /// What the ".." row offers beyond opening. It is the parent folder, and Rename or Delete on it
    /// would act on a folder the listing is not even showing.
    /// </summary>
    public static readonly IReadOnlyList<string> ForParent = ["dc.copyPath"];

    /// <param name="context">
    /// The context the commands will run against. For <see cref="MenuTarget.Parent"/>, one standing
    /// on the parent entry, which the selection never contains.
    /// </param>
    public static IReadOnlyList<MenuEntry> Build(ICommandRegistry registry, MenuTarget target, ICommandContext context)
    {
        var entries = new List<MenuEntry>();

        switch (target)
        {
            case MenuTarget.Location:
                entries.Add(new MenuHeader());
                entries.Add(new MenuSeparator());
                entries.AddRange(Grouped(Applicable(registry, context, CommandSubject.Location)));
                break;

            case MenuTarget.Parent:
                entries.AddRange(OpeningBlock(registry, context));
                foreach (var id in ForParent)
                {
                    if (registry.Find(id) is { } command && command.IsApplicable(context))
                        entries.Add(new MenuCommand(command));
                }
                break;

            default:
                entries.AddRange(OpeningBlock(registry, context));
                entries.AddRange(Grouped(
                    Applicable(registry, context, CommandSubject.Selection).Where(c => !Opening.Contains(c.Id))));

                // The folder's commands, one hover away from any row: a listing has very little
                // empty space to right-click, and this is the answer to that.
                var folder = Grouped(Applicable(registry, context, CommandSubject.Location));
                if (folder.Count > 0)
                {
                    if (entries.Count > 0 && entries[^1] is not MenuSeparator)
                        entries.Add(new MenuSeparator());
                    entries.Add(new MenuFolder(folder));
                }
                break;
        }

        return entries;
    }

    private static List<CommandDefinition> Applicable(
        ICommandRegistry registry, ICommandContext context, CommandSubject subject) =>
        registry.ApplicableTo(context)
            .Where(c => c.Subject == subject && c.Group is not null)
            .ToList();

    private static IEnumerable<MenuEntry> OpeningBlock(ICommandRegistry registry, ICommandContext context)
    {
        var added = new List<MenuEntry>();
        foreach (var id in Opening)
        {
            if (registry.Find(id) is { } command && command.IsApplicable(context))
                added.Add(new MenuCommand(command, IsPrimary: id == "dc.open"));
        }

        if (added.Count > 0)
            added.Add(new MenuSeparator());
        return added;
    }

    /// <summary>Grouped as the commands declare, separated, each group in title order.</summary>
    private static List<MenuEntry> Grouped(IEnumerable<CommandDefinition> commands)
    {
        var entries = new List<MenuEntry>();
        var groups = commands
            .GroupBy(c => c.Group!)
            .OrderBy(g => GroupOrder(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            if (entries.Count > 0)
                entries.Add(new MenuSeparator());

            entries.AddRange(group
                .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .Select(c => new MenuCommand(c)));
        }

        return entries;
    }

    /// <summary>File actions first, navigation last; tools sit in between where they were put.</summary>
    private static int GroupOrder(string group) => group switch
    {
        "File" => 0,
        "Edit" => 1,
        "Clipboard" => 2,
        "Navigate" => 90,
        _ => 50,
    };
}
